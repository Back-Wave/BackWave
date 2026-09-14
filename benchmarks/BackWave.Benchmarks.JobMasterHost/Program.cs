using System.Collections.Concurrent;
using System.Text.Json;
using BackWave.Benchmarks.JobMasterHost;
using BackWave.Benchmarks.Targets;
using BackWave.Benchmarks.Workload;
using JobMaster;
using JobMaster.Abstractions.Models;
using JobMaster.Ioc.Extensions;
using JobMaster.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

// The JobMaster benchmark host. One process per benchmark run: the harness spawns it, waits for the ready
// line, then drives preload / execute / samples over stdin and reads one "#bench {...}" JSON line per
// request from stdout. Everything JobMaster itself prints (it logs to its own table, so normally nothing)
// passes through unprefixed and the parent forwards it to stderr.
//
// Environment:
//   BACKWAVE_JOBMASTER_POSTGRES_DSN         Postgres connection string (required)
//   BACKWAVE_JOBMASTER_BUCKETS              Medium-priority buckets the worker owns (default 1)
//   BACKWAVE_JOBMASTER_BUCKET_BUFFER        per-bucket in-memory buffer (default 1000, tuned; JobMaster ships 250)
//   BACKWAVE_JOBMASTER_PARALLELISM_FACTOR   run slots = 5 x factor (the parent derives it from BackWave's pool)

var dsn = Environment.GetEnvironmentVariable(JobMasterHostProtocol.ConnectionStringEnvVar)
    ?? throw new InvalidOperationException($"{JobMasterHostProtocol.ConnectionStringEnvVar} is not set.");
var buckets = ReadInt(JobMasterHostProtocol.BucketCountEnvVar, 1);
var bucketBuffer = ReadInt(JobMasterHostProtocol.BucketBufferSizeEnvVar, 1000);
var parallelismFactor = ReadInt(JobMasterHostProtocol.ParallelismFactorEnvVar, 8);

var services = new ServiceCollection();
services.AddJobMasterCluster("bench", cluster =>
{
    cluster.UsePostgresForMaster(dsn).SetAsDefault();
    // Gated jobs hold their run slot from preload until the window opens, so the default 1 minute timeout
    // would fail them before the run starts. Retries are off to match the BackWave and Hangfire targets.
    cluster.DefaultJobTimeout(TimeSpan.FromMinutes(30));
    cluster.TransientThreshold(TimeSpan.FromMinutes(2));
    cluster.DefaultMaxRetryCount(0);
    cluster.RetainDataForever();
    // One priority lane, like the single "bench" queue on the other targets.
    cluster.DisablePriority(JobMasterPriority.VeryLow);
    cluster.DisablePriority(JobMasterPriority.Low);
    cluster.DisablePriority(JobMasterPriority.High);
    cluster.DisablePriority(JobMasterPriority.Critical);
    cluster.AddAgentConnectionConfig("bench-agent").UsePostgresForAgent(dsn);
    cluster.AddWorker("bench", "bench-agent", transferBatchSize: 1000, bucketBufferSize: bucketBuffer)
        .ParallelismFactor(parallelismFactor)
        .BucketQtyConfig(JobMasterPriority.Medium, buckets)
        .SetWorkerMode(AgentWorkerMode.Full)
        .SkipWarmUpTime();
});

await using var provider = services.BuildServiceProvider();
await provider.StartJobMasterRuntimeAsync().ConfigureAwait(false);

await using var dataSource = NpgsqlDataSource.Create(dsn);
var enqueuedAt = new ConcurrentDictionary<Guid, DateTimeOffset>();
var enqueueLatencies = new List<TimeSpan>();
var stdout = Console.Out;
Respond(new JobMasterHostResponse(true));

string? line;
while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) is not null)
{
    JobMasterHostResponse response;
    try
    {
        var request = JsonSerializer.Deserialize<JobMasterHostRequest>(line, JobMasterHostProtocol.Json)
            ?? throw new InvalidOperationException("Empty request.");
        response = request.Op switch
        {
            JobMasterHostProtocol.Preload => await PreloadAsync(Spec(request)).ConfigureAwait(false),
            JobMasterHostProtocol.Execute => await ExecuteAsync(Spec(request)).ConfigureAwait(false),
            JobMasterHostProtocol.Samples => await SamplesAsync().ConfigureAwait(false),
            _ => new JobMasterHostResponse(false, $"Unknown op '{request.Op}'."),
        };
    }
    catch (Exception ex)
    {
        response = new JobMasterHostResponse(false, ex.ToString());
    }

    Respond(response);
}

