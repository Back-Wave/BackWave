using System.Data;
using BackWave.Oracle;
using BackWave.Storage;
using Oracle.ManagedDataAccess.Client;

namespace BackWave.Benchmarks.Targets;

/// <summary>
/// BackWave over a real Oracle Storage Adapter: drives the real pump against a real database, never the
/// In-Memory Store (which runs on Virtual Time and has no wall-clock throughput to measure).
/// All the run machinery is inherited from <see cref="BackWaveTarget"/>; this type only knows how to
/// migrate, build, version, and wipe an Oracle database. It runs the same tuning as every other adapter
/// target - no Oracle-only dial, or the number stops being comparable with the Postgres and SQL Server
/// cells. There is deliberately no Hangfire Oracle counterpart: Hangfire ships no first-party Oracle
/// storage, so Oracle is a BackWave-only cell.
/// </summary>
public sealed class OracleBenchmarkTarget : BackWaveTarget
{
    /// <summary>Environment variable holding the Oracle connection string (DSN).</summary>
    public const string ConnectionStringEnvVar = "BACKWAVE_ORACLE_DSN";

    /// <summary>
    /// Environment variable holding a privileged DSN used only by the connection sampler. Oracle exposes the
    /// session list through <c>v$session</c>, which an application user cannot read, so unlike Postgres and
    /// SQL Server the peak-connection probe has to log in as an administrator. It touches nothing else.
    /// Leave it unset and the peak-connections metric is simply omitted; there is no default.
    /// </summary>
    public const string ProbeConnectionStringEnvVar = "BACKWAVE_ORACLE_SYSTEM_DSN";

    private const string DefaultConnectionString =
        "User Id=backwave;Password=backwave;Data Source=localhost:15210/FREEPDB1;";

    // FK-safe wipe order: job_parents references jobs without ON DELETE CASCADE, so children go first.
    // ODP.NET sends one statement per round-trip, so the DELETEs cannot be semicolon-batched into one
    // command text the way the SQL Server reset batches them - they go in a single anonymous PL/SQL block.
    private const string WipeBlock =
        """
        BEGIN
            DELETE FROM backwave.workflow_edges;
            DELETE FROM backwave.job_parents;
            DELETE FROM backwave.job_tags;
            DELETE FROM backwave.job_transitions;
            DELETE FROM backwave.observer_dead_letters;
            DELETE FROM backwave.observer_deliveries;
            DELETE FROM backwave.observers;
            DELETE FROM backwave.jobs;
            DELETE FROM backwave.workflows;
            DELETE FROM backwave.schedules;
            DELETE FROM backwave.queue_limits;
            DELETE FROM backwave.queue_locks;
            DELETE FROM backwave.operator_audit;
        END;
        """;

    private readonly string _connectionString;
    private readonly string _appUser;

    // Null unless the operator supplies an administrator DSN. There is no sensible default to fall back on:
    // guessing one would ship a privileged credential in source and fire a real SYSTEM login at whatever
    // database the run happens to point at, which is how you trip an account lockout or an audit alarm.
    private readonly string? _probeConnectionString;

    // One persistent connection for the in-window connection sampler, opened once and reused - never one per
    // poll, for the same reason as the SQL Server probe: the sampler runs every 20ms for the whole timed
    // window, so a fresh OpenAsync per poll churns a pool checkout into the very window it measures. Migrate,
    // version, and reset keep their own short-lived connections - they run outside the window.
    private OracleConnection? _probe;
    private bool _probeUnavailable;

    /// <summary>Creates the target using the DSN from the environment, or the local docker-compose default.</summary>
    public OracleBenchmarkTarget()
        : this(System.Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString)
    {
    }

    /// <summary>Creates the target against an explicit connection string.</summary>
    public OracleBenchmarkTarget(string connectionString)
    {
        _connectionString = connectionString;
        _appUser = new OracleConnectionStringBuilder(connectionString).UserID;
        var probeConnectionString = System.Environment.GetEnvironmentVariable(ProbeConnectionStringEnvVar);
        _probeConnectionString = string.IsNullOrWhiteSpace(probeConnectionString) ? null : probeConnectionString;
    }

    /// <inheritdoc/>
    public override string Name => "BackWave/Oracle";

    /// <inheritdoc/>
    public override string Engine => "Oracle";

    // Oracle rejects ROWNUM (or FETCH) together with FOR UPDATE at one query level, so the top-N candidate
    // set is bounded in an inner subquery and locked in the outer one. Surfaced verbatim, like the siblings.
    /// <inheritdoc/>
    protected override string ClaimStrategy => "ROWNUM-bounded subquery, FOR UPDATE SKIP LOCKED";

    /// <inheritdoc/>
    protected override Task MigrateAsync(CancellationToken cancellationToken)
        => OracleMigrator.MigrateAsync(_connectionString, cancellationToken);

    /// <inheritdoc/>
    protected override IJobStore CreateStore()
        => new OracleJobStore(new OracleStoreOptions { ConnectionString = _connectionString });

    /// <inheritdoc/>
    protected override async Task<string> ReadEngineVersionAsync(CancellationToken cancellationToken)
    {
        // v$version and v$instance are administrator-only; product_component_version is readable by any user.
        await using var connection = new OracleConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version_full FROM product_component_version WHERE rownum = 1";
        var version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return version?.ToString() ?? "unknown";
    }

    /// <inheritdoc/>
    protected override async Task<int> SampleConnectionCountAsync(CancellationToken cancellationToken)
    {
        // Counts this process's server-side sessions, defined the same way as the Postgres and SQL Server
        // probes so it stays a fair cross-system metric. The probe logs in as an administrator (v$session is
        // not visible to the application user), which also means it is a session of a DIFFERENT user and so
        // is never counted by the filter - the equivalent of excluding pg_backend_pid()/@@SPID there.
        if (_probeUnavailable)
        {
            return 0;
        }

        if (_probeConnectionString is null)
        {
            MarkProbeUnavailable("not set");
            return 0;
        }

        _probe ??= new OracleConnection(_probeConnectionString);
        if (_probe.State != ConnectionState.Open)
        {
            try
            {
                await _probe.OpenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OracleException failure)
            {
                MarkProbeUnavailable(failure.Message.Split('\n')[0]);
                return 0;
            }
        }

        await using var command = _probe.CreateCommand();
        command.CommandText = "SELECT count(*) FROM v$session WHERE username = :app_user";
        command.Parameters.Add(new OracleParameter("app_user", _appUser.ToUpperInvariant()));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is decimal count ? (int)count : 0;
    }

    // Without an administrator login the peak-connection cell simply has no number, exactly as for an adapter
    // that does not override the base sampler. Say so once rather than reporting a silent zero as if it were
    // measured, and name the variable that turns the probe on.
    private void MarkProbeUnavailable(string reason)
    {
        _probeUnavailable = true;
        Console.Error.WriteLine(
            "peak-connections unavailable: the sampler needs an administrator DSN in " +
            $"${ProbeConnectionStringEnvVar} to read v$session ({reason})");
    }

    /// <inheritdoc/>
    protected override async Task ResetStoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = new OracleConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = WipeBlock;
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
