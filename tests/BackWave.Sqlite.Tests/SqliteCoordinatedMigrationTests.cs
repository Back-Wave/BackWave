using BackWave.Sqlite.Internal;
using Microsoft.Data.Sqlite;

namespace BackWave.Sqlite.Tests;

/// <summary>
/// Coordinated migration (ADR 0046): several co-resident processes opening the same fresh SQLite file
/// with AutoMigrate on must be safe under true concurrency — exactly one applies the schema while the
/// rest block on the single-writer write lock, re-check, and no-op. There is no distributed SQLite, so
/// the guarantee is per-host; the file's reserved write lock IS the coordination. Proven by a
/// concurrent-boot storm against a fresh file (teeth) and a storm against an already-current file
/// (steady state).
/// </summary>
public sealed class SqliteCoordinatedMigrationTests
{
    private const int Fleet = 8;

    // Fires K migrators against one connection string as simultaneously as possible (a barrier
    // releases them together, each on its own connection opened inside MigrateAsync) and returns each
    // task's outcome — null on success, the thrown exception otherwise.
    private static async Task<Exception?[]> StormAsync(string connectionString)
    {
        using var barrier = new Barrier(Fleet);
        var tasks = Enumerable.Range(0, Fleet).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            try
            {
                await SqliteMigrator.MigrateAsync(connectionString);
                return (Exception?)null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        })).ToArray();

        return await Task.WhenAll(tasks);
    }

    private static async Task<(long RowCount, long? Version)> InspectSchemaVersionAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM backwave_schema_version";
        var rowCount = (long)(await count.ExecuteScalarAsync())!;

        long? version = null;
        if (rowCount > 0)
        {
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT version FROM backwave_schema_version LIMIT 1";
            version = (long)(await read.ExecuteScalarAsync())!;
        }

        return (rowCount, version);
    }

    [Fact]
    public async Task ConcurrentFirstMigration_AllSucceed_ExactlyOneSchemaVersionRow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"backwave_sqlite_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path}";
        try
        {
            var outcomes = await StormAsync(connectionString);

            Assert.All(outcomes, outcome => Assert.Null(outcome));
            var (rowCount, version) = await InspectSchemaVersionAsync(connectionString);
            Assert.Equal(1, rowCount);
            Assert.Equal(SqliteMigrator.ExpectedSchemaVersion, version);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task ConcurrentBootAgainstCurrentFile_AllNoOp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"backwave_sqlite_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path}";
        try
        {
            await SqliteMigrator.MigrateAsync(connectionString);

            // Every migrator in this storm should take the unlocked pre-check path and no-op — no
            // error, no second migration.
            var outcomes = await StormAsync(connectionString);

            Assert.All(outcomes, outcome => Assert.Null(outcome));
            var (rowCount, version) = await InspectSchemaVersionAsync(connectionString);
            Assert.Equal(1, rowCount);
            Assert.Equal(SqliteMigrator.ExpectedSchemaVersion, version);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Migration_DoesNotLeakBusyTimeout_IntoThePooledConnection()
    {
        // Coordinated migration raises PRAGMA busy_timeout to a 30s budget so a co-resident migrator
        // BLOCKS on the write lock instead of erroring. busy_timeout is native, connection-local state
        // that SQLite carries back into the pool, and the store forces Pooling=true on one shared pool,
        // so if migration pooled its connection it would hand that 30s timeout to the next runtime
        // claim/reap — stretching every contended write far past the configured BusyTimeout. Migration
        // must therefore run non-pooled: a fresh pooled connection carries no migration state.
        var path = Path.Combine(Path.GetTempPath(), $"backwave_sqlite_{Guid.NewGuid():N}.db");
        var connectionString = SqliteConnectionStringNormalizer.Normalize(
            $"Data Source={path}", TimeSpan.FromMilliseconds(250));
        try
        {
            await SqliteMigrator.MigrateAsync(connectionString);

            // Open exactly as the store's runtime OpenAsync does — same pooled connection string.
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var probe = connection.CreateCommand();
            probe.CommandText = "PRAGMA busy_timeout";
            var busyTimeout = (long)(await probe.ExecuteScalarAsync())!;

            // SQLite's default (0) — never the migration budget. A leaked value would silently gate
            // every contended runtime write on the migrator's 30s wait.
            Assert.Equal(0, busyTimeout);
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static void Cleanup(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(path + suffix); } catch (IOException) { }
        }
    }
}
