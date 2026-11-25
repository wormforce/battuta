using System.IO;
using Battuta.Core.Audio;
using Battuta.Core.Input;
using Battuta.Windows.Audio;
using Battuta.Windows.Audio.Linux;
using Battuta.Windows.Diy.Packages;
using Battuta.Engine.Input;
using Battuta.Windows.Settings;
using Battuta.Windows.Stats.Persistence;
using Battuta.Windows.Stats.Services;
using Battuta.Linux.Platform.Authorization;
using Battuta.Linux.Platform.ForegroundApps;
using Battuta.Linux.Platform.Paths;

namespace Battuta.Linux.Runtime;

/// <summary>Construction seams for <see cref="LinuxBattutaRuntime"/>. Tests
/// inject fakes; production leaves everything null.</summary>
public sealed record LinuxRuntimeOptions
{
    public required LinuxAppPaths Paths { get; init; }

    /// <summary>Canonical audio source tree; default is
    /// <c>&lt;app&gt;/Assets/Sounds</c> (same layout as the Windows app).</summary>
    public string? AudioRoot { get; init; }

    /// <summary>Prep product tree; default is the audio root (products sit
    /// next to the canonical sources with a <c>.p48m</c> extension).</summary>
    public string? ProductRoot { get; init; }

    /// <summary>Injected audio backend (tests). Null creates the real
    /// PipeWire service, which the runtime owns and disposes.</summary>
    public IAudioStreamController? AudioController { get; init; }

    /// <summary>Injected sample loader (tests). Null uses the prep products.</summary>
    public IAudioSampleLoader? Loader { get; init; }

    public string? BrokerSocketPath { get; init; }

    public Func<CancellationToken, ValueTask<Stream>>? BrokerConnect { get; init; }

    /// <summary>Injected foreground-application provider (tests). Null builds
    /// the compositor-detecting <see cref="ForegroundAppMonitor"/>.</summary>
    public IForegroundApplicationSnapshotProvider? ForegroundApps { get; init; }

    /// <summary>Disables foreground-application detection entirely (tests);
    /// statistics then record the application dimension as unknown.</summary>
    public bool EnableForegroundApps { get; init; } = true;

    public bool EnableBroker { get; init; } = true;

    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>
/// Owns the live Linux input → audio → statistics pipeline (plan §7 "Broker →
/// Engine → PipeWire 完整闭环"). Composition root: the shared
/// <see cref="KeyboardAudioEngine"/> runs on the PipeWire shim scheduler with
/// build-time PCM products, broker events arrive through the neutral sink,
/// and statistics use the shared recorder and schema.
/// </summary>
public sealed class LinuxBattutaRuntime : IInputEventSink, IAsyncDisposable
{
    private readonly LinuxAppPaths paths;
    private readonly TypingStatsInputEventSink statisticsSink;
    private readonly DiySoundPackLibrary soundPackLibrary;
    private readonly SoundPackRuntimeController soundPackController;
    private readonly AudioStreamSupervisor? audioSupervisor;
    private readonly BrokerInputSource? brokerSource;
    private readonly ForegroundAppMonitor? foregroundAppMonitor;
    private readonly bool ownsAudio;
    private readonly TimeProvider timeProvider;
    private Task soundPackActivationTask = Task.CompletedTask;
    private AppSettingsSnapshot _settings;
    private int started;
    private int disposed;

    public LinuxBattutaRuntime(LinuxRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        paths = options.Paths;
        paths.EnsureCreated();
        timeProvider = options.TimeProvider ?? TimeProvider.System;

        SettingsStore = new JsonAppSettingsStore(paths.SettingsFile, paths.SettingsBackupFile);
        _settings = SettingsStore.LoadAsync(CancellationToken.None).GetAwaiter().GetResult().Normalize();
        SettingsAutosave = new SettingsAutosaveService(SettingsStore);

        var audioRoot = options.AudioRoot
            ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds");
        var productRoot = options.ProductRoot ?? audioRoot;
        var loader = options.Loader ?? new PreparedAudioProductLoader(productRoot);
        if (options.AudioController is not null)
        {
            Audio = options.AudioController;
            ownsAudio = false;
        }
        else
        {
            Audio = new PipeWireAudioService();
            ownsAudio = true;
        }

        AudioEngine = new KeyboardAudioEngine(
            Audio,
            new BuiltInAudioResourceCatalog(audioRoot, loader));

        // Supervision is testable behavior, not a PipeWire detail: injected
        // controllers get the same failure/device-change recovery.
        audioSupervisor = new AudioStreamSupervisor(Audio, timeProvider);

        soundPackLibrary = new DiySoundPackLibrary(paths.SoundPacksDirectory);
        soundPackController = new SoundPackRuntimeController(
            AudioEngine,
            soundPackLibrary.LoadAsync);
        soundPackController.StateChanged += SoundPackControllerStateChanged;

        StatisticsStore = new TypingStatsSqliteStore(paths.StatisticsDatabaseFile);
        StatisticsRecorder = new TypingStatsRecorder(StatisticsStore);
        statisticsSink = new TypingStatsInputEventSink(
            StatisticsRecorder,
            () => Settings.IsTypingStatsEnabled);

        if (options.EnableBroker)
        {
            brokerSource = new BrokerInputSource(
                socketPath: options.BrokerSocketPath,
                connect: options.BrokerConnect);
            brokerSource.StatusChanged += BrokerStatusChanged;
        }

        if (options.ForegroundApps is { } injectedForeground)
        {
            ForegroundApps = injectedForeground;
        }
        else if (options.EnableForegroundApps)
        {
            var monitor = new ForegroundAppMonitor(
                timeProvider: timeProvider,
                busServicesProbe: SessionBusServiceProbe.ListServicesAsync,
                kwinScriptFile: paths.KwinForegroundAgentScriptFile,
                gnomeExtensionDirectory: paths.GnomeForegroundAgentExtensionDirectory);
            monitor.StatusChanged += ForegroundAppStatusChanged;
            foregroundAppMonitor = monitor;
            ForegroundApps = monitor.Cache;
        }
    }


