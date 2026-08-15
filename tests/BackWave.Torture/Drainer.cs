using BackWave.Core;
using BackWave.Storage;

namespace BackWave.Torture;

/// <summary>
/// Drives the store from workload-end to quiescence within a bound — the liveness family of the
/// audit. It resumes every queue, clears every concurrency limit (config queues AND the governed
/// queue), then loops expire/claim/report until nothing live remains. A store that cannot be
/// drained by a well-behaved pump within the bound has a liveness bug (or the run found a stuck
/// shape), reported as DrainLiveness.
/// </summary>
internal sealed class Drainer(IJobStore store, KeySpace keys, TortureOptions options, Func<Exception, bool> isTransient)
{
    private const string WorkerId = "torture-drainer";

    private readonly RetryDisposition _disposition =
        new RetryPolicy { MaxAttempts = options.MaxAttempts, Backoff = _ => TimeSpan.FromMilliseconds(100) }
            .ToDisposition();

    public async Task<IReadOnlyList<TortureViolation>> DrainAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + options.DrainBound;

        foreach (var queue in keys.AllQueues)
        {
            await Retrying(() => store.ResumeQueueAsync(queue, WorkerId, Now(), cancellationToken).AsTask(), deadline);
        }
        // Clear EVERY concurrency limit, governed queue included. The limit is a workload-phase
        // invariant (audited from the journal, which the drainer never writes); during drain it is
        // pure throttle. Leaving the governed limit at 2 caps drain to 2 jobs per claim pass, so a
        // large governed backlog cannot clear within DrainBound on a higher-latency adapter — a
        // false DrainLiveness trip even though the backlog drains monotonically (SqlServer, 8h run).
        foreach (var queue in keys.ConfigQueues.Append(keys.GovernedQueue))
        {
            await Retrying(() => store.SetConcurrencyLimitAsync(queue, null, WorkerId, Now(), cancellationToken).AsTask(), deadline);
        }

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!await StepAsync(cancellationToken) && await LiveCountAsync(cancellationToken) == 0)
            {
                return [];
            }
            await Task.Delay(25, cancellationToken);
        }

        return await StuckDiagnosticAsync(cancellationToken);
    }

    /// <summary>One pump pass. Returns true when it did any work (claimed or swept something).</summary>
    private async Task<bool> StepAsync(CancellationToken cancellationToken)
    {
        var worked = false;
        try
        {
            worked |= await store.ExpireLeasesAsync(Now(), 500, keys.AllQueues, _disposition, cancellationToken) > 0;

            var claimed = await store.ClaimAsync(
                new ClaimRequest(WorkerId, keys.AllQueues, 32, TimeSpan.FromSeconds(30), Now()), cancellationToken);
            worked |= claimed.Count > 0;

            foreach (var record in claimed)
            {
                JobOutcome outcome = keys.IsUnroutable(record.WireName)
                    ? new JobOutcome.Unroutable("torture drain: designated unroutable")
                    : record.CancelRequested
                        ? new JobOutcome.Cancelled("torture drain: cooperative cancel")
                        : new JobOutcome.Success();
                await store.ReportOutcomeAsync(record.JobId, WorkerId, record.Attempt, outcome, Now(),
                    cancellationToken: cancellationToken);
            }
        }
        catch (Exception exception) when (isTransient(exception))
        {
            // Contention noise; the loop just tries again.
        }
        return worked;
    }

    private async Task<int> LiveCountAsync(CancellationToken cancellationToken)
    {
        var counts = await store.CountJobsAsync(cancellationToken);
        return counts.Where(c => !JobStates.IsTerminal(c.State)).Sum(c => c.Count);
    }

    private async Task<IReadOnlyList<TortureViolation>> StuckDiagnosticAsync(CancellationToken cancellationToken)
    {
        var violations = new List<TortureViolation>();
        foreach (var state in new[] { JobState.Scheduled, JobState.AwaitingParent, JobState.Leased })
        {
            var stuck = await store.ListJobsAsync(new JobQuery { State = state, MaxResults = 50 }, cancellationToken);
            foreach (var job in stuck)
            {
                violations.Add(new TortureViolation(
                    TortureInvariant.DrainLiveness,
                    $"Job {job.JobId} stuck {state} in '{job.Queue}' (attempt {job.Attempt}, due {job.DueTime:O}, " +
                    $"leaseOwner {job.LeaseOwner ?? "-"}, wire {job.WireName}) after the {options.DrainBound.TotalSeconds:F0}s drain bound.",
                    job.JobId));
            }
        }
        if (violations.Count == 0)
        {
            // Live counts said non-zero but the listing found nothing concrete — report the counts.
            var counts = await store.CountJobsAsync(cancellationToken);
            foreach (var count in counts.Where(c => !JobStates.IsTerminal(c.State) && c.Count > 0))
            {
                violations.Add(new TortureViolation(
                    TortureInvariant.DrainLiveness,
                    $"{count.Count} job(s) still {count.State} in '{count.Queue}' after the drain bound."));
            }
        }
        return violations;
    }

    private async Task Retrying(Func<Task> action, DateTimeOffset deadline)
    {
        while (true)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception exception) when (isTransient(exception) && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
        }
    }

    private static DateTimeOffset Now() => DateTimeOffset.UtcNow;
}
