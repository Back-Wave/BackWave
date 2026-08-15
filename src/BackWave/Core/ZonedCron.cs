namespace BackWave.Core;

/// <summary>
/// Maps cron occurrences through an IANA time zone with fixed DST rules: a tick whose
/// local time was skipped (spring forward) fires once at the first valid instant; a tick
/// whose local time is ambiguous (fall back) fires the first occurrence only.
/// </summary>
internal static class ZonedCron
{
    /// <summary>
    /// The first UTC occurrence strictly after <paramref name="utcAfter"/>, straight from
    /// the stored schedule shape — the single definition of "next due tick",
    /// shared by the mint planner and the Monitor so they can never diverge.
    /// </summary>
    public static DateTimeOffset? NextAfter(string cron, string? timeZoneId, DateTimeOffset utcAfter)
        => NextAfter(CronExpression.Parse(cron), utcAfter, ResolveZone(timeZoneId));

    /// <summary>Resolves a stored zone id to the zone the cron evaluates in; null means UTC.</summary>
    public static TimeZoneInfo? ResolveZone(string? timeZoneId)
        => timeZoneId is null ? null : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

    /// <summary>
    /// Named bound: how far wall-clock time may advance while remapping candidates whose UTC
    /// mapping lands at or before the cursor. Inside the DST fall-back hour every ambiguous
    /// candidate maps (fire-first-occurrence) to an instant at or before the cursor, so the
    /// loop must clear the whole repeated hour; a generous day-plus covers any real transition
    /// and a pathological zone yields no occurrence rather than spinning forever.
    /// </summary>
    private static readonly TimeSpan MaxWallClockAdvance = TimeSpan.FromHours(25);

    /// <summary>Named bound: minutes stepped to climb out of a spring-forward gap (no real gap exceeds a day).</summary>
    private const int MaxGapMinutes = 26 * 60;

    /// <summary>The first UTC occurrence strictly after <paramref name="utcAfter"/>; null if none.</summary>
    public static DateTimeOffset? NextAfter(CronExpression cron, DateTimeOffset utcAfter, TimeZoneInfo? zone)
    {
        if (zone is null)
        {
            return cron.NextAfter(utcAfter);
        }

        // Iterate in local wall-clock time (offset-free), then map each candidate to UTC.
        // Inside the DST fall-back hour, ambiguous candidates map to UTC instants at or before
        // the cursor; keep advancing until one maps strictly past it. The bound is wall-clock
        // distance, not an iteration count — a sub-minute cron clears the entire repeated hour
        // instead of exhausting a fixed count mid-hour and stalling the schedule on a spurious
        // null (issue 0030).
        var startWall = new DateTimeOffset(TimeZoneInfo.ConvertTime(utcAfter, zone).DateTime, TimeSpan.Zero);
        var wall = startWall;
        while (wall - startWall <= MaxWallClockAdvance)
        {
            if (cron.NextAfter(wall) is not { } candidate)
            {
                return null;
            }
            wall = candidate;
            var utc = MapToUtc(candidate.DateTime, zone);
            if (utc > utcAfter)
            {
                return utc;
            }
        }
        return null;
    }

    private static DateTimeOffset MapToUtc(DateTime wallClock, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(wallClock))
        {
            // Skipped local time: fire once at the first valid instant after the gap.
            var firstValid = wallClock;
            for (var i = 0; i < MaxGapMinutes && zone.IsInvalidTime(firstValid); i++)
            {
                firstValid = firstValid.AddMinutes(1);
            }
            return new DateTimeOffset(firstValid, zone.GetUtcOffset(firstValid));
        }

        if (zone.IsAmbiguousTime(wallClock))
        {
            // Repeated local time: first occurrence only — the larger offset is the
            // pre-transition (DST) one and maps to the earlier UTC instant.
            return new DateTimeOffset(wallClock, zone.GetAmbiguousTimeOffsets(wallClock).Max());
        }

        return new DateTimeOffset(wallClock, zone.GetUtcOffset(wallClock));
    }
}
