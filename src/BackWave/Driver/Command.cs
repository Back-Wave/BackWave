using BackWave.Storage;

namespace BackWave.Driver;

/// <summary>
/// Outputs of the Node Driver: what the Shell should do next. The Driver decides;
/// only the Shell acts.
/// </summary>
internal abstract record Command
{
    private Command() { }

    /// <summary>Load the Recurring Schedules so the Core can plan mints.</summary>
    public sealed record LoadSchedules : Command;

    /// <summary>Apply the Core's mint decisions, fenced by each schedule's ExpectedCursor.</summary>
    public sealed record MintDue(IReadOnlyList<Core.MintDecision> Decisions) : Command;

    /// <summary>
    /// Re-poll now: the Core decided a state change — an applied outcome (a released
    /// Dependency may be due this instant) or a mint that produced work — may have made more
    /// work claimable right now. The Shell turns this into another poll; it holds no
    /// scheduling judgment of its own, so the production pump and the deterministic
    /// test pump stay identical by construction.
    /// </summary>
    public sealed record RequestPoll(DateTimeOffset Now) : Command;

    /// <summary>Claim up to MaxJobs due jobs from the candidate Queues.</summary>
    public sealed record ClaimBatch(
        string WorkerId,
        IReadOnlyList<string> Queues,
        int MaxJobs,
        TimeSpan LeaseDuration) : Command;

    /// <summary>Run the leased job's handler.</summary>
    public sealed record ExecuteJob(JobRecord Job) : Command;

    /// <summary>
    /// Report a coalesced batch of terminal outcomes to the store in one fenced write. The Driver
    /// buffers outcomes and flushes them together so the pump stays single-writer (throughput-per-write
    /// rises, connection count does not). Each row's Attempt fences its own Lease, exactly as the singular
    /// report does; a single buffered outcome flushes as a batch-of-one.
    /// </summary>
    public sealed record ReportOutcomeBatch(IReadOnlyList<ReportedOutcome> Outcomes) : Command;

    /// <summary>Renew Leases on the executing jobs and learn of cancel requests.</summary>
    public sealed record Heartbeat(string WorkerId, IReadOnlyList<Guid> JobIds, TimeSpan LeaseDuration) : Command;

    /// <summary>
    /// Dispose expired Leases in this Worker Group's own Queues by the expiry-as-Attempt rule.
    /// Scoped to <see cref="Queues"/> with the group's <see cref="Disposition"/>
    /// so a job's backoff and ceiling follow its own policy whichever node runs the sweep.
    /// </summary>
    public sealed record ExpireLeases(
        int MaxJobs, IReadOnlyList<string> Queues, Core.RetryDisposition Disposition) : Command;

    /// <summary>One bounded retention sweep; repeated until a pass purges zero.</summary>
    public sealed record PurgeTerminal(
        TerminalStateClass StateClass, DateTimeOffset TerminalBefore, int MaxJobs) : Command;

    /// <summary>Fire the executing handler's CancellationToken (cooperative cancel).</summary>
    public sealed record SignalCancellation(Guid JobId) : Command;

    /// <summary>
    /// The Lease was lost: stop applying this job's effects. The job may already be
    /// running elsewhere; at-least-once semantics make abandoning it benign.
    /// </summary>
    public sealed record AbandonExecution(Guid JobId) : Command;
}

/// <summary>
/// One buffered terminal outcome awaiting a batched report: the job, the worker and Attempt that fence its
/// Lease, and the outcome to apply. The Shell attaches the per-row diagnostics, tag delta, and output it
/// stashed at the execution edge when it drains the batch.
/// </summary>
internal sealed record ReportedOutcome(Guid JobId, string WorkerId, int Attempt, JobOutcome Outcome);
