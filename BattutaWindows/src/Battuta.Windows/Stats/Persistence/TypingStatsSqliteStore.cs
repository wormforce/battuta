using System.Globalization;
using System.IO;
using Battuta.Core.Input;
using Battuta.Windows.Stats.Models;
using Microsoft.Data.Sqlite;

namespace Battuta.Windows.Stats.Persistence;

/// <summary>
/// Native Windows typing-statistics store. It persists only aggregate counts,
/// physical key IDs, daily mouse-click totals, time buckets, and foreground application identity.
/// </summary>
public sealed class TypingStatsSqliteStore : ITypingStatsPersistence, IAsyncDisposable
{
    // ASCII "BTTA". This prevents a copied macOS v2 database from being
    // mistaken for the Windows PhysicalKeyId schema and silently rewritten.
    public const int ApplicationId = 0x42545441;
    public const int SchemaVersion = 2;
    public const int HistoryDayCount = 14;
    public const int DetailedRetentionDays = 31;

    private const string DateFormat = "yyyy-MM-dd";

    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly Func<DateTimeOffset> _nowProvider;
    private readonly TimeZoneInfo _localTimeZone;
    private readonly Dictionary<string, long> _cachedApplicationIds = new(StringComparer.Ordinal);
    private SqliteConnection? _connection;
    private DateOnly? _lastCleanupDate;
    private bool _disposed;

    public TypingStatsSqliteStore(
        string? databasePath = null,
        Func<DateTimeOffset>? nowProvider = null,
        TimeZoneInfo? localTimeZone = null)
    {
        DatabasePath = databasePath ?? GetDefaultDatabasePath();
        _nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
    }

    public string DatabasePath { get; }

