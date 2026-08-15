using BackWave.Storage;

namespace BackWave.Driver;

/// <summary>
/// The sans-I/O state machine holding a node's logic: Step(event) → Commands.
/// It never awaits, times, or threads — the production pump and the Simulator are its
/// two callers, and both own all I/O and clocks. Its only state is the set of jobs this
/// node is currently executing.
/// </summary>
internal sealed class NodeDriver(NodeOptions options)
{
    private readonly HashSet<Guid> _executing = [];

    /// <summary>
    /// Terminal outcomes buffered for a coalesced report (ADR 0035), in completion order. The pump stays
    /// single-writer: outcomes flush together as one <see cref="Command.ReportOutcomeBatch"/> instead of one
    /// write each. A crash discards this buffer (a fresh Driver is installed), so those leases lapse and the
    /// jobs are reclaimed — At-Least-Once working as designed.
    /// </summary>
    private List<ReportedOutcome> _outcomeBuffer = [];

    /// <summary>
    /// How many terminal outcomes are buffered but not yet flushed. A crash discards them (a fresh Driver
    /// is installed), so the deterministic harness reads this to observe the buffer-loss-on-crash window.
    /// </summary>
    internal int BufferedOutcomeCount => _outcomeBuffer.Count;
    private readonly Core.SmoothWeightedRoundRobin? _roundRobin =
        options.Policy is Core.DispatchPolicy.Weighted weighted
            ? new Core.SmoothWeightedRoundRobin(weighted.Weights)
            : null;
    private readonly Core.CronCache _cronCache = new();

    /// <summary>The remaining per-Queue claim batches of the current Weighted pass.</summary>
    private readonly Queue<Command.ClaimBatch> _weightedPlan = new();

    /// <summary>When maintenance last ran; null until the first poll. Throttles the sweep.</summary>
    private DateTimeOffset? _lastMaintenance;

