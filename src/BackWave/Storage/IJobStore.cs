using System.Data.Common;

namespace BackWave.Storage;

// Contributor breadcrumb: this is the Storage Contract seam; see docs/storage-contract.md for the
// normative semantics. Implementations MUST NOT consult their own clock for any semantic decision —
// time is always passed in as an explicit `now` parameter.
/// <summary>
/// The persistence contract every storage adapter implements. It is the single seam between
/// BackWave's engine and a backing store (a database, an embedded file, an in-memory reference).
/// Every method that makes a time-dependent decision takes the current instant as an explicit
/// <c>now</c> parameter: an implementation MUST NOT read its own clock for any semantic decision,
/// so behavior is fully determined by its inputs.
/// </summary>
public interface IJobStore
{
    /// <summary>
    /// The single capability flag: whether <see cref="EnqueueAsync"/> and
    /// <see cref="EnqueueWorkflowAsync"/> can enlist in a caller-supplied ADO.NET transaction, so a
    /// job commits or rolls back atomically with the caller's own writes. Implementations that
    /// return <c>false</c> here MUST reject any non-null transaction passed to those methods loudly,
    /// never silently ignore it.
    /// </summary>
    bool SupportsTransactionalEnqueue { get; }

    /// <summary>
    /// How much of each job's history this store records: whether it keeps the append-only timeline of
    /// state changes at all, and whether it keeps the diagnostic failure detail alongside a failed
    /// attempt. This is the single source of truth for the effective policy, so a monitoring surface can
    /// tell a genuinely empty timeline apart from one that is empty because recording is turned off.
    /// Implementations that do not surface their own policy report
    /// <see cref="JobHistoryPolicy.TransitionsAndFailureDetail"/>, the recording default.
    /// </summary>
    JobHistoryPolicy HistoryPolicy => JobHistoryPolicy.TransitionsAndFailureDetail;

    /// <summary>
    /// The size and batch limits this store enforces: payload and output caps, claim and purge batch
    /// ceilings, and the monitor page cap. This is the single source of truth for the effective
    /// bounds, so a monitoring surface can size its own reads under a limit the store actually
    /// applies — for example, paging under the monitor page cap. Implementations that do not surface
    /// their own bounds report <see cref="StoreBounds.Default"/>.
    /// </summary>
    StoreBounds Bounds => StoreBounds.Default;