    public static string GetDefaultDatabasePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Battuta", "typing-stats.sqlite3");
    }

    public async Task RecordAsync(
        TypingStatsWriteBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.IsEmpty)
        {
            return;
        }

        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            var connection = await GetOpenConnectionAsync(cancellationToken);
            var updatedAt = _nowProvider().ToUnixTimeSeconds();
            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var resolvedApplications = new Dictionary<TypingApplicationIdentity, long>();
                foreach (var aggregate in batch.CharacterAggregates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (aggregate.Count <= 0 || aggregate.LocalHour is < 0 or > 23)
                    {
                        continue;
                    }

                    if (!resolvedApplications.TryGetValue(aggregate.Application, out var applicationId))
                    {
                        applicationId = await UpsertApplicationAsync(
                            aggregate.Application,
                            updatedAt,
                            connection,
                            transaction,
                            cancellationToken);
                        resolvedApplications[aggregate.Application] = applicationId;
                    }

                    await UpsertCharacterAggregateAsync(
                        aggregate,
                        applicationId,
                        updatedAt,
                        connection,
                        transaction,
                        cancellationToken);
                }

                foreach (var aggregate in batch.KeyAggregates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (aggregate.Count <= 0 || !aggregate.PhysicalKeyId.IsValid)
                    {
                        continue;
                    }

                    await UpsertKeyAggregateAsync(
                        aggregate,
                        updatedAt,
                        connection,
                        transaction,
                        cancellationToken);
                }

                foreach (var aggregate in batch.MouseAggregates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (aggregate.Count <= 0)
                    {
                        continue;
                    }

                    await UpsertMouseAggregateAsync(
                        aggregate,
                        updatedAt,
                        connection,
                        transaction,
                        cancellationToken);
                }

                var cleanupDate = await PerformCleanupIfNeededAsync(
                    connection,
                    transaction,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                if (cleanupDate is not null)
                {
                    _lastCleanupDate = cleanupDate;
                    _cachedApplicationIds.Clear();
                }
            }
            catch
            {
                await TryRollbackAsync(transaction);
                _cachedApplicationIds.Clear();
                throw;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw MapException(exception);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<TypingStatsSnapshot> LoadSnapshotAsync(
        TypingTimelineRange timelineRange = TypingTimelineRange.OneHour,
        CancellationToken cancellationToken = default)
    {
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            var connection = await GetOpenConnectionAsync(cancellationToken);
            var now = _nowProvider();
            var today = LocalDate(now);
            using var transaction = connection.BeginTransaction(deferred: true);
            try
            {
                var rankedApps = await LoadAppsAsync(
                    today,
                    100,
                    connection,
                    transaction,
                    cancellationToken);
                var recentTimeline = await LoadRecentTimelineAsync(
                    now,
                    timelineRange,
                    connection,
                    transaction,
                    cancellationToken);
                var snapshot = new TypingStatsSnapshot(
                    now,
                    await LoadLastInputAtAsync(connection, transaction, cancellationToken),
                    await LoadDaySummaryAsync(today, connection, transaction, cancellationToken),
                    timelineRange,
                    recentTimeline.Buckets,
                    rankedApps.Take(20).ToArray(),
                    recentTimeline.AppTimelines,
                    await LoadHistoryAsync(now, connection, transaction, cancellationToken),
                    await LoadKeyCountsAsync(today, connection, transaction, cancellationToken),
                    await LoadAllTimeKeyCountsAsync(connection, transaction, cancellationToken))
                {
                    TodayMouseClicks = await ExecuteScalarInt64Async(
                        connection,
                        transaction,
                        "SELECT COALESCE(SUM(ClickCount), 0) FROM MouseDailyStat WHERE LocalDate = $localDate;",
                        cancellationToken,
                        ("$localDate", DateKey(today))),
                    AllTimeMouseClicks = await ExecuteScalarInt64Async(
                        connection,
                        transaction,
                        "SELECT COALESCE(SUM(ClickCount), 0) FROM MouseDailyStat;",
                        cancellationToken),
                };
                await transaction.CommitAsync(cancellationToken);
                return snapshot;
            }
            catch
            {
                await TryRollbackAsync(transaction);
                throw;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw MapException(exception);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<TypingRangeReportSnapshot> LoadReportAsync(
        TypingDateRange range,
        TypingDateRange? comparisonRange = null,
        CancellationToken cancellationToken = default)
    {
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            var connection = await GetOpenConnectionAsync(cancellationToken);
            var generatedAt = _nowProvider();
            using var transaction = connection.BeginTransaction(deferred: true);
            try
            {
                var days = await LoadRangeDaysAsync(range, connection, transaction, cancellationToken);
                var weekdays = WeekdayDistribution(days);
                var hours = await LoadHourDistributionAsync(
                    range,
                    connection,
                    transaction,
                    cancellationToken);
                var weekdayHourCounts = await LoadWeekdayHourCountsAsync(
                    range,
                    connection,
                    transaction,
                    cancellationToken);
                var metrics = RangeMetrics(days, weekdays, hours);

                IReadOnlyList<TypingDaySummary> comparisonDays = [];
                TypingRangeMetrics? comparisonMetrics = null;
                IReadOnlyDictionary<int, long> comparisonWeekdayHourCounts =
                    new Dictionary<int, long>();
                if (comparisonRange is { } baseline)
                {
                    comparisonDays = await LoadRangeDaysAsync(
                        baseline,
                        connection,
                        transaction,
                        cancellationToken);
                    var comparisonWeekdays = WeekdayDistribution(comparisonDays);
                    var comparisonHours = await LoadHourDistributionAsync(
                        baseline,
                        connection,
                        transaction,
                        cancellationToken);
                    comparisonMetrics = RangeMetrics(
                        comparisonDays,
                        comparisonWeekdays,
                        comparisonHours);
                    comparisonWeekdayHourCounts = await LoadWeekdayHourCountsAsync(
                        baseline,
                        connection,
                        transaction,
                        cancellationToken);
                }

                var currentApplications = await LoadApplicationRangeValuesAsync(
                    range,
                    connection,
                    transaction,
                    cancellationToken);
                var comparisonApplications = comparisonRange is { } comparison
                    ? await LoadApplicationRangeValuesAsync(
                        comparison,
                        connection,
                        transaction,
                        cancellationToken)
                    : new Dictionary<string, ApplicationRangeValue>(StringComparer.Ordinal);

                var report = new TypingRangeReportSnapshot(
                    generatedAt,
                    range,
                    comparisonRange,
                    metrics,
                    comparisonMetrics,
                    days,
                    weekdays,
                    hours,
                    WeekdayHourDistribution(weekdayHourCounts, comparisonWeekdayHourCounts),
                    MergeApplicationRangeValues(
                        currentApplications,
                        comparisonApplications,
                        metrics.CharacterCount,
                        comparisonMetrics?.CharacterCount ?? 0),
                    await LoadCoverageAsync(
                        range,
                        metrics.ActiveDayCount,
                        connection,
                        transaction,
                        cancellationToken));
                await transaction.CommitAsync(cancellationToken);
                return report;
            }
            catch
            {
                await TryRollbackAsync(transaction);
                throw;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw MapException(exception);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            var connection = await GetOpenConnectionAsync(cancellationToken);
            await ExecuteNonQueryAsync(connection, null, "PRAGMA secure_delete = ON;", cancellationToken);
            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                await ExecuteNonQueryAsync(
                    connection,
                    transaction,
                    """
                    DELETE FROM CharacterSecondStat;
                    DELETE FROM KeyDailyStat;
                    DELETE FROM KeyTotalStat;
                    DELETE FROM MouseDailyStat;
                    DELETE FROM HourDayStat;
                    DELETE FROM AppDayStat;
                    DELETE FROM DayStat;
                    DELETE FROM AppProfile;
                    """,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                _cachedApplicationIds.Clear();
                _lastCleanupDate = null;
            }
            catch
            {
                await TryRollbackAsync(transaction);
                _cachedApplicationIds.Clear();
                throw;
            }

            // Logical deletion above is authoritative. Compaction is best effort.
            try
            {
                await ExecuteNonQueryAsync(
                    connection,
                    null,
                    "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;",
                    cancellationToken);
            }
            catch (SqliteException)
            {
                // A reader in another process may temporarily prevent compaction.
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw MapException(exception);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _connectionGate.WaitAsync();
        try
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
        finally
        {
            _connectionGate.Release();
            _connectionGate.Dispose();
        }
    }

    private async Task<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection is not null)
        {
            return _connection;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch (Exception exception)
        {
            throw new TypingStatsStoreException(
                TypingStatsStoreErrorKind.CannotCreateDirectory,
                $"无法创建 Battuta 统计目录：{exception.Message}",
                exception);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteNonQueryAsync(
                connection,
                null,
                """
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 1000;
                """,
                cancellationToken);
            await MigrateAndValidateAsync(connection, cancellationToken);
            // Persistent pragmas are applied only after the application ID and
            // schema have been accepted, so a foreign database is never rewritten.
            await ExecuteNonQueryAsync(
                connection,
                null,
                """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                """,
                cancellationToken);
            _connection = connection;
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task MigrateAndValidateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var applicationId = await ReadPragmaInt64Async(
            connection,
            "application_id",
            cancellationToken);
        var version = await ReadPragmaInt64Async(connection, "user_version", cancellationToken);
        var userTableCount = await ExecuteScalarInt64Async(
            connection,
            null,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';",
            cancellationToken);

        if (applicationId == 0 && version == 0 && userTableCount == 0)
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                await ExecuteNonQueryAsync(
                    connection,
                    transaction,
                    CreateSchemaSql,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await TryRollbackAsync(transaction);
                throw;
            }

            applicationId = ApplicationId;
            version = SchemaVersion;
        }

        if (applicationId != ApplicationId || version is not (1 or SchemaVersion))
        {
            throw new TypingStatsStoreException(
                TypingStatsStoreErrorKind.IncompatibleSchema,
                "Battuta Windows 本地统计数据库版本不兼容；数据库未被改写。");
        }

        var requiredSchema = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["AppProfile"] = ["Id", "ProcessKey", "ProcessName", "DisplayName", "ApplicationUserModelId", "UpdatedAtUtc"],
            ["CharacterSecondStat"] = ["SecondStartUtc", "LocalDate", "LocalHour", "AppId", "CharacterCount", "UpdatedAtUtc"],
            ["KeyDailyStat"] = ["LocalDate", "PhysicalKeyId", "PressCount", "UpdatedAtUtc"],
            ["KeyTotalStat"] = ["PhysicalKeyId", "PressCount", "UpdatedAtUtc"],
            ["DayStat"] = ["LocalDate", "CharacterCount", "ActiveMinuteBuckets", "ActiveSeconds", "PeakCPS", "LastInputUtc", "UpdatedAtUtc"],
            ["AppDayStat"] = ["LocalDate", "AppId", "CharacterCount", "ActiveMinuteBuckets", "ActiveSeconds", "PeakCPS", "UpdatedAtUtc"],
            ["HourDayStat"] = ["LocalDate", "LocalHour", "CharacterCount", "ActiveMinuteBuckets", "ActiveSeconds", "PeakCPS", "UpdatedAtUtc"],
        };

        if (version >= 2)
        {
            requiredSchema["MouseDailyStat"] = ["LocalDate", "ClickCount", "UpdatedAtUtc"];
        }

        foreach (var (table, requiredColumns) in requiredSchema)
        {
            var columns = await LoadColumnNamesAsync(connection, table, cancellationToken);
            if (!requiredColumns.All(columns.Contains))
            {
                throw new TypingStatsStoreException(
                    TypingStatsStoreErrorKind.IncompatibleSchema,
                    $"Battuta Windows 本地统计数据库缺少 {table} 所需字段。");
            }
        }

        // Validate the existing keyboard schema before migrating. An incompatible
        // or foreign database must remain untouched, including its user_version.
        if (version == 1)
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                await ExecuteNonQueryAsync(
                    connection,
                    transaction,
                    CreateMouseSchemaSql,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await TryRollbackAsync(transaction);
                throw;
            }
        }
    }

    private const string CreateMouseSchemaSql = """
        CREATE TABLE MouseDailyStat (
            LocalDate TEXT PRIMARY KEY CHECK(length(LocalDate) = 10),
            ClickCount INTEGER NOT NULL CHECK(ClickCount >= 0),
            UpdatedAtUtc INTEGER NOT NULL
        ) WITHOUT ROWID;
        PRAGMA user_version = 2;
        """;

    private const string CreateSchemaSql = """
        CREATE TABLE AppProfile (
            Id INTEGER PRIMARY KEY,
            ProcessKey TEXT NOT NULL UNIQUE,
            ProcessName TEXT NOT NULL,
            DisplayName TEXT NOT NULL,
            ApplicationUserModelId TEXT,
            UpdatedAtUtc INTEGER NOT NULL
        );
        CREATE TABLE CharacterSecondStat (
            SecondStartUtc INTEGER NOT NULL,
            LocalDate TEXT NOT NULL CHECK(length(LocalDate) = 10),
            LocalHour INTEGER NOT NULL CHECK(LocalHour BETWEEN 0 AND 23),
            AppId INTEGER NOT NULL REFERENCES AppProfile(Id),
            CharacterCount INTEGER NOT NULL CHECK(CharacterCount >= 0),
            UpdatedAtUtc INTEGER NOT NULL,
            PRIMARY KEY (SecondStartUtc, AppId)
        ) WITHOUT ROWID;
        CREATE INDEX IX_CharacterSecondStat_Date
            ON CharacterSecondStat(LocalDate, SecondStartUtc);
        CREATE INDEX IX_CharacterSecondStat_DateApp
            ON CharacterSecondStat(LocalDate, AppId);
        CREATE TABLE KeyDailyStat (
            LocalDate TEXT NOT NULL CHECK(length(LocalDate) = 10),
            PhysicalKeyId TEXT NOT NULL COLLATE BINARY
                CHECK(length(PhysicalKeyId) BETWEEN 1 AND 128),
            PressCount INTEGER NOT NULL CHECK(PressCount >= 0),
            UpdatedAtUtc INTEGER NOT NULL,
            PRIMARY KEY (LocalDate, PhysicalKeyId)
        ) WITHOUT ROWID;
        CREATE TABLE KeyTotalStat (
            PhysicalKeyId TEXT PRIMARY KEY COLLATE BINARY
                CHECK(length(PhysicalKeyId) BETWEEN 1 AND 128),
            PressCount INTEGER NOT NULL CHECK(PressCount >= 0),
            UpdatedAtUtc INTEGER NOT NULL
        ) WITHOUT ROWID;
        CREATE TABLE DayStat (
            LocalDate TEXT PRIMARY KEY,
            CharacterCount INTEGER NOT NULL CHECK(CharacterCount >= 0),
            ActiveMinuteBuckets INTEGER NOT NULL CHECK(ActiveMinuteBuckets >= 0),
            ActiveSeconds INTEGER NOT NULL CHECK(ActiveSeconds >= 0),
            PeakCPS INTEGER NOT NULL CHECK(PeakCPS >= 0),
            LastInputUtc INTEGER NOT NULL,
            UpdatedAtUtc INTEGER NOT NULL
        ) WITHOUT ROWID;
        CREATE TABLE AppDayStat (
            LocalDate TEXT NOT NULL,
            AppId INTEGER NOT NULL REFERENCES AppProfile(Id),
            CharacterCount INTEGER NOT NULL CHECK(CharacterCount >= 0),
            ActiveMinuteBuckets INTEGER NOT NULL CHECK(ActiveMinuteBuckets >= 0),
            ActiveSeconds INTEGER NOT NULL CHECK(ActiveSeconds >= 0),
            PeakCPS INTEGER NOT NULL CHECK(PeakCPS >= 0),
            UpdatedAtUtc INTEGER NOT NULL,
            PRIMARY KEY (LocalDate, AppId)
        ) WITHOUT ROWID;
        CREATE INDEX IX_AppDayStat_AppDate ON AppDayStat(AppId, LocalDate);
        CREATE TABLE HourDayStat (
            LocalDate TEXT NOT NULL,
            LocalHour INTEGER NOT NULL CHECK(LocalHour BETWEEN 0 AND 23),
            CharacterCount INTEGER NOT NULL CHECK(CharacterCount >= 0),
            ActiveMinuteBuckets INTEGER NOT NULL CHECK(ActiveMinuteBuckets >= 0),
            ActiveSeconds INTEGER NOT NULL CHECK(ActiveSeconds >= 0),
            PeakCPS INTEGER NOT NULL CHECK(PeakCPS >= 0),
            UpdatedAtUtc INTEGER NOT NULL,
            PRIMARY KEY (LocalDate, LocalHour)
        ) WITHOUT ROWID;

        CREATE TRIGGER TR_CharacterSecondStat_AggregateInsert
        AFTER INSERT ON CharacterSecondStat
        BEGIN
            INSERT INTO DayStat (
                LocalDate, CharacterCount, ActiveMinuteBuckets, ActiveSeconds,
                PeakCPS, LastInputUtc, UpdatedAtUtc
            ) VALUES (
                NEW.LocalDate,
                NEW.CharacterCount,
                CASE WHEN (
                    SELECT COUNT(*) FROM CharacterSecondStat
                    WHERE LocalDate = NEW.LocalDate
                      AND SecondStartUtc / 60 = NEW.SecondStartUtc / 60
                ) = 1 THEN 1 ELSE 0 END,
                CASE WHEN (
                    SELECT COUNT(*) FROM CharacterSecondStat
                    WHERE LocalDate = NEW.LocalDate
                      AND SecondStartUtc = NEW.SecondStartUtc
                ) = 1 THEN 1 ELSE 0 END,
                (SELECT COALESCE(SUM(CharacterCount), 0) FROM CharacterSecondStat
                 WHERE LocalDate = NEW.LocalDate
                   AND SecondStartUtc = NEW.SecondStartUtc),
                NEW.SecondStartUtc,
                NEW.UpdatedAtUtc
            ) ON CONFLICT(LocalDate) DO UPDATE SET
                CharacterCount = DayStat.CharacterCount + excluded.CharacterCount,
                ActiveMinuteBuckets = DayStat.ActiveMinuteBuckets + excluded.ActiveMinuteBuckets,
                ActiveSeconds = DayStat.ActiveSeconds + excluded.ActiveSeconds,
                PeakCPS = MAX(DayStat.PeakCPS, excluded.PeakCPS),
                LastInputUtc = MAX(DayStat.LastInputUtc, excluded.LastInputUtc),
                UpdatedAtUtc = MAX(DayStat.UpdatedAtUtc, excluded.UpdatedAtUtc);

            INSERT INTO AppDayStat (
                LocalDate, AppId, CharacterCount, ActiveMinuteBuckets,
                ActiveSeconds, PeakCPS, UpdatedAtUtc
            ) VALUES (
                NEW.LocalDate,
                NEW.AppId,
                NEW.CharacterCount,
                CASE WHEN (
                    SELECT COUNT(*) FROM CharacterSecondStat
                    WHERE LocalDate = NEW.LocalDate
                      AND AppId = NEW.AppId
                      AND SecondStartUtc / 60 = NEW.SecondStartUtc / 60
                ) = 1 THEN 1 ELSE 0 END,
                1,
                NEW.CharacterCount,
                NEW.UpdatedAtUtc
            ) ON CONFLICT(LocalDate, AppId) DO UPDATE SET
                CharacterCount = AppDayStat.CharacterCount + excluded.CharacterCount,
                ActiveMinuteBuckets = AppDayStat.ActiveMinuteBuckets + excluded.ActiveMinuteBuckets,
                ActiveSeconds = AppDayStat.ActiveSeconds + excluded.ActiveSeconds,
                PeakCPS = MAX(AppDayStat.PeakCPS, excluded.PeakCPS),
                UpdatedAtUtc = MAX(AppDayStat.UpdatedAtUtc, excluded.UpdatedAtUtc);

            INSERT INTO HourDayStat (
                LocalDate, LocalHour, CharacterCount, ActiveMinuteBuckets,
                ActiveSeconds, PeakCPS, UpdatedAtUtc
            ) VALUES (
                NEW.LocalDate,
                NEW.LocalHour,
                NEW.CharacterCount,
                CASE WHEN (
                    SELECT COUNT(*) FROM CharacterSecondStat
                    WHERE LocalDate = NEW.LocalDate
                      AND LocalHour = NEW.LocalHour
                      AND SecondStartUtc / 60 = NEW.SecondStartUtc / 60
                ) = 1 THEN 1 ELSE 0 END,
                CASE WHEN (
                    SELECT COUNT(*) FROM CharacterSecondStat
                    WHERE LocalDate = NEW.LocalDate
                      AND LocalHour = NEW.LocalHour
                      AND SecondStartUtc = NEW.SecondStartUtc
                ) = 1 THEN 1 ELSE 0 END,
                (SELECT COALESCE(SUM(CharacterCount), 0) FROM CharacterSecondStat
                 WHERE LocalDate = NEW.LocalDate
                   AND LocalHour = NEW.LocalHour
                   AND SecondStartUtc = NEW.SecondStartUtc),
                NEW.UpdatedAtUtc
            ) ON CONFLICT(LocalDate, LocalHour) DO UPDATE SET
                CharacterCount = HourDayStat.CharacterCount + excluded.CharacterCount,
                ActiveMinuteBuckets = HourDayStat.ActiveMinuteBuckets + excluded.ActiveMinuteBuckets,
                ActiveSeconds = HourDayStat.ActiveSeconds + excluded.ActiveSeconds,
                PeakCPS = MAX(HourDayStat.PeakCPS, excluded.PeakCPS),
                UpdatedAtUtc = MAX(HourDayStat.UpdatedAtUtc, excluded.UpdatedAtUtc);
        END;

        CREATE TRIGGER TR_CharacterSecondStat_AggregateUpdate
        AFTER UPDATE OF CharacterCount ON CharacterSecondStat
        BEGIN
            UPDATE DayStat SET
                CharacterCount = CharacterCount + NEW.CharacterCount - OLD.CharacterCount,
                PeakCPS = MAX(
                    PeakCPS,
                    (SELECT COALESCE(SUM(CharacterCount), 0)
                     FROM CharacterSecondStat
                     WHERE LocalDate = NEW.LocalDate
                       AND SecondStartUtc = NEW.SecondStartUtc)
                ),
                LastInputUtc = MAX(LastInputUtc, NEW.SecondStartUtc),
                UpdatedAtUtc = MAX(UpdatedAtUtc, NEW.UpdatedAtUtc)
            WHERE LocalDate = NEW.LocalDate;

            UPDATE AppDayStat SET
                CharacterCount = CharacterCount + NEW.CharacterCount - OLD.CharacterCount,
                PeakCPS = MAX(PeakCPS, NEW.CharacterCount),
                UpdatedAtUtc = MAX(UpdatedAtUtc, NEW.UpdatedAtUtc)
            WHERE LocalDate = NEW.LocalDate AND AppId = NEW.AppId;

            UPDATE HourDayStat SET
                CharacterCount = CharacterCount + NEW.CharacterCount - OLD.CharacterCount,
                PeakCPS = MAX(
                    PeakCPS,
                    (SELECT COALESCE(SUM(CharacterCount), 0)
                     FROM CharacterSecondStat
                     WHERE LocalDate = NEW.LocalDate
                       AND LocalHour = NEW.LocalHour
                       AND SecondStartUtc = NEW.SecondStartUtc)
                ),
                UpdatedAtUtc = MAX(UpdatedAtUtc, NEW.UpdatedAtUtc)
            WHERE LocalDate = NEW.LocalDate AND LocalHour = NEW.LocalHour;
        END;

        PRAGMA application_id = 1112822849;
        """ + CreateMouseSchemaSql;

    private async Task<long> UpsertApplicationAsync(
        TypingApplicationIdentity application,
        long updatedAt,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (_cachedApplicationIds.TryGetValue(application.ProcessKey, out var cached))
        {
            // Names can change after an app update, so update metadata even when the ID is cached.
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                """
                UPDATE AppProfile
                SET ProcessName = $processName,
                    DisplayName = $displayName,
                    ApplicationUserModelId = $applicationUserModelId,
                    UpdatedAtUtc = $updatedAt
                WHERE Id = $id;
                """,
                cancellationToken,
                ("$processName", application.ProcessName),
                ("$displayName", application.DisplayName),
                ("$applicationUserModelId", application.ApplicationUserModelId),
                ("$updatedAt", updatedAt),
                ("$id", cached));
            return cached;
        }

        var id = await ExecuteScalarInt64Async(
            connection,
            transaction,
            """
            INSERT INTO AppProfile (
                ProcessKey, ProcessName, DisplayName, ApplicationUserModelId, UpdatedAtUtc
            ) VALUES (
                $processKey, $processName, $displayName, $applicationUserModelId, $updatedAt
            )
            ON CONFLICT(ProcessKey) DO UPDATE SET
                ProcessName = excluded.ProcessName,
                DisplayName = excluded.DisplayName,
                ApplicationUserModelId = excluded.ApplicationUserModelId,
                UpdatedAtUtc = excluded.UpdatedAtUtc
            RETURNING Id;
            """,
            cancellationToken,
            ("$processKey", application.ProcessKey),
            ("$processName", application.ProcessName),
            ("$displayName", application.DisplayName),
            ("$applicationUserModelId", application.ApplicationUserModelId),
            ("$updatedAt", updatedAt));
        _cachedApplicationIds[application.ProcessKey] = id;
        return id;
    }

    private static Task<int> UpsertCharacterAggregateAsync(
        TypingCharacterAggregate aggregate,
        long applicationId,
        long updatedAt,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            INSERT INTO CharacterSecondStat (
                SecondStartUtc, LocalDate, LocalHour, AppId, CharacterCount, UpdatedAtUtc
            ) VALUES (
                $secondStartUtc, $localDate, $localHour, $appId, $characterCount, $updatedAt
            )
            ON CONFLICT(SecondStartUtc, AppId) DO UPDATE SET
                CharacterCount = CharacterCount + excluded.CharacterCount,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """,
            cancellationToken,
            ("$secondStartUtc", aggregate.SecondStartUtc),
            ("$localDate", DateKey(aggregate.LocalDate)),
            ("$localHour", aggregate.LocalHour),
            ("$appId", applicationId),
            ("$characterCount", aggregate.Count),
            ("$updatedAt", updatedAt));

    private static async Task UpsertKeyAggregateAsync(
        TypingKeyAggregate aggregate,
        long updatedAt,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            INSERT INTO KeyDailyStat (LocalDate, PhysicalKeyId, PressCount, UpdatedAtUtc)
            VALUES ($localDate, $physicalKeyId, $pressCount, $updatedAt)
            ON CONFLICT(LocalDate, PhysicalKeyId) DO UPDATE SET
                PressCount = PressCount + excluded.PressCount,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """,
            cancellationToken,
            ("$localDate", DateKey(aggregate.LocalDate)),
            ("$physicalKeyId", aggregate.PhysicalKeyId.Value),
            ("$pressCount", aggregate.Count),
            ("$updatedAt", updatedAt));

        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            INSERT INTO KeyTotalStat (PhysicalKeyId, PressCount, UpdatedAtUtc)
            VALUES ($physicalKeyId, $pressCount, $updatedAt)
            ON CONFLICT(PhysicalKeyId) DO UPDATE SET
                PressCount = PressCount + excluded.PressCount,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """,
            cancellationToken,
            ("$physicalKeyId", aggregate.PhysicalKeyId.Value),
            ("$pressCount", aggregate.Count),
            ("$updatedAt", updatedAt));
    }

    private static Task<int> UpsertMouseAggregateAsync(
        MouseClickAggregate aggregate,
        long updatedAt,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            INSERT INTO MouseDailyStat (LocalDate, ClickCount, UpdatedAtUtc)
            VALUES ($localDate, $clickCount, $updatedAt)
            ON CONFLICT(LocalDate) DO UPDATE SET
                ClickCount = ClickCount + excluded.ClickCount,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """,
            cancellationToken,
            ("$localDate", DateKey(aggregate.LocalDate)),
            ("$clickCount", aggregate.Count),
            ("$updatedAt", updatedAt));

    private async Task<DateOnly?> PerformCleanupIfNeededAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var today = LocalDate(_nowProvider());
        if (_lastCleanupDate == today)
        {
            return null;
        }

        var cutoff = today.AddDays(-DetailedRetentionDays);
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            "DELETE FROM CharacterSecondStat WHERE LocalDate < $cutoff;",
            cancellationToken,
            ("$cutoff", DateKey(cutoff)));
        return today;
    }

    private static async Task<TypingDaySummary> LoadDaySummaryAsync(
        DateOnly date,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        long characterCount;
        long peakCps;
        long activeMinuteBuckets;
        long activeSeconds;
        DateTimeOffset? lastUpdatedAt;
        await using (var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT CharacterCount, PeakCPS, ActiveMinuteBuckets, ActiveSeconds, UpdatedAtUtc
            FROM DayStat
            WHERE LocalDate = $localDate;
            """,
            ("$localDate", DateKey(date))))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return EmptyDay(date);
            }

            characterCount = reader.GetInt64(0);
            peakCps = reader.GetInt64(1);
            activeMinuteBuckets = reader.GetInt64(2);
            activeSeconds = reader.GetInt64(3);
            lastUpdatedAt = UnixDate(reader, 4);
        }

        var topApp = await LoadTopAppNameAsync(
            date,
            connection,
            transaction,
            cancellationToken);
        return new TypingDaySummary(
            date,
            characterCount,
            peakCps,
            activeMinuteBuckets,
            activeSeconds,
            topApp,
            lastUpdatedAt);
    }

    private static async Task<string?> LoadTopAppNameAsync(
        DateOnly date,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT app.DisplayName
            FROM AppDayStat stat
            JOIN AppProfile app ON app.Id = stat.AppId
            WHERE stat.LocalDate = $localDate
            ORDER BY stat.CharacterCount DESC, app.DisplayName COLLATE NOCASE, stat.AppId
            LIMIT 1;
            """,
            ("$localDate", DateKey(date)));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<TypingAppSummary>> LoadAppsAsync(
        DateOnly date,
        int limit,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT app.ProcessKey,
                   app.DisplayName,
                   app.ProcessName,
                   app.ApplicationUserModelId,
                   stat.CharacterCount,
                   stat.ActiveMinuteBuckets,
                   stat.ActiveSeconds,
                   stat.PeakCPS
            FROM AppDayStat stat
            JOIN AppProfile app ON app.Id = stat.AppId
            WHERE stat.LocalDate = $localDate
            ORDER BY stat.CharacterCount DESC,
                     app.DisplayName COLLATE NOCASE,
                     stat.AppId
            LIMIT $limit;
            """,
            ("$localDate", DateKey(date)),
            ("$limit", Math.Clamp(limit, 1, 100)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var output = new List<TypingAppSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new TypingAppSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                NullableString(reader, 3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7)));
        }

        return output;
    }

    private sealed record RecentTimelineData(
        IReadOnlyList<TypingBucket> Buckets,
        IReadOnlyList<TypingAppTimeline> AppTimelines);

    private static async Task<RecentTimelineData> LoadRecentTimelineAsync(
        DateTimeOffset now,
        TypingTimelineRange range,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var definition = range.GetDefinition();
        var endExclusive = now.ToUnixTimeSeconds() + 1;
        var start = endExclusive - definition.DurationSeconds;
        var totalCounts = new long[definition.BucketCount];
        var identities = new Dictionary<string, TypingApplicationIdentity>(StringComparer.Ordinal);
        var sparseAppCounts = new Dictionary<string, Dictionary<int, long>>(StringComparer.Ordinal);

        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT app.ProcessKey,
                   app.DisplayName,
                   app.ProcessName,
                   app.ApplicationUserModelId,
                   CAST((stat.SecondStartUtc - $start) / $bucketSeconds AS INTEGER) AS BucketIndex,
                   SUM(stat.CharacterCount)
            FROM CharacterSecondStat stat
            JOIN AppProfile app ON app.Id = stat.AppId
            WHERE stat.SecondStartUtc >= $start AND stat.SecondStartUtc < $endExclusive
            GROUP BY stat.AppId, BucketIndex
            ORDER BY stat.AppId, BucketIndex;
            """,
            ("$start", start),
            ("$endExclusive", endExclusive),
            ("$bucketSeconds", definition.BucketSeconds));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var processKey = reader.GetString(0);
            var index = checked((int)reader.GetInt64(4));
            if (index < 0 || index >= totalCounts.Length)
            {
                continue;
            }

            var count = reader.GetInt64(5);
            totalCounts[index] += count;
            if (!sparseAppCounts.TryGetValue(processKey, out var counts))
            {
                counts = [];
                sparseAppCounts[processKey] = counts;
            }

            counts[index] = count;
            identities[processKey] = new TypingApplicationIdentity(
                processKey,
                reader.GetString(1),
                reader.GetString(2),
                NullableString(reader, 3));
        }

        var buckets = Enumerable.Range(0, definition.BucketCount)
            .Select(index => new TypingBucket(
                index,
                DateTimeOffset.FromUnixTimeSeconds(start + index * definition.BucketSeconds),
                totalCounts[index]))
            .ToArray();
        var sortedKeys = identities.Keys
            .OrderByDescending(key => sparseAppCounts[key].Values.Sum())
            .ThenBy(key => identities[key].DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(key => key, StringComparer.Ordinal)
            .Take(20);
        var timelines = sortedKeys.Select(processKey =>
        {
            var counts = sparseAppCounts[processKey];
            var appBuckets = Enumerable.Range(0, definition.BucketCount)
                .Select(index => new TypingBucket(
                    index,
                    DateTimeOffset.FromUnixTimeSeconds(start + index * definition.BucketSeconds),
                    counts.GetValueOrDefault(index)))
                .ToArray();
            return new TypingAppTimeline(identities[processKey], appBuckets);
        }).ToArray();

        return new RecentTimelineData(buckets, timelines);
    }

    private async Task<IReadOnlyList<TypingDaySummary>> LoadHistoryAsync(
        DateTimeOffset now,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var today = LocalDate(now);
        var start = today.AddDays(-(HistoryDayCount - 1));
        return await LoadRangeDaysAsync(
            new TypingDateRange(start, today),
            connection,
            transaction,
            cancellationToken);
    }

    private static Task<IReadOnlyDictionary<PhysicalKeyId, long>> LoadKeyCountsAsync(
        DateOnly date,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        ReadKeyCountsAsync(
            connection,
            transaction,
            "SELECT PhysicalKeyId, PressCount FROM KeyDailyStat WHERE LocalDate = $localDate;",
            cancellationToken,
            ("$localDate", DateKey(date)));

    private static Task<IReadOnlyDictionary<PhysicalKeyId, long>> LoadAllTimeKeyCountsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        ReadKeyCountsAsync(
            connection,
            transaction,
            "SELECT PhysicalKeyId, PressCount FROM KeyTotalStat;",
            cancellationToken);

    private static async Task<IReadOnlyDictionary<PhysicalKeyId, long>> ReadKeyCountsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var output = new Dictionary<PhysicalKeyId, long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (PhysicalKeyId.TryParse(reader.GetString(0), out var keyId))
            {
                output[keyId] = reader.GetInt64(1);
            }
        }

        return output;
    }

    private static async Task<DateTimeOffset?> LoadLastInputAtAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            "SELECT MAX(LastInputUtc) FROM DayStat;");
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    private static async Task<IReadOnlyList<TypingDaySummary>> LoadRangeDaysAsync(
        TypingDateRange range,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var stored = new Dictionary<DateOnly, TypingDaySummary>();
        await using (var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT LocalDate, CharacterCount, PeakCPS, ActiveMinuteBuckets,
                   ActiveSeconds, UpdatedAtUtc
            FROM DayStat
            WHERE LocalDate BETWEEN $start AND $end
            ORDER BY LocalDate;
            """,
            ("$start", DateKey(range.StartDate)),
            ("$end", DateKey(range.EndDate))))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!TryParseDate(reader.GetString(0), out var date))
                {
                    continue;
                }

                stored[date] = new TypingDaySummary(
                    date,
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    null,
                    UnixDate(reader, 5));
            }
        }

        var topApps = await LoadDailyTopAppsAsync(
            range,
            connection,
            transaction,
            cancellationToken);
        var output = new List<TypingDaySummary>(range.DayCount);
        for (var offset = 0; offset < range.DayCount; offset++)
        {
            var date = range.StartDate.AddDays(offset);
            if (!stored.TryGetValue(date, out var summary))
            {
                output.Add(EmptyDay(date));
                continue;
            }

            output.Add(summary with { TopAppName = topApps.GetValueOrDefault(date) });
        }

        return output;
    }

    private static async Task<IReadOnlyDictionary<DateOnly, string>> LoadDailyTopAppsAsync(
        TypingDateRange range,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            WITH Ranked AS (
                SELECT stat.LocalDate,
                       app.DisplayName,
                       ROW_NUMBER() OVER (
                           PARTITION BY stat.LocalDate
                           ORDER BY stat.CharacterCount DESC,
                                    app.DisplayName COLLATE NOCASE,
                                    stat.AppId
                       ) AS Position
                FROM AppDayStat stat
                JOIN AppProfile app ON app.Id = stat.AppId
                WHERE stat.LocalDate BETWEEN $start AND $end
            )
            SELECT LocalDate, DisplayName
            FROM Ranked
            WHERE Position = 1;
            """,
            ("$start", DateKey(range.StartDate)),
            ("$end", DateKey(range.EndDate)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var output = new Dictionary<DateOnly, string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (TryParseDate(reader.GetString(0), out var date))
            {
                output[date] = reader.GetString(1);
            }
        }

        return output;
    }

    private static async Task<IReadOnlyList<TypingHourAggregate>> LoadHourDistributionAsync(
        TypingDateRange range,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT LocalHour,
                   SUM(CharacterCount),
                   COUNT(DISTINCT LocalDate),
                   MAX(PeakCPS)
            FROM HourDayStat
            WHERE LocalDate BETWEEN $start AND $end
            GROUP BY LocalHour
            ORDER BY LocalHour;
            """,
            ("$start", DateKey(range.StartDate)),
            ("$end", DateKey(range.EndDate)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var stored = new Dictionary<int, TypingHourAggregate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var hour = checked((int)reader.GetInt64(0));
            if (hour is < 0 or > 23)
            {
                continue;
            }

            stored[hour] = new TypingHourAggregate(
                hour,
                reader.GetInt64(1),
                checked((int)reader.GetInt64(2)),
                reader.GetInt64(3));
        }

        return Enumerable.Range(0, 24)
            .Select(hour => stored.GetValueOrDefault(hour)
                ?? new TypingHourAggregate(hour, 0, 0, 0))
            .ToArray();
    }

    private static async Task<IReadOnlyDictionary<int, long>> LoadWeekdayHourCountsAsync(
        TypingDateRange range,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT CAST(strftime('%w', LocalDate) AS INTEGER) + 1 AS FoundationWeekday,
                   LocalHour,
                   SUM(CharacterCount)
            FROM HourDayStat
            WHERE LocalDate BETWEEN $start AND $end
            GROUP BY FoundationWeekday, LocalHour
            ORDER BY FoundationWeekday, LocalHour;
            """,
            ("$start", DateKey(range.StartDate)),
            ("$end", DateKey(range.EndDate)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var output = new Dictionary<int, long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var weekday = checked((int)reader.GetInt64(0));
            var hour = checked((int)reader.GetInt64(1));
            if (weekday is >= 1 and <= 7 && hour is >= 0 and <= 23)
            {
                output[(weekday - 1) * 24 + hour] = reader.GetInt64(2);
            }
        }

        return output;
    }

    private sealed record ApplicationRangeValue(
        TypingApplicationIdentity Application,
        long CharacterCount,
        int ActiveDayCount);

    private static async Task<Dictionary<string, ApplicationRangeValue>>
        LoadApplicationRangeValuesAsync(
            TypingDateRange range,
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT app.ProcessKey,
                   app.DisplayName,
                   app.ProcessName,
                   app.ApplicationUserModelId,
                   SUM(stat.CharacterCount),
                   COUNT(DISTINCT stat.LocalDate)
            FROM AppDayStat stat
            JOIN AppProfile app ON app.Id = stat.AppId
            WHERE stat.LocalDate BETWEEN $start AND $end
            GROUP BY stat.AppId
            ORDER BY SUM(stat.CharacterCount) DESC,
                     app.DisplayName COLLATE NOCASE,
                     stat.AppId;
            """,
            ("$start", DateKey(range.StartDate)),
            ("$end", DateKey(range.EndDate)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var output = new Dictionary<string, ApplicationRangeValue>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var processKey = reader.GetString(0);
            output[processKey] = new ApplicationRangeValue(
                new TypingApplicationIdentity(
                    processKey,
                    reader.GetString(1),
                    reader.GetString(2),
                    NullableString(reader, 3)),
                reader.GetInt64(4),
                checked((int)reader.GetInt64(5)));
        }

        return output;
    }

    private static async Task<TypingReportDataCoverage> LoadCoverageAsync(
        TypingDateRange range,
        int recordedDayCount,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            "SELECT MIN(LocalDate), MAX(LocalDate) FROM DayStat;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new TypingReportDataCoverage(null, null, range.DayCount, recordedDayCount, false);
        }

        DateOnly? first = null;
        DateOnly? last = null;
        if (!reader.IsDBNull(0) && TryParseDate(reader.GetString(0), out var firstValue))
        {
            first = firstValue;
        }

        if (!reader.IsDBNull(1) && TryParseDate(reader.GetString(1), out var lastValue))
        {
            last = lastValue;
        }

        var isWithin = first is not null
            && last is not null
            && range.StartDate >= first.Value
            && range.EndDate <= last.Value;
        return new TypingReportDataCoverage(
            first,
            last,
            range.DayCount,
            recordedDayCount,
            isWithin);
    }

    private static TypingWeekdayAggregate[] WeekdayDistribution(
        IReadOnlyList<TypingDaySummary> days)
    {
        var counts = new long[7];
        var activeDays = new int[7];
        foreach (var day in days)
        {
            var weekday = (int)day.Date.DayOfWeek + 1;
            counts[weekday - 1] += day.CharacterCount;
            if (day.CharacterCount > 0)
            {
                activeDays[weekday - 1]++;
            }
        }

        return Enumerable.Range(1, 7)
            .Select(weekday => new TypingWeekdayAggregate(
                weekday,
                counts[weekday - 1],
                activeDays[weekday - 1]))
            .ToArray();
    }

    private static TypingWeekdayHourAggregate[] WeekdayHourDistribution(
        IReadOnlyDictionary<int, long> current,
        IReadOnlyDictionary<int, long> comparison) =>
        Enumerable.Range(1, 7)
            .SelectMany(weekday => Enumerable.Range(0, 24).Select(hour =>
            {
                var key = (weekday - 1) * 24 + hour;
                return new TypingWeekdayHourAggregate(
                    weekday,
                    hour,
                    current.GetValueOrDefault(key),
                    comparison.GetValueOrDefault(key));
            }))
            .ToArray();

    private static TypingRangeMetrics RangeMetrics(
        IReadOnlyList<TypingDaySummary> days,
        IReadOnlyList<TypingWeekdayAggregate> weekdays,
        IReadOnlyList<TypingHourAggregate> hours)
    {
        var total = days.Sum(day => day.CharacterCount);
        var activeDays = days.Where(day => day.CharacterCount > 0).ToArray();
        var bestDay = activeDays
            .OrderByDescending(day => day.CharacterCount)
            .ThenBy(day => day.Date)
            .FirstOrDefault();
        var longestStreak = 0;
        var currentStreak = 0;
        foreach (var day in days)
        {
            if (day.CharacterCount > 0)
            {
                currentStreak++;
                longestStreak = Math.Max(longestStreak, currentStreak);
            }
            else
            {
                currentStreak = 0;
            }
        }

        var busiestWeekday = weekdays
            .Where(item => item.CharacterCount > 0)
            .OrderByDescending(item => item.CharacterCount)
            .ThenByDescending(item => item.ActiveDayCount)
            .ThenBy(item => item.Weekday)
            .FirstOrDefault();
        var busiestHour = hours
            .Where(item => item.CharacterCount > 0)
            .OrderByDescending(item => item.CharacterCount)
            .ThenByDescending(item => item.ActiveDayCount)
            .ThenBy(item => item.Hour)
            .FirstOrDefault();
        return new TypingRangeMetrics(
            total,
            days.Count,
            activeDays.Length,
            days.Count == 0 ? 0 : (double)total / days.Count,
            activeDays.Length == 0 ? 0 : (double)total / activeDays.Length,
            days.Count == 0 ? 0 : days.Max(day => day.PeakCps),
            bestDay,
            longestStreak,
            busiestWeekday,
            busiestHour);
    }

    private static TypingRangeApplicationSummary[] MergeApplicationRangeValues(
        IReadOnlyDictionary<string, ApplicationRangeValue> current,
        IReadOnlyDictionary<string, ApplicationRangeValue> comparison,
        long currentTotal,
        long comparisonTotal)
    {
        return current.Keys
            .Concat(comparison.Keys)
            .Distinct(StringComparer.Ordinal)
            .Select(key =>
            {
                current.TryGetValue(key, out var currentValue);
                comparison.TryGetValue(key, out var comparisonValue);
                var identity = currentValue?.Application ?? comparisonValue!.Application;
                var count = currentValue?.CharacterCount ?? 0;
                var baseline = comparisonValue?.CharacterCount ?? 0;
                return new TypingRangeApplicationSummary(
                    identity,
                    count,
                    baseline,
                    currentValue?.ActiveDayCount ?? 0,
                    comparisonValue?.ActiveDayCount ?? 0,
                    currentTotal > 0 ? (double)count / currentTotal : 0,
                    comparisonTotal > 0 ? (double)baseline / comparisonTotal : 0,
                    count - baseline,
                    baseline > 0 ? (double)(count - baseline) / baseline : null);
            })
            .OrderByDescending(item => item.CharacterCount)
            .ThenByDescending(item => item.ComparisonCharacterCount)
            .ThenBy(item => item.Application.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Application.ProcessKey, StringComparer.Ordinal)
            .ToArray();
    }

    private DateOnly LocalDate(DateTimeOffset value)
    {
        var local = TimeZoneInfo.ConvertTime(value, _localTimeZone);
        return DateOnly.FromDateTime(local.DateTime);
    }

    private static TypingDaySummary EmptyDay(DateOnly date) =>
        new(date, 0, 0, 0, 0, null, null);

    private static string DateKey(DateOnly date) =>
        date.ToString(DateFormat, CultureInfo.InvariantCulture);

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(
            value,
            DateFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    private static string? NullableString(System.Data.Common.DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? UnixDate(
        System.Data.Common.DbDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(ordinal));

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static async Task<int> ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ExecuteScalarInt64Async(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
        {
            throw new TypingStatsStoreException(
                TypingStatsStoreErrorKind.QueryFailed,
                "读取 Battuta 本地统计时没有返回预期值。");
        }

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static Task<long> ReadPragmaInt64Async(
        SqliteConnection connection,
        string pragma,
        CancellationToken cancellationToken) =>
        ExecuteScalarInt64Async(
            connection,
            null,
            $"PRAGMA {pragma};",
            cancellationToken);

    private static async Task<HashSet<string>> LoadColumnNamesAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            null,
            "SELECT name FROM pragma_table_info($table);",
            ("$table", table));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task TryRollbackAsync(SqliteTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync();
        }
        catch
        {
            // Preserve the original failure.
        }
    }

    private static Exception MapException(Exception exception)
    {
        if (exception is TypingStatsStoreException)
        {
            return exception;
        }

        if (exception is SqliteException sqliteException)
        {
            var message = sqliteException.Message;
            var normalizedMessage = message.ToLowerInvariant();
            var kind = sqliteException.SqliteErrorCode switch
            {
                5 or 6 => TypingStatsStoreErrorKind.Busy,
                11 or 26 => TypingStatsStoreErrorKind.Corrupt,
                14 => TypingStatsStoreErrorKind.CannotOpen,
                17 => TypingStatsStoreErrorKind.IncompatibleSchema,
                _ when normalizedMessage.Contains("no such table", StringComparison.Ordinal)
                    || normalizedMessage.Contains("no such column", StringComparison.Ordinal) =>
                    TypingStatsStoreErrorKind.IncompatibleSchema,
                _ => TypingStatsStoreErrorKind.QueryFailed,
            };
            var prefix = kind switch
            {
                TypingStatsStoreErrorKind.Busy => "本地统计数据库暂时繁忙，请稍后重试。",
                TypingStatsStoreErrorKind.Corrupt => "本地统计数据库无法读取或已经损坏。",
                TypingStatsStoreErrorKind.CannotOpen => "无法打开 Battuta 本地统计。",
                TypingStatsStoreErrorKind.IncompatibleSchema => "Battuta 本地统计数据库版本不兼容。",
                _ => "读取 Battuta 本地统计失败。",
            };
            return new TypingStatsStoreException(kind, $"{prefix} {message}", exception);
        }

        return new TypingStatsStoreException(
            TypingStatsStoreErrorKind.QueryFailed,
            $"读取 Battuta 本地统计失败：{exception.Message}",
            exception);
    }
}
