namespace BackWave.Core;

/// <summary>
/// The fluent way to define a Recurring Schedule. Every method compiles to a cron
/// expression — by construction the builder can only express what cron can, and the
/// canonical cron form is what gets stored either way.
/// </summary>
public static class Cron
{
    /// <summary>A schedule that fires at the start of every minute.</summary>
    /// <returns>The cron expression for "every minute".</returns>
    public static CronExpression EveryMinute() => CronExpression.Parse("* * * * *");

    /// <summary>A schedule that fires every <paramref name="minutes"/> minutes.</summary>
    /// <param name="minutes">The interval in minutes between fires. Must be at least 1.</param>
    /// <returns>The cron expression for the given interval.</returns>
    /// <exception cref="FormatException"><paramref name="minutes"/> is less than 1, which is not a valid cron step.</exception>
    public static CronExpression EveryMinutes(int minutes) => CronExpression.Parse($"*/{minutes} * * * *");

    /// <summary>A schedule that fires once an hour, at <paramref name="atMinute"/> minutes past the hour.</summary>
    /// <param name="atMinute">The minute past the hour to fire on, 0–59. Defaults to the top of the hour.</param>
    /// <returns>The cron expression for the given hourly time.</returns>
    /// <exception cref="FormatException"><paramref name="atMinute"/> is outside 0–59.</exception>
    public static CronExpression Hourly(int atMinute = 0) => CronExpression.Parse($"{atMinute} * * * *");

    /// <summary>A schedule that fires once a day, at <paramref name="hour"/>:<paramref name="minute"/>.</summary>
    /// <param name="hour">The hour of day to fire on, 0–23.</param>
    /// <param name="minute">The minute of the hour to fire on, 0–59. Defaults to the top of the hour.</param>
    /// <returns>The cron expression for the given daily time.</returns>
    /// <exception cref="FormatException"><paramref name="hour"/> is outside 0–23 or <paramref name="minute"/> is outside 0–59.</exception>
    public static CronExpression Daily(int hour, int minute = 0) => CronExpression.Parse($"{minute} {hour} * * *");

    /// <summary>A schedule that fires once a week, on <paramref name="day"/> at <paramref name="hour"/>:<paramref name="minute"/>.</summary>
    /// <param name="day">The day of the week to fire on.</param>
    /// <param name="hour">The hour of day to fire on, 0–23.</param>
    /// <param name="minute">The minute of the hour to fire on, 0–59. Defaults to the top of the hour.</param>
    /// <returns>The cron expression for the given weekly time.</returns>
    /// <exception cref="FormatException"><paramref name="hour"/> is outside 0–23 or <paramref name="minute"/> is outside 0–59.</exception>
    public static CronExpression Weekly(DayOfWeek day, int hour, int minute = 0)
        => CronExpression.Parse($"{minute} {hour} * * {(int)day}");

    /// <summary>A schedule that fires once a month, on <paramref name="dayOfMonth"/> at <paramref name="hour"/>:<paramref name="minute"/>.</summary>
    /// <param name="dayOfMonth">The day of the month to fire on, 1–31. A day past the end of a short month simply does not fire that month.</param>
    /// <param name="hour">The hour of day to fire on, 0–23.</param>
    /// <param name="minute">The minute of the hour to fire on, 0–59. Defaults to the top of the hour.</param>
    /// <returns>The cron expression for the given monthly time.</returns>
    /// <exception cref="FormatException"><paramref name="dayOfMonth"/> is outside 1–31, <paramref name="hour"/> is outside 0–23, or <paramref name="minute"/> is outside 0–59.</exception>
    public static CronExpression Monthly(int dayOfMonth, int hour, int minute = 0)
        => CronExpression.Parse($"{minute} {hour} {dayOfMonth} * *");
}
