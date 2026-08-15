using BackWave.Storage;

namespace BackWave.Core;

/// <summary>
/// The pure minting decision: which ticks of each Recurring Schedule mint,
/// which are skipped (Catch-Up Policy, No-Overlap), and where each Cursor advances to.
/// The store applies decisions atomically, fenced by ExpectedCursor so concurrent nodes
/// can never double-mint a tick.
/// </summary>
internal static class MintPlanner
{
    /// <summary>Named bound: ticks resolved per schedule per poll (a stalled cluster catches up in batches).</summary>
    public const int MaxTicksPerPoll = 32;

    /// <summary>
    /// Named bound: a tick older than this when resolved was "missed" (outage), not merely
    /// late (poll granularity), and falls to the Catch-Up Policy.
    /// </summary>
    public static readonly TimeSpan MissedTickThreshold = TimeSpan.FromMinutes(1);

    public static IReadOnlyList<MintDecision> Plan(
        IReadOnlyList<ScheduleSnapshot> schedules, DateTimeOffset now,
        TimeSpan? missedTickThreshold = null, CronCache? cronCache = null)
    {
        var threshold = missedTickThreshold ?? MissedTickThreshold;
        var decisions = new List<MintDecision>();

        foreach (var (schedule, hasLiveInstance) in schedules)
        {
            // Per-schedule fault isolation: a poisoned row (an IANA zone id absent on this
            // host, a corrupted cron written by an older version) is skipped here, not thrown,
            // so it can never fail-stop the Worker Group. Healthy schedules keep minting; the
            // Monitor surfaces the bad row as errored via the same ScheduleValidation check.
            // The cache resolves a repeated (cron, zone) without re-parsing every poll (0039).
            var resolved = cronCache is not null
                ? cronCache.TryResolve(schedule.Cron, schedule.TimeZoneId, out var cronEx, out var zone, out _)
                : ScheduleValidation.TryResolve(schedule.Cron, schedule.TimeZoneId, out cronEx, out zone, out _);
            if (!resolved)
            {
                continue;
            }
            var cron = cronEx!;

            var ticks = new List<DateTimeOffset>();
            var cursor = schedule.Cursor;
            while (ticks.Count < MaxTicksPerPoll && ZonedCron.NextAfter(cron, cursor, zone) is { } tick && tick <= now)
            {
                ticks.Add(tick);
                cursor = tick;
            }
            if (ticks.Count == 0)
            {
                continue;
            }

            List<DateTimeOffset> mint;
            List<DateTimeOffset> skipped;
            if (schedule.NoOverlap && hasLiveInstance)
            {
                // A previous instance is still non-terminal: skip every tick, visibly.
                (mint, skipped) = ([], ticks);
            }
            else
            {
                var missed = ticks.Where(t => now - t > threshold).ToList();
                var fresh = ticks.Where(t => now - t <= threshold).ToList();
                (mint, skipped) = schedule.CatchUp switch
                {
                    CatchUpPolicy.Coalesce when missed.Count > 0 =>
                        ([missed[^1], .. fresh], missed[..^1]),
                    _ => (fresh, missed),
                };

                // No-Overlap also bounds a single batch: at most one instance can mint.
                if (schedule.NoOverlap && mint.Count > 1)
                {
                    skipped = [.. skipped, .. mint[1..]];
                    mint = [mint[0]];
                }
            }

            decisions.Add(new MintDecision(schedule.ScheduleId, schedule.Cursor, cursor, mint, skipped));
        }

        return decisions;
    }
}

/// <summary>
/// One recurring schedule's minting decision, expressed purely as data so the store can apply
/// it atomically. The store must apply a decision only when the schedule's currently stored
/// cursor still equals <paramref name="ExpectedCursor"/>; if it differs, another node has
/// already minted these ticks, so the store must skip the whole decision and apply none of it.
/// This compare-and-set on the cursor is what guarantees a tick is minted exactly once across
/// concurrent nodes. When the decision is applied, the store enqueues a job for each instant in
/// <paramref name="Ticks"/>, records each instant in <paramref name="SkippedTicks"/> as visibly
/// skipped, and advances the stored cursor to <paramref name="NewCursor"/> — all in one atomic
/// write.
/// </summary>
/// <param name="ScheduleId">Identifies the recurring schedule this decision applies to.</param>
/// <param name="ExpectedCursor">
/// The cursor value the schedule must still hold for this decision to apply. The store compares
/// it against the stored cursor and applies the decision only on an exact match.
/// </param>
/// <param name="NewCursor">The cursor value the store advances the schedule to once the decision is applied.</param>
/// <param name="Ticks">The occurrence instants to enqueue as jobs when the decision is applied.</param>
/// <param name="SkippedTicks">
/// The occurrence instants to record as visibly skipped (coalesced or suppressed by the
/// schedule's catch-up and no-overlap rules) rather than enqueued.
/// </param>
public sealed record MintDecision(
    string ScheduleId,
    DateTimeOffset ExpectedCursor,
    DateTimeOffset NewCursor,
    IReadOnlyList<DateTimeOffset> Ticks,
    IReadOnlyList<DateTimeOffset> SkippedTicks);
