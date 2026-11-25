using System.IO;
using System.Net.Sockets;
using Battuta.Engine.Input;
using Battuta.Engine.Input.Broker;
using Battuta.Engine.Input.Linux;
using Battuta.Linux.Platform.Net;

namespace Battuta.Linux.Runtime;

public enum BrokerConnectionPhase
{
    Disabled,
    Connecting,
    Connected,
    /// <summary>The broker refused the handshake (logind/polkit). The UI
    /// shows the reason and offers the authorization flow.</summary>
    Rejected,
    /// <summary>The broker socket is unreachable: not installed, service
    /// not enabled, or permissions. The packaged deployment runs
    /// battuta-inputd via systemd socket activation, so the source retries
    /// with a bounded delay and recovers on its own once the service
    /// appears; the UI shows deployment guidance in the meantime.</summary>
    Unavailable,
    /// <summary>A protocol violation ended the session; no automatic retry.</summary>
    Failed,
}

public sealed record BrokerConnectionStatus(
    BrokerConnectionPhase Phase,
    string? Detail = null)
{
    public static readonly BrokerConnectionStatus Disabled = new(BrokerConnectionPhase.Disabled);
    public static readonly BrokerConnectionStatus Connecting = new(BrokerConnectionPhase.Connecting);
}

/// <summary>
/// Owns the broker session (plan §5.3): connects to battuta-inputd's unix
/// socket, pumps decoded broker messages through the neutral event adapter
/// into a sink, and drives the UI-visible connection state. Transport loss
/// reconnects with the client's bounded delay; authorization rejections park
/// until <see cref="RequestRetry"/> (the GUI polkit flow) or cancellation.
/// </summary>
public sealed class BrokerInputSource : IAsyncDisposable
{
    /// <summary>Packaged broker socket (systemd unit default).</summary>
    public const string DefaultSocketPath = "/run/battuta-inputd/input.sock";

    /// <summary>Delay before retrying after the socket was unreachable.
    /// Kept modest: an ENOENT probe is a cheap syscall, and the packaged
    /// app must pick up `systemctl start battuta-inputd` without user
    /// action. Tests shrink this.</summary>
    public TimeSpan UnreachableRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    private readonly BrokerClient client;
    private readonly string socketPath;
    private readonly Func<CancellationToken, ValueTask<Stream>>? connectOverride;
    private readonly Lock gate = new();
    private TaskCompletionSource retryRequested =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private BrokerConnectionStatus status = BrokerConnectionStatus.Disabled;
    private BrokerSession? session;

    public BrokerInputSource(
        string? socketPath = null,
        BrokerClientOptions? options = null,
        Func<CancellationToken, ValueTask<Stream>>? connect = null)
    {
        this.socketPath = socketPath
            ?? Environment.GetEnvironmentVariable("BATTUTA_INPUTD_SOCKET")
            ?? DefaultSocketPath;
        connectOverride = connect;
        client = new BrokerClient(ConnectAsync, options);
    }

    /// <summary>Raises on the pump task whenever the status changes.</summary>
    public event EventHandler? StatusChanged;

    public BrokerConnectionStatus Status
    {
        get
        {
            lock (gate)
            {
                return status;
            }
        }
    }

    public string SocketPath => socketPath;

    private ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        if (connectOverride is not null)
        {
            return connectOverride(cancellationToken);
        }

