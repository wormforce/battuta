using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Battuta.Core.Audio;
using Battuta.Core.Input;
using Battuta.Windows.Settings;
using Battuta.Linux.Platform.Authorization;
using Battuta.Linux.Runtime;

namespace Battuta.Linux.ViewModels;

/// <summary>
/// The Linux control window state (plan §7.3). Holds no Avalonia types so it
/// stays unit-testable headlessly; the window only binds and marshals.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly LinuxBattutaRuntime runtime;
    private readonly SynchronizationContext? uiContext;

    [ObservableProperty]
    private bool isEnabled;

    [ObservableProperty]
    private bool isPointerSoundEnabled;

    [ObservableProperty]
    private bool isTypingStatsEnabled;

    [ObservableProperty]
    private bool playsReleaseSound;

    [ObservableProperty]
    private bool playsPointerReleaseSound;

    [ObservableProperty]
    private bool usesPitchVariation;

    [ObservableProperty]
    private double volume;

    [ObservableProperty]
    private double pointerVolume;

    [ObservableProperty]
    private SoundPackOptionViewModel selectedProfile;

    [ObservableProperty]
    private PointerProfileOptionViewModel selectedPointerProfile;

    [ObservableProperty]
    private string statusText = "启动中…";

    [ObservableProperty]
    private string brokerStatusText = "连接 battuta-inputd…";

    [ObservableProperty]
    private string audioStatusText = "音频流待启动";

    [ObservableProperty]
    private string authorizationStatusText = string.Empty;

    [ObservableProperty]
    private bool isRequestingAuthorization;

    [ObservableProperty]
    private string soundPackErrorText = string.Empty;

    public partial class SoundPackOptionViewModel(
        string selectionId,
        string displayName)
    {
        public string SelectionId { get; } = selectionId;

        public string DisplayName { get; } = displayName;

        public override string ToString() => DisplayName;
    }

    public partial class PointerProfileOptionViewModel(
        string id,
        string displayName)
    {
        public string Id { get; } = id;

        public string DisplayName { get; } = displayName;

        public override string ToString() => DisplayName;
    }

    public MainViewModel(LinuxBattutaRuntime runtime)
    {
        this.runtime = runtime;
        uiContext = SynchronizationContext.Current;

        RetryBrokerCommand = new RelayCommand(RetryBroker);
        RequestAuthorizationCommand = new AsyncRelayCommand(RequestAuthorizationAsync);
        PreviewKeyboardSoundCommand = new RelayCommand(PreviewKeyboardSound);
        OpenStatsCommand = new RelayCommand(
            () => OpenStatsRequested?.Invoke(this, EventArgs.Empty));
        OpenDiyCommand = new RelayCommand(
            () => OpenDiyRequested?.Invoke(this, EventArgs.Empty));
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));

        PointerProfiles.Add(new PointerProfileOptionViewModel(
            PointerSoundProfiles.Classic.Value, "经典微动"));
        PointerProfiles.Add(new PointerProfileOptionViewModel(
            PointerSoundProfiles.Silent.Value, "静音微动"));
        PointerProfiles.Add(new PointerProfileOptionViewModel(
            PointerSoundProfiles.Crisp.Value, "电竞脆响"));
        PointerProfiles.Add(new PointerProfileOptionViewModel(
            PointerSoundProfiles.Heavy.Value, "厚重办公"));
        PointerProfiles.Add(new PointerProfileOptionViewModel(
            PointerSoundProfiles.Glass.Value, "玻璃触控板"));

        foreach (var profile in PointerProfiles)
        {
            if (string.Equals(
                profile.Id,
                runtime.Settings.SelectedPointerProfileId,
                StringComparison.Ordinal))
            {
                selectedPointerProfile = profile;
            }
        }

        selectedPointerProfile ??= PointerProfiles[0];

        var settings = runtime.Settings;
        isEnabled = settings.IsEnabled;
        isPointerSoundEnabled = settings.IsPointerSoundEnabled;
        isTypingStatsEnabled = settings.IsTypingStatsEnabled;
        playsReleaseSound = settings.PlaysReleaseSound;
        playsPointerReleaseSound = settings.PlaysPointerReleaseSound;
        usesPitchVariation = settings.UsesPitchVariation;
        volume = settings.Volume;
        pointerVolume = settings.PointerVolume;

        runtime.SettingsChanged += OnRuntimeSettingsChanged;
        runtime.SoundPackStateChanged += OnRuntimeSoundPackStateChanged;
        runtime.BrokerStatusChanged += OnBrokerStatusChanged;
        runtime.Audio.StreamReady += OnStreamReady;
        runtime.Audio.StreamFailed += OnStreamFailed;
        if (runtime.Audio.IsStreamReady)
        {
            AudioStatusText = "音频流已就绪";
        }
    }

    public bool IsExiting { get; set; }

    /// <summary>Raised by <see cref="ExitCommand"/>; the window forwards it
    /// to the application shutdown path (the VM stays Avalonia-free).</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised by <see cref="OpenStatsCommand"/>; the app shell opens
    /// the statistics window.</summary>
    public event EventHandler? OpenStatsRequested;

    /// <summary>Raised by <see cref="OpenDiyCommand"/>; the app shell opens the
    /// DIY editor window singleton.</summary>
    public event EventHandler? OpenDiyRequested;

    public IRelayCommand RetryBrokerCommand { get; }

    public IAsyncRelayCommand RequestAuthorizationCommand { get; }

    public IRelayCommand PreviewKeyboardSoundCommand { get; }

    public IRelayCommand OpenStatsCommand { get; }

    public IRelayCommand OpenDiyCommand { get; }

    public IRelayCommand ExitCommand { get; }

    public ObservableCollection<SoundPackOptionViewModel> Profiles { get; } = [];

    public ObservableCollection<PointerProfileOptionViewModel> PointerProfiles { get; } = [];

    public bool IsBrokerConnected =>
        runtime.Broker?.Status.Phase == BrokerConnectionPhase.Connected;

    /// <summary>Loads sound pack choices and starts runtime + status pumps.
    /// Safe to call from any thread; called once by the window.</summary>
    public async Task StartAsync()
    {
        await RefreshProfilesAsync().ConfigureAwait(true);
        await runtime.StartAsync().ConfigureAwait(true);
        RefreshRuntimeStatus();
    }

    /// <summary>Reloads the DIY pack list (also used after library changes).</summary>
    public async Task RefreshProfilesAsync()
    {
        var descriptors = await runtime.SoundPackLibrary
            .DescriptorsAsync(CancellationToken.None)
            .ConfigureAwait(true);
        var active = runtime.ActiveSoundPackSelectionId ?? runtime.Settings.SelectedProfileId;
        Profiles.Clear();
        SoundPackOptionViewModel? selection = null;
        foreach (var descriptor in descriptors)
        {
            var option = new SoundPackOptionViewModel(
                descriptor.SelectionId,
                descriptor.Name);
            Profiles.Add(option);
            if (string.Equals(option.SelectionId, active, StringComparison.Ordinal))
            {
                selection = option;
            }
        }

        SelectedProfile = selection ?? Profiles.FirstOrDefault()
            ?? new SoundPackOptionViewModel(runtime.Settings.SelectedProfileId, "当前音色");
    }

    partial void OnIsEnabledChanged(bool value) =>
        ApplySettings(settings => settings with { IsEnabled = value });

    partial void OnIsPointerSoundEnabledChanged(bool value) =>
        ApplySettings(settings => settings with { IsPointerSoundEnabled = value });

    partial void OnIsTypingStatsEnabledChanged(bool value) =>
        ApplySettings(settings => settings with { IsTypingStatsEnabled = value });

    partial void OnPlaysReleaseSoundChanged(bool value) =>
        ApplySettings(settings => settings with { PlaysReleaseSound = value });

    partial void OnPlaysPointerReleaseSoundChanged(bool value) =>
        ApplySettings(settings => settings with { PlaysPointerReleaseSound = value });

    partial void OnUsesPitchVariationChanged(bool value) =>
        ApplySettings(settings => settings with { UsesPitchVariation = value });

    partial void OnVolumeChanged(double value) =>
        ApplySettings(settings => settings with { Volume = Math.Clamp(value, 0d, 1.2d) });

    partial void OnPointerVolumeChanged(double value) =>
        ApplySettings(settings => settings with { PointerVolume = Math.Clamp(value, 0d, 1.2d) });

    partial void OnSelectedProfileChanged(SoundPackOptionViewModel value)
    {
        var selectionId = value.SelectionId;
        _ = ActivateProfileAsync(selectionId);
    }

    partial void OnSelectedPointerProfileChanged(PointerProfileOptionViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        var id = value.Id;
        ApplySettings(settings => settings with { SelectedPointerProfileId = id });
    }

    /// <summary>试听 current keyboard sound with the live settings.</summary>
    public void PreviewKeyboardSound() => runtime.AudioEngine.PlayKeyboard(
        PhysicalKeys.KeyA,
        KeySoundPhase.Press,
        runtime.Settings.Volume,
        runtime.Settings.UsesPitchVariation);

    /// <summary>重试 broker connection without the polkit flow.</summary>
    public void RetryBroker() => runtime.RetryBrokerConnection();

    /// <summary>The GUI authorization flow: interactive polkit check for the
    /// subscribe action, then resume the broker loop.</summary>
    public async Task RequestAuthorizationAsync()
    {
        if (IsRequestingAuthorization)
        {
            return;
        }

        IsRequestingAuthorization = true;
        try
        {
            AuthorizationStatusText = "正在请求系统授权…";
            var decision = await runtime
                .RequestBrokerAuthorizationAsync(interactive: true)
                .ConfigureAwait(true);
            AuthorizationStatusText = decision switch
            {
                PolkitDecision.Authorized => "已获得系统授权,正在重连 battuta-inputd…",
                PolkitDecision.Challenge => "系统弹出了授权对话框,请输入凭据。",
                _ => "授权被拒绝:请在系统提示中输入管理员凭据后重试。",
            };
        }
        catch (PolkitRequestException error) when (
            error.Failure == PolkitRequestFailure.ActionNotRegistered)
        {
            // The authority parsed the subject and looked the action up;
            // the .policy file from the Battuta system package is missing.
            AuthorizationStatusText =
                "Battuta 的系统授权组件尚未安装:请通过系统安装包(deb/rpm)部署后重试。";
        }
        catch (PolkitRequestException error) when (
            error.Failure == PolkitRequestFailure.AuthorityUnavailable)
        {
            AuthorizationStatusText = "系统 polkit 服务不可用:无法完成授权检查。";
        }
        catch (Exception error)
        {
            AuthorizationStatusText = $"授权请求失败:{error.Message}";
        }
        finally
        {
            IsRequestingAuthorization = false;
        }
    }

    private bool suppressSettingsPush;

    private void ApplySettings(Func<AppSettingsSnapshot, AppSettingsSnapshot> update)
    {
        if (suppressSettingsPush)
        {
            return;
        }

        runtime.UpdateSettings(update);
    }

    private void OnRuntimeSettingsChanged(object? sender, EventArgs eventArgs) =>
        Post(RefreshFromRuntimeSettings);

    private void OnRuntimeSoundPackStateChanged(object? sender, EventArgs eventArgs) =>
        Post(() =>
        {
            SoundPackErrorText = runtime.SoundPackError ?? string.Empty;
            RefreshRuntimeStatus();
        });

    private void OnBrokerStatusChanged(object? sender, EventArgs eventArgs)
    {
        Post(() =>
        {
            BrokerStatusText = DescribeBrokerStatus();
            RefreshRuntimeStatus();
        });
    }

    private void OnStreamReady(object? sender, EventArgs eventArgs) =>
        Post(() => AudioStatusText = "音频流已就绪");

    private void OnStreamFailed(object? sender, EventArgs eventArgs) =>
        Post(() => AudioStatusText = "音频流异常,正在自动恢复…");

    private void RefreshFromRuntimeSettings()
    {
        var settings = runtime.Settings;
        // Event-driven mirror: push runtime state into the properties with
        // the push-to-runtime path suppressed (SetProperty no-ops when the
        // value is unchanged, so this settles immediately).
        suppressSettingsPush = true;
        try
        {
            IsEnabled = settings.IsEnabled;
            IsPointerSoundEnabled = settings.IsPointerSoundEnabled;
            IsTypingStatsEnabled = settings.IsTypingStatsEnabled;
            PlaysReleaseSound = settings.PlaysReleaseSound;
            PlaysPointerReleaseSound = settings.PlaysPointerReleaseSound;
            UsesPitchVariation = settings.UsesPitchVariation;
            Volume = settings.Volume;
            PointerVolume = settings.PointerVolume;

            var pointer = PointerProfiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, settings.SelectedPointerProfileId, StringComparison.Ordinal));
            if (pointer is not null && !ReferenceEquals(pointer, SelectedPointerProfile))
            {
                SelectedPointerProfile = pointer;
            }
        }
        finally
        {
            suppressSettingsPush = false;
        }

        RefreshRuntimeStatus();
    }

    private void RefreshRuntimeStatus()
    {
        StatusText = runtime.StatusText;
        SoundPackErrorText = runtime.SoundPackError ?? string.Empty;
        OnPropertyChanged(nameof(IsBrokerConnected));
    }

    private string DescribeBrokerStatus()
    {
        if (runtime.Broker is not { } broker)
        {
            return "未启用 battuta-inputd 集成";
        }

        return broker.Status.Phase switch
        {
            BrokerConnectionPhase.Disabled => "未启用 battuta-inputd 集成",
            BrokerConnectionPhase.Connecting => "正在连接 battuta-inputd…",
            BrokerConnectionPhase.Connected => "已连接 battuta-inputd",
            BrokerConnectionPhase.Rejected => $"battuta-inputd 拒绝订阅:{broker.Status.Detail ?? "需要系统授权"}",
            BrokerConnectionPhase.Unavailable =>
                $"未检测到 battuta-inputd,将在后台自动重试。{broker.Status.Detail ?? "请安装 Battuta 系统包并启用 battuta-inputd.socket。"}",
            BrokerConnectionPhase.Failed => $"battuta-inputd 连接失败:{broker.Status.Detail}",
            _ => broker.Status.Phase.ToString(),
        };
    }

    private async Task ActivateProfileAsync(string selectionId)
    {
        try
        {
            await runtime
                .ActivateSoundPackAsync(selectionId)
                .ConfigureAwait(true);
        }
        catch (Exception error)
        {
            SoundPackErrorText = $"切换音色失败:{error.Message}";
        }

        RefreshRuntimeStatus();
    }

    private void Post(Action action)
    {
        if (uiContext is not null)
        {
            uiContext.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }
}
