using BackWave.Core;

namespace BackWave.Hosting;

/// <summary>
/// Settings for the observer dispatch pump, tuned inside the <c>AddObservers</c> block via
/// <see cref="ObserverBuilder.ConfigurePump"/>. They control how many transitions are claimed per
/// poll, how long a claim is held, how delivery failures are retried, how often the pump polls, and
/// how long a single observer callback may run before the pump gives up on it. Every property has a
/// sensible default, so configuring the pump is optional.
/// </summary>
public sealed class ObserverPumpOptions
{
    /// <summary>
    /// The maximum number of transitions claimed for each observer per poll. Bounds how much one
    /// observer processes at a time. Defaults to 32.
    /// </summary>
    public int MaxBatch { get; set; } = 32;

    /// <summary>
    /// How long a claim is held before it can be re-claimed by another node. If a process crashes
    /// mid-delivery, this is the window after which the claimed transitions become deliverable again.
    /// Defaults to 60 seconds.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How a failed delivery is retried: the backoff schedule and the attempt ceiling after which a
    /// delivery is dead-lettered. Defaults to the standard retry policy.
    /// </summary>
    public RetryPolicy DeliveryRetryPolicy { get; set; } = RetryPolicy.Default;

    // Shell-only timing knob: the Core never times its own polls, so this never reaches it (the
    // determinism boundary lives in the Simulator, not the wall clock).
    /// <summary>
    /// How often the pump polls for each observer's next batch of transitions. A shorter interval
    /// lowers delivery latency at the cost of more frequent store queries. Defaults to 1 second.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    // Shell-only: the Core only ever sees the resulting Succeeded = false and decides
    // retry-with-backoff vs dead-letter (ADR 0020, §0077). Worst-case latency under a fully-hung
    // observer is MaxBatch × this for THAT observer — bounded and self-healing once it dead-letters.
    /// <summary>
    /// How long a single observer callback may run before the pump records the delivery as failed and
    /// moves on without waiting for it. A callback that ignores its cancellation token and runs past
    /// this deadline is left to finish in the background; its eventual exception is still observed so
    /// it cannot crash the process. The failed delivery is then retried with backoff and eventually
    /// dead-lettered, so one stuck observer never blocks the others. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan DeliveryTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
