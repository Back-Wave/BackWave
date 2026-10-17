using System.Data.Common;
using BackWave.Storage;

namespace BackWave.Operations;

/// <summary>
/// The operator-actions surface: the administrative peer of the enqueue client and the monitor.
/// Cancel a job, move a waiting job's due time, requeue a failed job, pause and resume a queue, cap
/// a queue's concurrency, and trigger a recurring schedule on demand. Every action is stamped with
/// the acting operator's identity and recorded in the append-only audit log, so reach for this
/// rather than mutating the store directly.
/// </summary>
/// <param name="store">The storage adapter the actions are applied to.</param>
/// <param name="clock">
/// The time source used to stamp each action. Defaults to the system clock; a deterministic test
/// harness can supply a virtual clock. Every method also accepts an explicit instant for callers
/// that need to override it.
/// </param>
public sealed class BackWaveOperator(IJobStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // The Core dependencies BackWave Pro's relocated workflow-cancel extension drives through.
    // Internal — the Pro package reaches them via InternalsVisibleTo, so the free operator surface
    // exposes no Workflow API.
    internal IJobStore Store => store;
    internal TimeProvider Clock => _clock;

    /// <summary>
    /// Cancels a job. A job that has not started yet cancels immediately; a job that is currently
    /// running is asked to stop cooperatively and transitions to cancelled when its handler next
    /// checks for the request. The action is recorded against the acting operator.
    /// </summary>
    /// <param name="jobId">The id of the job to cancel.</param>
    /// <param name="actor">The acting operator's identity, recorded in the audit log.</param>
    /// <param name="now">The instant to stamp the action with. Defaults to the current instant.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the job was cancelled immediately, asked to stop cooperatively, or was not in a cancellable state.</returns>
    public ValueTask<CancelResult> CancelJobAsync(
        Guid jobId, string actor, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
        => store.CancelJobAsync(jobId, actor, now ?? _clock.GetUtcNow(), cancellationToken);

    /// <summary>
    /// Requeues a dead-lettered or quarantined job: it returns to scheduled with its attempt count
    /// reset to zero and runs again. A job in any other state is rejected unchanged. The action is
    /// recorded against the acting operator.
    /// </summary>
    /// <param name="jobId">The id of the job to requeue.</param>
    /// <param name="actor">The acting operator's identity, recorded in the audit log.</param>
    /// <param name="now">The instant to stamp the action with. Defaults to the current instant.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the job was requeued, or was not in a requeueable state.</returns>
    public ValueTask<RequeueResult> RequeueAsync(
        Guid jobId, string actor, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
        => store.RequeueAsync(jobId, actor, now ?? _clock.GetUtcNow(), cancellationToken);

    /// <summary>
    /// Moves a waiting job's due time: a job that is scheduled, or waiting on its parents, gets
    /// <paramref name="dueTime"/> as its new due time and keeps its id, payload, queue, and attempt
    /// count. A job that waits on parents starts no earlier than the new due time once its parents
    /// finish. A due time at or before now makes the job run as soon as a worker is free. A running,
    /// finished, or unknown job is rejected unchanged, so a rejection means it is too late to move the
    /// job. Pass <paramref name="transaction"/> to move the job atomically with your own database
    /// writes: both commit or roll back together. The action is recorded against the acting operator.
    /// </summary>
    /// <remarks>
    /// Keep a <paramref name="transaction"/> short. The job row stays locked until you commit. When the
    /// job waits on a parent, the parent's outcome report waits for your commit, because the report
    /// locks the child rows to release them. On SQLite, your transaction holds the database write lock,
    /// so every other write waits for your commit. Under REPEATABLE READ, SERIALIZABLE, or SNAPSHOT
    /// isolation, your transaction can get a serialization error from the database instead of
    /// <see cref="RescheduleResult.NotReschedulable"/>.
    /// </remarks>
    /// <param name="jobId">The id of the job to reschedule.</param>
    /// <param name="dueTime">The job's new due time.</param>
    /// <param name="actor">The acting operator's identity, recorded in the audit log.</param>
    /// <param name="transaction">
    /// An optional open transaction on the same database as the storage adapter. The new due time and
    /// the audit record are written on it and share its commit or rollback. Null moves the job at once.
    /// </param>
    /// <param name="now">The instant to stamp the action with. Defaults to the current instant.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the job was rescheduled, or was not in a state whose due time can move.</returns>
    /// <exception cref="NotSupportedException">A <paramref name="transaction"/> was supplied but the storage adapter does not support transactional writes, or the storage adapter does not implement reschedule.</exception>
    /// <exception cref="ArgumentException">The <paramref name="transaction"/> is not a transaction of the storage adapter's database provider, or, for the in-memory store, was not created by that store.</exception>
    /// <example>
    /// <code>
    /// var result = await backWaveOperator.RescheduleAsync(campaign.SendJobId, newSendAt, actor: userId);
    /// if (result == RescheduleResult.NotReschedulable)
    /// {
    ///     // The campaign is already sending or sent.
    /// }
    /// </code>
    /// </example>
    public ValueTask<RescheduleResult> RescheduleAsync(
        Guid jobId,
        DateTimeOffset dueTime,
        string actor,
        DbTransaction? transaction = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        if (transaction is not null && !store.SupportsTransactionalEnqueue)
        {
            throw new NotSupportedException(
                "This storage adapter does not support transactional writes " +
                "(SupportsTransactionalEnqueue is false); reschedule without a transaction instead.");
        }
        return store.RescheduleAsync(jobId, dueTime, actor, now ?? _clock.GetUtcNow(), transaction, cancellationToken);
    }

    /// <summary>
    /// Pauses a queue across the whole cluster: no worker claims work from it until it is resumed.
    /// Jobs already running are unaffected. The action is recorded against the acting operator.
    /// </summary>
    /// <param name="queue">The queue to pause.</param>
    /// <param name="actor">The acting operator's identity, recorded in the audit log.</param>
    /// <param name="now">The instant to stamp the action with. Defaults to the current instant.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes once the queue is paused.</returns>
    public ValueTask PauseQueueAsync(
        string queue, string actor, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
        => store.PauseQueueAsync(queue, actor, now ?? _clock.GetUtcNow(), cancellationToken);

    /// <summary>
    /// Resumes a paused queue, so workers may claim work from it again. The action is recorded
    /// against the acting operator.
    /// </summary>
    /// <param name="queue">The queue to resume.</param>
    /// <param name="actor">The acting operator's identity, recorded in the audit log.</param>
    /// <param name="now">The instant to stamp the action with. Defaults to the current instant.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes once the queue is resumed.</returns>
    public ValueTask ResumeQueueAsync(
        string queue, string actor, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
        => store.ResumeQueueAsync(queue, actor, now ?? _clock.GetUtcNow(), cancellationToken);

    /// <summary>
    /// Sets or clears a queue's cluster-wide concurrency limit: at most <paramref name="limit"/> jobs
    /// from the queue run at once across every worker in the cluster, and passing null removes the
    /// cap. Takes effect on the next claim; jobs already running are unaffected. The action is
    /// recorded against the acting operator.
    /// </summary>
    /// <param name="queue">The queue whose limit to set.</param>
    /// <param name="limit">The maximum number of concurrently-running jobs allowed in the queue, or null to remove the limit.</param>
    /// <param name="actor">The acting operator's identity, recorded in the audit log.</param>
    /// <param name="now">The instant to stamp the action with. Defaults to the current instant.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes once the limit is applied.</returns>
    public ValueTask SetConcurrencyLimitAsync(
        string queue, int? limit, string actor, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
        => store.SetConcurrencyLimitAsync(queue, limit, actor, now ?? _clock.GetUtcNow(), cancellationToken);

    /// <summary>
    /// Mints one instance of a recurring schedule right now, without moving the schedule's cursor or
    /// disturbing its future ticks — a one-off run on demand. An unknown schedule is rejected. The
    /// action is recorded against the acting operator.
    /// </summary>
    /// <param name="scheduleId">The id of the schedule to trigger.</param>
    /// <param name="actor">The acting operator's identity, recorded in the audit log.</param>
    /// <param name="now">The instant the minted instance becomes due. Defaults to the current instant.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether an instance was minted, or no schedule with that id exists.</returns>
    public ValueTask<TriggerScheduleResult> TriggerScheduleNowAsync(
        string scheduleId, string actor, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
        => store.TriggerScheduleNowAsync(scheduleId, actor, now ?? _clock.GetUtcNow(), cancellationToken);

    /// <summary>
    /// The operator audit trail for one target — a job id, a queue name, or a schedule id — oldest
    /// entry first. Every operator action contributes exactly one record.
    /// </summary>
    /// <param name="target">The job id, queue name, or schedule id whose audit trail to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The audit records for the target, oldest first; empty when none exist.</returns>
    public ValueTask<IReadOnlyList<OperatorAuditRecord>> ListAuditRecordsAsync(
        string target, CancellationToken cancellationToken = default)
        => store.ListAuditRecordsAsync(target, cancellationToken);
}
