using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Battuta.Engine.Input;

namespace Battuta.Linux.Platform.ForegroundApps;

/// <summary>
/// Hyprland adapter (plan §8.3 priority 4): polls <c>activewindow -j</c> over
/// the instance IPC socket and publishes the window class identity. The IPC
/// reply also carries the window title; it is parsed away and never stored.
/// </summary>
public sealed class HyprlandForegroundAppSource : PollingForegroundAppSource
{
    private readonly Func<string?> socketPathResolver;

    public HyprlandForegroundAppSource(
        ForegroundApplicationCache cache,
        Func<string?>? socketPathResolver = null,
        TimeProvider? timeProvider = null,
        TimeSpan? interval = null)
        : base(cache, timeProvider, interval)
    {
        this.socketPathResolver = socketPathResolver ?? ResolveSocketPath;
    }

    /// <summary>$XDG_RUNTIME_DIR/hypr/$HYPRLAND_INSTANCE_SIGNATURE/.socket.sock.</summary>
    public static string? ResolveSocketPath()
    {
        if (Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE")
            is not { Length: > 0 } instance)
        {
            return null;
        }

        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtimeDir))
        {
            return null;
        }

        return Path.Combine(runtimeDir, "hypr", instance, ".socket.sock");
    }

    protected override async ValueTask<ForegroundApplicationSnapshot?> PollAsync(
        CancellationToken cancellationToken)
    {
        var socketPath = socketPathResolver();
        if (string.IsNullOrEmpty(socketPath))
        {
            SetStatus(new ForegroundAppSourceStatus(
                ForegroundAppSourceState.Unavailable,
                "HYPRLAND_INSTANCE_SIGNATURE 未设置"));
            return null;
        }

        var reply = await RequestAsync(socketPath, "activewindow -j", cancellationToken)
            .ConfigureAwait(false);
        var identity = TryParseActivewindow(reply);
        if (identity is not (var applicationClass, var processId))
        {
            // Hyprland answers "Invalid" or an empty payload without focus,
            // or the focused window carries no class.
            SetStatus(new ForegroundAppSourceStatus(
                ForegroundAppSourceState.Running,
                "当前没有可识别的活动窗口"));
            return null;
        }

        SetStatus(new ForegroundAppSourceStatus(
            ForegroundAppSourceState.Running,
            $"Hyprland IPC({applicationClass})"));
        return ForegroundAppSnapshotFactory.FromDesktopApplication(applicationClass, processId);
    }

    /// <summary>
    /// Parses the <c>activewindow -j</c> reply. The reply also carries the
    /// window title; it is parsed only to be discarded — nothing but the
    /// class and PID ever leaves this method.
    /// </summary>
    public static (string ApplicationClass, uint ProcessId)? TryParseActivewindow(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply) || !reply.TrimStart().StartsWith('{'))
        {
            return null;
        }

        using var document = JsonDocument.Parse(reply);
        var root = document.RootElement;
        var applicationClass =
            GetString(root, "class") is { Length: > 0 } windowClass ? windowClass
            : GetString(root, "initialClass");
        if (string.IsNullOrWhiteSpace(applicationClass))
        {
            return null;
        }

        var processId = root.TryGetProperty("pid", out var pid) &&
                        pid.ValueKind == JsonValueKind.Number &&
                        pid.TryGetUInt32(out var parsedPid)
            ? parsedPid
            : 0u;
        return (applicationClass, processId);
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<string> RequestAsync(
        string socketPath,
        string request,
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
        var payload = Encoding.UTF8.GetBytes(request + "\n");
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);

        var response = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            response.Write(buffer, 0, read);
            if (response.Length > 1024 * 1024)
            {
                throw new IOException("Hyprland IPC reply exceeded 1 MiB.");
            }
        }

        return Encoding.UTF8.GetString(response.ToArray());
    }
}
