using System.Data;
using BackWave.SqlServer;
using BackWave.Storage;
using Microsoft.Data.SqlClient;

namespace BackWave.Benchmarks.Targets;

/// <summary>
/// BackWave over a real SQL Server Storage Adapter: drives the real pump against a real database, never the
/// In-Memory Store (which runs on Virtual Time and has no wall-clock throughput to measure, ADR 0027 §6).
/// All the run machinery is inherited from <see cref="BackWaveTarget"/>; this type only knows how to
/// migrate, build, version, and wipe a SQL Server database. This is the adapter that carries the published
/// headline (ADR 0027 §5) — but only ever from a native-x86-64 official run, never the Rosetta docker box.
/// </summary>
public sealed class SqlServerBenchmarkTarget : BackWaveTarget
{
    /// <summary>Environment variable holding the SQL Server connection string (DSN).</summary>
    public const string ConnectionStringEnvVar = "BACKWAVE_SQLSERVER_DSN";

    private const string DefaultConnectionString =
        "Server=localhost,14330;User Id=sa;Password=BackWave!Passw0rd;TrustServerCertificate=true;Database=backwave_test";

    private readonly string _connectionString;

    // One persistent connection for the in-window connection sampler, opened once and reused — never one per
    // poll. The sampler runs every 20ms for the whole timed window, so a fresh SqlConnection.OpenAsync per
    // poll churns a pool checkout + sp_reset_connection round-trip into the very window it measures. Migrate,
    // version, and reset keep their own short-lived connections — they run outside the window, so their churn
    // never touches a measurement.
    private SqlConnection? _probe;

    /// <summary>Creates the target using the DSN from the environment, or the local docker-compose default.</summary>
    public SqlServerBenchmarkTarget()
        : this(System.Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString)
    {
    }

    /// <summary>Creates the target against an explicit connection string.</summary>
    public SqlServerBenchmarkTarget(string connectionString)
        => _connectionString = connectionString;

    /// <inheritdoc/>
    public override string Name => "BackWave/SqlServer";

    /// <inheritdoc/>
    public override string Engine => "SQL Server";

    /// <inheritdoc/>
    protected override string ClaimStrategy => "UPDLOCK, READPAST, ROWLOCK";

    /// <inheritdoc/>
    protected override Task MigrateAsync(CancellationToken cancellationToken)
        // Unlike Postgres (a datasource), the SQL Server migrator takes the connection string directly.
        => SqlServerMigrator.MigrateAsync(_connectionString, cancellationToken);

    /// <inheritdoc/>
    protected override IJobStore CreateStore()
        => new SqlServerJobStore(new SqlServerStoreOptions { ConnectionString = _connectionString });

    /// <inheritdoc/>
    protected override async Task<string> ReadEngineVersionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128))", connection);
        var version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return version?.ToString() ?? "unknown";
    }

    /// <inheritdoc/>
    protected override async Task<int> SampleConnectionCountAsync(CancellationToken cancellationToken)
    {
        // @@SPID is the probe's own session, excluded so the sampler never counts itself — everything else
        // against this database is a connection the benchmark process is actually holding. Defined the same
        // way as the Postgres probe (count this process's server-side sessions), keeping it cross-system fair.
        // The persistent probe is always the @@SPID running this count, so it is always the excluded session:
        // reusing it across the serial poll loop leaves PeakConnections unperturbed while removing the churn.
        _probe ??= new SqlConnection(_connectionString);
        if (_probe.State != ConnectionState.Open)
        {
            await _probe.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = new SqlCommand(
            "SELECT count(*) FROM sys.dm_exec_sessions WHERE database_id = DB_ID() AND session_id <> @@SPID",
            _probe);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is int count ? count : 0;
    }

    /// <inheritdoc/>
    protected override async Task ResetStoreAsync(CancellationToken cancellationToken)
    {
        // SQL Server TRUNCATE can't touch a table referenced by an enabled FK, so wipe with DELETE in FK
        // order (child rows first) — the same approach the SqlServer conformance suite uses between tests.
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(
            "DELETE FROM backwave.workflow_edges; DELETE FROM backwave.job_parents; " +
            "DELETE FROM backwave.job_tags; DELETE FROM backwave.jobs; DELETE FROM backwave.workflows; " +
            "DELETE FROM backwave.schedules; DELETE FROM backwave.queue_limits; " +
            "DELETE FROM backwave.operator_audit; DELETE FROM backwave.observers; " +
            "DELETE FROM backwave.observer_deliveries; DELETE FROM backwave.observer_dead_letters;",
            connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        if (_probe is not null)
        {
            await _probe.DisposeAsync().ConfigureAwait(false);
        }
    }
}