    public event EventHandler? SettingsChanged;

    public event EventHandler? SoundPackStateChanged;

    public event EventHandler? BrokerStatusChanged;

    public event EventHandler? ForegroundAppsStatusChanged;

    public LinuxAppPaths Paths => paths;

    public AppSettingsSnapshot Settings => Volatile.Read(ref _settings);

    public JsonAppSettingsStore SettingsStore { get; }

    public SettingsAutosaveService SettingsAutosave { get; }

    public IAudioStreamController Audio { get; }

    public KeyboardAudioEngine AudioEngine { get; }

    public DiySoundPackLibrary SoundPackLibrary => soundPackLibrary;

    public string? SoundPackError => soundPackController.Error;

    public string? ActiveSoundPackSelectionId => soundPackController.ActiveSelectionId;

    public TypingStatsSqliteStore StatisticsStore { get; }

    public TypingStatsRecorder StatisticsRecorder { get; }

    public BrokerInputSource? Broker => brokerSource;

    /// <summary>
    /// The foreground-application provider: a live compositor adapter in
    /// production, an injected fake in tests, or null when detection is
    /// disabled. Statistics attach its snapshot to every input event.
    /// </summary>
    public IForegroundApplicationSnapshotProvider? ForegroundApps { get; private set; }

    /// <summary>The compositor adapter, when the runtime owns one.</summary>
    public ForegroundAppMonitor? ForegroundAppMonitor => foregroundAppMonitor;

