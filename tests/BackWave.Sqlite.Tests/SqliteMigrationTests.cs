using BackWave.Sqlite;
using BackWave.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BackWave.Sqlite.Tests;

/// <summary>Migrator + consolidated-schema assertions for the 0092 acceptance criteria.</summary>
public sealed class SqliteMigrationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AutoMigrate_LogsMigrationApplied_TaggedWithTheSqliteDbSystem()
    {
        var capture = new LogCapture();
        await using var temp = TempSqliteStore.Create(loggerFactory: new CapturingLoggerFactory(capture));
        // The first store op wins the ready-gate and runs AutoMigrate, which logs MigrationApplied once.
        await temp.Store.EnqueueAsync(new NewJob(Guid.NewGuid(), "demo", default, "default", T0), T0);

        var migration = Assert.Single(capture.Records, r => r.EventId == 1302);
        Assert.Equal(LogLevel.Information, migration.Level);
        Assert.Contains("sqlite", migration.Message);
    }

    [Fact]
    public async Task Migration_enables_wal_and_creates_the_partial_claim_index()
    {
        await using var temp = TempSqliteStore.Create();
        // First store op forces EnsureReady -> migrate.
        await temp.Store.EnqueueAsync(new NewJob(Guid.NewGuid(), "demo", default, "default", T0), T0);

        await using var connection = new SqliteConnection($"Data Source={temp.Path}");
        await connection.OpenAsync();

        Assert.Equal("wal", await ScalarText(connection, "PRAGMA journal_mode"));

        var claimIndex = await ScalarText(connection,
            "SELECT sql FROM sqlite_master WHERE type='index' AND name='ix_backwave_jobs_claim'");
        Assert.NotNull(claimIndex);
        Assert.Contains("WHERE state = 0", claimIndex, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jobs_table_uses_autoincrement_sequence_and_unique_job_id()
    {
        await using var temp = TempSqliteStore.Create();
        await temp.Store.EnqueueAsync(new NewJob(Guid.NewGuid(), "demo", default, "default", T0), T0);

        await using var connection = new SqliteConnection($"Data Source={temp.Path}");
        await connection.OpenAsync();

        var tableSql = await ScalarText(connection,
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='backwave_jobs'");
        Assert.NotNull(tableSql);
        Assert.Contains("AUTOINCREMENT", tableSql, StringComparison.Ordinal);
        Assert.Contains("job_id", tableSql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE", tableSql, StringComparison.Ordinal);

        // AUTOINCREMENT tables register in sqlite_sequence once a row exists.
        var tracked = await ScalarText(connection,
            "SELECT name FROM sqlite_sequence WHERE name='backwave_jobs'");
        Assert.Equal("backwave_jobs", tracked);
    }

    [Fact]
    public async Task Schema_version_mismatch_fail_stops_the_store()
    {
        await using var temp = TempSqliteStore.Create();
        // Migrate via a first op, then corrupt the recorded version.
        await temp.Store.EnqueueAsync(new NewJob(Guid.NewGuid(), "demo", default, "default", T0), T0);

        await using (var connection = new SqliteConnection($"Data Source={temp.Path}"))
        {
            await connection.OpenAsync();
            await using var bump = connection.CreateCommand();
            bump.CommandText = "UPDATE backwave_schema_version SET version = 999";
            await bump.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var stale = new SqliteJobStore(new SqliteStoreOptions
        {
            ConnectionString = $"Data Source={temp.Path}",
            AutoMigrate = false,
        });
        await using (stale)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await stale.ClaimAsync(new ClaimRequest("w", ["default"], 1, TimeSpan.FromMinutes(1), T0)));
        }
    }

    [Theory]
    [InlineData("3.45.1", true)]
    [InlineData("3.35.0", true)]
    [InlineData("3.34.99", false)]
    [InlineData("3.7.17", false)]
    public void Engine_floor_is_3_35(string reported, bool acceptable)
    {
        Assert.True(SqliteMigrator.TryParseEngineVersion(reported, out var version));
        Assert.Equal(acceptable, version >= SqliteMigrator.MinimumEngineVersion);
    }

    private static async Task<string?> ScalarText(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
