using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Battuta.Core.Audio;
using Battuta.Core.SoundPacks;
using Battuta.Windows.Runtime;
using Battuta.Windows.Settings;
using Battuta.Windows.Startup;
using Battuta.Windows.Updates;

namespace Battuta.Windows.Views.Tray;

public partial class TrayFlyoutWindow
{
    private sealed record Choice(string Id, string Label, string Family, string Tone)
    {
        public override string ToString() => Label;
    }

    private BattutaRuntime? _runtime;
    private bool _applyingRuntimeState;
    private HttpClient? _updateHttpClient;
    private JsonUpdateCacheStore? _updateCacheStore;
    private UpdateCheckService? _updateCheckService;
    private CancellationTokenSource? _updateCancellation;
    private Task _updateOperation = Task.CompletedTask;
    private GitHubReleaseSummary? _availableRelease;
    private bool _trayResourcesDisposed;
    private CancellationTokenSource? _soundPackRefreshCancellation;
    private Task _soundPackRefreshOperation = Task.CompletedTask;
    private bool _soundPackRefreshRequested;
    private bool _soundPackSelectionInProgress;
    private SoundPackDescriptor[]? _deferredSoundPackChoices;
    private Task _launchAtLoginRefreshOperation = Task.CompletedTask;

    public void AttachRuntime(BattutaRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (ReferenceEquals(_runtime, runtime))
        {
            return;
        }

        if (_runtime is not null)
        {
            _runtime.SettingsChanged -= RuntimeSettingsChanged;
            _runtime.StatisticsSummaryViewModel.PropertyChanged -= StatisticsSummaryChanged;
        }

        _runtime = runtime;
        InitializeUpdateServices(runtime);
        _soundPackRefreshCancellation = new CancellationTokenSource();
        ApplySoundPackChoices(SoundPackDescriptors.BundledDefaults);
        KeyboardProfileCombo.DisplayMemberPath = nameof(Choice.Label);
        KeyboardProfileCombo.SelectedValuePath = nameof(Choice.Id);

        PointerProfileCombo.ItemsSource = null;
        PointerProfileCombo.Items.Clear();
        PointerProfileCombo.ItemsSource = PointerSoundProfileCatalog.All
            .Select(profile => new Choice(
                profile.Id.Value,
                $"{profile.DisplayName} · {profile.Family}",
                profile.Family,
                profile.Tone))
            .ToArray();
        PointerProfileCombo.DisplayMemberPath = nameof(Choice.Label);
        PointerProfileCombo.SelectedValuePath = nameof(Choice.Id);

        StatsToggle.Checked += RuntimeControlChanged;
        StatsToggle.Unchecked += RuntimeControlChanged;
        KeyboardToggle.Checked += RuntimeControlChanged;
        KeyboardToggle.Unchecked += RuntimeControlChanged;
        KeyboardProfileCombo.SelectionChanged += KeyboardProfileChanged;
        KeyboardVolumeSlider.ValueChanged += RuntimeControlChanged;
        KeyboardReleaseToggle.Checked += RuntimeControlChanged;
        KeyboardReleaseToggle.Unchecked += RuntimeControlChanged;
        KeyboardRepeatToggle.Checked += RuntimeControlChanged;
        KeyboardRepeatToggle.Unchecked += RuntimeControlChanged;
        VariationToggle.Checked += RuntimeControlChanged;
        VariationToggle.Unchecked += RuntimeControlChanged;
        PointerToggle.Checked += RuntimeControlChanged;
        PointerToggle.Unchecked += RuntimeControlChanged;
        PointerProfileCombo.SelectionChanged += PointerProfileChanged;
        PointerVolumeSlider.ValueChanged += RuntimeControlChanged;
        PointerReleaseToggle.Checked += RuntimeControlChanged;
        PointerReleaseToggle.Unchecked += RuntimeControlChanged;
        LaunchAtLoginToggle.Checked += LaunchAtLoginChanged;
        LaunchAtLoginToggle.Unchecked += LaunchAtLoginChanged;
        OpenLaunchAtLoginSettingsButton.Click += OpenLaunchAtLoginSettingsClick;
        PreviewButton.Click += PreviewClick;
        AutomaticUpdateToggle.Checked += AutomaticUpdatePreferenceChanged;
        AutomaticUpdateToggle.Unchecked += AutomaticUpdatePreferenceChanged;
        EnableAutomaticUpdatesButton.Click += EnableAutomaticUpdatesClick;
        DisableAutomaticUpdatesButton.Click += DisableAutomaticUpdatesClick;
        UpdateActionButton.Click += UpdateActionClick;
        IsVisibleChanged += TrayVisibilityChanged;

        runtime.SettingsChanged += RuntimeSettingsChanged;
        runtime.SoundPackStateChanged += SoundPackStateChanged;
        runtime.LaunchAtLoginStateChanged += LaunchAtLoginStateChanged;
        runtime.StatisticsSummaryViewModel.PropertyChanged += StatisticsSummaryChanged;
        Closed += RuntimeWindowClosed;
        ApplyRuntimeState();
        StartSoundPackRefresh();
    }

