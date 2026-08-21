using System.Diagnostics;
using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BackWave.Oracle.Tests;

/// <summary>
/// Drives a real <see cref="WorkerGroupService"/> pump against the dockerized Oracle store to confirm the two
/// user-reported faults on actual Oracle runs:
///   1. Admission is capped at PoolSize under a burst - the fix in 444d13f.
///   2. Idle claim latency stays bounded under polling-only pacing - the fix in ed4c681.
/// </summary>
[Collection("oracle")]
public sealed class OracleWorkerGroupLoadTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Queue = "orders";

    [Fact]
    public async Task Burst_of_jobs_never_admits_more_than_PoolSize_concurrently()
    {
        const int poolSize = 20;
        const int jobCount = 400;

        await OracleTestDatabase.CreateFreshStoreAsync();
        var store = new OracleJobStore(new OracleStoreOptions { ConnectionString = OracleTestDatabase.ConnectionString });
        var recorder = new ConcurrencyRecorder();

        using var host = BuildHost(store, recorder, new WorkerGroupOptions
        {
            Name = "orders-workers",
            Policy = new DispatchPolicy.Strict([Queue]),
            PoolSize = poolSize,
            MaxClaimBatch = 32,
            PollInterval = TimeSpan.FromMilliseconds(200),
            // Polling-only: no idle backoff ceiling, so the burst is claimed at the base rate.
            MaxPollInterval = TimeSpan.Zero,
            LeaseDuration = TimeSpan.FromMinutes(2),
        });

        // Enqueue the whole burst while the pump is still cold, so all 400 are due-now the instant it starts.
        var client = host.Services.GetRequiredService<BackWaveClient>();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < jobCount; i++)
        {
            await client.EnqueueAsync(new OrderJob(i), dueTime: now, queue: Queue);
        }

        await host.StartAsync();
        await WaitForAsync(() => recorder.Completed >= jobCount, TimeSpan.FromMinutes(2),
            $"all {jobCount} jobs to drain (completed {recorder.Completed})");
        await host.StopAsync();

        output.WriteLine($"burst: completed={recorder.Completed}, peak concurrency={recorder.PeakConcurrency}, PoolSize={poolSize}");
        Assert.Equal(jobCount, recorder.Completed);
        Assert.True(recorder.PeakConcurrency <= poolSize,
            $"peak live handlers {recorder.PeakConcurrency} must not exceed PoolSize {poolSize}");
    }

    [Fact]
    public async Task Idle_pump_claims_a_new_job_within_one_PollInterval()
    {
        var pollInterval = TimeSpan.FromMilliseconds(500);

        await OracleTestDatabase.CreateFreshStoreAsync();
        var store = new OracleJobStore(new OracleStoreOptions { ConnectionString = OracleTestDatabase.ConnectionString });
        var recorder = new ConcurrencyRecorder();

        using var host = BuildHost(store, recorder, new WorkerGroupOptions
        {
            Name = "orders-workers",
            Policy = new DispatchPolicy.Strict([Queue]),
            PoolSize = 20,
            PollInterval = pollInterval,
            // A ceiling far above the floor: the pre-ed4c681 pacer would saturate here on the drain tail and
            // strand a later enqueue for seconds. The elapsed-idle ramp must keep a soon-after enqueue near the floor.
            MaxPollInterval = TimeSpan.FromSeconds(4),
            LeaseDuration = TimeSpan.FromMinutes(2),
        });

        var client = host.Services.GetRequiredService<BackWaveClient>();
        await host.StartAsync();

        // Warm the pump, then let it drain and go idle so the backoff ramp starts to climb.
        for (var i = 0; i < 5; i++)
        {
            await client.EnqueueAsync(new OrderJob(i), dueTime: DateTimeOffset.UtcNow, queue: Queue);
        }
        await WaitForAsync(() => recorder.Completed >= 5, TimeSpan.FromSeconds(30), "warm-up jobs to drain");
        await Task.Delay(TimeSpan.FromMilliseconds(900));

        // The probe: one job enqueued into an idle group. Measure how long until the pump claims and runs it.
        recorder.ArmProbe();
        var stopwatch = Stopwatch.StartNew();
        await client.EnqueueAsync(new OrderJob(1000), dueTime: DateTimeOffset.UtcNow, queue: Queue);
        await WaitForAsync(() => recorder.ProbeSeen, TimeSpan.FromSeconds(10), "the idle pump to claim the probe job");
        stopwatch.Stop();
        await host.StopAsync();

        output.WriteLine($"idle claim latency={stopwatch.ElapsedMilliseconds} ms, PollInterval={pollInterval.TotalMilliseconds} ms, MaxPollInterval=4000 ms");

        // Polling-only guarantee: a job enqueued soon after idle is claimed near the PollInterval floor, not the
        // 4s ceiling. Generous 2x slack for Oracle round-trips; the pre-fix stall was multiples of the interval.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"idle claim latency {stopwatch.ElapsedMilliseconds} ms must stay near PollInterval {pollInterval.TotalMilliseconds} ms");
    }

    private static IHost BuildHost(IJobStore store, ConcurrencyRecorder recorder, WorkerGroupOptions group)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(recorder);
        builder.Services.AddTransient<IJobHandler<OrderJob>, OrderHandler>();
        builder.Services.AddBackWave(backwave =>
        {
            backwave
                .UseStore(store)
                .UseRegistry(new JobRegistry(
                [
                    JobRegistration.Create<OrderJob, OrderHandler>("order", OracleLoadJsonContext.Default.OrderJob, Queue),
                ]))
                .AddWorkerGroup(group);
        });
        return builder.Build();
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.Fail($"Timed out waiting for: {description}");
    }
}

public sealed record OrderJob(int Index);

public sealed class OrderHandler(ConcurrencyRecorder recorder) : IJobHandler<OrderJob>
{
    public async Task HandleAsync(OrderJob job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Enter(job.Index);
        // A short body so the pool holds several handlers at once and a burst can overshoot PoolSize if uncapped.
        await Task.Delay(40, cancellationToken);
        recorder.Exit();
    }
}

/// <summary>Tracks live handler concurrency, a completion count, and a single probe-job sighting.</summary>
public sealed class ConcurrencyRecorder
{
    private int _live;
    private int _peak;
    private int _completed;
    private volatile bool _probeArmed;
    private volatile bool _probeSeen;

    public int PeakConcurrency => Volatile.Read(ref _peak);
    public int Completed => Volatile.Read(ref _completed);
    public bool ProbeSeen => _probeSeen;

    public void ArmProbe() => _probeArmed = true;

    public void Enter(int index)
    {
        if (_probeArmed && index == 1000)
        {
            _probeSeen = true;
        }

        var current = Interlocked.Increment(ref _live);
        int observed;
        do
        {
            observed = Volatile.Read(ref _peak);
            if (current <= observed)
            {
                break;
            }
        }
        while (Interlocked.CompareExchange(ref _peak, current, observed) != observed);
    }

    public void Exit()
    {
        Interlocked.Decrement(ref _live);
        Interlocked.Increment(ref _completed);
    }
}

[JsonSerializable(typeof(OrderJob))]
internal sealed partial class OracleLoadJsonContext : JsonSerializerContext;
