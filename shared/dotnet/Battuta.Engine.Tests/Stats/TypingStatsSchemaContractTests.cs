using System.Globalization;
using Battuta.Windows.Stats.Persistence;
using Battuta.TestSupport.IO;
using Microsoft.Data.Sqlite;

namespace Battuta.Windows.Tests.Stats;

/// <summary>
/// Structural contract test locking the BTTA SQLite v1 schema documented in
/// shared/contracts/TYPING_STATS_FORMAT.md: exact table, trigger, and index
/// inventory plus the pragmas later platforms must reproduce.
/// </summary>
public sealed class TypingStatsSchemaContractTests
{
    // Inventories are listed in sqlite_master ordinal (name) order, matching
    // the ORDER BY name query below.
    private static readonly string[] RequiredTables =
    [
        "AppDayStat",
        "AppProfile",
        "CharacterSecondStat",
        "DayStat",
        "HourDayStat",
        "KeyDailyStat",
        "KeyTotalStat",
    ];

    private static readonly string[] RequiredTriggers =
    [
        "TR_CharacterSecondStat_AggregateInsert",
        "TR_CharacterSecondStat_AggregateUpdate",
    ];

    private static readonly string[] RequiredIndexes =
    [
        "IX_AppDayStat_AppDate",
        "IX_CharacterSecondStat_Date",
        "IX_CharacterSecondStat_DateApp",
    ];

    [Fact]
    public async Task CreatedDatabaseMatchesDocumentedSchemaInventory()
    {
        using var directory = new TempDirectory();
        var databasePath = Path.Combine(directory.Path, "typing-stats.sqlite3");
        var store = new TypingStatsSqliteStore(databasePath);
        try
        {
            // Opening through a snapshot load creates the schema if the file is new.
            await store.LoadSnapshotAsync();
        }
        finally
        {
            await store.DisposeAsync();
        }

        Assert.Equal(0x42545441, TypingStatsSqliteStore.ApplicationId);
        Assert.Equal(1, TypingStatsSqliteStore.SchemaVersion);
        Assert.Equal(31, TypingStatsSqliteStore.DetailedRetentionDays);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        Assert.Equal(
            TypingStatsSqliteStore.ApplicationId,
            await ReadPragmaAsync(connection, "application_id"));
        Assert.Equal(
            TypingStatsSqliteStore.SchemaVersion,
            await ReadPragmaAsync(connection, "user_version"));

        var tables = await ReadObjectNamesAsync(connection, "table");
        Assert.Equal(RequiredTables, tables);

        var triggers = await ReadObjectNamesAsync(connection, "trigger");
        Assert.Equal(RequiredTriggers, triggers);

        var indexes = await ReadObjectNamesAsync(connection, "index");
        Assert.Equal(RequiredIndexes, indexes);
    }

    [Fact]
    public async Task OpenedStoreEnablesWalJournalMode()
    {
        using var directory = new TempDirectory();
        var databasePath = Path.Combine(directory.Path, "typing-stats.sqlite3");
        var store = new TypingStatsSqliteStore(databasePath);
        try
        {
            await store.LoadSnapshotAsync();
        }
        finally
        {
            await store.DisposeAsync();
        }

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();

        Assert.Equal("wal", await ReadTextPragmaAsync(connection, "journal_mode"));
    }

    private static async Task<long> ReadPragmaAsync(SqliteConnection connection, string pragma)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string> ReadTextPragmaAsync(SqliteConnection connection, string pragma)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        var value = await command.ExecuteScalarAsync();
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<string[]> ReadObjectNamesAsync(
        SqliteConnection connection,
        string objectType)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name FROM sqlite_master
            WHERE type = $type AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;
        command.Parameters.AddWithValue("$type", objectType);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return [.. names];
    }
}
