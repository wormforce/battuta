using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Battuta.Engine.Input;

namespace Battuta.Linux.Platform.ForegroundApps;

/// <summary>
/// Sway and i3 adapter (plan §8.3 priority 3): queries the focused node of
/// the layout tree over the compositor IPC socket and publishes
/// <c>app_id</c>/<c>window_properties.class</c> identity. Window names are
/// never read.
/// </summary>
public sealed class SwayForegroundAppSource : PollingForegroundAppSource
{
    private readonly Func<string?> socketPathResolver;

    public SwayForegroundAppSource(
        ForegroundApplicationCache cache,
        Func<string?>? socketPathResolver = null,
        TimeProvider? timeProvider = null,
        TimeSpan? interval = null)
        : base(cache, timeProvider, interval)
    {
        this.socketPathResolver = socketPathResolver ?? ResolveSocketPath;
    }

    /// <summary>$SWAYSOCK, then $I3SOCK, then null.</summary>
    public static string? ResolveSocketPath() =>
        Environment.GetEnvironmentVariable("SWAYSOCK")
        is { Length: > 0 } swaySocket ? swaySocket
        : Environment.GetEnvironmentVariable("I3SOCK") is { Length: > 0 } i3Socket
            ? i3Socket
            : null;

    protected override async ValueTask<ForegroundApplicationSnapshot?> PollAsync(
        CancellationToken cancellationToken)
    {
        var socketPath = socketPathResolver();
        if (string.IsNullOrEmpty(socketPath))
        {
            SetStatus(new ForegroundAppSourceStatus(
                ForegroundAppSourceState.Unavailable,
                "SWAYSOCK/I3SOCK 未设置"));
            return null;
        }

        var tree = await QueryTreeAsync(socketPath, cancellationToken).ConfigureAwait(false);
        var focused = SwayIpc.TryFindFocusedApplication(tree);
        if (focused is not (var applicationClass, var processId))
        {
            SetStatus(new ForegroundAppSourceStatus(
                ForegroundAppSourceState.Running,
                "当前焦点没有应用身份"));
            return null;
        }

        SetStatus(new ForegroundAppSourceStatus(
            ForegroundAppSourceState.Running,
            $"sway/i3 IPC({applicationClass})"));
        return ForegroundAppSnapshotFactory.FromDesktopApplication(applicationClass, processId);
    }

    private static async Task<JsonElement> QueryTreeAsync(
        string socketPath,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(
            AddressFamily.Unix,
            SocketType.Stream,
            ProtocolType.Unspecified);
        await socket
            .ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken)
            .ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var request = SwayIpc.FormatMessage(SwayIpc.MessageType.GetTree);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);

        var response = new MemoryStream();
        var buffer = new byte[8192];
        string payload;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The compositor closed the IPC connection mid-reply.");
            }

            response.Write(buffer, 0, read);
            if (SwayIpc.TryParseMessage(
                    response.ToArray(),
                    out _,
                    out payload,
                    out _))
            {
                break;
            }
        }

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }
}
