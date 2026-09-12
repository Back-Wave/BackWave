using BackWave.Storage;

namespace BackWave.Observers;

/// <summary>
/// Inputs to the <see cref="ObserverDispatchDriver"/> — the things that have happened that the
/// sans-IO delivery state machine reacts to. Every variant carries the instant it
/// occurred so the Driver never reads a clock; the Shell synthesizes these from store I/O and
/// timer ticks, exactly as the Node Driver's <c>NodeEvent</c> does.
/// </summary>
internal abstract record ObserverEvent
{
    private ObserverEvent() { }

    /// <summary>Timer tick: time to claim each Observer's next undelivered batch.</summary>
    public sealed record PollDue(DateTimeOffset Now) : ObserverEvent;

    /// <summary>
    /// The store returned a claimed batch (empty when another node holds the Lease, or nothing is due).
    /// <para>
    /// <c>LeaseExpiry</c> is the instant this node's claim Lease runs until, which the Shell forms as the
    /// claim's instant plus the Lease duration it asked for - the store stamps the row from those same two
    /// values and never reads its own clock, so the two agree exactly. Carried so the report can tell the
    /// store what this node BELIEVED, which is the only evidence that separates a contradiction from an
    /// ordinary lapse when the fence refuses the report.
    /// </para>
    /// </summary>
    public sealed record BatchClaimed(
        string ObserverId, IReadOnlyList<ObserverClaimedDelivery> Deliveries, DateTimeOffset Now,
        DateTimeOffset LeaseExpiry) : ObserverEvent;

    /// <summary>The Shell finished invoking the callback for each claimed row; carries per-row results.</summary>
    public sealed record BatchInvoked(
        string ObserverId, IReadOnlyList<ObserverInvocationResult> Results, DateTimeOffset Now) : ObserverEvent;

    /// <summary>The store recorded the reported batch outcomes (cursor advanced where it could).</summary>
    public sealed record BatchReported(string ObserverId, DateTimeOffset Now) : ObserverEvent;

    /// <summary>
    /// The claim or report round-trip for this Observer failed at the Shell edge — the store threw
    /// (a fault, Node Isolation), so no batch came back and nothing was recorded. The Core must
    /// release its in-flight guard for this Observer so the next poll re-claims it; otherwise a single
    /// faulted claim wedges this node's delivery of the Observer forever. Mirrors the Node
    /// Driver forgetting an Attempt when its store call fails.
    /// </summary>
    public sealed record DeliveryAborted(string ObserverId, DateTimeOffset Now) : ObserverEvent;
}

/// <summary>
/// The result of the Shell invoking one Observer callback: which delivery
/// (<see cref="Position"/>), the <see cref="DeliveryAttempt"/> this was (the claim's per-row
/// counter, so the Core can apply the backoff schedule and the dead-letter ceiling), and whether
/// the callback returned without throwing or timing out. The dispatch edge turns a throw/timeout
/// into <c>Succeeded = false</c> — never an exception that could escape and fail-stop the pump.
/// </summary>
internal sealed record ObserverInvocationResult(long Position, bool Succeeded, int DeliveryAttempt);
