using System.Data.Common;
using BackWave.Core;
using BackWave.Diagnostics;
using BackWave.Storage;

namespace BackWave.Hosting.Tests;

/// <summary>A transient store fault: <see cref="DbException.IsTransient"/> is what both real adapters surface.</summary>
public sealed class TransientStoreException() : DbException("forced transient store fault")
{
    public override bool IsTransient => true;
}

/// <summary>Wraps the In-Memory Store to force invariant violations on demand.</summary>
public sealed class FaultableStore(IJobStore inner) : IJobStore
{
    private int _transientClaimFaults;
    private int _relinquishCalls;
    private int _reportOutcomesCalls;
    private int _rowsSettledBeforeRejection;

    /// <summary>Claims from this Queue throw — the targeted fail-stop trigger.</summary>
    public string? PoisonedQueue { get; set; }

    /// <summary>Claims throw a named invariant violation carrying this trigger - the classified fail-stop.</summary>
    public InvariantTrigger? ClaimInvariant { get; set; }

    /// <summary>Every operation throws — a node-wide fail-stop trigger.</summary>
    public bool FailEverything { get; set; }

    /// <summary>The shutdown hand-back throws - the unreachable-store-at-shutdown trigger.</summary>
    public bool FailRelinquish { get; set; }

    /// <summary>
    /// The shutdown hand-back throws a named invariant violation carrying this trigger - what every real
    /// adapter does when its relinquish path proves an impossible state (a dangling gating edge, a row
    /// count no legal write produces).
    /// </summary>
    public InvariantTrigger? RelinquishInvariant { get; set; }

    /// <summary>
    /// The shutdown hand-back waits this long inside the store before it does the work. A slow store
    /// rather than a broken one, which is the shape the hand-back's own budget is written against.
    /// </summary>
    public TimeSpan RelinquishDelay { get; set; }

    /// <summary>How many times the shutdown hand-back reached the store.</summary>
    public int RelinquishCalls => Volatile.Read(ref _relinquishCalls);

    /// <summary>When the shutdown hand-back FIRST reached the store, so a test can order it against its handlers.</summary>
    public DateTimeOffset? RelinquishedAt { get; private set; }

    /// <summary>Claims hand back their jobs rewritten into this state - the malformed-claim trigger.</summary>
    public JobState? RewriteClaimedState { get; set; }

    /// <summary>
    /// A non-empty claim is padded out to <c>MaxJobs + this</c> rows by cloning the first claimed job under
    /// fresh ids - the over-claim trigger.
    /// </summary>
    public int? OverClaimBy { get; set; }

    /// <summary>Batched outcome reports come back with this many trailing rows dropped - the short-answer trigger.</summary>
    public int? DropOutcomeResults { get; set; }

    /// <summary>Batched heartbeats come back with this many trailing rows dropped - the short-answer trigger.</summary>
    public int? DropHeartbeatResults { get; set; }

    /// <summary>Workflow reads come back empty - the member-without-its-workflow trigger.</summary>
    public bool HideWorkflows { get; set; }

    /// <summary>Batched outcome reports come back rewritten to StaleLease - the fenced-out-write trigger.</summary>
    public bool FenceOutOutcomes { get; set; }

    /// <summary>The next N claims throw a transient store fault, then recover — the degraded-then-healthy trigger.</summary>
    public int TransientClaimFaults
    {
        get => Volatile.Read(ref _transientClaimFaults);
        set => Volatile.Write(ref _transientClaimFaults, value);
    }

    private void ThrowIfFailing()
    {
        if (FailEverything)
        {
            throw new InvalidOperationException("forced invariant violation (FailEverything)");
        }
    }

    public bool SupportsTransactionalEnqueue => inner.SupportsTransactionalEnqueue;

    public ValueTask<EnqueueResult> EnqueueAsync(
        NewJob job, DateTimeOffset now, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return inner.EnqueueAsync(job, now, transaction, cancellationToken);
    }

