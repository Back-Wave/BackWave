using System.Text.Json;
using BackWave.Benchmarks.Workload;

namespace BackWave.Benchmarks.Targets;

/// <summary>
/// The wire protocol between <see cref="MassTransitPostgresTarget"/> (the parent) and the MassTransit host child
/// process. Same shape as the TickerQ protocol: one JSON request per line on the child's stdin, one JSON response
/// per line on stdout prefixed with <see cref="ResponsePrefix"/>, so anything else the child prints is forwarded
/// as noise rather than parsed. This file is compiled into both executables.
/// </summary>
internal static class MassTransitHostProtocol
{
    /// <summary>Marks a stdout line from the child as a protocol response.</summary>
    public const string ResponsePrefix = "#bench ";

    /// <summary>Create the transport schema and deploy the bench queue, and return the server version.</summary>
    public const string Setup = "setup";

    /// <summary>Empty the transport tables and start a fresh bus with no consumer endpoint.</summary>
    public const string Reset = "reset";

    /// <summary>Send the whole stream before the consumer is connected (drain only).</summary>
    public const string Preload = "preload";

    /// <summary>Connect the consumer endpoint, so it is already running when the timed window opens.</summary>
    public const string Start = "start";

    /// <summary>Send at rate when sustained, and wait until the bench queue holds no delivery row.</summary>
    public const string Execute = "execute";

    /// <summary>Stop and dispose the bus of the last run.</summary>
    public const string Cooldown = "cooldown";

    /// <summary>Return the enqueue and end-to-end latency samples of the last run.</summary>
    public const string Samples = "samples";

    /// <summary>Environment variable carrying the Postgres DSN into the child.</summary>
    public const string ConnectionStringEnvVar = "BACKWAVE_MASSTRANSIT_POSTGRES_DSN";

    /// <summary>Environment variable carrying the endpoint's ConcurrentMessageLimit.</summary>
    public const string ConcurrentMessageLimitEnvVar = "BACKWAVE_MASSTRANSIT_CONCURRENT_MESSAGE_LIMIT";

    /// <summary>Environment variable carrying the endpoint's PrefetchCount.</summary>
    public const string PrefetchCountEnvVar = "BACKWAVE_MASSTRANSIT_PREFETCH_COUNT";

    /// <summary>Environment variable carrying the endpoint's PollingInterval in milliseconds.</summary>
    public const string PollingMsEnvVar = "BACKWAVE_MASSTRANSIT_POLLING_MS";

    /// <summary>Environment variable carrying the transport's ConnectionLimit.</summary>
    public const string ConnectionLimitEnvVar = "BACKWAVE_MASSTRANSIT_CONNECTION_LIMIT";

    /// <summary>The serializer settings both sides use.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>One request from the parent to the child.</summary>
/// <param name="Op">One of the operation constants on <see cref="MassTransitHostProtocol"/>.</param>
/// <param name="Spec">The workload for <c>preload</c> and <c>execute</c>; null otherwise.</param>
internal sealed record MassTransitHostRequest(string Op, WorkloadSpec? Spec);

/// <summary>One response from the child to the parent.</summary>
/// <param name="Ok">False when the operation failed; <paramref name="Error"/> then carries the reason.</param>
/// <param name="Error">The failure message, or null.</param>
/// <param name="ServerVersion">The Postgres server version; only on a <c>setup</c> response.</param>
/// <param name="EnqueueTicks">Per-call enqueue latencies in ticks; only on a <c>samples</c> response.</param>
/// <param name="EndToEndTicks">Per-job enqueue-to-done latencies in ticks; only on a <c>samples</c> response.</param>
/// <param name="Executions">Consumer runs the child counted; only on a <c>samples</c> response.</param>
/// <param name="FirstCompletionUtcTicks">UTC ticks of the earliest completion; only on a <c>samples</c> response.</param>
/// <param name="LastCompletionUtcTicks">UTC ticks of the latest completion; only on a <c>samples</c> response.</param>
/// <param name="ProducerTicks">First to last enqueue of the producers, in ticks; only on a <c>samples</c> response.</param>
internal sealed record MassTransitHostResponse(
    bool Ok,
    string? Error = null,
    string? ServerVersion = null,
    long[]? EnqueueTicks = null,
    long[]? EndToEndTicks = null,
    int? Executions = null,
    long? FirstCompletionUtcTicks = null,
    long? LastCompletionUtcTicks = null,
    long? ProducerTicks = null);
