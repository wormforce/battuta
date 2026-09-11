using Battuta.Core.Audio;
using Battuta.Core.Input;
using Battuta.Windows.Audio;
using Battuta.Windows.Bootstrap;
using Battuta.Windows.Diy.Packages;
using Battuta.Windows.Input;
using Battuta.Windows.Paths;
using Battuta.Windows.Settings;
using Battuta.Windows.Stats.Persistence;
using Battuta.Windows.Stats.Services;
using Battuta.Windows.Stats.ViewModels;
using Battuta.Windows.Startup;

namespace Battuta.Windows.Runtime;

/// <summary>
/// Owns the live Windows input/audio/statistics pipeline. The hook callback only
/// enqueues physical events; this sink performs the inexpensive runtime routing.
/// </summary>
public sealed class BattutaRuntime : IWindowsInputEventSink, IAsyncDisposable
{
    private readonly PlatformBootstrapResult _platform;
    private readonly TypingStatsInputEventSink _statisticsSink;
    private readonly DiySoundPackLibrary _soundPackLibrary;
    private readonly SoundPackRuntimeController _soundPackController;
    private readonly RuntimeLaunchAtLoginController _launchAtLoginController;
    private Task _soundPackActivationTask = Task.CompletedTask;
    private AppSettingsSnapshot _settings;
    private int _started;
    private int _disposed;

    public BattutaRuntime(PlatformBootstrapResult platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        if (!platform.IsPrimary || platform.Paths is null || platform.Settings is null)
        {
            throw new ArgumentException("A primary initialized platform result is required.", nameof(platform));
        }

        _settings = platform.Settings.Normalize();
        Mixer = new PolyphonicSampleProvider();
        AudioOutput = new AudioOutputService(Mixer);
        AudioEngine = new KeyboardAudioEngine(Mixer);
        _soundPackLibrary = new DiySoundPackLibrary(platform.Paths.SoundPacksDirectory);
        _soundPackController = new SoundPackRuntimeController(
            AudioEngine,
            _soundPackLibrary.LoadAsync);
        _soundPackController.StateChanged += SoundPackControllerStateChanged;
        _launchAtLoginController = new RuntimeLaunchAtLoginController(
            platform.LaunchAtLogin,
            platform.LaunchAtLoginState);
        StatisticsStore = new TypingStatsSqliteStore(platform.Paths.StatisticsDatabaseFile);
        StatisticsRecorder = new TypingStatsRecorder(StatisticsStore);
        StatisticsViewModel = new TypingStatsViewModel(StatisticsRecorder);
        StatisticsSummaryViewModel = new TypingStatsSummaryViewModel(StatisticsViewModel);
        _statisticsSink = new TypingStatsInputEventSink(
            StatisticsRecorder,
            () => Settings.IsTypingStatsEnabled);
        InputHooks = new Win32InputHookService(this);
    }

    public event EventHandler? SettingsChanged;

    public event EventHandler? SoundPackStateChanged;

    public event EventHandler? LaunchAtLoginStateChanged
    {
        add => _launchAtLoginController.StateChanged += value;
        remove => _launchAtLoginController.StateChanged -= value;
    }

    public AppSettingsSnapshot Settings => Volatile.Read(ref _settings);

    public AppPaths Paths => _platform.Paths!;

    public PolyphonicSampleProvider Mixer { get; }

    public AudioOutputService AudioOutput { get; }

    public KeyboardAudioEngine AudioEngine { get; }

    public DiySoundPackLibrary SoundPackLibrary => _soundPackLibrary;

    public string? SoundPackError => _soundPackController.Error;

    public string? ActiveSoundPackSelectionId => _soundPackController.ActiveSelectionId;

    public LaunchAtLoginState? LaunchAtLoginState => _launchAtLoginController.State;

    public TypingStatsSqliteStore StatisticsStore { get; }

    public TypingStatsRecorder StatisticsRecorder { get; }

