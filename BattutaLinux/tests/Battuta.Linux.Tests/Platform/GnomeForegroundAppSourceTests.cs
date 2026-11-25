using Battuta.Engine.Input;
using Battuta.Linux.Platform.ForegroundApps;
using Xunit;

namespace Battuta.Linux.Tests.Platform;

/// <summary>
/// GNOME Shell extension flow (Linux port PR 11): the source installs the
/// embedded extension into the injected user directory, reports precise
/// status (Waiting until the user enables it, Running once enabled,
/// NeedsExtension without an install path), and the embedded text never
/// touches window titles (plan §8.3).
/// </summary>
public sealed class GnomeForegroundAppSourceTests : IDisposable
{
    private readonly PrivateSessionBus bus = new();
    private readonly string extensionDirectory =
        Path.Combine(Path.GetTempPath(), $"battuta-gnome-{Guid.NewGuid():N}");

    public void Dispose()
    {
        bus.Dispose();
        try
        {
            Directory.Delete(extensionDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private List<string> EnabledExtensions { get; } = [];

    private Func<CancellationToken, Task<IReadOnlyCollection<string>>> Probe =>
        _ => Task.FromResult<IReadOnlyCollection<string>>(EnabledExtensions.ToArray());

    [Fact]
    public async Task StartAsyncInstallsExtensionAndReportsWaitingUntilEnabled()
    {
        if (!bus.Available)
        {
            Console.WriteLine("[agent-test] skipped: dbus-daemon unavailable");
            return;
        }

        var cache = new ForegroundApplicationCache();
        await using var agent = new ForegroundAgentService(cache, busAddress: bus.Address);
        await using var source = new GnomeForegroundAppSource(
            agent,
            extensionDirectory,
            enabledExtensionsProbe: Probe);

        await source.StartAsync();

        var metadata = File.ReadAllText(Path.Combine(extensionDirectory, "metadata.json"));
        Assert.Contains($"\"uuid\": \"{GnomeForegroundAppSource.ExtensionUuid}\"", metadata);
        var script = File.ReadAllText(Path.Combine(extensionDirectory, "extension.js"));
        Assert.Contains(ForegroundAgentService.BusName, script);
        Assert.Contains(ForegroundAgentService.ObjectPath, script);
        Assert.Contains("'notify::focus-window'", script);

        Assert.Equal(ForegroundAppSourceState.Waiting, source.Status.State);
        Assert.Contains("gnome-extensions enable", source.Status.Detail);
        Assert.Contains(GnomeForegroundAppSource.ExtensionUuid, source.Status.Detail);
    }

    [Fact]
    public async Task EnabledExtensionSwitchesToRunning()
    {
        if (!bus.Available)
        {
            Console.WriteLine("[agent-test] skipped: dbus-daemon unavailable");
            return;
        }

        EnabledExtensions.Add(GnomeForegroundAppSource.ExtensionUuid);
        var cache = new ForegroundApplicationCache();
        await using var agent = new ForegroundAgentService(cache, busAddress: bus.Address);
        await using var source = new GnomeForegroundAppSource(
            agent,
            extensionDirectory,
            enabledExtensionsProbe: Probe);

        await source.StartAsync();

        Assert.Equal(ForegroundAppSourceState.Running, source.Status.State);
        Assert.Contains("已启用", source.Status.Detail);
    }

    [Fact]
    public async Task MissingInstallDirectoryReportsNeedsExtension()
    {
        if (!bus.Available)
        {
            Console.WriteLine("[agent-test] skipped: dbus-daemon unavailable");
            return;
        }

        var cache = new ForegroundApplicationCache();
        await using var agent = new ForegroundAgentService(cache, busAddress: bus.Address);
        await using var source = new GnomeForegroundAppSource(
            agent,
            extensionDirectory: null,
            enabledExtensionsProbe: Probe);

        await source.StartAsync();

        Assert.Equal(ForegroundAppSourceState.NeedsExtension, source.Status.State);
        Assert.Contains("未知", source.Status.Detail);
    }

    [Fact]
    public async Task RescanPicksUpLateEnablement()
    {
        if (!bus.Available)
        {
            Console.WriteLine("[agent-test] skipped: dbus-daemon unavailable");
            return;
        }

        var cache = new ForegroundApplicationCache();
        await using var agent = new ForegroundAgentService(cache, busAddress: bus.Address);
        await using var source = new GnomeForegroundAppSource(
            agent,
            extensionDirectory,
            enabledExtensionsProbe: Probe)
        {
            RescanInterval = TimeSpan.FromMilliseconds(20),
        };

        await source.StartAsync();
        Assert.Equal(ForegroundAppSourceState.Waiting, source.Status.State);

        // The user enables the extension later; the bounded rescan notices.
        EnabledExtensions.Add(GnomeForegroundAppSource.ExtensionUuid);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (source.Status.State != ForegroundAppSourceState.Running &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(ForegroundAppSourceState.Running, source.Status.State);
    }

    [Fact]
    public void EmbeddedExtensionNeverReadsWindowTitles()
    {
        var script = GnomeForegroundAppSource.ExtensionScript;

        // Privacy boundary (plan §8.3): only identity getters, never titles.
        Assert.DoesNotContain("get_title", script, StringComparison.Ordinal);
        Assert.DoesNotContain("get_name", script, StringComparison.Ordinal);
        Assert.DoesNotContain("window-title", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("get_gtk_application_id", script, StringComparison.Ordinal);
        Assert.Contains("get_wm_class", script, StringComparison.Ordinal);
    }
}
