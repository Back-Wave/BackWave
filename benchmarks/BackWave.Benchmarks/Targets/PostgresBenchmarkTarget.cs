using BackWave.Postgres;
using BackWave.Storage;
using Npgsql;

namespace BackWave.Benchmarks.Targets;

/// <summary>
/// BackWave over a real Postgres Storage Adapter: drives the real pump against a real database, never the
/// In-Memory Store (which runs on Virtual Time and has no wall-clock throughput to measure, ADR 0027 §6).
/// All the run machinery is inherited from <see cref="BackWaveTarget"/>; this type only knows how to
/// migrate, build, version, and truncate a Postgres database.
/// </summary>
public sealed class PostgresBenchmarkTarget : BackWaveTarget
{
    /// <summary>Environment variable holding the Postgres connection string (DSN).</summary>
    public const string ConnectionStringEnvVar = "BACKWAVE_POSTGRES_DSN";

    private const string DefaultConnectionString =
        "Host=localhost;Port=5499;Username=backwave;Password=backwave;Database=backwave_test";

    private readonly string _connectionString;

    // One data source for the harness's own probes (migrate, version, reset, the connection sampler), built
    // once and reused — never one per call. The connection sampler runs every 20ms for the whole timed
    // window, so a fresh NpgsqlDataSource.Create per poll would spin up a new pool + physical connection
    // (TCP/TLS/auth) each time, perturbing the very window it measures. This is separate from the pump's own
    // store (CreateStore) so the probe never borrows from the engine's connection pool.
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the target using the DSN from the environment, or the local docker-compose default.</summary>
    public PostgresBenchmarkTarget()
        : this(System.Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString)
    {
    }

    /// <summary>Creates the target against an explicit connection string.</summary>
    public PostgresBenchmarkTarget(string connectionString)
    {
        _connectionString = connectionString;
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    /// <inheritdoc/>
    public override string Name => "BackWave/Postgres";

    /// <inheritdoc/>
    public override string Engine => "PostgreSQL";

    /// <inheritdoc/>
    protected override string ClaimStrategy => "FOR UPDATE SKIP LOCKED";

    /// <inheritdoc/>
    protected override Task MigrateAsync(CancellationToken cancellationToken)
        => PostgresMigrator.MigrateAsync(_dataSource, cancellationToken);

    /// <inheritdoc/>
    protected override IJobStore CreateStore()
        => new PostgresJobStore(new PostgresStoreOptions { ConnectionString = _connectionString });

    /// <inheritdoc/>
    protected override async Task<string> ReadEngineVersionAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("SHOW server_version");
        var version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return version?.ToString() ?? "unknown";
    }

    /// <inheritdoc/>
    protected override async Task<int> SampleConnectionCountAsync(CancellationToken cancellationToken)
    {
        // pg_backend_pid() is the probe's own backend, excluded so the sampler never counts itself —
        // everything else against this database is a connection the benchmark process is actually holding.
        // The shared data source reuses a single pooled backend across the serial poll loop, and that backend
        // is always the one running this count (so it is always the excluded pg_backend_pid): the probe never
        // adds a counted connection, so reusing the data source leaves PeakConnections unperturbed.
        await using var command = _dataSource.CreateCommand(
            "SELECT count(*) FROM pg_stat_activity " +
            "WHERE datname = current_database() AND pid <> pg_backend_pid()");
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long count ? (int)count : 0;
    }

    /// <inheritdoc/>
    protected override async Task ResetStoreAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "TRUNCATE backwave.jobs, backwave.job_parents, backwave.job_tags, backwave.schedules, " +
            "backwave.queue_limits, backwave.operator_audit, backwave.observers, " +
            "backwave.observer_deliveries, backwave.observer_dead_letters, " +
            "backwave.workflows, backwave.workflow_edges RESTART IDENTITY CASCADE");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        await _dataSource.DisposeAsync().ConfigureAwait(false);
    }
}
