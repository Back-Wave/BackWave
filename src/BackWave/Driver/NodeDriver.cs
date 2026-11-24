using BackWave.Diagnostics;
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

    private int? _claimInFlight;

    private int _pollsDuringClaim;

    private int _pollsFromBeforeClaim;

    private int _reportingSlots;

    private bool _flushDeferred;

    /// <summary>
    /// Whether the last outcome write answered with no rows for a batch that had rows. It closes the
    /// <see cref="NodeOptions.UnwrittenOutcomeSlots"/> headroom until a write answers with rows again, so a
    /// node whose writes fail holds a slot for every Unwritten outcome and claims no more than with no headroom.
    /// </summary>
    private bool _outcomeWriteFaulted;

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
                Command.ClaimBatch? firstClaim = null;
                if (_claimInFlight is null)
                {
                    firstClaim = StartClaimPass();
                }
                else
                {
                    _pollsDuringClaim++;
                }
                var commands = new List<Command>();
                // A poll tick is a flush trigger (ADR 0035): land any partial batch first, so released
                // Dependencies are claimable on the flush's own re-poll.
                if (FlushOutcomes() is { } pollFlush)
                {
                    commands.Add(pollFlush);
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
                var bound = ReleaseClaim();
                if (bound is { } asked && claim.Jobs.Count > asked)
                {
                    throw Invariant.Halt(
                        InvariantTrigger.ClaimBatchOverrun,
                        $"A claim bounded at {asked} job(s) returned {claim.Jobs.Count}.");
                }
                var executions = new List<Command>(claim.Jobs.Count + 1);
                foreach (var job in claim.Jobs)
                {
                    _executing.Add(job.JobId);
                    executions.Add(new Command.ExecuteJob(job));
                }
                // Weighted issues one batch per Queue. Credit advances at ISSUE, not allocation,
                // so a pass interrupted before it drains strands none — advance for this batch now. A
                // batch that returns work means the pass continues; an empty claim ends the pass and
                // leaves the rest of the plan un-issued (uncharged).
                if (claim.Jobs.Count > 0 && _weightedPlan.TryDequeue(out var nextBatch))
                {
                    ChargeIssued(nextBatch);
                    executions.Add(Issue(nextBatch));
                }
                else if (RunWaitingClaimPass(cameBackFull: claim.Jobs.Count == bound) is { } waitingPass)
                {
                    executions.Add(waitingPass);
                }
                return executions;

            case NodeEvent.ExecutionSucceeded success:
                return Finish(success.Job, new JobOutcome.Success(), success.Now);

            case NodeEvent.ExecutionFailed failure:
            {
                // Loud-failure path (ADR 0051): the node that ran the job holds its type, so a per-job-type
                // [Retry] override resolves here, above the storage boundary. Absent an override, the job
                // inherits the Worker Group policy. The lease-expiry sweep keeps the group policy.
                var nextAttemptAt = options.RetryOverrides.TryGetValue(failure.Job.WireName, out var disposition)
                    ? disposition.NextAttemptAt(failure.Job.Attempt, failure.Now)
                    : options.RetryPolicy.NextAttemptAt(failure.Job.Attempt, failure.Now);
                return Finish(failure.Job, new JobOutcome.Failure(nextAttemptAt, failure.Error), failure.Now);
            }

            case NodeEvent.ExecutionCancelled cancelled:
                return Finish(cancelled.Job, new JobOutcome.Cancelled(cancelled.Cause), cancelled.Now);

            case NodeEvent.ExecutionUnroutable unroutable:
                return Finish(unroutable.Job, new JobOutcome.Unroutable(unroutable.Reason), unroutable.Now);

            case NodeEvent.HeartbeatDue:
                // A heartbeat tick is a flush trigger (ADR 0035): drain any partial buffer, then renew the
                // leases of whatever is still executing. An idle node with a non-empty buffer would already
                // have flushed on the drain-tail, so this mainly catches a partial batch under sustained load.
                var heartbeatCommands = new List<Command>();
                if (FlushOutcomes() is { } heartbeatFlush)
                {
                    heartbeatCommands.Add(heartbeatFlush);
                }
                if (LeasesToRenew() is { Count: > 0 } renewing)
                {
                    heartbeatCommands.Add(new Command.Heartbeat(options.WorkerId, renewing, options.LeaseDuration));
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

            case NodeEvent.OutcomeBatchReported reported:
            {
                if (reported.Results.Count > 0)
                {
                    _outcomeWriteFaulted = false;
                }
                else if (_reportingSlots > 0)
                {
                    _outcomeWriteFaulted = true;
                }
                _reportingSlots = 0;
                var answered = new List<Command>();
                // An applied terminal outcome may have released a Dependency due right now:
                // re-poll so it is claimed promptly, not on the next timer tick. This decision
                // lives here, not in either pump, so production and the deterministic harness
                // drain everything due at this instant identically.
                foreach (var row in reported.Results)
                {
                    if (row.Result == OutcomeResult.Applied)
                    {
                        answered.Add(new Command.RequestPoll(reported.Now));
                    }
                }
                if (reported.Results.Count > 0 && _flushDeferred && FlushOutcomes() is { } deferredFlush)
                {
                    answered.Add(deferredFlush);
                }
                return answered;
            }

            case NodeEvent.MintCompleted mint:
                // Minted-due-now instances run this cycle: re-poll when the mint produced work.
                return mint.Minted > 0 ? [new Command.RequestPoll(mint.Now)] : [];

            default:
                throw new ArgumentOutOfRangeException(nameof(nodeEvent));
        }
    }

    /// <summary>
    /// Takes a finished job out of the running set and buffers its terminal outcome, flushing the batch when a
    /// flush trigger fires: the buffer reaches <see cref="NodeOptions.MaxOutcomeBatch"/>, or the node
    /// just went idle (the drain-tail, so a lone outcome never waits a whole poll interval before releasing its
    /// dependents). The poll and heartbeat ticks are the third trigger, handled in their own cases.
    /// <para>
    /// A finish frees a slot only while the Unwritten outcomes fit in the headroom. It re-polls only when it
    /// raises the free slots to a full claim from below, so the next claim asks for the whole pool rather than
    /// one claim per finish. With no headroom a finish never changes the free slots and never re-polls.
    /// </para>
    /// </summary>
    private IReadOnlyList<Command> Finish(JobRecord job, JobOutcome outcome, DateTimeOffset now)
    {
        var freeBefore = FreeSlots;
        _executing.Remove(job.JobId);
        _outcomeBuffer.Add(new ReportedOutcome(job.JobId, options.WorkerId, job.Attempt, outcome));
        var flush = _outcomeBuffer.Count >= options.MaxOutcomeBatch || _executing.Count == 0
            ? FlushOutcomes()
            : null;
        if (freeBefore >= FullClaim || FreeSlots < FullClaim)
        {
            return flush is null ? [] : [flush];
        }
        return flush is null ? [new Command.RequestPoll(now)] : [flush, new Command.RequestPoll(now)];
    }

    /// <summary>
    /// The Leases a heartbeat renews: every running job, and with a headroom the buffered outcomes as well,
    /// since the node now runs more work while they wait. Never the rows of the write in flight: that write
    /// either commits them or drops them, and a renewal would take the row locks it holds in another order.
    /// Each id once, because the store answers one result per requested job.
    /// </summary>
    private IReadOnlyList<Guid> LeasesToRenew()
    {
        if (options.UnwrittenOutcomeSlots <= 0 || _outcomeBuffer.Count == 0)
        {
            return [.. _executing];
        }
        var renewing = new List<Guid>(_executing);
        var listed = new HashSet<Guid>(_executing);
        foreach (var row in _outcomeBuffer)
        {
            if (listed.Add(row.JobId))
            {
                renewing.Add(row.JobId);
            }
        }
        return renewing;
    }

    private Command.ReportOutcomeBatch? FlushOutcomes()
    {
        if (_outcomeBuffer.Count == 0)
        {
            return null;
        }
        if (_reportingSlots > 0)
        {
            _flushDeferred = true;
            return null;
        }
        var batch = DrainOutcomeBatch();
        _reportingSlots = batch.Outcomes.Count;
        _flushDeferred = _outcomeBuffer.Count > 0;
        return batch;
    }

    /// <summary>Drains up to <see cref="NodeOptions.MaxOutcomeBatch"/> buffered outcomes into one batch command, in completion order.</summary>
    private Command.ReportOutcomeBatch DrainOutcomeBatch()
    {
        List<ReportedOutcome> rows;
        if (_outcomeBuffer.Count <= options.MaxOutcomeBatch)
        {
            rows = _outcomeBuffer;
            _outcomeBuffer = [];
        }
        else
        {
            rows = _outcomeBuffer.GetRange(0, options.MaxOutcomeBatch);
            _outcomeBuffer.RemoveRange(0, options.MaxOutcomeBatch);
        }
        return new Command.ReportOutcomeBatch(rows);
    }

    /// <summary>
    /// Drains the next batch of outcomes still buffered, for the Shell to report as this node stops. Returns
    /// null when the buffer is empty, so a clean stop with nothing pending issues no write.
    /// </summary>
    internal Command.ReportOutcomeBatch? DrainBufferedOutcomes()
        => _outcomeBuffer.Count > 0 ? DrainOutcomeBatch() : null;

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
        var available = Math.Min(options.MaxClaimBatch, FreeSlots);
        if (available <= 0)
        {
            return null;
        }
        if (_roundRobin is null)
        {
            return Issue(new Command.ClaimBatch(options.WorkerId, options.Policy.Queues, available, options.LeaseDuration));
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
        return Issue(first);
    }

    /// <summary>
    /// The finished jobs whose outcome write has not answered: the buffered outcomes plus the rows of the write
    /// in flight. Each still holds its store Lease. The production pump reads it after each step for the
    /// unwritten-outcomes metric.
    /// </summary>
    internal int UnwrittenOutcomeCount => _outcomeBuffer.Count + _reportingSlots;

    /// <summary>
    /// The pool slots a claim may fill. A running job holds one. The Unwritten outcomes fill the
    /// <see cref="NodeOptions.UnwrittenOutcomeSlots"/> headroom first, and only those beyond it hold a pool
    /// slot. A faulted write closes the headroom. With no headroom this is the pool minus every Leased job.
    /// </summary>
    /// <summary>
    /// Whether a running job's finish, or the answer of a write, will re-poll before this pool can take a full
    /// claim. With headroom open, a claim that just landed leaves its own jobs running, so a pass chained at
    /// once would claim only the slots they leave and split the pool into two cohorts again.
    /// </summary>
    private bool FinishWillRePoll =>
        !_outcomeWriteFaulted && options.UnwrittenOutcomeSlots > 0 && _executing.Count > 0 && FreeSlots < FullClaim;

    private int FullClaim => Math.Min(options.MaxClaimBatch, options.PoolSize);

    private int FreeSlots
    {
        get
        {
            var headroom = _outcomeWriteFaulted ? 0 : options.UnwrittenOutcomeSlots;
            return options.PoolSize - _executing.Count - Math.Max(0, UnwrittenOutcomeCount - headroom);
        }
    }

    private Command.ClaimBatch Issue(Command.ClaimBatch batch)
    {
        _claimInFlight = batch.MaxJobs;
        return batch;
    }

    private int? ReleaseClaim()
    {
        var bound = _claimInFlight;
        _claimInFlight = null;
        return bound;
    }

    private Command.ClaimBatch? RunWaitingClaimPass(bool cameBackFull)
    {
        var waiting = _pollsDuringClaim + (cameBackFull ? _pollsFromBeforeClaim : 0);
        _pollsDuringClaim = 0;
        _pollsFromBeforeClaim = 0;
        if (waiting == 0 || FinishWillRePoll || StartClaimPass() is not { } pass)
        {
            return null;
        }
        _pollsFromBeforeClaim = waiting - 1;
        return pass;
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

    /// <summary>
    /// Named bound: extra slots that only a finished job whose outcome write has not answered can hold, never
    /// a running one. The Unwritten outcomes fill them first, so a finished job can free its
    /// <see cref="PoolSize"/> slot before its write lands. The executing jobs stay at most PoolSize, and the
    /// executing plus the Unwritten at most PoolSize plus this bound. 0 (the default) holds every finished
    /// job's slot until its write answers.
    /// </summary>
    public int UnwrittenOutcomeSlots { get; init; }

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(60);
    public Core.RetryPolicy RetryPolicy { get; init; } = Core.RetryPolicy.Default;

    /// <summary>
    /// Per-job-type retry overrides, keyed by Wire Name (ADR 0051). When a job that failed loudly has a
    /// Wire Name in this map, the node computes the next attempt from that disposition instead of from
    /// <see cref="RetryPolicy"/>. Empty by default, so every job inherits the Worker Group policy.
    /// </summary>
    public IReadOnlyDictionary<string, Core.RetryDisposition> RetryOverrides { get; init; }
        = new Dictionary<string, Core.RetryDisposition>();

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
