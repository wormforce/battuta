using Battuta.Core.Input;
using Battuta.Windows.Stats.Models;
using Battuta.Windows.Stats.Persistence;
using Microsoft.Data.Sqlite;

namespace Battuta.Windows.Tests.Stats;

public sealed class TypingStatsSqliteStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TypingApplicationIdentity AppOne = new(
        "win32:one",
        "Example One",
        "ExampleOne.exe");
    private static readonly TypingApplicationIdentity AppTwo = new(
        "win32:two",
        "Example Two",
        "ExampleTwo.exe");

    [Fact]
    public async Task CreatesNativeSchemaAndLoadsEquivalentSnapshot()
    {
        var fixture = TemporaryDatabase.Create();
        try
        {
            await using var store = fixture.CreateStore(Now);
            var today = DateOnly.FromDateTime(Now.UtcDateTime);
            var yesterday = today.AddDays(-1);
            var nowEpoch = Now.ToUnixTimeSeconds();
            var unknownKey = new PhysicalKeyId("win.scan.e0.005E");

            await store.RecordAsync(new TypingStatsWriteBatch(
                [
                    new(nowEpoch, today, 12, AppOne, 3),
                    new(nowEpoch, today, 12, AppTwo, 2),
                    new(nowEpoch - 10, today, 11, AppOne, 4),
                    new(nowEpoch - 86_400, yesterday, 12, AppTwo, 12),
                ],
                [
                    new(today, PhysicalKeys.KeyA, 5),
                    new(today, PhysicalKeys.LeftShift, 2),
                    new(today, unknownKey, 1),
                    new(yesterday, PhysicalKeys.KeyA, 3),
                ]));

            var snapshot = await store.LoadSnapshotAsync();

            Assert.True(File.Exists(fixture.Path));
            Assert.Equal(TypingStatsSqliteStore.ApplicationId, await ReadPragmaAsync(fixture.Path, "application_id"));
            Assert.Equal(TypingStatsSqliteStore.SchemaVersion, await ReadPragmaAsync(fixture.Path, "user_version"));
            Assert.Equal(9, snapshot.Today.CharacterCount);
            Assert.Equal(5, snapshot.Today.PeakCps);
            Assert.Equal("Example One", snapshot.Today.TopAppName);
            Assert.Equal(2, snapshot.Apps.Count);
            Assert.Equal(7, snapshot.Apps[0].CharacterCount);
            Assert.Equal(60, snapshot.RecentBuckets.Count);
            Assert.Equal(9, snapshot.RecentBuckets.Sum(bucket => bucket.CharacterCount));
            Assert.Equal(2, snapshot.RecentAppTimelines.Count);
            Assert.Equal(14, snapshot.History.Count);
            Assert.Equal(21, snapshot.FourteenDayTotal);
            Assert.Equal(5, snapshot.TodayKeyCounts[PhysicalKeys.KeyA]);
            Assert.Equal(8, snapshot.AllTimeKeyCounts[PhysicalKeys.KeyA]);
            Assert.Equal(1, snapshot.TodayKeyCounts[unknownKey]);
            Assert.Equal(11, snapshot.AllTimePhysicalPresses);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task BuildsInclusiveCurrentAndComparisonReports()
    {
        var fixture = TemporaryDatabase.Create();
        try
        {
            await using var store = fixture.CreateStore(Now);
            var today = DateOnly.FromDateTime(Now.UtcDateTime);
            var yesterday = today.AddDays(-1);
            var epoch = Now.ToUnixTimeSeconds();
            await store.RecordAsync(new TypingStatsWriteBatch(
                [
                    new(epoch, today, 12, AppOne, 7),
                    new(epoch, today, 12, AppTwo, 2),
                    new(epoch - 86_400, yesterday, 12, AppTwo, 12),
                ],
                []));

            var report = await store.LoadReportAsync(new TypingDateRange(yesterday, today));
            Assert.Equal(2, report.Days.Count);
            Assert.Equal(21, report.Metrics.CharacterCount);
            Assert.Equal(10.5, report.Metrics.DailyAverage);
            Assert.Equal(2, report.Metrics.LongestActiveDayStreak);
            Assert.Equal(7, report.WeekdayDistribution.Count);
            Assert.Equal(24, report.HourlyDistribution.Count);
            Assert.Equal(168, report.WeekdayHourDistribution.Count);
            Assert.Equal(21, report.WeekdayHourDistribution.Sum(item => item.CharacterCount));
            Assert.True(report.Coverage.IsRangeWithinAvailableDates);

            var comparison = await store.LoadReportAsync(
                new TypingDateRange(today, today),
                new TypingDateRange(yesterday, yesterday));
            Assert.Equal(9, comparison.Metrics.CharacterCount);
            Assert.Equal(12, comparison.ComparisonMetrics?.CharacterCount);
            var appOne = Assert.Single(comparison.Applications, item => item.Application == AppOne);
            Assert.Equal(7, appOne.CharacterCount);
            Assert.Equal(0, appOne.ComparisonCharacterCount);
            Assert.Null(appOne.RelativeCharacterChange);
            var appTwo = Assert.Single(comparison.Applications, item => item.Application == AppTwo);
            Assert.Equal(-10, appTwo.CharacterChange);
            Assert.Equal(12, comparison.WeekdayHourDistribution.Sum(item => item.ComparisonCharacterCount));

            var threeDay = await store.LoadReportAsync(
                new TypingDateRange(today.AddDays(-2), today));
            Assert.Equal(0, threeDay.Days[0].CharacterCount);
            Assert.Equal(7, threeDay.Metrics.DailyAverage);
            Assert.False(threeDay.Coverage.IsRangeWithinAvailableDates);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task RetainsPermanentAggregatesWhenSecondDetailExpires()
    {
        var fixture = TemporaryDatabase.Create();
        try
        {
            await using var store = fixture.CreateStore(Now);
            var today = DateOnly.FromDateTime(Now.UtcDateTime);
            var oldDate = today.AddDays(-40);
            await store.RecordAsync(new TypingStatsWriteBatch(
                [new(Now.AddDays(-40).ToUnixTimeSeconds(), oldDate, 12, AppOne, 9)],
                [new(oldDate, PhysicalKeys.KeyA, 7)]));

            Assert.Equal(0, await ReadScalarAsync(fixture.Path, "SELECT COUNT(*) FROM CharacterSecondStat;"));
            var report = await store.LoadReportAsync(new TypingDateRange(oldDate, oldDate));
            Assert.Equal(9, report.Metrics.CharacterCount);
            Assert.Equal(9, Assert.Single(report.Applications).CharacterCount);
            Assert.Equal(9, report.HourlyDistribution.Sum(item => item.CharacterCount));
            var snapshot = await store.LoadSnapshotAsync();
            Assert.Equal(7, snapshot.AllTimeKeyCounts[PhysicalKeys.KeyA]);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task RejectsForeignApplicationIdWithoutRewritingIt()
    {
        var fixture = TemporaryDatabase.Create();
        try
        {
            await using (var connection = new SqliteConnection(
                $"Data Source={fixture.Path};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA application_id = 1234; PRAGMA user_version = 1; CREATE TABLE ForeignData(Value TEXT);";
                await command.ExecuteNonQueryAsync();
            }

            await using var store = fixture.CreateStore(Now);
            var exception = await Assert.ThrowsAsync<TypingStatsStoreException>(
                () => store.LoadSnapshotAsync());
            Assert.Equal(TypingStatsStoreErrorKind.IncompatibleSchema, exception.Kind);
            Assert.Equal(1234, await ReadPragmaAsync(fixture.Path, "application_id"));
            Assert.Equal(1, await ReadScalarAsync(
                fixture.Path,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ForeignData';"));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task LaterBatchIncrementsTotalsAndClearRemovesEveryAggregate()
    {
        var fixture = TemporaryDatabase.Create();
        try
        {
            var today = DateOnly.FromDateTime(Now.UtcDateTime);
            var epoch = Now.ToUnixTimeSeconds();
            await using (var store = fixture.CreateStore(Now))
            {
                await store.RecordAsync(new TypingStatsWriteBatch(
                    [new(epoch, today, 12, AppOne, 3)],
                    [new(today, PhysicalKeys.KeyA, 2)]));
                await store.RecordAsync(new TypingStatsWriteBatch(
                    [new(epoch, today, 12, AppOne, 2)],
                    [new(today, PhysicalKeys.KeyA, 1)]));
                var snapshot = await store.LoadSnapshotAsync();
                Assert.Equal(5, snapshot.Today.CharacterCount);
                Assert.Equal(5, snapshot.Today.PeakCps);
                Assert.Equal(3, snapshot.TodayKeyCounts[PhysicalKeys.KeyA]);
            }

            await using (var reopened = fixture.CreateStore(Now))
            {
                Assert.Equal(5, (await reopened.LoadSnapshotAsync()).Today.CharacterCount);
                await reopened.ClearAllAsync();
                var cleared = await reopened.LoadSnapshotAsync();
                Assert.Equal(0, cleared.Today.CharacterCount);
                Assert.Empty(cleared.Apps);
                Assert.Empty(cleared.TodayKeyCounts);
                Assert.Empty(cleared.AllTimeKeyCounts);
            }
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task RollingRangeIncludesExactFirstAndLastSecondOnly()
    {
        var fixture = TemporaryDatabase.Create();
        try
        {
            var rollingNow = Now.AddSeconds(37.4);
            await using var store = fixture.CreateStore(rollingNow);
            var definition = TypingTimelineRange.OneHour.GetDefinition();
            var endExclusive = rollingNow.ToUnixTimeSeconds() + 1;
            var start = endExclusive - definition.DurationSeconds;
            var date = DateOnly.FromDateTime(rollingNow.UtcDateTime);
            await store.RecordAsync(new TypingStatsWriteBatch(
                [
                    new(start - 1, date, 11, AppOne, 11),
                    new(start, date, 11, AppOne, 2),
                    new(endExclusive - 1, date, 12, AppOne, 3),
                    new(endExclusive, date, 12, AppOne, 13),
                ],
                []));

            var snapshot = await store.LoadSnapshotAsync(TypingTimelineRange.OneHour);
            Assert.Equal(5, snapshot.RecentBuckets.Sum(item => item.CharacterCount));
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(start), snapshot.RecentBuckets[0].Start);
            Assert.Equal(
                DateTimeOffset.FromUnixTimeSeconds(endExclusive),
                snapshot.RecentBuckets[^1].Start.AddSeconds(definition.BucketSeconds));
            Assert.Equal(5, Assert.Single(snapshot.RecentAppTimelines).RangeCharacterCount);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task<long> ReadPragmaAsync(string path, string pragma) =>
        await ReadScalarAsync(path, $"PRAGMA {pragma};");

    private static async Task<long> ReadScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory;

        private TemporaryDatabase(string directory)
        {
            _directory = directory;
            Path = System.IO.Path.Combine(directory, "typing-stats.sqlite3");
        }

        public string Path { get; }

        public static TemporaryDatabase Create()
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"battuta-stats-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            return new TemporaryDatabase(directory);
        }

        public TypingStatsSqliteStore CreateStore(DateTimeOffset now) =>
            new(Path, () => now, TimeZoneInfo.Utc);

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
