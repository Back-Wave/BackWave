namespace BackWave.Storage;

/// <summary>
/// A request to claim a bounded batch of one observer's undelivered transition-log rows <b>under a
/// lease</b>. The claim uses the same claim/lease/heartbeat mechanism as job claiming: exactly one
/// node advances a given observer's cursor at a time, so the happy path delivers each transition
/// once, and a crash mid-delivery lets the claim lapse for another node to redeliver. The filter
/// (<see cref="States"/> plus the optional <see cref="WireName"/> and <see cref="Queue"/>) is the
/// subscription; the store walks the log in append order and hands back only matching rows after the
/// cursor.
/// </summary>
/// <param name="ObserverId">The observer whose deliveries are being claimed.</param>
/// <param name="States">The job states this observer subscribes to; only transitions into these states match.</param>
/// <param name="WireName">An optional payload-type wire name to filter on; null subscribes to every wire name.</param>
/// <param name="Queue">An optional queue to filter on; null subscribes to every queue.</param>
/// <param name="WorkerId">The worker attempting the claim; it becomes the lease owner if the claim succeeds.</param>
/// <param name="MaxRows">The most rows to claim in this batch.</param>
/// <param name="LeaseDuration">How long the claim lease is held before it lapses.</param>
/// <param name="Now">The current instant, used to test lease expiry and backoff windows.</param>
public sealed record ObserverClaimRequest(
    string ObserverId,
    IReadOnlyList<JobState> States,
    string? WireName,
    string? Queue,
    string WorkerId,
    int MaxRows,
    TimeSpan LeaseDuration,
    DateTimeOffset Now);

/// <summary>
/// One transition-log row handed to a claimer for delivery. It carries the transition facts eagerly,
/// plus the durable bookkeeping the store needs: the global log <see cref="Position"/> the cursor
/// advances over, the per-job <see cref="Ordinal"/> that identifies the transition, and the
/// <see cref="DeliveryAttempt"/> count the claim just incremented (the delivery's own retry counter,
/// distinct from the job's <see cref="Attempt"/>).
/// </summary>
/// <param name="Position">The row's position in the global delivery log; the observer's cursor advances over it.</param>
/// <param name="JobId">The job the transition belongs to.</param>
/// <param name="Ordinal">The transition's per-job ordinal, identifying it within that job's history.</param>
/// <param name="WireName">The job's payload-type wire name, carried for subscription matching.</param>
/// <param name="Queue">The job's queue, carried for subscription matching.</param>
/// <param name="State">The state the job transitioned into.</param>
/// <param name="Attempt">The job's attempt number at the transition.</param>
/// <param name="Timestamp">When the transition occurred.</param>
/// <param name="FailureDetail">The captured failure detail when the transition was a failure, or null otherwise.</param>
/// <param name="DeliveryAttempt">How many times delivery of this row has been attempted, including the current one.</param>
public sealed record ObserverClaimedDelivery(
    long Position,
    Guid JobId,
    long Ordinal,
    string WireName,
    string Queue,
    JobState State,
    int Attempt,
    DateTimeOffset Timestamp,
    string? FailureDetail,
    int DeliveryAttempt);

/// <summary>
/// The result of claiming an observer's deliveries. <see cref="Acquired"/> is false when another node
/// holds this observer's claim lease (its cursor is being advanced elsewhere) — the leaderless way one
/// node delivers each transition in the happy path. When acquired, <see cref="Deliveries"/> are the
/// matching rows after the cursor, in log order.
/// </summary>
/// <param name="ObserverId">The observer this claim is for.</param>
/// <param name="Acquired">Whether the lease was acquired; false means another node holds it or there was nothing due.</param>
/// <param name="Deliveries">The claimed rows in log order, empty when not acquired.</param>
public sealed record ObserverClaim(string ObserverId, bool Acquired, IReadOnlyList<ObserverClaimedDelivery> Deliveries)
{
    /// <summary>Builds an empty, unacquired claim — used when another node holds the lease, or there was nothing due.</summary>
    /// <param name="observerId">The observer the claim is for.</param>
    /// <returns>An unacquired claim with no deliveries.</returns>
    public static ObserverClaim None(string observerId) => new(observerId, Acquired: false, []);
}

/// <summary>What a claimer decided about one delivery, reported back under the lease.</summary>
public enum ObserverDeliveryDisposition
{
    /// <summary>The observer callback returned successfully; the cursor may advance past this row.</summary>
    Delivered,

