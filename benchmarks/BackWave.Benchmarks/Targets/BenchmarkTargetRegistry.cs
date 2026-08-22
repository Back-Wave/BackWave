namespace BackWave.Benchmarks.Targets;

/// <summary>
/// Maps the harness's <c>--target</c> name onto the system under test. Every target is reachable by its full
/// engine name and by the short form the rest of the repo's runners already accept (<c>pg</c>, <c>mssql</c>,
/// <c>ora</c>). There is no Hangfire Oracle entry: Hangfire ships no first-party Oracle storage, so the
/// competitor chart stays a Postgres and SQL Server story.
/// </summary>
public static class BenchmarkTargetRegistry
{
    /// <summary>Resolves a <c>--target</c> name to its target, or throws for a name nothing serves.</summary>
    public static IBenchmarkTarget Create(string target) => target switch
    {
        "postgres" or "pg" => new PostgresBenchmarkTarget(),
        "sqlserver" or "mssql" => new SqlServerBenchmarkTarget(),
        "oracle" or "ora" => new OracleBenchmarkTarget(),
        "hangfire-postgres" or "hangfire-pg" => new HangfirePostgresTarget(),
        "hangfire-sqlserver" or "hangfire-mssql" => new HangfireSqlServerTarget(),
        _ => throw new ArgumentException(
            $"Unknown target '{target}'. Expected 'postgres', 'sqlserver', 'oracle', " +
            "'hangfire-postgres', or 'hangfire-sqlserver'."),
    };
}
