using System.Reflection;
using BackWave.Postgres;
using BackWave.Sqlite;
using BackWave.SqlServer;
using BackWave.Storage;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace BackWave.Torture;

/// <summary>
/// One relational adapter under the in-place upgrade harness (issue 0202). Knows how to reset the
/// schema to empty, apply a PREFIX of the shipped schema scripts to reach an older version N-1,
/// run raw parameterized DDL/DML (for direct-SQL fixture population and the reserved-word quoting
/// each dialect needs), run the REAL production migration to current, and hand out a current-build
/// store for the live workload / drain / audit. SQLite is deliberately absent — it ships a single
/// consolidated v1 schema, so no vN-1 → vN in-place step exists to exercise yet.
/// </summary>
internal interface IUpgradeStore : IAsyncDisposable
{
    string Name { get; }

    /// <summary>The version this adapter build migrates to — the top of the shipped script prefix.</summary>
    int CurrentVersion { get; }

    /// <summary>Reachability + create-database-if-missing. Throws with a hint when the docker service is down.</summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Drops every BackWave object so the next prior-version fixture starts from bare metal.</summary>
    ValueTask ResetSchemaAsync(CancellationToken cancellationToken);

    /// <summary>Applies schema scripts 0001..000{throughVersion} only — the real migrator's own enumeration, truncated.</summary>
    ValueTask PrefixMigrateAsync(int throughVersion, CancellationToken cancellationToken);

    /// <summary>Runs the REAL production migration (every script, idempotently) up to <see cref="CurrentVersion"/>.</summary>
    ValueTask MigrateToCurrentAsync(CancellationToken cancellationToken);

    /// <summary>The deployed schema version stamp (for a post-prefix sanity check).</summary>
    ValueTask<int> ReadSchemaVersionAsync(CancellationToken cancellationToken);

    /// <summary>Runs one parameterized statement. Placeholders are <c>@p0, @p1, …</c>; null becomes SQL NULL.</summary>
    ValueTask ExecuteAsync(string sql, object?[] parameters, CancellationToken cancellationToken);

    /// <summary>Brackets an identifier for this dialect (SQL Server reserved words) — bare for Postgres.</summary>
    string Quote(string identifier);

    IJobStore CreateStore();

    /// <summary>
    /// Disposes every store handed out by <see cref="CreateStore"/> since the last release and frees
    /// their pooled connections. Called at the end of each prior-version iteration so the per-iteration
    /// fleets do not accumulate and exhaust the database's connection ceiling across the full sweep.
    /// </summary>
    ValueTask ReleaseStoresAsync();

    bool IsTransientFault(Exception exception);

    ValueTask<IReadOnlyList<TortureViolation>> RawAuditAsync(CancellationToken cancellationToken);
}

