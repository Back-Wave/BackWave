using BackWave.Jobs;
using BackWave.Storage;

namespace BackWave.Demo;

/// <summary>
/// Every handler the live demo runs, declared with <c>[Job("wire-name")]</c> on a method. The source
/// generator emits the payload record, the <c>IJobHandler&lt;T&gt;</c>, the wire format, and the
/// registry from these signatures — no hand-written serialization, no reflection.
///
/// The handlers deliberately succeed, throw, run slow, and dead-letter so every Dashboard tab stays
/// alive. The last one, <see cref="GenerateWorkloadAsync"/>, is the continuous generator: a recurring
/// schedule mints it every minute and it enqueues a fresh wave of work, so the demo self-replenishes
/// and "Executing now" is never empty.
/// </summary>
public sealed class DemoJobs(
    ILogger<DemoJobs> logger,
    ConcurrencyTracker tracker,
    BackWaveClient client,
    IJobStore store)
{
    /// <summary>The bread-and-butter job. <paramref name="delayMs"/> holds the Lease so long runs show in "Executing now".</summary>
    [Job("greet", Queue = "critical")]
    public async Task GreetAsync(string name, int delayMs, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("greet: hello {Name} (job {JobId}, attempt {Attempt})", name, context.JobId, context.Attempt);
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }
    }

    /// <summary>Bulk work on the Strict group's lower-priority queue — drained only when 'critical' is empty.</summary>
    [Job("process", Queue = "bulk")]
    public async Task ProcessAsync(string item, int delayMs, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("process: {Item} (job {JobId})", item, context.JobId);
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }
    }

    /// <summary>Always throws, so it exhausts the Weighted group's attempt ceiling and lands Dead-Lettered.</summary>
    [Job("flaky", Queue = "low")]
    public Task FlakyAsync(string label, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogWarning("flaky: {Label} failing on attempt {Attempt}", label, context.Attempt);
        throw new InvalidOperationException($"flaky '{label}' always fails (attempt {context.Attempt})");
    }

    /// <summary>Weighted-fair demo: many across 'high' and 'low' reveal the 6:1 smooth round-robin.</summary>
    [Job("weighted-work", Queue = "high")]
    public async Task WeightedWorkAsync(string tag, int delayMs, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("weighted-work: {Tag} (job {JobId})", tag, context.JobId);
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }
    }

    /// <summary>Sleeps while running so the per-Queue Concurrency Limit (set to 1) is observable.</summary>
    [Job("limited-work", Queue = "limited")]
    public async Task LimitedWorkAsync(string id, JobContext context, CancellationToken cancellationToken)
    {
        var concurrent = tracker.Enter();
        try
        {
            logger.LogInformation("limited-work: {Id} running — {Concurrent} concurrent (peak {Peak})",
                id, concurrent, tracker.Peak);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        finally
        {
            tracker.Exit();
        }
    }

    /// <summary>
    /// The Job Tags showcase. Type-default Labels (<c>billing</c>, <c>report</c>), enqueue-time Tags
    /// (a Keyed <c>tenant</c>, optional <c>priority</c> Label), and runtime Tags (<c>processed</c> plus
    /// a computed <c>amount-band</c>) all union onto one job — driving the pills and the facets.
    /// </summary>
    [Job("tagged-report", Queue = "bulk", Labels = ["billing", "report"])]
    public async Task TaggedReportAsync(string tenant, decimal amount, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("tagged-report: tenant {Tenant}, amount {Amount:C} (job {JobId})", tenant, amount, context.JobId);
        context.AddLabel("processed");
        var band = amount >= 1000m ? "high" : amount >= 100m ? "mid" : "low";
        context.AddTag("amount-band", band);
        await Task.Delay(500, cancellationToken);
    }

    // ── Continuous generator ───────────────────────────────────────────────────

    /// <summary>
    /// The self-replenishing heartbeat of the demo. A recurring schedule mints one of these every
    /// minute (see <c>Program.cs</c>); each run enqueues a fresh wave — long-running jobs that hold
    /// their Lease well past the next tick (so "Executing now" never empties), a spread of instant
    /// Succeeded work, a trickle of failures and one Quarantine, a burst against the Concurrency-Limited
    /// queue, and one Workflow. Everything it mints is bounded by the hourly container recycle.
    /// </summary>
    [Job("generate-workload", Queue = "critical")]
    public async Task GenerateWorkloadAsync(string source, JobContext context, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var rng = Random.Shared;

        // Executing feed: long holds (90–210s) span several ticks, so the pool stays full as older
        // in-flight work completes. Spread across both Worker Groups' queues.
        for (var i = 0; i < 8; i++)
        {
            var hold = rng.Next(90_000, 210_000);
            switch (i % 4)
            {
                case 0: await client.EnqueueAsync(new Greet(DemoWorkloads.Person(), hold), now); break;
                case 1: await client.EnqueueAsync(new Process($"stream-{now:HHmmss}-{i}", hold), now); break;
                case 2: await client.EnqueueAsync(new WeightedWork($"hi-{i}", hold), now, queue: "high"); break;
                default: await client.EnqueueAsync(new WeightedWork($"lo-{i}", hold), now, queue: "low"); break;
            }
        }

        // Instant Succeeded backlog: quick work across queues plus a couple tenant-tagged reports.
        for (var i = 0; i < 10; i++)
        {
            switch (i % 5)
            {
                case 0: await client.EnqueueAsync(new Greet(DemoWorkloads.Person(), 0), now); break;
                case 1: await client.EnqueueAsync(new Process($"batch-{i}", 0), now); break;
                case 2: await client.EnqueueAsync(new WeightedWork($"hi-{i}", 0), now, queue: "high"); break;
                case 3:
                    await client.EnqueueAsync(
                        new OrderNotification(DemoWorkloads.OrderRef(), DemoWorkloads.Email(), DemoWorkloads.Channel(),
                            rng.Next(1, 6), DemoWorkloads.Amount(), false), now);
                    break;
                default:
                    await client.EnqueueAsync(new TaggedReport(DemoWorkloads.Tenant(), DemoWorkloads.Amount()), now,
                        tags: DemoWorkloads.TenantTags());
                    break;
            }
        }

        // Concurrency Limit: a small burst so the 'limited' queue sits at its cap-of-1 with a backlog.
        for (var i = 0; i < 4; i++)
        {
            await client.EnqueueAsync(new LimitedWork($"slot-{now:HHmmss}-{i}"), now);
        }

        // Failures: one always-failing flaky, plus (half the time) a failing order-notification — both
        // land Dead-Lettered after their attempts, keeping "Needs attention" populated.
        await client.EnqueueAsync(new Flaky($"nightly-export-{now:HHmmss}"), now);
        if (rng.Next(2) == 0)
        {
            await client.EnqueueAsync(
                new OrderNotification(DemoWorkloads.OrderRef(), DemoWorkloads.Email(), DemoWorkloads.Channel(),
                    rng.Next(1, 6), DemoWorkloads.Amount(), true), now);
        }

        // Quarantine: an unregistered Wire Name pushed straight through the store — no handler routes
        // it, so the pump Quarantines it (a routing failure, not an execution one).
        await store.EnqueueAsync(
            new NewJob(Guid.NewGuid(), DemoWorkloads.Ghost(), "{}"u8.ToArray(),
                rng.Next(2) == 0 ? "critical" : "bulk", now),
            now: now);

        // Workflows: one per tick, rotating shape (some order-fulfillments fail and cascade Cancels onto
        // their downstream members; the checkout exercises every Workflows v2 shape at once), so the
        // Workflows tab keeps fresh running graphs.
        switch (rng.Next(6))
        {
            case 0:
            case 1: await DemoWorkloads.OrderFulfillmentAsync(client, fail: false); break;
            case 2: await DemoWorkloads.OrderFulfillmentAsync(client, fail: true); break;
            case 3: await DemoWorkloads.FanOutFanInAsync(client); break;
            case 4: await DemoWorkloads.CheckoutAsync(client, expedite: rng.Next(2) == 0); break;
            default: await DemoWorkloads.JobOutputAsync(client); break;
        }

        logger.LogInformation("generate-workload: enqueued a fresh wave from {Source} (job {JobId})", source, context.JobId);
    }
}

/// <summary>
/// Counts in-flight <c>limited-work</c> executions and remembers the high-water mark, so the
/// Concurrency Limit demo can show the cluster-wide cap actually held.
/// </summary>
public sealed class ConcurrencyTracker
{
    private int _current;
    private int _peak;
    private readonly Lock _gate = new();

    public int Enter()
    {
        lock (_gate)
        {
            _current++;
            if (_current > _peak)
            {
                _peak = _current;
            }

            return _current;
        }
    }

    public void Exit()
    {
        lock (_gate)
        {
            _current--;
        }
    }

    public int Peak
    {
        get
        {
            lock (_gate)
            {
                return _peak;
            }
        }
    }
}