    /// <summary>
    /// Creates one job. Implementations MUST reject a duplicate id and any bound violation by
    /// returning the matching <see cref="EnqueueResult"/> — never silently truncate, never replace
    /// an existing job. When <paramref name="transaction"/> is supplied the job MUST commit or roll
    /// back atomically with the caller's own writes, so a rolled-back transaction means the job
    /// never existed; an implementation whose <see cref="SupportsTransactionalEnqueue"/> is
    /// <c>false</c> MUST reject a non-null transaction loudly rather than ignore it.
    /// </summary>
    /// <param name="job">The job to create, including its caller-supplied id, payload, queue, due time, and any gating parents.</param>
    /// <param name="now">The current instant, used for any time-dependent decision (the store must not read its own clock).</param>
    /// <param name="transaction">An optional caller-owned transaction to enlist in for atomic enqueue; null for a standalone insert.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see cref="EnqueueResult.Ok"/> on success, or the reason the job was rejected:
    /// <see cref="EnqueueResult.Duplicate"/>, <see cref="EnqueueResult.PayloadTooLarge"/>,
    /// <see cref="EnqueueResult.WireNameTooLong"/>, <see cref="EnqueueResult.UnknownParent"/>, or
    /// <see cref="EnqueueResult.TooManyParents"/>.
    /// </returns>
    ValueTask<EnqueueResult> EnqueueAsync(
        NewJob job, DateTimeOffset now, DbTransaction? transaction = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically leases up to <see cref="ClaimRequest.MaxJobs"/> currently-due jobs from the
    /// requested Queues to the requesting worker. Implementations MUST claim each returned job to at
    /// most one caller — no double-claim under concurrency — and MUST NOT return a job whose due time
    /// is after the request's <see cref="ClaimRequest.Now"/>. The claim itself increments each
    /// returned job's Attempt, and a claimed job stays invisible to other claimers until its lease
    /// lapses or it reaches a terminal state. A Paused Queue yields nothing.
    /// </summary>
    /// <param name="request">The claim parameters: the worker id, the candidate Queues, the batch cap, the lease duration, and the current instant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The leased jobs (each with its incremented Attempt and active lease, and carrying its enqueue-time tags so the claiming worker needs no second read); an empty list when nothing is due or all candidate Queues are paused or at their concurrency limit.</returns>
    ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims due jobs exactly as <see cref="ClaimAsync"/> does, and additionally reports the earliest
    /// future instant at which a currently-empty claim could begin to return work through the passage of
    /// time alone - the next scheduled job's due time. This lets an idle worker sleep until that instant
    /// rather than poll at a fixed rate (see <c>WorkerGroupOptions.MaxPollInterval</c>).
    /// <para>
    /// The default implementation delegates to <see cref="ClaimAsync"/> and reports
    /// <see cref="ClaimResult.NextDue"/> as <c>null</c> (unknown), so an adapter that does not override it
    /// keeps the fixed-rate behavior with no change. An overriding adapter MUST compute NextDue against a
    /// snapshot consistent with the claim it just performed - a same-connection read taken as part of, or
    /// immediately after, the claim is acceptable - and MUST report a value at or before <see cref="ClaimRequest.Now"/>
    /// whenever work is due now but was withheld by a concurrency limit or the batch cap, so the caller
    /// never extends its backoff past due-now pressure. A paused queue is excluded: its work does not become
    /// claimable through the passage of time, so it never forces NextDue to Now and never contributes a
    /// future due time. NextDue never affects correctness; it only schedules the next poll, so an inexact
    /// value costs latency, nothing else.
    /// </para>
    /// </summary>
    /// <param name="request">The claim parameters, identical to <see cref="ClaimAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The leased jobs and the next future due time for idle-poll scheduling.</returns>
    async ValueTask<ClaimResult> ClaimBatchAsync(ClaimRequest request, CancellationToken cancellationToken = default)
    {
        var jobs = await ClaimAsync(request, cancellationToken).ConfigureAwait(false);
        return new ClaimResult(jobs, NextDue: null);
    }

    /// <summary>
    /// Applies the outcome of one execution attempt and appends the resulting transition to the
    /// job's history atomically. The <paramref name="workerId"/> and <paramref name="attempt"/> pair
    /// fences the lease: an implementation MUST apply the outcome only when the caller still holds
    /// the live lease for exactly this Attempt, and otherwise change nothing and return
    /// <see cref="OutcomeResult.StaleLease"/>. This is what makes a late report from an
    /// isolated-then-recovered worker harmless.
    /// <para>
    /// <paramref name="failureDetail"/> is optional, write-only diagnostics (the captured exception
    /// type, message, and stack) recorded only on a <see cref="JobOutcome.Failure"/> outcome and
    /// left null for every other outcome. It is bounded for storage: detail longer than the store's
    /// failure-detail cap MUST be TRUNCATED, never rejected, because it is diagnostics, not
    /// functional data.
    /// </para>
    /// <para>
    /// <paramref name="addedTags"/> is an optional set of tags the handler buffered during this
    /// Attempt. It rides the same lease fence: when the outcome is applied the set unions onto the
    /// job's existing tags (re-adding an identical tag is a no-op — set semantics), and when the
    /// report is fenced out the buffered tags are discarded with the rest of the write, so a stale
    /// node never leaves split-brain annotations.
    /// </para>
    /// <para>
    /// <paramref name="output"/> is an optional opaque result blob the handler emitted. It rides the
    /// same lease fence and is persisted to the job's output column ONLY on a
    /// <see cref="JobOutcome.Success"/> outcome (every other outcome, including a graceful
    /// <see cref="JobOutcome.Failure"/>, persists no output); a fenced-out report discards it. It is
    /// written atomically with the success transition and is retained independently of history
    /// policy — it is functional data a dependent job later reads, not diagnostics. Output larger
    /// than the store's output cap MUST be REJECTED loudly (it is undeserializable if clipped),
    /// never truncated.
    /// </para>
    /// </summary>
    /// <param name="jobId">The id of the job whose attempt is being reported.</param>
    /// <param name="workerId">The id of the worker reporting; together with <paramref name="attempt"/> it fences the lease.</param>
    /// <param name="attempt">The Attempt number this outcome is for; a mismatch with the live lease fences the report out.</param>
    /// <param name="outcome">The execution outcome to apply — success, failure (with the retry-or-dead-letter decision), cancellation, or unroutable.</param>
    /// <param name="now">The current instant, used for the transition timestamp and any time-dependent decision (the store must not read its own clock).</param>
    /// <param name="failureDetail">Optional diagnostics recorded only on a failure outcome; truncated to the store's cap, never rejected.</param>
    /// <param name="addedTags">Optional tag delta to union onto the job's tags when the outcome is applied; discarded if the report is fenced out.</param>
    /// <param name="output">Optional result blob persisted only on a success outcome; rejected (never truncated) if it exceeds the store's output cap.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="OutcomeResult.Applied"/> when the outcome was applied; <see cref="OutcomeResult.StaleLease"/> when the caller no longer held the live lease for this Attempt and nothing changed.</returns>
    /// <exception cref="JobOutputTooLargeException">A success outcome carried <paramref name="output"/> larger than the store's output cap; the write is rejected, never truncated.</exception>
    ValueTask<OutcomeResult> ReportOutcomeAsync(
        Guid jobId, string workerId, int attempt, JobOutcome outcome, DateTimeOffset now,
        string? failureDetail = null,
        JobTags? addedTags = null,
        ReadOnlyMemory<byte>? output = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a batch of execution outcomes in one operation, returning one result per row in the same
    /// order. Each row carries the full payload of a single <see cref="ReportOutcomeAsync"/> call and is
    /// fenced INDEPENDENTLY by its own <c>(WorkerId, Attempt)</c> pair: a row whose caller still holds the
    /// live lease for exactly that Attempt is applied and reported <see cref="OutcomeResult.Applied"/>;
    /// any other row changes nothing and is reported <see cref="OutcomeResult.StaleLease"/>. A batch may
    /// freely mix applied and stale rows. The semantics of each row — the failure-detail truncation, the
    /// tag union, the success-only output persistence, and the loud rejection of over-cap output — are
    /// exactly those of <see cref="ReportOutcomeAsync"/>; this method only amortizes the per-row writes.
    /// <para>
    /// The default implementation applies the rows one by one through <see cref="ReportOutcomeAsync"/>, so
    /// every store is correct without overriding it; an adapter MAY override this to apply the whole batch
    /// in a single fenced round-trip for throughput, provided it preserves the per-row fence and per-row
    /// semantics verbatim. An empty batch applies nothing and returns an empty list.
    /// </para>
    /// </summary>
    /// <param name="batch">The outcome rows to apply, each carrying its own job id, worker id, attempt, outcome, and optional failure detail, tag delta, and output.</param>
    /// <param name="now">The current instant, used for every transition timestamp and any time-dependent decision (the store must not read its own clock).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One result per input row, in the same order, each pairing the row's job id with whether its outcome was <see cref="OutcomeResult.Applied"/> or fenced out as <see cref="OutcomeResult.StaleLease"/>.</returns>
    /// <exception cref="JobOutputTooLargeException">A success row carried output larger than the store's output cap; that write is rejected, never truncated.</exception>
    async ValueTask<IReadOnlyList<OutcomeReportResult>> ReportOutcomesAsync(
        IReadOnlyList<OutcomeReport> batch, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var results = new OutcomeReportResult[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            var row = batch[i];
            var result = await ReportOutcomeAsync(
                row.JobId, row.WorkerId, row.Attempt, row.Outcome, now,
                row.FailureDetail, row.AddedTags, row.Output, cancellationToken).ConfigureAwait(false);
            results[i] = new OutcomeReportResult(row.JobId, result);
        }
        return results;
    }

    /// <summary>
    /// Renews the leases on a batch of in-flight jobs and, in the same round-trip, reports each
    /// job's cancellation-requested flag — the cooperative-cancellation channel. Implementations
    /// MUST extend the lease only for a job this worker still holds; for any job the worker no
    /// longer holds (lease lapsed, job terminal, or never held) the result MUST carry
    /// <c>Renewed = false</c>, which tells the worker to stop applying that job's effects.
    /// </summary>
    /// <param name="workerId">The id of the worker whose leases are being renewed.</param>
    /// <param name="jobIds">The ids of the in-flight jobs to renew.</param>
    /// <param name="leaseDuration">How far past <paramref name="now"/> to extend each renewed lease.</param>
    /// <param name="now">The current instant from which the new lease expiry is measured (the store must not read its own clock).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One result per requested job: whether the lease was renewed and whether cancellation has been requested for it.</returns>
    ValueTask<IReadOnlyList<HeartbeatResult>> HeartbeatAsync(
        string workerId,
        IReadOnlyList<Guid> jobIds,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Disposes up to <paramref name="maxJobs"/> jobs whose lease has expired (as of
    /// <paramref name="now"/>) in the given <paramref name="queues"/>. Because the original claim
    /// already counted the Attempt, each expired job is either rescheduled at the backoff instant
    /// carried by <paramref name="disposition"/> or, once its attempt ceiling is reached,
    /// Dead-Lettered. Implementations MUST dispose each expired lease exactly once even when several
    /// nodes sweep concurrently, and MUST treat <paramref name="disposition"/> as pure data, never
    /// as executable code. Restricting the sweep to the caller's served Queues lets a job's own
    /// policy govern regardless of which node performs the sweep.
    /// </summary>
    /// <param name="now">The current instant; a lease is expired when its expiry is at or before this.</param>
    /// <param name="maxJobs">The maximum number of expired leases to dispose in this call, bounding the sweep.</param>
    /// <param name="queues">The Queues this caller serves and is permitted to sweep.</param>
    /// <param name="disposition">Pure data describing, per Attempt, the backoff reschedule instant or the dead-letter decision.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of expired leases disposed in this call.</returns>
    ValueTask<int> ExpireLeasesAsync(
        DateTimeOffset now,
        int maxJobs,
        IReadOnlyList<string> queues,
        Core.RetryDisposition disposition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests cancellation of one job. A job not yet running (Scheduled or AwaitingParent)
    /// transitions to Cancelled immediately; a job currently leased instead has its
    /// cancellation-requested flag set and cancels cooperatively when its worker next heartbeats. A
    /// job already terminal cannot be cancelled. Implementations MUST append the operator audit
    /// record naming <paramref name="actor"/> atomically with the effect, so the audit trail and the
    /// state change can never disagree.
    /// </summary>
    /// <param name="jobId">The id of the job to cancel.</param>
    /// <param name="actor">Who requested the cancellation; recorded in the audit log.</param>
    /// <param name="now">The current instant, used for the transition and audit timestamps (the store must not read its own clock).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see cref="CancelResult.CancelledImmediately"/> when the job was not yet running and is now
    /// Cancelled; <see cref="CancelResult.CancellationRequested"/> when a leased job was flagged for
    /// cooperative cancellation; <see cref="CancelResult.NotCancellable"/> when the job was absent or
    /// already terminal.
    /// </returns>
    ValueTask<CancelResult> CancelJobAsync(
        Guid jobId,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Operator requeue of a failed-and-parked job: a Dead-Lettered or Quarantined job returns to
    /// Scheduled with its Attempt reset to 0 and becomes due at <paramref name="now"/>. A job in any
    /// other state is rejected without effect. Implementations MUST append the operator audit record
    /// naming <paramref name="actor"/> atomically with the requeue.
    /// </summary>
    /// <param name="jobId">The id of the job to requeue.</param>
    /// <param name="actor">Who requested the requeue; recorded in the audit log.</param>
    /// <param name="now">The current instant; the requeued job becomes due at this time (the store must not read its own clock).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="RequeueResult.Requeued"/> when a Dead-Lettered or Quarantined job was returned to Scheduled; <see cref="RequeueResult.NotRequeueable"/> when the job was absent or in a state that cannot be requeued.</returns>
    ValueTask<RequeueResult> RequeueAsync(
        Guid jobId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses a Queue cluster-wide: while paused, the Queue yields nothing to a claim. Jobs already
    /// leased are untouched. The effect is idempotent (pausing an already-paused Queue changes
    /// nothing), but implementations MUST still append the operator audit record naming
    /// <paramref name="actor"/> on every call.
    /// </summary>
    /// <param name="queue">The Queue to pause.</param>
    /// <param name="actor">Who paused the Queue; recorded in the audit log.</param>
    /// <param name="now">The current instant, used for the audit timestamp (the store must not read its own clock).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask PauseQueueAsync(
        string queue, string actor, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears a Queue's paused flag, so claiming resumes. The effect is idempotent, but the operator
    /// audit record naming <paramref name="actor"/> MUST be appended atomically on every call.
    /// </summary>
    /// <param name="queue">The Queue to resume.</param>
    /// <param name="actor">Who resumed the Queue; recorded in the audit log.</param>
    /// <param name="now">The current instant, used for the audit timestamp (the store must not read its own clock).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask ResumeQueueAsync(
        string queue, string actor, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mints exactly one instance of a recurring schedule immediately (due at <paramref name="now"/>)
    /// without advancing the schedule's cursor or disturbing its future ticks. An unknown schedule is
    /// rejected without effect. Implementations MUST append the operator audit record naming
    /// <paramref name="actor"/> atomically with the mint.
    /// </summary>
    /// <param name="scheduleId">The id of the recurring schedule to fire once now.</param>
    /// <param name="actor">Who triggered the schedule; recorded in the audit log.</param>
    /// <param name="now">The current instant; the minted instance is due at this time (the store must not read its own clock).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="TriggerScheduleResult.Triggered"/> when one instance was minted without moving the cursor; <see cref="TriggerScheduleResult.ScheduleNotFound"/> when no schedule with that id exists.</returns>
    ValueTask<TriggerScheduleResult> TriggerScheduleNowAsync(
        string scheduleId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the operator audit records for one target — a job id, a Queue name, or a schedule id —
    /// oldest first. The log is append-only and every operator action contributes exactly one record,
    /// so this is the authoritative trail of who did what to the target.
    /// </summary>
    /// <param name="target">The job id, Queue name, or schedule id whose audit records to read.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The target's audit records, oldest first; empty when nothing has acted on it.</returns>
    ValueTask<IReadOnlyList<OperatorAuditRecord>> ListAuditRecordsAsync(
        string target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a recurring schedule, or replaces one that already exists with the same id. For a
    /// newly created schedule the cursor starts at the time of the upsert, so it mints from now
    /// forward rather than backfilling.
    /// </summary>
    /// <param name="schedule">The schedule to create or replace, including its id, cron expression, and job template.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask UpsertScheduleAsync(ScheduleRecord schedule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a recurring schedule. Jobs the schedule has already minted are left untouched — only
    /// future minting stops. Removing an unknown schedule has no effect.
    /// </summary>
    /// <param name="scheduleId">The id of the schedule to remove.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask RemoveScheduleAsync(string scheduleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads every recurring schedule with the facts needed to decide minting, for the planner and
    /// for monitor reads.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A snapshot of all schedules and their mint-relevant state.</returns>
    ValueTask<IReadOnlyList<ScheduleSnapshot>> ListSchedulesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a batch of mint decisions: per schedule, atomically advance the cursor and insert the
    /// minted jobs. Implementations MUST skip a decision whole when its expected cursor no longer
    /// matches the schedule's current cursor — that means another node already minted those ticks —
    /// so the same ticks are never minted twice across the cluster.
    /// </summary>
    /// <param name="decisions">The per-schedule mint decisions to apply, each carrying the cursor it expects and the jobs to mint.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The total number of jobs minted across all applied decisions (decisions skipped for a stale cursor contribute none).</returns>
    ValueTask<int> MintDueAsync(
        IReadOnlyList<Core.MintDecision> decisions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets or clears a Queue's cluster-wide concurrency limit (pass null to clear). A Queue's slot
    /// usage is the count of its currently-leased jobs, so a slot frees by construction the moment a
    /// job reaches a terminal state or its lease expires — implementations do not track a separate
    /// in-use counter. The effect is idempotent (re-applying the same limit changes nothing), but
    /// implementations MUST still append the operator audit record naming <paramref name="actor"/>
    /// on every call.
    /// </summary>
    /// <param name="queue">The Queue whose limit to set.</param>
    /// <param name="limit">The maximum number of concurrently-leased jobs allowed in the Queue, or null to remove the limit.</param>
    /// <param name="actor">Who set the limit; recorded in the audit log.</param>
    /// <param name="now">The current instant, used for the audit timestamp (the store must not read its own clock).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask SetConcurrencyLimitAsync(
        string queue, int? limit, string actor, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one job's committed snapshot by id, for monitoring.
    /// </summary>
    /// <param name="jobId">The id of the job to read.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The job's current committed state, or null if no job with that id exists.</returns>
    ValueTask<JobRecord?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one job's output blob by id — the opaque result value a handler emitted on its
    /// successful Attempt. Implementations read only the output column, so a large blob never rides
    /// the listing or claim path. This is the read a dependent job resolves to pull a parent's
    /// result.
    /// </summary>
    /// <param name="jobId">The id of the job whose output to read.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The job's output blob, or null when the job never set output or no job with that id exists.</returns>
    ValueTask<ReadOnlyMemory<byte>?> GetJobOutputAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one job's transition log — its append-only history of state changes, oldest first. Each
    /// state-changing operation appends exactly one entry atomically with the change, the timeline is
    /// bounded per job by the store's per-job transition cap (older entries age out beyond the cap),
    /// and it is deleted with the job under retention.
    /// </summary>
    /// <param name="jobId">The id of the job whose history to read.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The job's transitions, oldest first; an empty list for an unknown job.</returns>
    ValueTask<IReadOnlyList<JobTransition>> GetJobHistoryAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the jobs matching a filter, for monitoring. The page size is clamped to the store's
    /// maximum monitor page size regardless of what the query requests.
    /// </summary>
    /// <param name="query">The filter, pagination cursor, sort direction, and page size to apply.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The matching jobs in the requested sort order, capped at the store's maximum page size.</returns>
    ValueTask<IReadOnlyList<JobRecord>> ListJobsAsync(JobQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads job counts grouped by Queue and state — the queue depths — for monitoring.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One count per (Queue, state) pair that has at least one job.</returns>
    ValueTask<IReadOnlyList<QueueStateCount>> CountJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Faceting monitor read: groups jobs by one tag dimension and counts them. For every distinct
    /// value carried under <paramref name="key"/> it returns one <see cref="TagFacet"/> whose count
    /// is the number of DISTINCT jobs carrying <c>(key, value)</c> — never the number of underlying
    /// tag rows. A job with several values under the same key (for example <c>variant→BRCA1</c> and
    /// <c>variant→TP53</c>) contributes once to each of its values; it is otherwise counted once per
    /// value.
    /// <para>
    /// The empty-string sentinel key (<c>""</c>) facets Labels — each Label's value mapped to the
    /// number of jobs carrying it — mirroring the structural split between a Label (a value with no
    /// key) and a Keyed tag. A non-empty key facets that keyed dimension (for example <c>"tenant"</c>
    /// gives per-tenant job counts).
    /// </para>
    /// <para>
    /// <paramref name="baseQuery"/> optionally scopes the population FIRST, using exactly the same
    /// filter predicates <see cref="ListJobsAsync"/> applies (state, queue, wire name, schedule id,
    /// and AND-ed tag predicates); the facet is then computed over only the jobs in that scope (for
    /// example "within Quarantined jobs on the <c>lab</c> queue, break down by <c>tenant</c>"). The
    /// query's pagination fields do not apply — faceting always counts the whole matching population,
    /// never a single page. A null <paramref name="baseQuery"/> facets over all jobs.
    /// </para>
    /// <para>
    /// Results are ordered by count descending, with the value ascending (ordinal) as a stable
    /// tiebreak, so the result is deterministic and identical across every adapter implementation.
    /// Exactly one key is faceted per call; multi-key cross-tabs, time-bucketing, and cached counts
    /// are out of scope.
    /// </para>
    /// <para>
    /// <paramref name="maxResults"/> caps how many buckets are returned: the store keeps the highest
    /// <paramref name="maxResults"/> buckets in the count-descending order above and drops the rest,
    /// so a dimension with thousands of distinct values still returns a small, bounded result. The cap
    /// applies AFTER counting — the group-and-count still runs over the whole scoped population, only
    /// the returned rows are limited. A value at or below zero returns no buckets; the default returns
    /// every bucket (uncapped).
    /// </para>
    /// </summary>
    /// <param name="key">The tag key to facet by; the empty string facets Labels instead of a keyed dimension.</param>
    /// <param name="baseQuery">An optional filter that scopes which jobs are counted; null facets over all jobs. Its pagination and sort fields are ignored.</param>
    /// <param name="maxResults">The maximum number of buckets to return, keeping the highest-count buckets; defaults to returning every bucket.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Up to <paramref name="maxResults"/> buckets, each a distinct value under the faceted key with its distinct-job count, ordered by count descending then value ascending.</returns>
    ValueTask<IReadOnlyList<TagFacet>> FacetAsync(
        string key, JobQuery? baseQuery = null, int maxResults = int.MaxValue, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tag Suggest monitor read: a case-insensitive prefix completion over the Tags present in the
    /// store, used to help an operator compose an exact Tag filter by typing rather than by clicking a
    /// Tag on a job already in view. It answers "what Tags start with what I have typed?" and returns
    /// candidates carrying the canonical stored casing, so picking one composes a Tag predicate that is
    /// then guaranteed to match. The suggest never filters jobs and never promises a suggested Tag has
    /// matches under any current filter — only that the Tag exists somewhere in the store.
    /// <para>
    /// The read has two stages, both served by this one method through <see cref="TagSuggestQuery"/>:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <b>Stage one</b> — <see cref="TagSuggestQuery.Key"/> is null. Labels and keys are suggested
    /// together: each matching Label value is returned as a Label suggestion, and each distinct keyed
    /// dimension whose key matches is returned as a key suggestion (a drill-in). Labels sort first as a
    /// block, then keys, each block ordered as below.
    /// </item>
    /// <item>
    /// <b>Stage two</b> — <see cref="TagSuggestQuery.Key"/> is a specific key (the empty string selects
    /// the Label dimension). The distinct values carried under that key whose value matches the prefix
    /// are returned as value suggestions.
    /// </item>
    /// </list>
    /// <para>
    /// Matching is <b>prefix</b> (not substring) and case-insensitive: ASCII case folding
    /// (<c>a</c>–<c>z</c> equals <c>A</c>–<c>Z</c>) is guaranteed identical across every adapter; case
    /// folding beyond ASCII follows the underlying store and is not guaranteed. An empty prefix matches
    /// everything. Ordering within each block is lexicographic by the ASCII-folded token, with the
    /// canonical (ordinal) token as a stable tiebreak — the same order feeds the keyset cursor.
    /// </para>
    /// <para>
    /// The read is <b>global</b> (every Tag in the store), never scoped to a job filter, so it is a
    /// pure index-range scan. It is paged by a keyset cursor: pass the last returned
    /// <see cref="TagSuggestion"/> as <see cref="TagSuggestQuery.After"/> to fetch the next window.
    /// <see cref="TagSuggestQuery.MaxResults"/> is clamped to at least one and at most
    /// <see cref="TagSuggestQuery.MaxSuggestResults"/>.
    /// </para>
    /// </summary>
    /// <param name="query">The prefix, optional key (stage selector), keyset cursor, and window size for the suggest.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Up to the clamped <see cref="TagSuggestQuery.MaxResults"/> suggestions, in the lexicographic order described above; empty when nothing matches beyond the cursor.</returns>
    ValueTask<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(
        TagSuggestQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads each Queue's operational settings — its paused flag and configured concurrency-limit cap
    /// — for monitoring. This is the read-side mirror of the pause, resume, and set-concurrency-limit
    /// writes; only Queues that have settings on record appear. In-use slots are NOT reported here:
    /// those are derived from the Leased count obtained via <see cref="CountJobsAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The settings of every Queue that has any on record; Queues with no settings are absent.</returns>
    ValueTask<IReadOnlyList<QueueSettings>> ListQueueSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the dependency gating edges around one job: the parents still non-terminal that gate it
    /// as a child, and the children waiting on it as a parent. An edge is deleted as each parent
    /// terminates, so the parent side is the STILL-gating set — not the child's full original parent
    /// history.
    /// </summary>
    /// <param name="jobId">The id of the job whose dependency edges to read.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The job's still-gating parents and its waiting children; both lists empty when the job has no live dependency edges.</returns>
    ValueTask<DependencyEdges> GetDependencyEdgesAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically inserts a whole workflow — every member job plus the workflow record — in one
    /// all-or-nothing operation: on any failure nothing is inserted (no orphan members, no workflow
    /// record). Members land like ordinary jobs, so a member with gating parents starts in
    /// AwaitingParent. Implementations MUST enforce intra-workflow containment here — every gating
    /// parent of a member must itself be a member of the same workflow, else
    /// <see cref="WorkflowEnqueueResult.ContainmentViolation"/>. When <paramref name="transaction"/>
    /// is supplied the whole graph MUST commit or roll back atomically with the caller's own writes;
    /// a store whose <see cref="SupportsTransactionalEnqueue"/> is <c>false</c> MUST reject a non-null
    /// transaction loudly.
    /// </summary>
    /// <param name="workflow">The workflow to insert: its members and the structural edges between them.</param>
    /// <param name="now">The current instant, used for any time-dependent decision (the store must not read its own clock).</param>
    /// <param name="transaction">An optional caller-owned transaction to enlist in for atomic enqueue; null for a standalone insert.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="WorkflowEnqueueResult.Ok"/> on success, or the reason the workflow was rejected — for example <see cref="WorkflowEnqueueResult.ContainmentViolation"/> when a member gates on a non-member.</returns>
    ValueTask<WorkflowEnqueueResult> EnqueueWorkflowAsync(
        WorkflowDefinition workflow, DateTimeOffset now, DbTransaction? transaction = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads every workflow, oldest first by creation time, for monitoring — each with its derived
    /// status and member count.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A snapshot of every workflow, oldest first.</returns>
    ValueTask<IReadOnlyList<WorkflowSnapshot>> ListWorkflowsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one workflow's full graph by id, for monitoring — its members with their current job
    /// state, the immutable structural edges between them, and the derived status.
    /// </summary>
    /// <param name="workflowId">The id of the workflow to read.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The workflow's full graph, or null if no workflow with that id exists.</returns>
    ValueTask<WorkflowGraph?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes at most <paramref name="maxJobs"/> jobs in the given terminal state class whose
    /// terminal instant is at or before <paramref name="terminalBefore"/>. The retention clock is
    /// always the terminal instant, never enqueue time. The call is bounded so a single sweep can
    /// never become a storm; the caller schedules repeated sweeps until a pass purges nothing.
    /// </summary>
    /// <param name="stateClass">Which class of terminal jobs to purge (succeeded-or-cancelled, or dead-lettered-or-quarantined).</param>
    /// <param name="terminalBefore">Only jobs that became terminal at or before this instant are eligible to purge.</param>
    /// <param name="maxJobs">The maximum number of jobs to delete in this call, bounding the sweep.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of jobs deleted in this call; zero means the pass found nothing eligible and sweeping can stop.</returns>
    ValueTask<int> PurgeTerminalAsync(
        TerminalStateClass stateClass, DateTimeOffset terminalBefore, int maxJobs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims a bounded batch of an observer's undelivered transition-log rows under a lease, in
    /// append order, for delivery. Implementations MUST let at most one node hold a given observer's
    /// claim lease at a time, so the happy path delivers each row once while a lapsed claim (after a
    /// crash or isolation) lets another node redeliver — at-least-once delivery without any leader
    /// election. The claim increments each claimed row's delivery attempt. An unacquired, empty claim
    /// MUST be returned when another node holds the lease or nothing is due. When history is disabled
    /// there are no rows to observe, so this always returns nothing.
    /// </summary>
    /// <param name="request">The claim parameters: the observer id, the worker claiming, the batch cap, the lease duration, and the current instant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An acquired claim holding the leased batch of rows to deliver, or an unacquired empty claim when another node holds the lease or nothing is due.</returns>
    ValueTask<ObserverClaim> ClaimObserverDeliveriesAsync(
        ObserverClaimRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports the outcome of delivering a previously-claimed batch, fenced by the claim lease: a
    /// worker that no longer holds the live lease changes nothing. Delivered and dead-lettered rows
    /// advance the durable cursor over the contiguous resolved prefix, while a row marked for retry
    /// holds the cursor. Implementations MUST make the cursor advance durable, so at-least-once
    /// delivery survives a crash.
    /// </summary>
    /// <param name="report">The per-row delivery results for the claimed batch, carrying the claim lease that fences the write.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask ReportObserverDeliveriesAsync(
        ObserverDeliveryReport report, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads an observer's durable delivery cursor, for monitoring — the global log position up to
    /// and including which every matching row has been delivered or dead-lettered. This is read-only
    /// and never moves the cursor.
    /// </summary>
    /// <param name="observerId">The id of the observer whose cursor to read.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The observer's cursor position, or <c>-1</c> when the observer has no cursor yet (nothing delivered).</returns>
    ValueTask<long> GetObserverCursorAsync(string observerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads an observer's delivery lag, for monitoring — how many matching transitions have appeared
    /// in the log that its durable cursor has not yet advanced past, and when the oldest of those
    /// occurred. Read-only and subscription-aware: only rows the observer would deliver are counted,
    /// so a narrowly-scoped observer is never reported as lagging behind rows it ignores. When history
    /// is disabled there is nothing to observe, so this reports a caught-up observer.
    /// </summary>
    /// <param name="request">The observer id and its subscription filter (states plus optional wire name and queue).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The observer's cursor, its count of pending matching transitions, and the age of the oldest.</returns>
    ValueTask<ObserverLag> GetObserverLagAsync(
        ObserverLagRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads an observer's dead-lettered deliveries, oldest first, for monitoring — metadata only,
    /// surfaced like dead-lettered jobs.
    /// </summary>
    /// <param name="observerId">The id of the observer whose dead letters to read.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The observer's dead-lettered deliveries, oldest first; empty for a healthy observer.</returns>
    ValueTask<IReadOnlyList<ObserverDeadLetterRecord>> ListObserverDeadLettersAsync(
        string observerId, CancellationToken cancellationToken = default);
}

/// <summary>The two retention classes for terminal jobs, each purged on its own keep window.</summary>
public enum TerminalStateClass
{
    /// <summary>Jobs that ended as intended — Succeeded and Cancelled — typically kept for a short window.</summary>
    SucceededOrCancelled,

    /// <summary>Jobs someone may still need to inspect — Dead-Lettered and Quarantined — typically kept for a longer window.</summary>
    DeadLetteredOrQuarantined,
}

/// <summary>A filter for a job listing or facet; every null or empty field simply adds no constraint, so it matches everything.</summary>
public sealed record JobQuery
{
    /// <summary>Match only jobs in this state; null matches any state.</summary>
    public JobState? State { get; init; }

    /// <summary>Match only jobs on this Queue; null matches any Queue.</summary>
    public string? Queue { get; init; }

    /// <summary>Match only jobs with this wire name (payload type identifier); null matches any wire name.</summary>
    public string? WireName { get; init; }

    /// <summary>Match only jobs minted by this recurring schedule; null matches jobs from any source.</summary>
    public string? ScheduleId { get; init; }

    /// <summary>
    /// Tag predicates AND-ed together and AND-composed with the scalar filters above: a job matches
    /// only when it satisfies EVERY predicate. An empty list adds no constraint (matches everything).
    /// OR and arbitrary boolean trees are out of scope — a caller wanting OR runs two queries.
    /// </summary>
    public IReadOnlyList<JobTagPredicate> TagPredicates { get; init; } = [];

    /// <summary>
    /// Pagination cursor: only jobs strictly beyond the cursor in the requested
    /// <see cref="SortDirection"/> are returned — for the oldest-first default that means a
    /// <see cref="JobRecord.Sequence"/> strictly greater, for newest-first strictly less (continuing
    /// toward older jobs). Pass the last returned row's Sequence to fetch the next page; null starts
    /// at the first page.
    /// </summary>
    public long? AfterSequence { get; init; }

    /// <summary>
    /// Sort order for the listing, by <see cref="JobRecord.Sequence"/>; defaults to oldest-first
    /// (ascending).
    /// </summary>
    public JobSortDirection SortDirection { get; init; } = JobSortDirection.OldestFirst;

    /// <summary>Requested page size; the store clamps it down to its maximum monitor page size.</summary>
    public int MaxResults { get; init; } = int.MaxValue;
}

/// <summary>
/// One tag filter, of three structural kinds — each asking whether a job's tags CONTAIN a matching
/// tag:
/// <list type="bullet">
/// <item><b>has-label</b> (<see cref="HasLabel"/>) — the job carries the Label with this value.</item>
/// <item><b>has key=value</b> (<see cref="HasKeyValue"/>) — the job carries this keyed tag.</item>
/// <item><b>has-key-any-value</b> (<see cref="HasKey"/>) — the job carries any tag under this key.</item>
/// </list>
/// Predicates on a <see cref="JobQuery"/> are AND-ed together; OR is out of scope (run two queries).
/// </summary>
public sealed record JobTagPredicate
{
    private JobTagPredicate(string key, string? value)
    {
        Key = key;
        Value = value;
    }

    /// <summary>The key to match; the empty string for a has-label predicate.</summary>
    public string Key { get; }

    /// <summary>The value to match, or null to match any value under <see cref="Key"/> (a has-key-any-value predicate).</summary>
    public string? Value { get; }

    /// <summary>Builds a predicate matching a job that carries the Label with this value (a tag with an empty key and this value).</summary>
    /// <param name="value">The Label value to match.</param>
    /// <returns>A predicate matching jobs that carry the given Label.</returns>
    public static JobTagPredicate HasLabel(string value) => new(string.Empty, JobTag.Label(value).Value);

    /// <summary>Builds a predicate matching a job that carries this exact keyed tag.</summary>
    /// <param name="key">The tag key to match.</param>
    /// <param name="value">The tag value to match under that key.</param>
    /// <returns>A predicate matching jobs that carry the given key/value tag.</returns>
    public static JobTagPredicate HasKeyValue(string key, string value)
    {
        var tag = JobTag.Keyed(key, value);
        return new(tag.Key, tag.Value);
    }

    /// <summary>Builds a predicate matching a job that carries any tag (any value) under this key.</summary>
    /// <param name="key">The tag key to match under any value.</param>
    /// <returns>A predicate matching jobs that carry at least one tag with the given key.</returns>
    public static JobTagPredicate HasKey(string key)
    {
        // Reuse JobTag.Keyed's non-empty-key guard with a throwaway value, then drop the value.
        var key2 = JobTag.Keyed(key, "x").Key;
        return new(key2, value: null);
    }

    /// <summary>Tests whether a given tag set satisfies this predicate.</summary>
    /// <param name="tags">The job's tags to test against this predicate.</param>
    /// <returns><c>true</c> when <paramref name="tags"/> contains a tag satisfying this predicate.</returns>
    public bool Matches(JobTags tags)
        => Value is null
            ? tags.Any(t => t.Key == Key)
            : tags.Contains(Key.Length == 0 ? JobTag.Label(Value) : JobTag.Keyed(Key, Value));
}

/// <summary>Sort order for a job listing, by <see cref="JobRecord.Sequence"/>.</summary>
public enum JobSortDirection
{
    /// <summary>Ascending by Sequence — the default; enqueue/claim order.</summary>
    OldestFirst,

    /// <summary>Descending by Sequence — most recently enqueued jobs first.</summary>
    NewestFirst,
}

/// <summary>One cell of the queue-depths read: how many jobs sit in a Queue in a given state.</summary>
/// <param name="Queue">The Queue the count is for.</param>
/// <param name="State">The job state the count is for.</param>
/// <param name="Count">The number of jobs in that Queue and state.</param>
public sealed record QueueStateCount(string Queue, JobState State, int Count);

/// <summary>
/// One bucket of a faceted read: a tag dimension's value and the number of DISTINCT jobs carrying it
/// under the faceted key. For a Label facet (the empty-string key) the value is the Label text.
/// Buckets are ordered by count descending, value ascending as the stable tiebreak.
/// </summary>
/// <param name="Value">The tag value (or Label text) this bucket counts.</param>
/// <param name="Count">The number of distinct jobs carrying the faceted key with this value.</param>
public sealed record TagFacet(string Value, int Count);

/// <summary>
/// A request for a Tag Suggest read — a case-insensitive prefix completion over the Tags in the
/// store (see <see cref="IJobStore.SuggestTagsAsync"/>). The same request shape serves both stages:
/// stage one (a null <see cref="Key"/>) suggests Labels and keys together; stage two (a non-null
/// <see cref="Key"/>) suggests the values under that one key, where the empty string selects the
/// Label dimension.
/// </summary>
public sealed record TagSuggestQuery
{
    /// <summary>The largest window a single call will ever return; <see cref="MaxResults"/> is clamped down to this.</summary>
    public const int MaxSuggestResults = 100;

    /// <summary>The prefix to complete, matched case-insensitively (ASCII); the empty string (the default) matches every Tag.</summary>
    public string Prefix { get; init; } = "";

    /// <summary>
    /// The stage selector. Null suggests Labels and keys together (stage one). A non-null key suggests
    /// the distinct values under that key (stage two); the empty string selects the Label dimension.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>
    /// Keyset pagination cursor: the last suggestion from the previous window. Only suggestions strictly
    /// after it in the read's lexicographic order are returned; null starts at the first window.
    /// </summary>
    public TagSuggestion? After { get; init; }

    /// <summary>The requested window size; the store clamps it to at least one and at most <see cref="MaxSuggestResults"/>.</summary>
    public int MaxResults { get; init; } = 50;
}

/// <summary>
/// One Tag Suggest candidate, carrying the canonical stored casing so the Tag predicate it composes
/// matches exactly. Its structural kind is read from which parts are present:
/// <list type="bullet">
/// <item><b>Label</b> (<see cref="IsLabel"/>) — an empty <see cref="Key"/> and a non-empty <see cref="Value"/>; picking it composes a has-label predicate.</item>
/// <item><b>Key</b> (<see cref="IsKey"/>) — a non-empty <see cref="Key"/> and an empty <see cref="Value"/>; a stage-one drill-in, re-query stage two with this <see cref="Key"/>.</item>
/// <item><b>Key/value</b> (<see cref="IsKeyValue"/>) — both parts non-empty; picking it composes a has-key/value predicate.</item>
/// </list>
/// A suggestion can be passed back verbatim as <see cref="TagSuggestQuery.After"/> to page forward.
/// </summary>
/// <param name="Key">The tag key, or the empty string for a Label suggestion.</param>
/// <param name="Value">The tag value (or Label text), or the empty string for a stage-one key drill-in.</param>
public sealed record TagSuggestion(string Key, string Value)
{
    /// <summary>True when this suggests a Label — an empty <see cref="Key"/> with a Label value in <see cref="Value"/>.</summary>
    public bool IsLabel => Key.Length == 0;

    /// <summary>True when this suggests a key to drill into (stage one) — a non-empty <see cref="Key"/> with an empty <see cref="Value"/>.</summary>
    public bool IsKey => Value.Length == 0;

    /// <summary>True when this suggests a specific keyed tag — both <see cref="Key"/> and <see cref="Value"/> non-empty.</summary>
    public bool IsKeyValue => Key.Length != 0 && Value.Length != 0;
}

/// <summary>
/// A Queue's operational settings: the read-side view of the pause flag and the concurrency limit.
/// In-use slots are NOT carried here — they are derived from the Leased count in the queue depths.
/// </summary>
/// <param name="Queue">The Queue these settings are for.</param>
/// <param name="Paused">Whether the Queue is paused (a paused Queue yields nothing to a claim).</param>
/// <param name="ConcurrencyLimit">The configured maximum of concurrently-leased jobs, or null when no cap is configured.</param>
public sealed record QueueSettings(string Queue, bool Paused, int? ConcurrencyLimit);

/// <summary>
/// The dependency gating edges around one job. <paramref name="GatingParents"/> are the parents
/// still non-terminal that block this job as a child — an edge resolves away as each parent
/// terminates, so this is never the full original parent set; <paramref name="Children"/> are the
/// jobs waiting on this one as a parent.
/// </summary>
/// <param name="GatingParents">The still-non-terminal parents currently blocking this job as a child.</param>
/// <param name="Children">The jobs waiting on this job as a parent.</param>
public sealed record DependencyEdges(
    IReadOnlyList<Guid> GatingParents, IReadOnlyList<Guid> Children);

/// <summary>The result of renewing one job's lease in a heartbeat batch, with its cancellation-requested flag.</summary>
/// <param name="JobId">The job this result is for.</param>
/// <param name="Renewed"><c>true</c> when the lease was extended; <c>false</c> when this worker no longer holds the job and must stop applying its effects.</param>
/// <param name="CancelRequested"><c>true</c> when cancellation has been requested for this job, so the worker should stop cooperatively.</param>
public sealed record HeartbeatResult(Guid JobId, bool Renewed, bool CancelRequested);

/// <summary>The outcome of a cancellation request.</summary>
public enum CancelResult
{
    /// <summary>The job was not yet running and is now terminal Cancelled.</summary>
    CancelledImmediately,

    /// <summary>The job was leased; its cancellation-requested flag is set and it will cancel cooperatively on the next heartbeat.</summary>
    CancellationRequested,

    /// <summary>The job was absent or already terminal, so it could not be cancelled; nothing changed.</summary>
    NotCancellable,
}

/// <summary>The outcome of an operator requeue.</summary>
public enum RequeueResult
{
    /// <summary>A Dead-Lettered or Quarantined job was returned to Scheduled with its Attempt reset to 0.</summary>
    Requeued,

    /// <summary>The job was absent or in a state that cannot be requeued; nothing changed.</summary>
    NotRequeueable,
}

/// <summary>The outcome of triggering a recurring schedule immediately.</summary>
public enum TriggerScheduleResult
{
    /// <summary>One instance was minted immediately; the schedule's cursor was not moved.</summary>
    Triggered,

    /// <summary>No schedule with that id exists; nothing changed.</summary>
    ScheduleNotFound,
}

/// <summary>The operator actions recorded in the audit log.</summary>
public enum OperatorAction
{
    /// <summary>An operator cancelled a job.</summary>
    Cancel,

    /// <summary>An operator requeued a Dead-Lettered or Quarantined job.</summary>
    Requeue,

    /// <summary>An operator minted one instance of a recurring schedule immediately.</summary>
    TriggerScheduleNow,

    /// <summary>An operator paused a Queue.</summary>
    PauseQueue,

    /// <summary>An operator resumed a Queue.</summary>
    ResumeQueue,

    // Appended last: the audit tables persist the numeric value, so existing members never renumber.
    /// <summary>An operator set or cleared a Queue's concurrency limit.</summary>
    SetConcurrencyLimit,
}

/// <summary>
/// One append-only operator audit record: who did what to which target, and when. The target is a
/// job id, a Queue name, or a schedule id depending on the action.
/// </summary>
/// <param name="Actor">Who performed the action.</param>
/// <param name="Action">Which operator action was performed.</param>
/// <param name="Target">The job id, Queue name, or schedule id the action was performed on.</param>
/// <param name="RecordedAt">When the action was recorded.</param>
public sealed record OperatorAuditRecord(
    string Actor, OperatorAction Action, string Target, DateTimeOffset RecordedAt);

/// <summary>
/// One entry in a job's transition log: the resulting state a state-changing operation produced, the
/// job's Attempt at that point, and the timestamp taken from the store's <c>now</c> input (so the log
/// is deterministic). The ordinal is the per-job sequence number, surviving even when older entries
/// age out beyond the cap.
/// </summary>
/// <param name="Ordinal">The 0-based per-job sequence number of this entry, oldest first; preserved even when older entries age out.</param>
/// <param name="Timestamp">When the transition occurred, taken from the store's <c>now</c> input.</param>
/// <param name="State">The job state this transition produced.</param>
/// <param name="Attempt">The job's Attempt number at this transition.</param>
/// <param name="FailureDetail">For a failing transition, the captured diagnostics (exception type, message, stack), bounded for storage; null on every non-failing transition.</param>
public sealed record JobTransition(
    long Ordinal, DateTimeOffset Timestamp, JobState State, int Attempt, string? FailureDetail);

/// <summary>
/// A job to create. The id is caller-supplied and a duplicate is rejected, never replaced. A
/// non-empty parent set makes this a dependency: it waits in AwaitingParent until every parent is
/// terminal, then releases per <see cref="Mode"/>.
/// </summary>
/// <param name="JobId">The caller-supplied job id; an enqueue with an id that already exists is rejected as a duplicate.</param>
/// <param name="WireName">The payload type identifier used to route the job to its handler.</param>
/// <param name="Payload">The serialized job payload; rejected if it exceeds the store's payload size bound.</param>
/// <param name="Queue">The Queue the job belongs to.</param>
/// <param name="DueTime">When the job becomes eligible to run; a future time defers it.</param>
public sealed record NewJob(
    Guid JobId,
    string WireName,
    ReadOnlyMemory<byte> Payload,
    string Queue,
    DateTimeOffset DueTime)
{
    /// <summary>The ids of the parent jobs that gate this one; a non-empty set makes the job a dependency that waits in AwaitingParent until every parent is terminal. Empty for an ordinary job.</summary>
    public IReadOnlyList<Guid> Parents { get; init; } = [];

    /// <summary>How the job reacts when its parent set goes terminal; defaults to releasing only on all-parents-succeeded.</summary>
    public DependencyMode Mode { get; init; } = DependencyMode.OnSuccess;

    /// <summary>Opaque trace correlation (for example a traceparent header); stored and returned verbatim and never interpreted.</summary>
    public string? TraceContext { get; init; }

    /// <summary>
    /// The job's tags: an observational string set attached at enqueue. Because it is a set, an
    /// identical tag collapses. Tags are metadata for querying and faceting; they never affect
    /// execution.
    /// </summary>
    public JobTags Tags { get; init; } = JobTags.Empty;
}

/// <summary>How a dependency reacts to its parent set going terminal.</summary>
public enum DependencyMode
{
    /// <summary>Release only if every parent Succeeded; any other terminal outcome cancels the dependency.</summary>
    OnSuccess,

    /// <summary>Release once every parent is terminal, whatever the terminal states are.</summary>
    OnAnyTerminal,
}

/// <summary>The outcome of enqueuing one job.</summary>
public enum EnqueueResult
{
    /// <summary>The job was created.</summary>
    Ok,

    /// <summary>A job with the same id already exists; nothing was created (the existing job is left as is).</summary>
    Duplicate,

    /// <summary>The serialized payload exceeds the store's payload size bound; nothing was created.</summary>
    PayloadTooLarge,

    /// <summary>The wire name exceeds the store's wire-name length bound; nothing was created.</summary>
    WireNameTooLong,

    /// <summary>A declared gating parent does not exist; nothing was created.</summary>
    UnknownParent,

    /// <summary>The declared parent set exceeds the store's maximum parent count; nothing was created.</summary>
    TooManyParents,
}

/// <summary>The parameters of one claim: who is claiming, from which Queues, how many, for how long, and the current instant.</summary>
/// <param name="WorkerId">The id of the worker claiming the jobs; recorded as the lease holder.</param>
/// <param name="Queues">The candidate Queues to claim due jobs from.</param>
/// <param name="MaxJobs">The maximum number of jobs to lease in this claim.</param>
/// <param name="LeaseDuration">How far past <see cref="Now"/> each claimed job's lease extends.</param>
/// <param name="Now">The current instant; only jobs due at or before this are eligible.</param>
public sealed record ClaimRequest(
    string WorkerId,
    IReadOnlyList<string> Queues,
    int MaxJobs,
    TimeSpan LeaseDuration,
    DateTimeOffset Now);

/// <summary>The result of a batch claim: the leased jobs, and the next future due time for idle-poll scheduling.</summary>
/// <param name="Jobs">The leased jobs, exactly as <see cref="IJobStore.ClaimAsync"/> returns them; empty when nothing is due.</param>
/// <param name="NextDue">
/// The earliest future instant at which a currently-empty claim could begin to return work through the
/// passage of time alone (the next scheduled job's due time), or <c>null</c> when unknown or no future work
/// exists. A value at or before the request's <see cref="ClaimRequest.Now"/> means work is due now but was
/// withheld by a concurrency limit or the batch cap, so the caller should poll again promptly rather than
/// back off. A paused queue is excluded, since its work does not become claimable through time alone.
/// Advisory only, never a correctness input.
/// </param>
public sealed record ClaimResult(IReadOnlyList<JobRecord> Jobs, DateTimeOffset? NextDue);

/// <summary>Whether an outcome report was applied or fenced out by a stale lease.</summary>
public enum OutcomeResult
{
    /// <summary>The caller held the live lease for this Attempt; the outcome was applied.</summary>
    Applied,

    /// <summary>The caller no longer held the live lease for this Attempt; nothing changed.</summary>
    StaleLease,
}

/// <summary>
/// One row of a batched outcome report: the full payload of a single outcome to apply, fenced by its own
/// <see cref="WorkerId"/> and <see cref="Attempt"/> pair. The optional diagnostics, tag delta, and output
/// carry exactly the meaning they do on the single-outcome report — failure detail is recorded only on a
/// failure and truncated to the store's cap, the tag delta unions onto the job's tags when applied, and
/// output is persisted only on a success and rejected (never truncated) if it exceeds the store's cap.
/// </summary>
/// <param name="JobId">The id of the job whose attempt is being reported.</param>
/// <param name="WorkerId">The id of the worker reporting; together with <paramref name="Attempt"/> it fences the lease for this row.</param>
/// <param name="Attempt">The Attempt number this outcome is for; a mismatch with the live lease fences this row out.</param>
/// <param name="Outcome">The execution outcome to apply for this row — success, failure, cancellation, or unroutable.</param>
public sealed record OutcomeReport(Guid JobId, string WorkerId, int Attempt, JobOutcome Outcome)
{
    /// <summary>Optional diagnostics recorded only on a failure outcome; truncated to the store's cap, never rejected. Null on every other outcome.</summary>
    public string? FailureDetail { get; init; }

    /// <summary>Optional tag delta to union onto the job's tags when this row is applied; discarded if the row is fenced out.</summary>
    public JobTags? AddedTags { get; init; }

    /// <summary>Optional result blob persisted only on a success outcome; rejected (never truncated) if it exceeds the store's output cap.</summary>
    public ReadOnlyMemory<byte>? Output { get; init; }
}

/// <summary>The per-row result of a batched outcome report: which job the result is for and whether its outcome applied or was fenced out as stale.</summary>
/// <param name="JobId">The id of the job this result is for.</param>
/// <param name="Result">Whether this row's outcome was <see cref="OutcomeResult.Applied"/> or fenced out as <see cref="OutcomeResult.StaleLease"/>.</param>
public sealed record OutcomeReportResult(Guid JobId, OutcomeResult Result);

/// <summary>
/// Thrown when a successful outcome carries an output blob larger than the store's output cap. Output
/// is functional data a dependent job deserializes, so — unlike diagnostics — it is rejected loudly
/// rather than truncated: a clipped serialized blob is undeserializable, and silent corruption at the
/// reader is worse than a failed write. Store a reference (an id or blob key) instead of the data
/// itself when output is large.
/// </summary>
public sealed class JobOutputTooLargeException(Guid jobId, int actualBytes, int maxOutputBytes)
    : Exception(
        $"Job Output for job {jobId} is {actualBytes} bytes, which exceeds the MaxOutputBytes bound " +
        $"of {maxOutputBytes}. Output is rejected (never truncated) so a descendant never reads a " +
        "corrupted blob; store a reference (id, blob key) instead of the data itself.")
{
    /// <summary>The id of the job whose output exceeded the cap.</summary>
    public Guid JobId { get; } = jobId;

    /// <summary>The actual size of the rejected output, in bytes.</summary>
    public int ActualBytes { get; } = actualBytes;

    /// <summary>The maximum output size the store allows, in bytes.</summary>
    public int MaxOutputBytes { get; } = maxOutputBytes;
}

/// <summary>The outcome of executing one job Attempt, reported back to the store.</summary>
public abstract record JobOutcome
{
    private JobOutcome() { }

    /// <summary>The Attempt succeeded; the job goes terminal Succeeded and any emitted output is persisted.</summary>
    public sealed record Success : JobOutcome;

    /// <summary>
    /// The Attempt failed. The retry-versus-dead-letter choice is made above the store and delivered
    /// here as data: a present next-due time means retry at that instant; an absent one means the
    /// attempt ceiling is exhausted and the job transitions to Dead-Lettered.
    /// </summary>
    /// <param name="NextDueTime">When to retry the job, or null when the attempt ceiling is exhausted (the job is Dead-Lettered).</param>
    /// <param name="Error">A short description of the failure.</param>
    public sealed record Failure(DateTimeOffset? NextDueTime, string Error) : JobOutcome;

    /// <summary>The handler observed cooperative cancellation; the job goes terminal Cancelled.</summary>
    /// <param name="Cause">A short description of why the job was cancelled.</param>
    public sealed record Cancelled(string Cause) : JobOutcome;

    /// <summary>
    /// The job cannot be routed — there is no handler for its wire name, or its payload no longer
    /// decodes — so it goes Quarantined: loud, visible, and never a silent retry storm. Distinct from
    /// Dead-Lettered, which is for jobs that ran and kept failing.
    /// </summary>
    /// <param name="Reason">A short description of why the job could not be routed.</param>
    public sealed record Unroutable(string Reason) : JobOutcome;
}
