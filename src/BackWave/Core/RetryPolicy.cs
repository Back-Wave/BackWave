namespace BackWave.Core;

/// <summary>
/// Pure retry decision: given the Attempt that just failed, either the instant of the
/// next try or null, meaning the attempt ceiling is exhausted and the job Dead-Letters.
/// </summary>
public sealed record RetryPolicy
{
    /// <summary>The maximum number of attempts before the job dead-letters instead of retrying. Defaults to 10.</summary>
    public int MaxAttempts { get; init; } = 10;

    /// <summary>
    /// Computes the delay before the next attempt, given the number of the attempt that just
    /// failed. Defaults to <see cref="DefaultBackoff"/> (exponential, capped at five minutes).
    /// </summary>
    public Func<int, TimeSpan> Backoff { get; init; } = DefaultBackoff;

    /// <summary>The default retry policy: up to 10 attempts with the default exponential backoff.</summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>
    /// The instant the next attempt should run after an attempt fails, or that the job has
    /// exhausted its retries.
    /// </summary>
    /// <param name="failedAttempt">The number of the attempt that just failed (the first attempt is 1).</param>
    /// <param name="now">The current instant, used as the base the backoff delay is added to.</param>
    /// <returns>
    /// When the next attempt should run, or <see langword="null"/> when <paramref name="failedAttempt"/>
    /// has reached the attempt ceiling — meaning no retry remains and the job dead-letters.
    /// </returns>
    public DateTimeOffset? NextAttemptAt(int failedAttempt, DateTimeOffset now)
        => failedAttempt >= MaxAttempts ? null : now + Backoff(failedAttempt);

    /// <summary>
    /// Reduces this policy to pure data a store can apply without executing code: the attempt
    /// ceiling plus the precomputed backoff delay for every attempt that can still retry. The
    /// <see cref="Backoff"/> delegate itself is never persisted — only its evaluated delays are.
    /// </summary>
    /// <returns>The data-only form a store uses to resolve an expired lease's next-attempt instant.</returns>
    public RetryDisposition ToDisposition()
        => new(MaxAttempts, [.. Enumerable.Range(1, Math.Max(0, MaxAttempts - 1)).Select(Backoff)]);

    /// <summary>The default backoff: 2 raised to the attempt number, in seconds, capped at five minutes.</summary>
    /// <param name="attempt">The number of the attempt that just failed (the first attempt is 1).</param>
    /// <returns>The delay to wait before the next attempt.</returns>
    public static TimeSpan DefaultBackoff(int attempt)
        => TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 300));
}

/// <summary>
/// A <see cref="RetryPolicy"/> reduced to pure data so a store can apply it without carrying any
/// executable code. The library decides the retry shape; the store applies it. This lets a store
/// resolve an expired lease's next-attempt instant by data alone, identically to how
/// <see cref="RetryPolicy"/> would.
/// </summary>
/// <param name="MaxAttempts">The maximum number of attempts before the job dead-letters instead of retrying.</param>
/// <param name="BackoffByAttempt">
/// The delay before each retryable attempt, indexed from the first attempt. It holds one entry per
/// attempt that can still retry (attempts 1 through <paramref name="MaxAttempts"/> minus one).
/// </param>
public sealed record RetryDisposition(int MaxAttempts, IReadOnlyList<TimeSpan> BackoffByAttempt)
{
    /// <summary>The most backoff intervals a per-job retry policy can declare (ADR 0051).</summary>
    public const int MaxBackoffIntervals = 20;

    /// <summary>
    /// The most attempts a per-job retry policy can declare (ADR 0051). No real retry policy needs more,
    /// and the cap bounds the attribute so a huge literal fails as a diagnostic, not a startup allocation.
    /// </summary>
    public const int MaxAttemptCeiling = 1000;

    /// <summary>
    /// Builds a disposition from a per-job-type retry declaration: an attempt ceiling and a fixed list
    /// of backoff intervals (the shape a <c>[Retry(...)]</c> attribute carries, ADR 0051). The list is
    /// expanded to one entry per retryable attempt. When it is shorter than the ceiling, the last
    /// interval repeats for the remaining attempts. Validation runs here, at registration time.
    /// </summary>
    /// <param name="maxAttempts">The attempt ceiling; at least 1 and at most <see cref="MaxAttemptCeiling"/>.</param>
    /// <param name="intervals">
    /// The backoff intervals, one or more, at most <see cref="MaxBackoffIntervals"/>, none negative.
    /// </param>
    /// <returns>The disposition the loud-failure path resolves in place of the Worker Group policy.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxAttempts"/> is less than 1 or more than <see cref="MaxAttemptCeiling"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="intervals"/> is empty, holds more than <see cref="MaxBackoffIntervals"/> entries,
    /// or holds a negative interval.
    /// </exception>
    public static RetryDisposition FromIntervals(int maxAttempts, IReadOnlyList<TimeSpan> intervals)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAttempts), maxAttempts, "A retry policy must allow at least one attempt.");
        }
        if (maxAttempts > MaxAttemptCeiling)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAttempts), maxAttempts, $"A retry policy allows at most {MaxAttemptCeiling} attempts.");
        }
        if (intervals is null || intervals.Count == 0)
        {
            throw new ArgumentException(
                "A per-job retry policy must declare at least one backoff interval.", nameof(intervals));
        }
        if (intervals.Count > MaxBackoffIntervals)
        {
            throw new ArgumentException(
                $"A per-job retry policy allows at most {MaxBackoffIntervals} backoff intervals; got {intervals.Count}.",
                nameof(intervals));
        }
        foreach (var interval in intervals)
        {
            if (interval < TimeSpan.Zero)
            {
                throw new ArgumentException("A backoff interval cannot be negative.", nameof(intervals));
            }
        }

        var retryable = Math.Max(0, maxAttempts - 1);
        var byAttempt = new TimeSpan[retryable];
        for (var i = 0; i < retryable; i++)
        {
            // Repeat the last interval when the list is shorter than the ceiling (TickerQ behavior).
            byAttempt[i] = intervals[Math.Min(i, intervals.Count - 1)];
        }
        return new RetryDisposition(maxAttempts, byAttempt);
    }

    /// <summary>
    /// The instant the next attempt should run after an attempt fails, computed from the precomputed
    /// delays alone — producing the same result as <see cref="RetryPolicy.NextAttemptAt"/>.
    /// </summary>
    /// <param name="failedAttempt">The number of the attempt that just failed (the first attempt is 1).</param>
    /// <param name="now">The current instant, used as the base the backoff delay is added to.</param>
    /// <returns>
    /// When the next attempt should run, or <see langword="null"/> when <paramref name="failedAttempt"/>
    /// has reached the attempt ceiling — meaning no retry remains and the job dead-letters.
    /// </returns>
    public DateTimeOffset? NextAttemptAt(int failedAttempt, DateTimeOffset now)
        => failedAttempt >= MaxAttempts ? null : now + BackoffByAttempt[failedAttempt - 1];
}