        // battuta-inputd listens on AF_UNIX SOCK_SEQPACKET; a plain
        // NetworkStream refuses that socket type, so the transport goes
        // through the packet-aware adapter, which also reports when the
        // broker ack arrives (handshake complete → Connected).
        var socket = new Socket(AddressFamily.Unix, SocketType.Seqpacket, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            session = new BrokerSession(this);
            SetStatus(BrokerConnectionStatus.Connecting);
            return ValueTask.FromResult<Stream>(
                new SeqPacketSocketStream(socket, ownsSocket: true, session.MarkConnected));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs until cancelled or until a fatal protocol failure. Broker
    /// authorization rejections park in <see cref="BrokerConnectionPhase.Rejected"/>
    /// until <see cref="RequestRetry"/> (authorization flow) arrives.
    /// </summary>
    public async Task RunAsync(IInputEventSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var adapter = new LinuxBrokerEventAdapter();
        while (true)
        {
            SetStatus(BrokerConnectionStatus.Connecting);
            try
            {
                await client.RunAsync(
                    (message, token) => DeliverAsync(message, adapter, sink, token),
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (BrokerHandshakeException error)
                when (error.Code == BrokerErrorCode.Unauthorized)
            {
                SetStatus(new BrokerConnectionStatus(BrokerConnectionPhase.Rejected, error.Message));
                await retryRequested.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                retryRequested = CreateRetrySource();
            }
            catch (BrokerHandshakeException error)
            {
                // Version mismatch or busy: retrying without a change would
                // only repeat, but keep the reason visible.
                SetStatus(new BrokerConnectionStatus(BrokerConnectionPhase.Failed, error.Message));
                return;
            }
            catch (Exception error)
                when (error is not BrokerHandshakeException
                    and not BrokerProtocolException
                    and not OperationCanceledException)
            {
                // Socket missing (service not installed/enabled), permission
                // denied, refused, or another transport-level failure. This
                // is deployment state, not a protocol violation: park in
                // Unavailable with guidance and retry on a bounded delay so
                // a later `systemctl start battuta-inputd` is picked up.
                SetStatus(
                    new BrokerConnectionStatus(
                        BrokerConnectionPhase.Unavailable,
                        DescribeUnreachable(error, socketPath)));
                if (UnreachableRetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(UnreachableRetryDelay, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (BrokerProtocolException error)
            {
                SetStatus(new BrokerConnectionStatus(BrokerConnectionPhase.Failed, error.Message));
                return;
            }
        }
    }

    /// <summary>Resumes the connect loop after an authorization flow.</summary>
    public void RequestRetry()
    {
        var source = retryRequested;
        if (!source.Task.IsCompleted)
        {
            source.TrySetResult();
        }
    }

    /// <summary>Turns a connect failure into the deployment diagnostic the
    /// UI shows while the source keeps retrying. Technical detail stays
    /// here; the ViewModel wraps it with user-facing guidance.</summary>
    internal static string DescribeUnreachable(Exception error, string socketPath)
    {
        if (error is SocketException socketError)
        {
            // .NET collapses most AF_UNIX errnos into generic socket errors,
            // so "missing node" is decided from the filesystem while the
            // distinctly mappable errnos (EACCES, ECONNREFUSED) take their
            // own branches first.
            return socketError.SocketErrorCode switch
            {
                SocketError.AccessDenied =>
                    $"no permission to connect to {socketPath}",
                SocketError.ConnectionRefused =>
                    $"nothing is listening on {socketPath}",
                _ when !File.Exists(socketPath) =>
                    $"broker socket not found at {socketPath}; " +
                    "install the Battuta package and enable battuta-inputd.socket",
                _ => $"connect to {socketPath} failed: {socketError.Message}",
            };
        }

        return error switch
        {
            FileNotFoundException =>
                $"broker socket not found at {socketPath}; " +
                "install the Battuta package and enable battuta-inputd.socket",
            DirectoryNotFoundException =>
                $"broker socket directory is missing at {socketPath}; " +
                "install the Battuta package and enable battuta-inputd.socket",
            UnauthorizedAccessException =>
                $"no permission to connect to {socketPath}",
            _ => $"connect to {socketPath} failed: {error.Message}",
        };
    }

    private static ValueTask DeliverAsync(
        BrokerMessage message,
        LinuxBrokerEventAdapter adapter,
        IInputEventSink sink,
        CancellationToken cancellationToken)
    {
        var inputEvent = adapter.Translate(message);
        if (inputEvent is null)
        {
            return ValueTask.CompletedTask;
        }

        return sink.OnInputAsync(inputEvent.Value, cancellationToken);
    }

    /// <summary>One transport session: the first received packet on the
    /// stream means the broker acknowledged the hello.</summary>
    private sealed class BrokerSession(BrokerInputSource owner)
    {
        private int connectedSignalled;

        public void MarkConnected()
        {
            if (Interlocked.Exchange(ref connectedSignalled, 1) == 0)
            {
                owner.SetStatus(new BrokerConnectionStatus(BrokerConnectionPhase.Connected));
            }
        }
    }

    private void SetStatus(BrokerConnectionStatus value)
    {
        bool changed;
        lock (gate)
        {
            changed = status != value;
            status = value;
        }

        if (changed)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private TaskCompletionSource CreateRetrySource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        retryRequested.TrySetResult();
        await client.DisposeAsync().ConfigureAwait(false);
    }
}
