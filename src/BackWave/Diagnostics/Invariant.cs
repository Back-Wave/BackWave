using Microsoft.Extensions.Logging;

namespace BackWave.Diagnostics;

// The one way a check site reports a detected impossible state, whichever action it takes about it.
//
// Both actions have to count, and the count is the only thing they share: a Halt site throws what this
// hands back, a Degrade site logs and carries past on its existing benign branch. Either way the two
// calls are easy to half-write - a site that acts but forgets the counter is invisible to the promotion
// rule that reads a full torture ledger for zeros. Pairing them here makes the omission impossible.
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

    // Reports a tripped invariant the caller then raises, and hands back the exception to throw.
    //
    // Counting happens on the RAISE rather than inside the exception's constructor, because both of
    // those constructors are public on a shipped assembly: anything that builds one without raising it
    // - a health report describing a halt that already happened, a test inspecting the type - would
    // otherwise put a violation that never occurred on the meter, and backwave.invariant.violations is
    // the surface the promotion rule reads FOR ZEROS. Every Halt site under src/ raises through here,
    // so a raise counts exactly once however far the throw travels before something catches it: a
    // violation raised inside a worker group's pump is caught there, one raised on the client's enqueue
    // path propagates to the application, and one raised in a dependency read can be swallowed by a
    // handler's broad catch. Telemetry-only, exactly as for Degrade: the count never changes what
    // happens next, and it is taken before the exception escapes, so no catch anywhere suppresses it.
    internal static InvariantViolationException Halt(InvariantTrigger trigger, string message)
    {
        BackWaveDiagnostics.RecordInvariantViolation(trigger, InvariantAction.Halt);
        return new InvariantViolationException(trigger, message);
    }

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
