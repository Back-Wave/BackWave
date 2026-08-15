using System.Text.Json;
using BackWave.Postgres;
using BackWave.Sqlite;
using BackWave.SqlServer;
using BackWave.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace BackWave.Torture;

/// <summary>
/// One adapter shape under torture: knows how to provision the database, hand out fresh store
/// instances (one per synthetic client — each with its own connections, so contention is real),
/// classify transient faults, run the raw-row audits the store surface can't see, and dump raw
/// state into the artifact bundle.
/// </summary>
internal interface ITortureTarget : IAsyncDisposable
{
    string Name { get; }

    /// <summary>Reachability check + create-database-if-missing + migrate + wipe. Throws with a hint when the docker service is down.</summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    IJobStore CreateStore();

    /// <summary>True when the exception is legitimate contention noise (deadlock victim, busy, timeout) rather than a contract violation.</summary>
    bool IsTransientFault(Exception exception);

    /// <summary>Raw-row checks below the store surface — duplicate tag/edge rows are invisible through the set-typed reads.</summary>
    ValueTask<IReadOnlyList<TortureViolation>> RawAuditAsync(CancellationToken cancellationToken);

    /// <summary>Dumps raw table contents (or the raw database file) into <paramref name="dir"/> for the artifact bundle.</summary>
    ValueTask RawDumpAsync(string dir, CancellationToken cancellationToken);
}

internal sealed class PostgresTarget : ITortureTarget
{
    private const string Database = "backwave_torture";

    private static readonly string BaseConnectionString =
        Environment.GetEnvironmentVariable("BACKWAVE_POSTGRES_DSN")
        ?? "Host=localhost;Port=5499;Username=backwave;Password=backwave;Database=backwave_test";

    private static readonly string ConnectionString =
        new NpgsqlConnectionStringBuilder(BaseConnectionString) { Database = Database }.ConnectionString;

    private readonly List<PostgresJobStore> _stores = [];

    public string Name => "postgres";

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The base DSN's database is known to exist (the conformance one); use it to create ours.
            await using (var admin = NpgsqlDataSource.Create(BaseConnectionString))
            {
                await using var exists = admin.CreateCommand($"SELECT 1 FROM pg_database WHERE datname = '{Database}'");
                if (await exists.ExecuteScalarAsync(cancellationToken) is null)
                {
                    await using var create = admin.CreateCommand($"CREATE DATABASE {Database}");
                    await create.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
            await PostgresMigrator.MigrateAsync(dataSource, cancellationToken);
            await using var truncate = dataSource.CreateCommand(
                "TRUNCATE backwave.jobs, backwave.job_parents, backwave.job_tags, backwave.schedules, backwave.queue_limits, " +
                "backwave.operator_audit, backwave.observers, backwave.observer_deliveries, " +
                "backwave.observer_dead_letters, backwave.workflows, backwave.workflow_edges " +
                "RESTART IDENTITY CASCADE");
            await truncate.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (NpgsqlException exception)
        {
            throw new InvalidOperationException(
                "Postgres is not reachable. Start it with: docker compose up -d postgres", exception);
        }
    }

    public IJobStore CreateStore()
    {
        var bounded = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            MaxPoolSize = 3,
            ConnectionIdleLifetime = 5,
            ConnectionPruningInterval = 2,
        }.ConnectionString;
        var store = new PostgresJobStore(new PostgresStoreOptions { ConnectionString = bounded });
        _stores.Add(store);
        return store;
    }

    public bool IsTransientFault(Exception exception) => exception switch
    {
        PostgresException pg => pg.SqlState is "40001" or "40P01" or "55P03" or "53300" or "57P03",
        NpgsqlException npg => npg.IsTransient || npg.InnerException is TimeoutException or IOException,
        TimeoutException => true,
        _ => false,
    };

