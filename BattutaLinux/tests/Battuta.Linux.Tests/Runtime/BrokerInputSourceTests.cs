using Xunit;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Battuta.Engine.Input;
using Battuta.Engine.Input.Broker;
using Battuta.Linux.Platform.Net;
using Battuta.Linux.Runtime;

namespace Battuta.Linux.Tests.Runtime;

/// <summary>
/// A real broker over an abstract-free unix SEQPACKET socket: accepts
/// connections in a loop, validates the hello, sends the ack and a scripted
/// event stream — the same wire behavior as battuta-inputd (minus logind).
/// </summary>
public sealed class FakeBrokerServer : IAsyncDisposable
{
    private readonly Socket listener;
    private readonly Task acceptLoop;
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;

    public FakeBrokerServer(string socketPath, params BrokerMessage[] scriptedMessages)
    {
        ScriptedMessages = scriptedMessages;
        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }

        listener = new Socket(AddressFamily.Unix, SocketType.Seqpacket, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(4);
        acceptLoop = AcceptLoopAsync(lifetime.Token);
        SocketPath = socketPath;
    }

    public string SocketPath { get; }

    public BrokerMessage[] ScriptedMessages { get; }

    public List<BrokerHello> ReceivedHellos { get; } = [];

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            using (connection)
            {
                await HandleAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask HandleAsync(Socket connection, CancellationToken cancellationToken)
    {
        await using var stream = new SeqPacketSocketStream(connection, ownsSocket: false);
        var decoder = new BrokerFrameDecoder();
        var buffer = new byte[4096];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            decoder.Append(buffer.AsSpan(0, read));
            while (decoder.TryRead(out var message, out _, out _))
            {
                if (message is not BrokerHello hello)
                {
                    continue;
                }

                ReceivedHellos.Add(hello);
                var rejections = ScriptedMessages.OfType<BrokerError>().ToArray();
                if (rejections.Length > 0)
                {
                    // Contract §8: the real broker rejects the handshake by
                    // replying with an error frame INSTEAD of hello_ack.
                    foreach (var rejection in rejections)
                    {
                        await stream.WriteAsync(
                            BrokerMessageCodec.Encode(rejection),
                            cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                await stream.WriteAsync(
                    BrokerMessageCodec.Encode(new BrokerHelloAck(
                        BrokerProtocol.ProtocolMajor,
                        BrokerProtocol.ProtocolMinor,
                        Caps: 0,
                        HeartbeatIntervalMilliseconds: 0,
                        Seat: "seat0")),
                    cancellationToken).ConfigureAwait(false);
                foreach (var scripted in ScriptedMessages)
                {
                    await stream.WriteAsync(
                        BrokerMessageCodec.Encode(scripted),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetime.Cancel();
        lifetime.Dispose();
        try
        {
            await acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        listener.Dispose();
        try
        {
            File.Delete(SocketPath);
        }
        catch (FileNotFoundException)
        {
        }
    }
}

public sealed class BrokerInputSourceTests : IDisposable
{
    /// <summary>Creates a hard link (the BCL has no managed hard-link API;
    /// the ECONNREFUSED scenario needs a second name for a socket inode).</summary>
    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldPath, string newPath);

    private readonly string runtimeDir =
        Path.Combine(Path.GetTempPath(), $"battuta-broker-{Guid.NewGuid():N}");

    public BrokerInputSourceTests() => Directory.CreateDirectory(runtimeDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(runtimeDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string SocketPath => Path.Combine(runtimeDir, "input.sock");

    private static async Task WaitForAsync(Func<bool> condition, Func<string>? describe = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition not met within timeout: " + (describe?.Invoke() ?? string.Empty));
    }

    [Fact]
    public async Task ConnectsPumpsEventsIntoSink()
    {
        var keyboard = new BrokerKeyboardEvent(
            Sequence: 1,
            MonotonicTimestampNanoseconds: 42,
            Code: 30,
            Value: BrokerProtocol.EvdevValuePress,
            Origin: BrokerEventOrigin.Hardware);
        await using var server = new FakeBrokerServer(SocketPath, keyboard);
        await using var source = new BrokerInputSource(
            socketPath: SocketPath,
            options: new BrokerClientOptions { AutoReconnect = false });

        var events = new List<InputEvent>();
        var runTask = source.RunAsync(new RecordingSink(events), CancellationToken.None);

        await WaitForAsync(() => source.Status.Phase == BrokerConnectionPhase.Connected);
        await WaitForAsync(() => events.Count == 1);

        Assert.Single(server.ReceivedHellos);
        Assert.Equal(InputEventKind.Keyboard, events[0].Kind);
        Assert.Equal(BrokerConnectionPhase.Connected, source.Status.Phase);

        // Ending the server (clean EOF with AutoReconnect=false) ends the run.
        await server.DisposeAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UnauthorizedHandshakeParksUntilRetrySucceeds()
    {
        var rejection = new BrokerError(
            BrokerErrorCode.Unauthorized,
            "polkit requires interactive authorization; authorize from the Battuta app");
        await using var rejecting = new FakeBrokerServer(SocketPath, rejection);
        await using var source = new BrokerInputSource(
            socketPath: SocketPath,
            options: new BrokerClientOptions { AutoReconnect = false });

        var events = new List<InputEvent>();
        var runTask = source.RunAsync(new RecordingSink(events), CancellationToken.None);

        await WaitForAsync(
            () => source.Status.Phase == BrokerConnectionPhase.Rejected,
            () => $"phase={source.Status.Phase} detail={source.Status.Detail}");
        Assert.Contains("polkit", source.Status.Detail, StringComparison.OrdinalIgnoreCase);
        await rejecting.DisposeAsync();

        // The GUI authorization flow resolves; retry must find a fresh broker.
        await using var accepting = new FakeBrokerServer(SocketPath);
        source.RequestRetry();

        await WaitForAsync(
            () => source.Status.Phase == BrokerConnectionPhase.Connected,
            () => $"phase={source.Status.Phase} hellos2={accepting.ReceivedHellos.Count}");
        await accepting.DisposeAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task VersionMismatchSurfacesFailedStatus()
    {
        var mismatch = new BrokerError(BrokerErrorCode.VersionMismatch, "protocol 2 is unsupported");
        await using var server = new FakeBrokerServer(SocketPath, mismatch);
        await using var source = new BrokerInputSource(
            socketPath: SocketPath,
            options: new BrokerClientOptions { AutoReconnect = false });

        var events = new List<InputEvent>();
        await source.RunAsync(new RecordingSink(events), CancellationToken.None);

        Assert.Equal(BrokerConnectionPhase.Failed, source.Status.Phase);
        Assert.Contains("protocol", source.Status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnreachableSocketParksInUnavailableAndRecovers()
    {
        // No broker at the path yet: connect() fails with ENOENT — exactly
        // what the packaged app sees before the systemd socket unit is up.
        await using var source = new BrokerInputSource(
            socketPath: SocketPath,
            options: new BrokerClientOptions { AutoReconnect = false })
        {
            UnreachableRetryDelay = TimeSpan.FromMilliseconds(1),
        };

        var unavailable = new TaskCompletionSource<BrokerConnectionStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.StatusChanged += (_, _) =>
        {
            var status = source.Status;
            if (status.Phase == BrokerConnectionPhase.Unavailable)
            {
                unavailable.TrySetResult(status);
            }
        };

        var events = new List<InputEvent>();
        var runTask = source.RunAsync(new RecordingSink(events), CancellationToken.None);

        var inaccessible = await unavailable.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("not found", inaccessible.Detail, StringComparison.OrdinalIgnoreCase);

        // The service appearing later (socket activation) is picked up by
        // the bounded retry without user action.
        await using var server = new FakeBrokerServer(SocketPath);
        await WaitForAsync(
            () => source.Status.Phase == BrokerConnectionPhase.Connected,
            () => $"phase={source.Status.Phase} hellos={server.ReceivedHellos.Count}");

        // Ending the broker ends the pump (clean EOF), as in packaged shutdown.
        await server.DisposeAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        await source.DisposeAsync();
    }

    [Fact]
    public void DescribeUnreachableTreatsGenericSocketErrorAsMissingNode()
    {
        // connect(2) to an absent AF_UNIX node raises ENOENT, which .NET
        // reports as a generic socket error; the classifier then keys off
        // the filesystem. Synthetic SocketException(2) reproduces that path.
        var message = BrokerInputSource.DescribeUnreachable(
            new SocketException(2),
            Path.Combine(Path.GetTempPath(), $"battuta-absent-{Guid.NewGuid():N}.sock"));
        Assert.Contains("not found", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeUnreachableClassifiesRealConnectFailures()
    {
        var runtimePath = Path.Combine(
            Path.GetTempPath(), $"battuta-classify-{Guid.NewGuid():N}.sock");

        // EACCES: a listener with mode 0000 rejects even its own owner, so
        // connect() fails with the errno production sees on permission bugs.
        using (var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            listener.Bind(new UnixDomainSocketEndPoint(runtimePath));
            listener.Listen(1);
            File.SetUnixFileMode(runtimePath, UnixFileMode.None);
            try
            {
                using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                probe.Connect(new UnixDomainSocketEndPoint(runtimePath));
                Assert.Fail("connect unexpectedly succeeded");
            }
            catch (SocketException error)
            {
                Assert.Equal(SocketError.AccessDenied, error.SocketErrorCode);
                Assert.Contains(
                    "no permission",
                    BrokerInputSource.DescribeUnreachable(error, runtimePath),
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                File.SetUnixFileMode(
                    runtimePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        // ECONNREFUSED: the node survives its listener's death, so connect()
        // hits a socket file nobody accepts on — the packaged symptom of a
        // stopped-but-enabled service. The managed socket unlinks its own
        // bind path on dispose, so a hard link keeps the dead node alive.
        var refusedPath = Path.Combine(
            Path.GetTempPath(), $"battuta-refused-{Guid.NewGuid():N}.sock");
        using (var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            listener.Bind(new UnixDomainSocketEndPoint(runtimePath));
            listener.Listen(1);
            Assert.Equal(0, link(runtimePath, refusedPath));
        }

        Assert.False(File.Exists(runtimePath));
        Assert.True(File.Exists(refusedPath));
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(refusedPath));
            Assert.Fail("connect unexpectedly succeeded");
        }
        catch (SocketException error)
        {
            Assert.Equal(SocketError.ConnectionRefused, error.SocketErrorCode);
            Assert.Contains(
                "nothing is listening",
                BrokerInputSource.DescribeUnreachable(error, refusedPath),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(refusedPath);
        }
    }

    [Fact]
    public void DescribeUnreachableClassifiesNonSocketFailures()
    {
        var notFound = BrokerInputSource.DescribeUnreachable(
            new DirectoryNotFoundException("runtime dir vanished"),
            "/run/battuta-inputd/input.sock");
        Assert.Contains("missing", notFound, StringComparison.OrdinalIgnoreCase);

        var denied = BrokerInputSource.DescribeUnreachable(
            new UnauthorizedAccessException(),
            "/run/battuta-inputd/input.sock");
        Assert.Contains("no permission", denied, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Simple sink recording every delivered event.</summary>
    private sealed class RecordingSink(List<InputEvent> events) : IInputEventSink
    {
        public ValueTask OnInputAsync(
            InputEvent inputEvent,
            CancellationToken cancellationToken)
        {
            events.Add(inputEvent);
            return ValueTask.CompletedTask;
        }
    }
}
