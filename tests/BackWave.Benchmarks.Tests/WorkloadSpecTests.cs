using BackWave.Benchmarks.Workload;

namespace BackWave.Benchmarks.Tests;

/// <summary>
/// The Workload Spec turns config into a deterministic job stream; these pin the stream shape and count so
/// a benchmark is reproducible and not sensitive to an undocumented dial (ADR 0027 §2–3).
/// </summary>
public sealed class WorkloadSpecTests
{
    [Fact]
    public void Noop_drain_stream_has_exactly_jobcount_jobs()
    {
        var spec = new WorkloadSpec { JobCount = 250 };
        Assert.Equal(250, spec.Stream().Count());
    }

    [Fact]
    public void Noop_drain_handler_delay_is_zero()
    {
        var spec = new WorkloadSpec { JobCount = 10 };
        Assert.Equal(ArrivalMode.Drain, spec.Arrival);
        Assert.Equal(0, spec.DelayMs);
        Assert.All(spec.Stream(), job => Assert.Equal(0, job.DelayMs));
    }

    [Fact]
    public void Realistic_anchor_carries_the_handler_delay_on_every_job()
    {
        var spec = new WorkloadSpec { JobCount = 10, HandlerDelay = TimeSpan.FromMilliseconds(10) };
        Assert.Equal(10, spec.DelayMs);
        Assert.All(spec.Stream(), job => Assert.Equal(10, job.DelayMs));
    }

    [Fact]
    public void Payload_is_a_fixed_size_in_the_configured_band()
    {
        var spec = new WorkloadSpec { JobCount = 5, PayloadSizeBytes = 150 };
        Assert.All(spec.Stream(), job => Assert.Equal(150, job.Payload.Length));
    }

    [Fact]
    public void Stream_is_deterministic_for_a_given_spec()
    {
        var spec = new WorkloadSpec { JobCount = 100, HandlerDelay = TimeSpan.FromMilliseconds(5), PayloadSizeBytes = 120 };
        Assert.Equal(spec.Stream().ToArray(), spec.Stream().ToArray());
    }

    [Fact]
    public void Sustained_shape_is_selectable_with_a_rate()
    {
        var spec = new WorkloadSpec
        {
            JobCount = 1000,
            Arrival = ArrivalMode.Sustained,
            SustainedRatePerSecond = 500,
        };
        Assert.Equal(ArrivalMode.Sustained, spec.Arrival);
        Assert.Equal(500, spec.SustainedRatePerSecond);
        Assert.Equal(1000, spec.Stream().Count());
    }

    [Fact]
    public void Every_job_targets_the_single_bench_queue()
    {
        Assert.Equal("bench", WorkloadSpec.BenchQueue);
    }
}