    public async ValueTask<IReadOnlyList<TortureViolation>> RawAuditAsync(CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        var violations = new List<TortureViolation>();
        await RelationalRawAudit.RunAsync(
            violations,
            async sql =>
            {
                await using var command = dataSource.CreateCommand(sql);
                return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
            });
        return violations;
    }

    public async ValueTask RawDumpAsync(string dir, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var tables = dataSource.CreateCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'backwave' ORDER BY table_name");
        var names = new List<string>();
        await using (var reader = await tables.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                names.Add(reader.GetString(0));
            }
        }
        foreach (var table in names)
        {
            await using var rows = dataSource.CreateCommand(
                $"SELECT COALESCE(json_agg(t), '[]'::json)::text FROM backwave.\"{table}\" t");
            var json = (string)(await rows.ExecuteScalarAsync(cancellationToken))!;
            await File.WriteAllTextAsync(Path.Combine(dir, $"table-{table}.json"), json, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }
    }
}

internal sealed class SqlServerTarget : ITortureTarget
{
    private const string Database = "backwave_torture";

    private static readonly string MasterConnectionString =
        Environment.GetEnvironmentVariable("BACKWAVE_SQLSERVER_MASTER_DSN")
        ?? "Server=localhost,14330;User Id=sa;Password=BackWave!Passw0rd;TrustServerCertificate=true;Database=master";

    private static readonly string ConnectionString =
        new SqlConnectionStringBuilder(MasterConnectionString) { InitialCatalog = Database }.ConnectionString;

    public string Name => "sqlserver";

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using (var master = new SqlConnection(MasterConnectionString))
            {
                await master.OpenAsync(cancellationToken);
                await using var create = new SqlCommand(
                    $"IF DB_ID('{Database}') IS NULL CREATE DATABASE [{Database}]", master);
                await create.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (SqlException exception)
        {
            throw new InvalidOperationException(
                "SQL Server is not reachable. Start it with: docker compose up -d sqlserver", exception);
        }

        await SqlServerMigrator.MigrateAsync(ConnectionString, cancellationToken);

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var wipe = new SqlCommand(
            "DELETE FROM backwave.workflow_edges; DELETE FROM backwave.job_parents; " +
            "DELETE FROM backwave.job_tags; DELETE FROM backwave.jobs; DELETE FROM backwave.workflows; " +
            "DELETE FROM backwave.schedules; DELETE FROM backwave.queue_limits; " +
            "DELETE FROM backwave.operator_audit; DELETE FROM backwave.observers; " +
            "DELETE FROM backwave.observer_deliveries; DELETE FROM backwave.observer_dead_letters;",
            connection);
        await wipe.ExecuteNonQueryAsync(cancellationToken);
    }

    public IJobStore CreateStore()
        => new SqlServerJobStore(new SqlServerStoreOptions { ConnectionString = ConnectionString });

    public bool IsTransientFault(Exception exception) => exception switch
    {
        SqlException sql => HasNumber(sql, 1205, -2, 121, 233, 10053, 10054, 10060, 40197, 40501, 40613),
        TimeoutException => true,
        _ => false,
    };

    private static bool HasNumber(SqlException exception, params int[] numbers)
        => exception.Errors.Cast<SqlError>().Any(e => numbers.Contains(e.Number)) || numbers.Contains(exception.Number);

    public async ValueTask<IReadOnlyList<TortureViolation>> RawAuditAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var violations = new List<TortureViolation>();
        await RelationalRawAudit.RunAsync(
            violations,
            async sql =>
            {
                await using var command = new SqlCommand(sql, connection);
                return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
            },
            tagColumns: "job_id, [key], [value]");
        return violations;
    }

    public async ValueTask RawDumpAsync(string dir, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tables = new SqlCommand(
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'backwave' AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME",
            connection);
        var names = new List<string>();
        await using (var reader = await tables.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                names.Add(reader.GetString(0));
            }
        }
        foreach (var table in names)
        {
            await using var rows = new SqlCommand(
                $"SELECT (SELECT * FROM backwave.[{table}] FOR JSON PATH, INCLUDE_NULL_VALUES)", connection);
            var json = await rows.ExecuteScalarAsync(cancellationToken) as string ?? "[]";
            await File.WriteAllTextAsync(Path.Combine(dir, $"table-{table}.json"), json, cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }
}

