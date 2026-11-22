using BackWave.Benchmarks;
using BackWave.Benchmarks.Environment;
using BackWave.Benchmarks.Latency;
using BackWave.Benchmarks.Metrics;
using BackWave.Benchmarks.Targets;
using BackWave.Benchmarks.Workload;

namespace BackWave.Benchmarks.Tests;

/// <summary>
/// A result on an environment that allows publishing is still unpublishable when its work audit failed or a
/// diagnostic knob was engaged, so neither can reach the published matrix.
/// </summary>
public sealed class PublishableResultTests
{
    private static readonly WorkAudit Passed = new(10, 10, 10, 10, 1, 1, 1, 10, []);
    private static readonly WorkAudit Failed = new(10, 9, 10, 9, 1, 1, 1, 10, ["succeeded 9 of 10"]);

    [Fact]
    public void A_clean_run_on_a_publishable_environment_is_publishable()
    {
        var result = Result([Passed], []);

        Assert.False(result.Diagnostic);
        Assert.True(result.Publishable);
    }

    [Fact]
    public void A_failed_work_audit_makes_the_result_unpublishable()
    {
        Assert.False(Result([Passed, Failed], []).Publishable);
    }

    [Fact]
    public void An_engaged_diagnostic_knob_marks_the_result_diagnostic_and_unpublishable()
    {
        var result = Result([Passed], ["BACKWAVE_BENCH_DB_TRACE"]);

        Assert.True(result.Diagnostic);
        Assert.False(result.Publishable);
    }

    private static BenchmarkResult Result(IReadOnlyList<WorkAudit> audits, IReadOnlyList<string> knobs) => new()
    {
        Target = "BackWave/Postgres",
        Engine = "PostgreSQL",
        Workload = WorkloadSummary.From(new WorkloadSpec { JobCount = 10 }),
        Manifest = EnvironmentManifest.Capture(RunMode.Official, "PostgreSQL", "17", LatencyProfile.Disabled)
            with { Publishable = true },
        TuningDials = new Dictionary<string, string>(),
        WarmupRuns = 0,
        MeasuredRuns = audits.Count,
        ThroughputJobsPerSecond = new Distribution(1, 2, 3),
        EndToEndP50Ms = new Distribution(1, 2, 3),
        EndToEndP99Ms = new Distribution(1, 2, 3),
        EnqueueP50Ms = new Distribution(1, 2, 3),
        EnqueueP99Ms = new Distribution(1, 2, 3),
        Resources = ResourceMetrics.None,
        WorkAudits = audits,
        DiagnosticKnobs = knobs,
    };
}
