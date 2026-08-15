using BackWave.Storage;

namespace BackWave.Observers;

/// <summary>
/// What a transition observer cares about: the <see cref="States"/> it wants to be told about,
/// optionally narrowed to a single <see cref="WireName"/> and/or <see cref="Queue"/>. A transition
/// is delivered to the observer only when it matches — so a subscription that watches a
/// <c>PaymentJob</c> dead-letter never fires for every job type. Reach for the narrowing properties
/// when one observer should react to only one kind of job.
/// </summary>
/// <param name="States">The states to be notified about; a transition into any one of them matches.</param>
public sealed record ObserverSubscription(IReadOnlyList<JobState> States)
{
    /// <summary>
    /// A subscription to a transition into <b>any</b> job state — the "audit everything" shape. Narrow
    /// it to one job type or queue by composing the <see cref="WireName"/>/<see cref="Queue"/> filters
    /// on top, for example <c>ObserverSubscription.AllTransitions with { WireName = "PaymentJob" }</c>.
    /// </summary>
    public static ObserverSubscription AllTransitions { get; } = new(Enum.GetValues<JobState>());

    /// <summary>Only deliver transitions of jobs with this type name; null matches every type.</summary>
    public string? WireName { get; init; }

    /// <summary>Only deliver transitions of jobs in this queue; null matches every queue.</summary>
    public string? Queue { get; init; }

    /// <summary>Whether a transition with these facts matches this subscription.</summary>
    /// <param name="state">The state the job reached.</param>
    /// <param name="wireName">The transitioning job's type name.</param>
    /// <param name="queue">The transitioning job's queue.</param>
    /// <returns>True when the state is one of <see cref="States"/> and any set type/queue filter also matches.</returns>
    public bool Matches(JobState state, string wireName, string queue)
        => States.Contains(state)
            && (WireName is null || string.Equals(WireName, wireName, StringComparison.Ordinal))
            && (Queue is null || string.Equals(Queue, queue, StringComparison.Ordinal));
}
