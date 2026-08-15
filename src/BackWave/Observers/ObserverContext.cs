using BackWave.Storage;

namespace BackWave.Observers;

/// <summary>
/// What an <see cref="ITransitionObserver"/> receives when a job it watches reaches a subscribed
/// state. The transition facts are carried eagerly — everything needed to build a useful message
/// with no extra calls — while the job payload is exposed through a lazy accessor, so its read cost
/// is paid only when an observer reaches for it.
/// <para>
/// Delivery is at-least-once: a crash between the transition and your callback completing is covered
/// by redelivery, so the same context may arrive more than once. Your reaction must be idempotent —
/// the same contract a job handler already carries.
/// </para>
/// </summary>
public sealed record ObserverContext
{
    /// <summary>The id of the job whose transition this is.</summary>
    public required Guid JobId { get; init; }

    /// <summary>The job's type name used to route it to a handler.</summary>
    public required string WireName { get; init; }

    /// <summary>The queue the job lives in.</summary>
    public required string Queue { get; init; }

    /// <summary>The state the job reached — the one your subscription matched.</summary>
    public required JobState State { get; init; }

    /// <summary>The job's attempt number at this transition.</summary>
    public required int Attempt { get; init; }

    /// <summary>When the transition was recorded.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// The failure detail recorded for a failing transition, or null. It is carried here only when
    /// failure-detail capture is enabled by your job history setting; if capture is disabled (for
    /// example to keep sensitive data out of history) this is null even for a failing transition.
    /// </summary>
    public string? FailureDetail { get; init; }

    /// <summary>
    /// The job payload, read lazily on first touch — a delivery that never reaches for it pays no
    /// read cost. Reports <see cref="ObserverPayload.NotAvailable"/> when the job has already been
    /// purged under retention, since a lazy read can race the retention sweep. Defaults to an unwired
    /// accessor that always reports unavailable; <see cref="FromDelivery"/> wires it to the store read.
    /// </summary>
    public ObserverPayloadAccessor Payload { get; init; } = ObserverPayloadAccessor.Unavailable;

    /// <summary>
    /// Builds the context handed to an observer from a claimed delivery, wiring the lazy payload
    /// accessor to a job-row read so the payload is fetched only on first touch and reports the purged
    /// case honestly. The transition facts and (capture-gated) failure detail ride eagerly off the
    /// delivery, so no extra read happens for the common case that never reaches for the payload.
    /// </summary>
    /// <param name="delivery">The claimed delivery carrying the eager transition facts.</param>
    /// <param name="store">The store the lazy payload accessor reads the job row from.</param>
    /// <returns>A context whose payload accessor lazily reads <paramref name="delivery"/>'s job from <paramref name="store"/>.</returns>
    public static ObserverContext FromDelivery(ObserverClaimedDelivery delivery, IJobStore store) => new()
    {
        JobId = delivery.JobId,
        WireName = delivery.WireName,
        Queue = delivery.Queue,
        State = delivery.State,
        Attempt = delivery.Attempt,
        Timestamp = delivery.Timestamp,
        FailureDetail = delivery.FailureDetail,
        Payload = new ObserverPayloadAccessor(async cancellationToken =>
        {
            var job = await store.GetJobAsync(delivery.JobId, cancellationToken);
            return job is null ? ObserverPayload.NotAvailable : ObserverPayload.Present(job.Payload);
        }),
    };
}