internal sealed class SqliteTarget(string dbPath, bool migrate) : ITortureTarget
{
    private readonly List<SqliteJobStore> _stores = [];

    public string Name => "sqlite";

    public string DbPath { get; } = dbPath;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (!migrate)
        {
            return;
        }
        // Migrate up front on a throwaway store so concurrent clients (and child processes) never
        // race the migrator — they all open AutoMigrate=false against a ready file.
        var store = new SqliteJobStore(new SqliteStoreOptions
        {
            ConnectionString = $"Data Source={DbPath}",
            AutoMigrate = true,
        });
        await store.CountJobsAsync(cancellationToken);
        await store.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }

    public IJobStore CreateStore()
    {
        var store = new SqliteJobStore(new SqliteStoreOptions
        {
            ConnectionString = $"Data Source={DbPath}",
            AutoMigrate = false,
        });
        _stores.Add(store);
        return store;
    }

    public bool IsTransientFault(Exception exception)
        => _stores.Count > 0
            ? _stores[0].IsTransientFault(exception)
            : exception is SqliteException { SqliteErrorCode: 5 or 6 };

    public async ValueTask<IReadOnlyList<TortureViolation>> RawAuditAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync(cancellationToken);
        var violations = new List<TortureViolation>();
        await RelationalRawAudit.RunAsync(
            violations,
            async sql =>
            {
                await using var command = connection.CreateCommand();
                // SQLite has no schemas; its tables are backwave_-prefixed instead.
                command.CommandText = sql.Replace("backwave.", "backwave_", StringComparison.Ordinal);
                return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
            });
        return violations;
    }

    public async ValueTask RawDumpAsync(string dir, CancellationToken cancellationToken)
    {
        // Checkpoint the WAL so the copied main file is complete, then copy the raw database.
        await using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            await connection.OpenAsync(cancellationToken);
            await using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken);
        }
        SqliteConnection.ClearAllPools();
        File.Copy(DbPath, Path.Combine(dir, Path.GetFileName(DbPath)), overwrite: true);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        if (migrate) // the run owner deletes the file; child processes leave it alone
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    File.Delete(DbPath + suffix);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}

/// <summary>
/// The raw-row audit shared by every relational target: duplicate tag/edge rows (ADR 0039's
/// quiescent audit family) are invisible through the set-typed store reads, so they are counted
/// with SQL. With the shipped schemas the primary keys make duplicates impossible — which is
/// exactly what this asserts survives whatever the torture run did.
/// </summary>
internal static class RelationalRawAudit
{
    public static async Task RunAsync(
        List<TortureViolation> violations, Func<string, Task<long>> scalar, string tagColumns = "job_id, key, value")
    {
        var duplicateTags = await scalar(
            "SELECT COALESCE(SUM(n - 1), 0) FROM (SELECT COUNT(*) AS n FROM backwave.job_tags " +
            $"GROUP BY {tagColumns}) g WHERE n > 1");
        if (duplicateTags > 0)
        {
            violations.Add(new TortureViolation(
                TortureInvariant.DuplicateTagRows, $"{duplicateTags} duplicate (job_id, key, value) tag row(s)."));
        }

        var duplicateEdges = await scalar(
            "SELECT COALESCE(SUM(n - 1), 0) FROM (SELECT COUNT(*) AS n FROM backwave.workflow_edges " +
            "GROUP BY workflow_id, parent_id, child_id) g WHERE n > 1");
        if (duplicateEdges > 0)
        {
            violations.Add(new TortureViolation(
                TortureInvariant.DuplicateEdgeRows, $"{duplicateEdges} duplicate (workflow_id, parent_id, child_id) edge row(s)."));
        }
    }
}
