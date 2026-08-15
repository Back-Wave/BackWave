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
