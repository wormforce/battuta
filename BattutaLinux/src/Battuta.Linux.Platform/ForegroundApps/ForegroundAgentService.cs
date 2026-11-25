using Battuta.Engine.Input;
using Tmds.DBus.Protocol;

namespace Battuta.Linux.Platform.ForegroundApps;

/// <summary>
/// Hosts the session-bus service that compositor-side helpers push active
/// window identity to: the KWin scripting agent (PR 8) and, later, the GNOME
/// Shell extension. The wire contract is intentionally tiny —
/// <c>ActiveWindow(s resourceClass, u pid)</c> plus a <c>Ping()</c> probe —
/// because it is invoked from foreign scripting runtimes. Window titles are
/// never part of the contract (plan §8.3).
/// </summary>
public sealed class ForegroundAgentService : IAsyncDisposable
{
    public const string BusName = "io.github.wormforce.Battuta.ForegroundAgent";
    public const string ObjectPath = "/io/github/wormforce/Battuta/ForegroundAgent";
    public const string InterfaceName = BusName;

    private readonly ForegroundApplicationCache cache;
    private readonly Func<string, uint, ForegroundApplicationSnapshot>? snapshotFactory;
    private readonly string? busAddressOverride;
    private DBusConnection? connection;
    private long generation;
    private int disposed;

    public ForegroundAgentService(
        ForegroundApplicationCache cache,
        Func<string, uint, ForegroundApplicationSnapshot>? snapshotFactory = null,
        string? busAddress = null)
    {
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.snapshotFactory = snapshotFactory ?? ForegroundAppSnapshotFactory.FromDesktopApplication;
        busAddressOverride = busAddress;
    }

    public ForegroundAppSourceStatus Status { get; private set; } =
        new(ForegroundAppSourceState.Waiting, "正在连接会话总线…");

    /// <summary>The live session-bus connection once started; sources that
    /// drive compositor-side helpers call out through the same connection.</summary>
    public DBusConnection? Connection => connection;

    public event EventHandler? StatusChanged;

    /// <summary>Connects to the session bus, claims the agent bus name, and
    /// starts serving the push methods. Honors <c>DBUS_SESSION_BUS_ADDRESS</c>
    /// for private-bus testing.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (connection is not null)
        {
            return;
        }

        var address = busAddressOverride;
        if (string.IsNullOrWhiteSpace(address))
        {
            address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
            if (string.IsNullOrWhiteSpace(address))
            {
                address = DBusAddress.Session
                    ?? throw new InvalidOperationException("No session D-Bus is available.");
            }
        }

        var newConnection = new DBusConnection(address);
        try
        {
            await newConnection
                .ConnectAsync().AsTask()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            newConnection.AddMethodHandler(new AgentMethodHandler(this));
            var becameOwner = await newConnection
                .TryRequestNameAsync(BusName, RequestNameOptions.None)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!becameOwner)
            {
                newConnection.Dispose();
                throw new InvalidOperationException(
                    $"The agent bus name '{BusName}' is already owned. " +
                    "Another Battuta instance is likely running.");
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            newConnection.Dispose();
            SetStatus(ForegroundAppSourceState.Failed, $"D-Bus agent 启动失败:{error.Message}");
            throw;
        }

        connection = newConnection;
        SetStatus(new ForegroundAppSourceStatus(
            ForegroundAppSourceState.Waiting,
            "agent 已就绪,等待合成器推送"));
    }

    /// <summary>Applies one pushed identity; shared by every compositor helper.</summary>
    public void Publish(string applicationClass, uint processId)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        var snapshot = snapshotFactory(applicationClass, processId);
        cache.TryPublish(Interlocked.Increment(ref generation), snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        connection?.Dispose();
        connection = null;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void SetStatus(ForegroundAppSourceStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetStatus(ForegroundAppSourceState state, string detail) =>
        SetStatus(new ForegroundAppSourceStatus(state, detail));

    private sealed class AgentMethodHandler(ForegroundAgentService owner) : IPathMethodHandler
    {
        public string Path => ObjectPath;

        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            if (request.InterfaceIsSet &&
                !string.Equals(request.InterfaceAsString, InterfaceName, StringComparison.Ordinal))
            {
                context.ReplyUnknownMethodError();
                return ValueTask.CompletedTask;
            }

            switch (request.MemberAsString)
            {
                case "ActiveWindow":
                    HandleActiveWindow(context);
                    return ValueTask.CompletedTask;
                case "Ping":
                    ReplyEmpty(context);
                    return ValueTask.CompletedTask;
                default:
                    context.ReplyUnknownMethodError();
                    return ValueTask.CompletedTask;
            }
        }

        private void HandleActiveWindow(MethodContext context)
        {
            var request = context.Request;
            // Our KWin script sends (ss); the contract accepts a numeric PID
            // encoding as well so a GNOME Shell extension can use a typed u.
            var signature = request.SignatureAsString ?? string.Empty;
            if (signature.Length < 2 || signature[0] != 's')
            {
                context.ReplyError(
                    ErrorName.InvalidArgs,
                    "ActiveWindow expects (s + uint32/int32/double/string pid).");
                return;
            }

            string applicationClass;
            uint processId = 0;
            var reader = request.GetBodyReader();
            applicationClass = reader.ReadString() ?? string.Empty;
            switch (signature[1])
            {
                case 'u':
                    processId = reader.ReadUInt32();
                    break;
                case 'i':
                    processId = (uint)Math.Max(0, reader.ReadInt32());
                    break;
                case 'd':
                    var asDouble = reader.ReadDouble();
                    processId = double.IsFinite(asDouble) && asDouble > 0
                        ? (uint)Math.Min(uint.MaxValue, asDouble)
                        : 0u;
                    break;
                case 's':
                    _ = uint.TryParse(reader.ReadString(), out processId);
                    break;
                default:
                    break;
            }

            owner.Publish(applicationClass, processId);
            ReplyEmpty(context);
        }

        private static void ReplyEmpty(MethodContext context)
        {
            if (context.NoReplyExpected)
            {
                return;
            }

            using var writer = context.CreateReplyWriter(string.Empty);
            context.Reply(writer.CreateMessage());
        }
    }

    private static class ErrorName
    {
        public const string InvalidArgs = "org.freedesktop.DBus.Error.InvalidArgs";
    }
}
