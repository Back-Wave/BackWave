namespace BackWave.Core;

/// <summary>
/// Standard cron (5-field, optional leading seconds field — no Quartz extensions), parsed
/// to a pure next-occurrence function over UTC instants. The canonical 6-field form is the
/// single stored representation regardless of which idiom defined the schedule.
/// </summary>
public sealed class CronExpression
{
    private readonly ulong _seconds;
    private readonly ulong _minutes;
    private readonly ulong _hours;
    private readonly ulong _daysOfMonth;
    private readonly ulong _months;
    private readonly ulong _daysOfWeek;
    private readonly bool _hasSeconds;
    private readonly bool _domRestricted;
    private readonly bool _dowRestricted;

    /// <summary>The canonical 6-field form (seconds first); what schedules store.</summary>
    public string Canonical { get; }

    private CronExpression(string[] fields, bool hasSeconds)
    {
        _hasSeconds = hasSeconds;
        _seconds = ParseField(fields[0], 0, 59, "seconds");
        _minutes = ParseField(fields[1], 0, 59, "minutes");
        _hours = ParseField(fields[2], 0, 23, "hours");
        _daysOfMonth = ParseField(fields[3], 1, 31, "day-of-month");
        _months = ParseField(fields[4], 1, 12, "month");
        var daysOfWeek = ParseField(fields[5], 0, 7, "day-of-week");
        _daysOfWeek = (daysOfWeek & (1UL << 7)) != 0 ? daysOfWeek | 1UL : daysOfWeek; // 7 = Sunday = 0
        _domRestricted = fields[3] != "*";
        _dowRestricted = fields[5] != "*";
        Canonical = string.Join(' ', fields);
    }

    /// <summary>
    /// Parses a standard cron expression into a reusable next-occurrence calculator. Accepts the
    /// 5-field form (minute, hour, day-of-month, month, day-of-week) or the 6-field form with a
    /// leading seconds field; any other field count is rejected.
    /// </summary>
    /// <param name="expression">The cron text to parse.</param>
    /// <returns>The parsed expression, exposing its canonical form and next-occurrence calculation.</returns>
    /// <exception cref="ArgumentException"><paramref name="expression"/> is null, empty, or only whitespace.</exception>
    /// <exception cref="FormatException">
    /// <paramref name="expression"/> does not have 5 or 6 fields, or a field is malformed or out of
    /// its allowed range.
    /// </exception>
    public static CronExpression Parse(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            5 => new CronExpression(["0", .. parts], hasSeconds: false),
            6 => new CronExpression(parts, hasSeconds: true),
            _ => throw new FormatException(
                $"Cron expression '{expression}' must have 5 fields (or 6 with leading seconds)."),
        };
    }

    /// <summary>
    /// The first occurrence strictly after <paramref name="after"/>, evaluated in UTC.
    /// </summary>
    /// <param name="after">The instant to search forward from. The result is always strictly later than this.</param>
    /// <returns>
    /// The next matching instant in UTC, or <see langword="null"/> if no occurrence falls within the
    /// four-year search bound — which happens only for an unsatisfiable expression such as one pinned
    /// to an impossible date like February 30.
    /// </returns>
    public DateTimeOffset? NextAfter(DateTimeOffset after)
    {
        var step = _hasSeconds ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1);
        var candidate = Truncate(after.ToUniversalTime(), step) + step;
        var bound = candidate.AddYears(4);

        // Field-hierarchical search: skip non-matching days, hours, and minutes wholesale
        // instead of stepping through them — a year of daily ticks is ~100 steps per day.
        while (candidate <= bound)
        {
            if (!MatchesDate(candidate))
            {
                candidate = Truncate(candidate, TimeSpan.FromDays(1)).AddDays(1);
            }
            else if (!IsSet(_hours, candidate.Hour))
            {
                candidate = Truncate(candidate, TimeSpan.FromHours(1)).AddHours(1);
            }
            else if (!IsSet(_minutes, candidate.Minute))
            {
                candidate = Truncate(candidate, TimeSpan.FromMinutes(1)).AddMinutes(1);
            }
            else if (!IsSet(_seconds, candidate.Second))
            {
                candidate += step;
            }
            else
            {
                return candidate;
            }
        }

        return null;
    }

    private bool MatchesDate(DateTimeOffset t) => IsSet(_months, t.Month) && MatchesDay(t);

    /// <summary>Standard cron rule: when both day fields are restricted they OR; otherwise AND.</summary>
    private bool MatchesDay(DateTimeOffset t)
    {
        var dom = IsSet(_daysOfMonth, t.Day);
        var dow = IsSet(_daysOfWeek, (int)t.DayOfWeek);
        return _domRestricted && _dowRestricted ? dom || dow : dom && dow;
    }

    private static bool IsSet(ulong mask, int value) => (mask & (1UL << value)) != 0;

    private static DateTimeOffset Truncate(DateTimeOffset t, TimeSpan step)
        => new(t.UtcTicks - (t.UtcTicks % step.Ticks), TimeSpan.Zero);

    private static ulong ParseField(string field, int min, int max, string name)
    {
        var mask = 0UL;
        foreach (var part in field.Split(','))
        {
            var (rangePart, parsedStep) = part.Split('/') is [var r, var s]
                ? (r, ParseNumber(s, name))
                : (part, 1);
            int low, high;
            if (rangePart == "*")
            {
                (low, high) = (min, max);
            }
            else if (rangePart.Split('-') is [var lo, var hi])
            {
                (low, high) = (ParseNumber(lo, name), ParseNumber(hi, name));
            }
            else
            {
                low = ParseNumber(rangePart, name);
                high = parsedStep > 1 ? max : low;
            }

            if (low < min || high > max || low > high || parsedStep < 1)
            {
                throw new FormatException($"Cron {name} field part '{part}' is out of range {min}-{max}.");
            }
            for (var v = low; v <= high; v += parsedStep)
            {
                mask |= 1UL << v;
            }
        }
        return mask;
    }

    private static int ParseNumber(string text, string name)
        => int.TryParse(text, out var value)
            ? value
            : throw new FormatException($"Cron {name} field contains a non-numeric part '{text}'.");
}
