using BackWave.Benchmarks.Targets;

namespace BackWave.Benchmarks.Tests;

/// <summary>
/// The recorded-dials fairness contract (ADR 0027 §5, bench-0140). Every dial that lets a third party
/// reproduce and challenge a number must be present, and the load-bearing neutralized dial — worker count —
/// must be provably matched between BackWave and the Hangfire competitor. Constructing a target with an
/// explicit connection string opens no connection, so these stay deterministic and DB-free.
/// </summary>
public sealed class TuningDialsTests
{
    private const string PgDsn = "Host=localhost;Port=5499;Username=u;Password=p;Database=d";
    private const string MssqlDsn = "Server=localhost,1433;User Id=sa;Password=p;Database=d";

    [Fact]
    public void Hangfire_worker_count_is_matched_to_backwave_pool_size_on_both_engines()
    {
        // The single most load-bearing neutralized dial: under-tuning the competitor's worker count
        // manufactures a win that reverses on re-run, so the two must record the identical value.
        var backwave = new PostgresBenchmarkTarget(PgDsn).TuningDials["worker-pool-size"];

        Assert.Equal(backwave, new HangfirePostgresTarget(PgDsn).TuningDials["worker-pool-size"]);
        Assert.Equal(backwave, new HangfireSqlServerTarget(MssqlDsn).TuningDials["worker-pool-size"]);
    }

    [Fact]
    public void Backwave_dials_surface_the_adapter_claim_strategy_and_source_gen_serialization()
    {
        var pg = new PostgresBenchmarkTarget(PgDsn).TuningDials;
        var mssql = new SqlServerBenchmarkTarget(MssqlDsn).TuningDials;

        Assert.Contains("SKIP LOCKED", pg["claim-strategy"]);
        Assert.Contains("READPAST", mssql["claim-strategy"]);
        Assert.Contains("source-generated", pg["serialization"]);
    }

    [Fact]
    public void Backwave_and_hangfire_both_record_every_neutralized_dial()
    {
        // Worker count, connection-pool size, and retry policy are the neutralized dials the doc tabulates;
        // each system must record all three so the comparison is reproducible.
        string[] neutralized = ["worker-pool-size", "db-connection-pool-size", "retry-policy"];

        foreach (var key in neutralized)
        {
            Assert.True(new PostgresBenchmarkTarget(PgDsn).TuningDials.ContainsKey(key), $"BackWave missing {key}");
            Assert.True(new HangfirePostgresTarget(PgDsn).TuningDials.ContainsKey(key), $"Hangfire missing {key}");
        }
    }

    [Fact]
    public void Hangfire_dials_surface_reflection_json_and_pin_the_version()
    {
        var dials = new HangfireSqlServerTarget(MssqlDsn).TuningDials;

        Assert.Contains("Newtonsoft.Json", dials["serialization"]);
        Assert.Contains("disabled", dials["retry-policy"]);
        Assert.False(string.IsNullOrWhiteSpace(dials["hangfire-version"]));
        Assert.Contains("first-party", dials["hangfire-adapter"]);
    }

    [Fact]
    public void Hangfire_postgres_is_footnoted_as_a_community_adapter()
    {
        // The credibility footnote (ADR 0027 §5): a PG win is the weaker claim, so the dial says so.
        var dials = new HangfirePostgresTarget(PgDsn).TuningDials;

        Assert.Contains("COMMUNITY", dials["hangfire-adapter"]);
        Assert.Contains("community-maintained", dials["adapter-note"]);
    }
}
