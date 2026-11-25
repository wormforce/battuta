using System.Diagnostics;
using System.Net.Sockets;
using Battuta.Engine.Input;
using Battuta.Linux.Platform.ForegroundApps;
using Xunit;

namespace Battuta.Linux.Tests.Platform;

public sealed class ForegroundAppMonitorTests
{
    // Detection must not depend on the host machine, so every test starts
    // from a minimal environment.
    private static readonly IReadOnlyDictionary<string, string?> Clean =
        new Dictionary<string, string?>();

    private static IReadOnlyDictionary<string, string?> Env(
        params (string Key, string? Value)[] entries) =>
        entries.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);

    [Fact]
    public void DetectPrefersCompositorEnvironmentMarkers()
    {
        Assert.Equal(
            ForegroundAppSourceKind.Hyprland,
            ForegroundAppMonitor.Detect(Env(("HYPRLAND_INSTANCE_SIGNATURE", "sig_1"))));
        Assert.Equal(
            ForegroundAppSourceKind.Sway,
            ForegroundAppMonitor.Detect(Env(("SWAYSOCK", "/run/sway-ipc.sock"))));
        Assert.Equal(
            ForegroundAppSourceKind.I3,
            ForegroundAppMonitor.Detect(Env(("I3SOCK", "/run/i3-ipc.sock"))));
    }

    [Fact]
    public void DetectFallsBackToDesktopIdentity()
    {
        Assert.Equal(
            ForegroundAppSourceKind.KWin,
            ForegroundAppMonitor.Detect(Env(("XDG_CURRENT_DESKTOP", "KDE"))));
        Assert.Equal(
            ForegroundAppSourceKind.KWin,
            ForegroundAppMonitor.Detect(Clean, hasKWinBusService: true));
        Assert.Equal(
            ForegroundAppSourceKind.Gnome,
            ForegroundAppMonitor.Detect(Env(("XDG_CURRENT_DESKTOP", "ubuntu:GNOME"))));
        Assert.Equal(
            ForegroundAppSourceKind.Gnome,
            ForegroundAppMonitor.Detect(Clean, hasGnomeShellBusService: true));
    }

    [Fact]
    public void DetectExplicitUnknownOnWaylandWithoutAdapter()
    {
        Assert.Equal(
            ForegroundAppSourceKind.None,
            ForegroundAppMonitor.Detect(Env(("WAYLAND_DISPLAY", "wayland-0"))));
    }

    [Fact]
    public void DetectUsesX11OnlyWithoutWayland()
    {
        Assert.Equal(
            ForegroundAppSourceKind.X11,
            ForegroundAppMonitor.Detect(Env(("DISPLAY", ":0"))));
        // Desktop identity wins over the bare X11 fallback even when a
        // Wayland display exists (a KDE Wayland session also sets DISPLAY for
        // XWayland).
        Assert.Equal(
            ForegroundAppSourceKind.KWin,
            ForegroundAppMonitor.Detect(Env(
                ("DISPLAY", ":0"),
                ("WAYLAND_DISPLAY", "wayland-0"),
                ("XDG_CURRENT_DESKTOP", "KDE"))));
    }

    [Fact]
    public void DetectReturnsNoneWithoutAnySession()
    {
        Assert.Equal(ForegroundAppSourceKind.None, ForegroundAppMonitor.Detect(Clean));
    }

    [Fact]
    public async Task MonitorWithoutAdapterPublishesUnavailableStatus()
    {
        await using var monitor = new ForegroundAppMonitor(environment: Clean);

        await monitor.StartAsync();

        Assert.Null(monitor.Source);
        Assert.Equal(ForegroundAppSourceState.Unavailable, monitor.Status.State);
        Assert.Equal(ForegroundApplicationSnapshot.Unknown, monitor.Cache.Current);
    }

    [Fact]
    public async Task MonitorBuildsPollingSourceFromEnvironment()
    {
        var socketPath = Path.Combine(
            Path.GetTempPath(),
            $"battuta-sway-{Guid.NewGuid():N}.sock");
        using var server = FakeSwayIpcServer.Start(socketPath);
        await using var monitor = new ForegroundAppMonitor(
            environment: Env(("SWAYSOCK", socketPath)));

        await monitor.StartAsync();

        Assert.IsType<SwayForegroundAppSource>(monitor.Source);
        var identity = await WaitUntilAsync(
            () => !ReferenceEquals(monitor.Cache.Current, ForegroundApplicationSnapshot.Unknown)
                ? monitor.Cache.Current
                : null,
            TimeSpan.FromSeconds(5));
        Assert.Equal("app:foot", identity.ProcessKey);
        Assert.Equal("foot", identity.DisplayName);
        Assert.Equal(ForegroundAppSourceState.Running, monitor.Status.State);
    }

    private static async Task<ForegroundApplicationSnapshot> WaitUntilAsync(
        Func<ForegroundApplicationSnapshot?> probe,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var value = probe();
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(50);
        }

        Assert.Fail("Timed out waiting for a foreground application snapshot.");
        throw new UnreachableException();
    }
}

/// <summary>
/// Minimal i3/sway IPC endpoint over a private unix socket: answers every
/// request with a fixed GET_TREE reply whose focused leaf is <c>foot</c>.
/// </summary>
internal sealed class FakeSwayIpcServer : IDisposable
{
    public static FakeSwayIpcServer Start(string socketPath) =>
        new(socketPath);

    private const string TreeReply = """
    {
      "id": 1,
      "nodes": [
        { "id": 2, "focused": false, "app_id": "bar" },
        { "id": 3, "focused": true, "app_id": "foot", "pid": 4242 }
      ]
    }
    """;

    private readonly Socket listener;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task acceptLoop;
    private int disposed;

    private FakeSwayIpcServer(string socketPath)
    {
        var directory = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(4);
        acceptLoop = Task.Run(() => AcceptLoopAsync(socketPath, cancellation.Token));
    }

    private async Task AcceptLoopAsync(string socketPath, CancellationToken cancellationToken)
    {
        var frame = SwayIpc.FormatMessage(
            SwayIpc.MessageType.GetTree,
            TreeReply);
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

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await using var stream = new NetworkStream(client, ownsSocket: true);
                        var request = new byte[SwayIpc.HeaderLength];
                        var headerRead = 0;
                        while (headerRead < request.Length)
                        {
                            var read = await stream
                                .ReadAsync(request.AsMemory(headerRead), cancellationToken)
                                .ConfigureAwait(false);
                            if (read == 0)
                            {
                                return;
                            }

                            headerRead += read;
                        }

                        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Client vanished; the loop keeps serving.
                    }
                },
                cancellationToken);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            listener.Dispose();
            _ = acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        cancellation.Dispose();
    }
}
