using Microsoft.Extensions.Logging;

namespace BackWave.Diagnostics;

// The one way a check site reports a detected impossible state it does NOT halt on.
//
// A Halt site needs no helper: it writes its own branch and throws InvariantViolationException, which
// carries the trigger to the pump, and the pump does the counting. A Degrade site has no such carrier.
// It has to log AND count at the site, and the two calls are easy to half-write - a site that logs but
// forgets the counter is invisible to the promotion rule that reads a full torture ledger for zeros.
// Pairing them here makes the omission impossible.
//
// Internal, like the rest of the detection vocabulary. The tag values it emits are public API; the
// method that emits them is not.
internal static class Invariant
{
    // How far ahead of the node doing the checking another node's clock may run before a fence
    // rejection counts as a contradiction rather than an ordinary lapse. The fleet shares no time
    // source, so a peer that expires a Lease legally can leave the checking node reading an expiry
    // that is still in the future by its own clock. Anything inside this window is ordinary skew and
    // is counted nowhere. Thirty seconds is far wider than a fleet on NTP ever drifts, and far
    // narrower than the shortest Lease anything here takes.
    //
    // One constant, read by every fence, because a fence without it reports skew as a contradiction:
    // the invariant ledger is the surface the promotion rule reads FOR ZEROS, so one such fence makes
    // its trigger meaningless for the whole fleet.
    internal static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromSeconds(30);

    // Reports a tripped invariant the caller then carries past on its existing benign branch.
    //
    // The logger is optional because the Core takes only an optional ILoggerFactory, so a simulated Core
    // and an adapter constructed without one still count. The count is the surface the promotion rule
    // reads, so it must not depend on logging being wired.
    internal static void Degrade(ILogger? logger, InvariantTrigger trigger, string detail)
    {
        if (logger is not null)
        {
            BackWaveLog.InvariantDegraded(logger, trigger.ToString(), detail);
        }

        BackWaveDiagnostics.RecordInvariantViolation(trigger, InvariantAction.Degrade);
    }
}
