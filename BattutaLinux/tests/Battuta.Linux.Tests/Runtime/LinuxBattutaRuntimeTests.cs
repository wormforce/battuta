using Xunit;
using Battuta.Core.Audio;
using Battuta.Core.Input;
using Battuta.Windows.Audio;
using Battuta.Engine.Input;
using Battuta.Windows.Settings;
using Battuta.Linux.Platform.Paths;
using Battuta.Linux.Runtime;

namespace Battuta.Linux.Tests.Runtime;

/// <summary>Returns tiny finite PCM for every decode request; the catalog
/// still resolves real stub source files, so file presence is exercised.</summary>
public sealed class StubAudioSampleLoader : IAudioSampleLoader
{
    public PreparedPcmSample Decode(string path, bool trimKeyboardLeadingSilence, CancellationToken cancellationToken) =>
        new([0f, 0.1f, 0f, -0.1f]);
}

/// <summary>
/// Redirects the XDG roots at a per-test temp directory. These tests mutate
/// process environment state, so the whole assembly runs with test
/// collections parallelized off (xunit.runner.json).
/// </summary>
public abstract class XdgSandboxedTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"battuta-linux-{Guid.NewGuid():N}");

    private readonly Dictionary<string, string?> savedEnvironment = [];

    protected XdgSandboxedTest()
    {
        Directory.CreateDirectory(root);
        foreach (var name in (ReadOnlySpan<string>)
            ["XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "XDG_CACHE_HOME", "XDG_RUNTIME_DIR"])
        {
            savedEnvironment[name] = Environment.GetEnvironmentVariable(name);
            var path = Path.Combine(root, name.ToLowerInvariant());
            Directory.CreateDirectory(path);
            Environment.SetEnvironmentVariable(name, path);
        }
    }

    protected string Root => root;

    public void Dispose()
    {
        foreach (var (name, value) in savedEnvironment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

public sealed class LinuxBattutaRuntimeTests : XdgSandboxedTest
{
    internal static void CreateStubSourcesFor(string audioRoot) => CreateStubSources(audioRoot);

    private static void CreateStubSources(string audioRoot)
    {
        // Stub the canonical sources the catalog resolves for the default
        // keyboard + pointer profiles; the loader is stubbed, so content
        // does not matter — but presence does (FileNotFoundException).
        Directory.CreateDirectory(audioRoot);
        foreach (var profile in SwitchProfileCatalog.All)
        {
            foreach (var phase in Enum.GetValues<KeySoundPhase>())
            {
                var directory = Path.Combine(
                    audioRoot, profile.Id.Value, phase.DirectoryName());
                Directory.CreateDirectory(directory);
                foreach (var sample in BuiltInSamplePlan.RequiredSamples(profile, phase))
                {
                    File.WriteAllText(Path.Combine(directory, sample.ResourceName() + ".wav"), "stub");
                }
            }
        }

        foreach (var phase in Enum.GetValues<PointerSoundPhase>())
        {
            var directory = Path.Combine(
                audioRoot, "pointer", PointerSoundProfileCatalog.Default.Id.Value,
                phase == PointerSoundPhase.Press ? "press" : "release");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "PRIMARY.wav"), "stub");
        }
    }

    private LinuxBattutaRuntime CreateRuntime(
        FakeAudioStreamController audio,
        bool enableBroker = false)
    {
        var audioRoot = Path.Combine(Root, "Assets", "Sounds");
        CreateStubSources(audioRoot);
        return new LinuxBattutaRuntime(new LinuxRuntimeOptions
        {
            Paths = LinuxAppPaths.ForCurrentProcess(),
            AudioRoot = audioRoot,
            AudioController = audio,
            Loader = new StubAudioSampleLoader(),
            EnableBroker = enableBroker,
            EnableForegroundApps = false,
        });
    }

    [Fact]
    public async Task StartActivatesDefaultsAndStartsAudio()
    {
        using var audio = new FakeAudioStreamController();
        await using var runtime = CreateRuntime(audio);

        await runtime.StartAsync();

        Assert.Equal(1, audio.StartCalls);
        Assert.True(runtime.ActiveSoundPackSelectionId is not null);
        Assert.Null(runtime.SoundPackError);
        Assert.Contains("监听", runtime.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateSettingsPersistsAcrossReload()
    {
        using var audio = new FakeAudioStreamController();
        await using var runtime = CreateRuntime(audio);
        await runtime.StartAsync();

        runtime.UpdateSettings(settings => settings with
        {
            IsEnabled = false,
            Volume = 0.7d,
        });

        Assert.False(runtime.Settings.IsEnabled);
        Assert.Equal(0.7d, runtime.Settings.Volume);

        // Flushing the autosave must leave a file the next process reads.
        await runtime.SettingsAutosave.DisposeAsync().ConfigureAwait(false);
        using var reloadedStore = new JsonAppSettingsStore(
            runtime.Paths.SettingsFile,
            runtime.Paths.SettingsBackupFile);
        var persisted = await reloadedStore.LoadAsync(CancellationToken.None);
        Assert.False(persisted.IsEnabled);
        Assert.Equal(0.7d, persisted.Volume);
    }

    [Fact]
    public async Task ActivateSoundPackSwitchesSelection()
    {
        using var audio = new FakeAudioStreamController();
        await using var runtime = CreateRuntime(audio);
        await runtime.StartAsync();
        var target = SwitchProfileCatalog.All[1].Id.Value;

        var result = await runtime.ActivateSoundPackAsync(target);

        Assert.True(result.LoadedRequestedSelection);
        Assert.Equal(target, runtime.ActiveSoundPackSelectionId);
    }

    [Fact]
    public async Task MissingAudioSourcesSurfaceAnError()
    {
        using var audio = new FakeAudioStreamController();
        await using var runtime = new LinuxBattutaRuntime(new LinuxRuntimeOptions
        {
            Paths = LinuxAppPaths.ForCurrentProcess(),
            AudioRoot = Path.Combine(Root, "missing-sounds"),
            AudioController = audio,
            Loader = new StubAudioSampleLoader(),
            EnableBroker = false,
        });

        await runtime.StartAsync();

        // The stream shell starts regardless; the bank failure is surfaced
        // and no selection is active (plan §6.2 degradation).
        Assert.Equal(1, audio.StartCalls);
        Assert.NotNull(runtime.SoundPackError);
        Assert.Null(runtime.ActiveSoundPackSelectionId);
    }

    [Fact]
    public async Task KeyboardPressPlaysAndCountsStats()
    {
        using var audio = new FakeAudioStreamController();
        await using var runtime = CreateRuntime(audio);
        await runtime.StartAsync();
        runtime.UpdateSettings(settings => settings with { IsTypingStatsEnabled = true });

        await runtime.OnInputAsync(
            InputEvent.FromKeyboard(new PhysicalKeyboardEvent(
                PhysicalKeys.KeyA,
                KeyPhase.Press,
                IsRepeat: false,
                ModifierState.None,
                IsShortcutModified: false,
                InputOrigin.Synthetic,
                MonotonicTimestampNanoseconds: 0,
                Timestamp: DateTimeOffset.UtcNow,
                Sequence: 1)),
            CancellationToken.None);
        await runtime.StatisticsRecorder.FlushPendingAsync();

        var snapshot = await runtime.StatisticsStore.LoadSnapshotAsync(
            cancellationToken: CancellationToken.None);
        Assert.Equal(1, snapshot.Today.CharacterCount);
    }
}
