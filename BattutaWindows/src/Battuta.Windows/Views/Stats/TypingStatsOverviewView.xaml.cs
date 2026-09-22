using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Battuta.Windows.Stats.Models;
using Battuta.Windows.Stats.ViewModels;

namespace Battuta.Windows.Views.Stats;

public partial class TypingStatsOverviewView : UserControl
{
    private bool _applyingRange;

    public TypingStatsOverviewView() => InitializeComponent();

    public void ApplySnapshot(TypingStatsSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            TodayCharactersText.Text = "0";
            PeakCpsText.Text = "0";
            TopApplicationNameText.Text = "暂无";
            TopApplicationCountText.Text = "0 个字符";
            LastInputText.Text = "还没有输入记录";
            ActiveTimeText.Text = "0 秒";
            ActiveMinutesText.Text = "0 个输入分钟";
            TodayMouseClicksText.Text = "0";
            AllTimeMouseClicksText.Text = "0";
            TrendTotalText.Text = "0";
            TrendPeakText.Text = "0";
            ApplicationCountText.Text = "0 个应用";
            Trend.Buckets = null;
            Timeline.Timelines = null;
            TrendEmptyState.Visibility = Visibility.Visible;
            TimelineEmptyState.Visibility = Visibility.Visible;
            Timeline.Visibility = Visibility.Collapsed;
            return;
        }

        var today = snapshot.Today;
        TodayCharactersText.Text = today.CharacterCount.ToString("N0", CultureInfo.CurrentCulture);
        PeakCpsText.Text = today.PeakCps.ToString("N0", CultureInfo.CurrentCulture);
        TopApplicationNameText.Text = today.TopAppName ?? "暂无";
        var topApplication = snapshot.Apps.FirstOrDefault(app =>
            string.Equals(app.DisplayName, today.TopAppName, StringComparison.Ordinal));
        TopApplicationCountText.Text = $"{topApplication?.CharacterCount ?? 0:N0} 个字符";
        LastInputText.Text = snapshot.LastInputAt is { } lastInput
            ? $"最近输入 {lastInput.ToLocalTime():HH:mm}"
            : "最近输入 --:--";
        ActiveTimeText.Text = FormatDuration(today.ActiveSeconds);
        ActiveMinutesText.Text = $"{today.ActiveMinuteBuckets:N0} 个输入分钟";
        TodayMouseClicksText.Text = snapshot.TodayMouseClicks.ToString("N0", CultureInfo.CurrentCulture);
        AllTimeMouseClicksText.Text = snapshot.AllTimeMouseClicks.ToString("N0", CultureInfo.CurrentCulture);

        var values = snapshot.RecentBuckets.Select(bucket => bucket.CharacterCount).ToArray();
        Trend.Buckets = snapshot.RecentBuckets;
        Trend.Range = snapshot.TimelineRange;
        TrendTotalText.Text = values.Sum().ToString("N0", CultureInfo.CurrentCulture);
        TrendPeakText.Text = values.DefaultIfEmpty().Max().ToString("N0", CultureInfo.CurrentCulture);
        ApplicationCountText.Text = $"{snapshot.RecentAppTimelines.Count:N0} 个应用";
        Timeline.Timelines = snapshot.RecentAppTimelines;
        Timeline.Range = snapshot.TimelineRange;
        var definition = snapshot.TimelineRange.GetDefinition();
        var subtitle = $"{definition.DisplayTitle} · {definition.BucketDescription}";
        TrendHeading.Subtitle = subtitle;
        TimelineHeading.Subtitle = subtitle;
        TrendEmptyState.Visibility = values.Sum() == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        TimelineEmptyState.Text = $"{definition.DisplayTitle}还没有应用输入。";
        TimelineEmptyState.Visibility = snapshot.RecentAppTimelines.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        Timeline.Visibility = snapshot.RecentAppTimelines.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        ApplyRangeSelection(snapshot.TimelineRange);
    }

    private async void TimelineRangeChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingRange || !IsInitialized || DataContext is not TypingStatsViewModel model)
        {
            return;
        }

        var range = SevenDaysRange.IsChecked == true ? TypingTimelineRange.SevenDays
            : TwentyFourHoursRange.IsChecked == true ? TypingTimelineRange.TwentyFourHours
            : SixHoursRange.IsChecked == true ? TypingTimelineRange.SixHours
            : TypingTimelineRange.OneHour;
        await model.SelectTimelineRangeAsync(range);
    }

    private void OverviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var contentWidth = Math.Min(1080, Math.Max(0, e.NewSize.Width - 40));
        TopLayout.Height = Math.Max(320, (contentWidth - 16) * .42);
    }

    private static string FormatDuration(long seconds)
    {
        seconds = Math.Max(0, seconds);
        if (seconds >= 3_600)
        {
            return $"{seconds / 3_600:N0} 小时 {(seconds % 3_600) / 60:N0} 分";
        }

        var minutes = seconds / 60;
        var remainder = seconds % 60;
        return minutes > 0 ? $"{minutes:N0} 分 {remainder:N0} 秒" : $"{remainder:N0} 秒";
    }

    private void ApplyRangeSelection(TypingTimelineRange range)
    {
        _applyingRange = true;
        try
        {
            SevenDaysRange.IsChecked = range == TypingTimelineRange.SevenDays;
            TwentyFourHoursRange.IsChecked = range == TypingTimelineRange.TwentyFourHours;
            SixHoursRange.IsChecked = range == TypingTimelineRange.SixHours;
            OneHourRange.IsChecked = range == TypingTimelineRange.OneHour;
        }
        finally
        {
            _applyingRange = false;
        }
    }
}