return 0;

static WorkloadSpec Spec(JobMasterHostRequest request)
    => request.Spec ?? throw new InvalidOperationException($"'{request.Op}' needs a workload spec.");

static int ReadInt(string name, int fallback)
{
    var raw = Environment.GetEnvironmentVariable(name);
    return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
}

void Respond(JobMasterHostResponse response)
{
    stdout.Write(JobMasterHostProtocol.ResponsePrefix);
    stdout.WriteLine(JsonSerializer.Serialize(response, JobMasterHostProtocol.Json));
    stdout.Flush();
}

// Drain preload: enqueue the whole stream, then wait until JobMaster has durably ingested every job. The
// handler gate is still closed, so any job JobMaster already pulled into a run slot parks there.
async Task<JobMasterHostResponse> PreloadAsync(WorkloadSpec spec)
{
    if (spec.Arrival != ArrivalMode.Drain)
    {
        return new JobMasterHostResponse(true);
    }

    var latencies = await ParallelProducer.RunAsync(spec, paceRatePerSecond: 0, EnqueueAsync, CancellationToken.None)
        .ConfigureAwait(false);
    enqueueLatencies.AddRange(latencies);
    await WaitForCountAsync("SELECT count(*) FROM jm_job", spec.JobCount).ConfigureAwait(false);
    return new JobMasterHostResponse(true);
}

// The timed window: open the gate, run the producers when sustained, and wait until every job is final.
async Task<JobMasterHostResponse> ExecuteAsync(WorkloadSpec spec)
{
    JobMasterBenchmarkJob.Gate.TrySetResult();
    if (spec.Arrival == ArrivalMode.Sustained)
    {
        var latencies = await ParallelProducer.RunAsync(spec, spec.SustainedRatePerSecond, EnqueueAsync, CancellationToken.None)
            .ConfigureAwait(false);
        enqueueLatencies.AddRange(latencies);
    }

    // Final statuses: Succeeded=5, Failed=7, Cancelled=8, Aborted=9.
    await WaitForCountAsync("SELECT count(*) FROM jm_job WHERE status IN (5, 7, 8, 9)", spec.JobCount).ConfigureAwait(false);
    return new JobMasterHostResponse(true);
}

// End-to-end latency = JobMaster's own finalized_at stamp minus the enqueue timestamp this process recorded.
async Task<JobMasterHostResponse> SamplesAsync()
{
    var endToEnd = new List<long>(enqueuedAt.Count);
    await using var command = dataSource.CreateCommand("SELECT id, finalized_at FROM jm_job WHERE status = 5 AND finalized_at IS NOT NULL");
    await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
    while (await reader.ReadAsync().ConfigureAwait(false))
    {
        var id = reader.GetGuid(0);
        var finalizedAt = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
        if (enqueuedAt.TryGetValue(id, out var startedAt))
        {
            endToEnd.Add((finalizedAt - startedAt.UtcDateTime).Ticks);
        }
    }

    return new JobMasterHostResponse(true, EnqueueTicks: enqueueLatencies.Select(t => t.Ticks).ToArray(), EndToEndTicks: endToEnd.ToArray());
}

async ValueTask EnqueueAsync(BenchJob job, CancellationToken cancellationToken)
{
    var data = WriteableMessageData.New()
        .SetStringValue(JobMasterBenchmarkJob.PayloadKey, job.Payload)
        .SetIntValue(JobMasterBenchmarkJob.DelayKey, job.DelayMs);
    var context = await JobMasterScheduler.Instance.OnceNowAsync<JobMasterBenchmarkJob>(data).ConfigureAwait(false);
    enqueuedAt[context.Id] = DateTimeOffset.UtcNow;
}

async Task WaitForCountAsync(string sql, int target)
{
    while (true)
    {
        await using var command = dataSource.CreateCommand(sql);
        var count = (long)(await command.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L);
        if (count >= target)
        {
            return;
        }

        await Task.Delay(10).ConfigureAwait(false);
    }
}
