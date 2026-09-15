using System.Data.Common;
using BackWave.Core;
using BackWave.Storage;

namespace BackWave.Hosting.Tests;

/// <summary>
/// A store built against the N-1 interface: it forwards every member EXCEPT the relinquish, which it
/// leaves to the default body on <see cref="IJobStore"/>. That body relinquishes nothing and returns
/// zero, so an adapter compiled before this feature keeps today's behaviour when a newer Shell calls
/// it - the Leases lapse and the expiry path sweeps them. A rolling deploy runs exactly this pairing.
/// </summary>
public sealed class PreRelinquishStore(IJobStore inner) : IJobStore
{
    public bool SupportsTransactionalEnqueue => inner.SupportsTransactionalEnqueue;

    public ValueTask<EnqueueResult> EnqueueAsync(
        NewJob job, DateTimeOffset now, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
        => inner.EnqueueAsync(job, now, transaction, cancellationToken);

    public ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(
        ClaimRequest request, CancellationToken cancellationToken = default)
        => inner.ClaimAsync(request, cancellationToken);

    public ValueTask<OutcomeResult> ReportOutcomeAsync(
        Guid jobId, string workerId, int attempt, JobOutcome outcome, DateTimeOffset now,
        string? failureDetail = null,
        JobTags? addedTags = null,
        ReadOnlyMemory<byte>? output = null,
        CancellationToken cancellationToken = default)
        => inner.ReportOutcomeAsync(
            jobId, workerId, attempt, outcome, now, failureDetail, addedTags, output, cancellationToken);

    public ValueTask<IReadOnlyList<OutcomeReportResult>> ReportOutcomesAsync(
        IReadOnlyList<OutcomeReport> batch, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.ReportOutcomesAsync(batch, now, cancellationToken);

    public ValueTask<IReadOnlyList<HeartbeatResult>> HeartbeatAsync(
        string workerId, IReadOnlyList<Guid> jobIds, TimeSpan leaseDuration, DateTimeOffset now,
        CancellationToken cancellationToken = default)
        => inner.HeartbeatAsync(workerId, jobIds, leaseDuration, now, cancellationToken);

    public ValueTask<int> ExpireLeasesAsync(
        DateTimeOffset now, int maxJobs, IReadOnlyList<string> queues, RetryDisposition disposition,
        CancellationToken cancellationToken = default)
        => inner.ExpireLeasesAsync(now, maxJobs, queues, disposition, cancellationToken);

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
        => inner.ListSchedulesAsync(cancellationToken);

    public ValueTask<int> MintDueAsync(
        IReadOnlyList<MintDecision> decisions, CancellationToken cancellationToken = default)
        => inner.MintDueAsync(decisions, cancellationToken);

    public ValueTask SetConcurrencyLimitAsync(
        string queue, int? limit, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.SetConcurrencyLimitAsync(queue, limit, actor, now, cancellationToken);

    public ValueTask<JobRecord?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetJobAsync(jobId, cancellationToken);

    public ValueTask<ReadOnlyMemory<byte>?> GetJobOutputAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetJobOutputAsync(jobId, cancellationToken);

    public ValueTask<IReadOnlyList<JobTransition>> GetJobHistoryAsync(
        Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetJobHistoryAsync(jobId, cancellationToken);

    public ValueTask<IReadOnlyList<JobRecord>> ListJobsAsync(
        JobQuery query, CancellationToken cancellationToken = default)
        => inner.ListJobsAsync(query, cancellationToken);

    public ValueTask<IReadOnlyList<QueueStateCount>> CountJobsAsync(CancellationToken cancellationToken = default)
        => inner.CountJobsAsync(cancellationToken);

    public ValueTask<IReadOnlyList<TagFacet>> FacetAsync(
        string key, JobQuery? baseQuery = null, int maxResults = int.MaxValue,
        CancellationToken cancellationToken = default)
        => inner.FacetAsync(key, baseQuery, maxResults, cancellationToken);

    public ValueTask<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(
        TagSuggestQuery query, CancellationToken cancellationToken = default)
        => inner.SuggestTagsAsync(query, cancellationToken);

    public ValueTask<IReadOnlyList<QueueSettings>> ListQueueSettingsAsync(CancellationToken cancellationToken = default)
        => inner.ListQueueSettingsAsync(cancellationToken);

    public ValueTask<DependencyEdges> GetDependencyEdgesAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetDependencyEdgesAsync(jobId, cancellationToken);

    public ValueTask<WorkflowEnqueueResult> EnqueueWorkflowAsync(
        WorkflowDefinition workflow, DateTimeOffset now, DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
        => inner.EnqueueWorkflowAsync(workflow, now, transaction, cancellationToken);

    public ValueTask<IReadOnlyList<WorkflowSnapshot>> ListWorkflowsAsync(CancellationToken cancellationToken = default)
        => inner.ListWorkflowsAsync(cancellationToken);

    public ValueTask<WorkflowGraph?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default)
        => inner.GetWorkflowAsync(workflowId, cancellationToken);

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

    public ValueTask<ObserverLag> GetObserverLagAsync(
        ObserverLagRequest request, CancellationToken cancellationToken = default)
        => inner.GetObserverLagAsync(request, cancellationToken);

    public ValueTask<IReadOnlyList<ObserverDeadLetterRecord>> ListObserverDeadLettersAsync(
        string observerId, CancellationToken cancellationToken = default)
        => inner.ListObserverDeadLettersAsync(observerId, cancellationToken);
}