    public TypingStatsViewModel StatisticsViewModel { get; }

    public TypingStatsSummaryViewModel StatisticsSummaryViewModel { get; }

    public Win32InputHookService InputHooks { get; }

    public WindowsHookStartResult HookStartResult { get; private set; }

    public string StatusText
    {
        get
        {
            var settings = Settings;
            if (!InputHooks.IsRunning)
            {
                return "键盘与点击监听未运行";
            }

            return (settings.IsEnabled, settings.IsPointerSoundEnabled, settings.IsTypingStatsEnabled) switch
            {
                (true, true, true) => "正在监听键盘与点击 · 统计已开启",
                (true, false, true) => "正在监听键盘 · 统计已开启",
                (false, true, true) => "点击音已开启 · 统计已开启",
                (false, false, true) => "声音已暂停 · 统计已开启",
                (true, true, false) => "正在监听键盘与点击 · 统计已暂停",
                (true, false, false) => "正在监听键盘 · 统计已暂停",
                (false, true, false) => "点击音已开启 · 统计已暂停",
                _ => "声音与统计均已暂停",
            };
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _ = await AudioOutput.StartAsync(cancellationToken).ConfigureAwait(false);

        var settings = Settings;
        var pointerProfile = PointerSoundProfileCatalog.TryGet(settings.SelectedPointerProfileId, out var selectedPointer)
            ? selectedPointer.Id
            : PointerSoundProfileCatalog.Default.Id;
        _ = await ActivateSoundPackCoreAsync(
            settings.SelectedProfileId,
            cancellationToken).ConfigureAwait(false);
        _ = await AudioEngine.LoadPointerProfileAsync(pointerProfile, cancellationToken).ConfigureAwait(false);

        HookStartResult = await InputHooks.StartAsync(cancellationToken).ConfigureAwait(false);
        if (settings.IsTypingStatsEnabled)
        {
            _ = StatisticsViewModel.RefreshAsync(cancellationToken);
        }
    }

    public AppSettingsSnapshot UpdateSettings(
        Func<AppSettingsSnapshot, AppSettingsSnapshot> update) =>
        UpdateSettingsCore(update, activateChangedSoundPack: true);

    public async Task<SoundPackActivationResult> ActivateSoundPackAsync(
        string selectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectionId);
        var updated = UpdateSettingsCore(
            settings => settings with { SelectedProfileId = selectionId },
            activateChangedSoundPack: false);
        return await ActivateSoundPackCoreAsync(
            updated.SelectedProfileId,
            cancellationToken).ConfigureAwait(false);
    }

    public Task WaitForPendingSoundPackActivationAsync() =>
        Volatile.Read(ref _soundPackActivationTask);

    private AppSettingsSnapshot UpdateSettingsCore(
        Func<AppSettingsSnapshot, AppSettingsSnapshot> update,
        bool activateChangedSoundPack)
    {
        ArgumentNullException.ThrowIfNull(update);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        AppSettingsSnapshot before;
        AppSettingsSnapshot after;
        do
        {
            before = Settings;
            after = update(before).Normalize();
        }
        while (!ReferenceEquals(
            Interlocked.CompareExchange(ref _settings, after, before),
            before));

        _platform.SettingsAutosave?.Schedule(after);
        if (activateChangedSoundPack
            && !string.Equals(before.SelectedProfileId, after.SelectedProfileId, StringComparison.Ordinal))
        {
            QueueSoundPackActivation(after.SelectedProfileId);
        }

        if (!string.Equals(before.SelectedPointerProfileId, after.SelectedPointerProfileId, StringComparison.Ordinal)
            && PointerSoundProfileCatalog.TryGet(after.SelectedPointerProfileId, out var pointer))
        {
            _ = AudioEngine.LoadPointerProfileAsync(pointer.Id);
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
        return after;
    }

    public async Task UpdateLaunchAtLoginAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        _ = UpdateSettings(settings => settings with { IsLaunchAtLoginEnabled = enabled });
        _ = await _launchAtLoginController
            .SetEnabledAsync(enabled, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<LaunchAtLoginState?> RefreshLaunchAtLoginStateAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _launchAtLoginController.RefreshAsync(cancellationToken);
    }

    public bool OpenLaunchAtLoginSystemSettings()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _launchAtLoginController.OpenSystemSettings();
    }

