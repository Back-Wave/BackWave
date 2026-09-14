using BackWave.Benchmarks.Targets;

namespace BackWave.Benchmarks.Tests;

/// <summary>
/// The <c>--target</c> name contract. A target nobody can name is a target nobody runs, and a silently
/// accepted misspelling would benchmark the wrong system, so every published name - long form and short -
/// is pinned here, and so is the refusal for anything else. Constructing a target opens no connection, so
/// these stay deterministic and DB-free.
/// </summary>
public sealed class BenchmarkTargetRegistryTests
{
    public static TheoryData<string, Type> KnownNames => new()
    {
        { "postgres", typeof(PostgresBenchmarkTarget) },
        { "pg", typeof(PostgresBenchmarkTarget) },
        { "sqlserver", typeof(SqlServerBenchmarkTarget) },
        { "mssql", typeof(SqlServerBenchmarkTarget) },
        { "oracle", typeof(OracleBenchmarkTarget) },
        { "ora", typeof(OracleBenchmarkTarget) },
        { "hangfire-postgres", typeof(HangfirePostgresTarget) },
        { "hangfire-pg", typeof(HangfirePostgresTarget) },
        { "hangfire-sqlserver", typeof(HangfireSqlServerTarget) },
        { "hangfire-mssql", typeof(HangfireSqlServerTarget) },
        { "jobmaster-postgres", typeof(JobMasterPostgresTarget) },
        { "jobmaster-pg", typeof(JobMasterPostgresTarget) },
    };

    [Theory]
    [MemberData(nameof(KnownNames))]
    public async Task Every_published_target_name_resolves_to_its_system(string name, Type expected)
    {
        await using var target = BenchmarkTargetRegistry.Create(name);

        Assert.IsType(expected, target);
    }

    [Fact]
    public async Task Oracle_resolves_under_both_names_to_the_backwave_oracle_target()
    {
        await using var full = BenchmarkTargetRegistry.Create("oracle");
        await using var shortForm = BenchmarkTargetRegistry.Create("ora");

        Assert.Equal("BackWave/Oracle", full.Name);
        Assert.Equal("Oracle", full.Engine);
        Assert.Equal(full.Name, shortForm.Name);
    }

    [Fact]
    public void An_unknown_target_is_refused_and_the_message_lists_what_is_available()
    {
        var refusal = Assert.Throws<ArgumentException>(() => BenchmarkTargetRegistry.Create("cassandra"));

        Assert.Contains("cassandra", refusal.Message);
        Assert.Contains("postgres", refusal.Message);
        Assert.Contains("sqlserver", refusal.Message);
        Assert.Contains("oracle", refusal.Message);
    }

    [Fact]
    public void There_is_no_hangfire_oracle_comparison_target()
    {
        // Hangfire ships no first-party Oracle storage, so the competitor chart is a Postgres and SQL Server
        // story only. Naming one must fail rather than quietly resolve to some community stand-in.
        Assert.Throws<ArgumentException>(() => BenchmarkTargetRegistry.Create("hangfire-oracle"));
    }
}