    private void RuntimeControlChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingRuntimeState || _runtime is null)
        {
            return;
        }

        _runtime.UpdateSettings(settings => settings with
        {
            IsTypingStatsEnabled = StatsToggle.IsChecked == true,
            IsEnabled = KeyboardToggle.IsChecked == true,
            Volume = KeyboardVolumeSlider.Value / 100d,
            PlaysReleaseSound = KeyboardReleaseToggle.IsChecked == true,
            PlaysKeyRepeatSound = KeyboardRepeatToggle.IsChecked == true,
            UsesPitchVariation = VariationToggle.IsChecked == true,
            IsPointerSoundEnabled = PointerToggle.IsChecked == true,
            PointerVolume = PointerVolumeSlider.Value / 100d,
            PlaysPointerReleaseSound = PointerReleaseToggle.IsChecked == true,
        });
    }

    private async void LaunchAtLoginChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingRuntimeState || _runtime is null)
        {
            return;
        }

        var enabled = LaunchAtLoginToggle.IsChecked == true;
        LaunchAtLoginToggle.IsEnabled = false;
        LaunchAtLoginStatusText.Text = enabled
            ? "正在请求 Windows 启用登录启动…"
            : "正在请求 Windows 关闭登录启动…";
        try
        {
            await _runtime.UpdateLaunchAtLoginAsync(enabled);
            ApplyLaunchAtLoginState();
        }
        catch (Exception exception)
        {
            LaunchAtLoginStatusText.Text = $"无法修改登录启动项：{exception.Message}";
        }
        finally
        {
            if (_runtime is not null)
            {
                ApplyLaunchAtLoginState();
            }
        }
    }

    private void LaunchAtLoginStateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyLaunchAtLoginState();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(ApplyLaunchAtLoginState);
        }
    }

    private void StartLaunchAtLoginRefresh()
    {
        if (_runtime is null || !_launchAtLoginRefreshOperation.IsCompleted)
        {
            return;
        }

        _launchAtLoginRefreshOperation = RefreshLaunchAtLoginStateAsync(_runtime);
    }

    private async Task RefreshLaunchAtLoginStateAsync(BattutaRuntime runtime)
    {
        var cancellationToken = _soundPackRefreshCancellation?.Token ?? CancellationToken.None;
        try
        {
            _ = await runtime.RefreshLaunchAtLoginStateAsync(cancellationToken);
            ApplyLaunchAtLoginState();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LaunchAtLoginStatusText.Text = $"无法读取登录启动项：{exception.Message}";
        }
    }

    private void ApplyLaunchAtLoginState()
    {
        if (_runtime is null)
        {
            return;
        }

        var state = _runtime.LaunchAtLoginState;
        if (state is null)
        {
            LaunchAtLoginStatusText.Text = "正在读取登录项状态…";
            LaunchAtLoginToggle.IsEnabled = false;
            OpenLaunchAtLoginSettingsButton.Visibility = Visibility.Collapsed;
            return;
        }

        LaunchAtLoginStatusText.Text = state.Description;
        LaunchAtLoginToggle.IsEnabled = state.CanChangeInApplication;
        var shouldShowSystemSettings = state.CanOpenSystemSettings
            && state.Status is (
                LaunchAtLoginStatus.DisabledByUser
                or LaunchAtLoginStatus.DisabledByPolicy
                or LaunchAtLoginStatus.Failed);
        OpenLaunchAtLoginSettingsButton.Visibility = shouldShowSystemSettings
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OpenLaunchAtLoginSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_runtime?.OpenLaunchAtLoginSystemSettings() != true)
        {
            LaunchAtLoginStatusText.Text = "无法打开 Windows 启动应用设置。";
        }
    }

    private void PreviewClick(object sender, RoutedEventArgs e)
    {
        if (_runtime?.Settings.IsEnabled == true)
        {
            _ = _runtime.PreviewCurrentKeyboardSound();
        }
    }

    private async void KeyboardProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_runtime is null
            || KeyboardProfileCombo.SelectedValue is not string selectionId)
        {
            return;
        }

        if (_applyingRuntimeState && !IsInteractivePopupOpen())
        {
            return;
        }

        _soundPackSelectionInProgress = true;
        KeyboardProfileCombo.IsEnabled = false;
        KeyboardSoundErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var result = await _runtime.ActivateSoundPackAsync(selectionId);
            if (!result.WasSuperseded && !string.IsNullOrWhiteSpace(result.Error))
            {
                KeyboardSoundErrorText.Text = result.Error;
                KeyboardSoundErrorText.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            KeyboardSoundErrorText.Text = $"无法切换音色：{exception.Message}";
            KeyboardSoundErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            _soundPackSelectionInProgress = false;
            if (_runtime is not null)
            {
                KeyboardProfileCombo.IsEnabled = _runtime.Settings.IsEnabled;
            }
        }
    }

    private void PointerProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_runtime is null
            || PointerProfileCombo.SelectedValue is not string selectionId)
        {
            return;
        }

        if (_applyingRuntimeState && !IsInteractivePopupOpen())
        {
            return;
        }

        _runtime.UpdateSettings(settings => settings with
        {
            SelectedPointerProfileId = selectionId,
        });
    }

    private void RuntimeSettingsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyRuntimeState();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(ApplyRuntimeState);
        }
    }

    private void StatisticsSummaryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyStatisticsSummary();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(ApplyStatisticsSummary);
        }
    }

    private void SoundPackStateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplySoundPackState();
            StartSoundPackRefresh();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(
                () =>
                {
                    ApplySoundPackState();
                    StartSoundPackRefresh();
                });
        }
    }

    private void StartSoundPackRefresh()
    {
        if (_runtime is null || _soundPackRefreshCancellation is null)
        {
            return;
        }

        _soundPackRefreshRequested = true;
        if (!_soundPackRefreshOperation.IsCompleted)
        {
            return;
        }

        _soundPackRefreshOperation = RefreshSoundPacksAsync(
            _runtime,
            _soundPackRefreshCancellation.Token);
    }

    private async Task RefreshSoundPacksAsync(
        BattutaRuntime runtime,
        CancellationToken cancellationToken)
    {
        do
        {
            _soundPackRefreshRequested = false;
            try
            {
                var descriptors = await runtime.SoundPackLibrary
                    .DescriptorsAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                ApplySoundPackChoices(descriptors);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                KeyboardSoundErrorText.Text = $"无法读取 DIY 音色：{exception.Message}";
                KeyboardSoundErrorText.Visibility = Visibility.Visible;
                return;
            }
        }
        while (_soundPackRefreshRequested && !cancellationToken.IsCancellationRequested);
    }

    private void ApplySoundPackChoices(IEnumerable<SoundPackDescriptor> descriptors)
    {
        var materialized = descriptors.ToArray();
        if (IsInteractivePopupOpen())
        {
            _deferredSoundPackChoices = materialized;
            return;
        }

        CommitSoundPackChoices(materialized);
    }

    private void ApplyDeferredSoundPackChoicesAfterPopupClosed()
    {
        if (_deferredSoundPackChoices is null)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            () =>
            {
                if (Dispatcher.HasShutdownStarted
                    || IsInteractivePopupOpen()
                    || _deferredSoundPackChoices is not { } pending)
                {
                    return;
                }

                _deferredSoundPackChoices = null;
                CommitSoundPackChoices(pending);
            });
    }

    private void CommitSoundPackChoices(IEnumerable<SoundPackDescriptor> descriptors)
    {
        var choices = descriptors
            .Select(descriptor => new Choice(
                descriptor.SelectionId,
                $"{descriptor.Name} · {descriptor.Family}",
                descriptor.Family,
                descriptor.Tone))
            .ToArray();
        var selected = _runtime?.Settings.SelectedProfileId;
        _applyingRuntimeState = true;
        try
        {
            KeyboardProfileCombo.ItemsSource = null;
            KeyboardProfileCombo.Items.Clear();
            KeyboardProfileCombo.ItemsSource = choices;
            KeyboardProfileCombo.SelectedValue = selected;
        }
        finally
        {
            _applyingRuntimeState = false;
        }

        ApplySoundPackState();
    }

    private void ApplySoundPackState()
    {
        if (_runtime is null)
        {
            return;
        }

        var selectedId = _runtime.Settings.SelectedProfileId;
        var choice = (KeyboardProfileCombo.ItemsSource as IEnumerable<Choice>)?
            .FirstOrDefault(candidate => string.Equals(
                candidate.Id,
                selectedId,
                StringComparison.Ordinal));
        if (choice is not null)
        {
            KeyboardSectionHeading.Subtitle = choice.Tone;
            KeyboardFamilyText.Text = choice.Family;
        }

        var error = _runtime.SoundPackError;
        if (string.IsNullOrWhiteSpace(error))
        {
            KeyboardSoundErrorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            KeyboardSoundErrorText.Text = error;
            KeyboardSoundErrorText.Visibility = Visibility.Visible;
        }
    }

    private void ApplyRuntimeState()
    {
        if (_runtime is null)
        {
            return;
        }

        var settings = _runtime.Settings;
        _applyingRuntimeState = true;
        try
        {
            StatsToggle.IsChecked = settings.IsTypingStatsEnabled;
            KeyboardToggle.IsChecked = settings.IsEnabled;
            if (!KeyboardProfileCombo.IsDropDownOpen)
            {
                KeyboardProfileCombo.SelectedValue = settings.SelectedProfileId;
            }
            KeyboardProfileCombo.IsEnabled = settings.IsEnabled && !_soundPackSelectionInProgress;
            PreviewButton.IsEnabled = settings.IsEnabled;
            KeyboardVolumeSlider.IsEnabled = settings.IsEnabled;
            KeyboardReleaseToggle.IsEnabled = settings.IsEnabled;
            KeyboardRepeatToggle.IsEnabled = settings.IsEnabled;
            KeyboardVolumeSlider.Value = settings.Volume * 100d;
            KeyboardVolumeText.Text = $"{Math.Round(settings.Volume * 100d):0}%";
            KeyboardReleaseToggle.IsChecked = settings.PlaysReleaseSound;
            KeyboardRepeatToggle.IsChecked = settings.PlaysKeyRepeatSound;
            VariationToggle.IsChecked = settings.UsesPitchVariation;
            PointerToggle.IsChecked = settings.IsPointerSoundEnabled;
            if (!PointerProfileCombo.IsDropDownOpen)
            {
                PointerProfileCombo.SelectedValue = settings.SelectedPointerProfileId;
            }
            PointerProfileCombo.IsEnabled = settings.IsPointerSoundEnabled;
            PointerVolumeSlider.IsEnabled = settings.IsPointerSoundEnabled;
            PointerReleaseToggle.IsEnabled = settings.IsPointerSoundEnabled;
            PointerVolumeSlider.Value = settings.PointerVolume * 100d;
            PointerVolumeText.Text = $"{Math.Round(settings.PointerVolume * 100d):0}%";
            PointerReleaseToggle.IsChecked = settings.PlaysPointerReleaseSound;
            LaunchAtLoginToggle.IsChecked = settings.IsLaunchAtLoginEnabled;
            ApplyLaunchAtLoginState();
            AutomaticUpdateToggle.IsChecked = settings.AutomaticUpdateCheckPreference
                == AutomaticUpdateCheckPreference.Enabled;
            if (_runtime.Paths.IsPackaged)
            {
                UpdatePreferencePrompt.Visibility = Visibility.Collapsed;
                UpdatePreferenceTogglePanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                var updatePreferenceIsUndecided = settings.AutomaticUpdateCheckPreference
                    == AutomaticUpdateCheckPreference.Undecided;
                UpdatePreferencePrompt.Visibility = updatePreferenceIsUndecided
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                UpdatePreferenceTogglePanel.Visibility = updatePreferenceIsUndecided
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
            HeaderStatusText.Text = _runtime.StatusText;
            FooterStatusText.Text = _runtime.InputHooks.IsRunning
                ? "✓  输入监控正在运行"
                : "⚠  输入监控未启动";
            ApplySoundPackState();
            ApplyStatisticsSummary();
        }
        finally
        {
            _applyingRuntimeState = false;
        }
    }

    private void ApplyStatisticsSummary()
    {
        if (_runtime is null)
        {
            return;
        }

        var summary = _runtime.StatisticsSummaryViewModel;
        TodayCountText.Text = summary.TodayCharacterCount.ToString("N0", CultureInfo.CurrentCulture);
        TodayPeakText.Text = $"{summary.TodayPeakCps:N0}/秒";
        TopApplicationText.Text = $"▣  今日最多应用：{summary.TopApplicationName}";
    }

    private void RuntimeWindowClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_trayResourcesDisposed)
        {
            return;
        }

        _trayResourcesDisposed = true;
        if (_runtime is null)
        {
            DisposeUpdateServices();
            GC.SuppressFinalize(this);
            return;
        }

        _runtime.SettingsChanged -= RuntimeSettingsChanged;
        _runtime.SoundPackStateChanged -= SoundPackStateChanged;
        _runtime.LaunchAtLoginStateChanged -= LaunchAtLoginStateChanged;
        _runtime.StatisticsSummaryViewModel.PropertyChanged -= StatisticsSummaryChanged;
        _runtime = null;
        AutomaticUpdateToggle.Checked -= AutomaticUpdatePreferenceChanged;
        AutomaticUpdateToggle.Unchecked -= AutomaticUpdatePreferenceChanged;
        EnableAutomaticUpdatesButton.Click -= EnableAutomaticUpdatesClick;
        DisableAutomaticUpdatesButton.Click -= DisableAutomaticUpdatesClick;
        UpdateActionButton.Click -= UpdateActionClick;
        IsVisibleChanged -= TrayVisibilityChanged;
        KeyboardProfileCombo.SelectionChanged -= KeyboardProfileChanged;
        PointerProfileCombo.SelectionChanged -= PointerProfileChanged;
        OpenLaunchAtLoginSettingsButton.Click -= OpenLaunchAtLoginSettingsClick;
        _soundPackRefreshCancellation?.Cancel();
        _soundPackRefreshCancellation?.Dispose();
        _soundPackRefreshCancellation = null;
        _deferredSoundPackChoices = null;
        DisposeUpdateServices();
        GC.SuppressFinalize(this);
    }

    private void InitializeUpdateServices(BattutaRuntime runtime)
    {
        DisposeUpdateServices();
        _availableRelease = null;
        UpdateProgress.Visibility = Visibility.Collapsed;
        if (runtime.Paths.IsPackaged)
        {
            UpdatePreferencePrompt.Visibility = Visibility.Collapsed;
            UpdatePreferenceTogglePanel.Visibility = Visibility.Collapsed;
            UpdateActionButton.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = "此安装版由 Microsoft Store 或 Windows 应用安装程序管理更新。";
            return;
        }

        UpdateActionButton.Visibility = Visibility.Visible;
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _updateHttpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _updateCacheStore = new JsonUpdateCacheStore(runtime.Paths);
        _updateCheckService = new UpdateCheckService(
            new GitHubReleaseClient(_updateHttpClient),
            _updateCacheStore,
            InstalledSemanticVersion());
        _updateCancellation = new CancellationTokenSource();
        UpdateActionButton.IsEnabled = true;
        UpdateActionButton.Content = "检查更新";
        UpdateStatusText.Text = runtime.Settings.AutomaticUpdateCheckPreference
            == AutomaticUpdateCheckPreference.Undecided
                ? "尚未检查更新；开启自动检查或手动检查均可。"
                : "尚未检查更新";
    }

    private void TrayVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true
            && _runtime?.Settings.AutomaticUpdateCheckPreference
                == AutomaticUpdateCheckPreference.Enabled)
        {
            StartUpdateCheck(UpdateCheckTrigger.Automatic);
        }

        if (e.NewValue is true)
        {
            StartSoundPackRefresh();
            StartLaunchAtLoginRefresh();
        }
    }

    private void AutomaticUpdatePreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingRuntimeState || _runtime is null)
        {
            return;
        }

        var preference = AutomaticUpdateToggle.IsChecked == true
            ? AutomaticUpdateCheckPreference.Enabled
            : AutomaticUpdateCheckPreference.Disabled;
        _runtime.UpdateSettings(settings => settings with
        {
            AutomaticUpdateCheckPreference = preference,
        });
        if (preference == AutomaticUpdateCheckPreference.Enabled)
        {
            StartUpdateCheck(UpdateCheckTrigger.Automatic);
        }
        else if (_updateOperation.IsCompleted && _availableRelease is null)
        {
            UpdateStatusText.Text = "自动检查已关闭；仍可手动检查。";
        }
    }

    private void EnableAutomaticUpdatesClick(object sender, RoutedEventArgs e) =>
        SetAutomaticUpdatePreference(AutomaticUpdateCheckPreference.Enabled);

    private void DisableAutomaticUpdatesClick(object sender, RoutedEventArgs e) =>
        SetAutomaticUpdatePreference(AutomaticUpdateCheckPreference.Disabled);

    private void SetAutomaticUpdatePreference(AutomaticUpdateCheckPreference preference)
    {
        if (_runtime is null)
        {
            return;
        }

        _runtime.UpdateSettings(settings => settings with
        {
            AutomaticUpdateCheckPreference = preference,
        });
        if (preference == AutomaticUpdateCheckPreference.Enabled)
        {
            StartUpdateCheck(UpdateCheckTrigger.Automatic);
        }
        else
        {
            UpdateStatusText.Text = "自动检查已关闭；仍可手动检查。";
        }
    }

    private async void UpdateActionClick(object sender, RoutedEventArgs e)
    {
        if (_runtime?.Paths.IsPackaged == true || !_updateOperation.IsCompleted)
        {
            return;
        }

        if (_availableRelease is { } release)
        {
            UpdateActionButton.IsEnabled = false;
            try
            {
                var result = await new ManualDownloadUpdateInstaller()
                    .InstallAsync(release);
                UpdateStatusText.Text = result.Message;
            }
            catch (Exception exception)
            {
                UpdateStatusText.Text = $"无法打开下载页面：{exception.Message}";
            }
            finally
            {
                UpdateActionButton.IsEnabled = true;
            }

            return;
        }

        StartUpdateCheck(UpdateCheckTrigger.Manual);
    }

    private void StartUpdateCheck(UpdateCheckTrigger trigger)
    {
        if (_updateCheckService is null
            || _updateCancellation is null
            || !_updateOperation.IsCompleted)
        {
            return;
        }

        _updateOperation = RunUpdateCheckAsync(
            _updateCheckService,
            trigger,
            _updateCancellation.Token);
    }

    private async Task RunUpdateCheckAsync(
        UpdateCheckService service,
        UpdateCheckTrigger trigger,
        CancellationToken cancellationToken)
    {
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateActionButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新…";
        try
        {
            var outcome = await service.CheckAsync(trigger, cancellationToken);
            ApplyUpdateOutcome(outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _availableRelease = null;
            UpdateStatusText.Text = $"暂时无法检查更新：{exception.Message}";
            UpdateActionButton.Content = "重试";
        }
        finally
        {
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateActionButton.IsEnabled = true;
        }
    }

    private void ApplyUpdateOutcome(UpdateCheckOutcome outcome)
    {
        var report = outcome.Report;
        _availableRelease = report?.Comparison == UpdateComparison.UpdateAvailable
            ? report.Release
            : null;

        if (outcome.Failure is { } failure)
        {
            UpdateStatusText.Text = FailureDescription(failure);
        }
        else if (report is not null)
        {
            UpdateStatusText.Text = report.Comparison switch
            {
                UpdateComparison.UpdateAvailable => $"发现新版本 {report.Release.Version}",
                UpdateComparison.UpToDate => $"✓  已是最新版 {report.InstalledVersion}",
                UpdateComparison.InstalledVersionIsNewer =>
                    $"当前安装版本高于公开版本 {report.Release.Version}",
                _ => "更新状态未知",
            };
        }
        else if (outcome.WasSkipped)
        {
            UpdateStatusText.Text = "自动检查尚未到间隔时间。";
        }
        else
        {
            UpdateStatusText.Text = "尚未检查更新";
        }

        UpdateActionButton.Content = _availableRelease is null
            ? "检查更新"
            : "前往 GitHub 下载";
        AutomationProperties.SetName(
            UpdateActionButton,
            _availableRelease is null ? "检查 Battuta 更新" : "下载 Battuta 更新");
    }

    private static string FailureDescription(UpdateCheckFailure failure)
    {
        var retryText = failure.RetryAt is { } retryAt
            ? $" 可在 {retryAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)} 后重试。"
            : string.Empty;
        return failure.Kind switch
        {
            UpdateCheckFailureKind.Offline => "当前离线，稍后可重试。",
            UpdateCheckFailureKind.TimedOut => "连接 GitHub 超时，稍后可重试。",
            UpdateCheckFailureKind.RequestedTooSoon => "刚刚检查过更新。" + retryText,
            UpdateCheckFailureKind.RateLimited => "GitHub 暂时限制请求。" + retryText,
            UpdateCheckFailureKind.NoPublishedRelease => "GitHub 上暂时没有公开版本。",
            UpdateCheckFailureKind.ApiVersionRetired => "更新服务需要升级，请前往 GitHub 查看。",
            UpdateCheckFailureKind.InvalidResponse => "GitHub 返回的版本信息格式异常。",
            _ => "暂时无法连接 GitHub，稍后可重试。",
        };
    }

    private void DisposeUpdateServices()
    {
        var cancellation = _updateCancellation;
        var operation = _updateOperation;
        var service = _updateCheckService;
        var cache = _updateCacheStore;
        var client = _updateHttpClient;
        _updateCancellation = null;
        _updateCheckService = null;
        _updateCacheStore = null;
        _updateHttpClient = null;
        _updateOperation = Task.CompletedTask;
        cancellation?.Cancel();
        _ = DisposeUpdateServicesWhenIdleAsync(operation, cancellation, service, cache, client);
    }

    private static async Task DisposeUpdateServicesWhenIdleAsync(
        Task operation,
        CancellationTokenSource? cancellation,
        UpdateCheckService? service,
        JsonUpdateCacheStore? cache,
        HttpClient? client)
    {
        try
        {
            await operation;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation?.Dispose();
            service?.Dispose();
            cache?.Dispose();
            client?.Dispose();
        }
    }

    private static SemanticVersion InstalledSemanticVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version
            ?? typeof(TrayFlyoutWindow).Assembly.GetName().Version
            ?? new Version(0, 0, 0);
        return new SemanticVersion(
            Math.Max(0, version.Major),
            Math.Max(0, version.Minor),
            Math.Max(0, version.Build));
    }
}
