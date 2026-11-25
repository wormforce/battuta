using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Battuta.Core.Input;
using Battuta.Windows.Stats.Models;
using Battuta.Windows.Stats.ViewModels;
using Battuta.Windows.Stats.Visualization;
using Battuta.Linux.Runtime;

namespace Battuta.Linux.ViewModels.Stats;

/// <summary>
/// The Linux statistics window state (plan §7.3 / PR 8): overview, live
/// timeline, application distribution, keyboard heatmap, recent history, and
/// the data-source status line required by plan §8.3 (which foreground
/// adapter produced application identities). Holds no Avalonia types.
/// </summary>
public sealed partial class StatsViewModel : ObservableObject, IDisposable
{
    private readonly LinuxBattutaRuntime runtime;
    private readonly TypingStatsViewModel engine;
    private readonly SynchronizationContext? uiContext;
    private CancellationTokenSource? refreshLoopCts;

    [ObservableProperty]
    private bool isAllTimeHeatmapScope;

    [ObservableProperty]
    private bool isConfirmingClear;

    public StatsViewModel(LinuxBattutaRuntime runtime)
    {
        this.runtime = runtime;
        uiContext = SynchronizationContext.Current;

        engine = new TypingStatsViewModel(runtime.StatisticsRecorder);
        engine.PropertyChanged += EnginePropertyChanged;

        Heatmap = new KeyboardHeatmapViewModel(WindowsAnsiVisualLayoutCatalog.CompactAnsi);

        ExtendedHeatmap = new KeyboardHeatmapViewModel(new KeyboardVisualLayout(
            "extended", 0, 0,
            WindowsAnsiVisualLayoutCatalog.ExtendedKeys
                .Select(key => (key.Id, key.Label))
                .Concat(PhysicalKeyCatalog.All
                    .Where(key => key.Membership == KeyboardLayoutMembership.InputMethod)
                    .Select(key => (key.Id, key.Label)))
                .Select(key => new KeyboardVisualPlacement(
                    key.Id.Value, key.Id, key.Label, null, 0, 0, 1, KeyboardVisualVerticalSlot.Full))
                .ToArray()));

        RangeOptions = Enum.GetValues<TypingTimelineRange>();
        ToggleHeatmapScopeCommand = new RelayCommand(ToggleHeatmapScope);
        StartClearCommand = new RelayCommand(() => IsConfirmingClear = true);
        CancelClearCommand = new RelayCommand(() => IsConfirmingClear = false);
        ConfirmClearCommand = new AsyncRelayCommand(ConfirmClearAsync);

        runtime.ForegroundAppsStatusChanged += ForegroundStatusChanged;
        UpdateHeatmap();
        UpdateSourceStatus();
    }

    public IAsyncRelayCommand RefreshCommand => engine.RefreshCommand;

    public IRelayCommand ToggleHeatmapScopeCommand { get; }

    public IRelayCommand StartClearCommand { get; }

    public IRelayCommand CancelClearCommand { get; }

    public IAsyncRelayCommand ConfirmClearCommand { get; }

    public IReadOnlyList<TypingTimelineRange> RangeOptions { get; }

    public KeyboardHeatmapViewModel Heatmap { get; }

    internal KeyboardHeatmapViewModel ExtendedHeatmap { get; }

    public ObservableCollection<TimelineBarViewModel> Bars { get; } = [];

    public ObservableCollection<AppUsageRowViewModel> Apps { get; } = [];

    public ObservableCollection<HistoryRowViewModel> History { get; } = [];

    /// <summary>Two-way bound to the range selector; setting it triggers a
    /// reload through the engine model.</summary>
    public TypingTimelineRange SelectedRange
    {
        get => engine.TimelineRange;
        set
        {
            if (value != engine.TimelineRange)
            {
                _ = engine.SelectTimelineRangeAsync(value);
            }
        }
    }

    internal Task SetRangeAsync(TypingTimelineRange range) => SelectRangeAsync(range);

    public string RangeTitle => engine.TimelineRange.GetDefinition().DisplayTitle;

    public string RangeBucketDescription => engine.TimelineRange.GetDefinition().BucketDescription;

    public long TodayCharacterCount => engine.Snapshot?.Today.CharacterCount ?? 0;

    public long TodayPeakCps => engine.Snapshot?.Today.PeakCps ?? 0;

    public long TodayActiveMinutes => engine.Snapshot is { } snapshot
        ? Math.Max(snapshot.Today.ActiveSeconds, 0) / 60
        : 0;

    public long TodayPhysicalPresses => engine.Snapshot?.TodayPhysicalPresses ?? 0;

    public string TodayTopApplication => engine.Snapshot?.Today.TopAppName ?? "暂无";

    public bool HasSnapshot => engine.Snapshot is not null;

    public bool IsRecordingSuspended => engine.IsRecordingSuspended;

    public string? StatusMessage => engine.StaleDataMessage ?? engine.SourceErrorMessage;

    /// <summary>Plan §8.3: the UI must show which adapter produced the
    /// application identities (or that none did).</summary>
    public string DataSourceText { get; private set; } = string.Empty;

    public string TimelineMaxText => Bars.Count == 0
        ? "—"
        : Bars.Max(bar => bar.Count).ToString();

    public async Task StartAsync()
    {
        await engine.RefreshAsync().ConfigureAwait(true);
        refreshLoopCts = new CancellationTokenSource();
        _ = engine.RunVisibleRefreshLoopAsync(refreshLoopCts.Token);
    }

    public void Dispose()
    {
        refreshLoopCts?.Cancel();
        refreshLoopCts?.Dispose();
        refreshLoopCts = null;
        runtime.ForegroundAppsStatusChanged -= ForegroundStatusChanged;
        engine.PropertyChanged -= EnginePropertyChanged;
        engine.Dispose();
    }

