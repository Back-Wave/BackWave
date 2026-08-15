using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Npgsql;

namespace BackWave.Benchmarks.Targets;

/// <summary>
/// Hangfire over Postgres via the <c>Hangfire.PostgreSql</c> adapter. This is published but footnoted: the
/// PG adapter is <em>community-maintained</em>, not first-party, so a win here is the weaker claim and the
/// result matrix says so (ADR 0027 §5). It is still tuned to its best honest config under the same fairness
/// rules as the SQL Server flagship — aggressive poll interval, sliding invisibility, native DB transactions —
/// and runs the identical measurement code path as every other target.
/// </summary>
public sealed class HangfirePostgresTarget : HangfireBenchmarkTarget
{
    /// <summary>Environment variable holding the Hangfire Postgres connection string (DSN).</summary>
    public const string ConnectionStringEnvVar = "BACKWAVE_HANGFIRE_POSTGRES_DSN";

    private const string SchemaName = "hangfire";
    private const string HangfireVersion = "1.8.23";
    private const string AdapterVersion = "1.21.1";

    private const string DefaultConnectionString =
        "Host=localhost;Port=5499;Username=backwave;Password=backwave;Database=hangfire_test";

    private readonly string _connectionString;

    /// <summary>Creates the target using the DSN from the environment, or the local docker-compose default.</summary>
    public HangfirePostgresTarget()
        : this(System.Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString)
    {
    }

    /// <summary>Creates the target against an explicit connection string.</summary>
    public HangfirePostgresTarget(string connectionString)
        => _connectionString = connectionString;

    /// <inheritdoc/>
    public override string Name => "Hangfire/Postgres";

    /// <inheritdoc/>
    public override string Engine => "PostgreSQL";

    /// <inheritdoc/>
    protected override JobStorage CreateStorage()
    {
        var options = new PostgreSqlStorageOptions
        {
            SchemaName = SchemaName,
            PrepareSchemaIfNecessary = true,
            // Tuned-to-best for the community adapter: an aggressive poll interval (the PG adapter has no
            // zero-poll fetch like SQL Server's sliding invisibility), sliding invisibility timeout, and
            // native DB transactions. Surfaced in StorageDials.
            QueuePollInterval = TimeSpan.FromMilliseconds(200),
            UseSlidingInvisibilityTimeout = true,
            UseNativeDatabaseTransactions = true,
        };
        return new PostgreSqlStorage(new NpgsqlConnectionFactory(_connectionString, options), options);
    }

    /// <inheritdoc/>
    protected override IReadOnlyDictionary<string, string> StorageDials() => new Dictionary<string, string>
    {
        ["queue-poll-interval"] = "200ms (community adapter, aggressive)",
        ["fetch-mode"] = "sliding invisibility timeout, native DB transactions (tuned-to-best)",
        ["schema"] = SchemaName,
        ["hangfire-version"] = HangfireVersion,
        ["hangfire-adapter"] = $"Hangfire.PostgreSql {AdapterVersion} (COMMUNITY adapter)",
        ["adapter-note"] = "Hangfire.PostgreSql is community-maintained — footnoted in the result matrix (ADR 0027 §5)",
    };

    /// <inheritdoc/>
    protected override async Task<string> ReadEngineVersionAsync(CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        await using var command = dataSource.CreateCommand("SHOW server_version");
        var version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return version?.ToString() ?? "unknown";
    }

    /// <inheritdoc/>
    protected override async Task<int> SampleConnectionCountAsync(CancellationToken cancellationToken)
    {
        // Defined identically to the BackWave Postgres probe (this process's backends, minus the probe's own),
        // so peak DB connections is a fair cross-system metric.
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        await using var command = dataSource.CreateCommand(
            "SELECT count(*) FROM pg_stat_activity " +
            "WHERE datname = current_database() AND pid <> pg_backend_pid()");
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long count ? (int)count : 0;
    }

    /// <inheritdoc/>
    protected override async Task ResetStoreAsync(CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        await using var command = dataSource.CreateCommand(
            $"TRUNCATE {SchemaName}.job, {SchemaName}.jobparameter, {SchemaName}.jobqueue, {SchemaName}.state, " +
            $"{SchemaName}.server, {SchemaName}.hash, {SchemaName}.list, {SchemaName}.set, {SchemaName}.counter, " +
            $"{SchemaName}.aggregatedcounter, {SchemaName}.lock RESTART IDENTITY CASCADE");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