    public IReadOnlyList<Command> Step(NodeEvent nodeEvent)
    {
        switch (nodeEvent)
        {
            case NodeEvent.PollDue poll:
                // Cadence split (issue 0039, ADR-0008): every poll claims (the fast path a hint
                // or a re-poll takes), but maintenance — lease expiry, schedule load/mint,
                // retention — runs only once per MaintenanceInterval. A zero interval (the
                // default) runs maintenance every poll: the historical behaviour, byte-for-byte.
                var maintenanceDue =
                    _lastMaintenance is not { } last || poll.Now - last >= options.MaintenanceInterval;
                // Compute the claim pass BEFORE draining the buffer: a buffered-but-unreported outcome still
                // holds its store Lease, so it must keep counting against the pool (StartClaimPass subtracts
                // it). The buffered slots re-open only when the imminent flush's applied outcomes re-poll, so
                // claiming them here too would over-admit past PoolSize (ADR 0035).
                var firstClaim = StartClaimPass();
                var commands = new List<Command>();
                // A poll tick is a flush trigger (ADR 0035): land any partial batch first, so released
                // Dependencies are claimable on the flush's own re-poll.
                if (_outcomeBuffer.Count > 0)
                {
                    commands.Add(DrainOutcomeBatch());
                }
                if (maintenanceDue)
                {
                    _lastMaintenance = poll.Now;
                    commands.Add(new Command.ExpireLeases(
                        options.MaxClaimBatch, options.Policy.Queues, options.RetryPolicy.ToDisposition()));
                    commands.Add(new Command.LoadSchedules());
                    if (options.Retention is { } retention)
                    {
                        commands.Add(new Command.PurgeTerminal(
                            TerminalStateClass.SucceededOrCancelled,
                            poll.Now - retention.KeepSucceeded, options.MaxPurgeBatch));
                        commands.Add(new Command.PurgeTerminal(
                            TerminalStateClass.DeadLetteredOrQuarantined,
                            poll.Now - retention.KeepDeadLettered, options.MaxPurgeBatch));
                    }
                }
                if (firstClaim is not null)
                {
                    commands.Add(firstClaim);
                }
                return commands;

            case NodeEvent.PurgeCompleted purge:
                // A full batch means the backlog may not be drained: sweep again. Bounded
                // batches, repeated to zero — cleanup can never become a lock storm.
                return purge.Purged >= purge.Sweep.MaxJobs ? [purge.Sweep] : [];

            case NodeEvent.SchedulesLoaded schedules:
                var decisions = Core.MintPlanner.Plan(schedules.Schedules, schedules.Now, cronCache: _cronCache);
                return decisions.Count == 0 ? [] : [new Command.MintDue(decisions)];

            case NodeEvent.ClaimCompleted claim:
                var executions = new List<Command>(claim.Jobs.Count + 1);
                foreach (var job in claim.Jobs)
                {
                    _executing.Add(job.JobId);
                    executions.Add(new Command.ExecuteJob(job));
                }
                // Weighted issues one batch per Queue. Credit advances at ISSUE, not allocation,
                // so a pass interrupted before it drains strands none — advance for this batch now. A
                // batch that returns work means the pass continues; an empty claim emits no
                // ClaimCompleted, ending the pass and leaving the rest of the plan un-issued (uncharged).
                if (claim.Jobs.Count > 0 && _weightedPlan.TryDequeue(out var nextBatch))
                {
                    ChargeIssued(nextBatch);
                    executions.Add(nextBatch);
                }
                return executions;

            case NodeEvent.ExecutionSucceeded success:
                _executing.Remove(success.Job.JobId);
                return BufferOutcome(new ReportedOutcome(
                    success.Job.JobId, options.WorkerId, success.Job.Attempt, new JobOutcome.Success()));

            case NodeEvent.ExecutionFailed failure:
                _executing.Remove(failure.Job.JobId);
                return BufferOutcome(new ReportedOutcome(
                    failure.Job.JobId,
                    options.WorkerId,
                    failure.Job.Attempt,
                    new JobOutcome.Failure(
                        options.RetryPolicy.NextAttemptAt(failure.Job.Attempt, failure.Now),
                        failure.Error)));

            case NodeEvent.ExecutionCancelled cancelled:
                _executing.Remove(cancelled.Job.JobId);
                return BufferOutcome(new ReportedOutcome(
                    cancelled.Job.JobId, options.WorkerId, cancelled.Job.Attempt, new JobOutcome.Cancelled(cancelled.Cause)));

            case NodeEvent.ExecutionUnroutable unroutable:
                _executing.Remove(unroutable.Job.JobId);
                return BufferOutcome(new ReportedOutcome(
                    unroutable.Job.JobId, options.WorkerId, unroutable.Job.Attempt, new JobOutcome.Unroutable(unroutable.Reason)));

            case NodeEvent.HeartbeatDue:
                // A heartbeat tick is a flush trigger (ADR 0035): drain any partial buffer, then renew the
                // leases of whatever is still executing. An idle node with a non-empty buffer would already
                // have flushed on the drain-tail, so this mainly catches a partial batch under sustained load.
                var heartbeatCommands = new List<Command>();
                if (_outcomeBuffer.Count > 0)
                {
                    heartbeatCommands.Add(DrainOutcomeBatch());
                }
                if (_executing.Count > 0)
                {
                    heartbeatCommands.Add(new Command.Heartbeat(options.WorkerId, [.. _executing], options.LeaseDuration));
                }
                return heartbeatCommands;

            case NodeEvent.HeartbeatCompleted heartbeat:
                var reactions = new List<Command>();
                foreach (var result in heartbeat.Results)
                {
                    if (!result.Renewed)
                    {
                        _executing.Remove(result.JobId);
                        reactions.Add(new Command.AbandonExecution(result.JobId));
                    }
                    else if (result.CancelRequested)
                    {
                        reactions.Add(new Command.SignalCancellation(result.JobId));
                    }
                }
                return reactions;

            case NodeEvent.OutcomeReported outcome:
                // An applied terminal outcome may have released a Dependency due right now:
                // re-poll so it is claimed promptly, not on the next timer tick. This decision
                // lives here, not in either pump, so production and the deterministic harness
                // drain everything due at this instant identically.
                return outcome.Result == OutcomeResult.Applied ? [new Command.RequestPoll(outcome.Now)] : [];

            case NodeEvent.MintCompleted mint:
                // Minted-due-now instances run this cycle: re-poll when the mint produced work.
                return mint.Minted > 0 ? [new Command.RequestPoll(mint.Now)] : [];

            default:
                throw new ArgumentOutOfRangeException(nameof(nodeEvent));
        }
    }

    /// <summary>
    /// Buffers one terminal outcome and flushes the batch when a flush trigger fires (ADR 0035): the buffer
    /// reaches <see cref="NodeOptions.MaxOutcomeBatch"/>, or the node just went idle (the drain-tail, so a
    /// lone outcome never waits a whole poll interval before releasing its dependents). The poll and
    /// heartbeat ticks are the third trigger, handled in their own cases.
    /// </summary>
    private IReadOnlyList<Command> BufferOutcome(ReportedOutcome outcome)
    {
        _outcomeBuffer.Add(outcome);
        return _outcomeBuffer.Count >= options.MaxOutcomeBatch || _executing.Count == 0
            ? [DrainOutcomeBatch()]
            : [];
    }

    /// <summary>
    /// Drains the outcome buffer into a single batch command, in completion order. Hands the existing list
    /// to the command and swaps in a fresh one — no copy — since nothing mutates a drained buffer.
    /// </summary>
    private Command.ReportOutcomeBatch DrainOutcomeBatch()
    {
        var batch = new Command.ReportOutcomeBatch(_outcomeBuffer);
        _outcomeBuffer = [];
        return batch;
    }