    private Task SelectRangeAsync(TypingTimelineRange? range)
    {
        if (range is { } value && value != engine.TimelineRange)
        {
            return engine.SelectTimelineRangeAsync(value);
        }

        return Task.CompletedTask;
    }

    private void ToggleHeatmapScope()
    {
        IsAllTimeHeatmapScope = !IsAllTimeHeatmapScope;
        UpdateHeatmap();
        OnPropertyChanged(nameof(HeatmapScopeText));
    }

    public string HeatmapScopeText => IsAllTimeHeatmapScope ? "累计" : "今日";

    private async Task ConfirmClearAsync()
    {
        await engine.ClearAllAsync().ConfigureAwait(true);
        IsConfirmingClear = false;
    }

    private void EnginePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs) =>
        Post(RefreshFromSnapshot);

    private void ForegroundStatusChanged(object? sender, EventArgs eventArgs) =>
        Post(UpdateSourceStatus);

    private void RefreshFromSnapshot()
    {
        var snapshot = engine.Snapshot;
        if (snapshot is null)
        {
            return;
        }

        RebuildBars(snapshot);
        RebuildApps(snapshot);
        RebuildHistory(snapshot);
        UpdateHeatmap();

        OnPropertyChanged(nameof(TodayCharacterCount));
        OnPropertyChanged(nameof(TodayPeakCps));
        OnPropertyChanged(nameof(TodayActiveMinutes));
        OnPropertyChanged(nameof(TodayPhysicalPresses));
        OnPropertyChanged(nameof(TodayTopApplication));
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(IsRecordingSuspended));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(SelectedRange));
        OnPropertyChanged(nameof(RangeTitle));
        OnPropertyChanged(nameof(RangeBucketDescription));
        OnPropertyChanged(nameof(TimelineMaxText));
        UpdateSourceStatus();
    }

    private void RebuildBars(TypingStatsSnapshot snapshot)
    {
        var max = snapshot.RecentBuckets.Count == 0
            ? 0
            : snapshot.RecentBuckets.Max(bucket => bucket.CharacterCount);
        Bars.Clear();
        foreach (var bucket in snapshot.RecentBuckets)
        {
            Bars.Add(new TimelineBarViewModel(
                bucket.Index,
                bucket.Start.ToLocalTime().ToString("HH:mm"),
                bucket.CharacterCount,
                max > 0 ? bucket.CharacterCount / (double)max : 0d));
        }
    }

    private void RebuildApps(TypingStatsSnapshot snapshot)
    {
        var total = snapshot.Apps.Sum(app => app.CharacterCount);
        Apps.Clear();
        foreach (var app in snapshot.Apps
                     .OrderByDescending(app => app.CharacterCount)
                     .Take(6))
        {
            Apps.Add(new AppUsageRowViewModel(
                app.DisplayName,
                app.CharacterCount,
                total > 0 ? app.CharacterCount / (double)total : 0d));
        }
    }

    private void RebuildHistory(TypingStatsSnapshot snapshot)
    {
        History.Clear();
        foreach (var day in snapshot.History
                     .OrderByDescending(day => day.Date)
                     .Take(14))
        {
            History.Add(new HistoryRowViewModel(
                day.DateKey,
                day.CharacterCount,
                day.PeakCps,
                day.TopAppName));
        }
    }

    private void UpdateHeatmap()
    {
        var snapshot = engine.Snapshot;
        var counts = snapshot is null
            ? EmptyCounts
            : IsAllTimeHeatmapScope
                ? snapshot.AllTimeKeyCounts
                : snapshot.TodayKeyCounts;
        Heatmap.UpdateCounts(counts);
        ExtendedHeatmap.UpdateCounts(counts);
    }

    private void UpdateSourceStatus()
    {
        var monitor = runtime.ForegroundAppMonitor;
        var adapter = monitor is not null
            ? $"应用识别:{monitor.Status}"
            : runtime.ForegroundApps is not null
                ? "应用识别:测试注入"
                : "应用识别:已禁用,应用维度记录为未知";

        var recording = runtime.Settings.IsTypingStatsEnabled
            ? "统计已开启"
            : "统计已暂停";
        var failure = engine.IsRecordingSuspended || engine.StaleDataMessage is not null
            ? $" · 写入异常:{engine.StaleDataMessage}"
            : engine.SourceErrorMessage is not null
                ? $" · 读取异常:{engine.SourceErrorMessage}"
                : string.Empty;

        DataSourceText = $"{adapter} · {recording}{failure}";
        OnPropertyChanged(nameof(DataSourceText));
    }

    private void Post(Action action)
    {
        if (uiContext is not null && SynchronizationContext.Current != uiContext)
        {
            uiContext.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }

    private static readonly IReadOnlyDictionary<PhysicalKeyId, long> EmptyCounts =
        new Dictionary<PhysicalKeyId, long>();
}

public sealed record TimelineBarViewModel(
    int Index,
    string Label,
    long Count,
    double HeightFraction)
{
    /// <summary>Pixel bar height; keeps a visible stub for zero buckets.</summary>
    public double BarHeight => 2 + 46 * HeightFraction;

    public string CountText => Count.ToString();
}

public sealed record AppUsageRowViewModel(
    string DisplayName,
    long CharacterCount,
    double Share)
{
    public string ShareText => Share.ToString("P0");

    public string CountText => CharacterCount.ToString();
}

public sealed record HistoryRowViewModel(
    string DateText,
    long CharacterCount,
    long PeakCps,
    string? TopApplicationName)
{
    public string CountText => CharacterCount.ToString();

    public string TopApplicationText =>
        string.IsNullOrWhiteSpace(TopApplicationName) ? "—" : TopApplicationName;
}