    public ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        if (ClaimInvariant is { } tripped)
        {
            throw Invariant.Halt(tripped, "forced named invariant violation on claim");
        }
        if (PoisonedQueue is { } poisoned && request.Queues.Contains(poisoned))
        {
            throw new InvalidOperationException($"forced invariant violation (claim on '{poisoned}')");
        }
        if (Interlocked.Decrement(ref _transientClaimFaults) >= 0)
        {
            throw new TransientStoreException();
        }
        if (RewriteClaimedState is null && OverClaimBy is null)
        {
            return inner.ClaimAsync(request, cancellationToken);
        }
        return MalformClaimAsync(request, cancellationToken);
    }

    private async ValueTask<IReadOnlyList<JobRecord>> MalformClaimAsync(
        ClaimRequest request, CancellationToken cancellationToken)
    {
        var claimed = await inner.ClaimAsync(request, cancellationToken).ConfigureAwait(false);
        if (claimed.Count == 0)
        {
            return claimed;
        }

        var jobs = RewriteClaimedState is { } state
            ? [.. claimed.Select(job => job with { State = state })]
            : claimed.ToList();
        if (OverClaimBy is { } extra)
        {
            while (jobs.Count < request.MaxJobs + extra)
            {
                jobs.Add(jobs[0] with { JobId = Guid.NewGuid() });
            }
        }
        return jobs;
    }

    public ValueTask<OutcomeResult> ReportOutcomeAsync(
        Guid jobId, string workerId, int attempt, JobOutcome outcome, DateTimeOffset now,
        string? failureDetail = null,
        JobTags? addedTags = null,
        ReadOnlyMemory<byte>? output = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return inner.ReportOutcomeAsync(jobId, workerId, attempt, outcome, now, failureDetail, addedTags, output, cancellationToken);
    }

    /// <summary>
    /// When set, mirrors the adapters' batch Job Output pre-scan: an over-cap row rejects the WHOLE batch
    /// before any row is written. The In-Memory Store now pre-scans too, so this cap is for a test that
    /// needs the shape without the store's own bounds - a cap the store does not know about.
    /// </summary>
    public int? BatchOutputCap { get; set; }

    /// <summary>
    /// The other store shape: rows are applied ONE AT A TIME, so an over-cap row rejects the batch only
    /// after every row ahead of it is already settled. That is what the Shell's re-apply meets in the
    /// wild - rows the store has already written, which fence out as StaleLease on the second pass.
    /// </summary>
    public int? RowOutputCap { get; set; }

    /// <summary>How many batched outcome reports reached the store - one per re-apply pass.</summary>
    public int ReportOutcomesCalls => Volatile.Read(ref _reportOutcomesCalls);

    /// <summary>When the LAST batched outcome report reached the store, so a test can order the buffer
    /// flush against the relinquish that follows it.</summary>
    public DateTimeOffset? LastOutcomeReportAt { get; private set; }

    /// <summary>
    /// How many rows the <see cref="RowOutputCap"/> rejection had already settled when it threw, so a test
    /// can prove it exercised the settled-rows-ahead shape rather than passing on the trivial one.
    /// </summary>
    public int RowsSettledBeforeRejection => Volatile.Read(ref _rowsSettledBeforeRejection);

    public ValueTask<IReadOnlyList<OutcomeReportResult>> ReportOutcomesAsync(
        IReadOnlyList<OutcomeReport> batch, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _reportOutcomesCalls);
        LastOutcomeReportAt = DateTimeOffset.UtcNow;
        ThrowIfFailing();
        if (BatchOutputCap is { } cap)
        {
            foreach (var row in batch)
            {
                if (row.Outcome is JobOutcome.Success && row.Output is { } blob && blob.Length > cap)
                {
                    throw new JobOutputTooLargeException(row.JobId, blob.Length, cap);
                }
            }
        }
        if (RowOutputCap is { } rowCap)
        {
            return ApplyRowByRowAsync(batch, now, rowCap, cancellationToken);
        }
        if (DropOutcomeResults is null && !FenceOutOutcomes)
        {
            return inner.ReportOutcomesAsync(batch, now, cancellationToken);
        }
        return MalformOutcomesAsync(batch, now, cancellationToken);
    }

    private async ValueTask<IReadOnlyList<OutcomeReportResult>> MalformOutcomesAsync(
        IReadOnlyList<OutcomeReport> batch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var results = await inner.ReportOutcomesAsync(batch, now, cancellationToken).ConfigureAwait(false);
        if (FenceOutOutcomes)
        {
            results = [.. results.Select(row => row with { Result = OutcomeResult.StaleLease })];
        }
        if (DropOutcomeResults is { } dropped)
        {
            results = [.. results.Take(Math.Max(0, results.Count - dropped))];
        }
        return results;
    }

    private async ValueTask<IReadOnlyList<OutcomeReportResult>> ApplyRowByRowAsync(
        IReadOnlyList<OutcomeReport> batch, DateTimeOffset now, int cap, CancellationToken cancellationToken)
    {
        var results = new List<OutcomeReportResult>(batch.Count);
        foreach (var row in batch)
        {
            if (row.Outcome is JobOutcome.Success && row.Output is { } blob && blob.Length > cap)
            {
                Volatile.Write(ref _rowsSettledBeforeRejection, results.Count);
                throw new JobOutputTooLargeException(row.JobId, blob.Length, cap);
            }
            results.AddRange(
                await inner.ReportOutcomesAsync([row], now, cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    public ValueTask<ReadOnlyMemory<byte>?> GetJobOutputAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetJobOutputAsync(jobId, cancellationToken);

    public ValueTask<IReadOnlyList<HeartbeatResult>> HeartbeatAsync(
        string workerId, IReadOnlyList<Guid> jobIds, TimeSpan leaseDuration, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        if (DropHeartbeatResults is null)
        {
            return inner.HeartbeatAsync(workerId, jobIds, leaseDuration, now, cancellationToken);
        }
        return MalformHeartbeatAsync(workerId, jobIds, leaseDuration, now, cancellationToken);
    }

    private async ValueTask<IReadOnlyList<HeartbeatResult>> MalformHeartbeatAsync(
        string workerId, IReadOnlyList<Guid> jobIds, TimeSpan leaseDuration, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var results = await inner
            .HeartbeatAsync(workerId, jobIds, leaseDuration, now, cancellationToken).ConfigureAwait(false);
        var dropped = DropHeartbeatResults ?? 0;
        return [.. results.Take(Math.Max(0, results.Count - dropped))];
    }

    public ValueTask<int> ExpireLeasesAsync(
        DateTimeOffset now, int maxJobs, IReadOnlyList<string> queues, RetryDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return inner.ExpireLeasesAsync(now, maxJobs, queues, disposition, cancellationToken);
    }

    public ValueTask<int> RelinquishLeasesAsync(
        string workerId, DateTimeOffset now, RetryDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _relinquishCalls) == 1)
        {
            RelinquishedAt = DateTimeOffset.UtcNow;
        }
        ThrowIfFailing();
        if (RelinquishInvariant is { } tripped)
        {
            throw Invariant.Halt(tripped, "forced named invariant violation on relinquish");
        }
        if (FailRelinquish)
        {
            throw new InvalidOperationException("forced hand-back failure (FailRelinquish)");
        }
        if (RelinquishDelay > TimeSpan.Zero)
        {
            return SlowRelinquishAsync(workerId, now, disposition, cancellationToken);
        }
        return inner.RelinquishLeasesAsync(workerId, now, disposition, cancellationToken);
    }

    // The wait honors the token, as every real adapter's command does. A hand-back that runs out of
    // budget therefore meets the cancellation from INSIDE the store, which is where it meets it in the
    // wild - not at the call site, and not before the store was ever reached.
    private async ValueTask<int> SlowRelinquishAsync(
        string workerId, DateTimeOffset now, RetryDisposition disposition, CancellationToken cancellationToken)
    {
        await Task.Delay(RelinquishDelay, cancellationToken).ConfigureAwait(false);
        return await inner
            .RelinquishLeasesAsync(workerId, now, disposition, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<CancelResult> CancelJobAsync(
        Guid jobId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.CancelJobAsync(jobId, actor, now, cancellationToken);

    public ValueTask<RequeueResult> RequeueAsync(
        Guid jobId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.RequeueAsync(jobId, actor, now, cancellationToken);

    public ValueTask PauseQueueAsync(
        string queue, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.PauseQueueAsync(queue, actor, now, cancellationToken);

    public ValueTask ResumeQueueAsync(
        string queue, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.ResumeQueueAsync(queue, actor, now, cancellationToken);

    public ValueTask<TriggerScheduleResult> TriggerScheduleNowAsync(
        string scheduleId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.TriggerScheduleNowAsync(scheduleId, actor, now, cancellationToken);

    public ValueTask<IReadOnlyList<OperatorAuditRecord>> ListAuditRecordsAsync(
        string target, CancellationToken cancellationToken = default)
        => inner.ListAuditRecordsAsync(target, cancellationToken);

    public ValueTask UpsertScheduleAsync(ScheduleRecord schedule, CancellationToken cancellationToken = default)
        => inner.UpsertScheduleAsync(schedule, cancellationToken);

    public ValueTask RemoveScheduleAsync(string scheduleId, CancellationToken cancellationToken = default)
        => inner.RemoveScheduleAsync(scheduleId, cancellationToken);

    public ValueTask<IReadOnlyList<ScheduleSnapshot>> ListSchedulesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return inner.ListSchedulesAsync(cancellationToken);
    }

    public ValueTask<int> MintDueAsync(IReadOnlyList<MintDecision> decisions, CancellationToken cancellationToken = default)
        => inner.MintDueAsync(decisions, cancellationToken);

    public ValueTask SetConcurrencyLimitAsync(string queue, int? limit, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.SetConcurrencyLimitAsync(queue, limit, actor, now, cancellationToken);

    public ValueTask<JobRecord?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetJobAsync(jobId, cancellationToken);

    public ValueTask<IReadOnlyList<JobTransition>> GetJobHistoryAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetJobHistoryAsync(jobId, cancellationToken);

    public ValueTask<IReadOnlyList<JobRecord>> ListJobsAsync(JobQuery query, CancellationToken cancellationToken = default)
        => inner.ListJobsAsync(query, cancellationToken);

    public ValueTask<IReadOnlyList<QueueStateCount>> CountJobsAsync(CancellationToken cancellationToken = default)
        => inner.CountJobsAsync(cancellationToken);

    public ValueTask<IReadOnlyList<TagFacet>> FacetAsync(
        string key, JobQuery? baseQuery = null, int maxResults = int.MaxValue, CancellationToken cancellationToken = default)
        => inner.FacetAsync(key, baseQuery, maxResults, cancellationToken);

    public ValueTask<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(TagSuggestQuery query, CancellationToken cancellationToken = default)
        => inner.SuggestTagsAsync(query, cancellationToken);

    public ValueTask<WorkflowEnqueueResult> EnqueueWorkflowAsync(
        WorkflowDefinition workflow, DateTimeOffset now, DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
        => inner.EnqueueWorkflowAsync(workflow, now, transaction, cancellationToken);

    public ValueTask<IReadOnlyList<WorkflowSnapshot>> ListWorkflowsAsync(CancellationToken cancellationToken = default)
        => inner.ListWorkflowsAsync(cancellationToken);

    public ValueTask<WorkflowGraph?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default)
        => HideWorkflows
            ? ValueTask.FromResult<WorkflowGraph?>(null)
            : inner.GetWorkflowAsync(workflowId, cancellationToken);

    public ValueTask<IReadOnlyList<QueueSettings>> ListQueueSettingsAsync(CancellationToken cancellationToken = default)
        => inner.ListQueueSettingsAsync(cancellationToken);

    public ValueTask<DependencyEdges> GetDependencyEdgesAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetDependencyEdgesAsync(jobId, cancellationToken);

    public ValueTask<int> PurgeTerminalAsync(
        TerminalStateClass stateClass, DateTimeOffset terminalBefore, int maxJobs,
        CancellationToken cancellationToken = default)
        => inner.PurgeTerminalAsync(stateClass, terminalBefore, maxJobs, cancellationToken);

    public ValueTask<ObserverClaim> ClaimObserverDeliveriesAsync(
        ObserverClaimRequest request, CancellationToken cancellationToken = default)
        => inner.ClaimObserverDeliveriesAsync(request, cancellationToken);

    public ValueTask ReportObserverDeliveriesAsync(
        ObserverDeliveryReport report, CancellationToken cancellationToken = default)
        => inner.ReportObserverDeliveriesAsync(report, cancellationToken);

    // Declared, not inherited. TryReportObserverDeliveriesAsync is a default interface member, so a
    // wrapper that leaves it out silently gets the default body - which calls the void twin and answers
    // Unreported, a value no store returns - and the fence verdict the inner store produced never
    // reaches the pump. Every new default member on IJobStore belongs here for the same reason.
    public ValueTask<ObserverReportOutcome> TryReportObserverDeliveriesAsync(
        ObserverDeliveryReport report, CancellationToken cancellationToken = default)
        => inner.TryReportObserverDeliveriesAsync(report, cancellationToken);

    public ValueTask<long> GetObserverCursorAsync(string observerId, CancellationToken cancellationToken = default)
        => inner.GetObserverCursorAsync(observerId, cancellationToken);

    public ValueTask<ObserverLag> GetObserverLagAsync(ObserverLagRequest request, CancellationToken cancellationToken = default)
        => inner.GetObserverLagAsync(request, cancellationToken);

    public ValueTask<IReadOnlyList<ObserverDeadLetterRecord>> ListObserverDeadLettersAsync(
        string observerId, CancellationToken cancellationToken = default)
        => inner.ListObserverDeadLettersAsync(observerId, cancellationToken);
}
