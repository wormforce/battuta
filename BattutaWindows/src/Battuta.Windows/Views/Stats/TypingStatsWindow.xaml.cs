using System.Windows;
using System.ComponentModel;
using System.Windows.Media;
using Battuta.Windows.Stats.Models;
using Battuta.Windows.Stats.ViewModels;

namespace Battuta.Windows.Views.Stats;

public partial class TypingStatsWindow : Window, IDisposable
{
    public const double SafeMinimumWidth = 1100;

    private bool _applyingState;
    private CancellationTokenSource? _refreshLoopCancellation;
    private bool _disposed;

    public TypingStatsWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void SectionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        TodayView.Visibility = TodaySegment.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HistoryView.Visibility = HistorySegment.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        KeyboardView.Visibility = KeyboardSegment.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (HistorySegment.IsChecked == true && DataContext is TypingStatsViewModel model)
        {
            _ = model.LoadAnnualReportAsync(DateOnly.FromDateTime(DateTime.Today));
        }
    }

    private async void ClearStatsClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "今日、历史、应用排行、全部逐键累计和鼠标点击统计都将从本机删除，且无法恢复。",
            "清除全部输入统计？", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result == MessageBoxResult.OK && DataContext is TypingStatsViewModel model)
        {
            await model.ClearAllAsync();
            ApplySnapshot(model);
        }
    }

    private async void RefreshStatsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TypingStatsViewModel model)
        {
            await model.RefreshAsync();
            if (HistorySegment.IsChecked == true)
            {
                await model.RefreshCurrentReportAsync();
            }
            ApplySnapshot(model);
        }
    }

    private void StatsRecordingChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingState || !IsInitialized || Application.Current is not App app)
        {
            return;
        }

        app.Runtime.UpdateSettings(settings => settings with
        {
            IsTypingStatsEnabled = StatsRecordingToggle.IsChecked == true,
        });
        if (StatsRecordingToggle.IsChecked == true && DataContext is TypingStatsViewModel model)
        {
            _ = model.RefreshAsync();
        }

        if (DataContext is TypingStatsViewModel currentModel)
        {
            ApplySnapshot(currentModel);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.Runtime.SettingsChanged -= RuntimeSettingsChanged;
            app.Runtime.SettingsChanged += RuntimeSettingsChanged;
        }

        ApplyRuntimeSettings();
        if (DataContext is TypingStatsViewModel model)
        {
            ApplySnapshot(model);
            StartRefreshLoop(model);
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is TypingStatsViewModel oldModel)
        {
            oldModel.PropertyChanged -= ModelPropertyChanged;
        }

        if (e.NewValue is TypingStatsViewModel newModel)
        {
            newModel.PropertyChanged += ModelPropertyChanged;
            ApplySnapshot(newModel);
            if (IsLoaded)
            {
                StartRefreshLoop(newModel);
            }
        }
    }

    private void ModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            if (sender is TypingStatsViewModel model) ApplySnapshot(model);
        }
        else if (sender is TypingStatsViewModel model)
        {
            _ = Dispatcher.BeginInvoke(() => ApplySnapshot(model));
        }
    }

    private void ApplyRuntimeSettings()
    {
        if (Application.Current is not App app)
        {
            return;
        }

        _applyingState = true;
        try
        {
            StatsRecordingToggle.IsChecked = app.Runtime.Settings.IsTypingStatsEnabled;
        }
        finally
        {
            _applyingState = false;
        }
    }

    private void ApplySnapshot(TypingStatsViewModel model)
    {
        TodayView.ApplySnapshot(model.Snapshot);
        KeyboardView.ApplySnapshot(model.Snapshot);
        HistoryView.ApplyReport(
            model.ReportSnapshot,
            model.IsLoadingReport,
            model.ReportErrorMessage);
        var snapshot = model.Snapshot;
        var dataDate = snapshot?.Today.LastUpdatedAt ?? snapshot?.LastInputAt;
        DataCutoffText.Text = dataDate is { } lastInput
            ? $"◷  数据截至 {lastInput.ToLocalTime():HH:mm:ss}"
            : "◷  数据截至 --:--:--";
        ReadAtText.Text = snapshot is null
            ? "读取于 --:--:--"
            : $"读取于 {snapshot.GeneratedAt.ToLocalTime():HH:mm:ss}";

        var stale = model.StaleDataMessage;
        StaleWarning.Visibility = string.IsNullOrWhiteSpace(stale)
            ? Visibility.Collapsed
            : Visibility.Visible;
        StaleWarningText.Text = string.IsNullOrWhiteSpace(stale)
            ? string.Empty
            : $"刷新失败，正在显示上次成功的数据：{stale}";
        SourceStatePanel.Visibility = snapshot is null ? Visibility.Visible : Visibility.Collapsed;
        SourceProgress.Visibility = model.SourceState == TypingStatsSourceState.Failed
            ? Visibility.Collapsed
            : Visibility.Visible;
        SourceStateTitle.Text = model.SourceState == TypingStatsSourceState.Failed
            ? "暂时无法读取统计"
            : "正在读取统计";
        SourceStateMessage.Text = model.SourceState == TypingStatsSourceState.Failed
            ? model.SourceErrorMessage ?? "请稍后重试。"
            : "正在加载 Battuta 的本地输入统计。";
        RefreshButton.IsEnabled = !model.IsRefreshing && !model.IsLoadingReport;
        ApplyStatus(model);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshLoopCancellation?.Cancel();
        _refreshLoopCancellation?.Dispose();
        _refreshLoopCancellation = null;
        if (Application.Current is App app)
        {
            app.Runtime.SettingsChanged -= RuntimeSettingsChanged;
        }

        if (DataContext is TypingStatsViewModel model)
        {
            model.PropertyChanged -= ModelPropertyChanged;
        }

        GC.SuppressFinalize(this);
    }

    private void RuntimeSettingsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => RuntimeSettingsChanged(sender, e));
            return;
        }

        ApplyRuntimeSettings();
        if (DataContext is TypingStatsViewModel model)
        {
            ApplySnapshot(model);
        }
    }

    private void ApplyStatus(TypingStatsViewModel model)
    {
        var enabled = Application.Current is App app && app.Runtime.Settings.IsTypingStatsEnabled;
        if (!string.IsNullOrWhiteSpace(model.StaleDataMessage))
        {
            StatisticsStatus.Text = "显示上次数据";
            StatisticsStatus.Glyph = "◷";
            SetStatusColor(Color.FromRgb(242, 171, 51));
        }
        else if (!enabled)
        {
            StatisticsStatus.Text = "统计已暂停";
            StatisticsStatus.Glyph = "Ⅱ";
            SetStatusColor(Color.FromRgb(150, 158, 151));
        }
        else
        {
            StatisticsStatus.Text = "本地统计";
            StatisticsStatus.Glyph = "\uE9D2";
            SetStatusColor(Color.FromRgb(145, 201, 43));
        }
    }

    private void SetStatusColor(Color color)
    {
        StatisticsStatus.TextBrush = new SolidColorBrush(color);
        StatisticsStatus.PillBrush = new SolidColorBrush(Color.FromArgb(28, color.R, color.G, color.B));
        StatisticsStatus.PillBorderBrush = new SolidColorBrush(Color.FromArgb(41, color.R, color.G, color.B));
    }

    private void StartRefreshLoop(TypingStatsViewModel model)
    {
        _refreshLoopCancellation?.Cancel();
        _refreshLoopCancellation?.Dispose();
        _refreshLoopCancellation = new CancellationTokenSource();
        _ = RunRefreshLoopAsync(model, _refreshLoopCancellation.Token);
    }

    private static async Task RunRefreshLoopAsync(
        TypingStatsViewModel model,
        CancellationToken cancellationToken)
    {
        try
        {
            await model.RunVisibleRefreshLoopAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Window closed or its data context changed.
        }
    }
}