    /// <summary>The single status line the window header shows.</summary>
    public string StatusText
    {
        get
        {
            var settings = Settings;
            var listening = brokerSource?.Status.Phase == BrokerConnectionPhase.Connected;
            if (!listening)
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return;
        }

        var settings = Settings;
        var pointerProfile = PointerSoundProfileCatalog.TryGet(settings.SelectedPointerProfileId, out var selectedPointer)
            ? selectedPointer.Id
            : PointerSoundProfileCatalog.Default.Id;
        _ = await ActivateSoundPackCoreAsync(
            settings.SelectedProfileId,
            cancellationToken).ConfigureAwait(false);
        _ = await AudioEngine.LoadPointerProfileAsync(pointerProfile, cancellationToken).ConfigureAwait(false);

        audioSupervisor?.Start();
        if (brokerSource is not null)
        {
            _ = Task.Run(
                () => brokerSource.RunAsync(this, CancellationToken.None),
                CancellationToken.None);
        }

        if (foregroundAppMonitor is not null)
        {
            // Detection and the compositor handshake are slow (D-Bus); the
            // rest of the pipeline never waits for them. Status changes reach
            // the UI through ForegroundAppsStatusChanged.
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await foregroundAppMonitor.StartAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // The monitor reports its own failure state.
                    }
                },
                CancellationToken.None);
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
            snapshot => snapshot with { SelectedProfileId = selectionId },
            activateChangedSoundPack: false);
        return await ActivateSoundPackCoreAsync(
            updated.SelectedProfileId,
            cancellationToken).ConfigureAwait(false);
    }

    public Task WaitForPendingSoundPackActivationAsync() =>
        Volatile.Read(ref soundPackActivationTask);

    /// <summary>
    /// The GUI authorization flow (plan §5.2 / contract §8): run the polkit
    /// check for this process — interactively, so the session agent may
    /// prompt — and resume the broker loop when the decision allows it.
    /// Returns the decision for the UI to display.
    /// </summary>
    public async Task<PolkitDecision> RequestBrokerAuthorizationAsync(
        bool interactive,
        CancellationToken cancellationToken = default)
    {
        if (brokerSource is null)
        {
            throw new InvalidOperationException("The broker source is disabled.");
        }

        await using var polkit = await PolkitClient
            .ConnectAsync(cancellationToken)
            .ConfigureAwait(false);
        var decision = await polkit
            .CheckSubscribeAsync(interactive, cancellationToken)
            .ConfigureAwait(false);
        if (decision != PolkitDecision.Denied)
        {
            brokerSource.RequestRetry();
        }

        return decision;
    }

    /// <summary>Retries the broker connection without touching polkit.</summary>
    public void RetryBrokerConnection()
    {
        if (brokerSource is not null)
        {
            brokerSource.RequestRetry();
        }
    }

    public async ValueTask OnInputAsync(
        InputEvent inputEvent,
        CancellationToken cancellationToken)
    {
        var settings = Settings;
        if (inputEvent.Kind == InputEventKind.Keyboard)
        {
            var keyboard = inputEvent.Keyboard;
            if (settings.ShouldPlayKeyboardSound(keyboard.Phase, keyboard.IsRepeat))
            {
                _ = AudioEngine.PlayKeyboard(
                    keyboard.Key,
                    keyboard.Phase == KeyPhase.Press ? KeySoundPhase.Press : KeySoundPhase.Release,
                    settings.Volume,
                    settings.UsesPitchVariation);
            }
        }
        else if (inputEvent.Kind == InputEventKind.Pointer && settings.IsPointerSoundEnabled)
        {
            var pointer = inputEvent.Pointer;
            if (pointer.Phase == KeyPhase.Press || settings.PlaysPointerReleaseSound)
            {
                _ = AudioEngine.PlayPointer(
                    ToPointerButton(pointer.Button),
                    pointer.Phase == KeyPhase.Press ? PointerSoundPhase.Press : PointerSoundPhase.Release,
                    settings.PointerVolume,
                    settings.UsesPitchVariation);
            }
        }

        await statisticsSink
            .OnInputAsync(WithForegroundApplication(inputEvent), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Attaches the cached foreground-application identity to events the
    /// source produced without one. The cache read is a volatile field load —
    /// resolution happens on the slow monitor/poll path, never per keypress.
    /// </summary>
    private InputEvent WithForegroundApplication(InputEvent inputEvent)
    {
        if (inputEvent.Kind == InputEventKind.Reset || inputEvent.Application is not null)
        {
            return inputEvent;
        }

        var provider = ForegroundApps;
        var snapshot = provider?.Current;
        if (snapshot is null)
        {
            return inputEvent;
        }

        return inputEvent.Kind == InputEventKind.Keyboard
            ? InputEvent.FromKeyboard(
                inputEvent.Keyboard,
                snapshot.ToTypingApplicationIdentity())
            : InputEvent.FromPointer(
                inputEvent.Pointer,
                snapshot.ToTypingApplicationIdentity());
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (brokerSource is not null)
            {
                brokerSource.StatusChanged -= BrokerStatusChanged;
                await brokerSource.DisposeAsync().ConfigureAwait(false);
            }

            if (foregroundAppMonitor is not null)
            {
                foregroundAppMonitor.StatusChanged -= ForegroundAppStatusChanged;
                await foregroundAppMonitor.DisposeAsync().ConfigureAwait(false);
            }

            await StatisticsRecorder.DisposeAsync().ConfigureAwait(false);
            await StatisticsStore.DisposeAsync().ConfigureAwait(false);
            soundPackController.StateChanged -= SoundPackControllerStateChanged;
            await soundPackController.DisposeAsync().ConfigureAwait(false);
            try
            {
                await Volatile.Read(ref soundPackActivationTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            soundPackLibrary.Dispose();
            audioSupervisor?.Dispose();
            if (ownsAudio)
            {
                Audio.Dispose();
            }
        }
        finally
        {
            await SettingsAutosave.DisposeAsync().ConfigureAwait(false);
            SettingsStore.Dispose();
        }
    }

    private AppSettingsSnapshot UpdateSettingsCore(
        Func<AppSettingsSnapshot, AppSettingsSnapshot> update,
        bool activateChangedSoundPack)
    {
        ArgumentNullException.ThrowIfNull(update);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

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

        SettingsAutosave.Schedule(after);
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

    private async Task<SoundPackActivationResult> ActivateSoundPackCoreAsync(
        string selectionId,
        CancellationToken cancellationToken)
    {
        var activation = soundPackController.ActivateAsync(selectionId, cancellationToken);
        Volatile.Write(ref soundPackActivationTask, activation);
        return await activation.ConfigureAwait(false);
    }

    private void QueueSoundPackActivation(string selectionId)
    {
        var activation = ActivateSoundPackCoreAsync(selectionId, CancellationToken.None);
        Volatile.Write(ref soundPackActivationTask, activation);
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

    private void ForegroundAppStatusChanged(object? sender, EventArgs eventArgs) =>
        ForegroundAppsStatusChanged?.Invoke(this, EventArgs.Empty);

    private static PointerButton ToPointerButton(InputPointerButton button) => button switch
    {
        InputPointerButton.Primary => PointerButton.Primary,
        InputPointerButton.Secondary => PointerButton.Secondary,
        InputPointerButton.Middle => PointerButton.Middle,
        InputPointerButton.Side => PointerButton.Auxiliary(3),
        InputPointerButton.Extra => PointerButton.Auxiliary(4),
        _ => PointerButton.Primary,
    };
}
