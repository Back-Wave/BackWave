using BackWave.Jobs;
using BackWave.Storage;

namespace BackWave.Sample.Api;

/// <summary>
/// Every scenario job, declared with <c>[Job("wire-name")]</c> on a method. The source
/// generator emits the payload record, the <c>IJobHandler&lt;T&gt;</c>, the wire format, and
/// <c>BackWave.Generated.BackWaveJobs.CreateRegistry()</c> from these signatures — no
/// hand-written serialization, no reflection.
///
/// The generated handlers resolve this class from DI, so anything injected here (the logger,
/// the concurrency tracker) is available to every job body.
/// </summary>
public sealed class SampleJobs(
    ILogger<SampleJobs> logger, ConcurrencyTracker tracker, BackWaveClient client, ExternalReadinessGate readiness)
{
    /// <summary>
    /// The bread-and-butter job: immediate, delayed, recurring, and dependency demos all mint this.
    /// <paramref name="delayMs"/> lets the burst demos hold the Lease long enough to be visible in the
    /// dashboard's "Executing now" view; the instant demos pass 0.
    /// </summary>
    [Job("greet", Queue = "critical")]
    public async Task GreetAsync(string name, int delayMs, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("greet: hello {Name} (job {JobId}, attempt {Attempt})", name, context.JobId, context.Attempt);
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }
    }

    /// <summary>
    /// Bulk work on the Strict group's lower-priority queue — drained only when 'critical' is empty.
    /// <paramref name="delayMs"/> keeps it in flight long enough to watch the pool fill up.
    /// </summary>
    [Job("process", Queue = "bulk")]
    public async Task ProcessAsync(string item, int delayMs, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("process: {Item} (job {JobId})", item, context.JobId);
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }
    }

    /// <summary>
    /// Always throws, so it exhausts the Weighted group's attempt ceiling and lands Dead-Lettered.
    /// The <see cref="JobContext.Attempt"/> climbs 1, 2, 3 across the retries before the ceiling trips.
    /// </summary>
    [Job("flaky", Queue = "low")]
    public Task FlakyAsync(string label, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogWarning("flaky: {Label} failing on attempt {Attempt}", label, context.Attempt);
        throw new InvalidOperationException($"flaky '{label}' always fails (attempt {context.Attempt})");
    }

    /// <summary>
    /// Weighted-fair demo: many of these across 'high' and 'low' reveal the 6:1 smooth round-robin.
    /// <paramref name="delayMs"/> keeps each one in flight so the dashboard can show the pool at work.
    /// </summary>
    [Job("weighted-work", Queue = "high")]
    public async Task WeightedWorkAsync(string tag, int delayMs, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("weighted-work: {Tag} (job {JobId})", tag, context.JobId);
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }
    }

    /// <summary>
    /// Sleeps while running so the per-Queue Concurrency Limit is observable: with the limit set
    /// to 1, the tracker's peak never exceeds 1 no matter how many are enqueued at once.
    /// </summary>
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

    /// <summary>The job committed atomically with the business row by <c>POST /tx</c>.</summary>
    [Job("tx-finalize", Queue = "critical")]
    public Task TxFinalizeAsync(Guid rowId, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("tx-finalize: business row {RowId} committed alongside this job", rowId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The Job Tags showcase (ADR 0022). Tags are observational annotations the Core never reads —
    /// they exist purely to query and group jobs (dashboard pills, facets, filtered listings). This
    /// one job exercises all three places a Tag can come from, and both structural kinds (a bare
    /// <b>Label</b> vs a <b>Keyed Tag</b>):
    /// <list type="bullet">
    /// <item><b>Type-default Labels</b> — the <c>Labels</c> on the <c>[Job]</c> below; every
    /// 'tagged-report' starts with <c>billing</c> and <c>report</c>. Only Labels are expressible here.</item>
    /// <item><b>Enqueue-time Tags</b> — <c>POST /jobs/tagged-report</c> attaches a Keyed <c>tenant</c>
    /// Tag and (optionally) a <c>priority</c> Label.</item>
    /// <item><b>Runtime Tags</b> — this handler adds a <c>processed</c> Label and a computed
    /// <c>amount-band</c> Keyed Tag as it runs; they buffer on the <see cref="JobContext"/> and
    /// flush as a delta on the Attempt's fenced outcome write.</item>
    /// </list>
    /// All of them union onto the single job (set semantics — an identical Tag collapses).
    /// </summary>
    [Job("tagged-report", Queue = "bulk", Labels = ["billing", "report"])]
    public async Task TaggedReportAsync(string tenant, decimal amount, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("tagged-report: tenant {Tenant}, amount {Amount:C} (job {JobId})", tenant, amount, context.JobId);

        // Runtime Label: a bare marker added while the job runs.
        context.AddLabel("processed");

        // Runtime Keyed Tag: a computed dimension you can later facet on (?key=amount-band).
        var band = amount >= 1000m ? "high" : amount >= 100m ? "mid" : "low";
        context.AddTag("amount-band", band);

        // A short hold so the runtime Tags are observable in the dashboard before the outcome write.
        await Task.Delay(500, cancellationToken);
    }

    // -- Workflows v2 escape-hatch stages -----------------------------------------
    // Workflows v2 ships no .Delay step and no .WaitFor step by design. These handlers demonstrate
    // the honest alternatives, all built on the ordinary enqueue API (no workflow primitive needed):
    //   - a completion-anchored delay: a step, when it finishes, self-schedules the next step at a
    //     future due time. This appends a new job rather than pausing one, so no worker is held.
    //   - a poll-from-a-step wait: a step re-enqueues ITSELF on a backoff until its external
    //     condition holds, then lets the flow continue.
    // The fixed-floor delay and the external-enqueue trigger need no handler at all - they are a
    // plain EnqueueAsync(dueTime) and an out-of-band enqueue; see the /workflows/escape-hatches
    // endpoints in Program.cs.

    /// <summary>
    /// Completion-anchored delay, stage A. Does its work, then as its last act schedules stage B to
    /// become due <paramref name="cooldownSeconds"/> after THIS step completes. The wait is anchored to
    /// completion, not to when the flow started, and it only ever appends a new job.
    /// </summary>
    [Job("cooldown-warmup", Queue = "critical")]
    public async Task CooldownWarmupAsync(string label, int cooldownSeconds, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "cooldown-warmup: {Label} done; scheduling followup +{Cooldown}s (job {JobId})", label, cooldownSeconds, context.JobId);
        await client.EnqueueAsync(
            new CooldownFollowup(label), dueTime: DateTimeOffset.UtcNow.AddSeconds(cooldownSeconds));
    }

    /// <summary>Completion-anchored delay, stage B: the followup that runs a cooldown after stage A finished.</summary>
    [Job("cooldown-followup", Queue = "critical")]
    public Task CooldownFollowupAsync(string label, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("cooldown-followup: {Label} ran after the cooldown (job {JobId})", label, context.JobId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Poll-from-a-step wait. Checks an external condition; if it does not hold yet the step re-enqueues
    /// ITSELF at a backoff and tries again, up to <paramref name="maxPolls"/> times. When the condition
    /// holds it proceeds. This is the durable alternative to a wait-for-event step: the wait is just an
    /// ordinary future-due job, so it survives a restart and never blocks a worker.
    /// </summary>
    [Job("poll-external", Queue = "bulk")]
    public async Task PollExternalAsync(string reference, int poll, int maxPolls, JobContext context, CancellationToken cancellationToken)
    {
        if (readiness.IsReady(reference))
        {
            logger.LogInformation("poll-external: {Reference} ready on poll {Poll} - continuing (job {JobId})", reference, poll, context.JobId);
            return;
        }

        if (poll >= maxPolls)
        {
            logger.LogWarning("poll-external: {Reference} still not ready after {MaxPolls} polls - giving up (job {JobId})", reference, maxPolls, context.JobId);
            return;
        }

        var backoff = TimeSpan.FromSeconds(2);
        logger.LogInformation(
            "poll-external: {Reference} not ready (poll {Poll}); re-checking in {Backoff}s (job {JobId})", reference, poll, backoff.TotalSeconds, context.JobId);
        await client.EnqueueAsync(new PollExternal(reference, poll + 1, maxPolls), dueTime: DateTimeOffset.UtcNow + backoff);
    }

    /// <summary>
    /// External-enqueue trigger: the continuation an out-of-band event schedules directly. There is no
    /// poller and no reserved slot - the step simply does not exist until the event fires and enqueues it.
    /// </summary>
    [Job("process-webhook", Queue = "critical")]
    public Task ProcessWebhookAsync(string payload, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("process-webhook: handling out-of-band event '{Payload}' (job {JobId})", payload, context.JobId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A stand-in for an external system's readiness signal, so the poll-from-a-step escape-hatch demo has a
/// condition to wait on. Flip a reference to ready via POST /workflows/escape-hatches/wait-poll/ready.
/// </summary>
public sealed class ExternalReadinessGate
{
    private readonly HashSet<string> _ready = [];
    private readonly Lock _gate = new();

    public void MarkReady(string reference)
    {
        lock (_gate)
        {
            _ready.Add(reference);
        }
    }

    public bool IsReady(string reference)
    {
        lock (_gate)
        {
            return _ready.Contains(reference);
        }
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
