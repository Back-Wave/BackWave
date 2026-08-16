namespace BackWave.Jobs;

/// <summary>
/// Declares a per-job-type retry policy (ADR 0051). When a job of this type fails loudly, the node
/// that ran it computes the next attempt from this ceiling and these backoff intervals instead of
/// from the Worker Group policy. A job type with no <c>[Retry]</c> inherits the Worker Group policy.
/// The ceiling and the intervals are compile-time constants, so the source generator can read them.
/// </summary>
/// <param name="maxAttempts">
/// The attempt ceiling: the most attempts before the job dead-letters instead of retrying. At least 1
/// and at most <see cref="BackWave.Core.RetryDisposition.MaxAttemptCeiling"/>.
/// </param>
/// <param name="backoffSeconds">
/// The delay before each retry, in seconds, one value per retryable attempt. Declare at least one and
/// at most <see cref="BackWave.Core.RetryDisposition.MaxBackoffIntervals"/>. When the list is shorter
/// than the ceiling, the last value repeats for the rest.
/// </param>
/// <example>
/// A payment job that runs at most 3 times (2 retries), after 1 second, then 5 seconds:
/// <code>
/// [Job("charge-card")]
/// [Retry(3, 1, 5)]
/// public sealed record ChargeCard(Guid OrderId);
/// </code>
/// </example>
/// <remarks>
/// The override applies on the loud-failure path only. A job that dies by lease expiry (a crash or a
/// stall) uses the Worker Group policy for that attempt (ADR 0051, the accepted tax).
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RetryAttribute(int maxAttempts, params double[] backoffSeconds) : Attribute
{
    /// <summary>The attempt ceiling before the job dead-letters instead of retrying.</summary>
    public int MaxAttempts { get; } = maxAttempts;

    /// <summary>The delay before each retry, in seconds, one value per retryable attempt.</summary>
    public double[] BackoffSeconds { get; } = backoffSeconds ?? [];
}
