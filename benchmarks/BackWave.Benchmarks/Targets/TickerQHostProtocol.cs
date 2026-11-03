using System.Text.Json;
using BackWave.Benchmarks.Workload;

namespace BackWave.Benchmarks.Targets;

/// <summary>
/// The wire protocol between <see cref="TickerQPostgresTarget"/> (the parent) and the TickerQ host child
/// process. Same shape as the JobMaster protocol: one JSON request per line on the child's stdin, one JSON
/// response per line on stdout prefixed with <see cref="ResponsePrefix"/>, so anything else the child prints
/// is forwarded as noise rather than parsed. This file is compiled into both executables.
/// </summary>
internal static class TickerQHostProtocol
{
    /// <summary>Marks a stdout line from the child as a protocol response.</summary>
    public const string ResponsePrefix = "#bench ";

    /// <summary>Persist the whole stream through a producer-only TickerQ instance (drain only).</summary>
    public const string Preload = "preload";

    /// <summary>Start the processing host, so it is already running when the timed window opens.</summary>
    public const string Start = "start";

    /// <summary>Enqueue at rate when sustained, and wait until every job is final.</summary>
    public const string Execute = "execute";

    /// <summary>Return the enqueue and end-to-end latency samples of the last run.</summary>
    public const string Samples = "samples";

    /// <summary>Environment variable carrying the Postgres DSN into the child.</summary>
    public const string ConnectionStringEnvVar = "BACKWAVE_TICKERQ_POSTGRES_DSN";

    /// <summary>Environment variable carrying the scheduler and bench-function MaxConcurrency.</summary>
    public const string MaxConcurrencyEnvVar = "BACKWAVE_TICKERQ_MAX_CONCURRENCY";

    /// <summary>Environment variable carrying the fallback service's scan period in milliseconds.</summary>
    public const string FallbackIntervalEnvVar = "BACKWAVE_TICKERQ_FALLBACK_MS";

    /// <summary>Environment variable carrying the main scheduler's minimum wait in milliseconds.</summary>
    public const string MinPollingIntervalEnvVar = "BACKWAVE_TICKERQ_MIN_POLL_MS";

    /// <summary>The serializer settings both sides use.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>One request from the parent to the child.</summary>
/// <param name="Op">One of the operation constants on <see cref="TickerQHostProtocol"/>.</param>
/// <param name="Spec">The workload for <c>preload</c> and <c>execute</c>; null otherwise.</param>
internal sealed record TickerQHostRequest(string Op, WorkloadSpec? Spec);

/// <summary>One response from the child to the parent.</summary>
/// <param name="Ok">False when the operation failed; <paramref name="Error"/> then carries the reason.</param>
/// <param name="Error">The failure message, or null.</param>
/// <param name="EnqueueTicks">Per-call enqueue latencies in ticks; only on a <c>samples</c> response.</param>
/// <param name="EndToEndTicks">Per-job enqueue-to-done latencies in ticks; only on a <c>samples</c> response.</param>
internal sealed record TickerQHostResponse(
    bool Ok,
    string? Error = null,
    long[]? EnqueueTicks = null,
    long[]? EndToEndTicks = null);