    /// <summary>The callback threw or timed out; hold the cursor and retry at the reported next-attempt time.</summary>
    Retry,

    /// <summary>The delivery exhausted its retry ceiling; record it loudly and advance the cursor past it.</summary>
    DeadLettered,
}

/// <summary>One row's reported outcome. The next-attempt time is set only on a retry disposition.</summary>
/// <param name="Position">The delivery-log position of the row being reported.</param>
/// <param name="Disposition">What the claimer decided about the delivery.</param>
/// <param name="NextAttemptAt">When to retry the delivery; meaningful only when the disposition is retry, null otherwise.</param>
public sealed record ObserverDeliveryOutcome(
    long Position, ObserverDeliveryDisposition Disposition, DateTimeOffset? NextAttemptAt = null);

/// <summary>
/// Reports the outcome of delivering a claimed batch, fenced by the claim lease: a worker that no
/// longer holds the live lease changes nothing (it is a stale survivor of a lapsed claim). Delivered
/// and dead-lettered rows let the cursor advance over the contiguous resolved prefix; a retry row
/// holds the cursor.
/// </summary>
/// <param name="ObserverId">The observer whose deliveries are being reported.</param>
/// <param name="WorkerId">The worker reporting; it must still hold the live claim lease for the report to take effect.</param>
/// <param name="Outcomes">The per-row outcomes for the claimed batch.</param>
/// <param name="Now">The current instant, used to test that the lease is still live.</param>
public sealed record ObserverDeliveryReport(
    string ObserverId, string WorkerId, IReadOnlyList<ObserverDeliveryOutcome> Outcomes, DateTimeOffset Now);

/// <summary>
/// A request to read one observer's delivery lag — how far its durable cursor trails behind the
/// matching transitions in the log. Carries the same subscription filter as a claim (the
/// <see cref="States"/> plus optional <see cref="WireName"/> and <see cref="Queue"/>) so the store
/// counts only rows this observer would actually deliver, never the whole global log.
/// </summary>
/// <param name="ObserverId">The observer whose lag is being read.</param>
/// <param name="States">The job states this observer subscribes to; only transitions into these states count.</param>
/// <param name="WireName">An optional wire name to filter on; null counts every wire name.</param>
/// <param name="Queue">An optional queue to filter on; null counts every queue.</param>
public sealed record ObserverLagRequest(
    string ObserverId, IReadOnlyList<JobState> States, string? WireName, string? Queue);

/// <summary>
/// An observer's delivery-lag snapshot, for monitoring. <see cref="Cursor"/> is the durable
/// delivered-through position (−1 when nothing has been delivered yet). <see cref="Pending"/> is how
/// many matching transitions have appeared in the log that the cursor has not yet advanced past — 0
/// means the observer is caught up. <see cref="OldestPendingAt"/> is when the oldest of those pending
/// transitions occurred, so a growing age signals an observer falling behind; it is null when caught up.
/// </summary>
/// <param name="Cursor">The durable delivered-through log position, or −1 when nothing has been delivered.</param>
/// <param name="Pending">The count of matching transitions after the cursor; 0 when caught up.</param>
/// <param name="OldestPendingAt">When the oldest pending matching transition occurred, or null when caught up.</param>
public sealed record ObserverLag(long Cursor, int Pending, DateTimeOffset? OldestPendingAt);

/// <summary>
/// A dead-lettered observer delivery: a poison row that exhausted its retry ceiling. It is recorded
/// loudly (never silently dropped) and surfaced like a dead-lettered job — metadata only.
/// </summary>
/// <param name="Position">The delivery-log position of the dead-lettered row.</param>
/// <param name="JobId">The job the dead-lettered transition belongs to.</param>
/// <param name="Ordinal">The transition's per-job ordinal.</param>
/// <param name="State">The state the job had transitioned into.</param>
/// <param name="Attempt">The job's attempt number at the transition.</param>
/// <param name="DeliveryAttempts">How many delivery attempts were made before giving up.</param>
/// <param name="DeadLetteredAt">When the delivery was dead-lettered.</param>
public sealed record ObserverDeadLetterRecord(
    long Position, Guid JobId, long Ordinal, JobState State, int Attempt, int DeliveryAttempts, DateTimeOffset DeadLetteredAt);
