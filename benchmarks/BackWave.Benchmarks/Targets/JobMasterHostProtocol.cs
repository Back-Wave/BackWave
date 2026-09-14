using System.Text.Json;
using BackWave.Benchmarks.Workload;

namespace BackWave.Benchmarks.Targets;

/// <summary>
/// The wire protocol between <see cref="JobMasterPostgresTarget"/> (the parent) and the JobMaster host
/// child process. The parent writes one JSON request per line on the child's stdin; the child answers with
/// one JSON response per line on stdout, prefixed with <see cref="ResponsePrefix"/> so anything else the
/// child prints can be forwarded as noise rather than parsed. This file is compiled into both executables.
/// </summary>
internal static class JobMasterHostProtocol
{
    /// <summary>Marks a stdout line from the child as a protocol response.</summary>
    public const string ResponsePrefix = "#bench ";

    /// <summary>Enqueue the whole stream and wait until JobMaster has ingested every job (drain only).</summary>
    public const string Preload = "preload";

    /// <summary>Open the handler gate, enqueue at rate when sustained, and wait until every job is final.</summary>
    public const string Execute = "execute";

    /// <summary>Return the enqueue and end-to-end latency samples of the last run.</summary>
    public const string Samples = "samples";

    /// <summary>Environment variable carrying the Postgres DSN into the child.</summary>
    public const string ConnectionStringEnvVar = "BACKWAVE_JOBMASTER_POSTGRES_DSN";

    /// <summary>Environment variable carrying the number of Medium-priority buckets the worker owns.</summary>
    public const string BucketCountEnvVar = "BACKWAVE_JOBMASTER_BUCKETS";

    /// <summary>Environment variable carrying the per-bucket in-memory buffer size.</summary>
    public const string BucketBufferSizeEnvVar = "BACKWAVE_JOBMASTER_BUCKET_BUFFER";

    /// <summary>Environment variable carrying the worker's parallelism factor (run slots = 5 x factor).</summary>
    public const string ParallelismFactorEnvVar = "BACKWAVE_JOBMASTER_PARALLELISM_FACTOR";

    /// <summary>The serializer settings both sides use.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>One request from the parent to the child.</summary>
/// <param name="Op">One of the operation constants on <see cref="JobMasterHostProtocol"/>.</param>
/// <param name="Spec">The workload for <c>preload</c> and <c>execute</c>; null for <c>samples</c>.</param>
internal sealed record JobMasterHostRequest(string Op, WorkloadSpec? Spec);

/// <summary>One response from the child to the parent.</summary>
/// <param name="Ok">False when the operation failed; <paramref name="Error"/> then carries the reason.</param>
/// <param name="Error">The failure message, or null.</param>
/// <param name="EnqueueTicks">Per-call enqueue latencies in ticks; only on a <c>samples</c> response.</param>
/// <param name="EndToEndTicks">Per-job enqueue-to-success latencies in ticks; only on a <c>samples</c> response.</param>
internal sealed record JobMasterHostResponse(
    bool Ok,
    string? Error = null,
    long[]? EnqueueTicks = null,
    long[]? EndToEndTicks = null);
