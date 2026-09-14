using BackWave.Benchmarks.Latency;

namespace BackWave.Benchmarks.Targets;

/// <summary>
/// Everything the harness knows about one <c>--target</c> name without building it: how to build it, which
/// environment variable holds its DSN, and which driver spells that DSN. The latency dial needs the last
/// two so it can point the target at its proxy before the target is constructed.
/// </summary>
/// <param name="Name">The canonical target name.</param>
/// <param name="ConnectionStringEnvVar">The environment variable holding the target's DSN.</param>
/// <param name="Driver">The ADO.NET driver the target connects with.</param>
/// <param name="Create">Builds the target, reading its DSN from the environment.</param>
public sealed record BenchmarkTargetDescriptor(
    string Name,
    string ConnectionStringEnvVar,
    DatabaseDriver Driver,
    Func<IBenchmarkTarget> Create);

/// <summary>
/// Maps the harness's <c>--target</c> name onto the system under test. Every target is reachable by its full
/// engine name and by the short form the rest of the repo's runners already accept (<c>pg</c>, <c>mssql</c>,
/// <c>ora</c>). There is no Hangfire Oracle entry: Hangfire ships no first-party Oracle storage, so the
/// competitor chart stays a Postgres and SQL Server story.
/// </summary>
public static class BenchmarkTargetRegistry
{
    /// <summary>Resolves a <c>--target</c> name to its descriptor, or throws for a name nothing serves.</summary>
    public static BenchmarkTargetDescriptor Describe(string target) => target switch
    {
        "postgres" or "pg" => new BenchmarkTargetDescriptor(
            "postgres",
            PostgresBenchmarkTarget.ConnectionStringEnvVar,
            DatabaseDriver.Postgres,
            static () => new PostgresBenchmarkTarget()),
        "sqlserver" or "mssql" => new BenchmarkTargetDescriptor(
            "sqlserver",
            SqlServerBenchmarkTarget.ConnectionStringEnvVar,
            DatabaseDriver.SqlServer,
            static () => new SqlServerBenchmarkTarget()),
        "oracle" or "ora" => new BenchmarkTargetDescriptor(
            "oracle",
            OracleBenchmarkTarget.ConnectionStringEnvVar,
            DatabaseDriver.Oracle,
            static () => new OracleBenchmarkTarget()),
        "hangfire-postgres" or "hangfire-pg" => new BenchmarkTargetDescriptor(
            "hangfire-postgres",
            HangfirePostgresTarget.ConnectionStringEnvVar,
            DatabaseDriver.Postgres,
            static () => new HangfirePostgresTarget()),
        "hangfire-sqlserver" or "hangfire-mssql" => new BenchmarkTargetDescriptor(
            "hangfire-sqlserver",
            HangfireSqlServerTarget.ConnectionStringEnvVar,
            DatabaseDriver.SqlServer,
            static () => new HangfireSqlServerTarget()),
        "jobmaster-postgres" or "jobmaster-pg" => new BenchmarkTargetDescriptor(
            "jobmaster-postgres",
            JobMasterPostgresTarget.ConnectionStringEnvVar,
            DatabaseDriver.Postgres,
            static () => new JobMasterPostgresTarget()),
        _ => throw new ArgumentException(
            $"Unknown target '{target}'. Expected 'postgres', 'sqlserver', 'oracle', " +
            "'hangfire-postgres', 'hangfire-sqlserver', or 'jobmaster-postgres'."),
    };

    /// <summary>Resolves a <c>--target</c> name to its target, or throws for a name nothing serves.</summary>
    public static IBenchmarkTarget Create(string target) => Describe(target).Create();
}
