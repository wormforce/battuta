using Battuta.Core.Input;
using Battuta.Engine.Input;
using Battuta.Linux.Platform.ForegroundApps;
using Battuta.Linux.Platform.Paths;
using Battuta.Linux.Runtime;
using Battuta.Linux.ViewModels.Stats;
using Xunit;

namespace Battuta.Linux.Tests.Runtime;

public sealed class ForegroundAppEnrichmentTests : XdgSandboxedTest
{
    private sealed class StubForegroundProvider(
        ForegroundApplicationSnapshot snapshot) : IForegroundApplicationSnapshotProvider
    {
        public ForegroundApplicationSnapshot Current => snapshot;
    }

    private static readonly PhysicalKeyId TestKey = PhysicalKeys.KeyA;

    [Fact]
    public async Task KeyboardEventsCarryInjectedForegroundIdentity()
    {
        using var audio = new FakeAudioStreamController();
        await using var runtime = CreateRuntime(
            audio,
            ForegroundAppSnapshotFactory.FromDesktopApplication("TestApp", 4242));
        await runtime.StartAsync();
        runtime.UpdateSettings(settings => settings with { IsTypingStatsEnabled = true });

        await PressTestKeyAsync(runtime);
        await runtime.StatisticsRecorder.FlushPendingAsync();

        var snapshot = await runtime.StatisticsStore.LoadSnapshotAsync(
            cancellationToken: CancellationToken.None);
        var appRow = Assert.Single(snapshot.Apps);

        Assert.Equal("app:testapp", appRow.ProcessKey);
        Assert.Equal("TestApp", appRow.DisplayName);
        Assert.Equal(1, appRow.CharacterCount);
        Assert.Equal(1, snapshot.TodayKeyCounts[TestKey]);
    }

    [Fact]
    public async Task StatsWindowModelShowsIdentityAndCounts()
    {
        using var audio = new FakeAudioStreamController();
        await using var runtime = CreateRuntime(
            audio,
            ForegroundAppSnapshotFactory.FromDesktopApplication("TestApp", 4242));
        await runtime.StartAsync();
        runtime.UpdateSettings(settings => settings with { IsTypingStatsEnabled = true });

        var stats = new StatsViewModel(runtime);
        try
        {
            await PressTestKeyAsync(runtime);
            await stats.StartAsync();

            Assert.Equal(1, stats.TodayCharacterCount);
            Assert.Equal(1, stats.TodayPhysicalPresses);
            var appRow = Assert.Single(stats.Apps);
            Assert.Equal("TestApp", appRow.DisplayName);
            Assert.Equal(1, stats.Heatmap.MaxCount);
            var cell = stats.Heatmap.Cells.Single(c => c.KeyId == TestKey);
            Assert.Equal(1, cell.Count);
            Assert.Equal(1, cell.Intensity);
            Assert.Contains("应用识别", stats.DataSourceText, StringComparison.Ordinal);

            // Scope toggle switches count dictionaries without losing data.
            stats.ToggleHeatmapScopeCommand.Execute(null);
            Assert.Equal(1, stats.Heatmap.Cells.Single(c => c.KeyId == TestKey).Count);
        }
        finally
        {
            stats.Dispose();
        }
    }

    [Fact]
    public async Task MissingProviderRecordsUnknownApplication()
    {
        using var audio = new FakeAudioStreamController();
        await using var runtime = CreateRuntime(audio, snapshot: null);
        await runtime.StartAsync();
        runtime.UpdateSettings(settings => settings with { IsTypingStatsEnabled = true });

        await PressTestKeyAsync(runtime);
        await runtime.StatisticsRecorder.FlushPendingAsync();

        var snapshot = await runtime.StatisticsStore.LoadSnapshotAsync(
            cancellationToken: CancellationToken.None);
        // No provider: the sink falls back to TypingApplicationIdentity.Unknown.
        var appRow = Assert.Single(snapshot.Apps);
        Assert.Equal("unknown", appRow.ProcessKey);
    }

    private static async Task PressTestKeyAsync(LinuxBattutaRuntime runtime)
    {
        await runtime.OnInputAsync(
            InputEvent.FromKeyboard(new PhysicalKeyboardEvent(
                TestKey,
                KeyPhase.Press,
                IsRepeat: false,
                ModifierState.None,
                IsShortcutModified: false,
                InputOrigin.Synthetic,
                MonotonicTimestampNanoseconds: 0,
                Timestamp: DateTimeOffset.UtcNow,
                Sequence: 1)),
            CancellationToken.None);
    }

    private LinuxBattutaRuntime CreateRuntime(
        FakeAudioStreamController audio,
        ForegroundApplicationSnapshot? snapshot)
    {
        var audioRoot = Path.Combine(Root, "Assets", "Sounds");
        LinuxBattutaRuntimeTests.CreateStubSourcesFor(audioRoot);
        return new LinuxBattutaRuntime(new LinuxRuntimeOptions
        {
            Paths = LinuxAppPaths.ForCurrentProcess(),
            AudioRoot = audioRoot,
            AudioController = audio,
            Loader = new StubAudioSampleLoader(),
            EnableBroker = false,
            EnableForegroundApps = false,
            ForegroundApps = snapshot is null
                ? null
                : new StubForegroundProvider(snapshot),
        });
    }
}