/// <summary>Shared enumeration of an adapter's embedded schema scripts, in the migrator's Ordinal order.</summary>
internal static class UpgradeScripts
{
    public static IReadOnlyList<string> Ordered(Assembly assembly)
        => assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    public static string Read(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

internal sealed class PostgresUpgradeStore : IUpgradeStore
{
    private const string Database = "backwave_upgrade";

    private static readonly string BaseConnectionString =
        Environment.GetEnvironmentVariable("BACKWAVE_POSTGRES_DSN")
        ?? "Host=localhost;Port=5499;Username=backwave;Password=backwave;Database=backwave_test";

    private static readonly string ConnectionString =
        new NpgsqlConnectionStringBuilder(BaseConnectionString) { Database = Database }.ConnectionString;

    private readonly List<PostgresJobStore> _stores = [];

    public string Name => "postgres";

    public int CurrentVersion => PostgresMigrator.ExpectedSchemaVersion;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var admin = NpgsqlDataSource.Create(BaseConnectionString);
            await using var exists = admin.CreateCommand($"SELECT 1 FROM pg_database WHERE datname = '{Database}'");
            if (await exists.ExecuteScalarAsync(cancellationToken) is null)
            {
                await using var create = admin.CreateCommand($"CREATE DATABASE {Database}");
                await create.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (NpgsqlException exception)
        {
            throw new InvalidOperationException(
                "Postgres is not reachable. Start it with: docker compose up -d postgres", exception);
        }
    }

    public async ValueTask ResetSchemaAsync(CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var drop = dataSource.CreateCommand("DROP SCHEMA IF EXISTS backwave CASCADE");
        await drop.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask PrefixMigrateAsync(int throughVersion, CancellationToken cancellationToken)
    {
        var assembly = typeof(PostgresMigrator).Assembly;
        var scripts = UpgradeScripts.Ordered(assembly).Take(throughVersion).ToList();
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        foreach (var script in scripts)
        {
            await using var command = new NpgsqlCommand(UpgradeScripts.Read(assembly, script), connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async ValueTask MigrateToCurrentAsync(CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await PostgresMigrator.MigrateAsync(dataSource, cancellationToken);
    }

    public async ValueTask<int> ReadSchemaVersionAsync(CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var command = dataSource.CreateCommand("SELECT version FROM backwave.schema_version LIMIT 1");
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async ValueTask ExecuteAsync(string sql, object?[] parameters, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        for (var i = 0; i < parameters.Length; i++)
        {
            command.Parameters.AddWithValue($"p{i}", parameters[i] ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public string Quote(string identifier) => identifier;

    public IJobStore CreateStore()
    {
        // Bound the pool and prune idle connections fast (as the conformance harness does) so a store
        // left pooled between operations releases its connections quickly rather than pinning the DB's
        // connection budget across the whole sweep.
        var bounded = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            MaxPoolSize = 4,
            ConnectionIdleLifetime = 1,
            ConnectionPruningInterval = 1,
        }.ConnectionString;
        var store = new PostgresJobStore(new PostgresStoreOptions { ConnectionString = bounded });
        _stores.Add(store);
        return store;
    }

    public async ValueTask ReleaseStoresAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        _stores.Clear();
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

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }
    }
}

internal sealed class SqlServerUpgradeStore : IUpgradeStore
{
    private const string Database = "backwave_upgrade";

    private static readonly string MasterConnectionString =
        Environment.GetEnvironmentVariable("BACKWAVE_SQLSERVER_MASTER_DSN")
        ?? "Server=localhost,14330;User Id=sa;Password=BackWave!Passw0rd;TrustServerCertificate=true;Database=master";

    private static readonly string ConnectionString =
        new SqlConnectionStringBuilder(MasterConnectionString) { InitialCatalog = Database }.ConnectionString;

    // Drops every BackWave object, children before parents so foreign keys never block the drop.
    private const string ResetSql = """
        IF OBJECT_ID('backwave.workflow_edges', 'U') IS NOT NULL DROP TABLE backwave.workflow_edges;
        IF OBJECT_ID('backwave.job_tags', 'U') IS NOT NULL DROP TABLE backwave.job_tags;
        IF OBJECT_ID('backwave.job_transitions', 'U') IS NOT NULL DROP TABLE backwave.job_transitions;
        IF OBJECT_ID('backwave.job_parents', 'U') IS NOT NULL DROP TABLE backwave.job_parents;
        IF OBJECT_ID('backwave.observer_deliveries', 'U') IS NOT NULL DROP TABLE backwave.observer_deliveries;
        IF OBJECT_ID('backwave.observer_dead_letters', 'U') IS NOT NULL DROP TABLE backwave.observer_dead_letters;
        IF OBJECT_ID('backwave.observers', 'U') IS NOT NULL DROP TABLE backwave.observers;
        IF OBJECT_ID('backwave.workflows', 'U') IS NOT NULL DROP TABLE backwave.workflows;
        IF OBJECT_ID('backwave.jobs', 'U') IS NOT NULL DROP TABLE backwave.jobs;
        IF OBJECT_ID('backwave.schedules', 'U') IS NOT NULL DROP TABLE backwave.schedules;
        IF OBJECT_ID('backwave.queue_limits', 'U') IS NOT NULL DROP TABLE backwave.queue_limits;
        IF OBJECT_ID('backwave.operator_audit', 'U') IS NOT NULL DROP TABLE backwave.operator_audit;
        IF OBJECT_ID('backwave.schema_version', 'U') IS NOT NULL DROP TABLE backwave.schema_version;
        IF EXISTS (SELECT 1 FROM sys.sequences s JOIN sys.schemas c ON s.schema_id = c.schema_id
                   WHERE c.name = 'backwave' AND s.name = 'observer_log_position')
            DROP SEQUENCE backwave.observer_log_position;
        IF EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'backwave') DROP SCHEMA backwave;
        """;

    public string Name => "sqlserver";

    public int CurrentVersion => SqlServerMigrator.ExpectedSchemaVersion;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var master = new SqlConnection(MasterConnectionString);
            await master.OpenAsync(cancellationToken);
            await using var create = new SqlCommand($"IF DB_ID('{Database}') IS NULL CREATE DATABASE [{Database}]", master);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception)
        {
            throw new InvalidOperationException(
                "SQL Server is not reachable. Start it with: docker compose up -d sqlserver", exception);
        }
    }

    public async ValueTask ResetSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(ResetSql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask PrefixMigrateAsync(int throughVersion, CancellationToken cancellationToken)
    {
        var assembly = typeof(SqlServerMigrator).Assembly;
        var scripts = UpgradeScripts.Ordered(assembly).Take(throughVersion).ToList();
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var script in scripts)
        {
            await using var command = new SqlCommand(UpgradeScripts.Read(assembly, script), connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public ValueTask MigrateToCurrentAsync(CancellationToken cancellationToken)
        => new(SqlServerMigrator.MigrateAsync(ConnectionString, cancellationToken));

    public async ValueTask<int> ReadSchemaVersionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT TOP 1 version FROM backwave.schema_version", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async ValueTask ExecuteAsync(string sql, object?[] parameters, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        for (var i = 0; i < parameters.Length; i++)
        {
            command.Parameters.AddWithValue($"@p{i}", parameters[i] ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public string Quote(string identifier) => $"[{identifier}]";

    public IJobStore CreateStore() => new SqlServerJobStore(new SqlServerStoreOptions { ConnectionString = ConnectionString });

    public bool IsTransientFault(Exception exception) => exception switch
    {
        SqlException sql => sql.Errors.Cast<SqlError>().Any(e =>
            e.Number is 1205 or -2 or 121 or 233 or 10053 or 10054 or 10060 or 40197 or 40501 or 40613),
        TimeoutException => true,
        _ => false,
    };

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

    public ValueTask ReleaseStoresAsync()
    {
        // SQL Server stores share the process-wide SqlClient pool keyed by connection string; clearing
        // it frees the connections this iteration's fleet opened before the next iteration starts.
        SqlConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }
}