    public bool PreviewCurrentKeyboardSound() => AudioEngine.PlayKeyboard(
        PhysicalKeys.KeyA,
        KeySoundPhase.Press,
        Settings.Volume,
        Settings.UsesPitchVariation);

    public async ValueTask OnInputAsync(
        WindowsInputEvent inputEvent,
        CancellationToken cancellationToken)
    {
        var settings = Settings;
        if (inputEvent.Kind == WindowsInputKind.Keyboard)
        {
            var keyboard = inputEvent.Keyboard;
            if (settings.ShouldPlayKeyboardSound(keyboard.Phase, keyboard.IsRepeat))
            {
                _ = AudioEngine.PlayKeyboard(
                    keyboard.Key.Id,
                    keyboard.Phase == KeyPhase.Press ? KeySoundPhase.Press : KeySoundPhase.Release,
                    settings.Volume,
                    settings.UsesPitchVariation);
            }
        }
        else if (settings.IsPointerSoundEnabled)
        {
            var pointer = inputEvent.Mouse;
            if (pointer.Phase == KeyPhase.Press || settings.PlaysPointerReleaseSound)
            {
                _ = AudioEngine.PlayPointer(
                    ToPointerButton(pointer.Button),
                    pointer.Phase == KeyPhase.Press ? PointerSoundPhase.Press : PointerSoundPhase.Release,
                    settings.PointerVolume,
                    settings.UsesPitchVariation);
            }
        }

        await _statisticsSink.OnInputAsync(inputEvent, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await InputHooks.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            StatisticsSummaryViewModel.Dispose();
            StatisticsViewModel.Dispose();
            await StatisticsRecorder.DisposeAsync().ConfigureAwait(false);
            await StatisticsStore.DisposeAsync().ConfigureAwait(false);
            _soundPackController.StateChanged -= SoundPackControllerStateChanged;
            await _soundPackController.DisposeAsync().ConfigureAwait(false);
            try
            {
                await Volatile.Read(ref _soundPackActivationTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _soundPackLibrary.Dispose();
            await AudioOutput.DisposeAsync().ConfigureAwait(false);
            await _platform.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<SoundPackActivationResult> ActivateSoundPackCoreAsync(
        string selectionId,
        CancellationToken cancellationToken)
    {
        var activation = _soundPackController.ActivateAsync(selectionId, cancellationToken);
        Volatile.Write(ref _soundPackActivationTask, activation);
        return await activation.ConfigureAwait(false);
    }

    private void QueueSoundPackActivation(string selectionId)
    {
        var activation = ActivateSoundPackCoreAsync(selectionId, CancellationToken.None);
        Volatile.Write(ref _soundPackActivationTask, activation);
        _ = ObserveSoundPackActivationAsync(activation);
    }

    private static async Task ObserveSoundPackActivationAsync(Task activation)
    {
        try
        {
            await activation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SoundPackControllerStateChanged(object? sender, EventArgs eventArgs)
    {
        SoundPackStateChanged?.Invoke(this, EventArgs.Empty);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static PointerButton ToPointerButton(WindowsPointerButton button) => button switch
    {
        WindowsPointerButton.Primary => PointerButton.Primary,
        WindowsPointerButton.Secondary => PointerButton.Secondary,
        WindowsPointerButton.Middle => PointerButton.Middle,
        WindowsPointerButton.X1 => PointerButton.Auxiliary(3),
        WindowsPointerButton.X2 => PointerButton.Auxiliary(4),
        _ => PointerButton.Primary,
    };
}
