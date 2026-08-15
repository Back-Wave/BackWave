namespace BackWave.Core;

/// <summary>
/// Memoises cron parsing and zone resolution so a Recurring Schedule's cron is parsed once
/// per distinct expression, not once per poll. The (cron, zone) pair is its own
/// version key: a redefined schedule yields a new key, a cache miss, and a single re-parse;
/// an unchanged one is resolved from memory forever. Resolution is deterministic — the same
/// key always yields the same result — so memoising changes cost, never behaviour.
/// </summary>
/// <remarks>
/// Not thread-safe: a Node Driver processes its events serially (both pumps and the
/// Simulator), so each Driver owns a private cache without locking.
/// </remarks>
internal sealed class CronCache
{
    private readonly Dictionary<(string Cron, string? Zone), Resolution> _cache = [];

    /// <summary>
    /// Distinct (cron, zone) pairs actually parsed. A poll loop that resolves the same
    /// schedules every tick must leave this flat — the "parsed once per version" guarantee.
    /// </summary>
    public int ParseCount { get; private set; }

    /// <summary>
    /// As <see cref="ScheduleValidation.TryResolve"/>, but resolved from the cache when the
    /// (cron, zone) pair has been seen before.
    /// </summary>
    public bool TryResolve(
        string cron, string? timeZoneId,
        out CronExpression? expression, out TimeZoneInfo? zone, out string? error)
    {
        if (!_cache.TryGetValue((cron, timeZoneId), out var resolution))
        {
            var ok = ScheduleValidation.TryResolve(cron, timeZoneId, out var parsed, out var resolvedZone, out var failure);
            resolution = new Resolution(ok, parsed, resolvedZone, failure);
            _cache[(cron, timeZoneId)] = resolution;
            ParseCount++;
        }

        expression = resolution.Expression;
        zone = resolution.Zone;
        error = resolution.Error;
        return resolution.Ok;
    }

    private readonly record struct Resolution(bool Ok, CronExpression? Expression, TimeZoneInfo? Zone, string? Error);
}
