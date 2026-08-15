using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;

namespace BackWave.Benchmarks.Targets;

/// <summary>
/// Hangfire over SQL Server — the flagship head-to-head (ADR 0027 §5). Hangfire is first-party and
/// battle-tested here, so a win on SQL Server is the most credible result obtainable; this is the headline
/// comparison and both sides are tuned to their best honest config. The storage is configured with
/// Hangfire's own recommended high-throughput options (sliding invisibility, global locks off, recommended
/// isolation, command batching) and pointed at Microsoft.Data.SqlClient so the client stack matches BackWave's.
/// Like the BackWave headline, a published number must come from a native-x86-64 official run, never Rosetta.
/// </summary>
public sealed class HangfireSqlServerTarget : HangfireBenchmarkTarget
{
    /// <summary>Environment variable holding the Hangfire SQL Server connection string (DSN).</summary>
    public const string ConnectionStringEnvVar = "BACKWAVE_HANGFIRE_SQLSERVER_DSN";

    private const string SchemaName = "HangFire";
    private const string HangfireVersion = "1.8.23";

    private const string DefaultConnectionString =
        "Server=localhost,14330;User Id=sa;Password=BackWave!Passw0rd;TrustServerCertificate=true;Database=hangfire_test";

    private readonly string _connectionString;

    /// <summary>Creates the target using the DSN from the environment, or the local docker-compose default.</summary>
    public HangfireSqlServerTarget()
        : this(System.Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString)
    {
    }

    /// <summary>Creates the target against an explicit connection string.</summary>
    public HangfireSqlServerTarget(string connectionString)
        => _connectionString = connectionString;

    /// <inheritdoc/>
    public override string Name => "Hangfire/SqlServer";

    /// <inheritdoc/>
    public override string Engine => "SQL Server";

    /// <inheritdoc/>
    protected override JobStorage CreateStorage()
    {
        var options = new SqlServerStorageOptions
        {
            SchemaName = SchemaName,
            PrepareSchemaIfNecessary = true,
            // Tuned-to-best, per Hangfire's own high-throughput guidance: sliding invisibility (the fast,
            // non-transactional dequeue), zero poll interval, global locks disabled, the recommended
            // isolation level, and command batching. Every value is surfaced in StorageDials.
            QueuePollInterval = TimeSpan.Zero,
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
            DisableGlobalLocks = true,
            UseRecommendedIsolationLevel = true,
            CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
            // Match BackWave's client stack: Microsoft.Data.SqlClient, not the legacy System.Data.SqlClient.
            SqlClientFactory = SqlClientFactory.Instance,
        };
        return new SqlServerStorage(_connectionString, options);
    }

    /// <inheritdoc/>
    protected override IReadOnlyDictionary<string, string> StorageDials() => new Dictionary<string, string>
    {
        ["queue-poll-interval"] = "0ms (sliding invisibility, aggressive fetch)",
        ["fetch-mode"] = "sliding-invisibility, global locks disabled, recommended isolation (tuned-to-best)",
        ["command-batching"] = "CommandBatchMaxTimeout=5m",
        ["schema"] = SchemaName,
        ["hangfire-version"] = HangfireVersion,
        ["hangfire-adapter"] = $"Hangfire.SqlServer {HangfireVersion} (first-party)",
    };

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
        // Defined identically to the BackWave SQL Server probe (this process's server-side sessions, minus
        // @@SPID), so peak DB connections is a fair cross-system metric.
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(
            "SELECT count(*) FROM sys.dm_exec_sessions WHERE database_id = DB_ID() AND session_id <> @@SPID",
            connection);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is int count ? count : 0;
    }

    /// <inheritdoc/>
    protected override async Task ResetStoreAsync(CancellationToken cancellationToken)
    {
        // SQL Server TRUNCATE can't touch a table referenced by an enabled FK, so wipe with DELETE: child
        // tables (State, JobParameter, JobQueue) before Job, then the remaining counters/sets/servers.
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(
            $"DELETE FROM [{SchemaName}].[State]; DELETE FROM [{SchemaName}].[JobParameter]; " +
            $"DELETE FROM [{SchemaName}].[JobQueue]; DELETE FROM [{SchemaName}].[Job]; " +
            $"DELETE FROM [{SchemaName}].[Hash]; DELETE FROM [{SchemaName}].[List]; " +
            $"DELETE FROM [{SchemaName}].[Set]; DELETE FROM [{SchemaName}].[Counter]; " +
            $"DELETE FROM [{SchemaName}].[AggregatedCounter]; DELETE FROM [{SchemaName}].[Server];",
            connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
