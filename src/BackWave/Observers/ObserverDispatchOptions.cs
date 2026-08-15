using BackWave.Core;

namespace BackWave.Observers;

/// <summary>
/// The run config for one node's <see cref="ObserverDispatchDriver"/>. The registered
/// <see cref="Observers"/> are an input to the run — same seed + same set ⇒ same result. With an
/// empty set the Driver is never constructed and no dispatcher polls (zero cost when unused).
/// </summary>
internal sealed record ObserverDispatchOptions
{
    /// <summary>This node's worker identity — what the claim Lease is held under.</summary>
    public required string WorkerId { get; init; }

    /// <summary>The Observers this node delivers for (run config).</summary>
    public required IReadOnlyList<ObserverRegistration> Observers { get; init; }

    /// <summary>Max rows claimed per Observer per poll (the bounded batch).</summary>
    public int MaxBatch { get; init; } = 32;

    /// <summary>How long a claim Lease is held — the redelivery window after a crash mid-delivery.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The delivery retry policy: the backoff schedule and attempt ceiling the Core applies
    /// to a failed delivery. A delivery is itself at-least-once work, so it reuses the same
    /// <see cref="RetryPolicy"/> the Node Driver uses for an Attempt — a callback that throws, times
    /// out, or hangs is held and retried with backoff until the ceiling, then dead-lettered so the
    /// cursor advances past it (bounded head-of-line; a poison row never blocks later notifications
    /// forever).
    /// </summary>
    public RetryPolicy DeliveryRetryPolicy { get; init; } = RetryPolicy.Default;
}
