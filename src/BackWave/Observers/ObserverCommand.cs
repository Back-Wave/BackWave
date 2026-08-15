using BackWave.Storage;

namespace BackWave.Observers;

/// <summary>
/// Outputs of the <see cref="ObserverDispatchDriver"/>: what the Shell should do next.
/// The Driver decides; only the Shell acts — it claims, invokes the host callback, and reports,
/// owning all I/O, threading, and the clock. Mirrors the Node Driver's <c>Command</c>.
/// </summary>
internal abstract record ObserverCommand
{
    private ObserverCommand() { }

    /// <summary>
    /// Claim up to <see cref="MaxRows"/> of this Observer's undelivered rows under a Lease.
    /// The Shell appends the instant to form an <see cref="ObserverClaimRequest"/>.
    /// </summary>
    public sealed record ClaimBatch(
        string ObserverId,
        ObserverSubscription Subscription,
        string WorkerId,
        int MaxRows,
        TimeSpan LeaseDuration) : ObserverCommand;

    /// <summary>
    /// Invoke the host callback for each claimed row, in log order, each bounded by a timeout
    /// each bounded by a timeout. The Shell collects per-row <see cref="ObserverInvocationResult"/>s and never lets a
    /// throw escape.
    /// </summary>
    public sealed record InvokeBatch(
        string ObserverId, IReadOnlyList<ObserverClaimedDelivery> Deliveries) : ObserverCommand;

    /// <summary>
    /// Report the batch's per-row outcomes to the store, fenced by the claim Lease. The
    /// Shell appends the instant to form an <see cref="ObserverDeliveryReport"/>.
    /// </summary>
    public sealed record ReportBatch(
        string ObserverId, string WorkerId, IReadOnlyList<ObserverDeliveryOutcome> Outcomes) : ObserverCommand;

    /// <summary>Re-poll now: a batch just drained, so more rows may be claimable this instant.</summary>
    public sealed record RequestPoll(DateTimeOffset Now) : ObserverCommand;
}
