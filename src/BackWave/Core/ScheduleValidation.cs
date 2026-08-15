namespace BackWave.Core;

/// <summary>
/// The single definition of "is this Recurring Schedule resolvable on this host" — shared by
/// the upsert door (reject loudly before storing, the same posture as Enqueue), the mint
/// planner (skip a poisoned row instead of fail-stopping the Worker Group), and the Monitor
/// (surface the row as errored). A schedule fails resolution when its cron will not parse or
/// its IANA zone id will not resolve here — a typo, or a zone present in dev but absent in
/// production.
/// </summary>
internal static class ScheduleValidation
{
    /// <summary>
    /// Parses the cron and resolves the zone without throwing. On success returns true with
    /// both out values set and <paramref name="error"/> null; on failure returns false with a
    /// human-readable cause and the out values left null.
    /// </summary>
    public static bool TryResolve(
        string cron,
        string? timeZoneId,
        out CronExpression? expression,
        out TimeZoneInfo? zone,
        out string? error)
    {
        expression = null;
        zone = null;
        error = null;

        try
        {
            expression = CronExpression.Parse(cron);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            error = $"Invalid cron expression '{cron}': {ex.Message}";
            return false;
        }

        try
        {
            zone = ZonedCron.ResolveZone(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            error = $"Unresolvable time-zone id '{timeZoneId}': {ex.Message}";
            return false;
        }

        return true;
    }
}
