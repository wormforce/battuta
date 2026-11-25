using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Battuta.Linux.Platform.SingleInstance;

/// <summary>Delivered when a second launch asks the primary to surface its
/// window.</summary>
public sealed class ActivationEventArgs : EventArgs
{
    public static readonly ActivationEventArgs Show = new("show");

    public ActivationEventArgs(string command) => Command = command;

    /// <summary>Raw command line from the second instance ("show" in v1).</summary>
    public string Command { get; }
}

/// <summary>
/// Single-instance activation over a unix socket in <c>XDG_RUNTIME_DIR</c>
/// (plan §7.2: a second launch activates the existing instance instead of
/// starting a second copy). The primary binds and listens; a second process
/// delivers its command and exits. D-Bus is not required.
/// </summary>
public sealed class SingleInstanceService : IAsyncDisposable
{
    /// <summary>Protocol version line sent by both sides.</summary>
    public const string ProtocolLine = "battuta-activate/1";

    private readonly string socketPath;
    private Socket? listenerSocket;
    private CancellationTokenSource? listenerCancellation;
    private Task? listenerTask;

    public SingleInstanceService(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        this.socketPath = socketPath;
    }

    /// <summary>Raised on the primary when a second instance delivers a
    /// command. Callbacks run on the listener loop thread.</summary>
    public event EventHandler<ActivationEventArgs>? ActivationReceived;

    public string SocketPath => socketPath;

    /// <summary>
    /// Tries to become the primary instance. Returns false (after delivering
    /// <paramref name="activationCommand"/> to the running primary) when
    /// another instance already owns the socket.
    /// </summary>
    public async Task<bool> TryAcquireAsync(
        string activationCommand = "show",
        CancellationToken cancellationToken = default)
    {
        var socketDirectory = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrEmpty(socketDirectory))
        {
            Directory.CreateDirectory(socketDirectory);
        }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        }
        catch (SocketException)
        {
            listener.Dispose();
            // The socket node exists: either a live primary (deliver and
            // exit) or a stale node from a crash (best effort: remove and
            // retry once).
            if (TryDeliverToPrimary(activationCommand))
            {
                return false;
            }

            try
            {
                File.Delete(socketPath);
            }
            catch (IOException)
            {
                throw new IOException(
                    $"Another Battuta instance appears to be running ({socketPath}).");
            }

            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        }

        listener.Listen(1);
        listenerSocket = listener;
        listenerCancellation = new CancellationTokenSource();
        listenerTask = ListenLoopAsync(listener, listenerCancellation.Token);
        return true;
    }

    /// <summary>Delivers a command to the running primary. Returns false when
    /// no primary answers (stale socket node).</summary>
    private bool TryDeliverToPrimary(string command)
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(socketPath));
            using var stream = new NetworkStream(client, ownsSocket: false);
            var payload = Encoding.UTF8.GetBytes($"{ProtocolLine}\n{command}\n");
            stream.Write(payload);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task ListenLoopAsync(Socket listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = AcceptClientAsync(client, cancellationToken);
        }
    }

    private async Task AcceptClientAsync(Socket client, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new NetworkStream(client, ownsSocket: true);
            var buffer = new byte[512];
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            var payload = Encoding.UTF8.GetString(buffer, 0, read);
            var lines = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0 || lines[0].Trim() != ProtocolLine)
            {
                return; // Unknown peer; ignore.
            }

            var command = lines.Length > 1 ? lines[1].Trim() : "show";
            ActivationReceived?.Invoke(this, new ActivationEventArgs(command));
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (listenerSocket is not { } ownedListener)
        {
            return;
        }

        listenerSocket = null;
        listenerCancellation?.Cancel();
        ownedListener.Dispose();
        if (listenerTask is not null)
        {
            try
            {
                await listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        listenerCancellation?.Dispose();
        try
        {
            File.Delete(socketPath);
        }
        catch (IOException)
        {
        }
    }
}
