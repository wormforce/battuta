using Battuta.Core.Audio;
using Battuta.Core.Input;
using Battuta.Windows.Audio;
using Battuta.Engine.Input;
using Battuta.Linux.Platform.Paths;
using Battuta.Linux.Runtime;
using Battuta.Linux.Tests.Runtime;
using Battuta.Linux.ViewModels;
using Xunit;

namespace Battuta.Linux.Tests.ViewModels;

/// <summary>
/// The control-window view model has no Avalonia dependency, so it runs
/// headless against a runtime with a fake audio backend (plan §7.3).
/// </summary>
public sealed class MainViewModelTests : XdgSandboxedTest
{
    private static void CreateStubSources(string audioRoot) =>
        // Reuse the runtime fixture's stub layout.
        LinuxBattutaRuntimeTests.CreateStubSourcesFor(audioRoot);

    private (LinuxBattutaRuntime Runtime, FakeAudioStreamController Audio) CreateRuntime()
    {
        var audioRoot = Path.Combine(Root, "Assets", "Sounds");
        CreateStubSources(audioRoot);
        var audio = new FakeAudioStreamController();
        var runtime = new LinuxBattutaRuntime(new LinuxRuntimeOptions
        {
            Paths = LinuxAppPaths.ForCurrentProcess(),
            AudioRoot = audioRoot,
            AudioController = audio,
            Loader = new StubAudioSampleLoader(),
            EnableBroker = false,
            EnableForegroundApps = false,
        });
        return (runtime, audio);
    }

    [Fact]
    public async Task StartLoadsProfilesAndMirrorsSettings()
    {
        var (runtime, audio) = CreateRuntime();
        await using var _ = runtime;
        using var __ = audio;
        var viewModel = new MainViewModel(runtime);

        await viewModel.StartAsync();

        Assert.NotEmpty(viewModel.Profiles);
        Assert.NotNull(viewModel.SelectedProfile);
        Assert.Equal(runtime.Settings.SelectedProfileId, viewModel.SelectedProfile.SelectionId);
        Assert.Contains("监听", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TogglingControlsWritesThroughToRuntime()
    {
        var (runtime, audio) = CreateRuntime();
        await using var _ = runtime;
        using var __ = audio;
        var viewModel = new MainViewModel(runtime);
        await viewModel.StartAsync();

        viewModel.IsEnabled = false;
        viewModel.Volume = 0.9d;
        viewModel.PlaysReleaseSound = false;

        Assert.False(runtime.Settings.IsEnabled);
        Assert.Equal(0.9d, runtime.Settings.Volume);
        Assert.False(runtime.Settings.PlaysReleaseSound);
    }

    [Fact]
    public async Task RuntimeSettingsChangeMirrorsIntoProperties()
    {
        var (runtime, audio) = CreateRuntime();
        await using var _ = runtime;
        using var __ = audio;
        var viewModel = new MainViewModel(runtime);
        await viewModel.StartAsync();

        runtime.UpdateSettings(settings => settings with
        {
            IsPointerSoundEnabled = false,
            PointerVolume = 0.25d,
        });

        Assert.False(viewModel.IsPointerSoundEnabled);
        Assert.Equal(0.25d, viewModel.PointerVolume);
    }

    [Fact]
    public async Task SelectingProfileActivatesIt()
    {
        var (runtime, audio) = CreateRuntime();
        await using var _ = runtime;
        using var __ = audio;
        var viewModel = new MainViewModel(runtime);
        await viewModel.StartAsync();
        var target = viewModel.Profiles.First(p =>
            p.SelectionId == SwitchProfileCatalog.All[1].Id.Value);

        viewModel.SelectedProfile = target;
        await runtime.WaitForPendingSoundPackActivationAsync();

        Assert.Equal(target.SelectionId, runtime.ActiveSoundPackSelectionId);
    }

    [Fact]
    public async Task ExitCommandRaisesExitRequested()
    {
        var (runtime, audio) = CreateRuntime();
        await using var _ = runtime;
        using var __ = audio;
        var viewModel = new MainViewModel(runtime);
        await viewModel.StartAsync();
        var raised = false;
        viewModel.ExitRequested += (_, _) => raised = true;

        viewModel.ExitCommand.Execute(null);

        Assert.True(raised);
    }
}