    /// <summary>
    /// Starts a claim pass and returns its first claim, or null when the pool is full.
    /// Backpressure lives here, not in the Shell: claims never exceed the pool's free capacity,
    /// counting work already in flight.
    /// </summary>
    /// <remarks>
    /// Strict claims the free capacity in one priority-ordered batch (already O(1) round-trips).
    /// Weighted runs the smooth weighted round-robin over the free capacity to size a
    /// per-Queue batch, then issues one claim per Queue — O(Q) round-trips per pass, not O(N).
    /// Each batch lists its Queue first then the rest as fallthrough, so an empty allocated Queue
    /// reflows its slots to due Queues in the same pass (work-conserving; approximate within the
    /// pass, exact in aggregate). The remaining batches chain through <see cref="_weightedPlan"/>
    /// as each claim completes.
    /// </remarks>
    private Command.ClaimBatch? StartClaimPass()
    {
        _weightedPlan.Clear();
        // Free capacity subtracts BOTH in-flight executions and buffered-but-unreported outcomes (ADR 0035):
        // a completed job whose outcome has not yet been flushed still holds its store Lease, so it occupies
        // a pool slot until the report lands. Counting only _executing would over-claim past PoolSize.
        var available = Math.Min(options.MaxClaimBatch, options.PoolSize - _executing.Count - _outcomeBuffer.Count);
        if (available <= 0)
        {
            return null;
        }
        if (_roundRobin is null)
        {
            return new Command.ClaimBatch(options.WorkerId, options.Policy.Queues, available, options.LeaseDuration);
        }

        var allocation = _roundRobin.Allocate(available);
        var queues = options.Policy.Queues;
        for (var i = 0; i < queues.Count; i++)
        {
            if (allocation[i] > 0)
            {
                _weightedPlan.Enqueue(new Command.ClaimBatch(
                    options.WorkerId, FallthroughOrder(i), allocation[i], options.LeaseDuration));
            }
        }
        if (!_weightedPlan.TryDequeue(out var first))
        {
            return null;
        }
        // Advance the SWRR credit for the first batch as it is issued (returned to the caller).
        // Subsequent batches advance when dequeued in ClaimCompleted; a batch left in _weightedPlan —
        // Cleared by the next pass or never reached after an empty claim — is never issued and never
        // charged, so a dropped Queue keeps its credit instead of running a deficit.
        ChargeIssued(first);
        return first;
    }

    /// <summary>
    /// Advances the SWRR credit for one issued batch by the slots it claims. Allocation only sizes
    /// the batches (it does not move credit); deferring the credit advance to issue keeps the
    /// configured weights honoured even when a pass drops un-issued per-Queue batches.
    /// </summary>
    private void ChargeIssued(Command.ClaimBatch batch) => _roundRobin!.AdvanceServed(batch.MaxJobs);

    /// <summary>The served Queue first, then the rest in declaration order — the reflow fallthrough.</summary>
    private IReadOnlyList<string> FallthroughOrder(int primary)
    {
        var queues = options.Policy.Queues;
        var order = new List<string>(queues.Count) { queues[primary] };
        for (var i = 0; i < queues.Count; i++)
        {
            if (i != primary)
            {
                order.Add(queues[i]);
            }
        }
        return order;
    }
}

/// <summary>Static configuration of one Worker Group's node membership.</summary>
internal sealed record NodeOptions
{
    public required string WorkerId { get; init; }

    /// <summary>Which Queues this Worker Group serves and how it shares between them.</summary>
    public required Core.DispatchPolicy Policy { get; init; }

    public int MaxClaimBatch { get; init; } = 32;

    /// <summary>
    /// Named bound: the most terminal outcomes the node buffers before flushing them as one batched
    /// report (ADR 0035). The Shell defaults this to <see cref="MaxClaimBatch"/> so a claim batch's worth
    /// of outcomes coalesces into a single fenced write; the poll/heartbeat tick and the drain-tail flush
    /// any partial buffer before it is reached.
    /// </summary>
    public int MaxOutcomeBatch { get; init; } = 32;

    /// <summary>
    /// Named bound: concurrent executions this node accepts. Claims subtract in-flight
    /// work, so the pool can never overshoot. Unbounded unless the Shell sets it.
    /// </summary>
    public int PoolSize { get; init; } = int.MaxValue;

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(60);
    public Core.RetryPolicy RetryPolicy { get; init; } = Core.RetryPolicy.Default;

    /// <summary>
    /// Minimum spacing between maintenance sweeps — lease expiry, schedule load/mint, and
    /// retention purges. Claims still run every poll, so a Wake-Up Hint or a
    /// re-poll takes a claim-only fast path; maintenance fires on this slower, named cadence.
    /// Zero (the default) runs maintenance on every poll — the historical behaviour; the
    /// hosting pump sets a slower production cadence.
    /// </summary>
    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.Zero;

    /// <summary>Keep-then-purge retention; null disables sweeping on this node.</summary>
    public Core.RetentionPolicy? Retention { get; init; }

    /// <summary>Named bound: jobs deleted per retention sweep pass.</summary>
    public int MaxPurgeBatch { get; init; } = 500;
}
