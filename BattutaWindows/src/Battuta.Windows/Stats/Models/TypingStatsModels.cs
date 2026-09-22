using Battuta.Core.Input;

namespace Battuta.Windows.Stats.Models;

public sealed record TypingApplicationIdentity(
    string ProcessKey,
    string DisplayName,
    string ProcessName,
    string? ApplicationUserModelId = null)
{
    public static TypingApplicationIdentity Unknown { get; } = new(
        "unknown",
        "未知应用",
        "unknown");
}

public sealed record TypingDaySummary(
    DateOnly Date,
    long CharacterCount,
    long PeakCps,
    long ActiveMinuteBuckets,
    long ActiveSeconds,
    string? TopAppName,
    DateTimeOffset? LastUpdatedAt)
{
    public string DateKey => Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct TypingDateRange
{
    public TypingDateRange(DateOnly startDate, DateOnly endDate)
    {
        StartDate = startDate <= endDate ? startDate : endDate;
        EndDate = startDate <= endDate ? endDate : startDate;
    }

    public DateOnly StartDate { get; }

    public DateOnly EndDate { get; }

    public int DayCount => EndDate.DayNumber - StartDate.DayNumber + 1;
}

public static class TypingStatsReportRanges
{
    public static (TypingDateRange Current, TypingDateRange Comparison) Annual(DateOnly today)
    {
        var current = new TypingDateRange(today.AddDays(-364), today);
        var comparisonEnd = current.StartDate.AddDays(-1);
        var comparison = new TypingDateRange(comparisonEnd.AddDays(-364), comparisonEnd);
        return (current, comparison);
    }
}

public sealed record TypingWeekdayAggregate(
    int Weekday,
    long CharacterCount,
    int ActiveDayCount);

public sealed record TypingHourAggregate(
    int Hour,
    long CharacterCount,
    int ActiveDayCount,
    long PeakCps);

public sealed record TypingWeekdayHourAggregate(
    int Weekday,
    int Hour,
    long CharacterCount,
    long ComparisonCharacterCount)
{
    public int Id => (Weekday - 1) * 24 + Hour;
}

public sealed record TypingRangeMetrics(
    long CharacterCount,
    int CalendarDayCount,
    int ActiveDayCount,
    double DailyAverage,
    double ActiveDayAverage,
    long PeakCps,
    TypingDaySummary? BestDay,
    int LongestActiveDayStreak,
    TypingWeekdayAggregate? BusiestWeekday,
    TypingHourAggregate? BusiestHour);

public sealed record TypingRangeApplicationSummary(
    TypingApplicationIdentity Application,
    long CharacterCount,
    long ComparisonCharacterCount,
    int ActiveDayCount,
    int ComparisonActiveDayCount,
    double Share,
    double ComparisonShare,
    long CharacterChange,
    double? RelativeCharacterChange)
{
    public string Id => Application.ProcessKey;
}

public sealed record TypingReportDataCoverage(
    DateOnly? FirstRecordedDate,
    DateOnly? LastRecordedDate,
    int RequestedDayCount,
    int RecordedDayCount,
    bool IsRangeWithinAvailableDates);

public sealed record TypingRangeReportSnapshot(
    DateTimeOffset GeneratedAt,
    TypingDateRange Range,
    TypingDateRange? ComparisonRange,
    TypingRangeMetrics Metrics,
    TypingRangeMetrics? ComparisonMetrics,
    IReadOnlyList<TypingDaySummary> Days,
    IReadOnlyList<TypingWeekdayAggregate> WeekdayDistribution,
    IReadOnlyList<TypingHourAggregate> HourlyDistribution,
    IReadOnlyList<TypingWeekdayHourAggregate> WeekdayHourDistribution,
    IReadOnlyList<TypingRangeApplicationSummary> Applications,
    TypingReportDataCoverage Coverage);

public enum TypingTimelineRange
{
    SevenDays,
    TwentyFourHours,
    SixHours,
    OneHour,
}

public readonly record struct TypingTimelineRangeDefinition(
    string Id,
    long BucketSeconds,
    int BucketCount,
    string DisplayTitle,
    string BucketDescription,
    TimeSpan RefreshInterval)
{
    public long DurationSeconds => BucketSeconds * BucketCount;
}

public static class TypingTimelineRanges
{
    public static TypingTimelineRangeDefinition GetDefinition(this TypingTimelineRange range) => range switch
    {
        TypingTimelineRange.SevenDays => new("7d", 7_200, 84, "最近 7 天", "每格 2 小时", TimeSpan.FromSeconds(60)),
        TypingTimelineRange.TwentyFourHours => new("24h", 900, 96, "最近 24 小时", "每格 15 分钟", TimeSpan.FromSeconds(30)),
        TypingTimelineRange.SixHours => new("6h", 300, 72, "最近 6 小时", "每格 5 分钟", TimeSpan.FromSeconds(15)),
        TypingTimelineRange.OneHour => new("1h", 60, 60, "最近 1 小时", "每格 1 分钟", TimeSpan.FromSeconds(5)),
        _ => throw new ArgumentOutOfRangeException(nameof(range)),
    };
}

public sealed record TypingBucket(int Index, DateTimeOffset Start, long CharacterCount);

public sealed record TypingAppSummary(
    string ProcessKey,
    string DisplayName,
    string ProcessName,
    string? ApplicationUserModelId,
    long CharacterCount,
    long ActiveMinuteBuckets,
    long ActiveSeconds,
    long PeakCps);

public sealed record TypingAppTimeline(
    TypingApplicationIdentity Application,
    IReadOnlyList<TypingBucket> Buckets)
{
    public string Id => Application.ProcessKey;

    public long RangeCharacterCount => Buckets.Sum(bucket => bucket.CharacterCount);

    public long PeakBucketCount => Buckets.Count == 0 ? 0 : Buckets.Max(bucket => bucket.CharacterCount);
}

public sealed record TypingCharacterAggregate(
    long SecondStartUtc,
    DateOnly LocalDate,
    int LocalHour,
    TypingApplicationIdentity Application,
    long Count);

public sealed record TypingKeyAggregate(
    DateOnly LocalDate,
    PhysicalKeyId PhysicalKeyId,
    long Count);

public sealed record MouseClickAggregate(DateOnly LocalDate, long Count);

public sealed record TypingStatsWriteBatch(
    IReadOnlyList<TypingCharacterAggregate> CharacterAggregates,
    IReadOnlyList<TypingKeyAggregate> KeyAggregates)
{
    public IReadOnlyList<MouseClickAggregate> MouseAggregates { get; init; } = [];

    public static TypingStatsWriteBatch Empty { get; } = new([], []);

    public bool IsEmpty => CharacterAggregates.Count == 0
        && KeyAggregates.Count == 0
        && MouseAggregates.Count == 0;
}

public sealed record TypingStatsSnapshot(
    DateTimeOffset GeneratedAt,
    DateTimeOffset? LastInputAt,
    TypingDaySummary Today,
    TypingTimelineRange TimelineRange,
    IReadOnlyList<TypingBucket> RecentBuckets,
    IReadOnlyList<TypingAppSummary> Apps,
    IReadOnlyList<TypingAppTimeline> RecentAppTimelines,
    IReadOnlyList<TypingDaySummary> History,
    IReadOnlyDictionary<PhysicalKeyId, long> TodayKeyCounts,
    IReadOnlyDictionary<PhysicalKeyId, long> AllTimeKeyCounts)
{
    public long TodayMouseClicks { get; init; }

    public long AllTimeMouseClicks { get; init; }

    public long FourteenDayTotal => History.Sum(day => day.CharacterCount);

    public long FourteenDayAverage => History.Count == 0 ? 0 : FourteenDayTotal / History.Count;

    public TypingDaySummary? BestDay => History
        .Where(day => day.CharacterCount > 0)
        .OrderByDescending(day => day.CharacterCount)
        .ThenBy(day => day.Date)
        .FirstOrDefault();

    public int ActiveDayCount => History.Count(day => day.CharacterCount > 0);

    public long TodayPhysicalPresses => TodayKeyCounts.Values.Sum();

    public long AllTimePhysicalPresses => AllTimeKeyCounts.Values.Sum();
}

public enum TypingStatsSourceState
{
    Checking,
    Available,
    Failed,
}
