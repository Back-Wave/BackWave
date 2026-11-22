using System.Text.Json;
using BackWave.Benchmarks.Workload;

namespace BackWave.Benchmarks.Targets;

/// <summary>
/// The wire protocol between <see cref="ActaPostgresTarget"/> (the parent) and the Acta host child process. Same
/// shape as the MassTransit protocol: one JSON request per line on the child's stdin, one JSON response per line on
/// stdout prefixed with <see cref="ResponsePrefix"/>, so anything else the child prints is forwarded as noise rather
/// than parsed. This file is compiled into both executables.
/// </summary>
internal static class ActaHostProtocol
{
    /// <summary>Marks a stdout line from the child as a protocol response.</summary>
    public const string ResponsePrefix = "#bench ";

    /// <summary>Return the server version.</summary>
    public const string Setup = "setup";

    /// <summary>Drop the Acta schema and provision it again through Acta's own migrations and deploy.</summary>
    public const string Reset = "reset";

    /// <summary>Enqueue the whole stream through an enqueue-only host before any worker runs (drain only).</summary>
    public const string Preload = "preload";

    /// <summary>Start the worker host, so it is already running when the timed window opens.</summary>
    public const string Start = "start";

    /// <summary>Enqueue at rate through the worker host when sustained, and wait until every bench job succeeded.</summary>
    public const string Execute = "execute";

    /// <summary>Stop and dispose the worker host of the last run.</summary>
    public const string Cooldown = "cooldown";

    /// <summary>Check the last run's work against the ledger and return its latency samples.</summary>
    public const string Samples = "samples";

    /// <summary>The schema Acta provisions into; the Acta default.</summary>
    public const string Schema = "acta";

    /// <summary>The worker namespace the bench job runs in.</summary>
    public const string Namespace = WorkloadSpec.BenchQueue;

    /// <summary>The <c>[Job]</c> name of the bench job.</summary>
    public const string JobName = "bench-job";

    /// <summary>Environment variable carrying the Postgres DSN into the child.</summary>
    public const string ConnectionStringEnvVar = "BACKWAVE_ACTA_POSTGRES_DSN";

    /// <summary>Environment variable carrying the worker's MaxConcurrentExecutors.</summary>
    public const string MaxConcurrentExecutorsEnvVar = "BACKWAVE_ACTA_MAX_CONCURRENT_EXECUTORS";

    /// <summary>The serializer settings both sides use.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>One request from the parent to the child.</summary>
/// <param name="Op">One of the operation constants on <see cref="ActaHostProtocol"/>.</param>
/// <param name="Spec">The workload for <c>preload</c> and <c>execute</c>; null otherwise.</param>
internal sealed record ActaHostRequest(string Op, WorkloadSpec? Spec);

/// <summary>One response from the child to the parent.</summary>
/// <param name="Ok">False when the operation failed; <paramref name="Error"/> then carries the reason.</param>
/// <param name="Error">The failure message, or null.</param>
/// <param name="ServerVersion">The Postgres server version; only on a <c>setup</c> response.</param>
/// <param name="EnqueueTicks">Per-call enqueue latencies in ticks; only on a <c>samples</c> response.</param>
/// <param name="EndToEndTicks">Per-job enqueue-to-done latencies in ticks; only on a <c>samples</c> response.</param>
/// <param name="Executions">Handler runs the child counted; only on a <c>samples</c> response.</param>
/// <param name="FirstCompletionUtcTicks">UTC ticks of the earliest completion; only on a <c>samples</c> response.</param>
/// <param name="LastCompletionUtcTicks">UTC ticks of the latest completion; only on a <c>samples</c> response.</param>
/// <param name="ProducerTicks">First to last enqueue of the producers, in ticks; only on a <c>samples</c> response.</param>
internal sealed record ActaHostResponse(
    bool Ok,
    string? Error = null,
    string? ServerVersion = null,
    long[]? EnqueueTicks = null,
    long[]? EndToEndTicks = null,
    int? Executions = null,
    long? FirstCompletionUtcTicks = null,
    long? LastCompletionUtcTicks = null,
    long? ProducerTicks = null);
