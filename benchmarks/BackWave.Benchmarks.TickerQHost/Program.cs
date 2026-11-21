using System.Collections.Concurrent;
using System.Text.Json;
using BackWave.Benchmarks.Targets;
using BackWave.Benchmarks.TickerQHost;
using BackWave.Benchmarks.Workload;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using TickerQ.DependencyInjection;
using TickerQ.EntityFrameworkCore.DbContextFactory;
using TickerQ.EntityFrameworkCore.DependencyInjection;
using TickerQ.Utilities;
using TickerQ.Utilities.Entities;
using TickerQ.Utilities.Enums;
using TickerQ.Utilities.Interfaces.Managers;

// Two TickerQ instances share the one store. A producer-only instance (DisableBackgroundServices) persists
// the drain backlog without dispatching it: on a processing instance every run-now ticker is acquired and
// handed to the local worker pool inside AddAsync, so the backlog would run before the window opens. The
// processing host is built at startup and started by "start", outside the timed window.

var dsn = Environment.GetEnvironmentVariable(TickerQHostProtocol.ConnectionStringEnvVar)
    ?? throw new InvalidOperationException($"{TickerQHostProtocol.ConnectionStringEnvVar} is not set.");
var maxConcurrency = ReadMs(TickerQHostProtocol.MaxConcurrencyEnvVar, Environment.ProcessorCount);
var fallbackInterval = TimeSpan.FromMilliseconds(ReadMs(TickerQHostProtocol.FallbackIntervalEnvVar, 10));
var minPollingInterval = TimeSpan.FromMilliseconds(ReadMs(TickerQHostProtocol.MinPollingIntervalEnvVar, 0));

// Scheduler MaxConcurrency sizes TickerQ's worker loops, but a worker starts each async job without awaiting it,
// so it does not bound jobs in flight. The per-function limit is the semaphore that does; without it a drain
// backlog runs thousands of jobs at once and exhausts the connection pool.
TickerFunctionProvider.Configure(TickerQBenchmarkJob.FunctionName, maxConcurrency: maxConcurrency);

// The function registry is static and normally built by the processing host's initializer on start; the
// producer validates function names before then, so build it up front.
TickerFunctionProvider.Build();
if (TickerFunctionProvider.TickerFunctions[TickerQBenchmarkJob.FunctionName].MaxConcurrency != maxConcurrency)
{
    throw new InvalidOperationException("The per-function concurrency limit was not applied.");
}

var enqueuedAt = new ConcurrentDictionary<Guid, DateTimeOffset>();
var commitStamp = new EnqueueCommitStamp(enqueuedAt);

var producerServices = new ServiceCollection();
producerServices.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
producerServices.AddTickerQ(options =>
{
    ConfigureTickerQ(options);
    options.DisableBackgroundServices();
});
await using var producer = producerServices.BuildServiceProvider();

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddTickerQ(ConfigureTickerQ);
using var host = builder.Build();
host.UseTickerQ();

await using (var scope = producer.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<TickerQDbContext>().Database.EnsureCreatedAsync().ConfigureAwait(false);
}

await using var dataSource = NpgsqlDataSource.Create(dsn);
var producerManager = producer.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
var processingManager = host.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
var enqueueLatencies = new List<TimeSpan>();
TimeSpan? producerElapsed = null;
var stdout = Console.Out;
Respond(new TickerQHostResponse(true));

string? line;
while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) is not null)
{
    TickerQHostResponse response;
    try
    {
        var request = JsonSerializer.Deserialize<TickerQHostRequest>(line, TickerQHostProtocol.Json)
            ?? throw new InvalidOperationException("Empty request.");
        response = request.Op switch
        {
            TickerQHostProtocol.Preload => await PreloadAsync(Spec(request)).ConfigureAwait(false),
            TickerQHostProtocol.Start => await StartAsync().ConfigureAwait(false),
            TickerQHostProtocol.Execute => await ExecuteAsync(Spec(request)).ConfigureAwait(false),
            TickerQHostProtocol.Samples => await SamplesAsync().ConfigureAwait(false),
            _ => new TickerQHostResponse(false, $"Unknown op '{request.Op}'."),
        };
    }
    catch (Exception ex)
    {
        response = new TickerQHostResponse(false, ex.ToString());
    }

    Respond(response);
}

return 0;

void ConfigureTickerQ(TickerOptionsBuilder<TimeTickerEntity, CronTickerEntity> options)
{
    options.AddOperationalStore(ef => ef.UseTickerQDbContext<TickerQDbContext>(db => db.UseNpgsql(dsn).AddInterceptors(commitStamp)));
    options.ConfigureScheduler(scheduler =>
    {
        scheduler.MaxConcurrency = maxConcurrency;
        scheduler.FallbackIntervalChecker = fallbackInterval;
        scheduler.MinPollingInterval = minPollingInterval;
    });
}

static WorkloadSpec Spec(TickerQHostRequest request)
    => request.Spec ?? throw new InvalidOperationException($"'{request.Op}' needs a workload spec.");

static int ReadMs(string name, int fallback)
{
    var raw = Environment.GetEnvironmentVariable(name);
    return int.TryParse(raw, out var value) && value >= 0 ? value : fallback;
}

void Respond(TickerQHostResponse response)
{
    stdout.Write(TickerQHostProtocol.ResponsePrefix);
    stdout.WriteLine(JsonSerializer.Serialize(response, TickerQHostProtocol.Json));
    stdout.Flush();
}

async Task<TickerQHostResponse> PreloadAsync(WorkloadSpec spec)
{
    if (spec.Arrival != ArrivalMode.Drain)
    {
        return new TickerQHostResponse(true);
    }

    var latencies = await ParallelProducer.RunAsync(
        spec, paceRatePerSecond: 0, (job, ct) => EnqueueAsync(producerManager, job, ct), CancellationToken.None)
        .ConfigureAwait(false);
    enqueueLatencies.AddRange(latencies);
    producerElapsed = ParallelProducer.LastElapsed;
    return new TickerQHostResponse(true);
}

async Task<TickerQHostResponse> StartAsync()
{
    await host.StartAsync().ConfigureAwait(false);
    return new TickerQHostResponse(true);
}

async Task<TickerQHostResponse> ExecuteAsync(WorkloadSpec spec)
{
    if (spec.Arrival == ArrivalMode.Sustained)
    {
        var latencies = await ParallelProducer.RunAsync(
            spec, spec.SustainedRatePerSecond, (job, ct) => EnqueueAsync(processingManager, job, ct), CancellationToken.None)
            .ConfigureAwait(false);
        enqueueLatencies.AddRange(latencies);
        producerElapsed = ParallelProducer.LastElapsed;
    }

    await WaitForFinalAsync(spec.JobCount).ConfigureAwait(false);
    var unsuccessful = await CountAsync("""SELECT count(*) FROM ticker."TimeTickers" WHERE "Status" IN (5, 6, 7)""").ConfigureAwait(false);
    return unsuccessful == 0
        ? new TickerQHostResponse(true)
        : new TickerQHostResponse(false, $"{unsuccessful} tickers ended unsuccessful: {await StatusBreakdownAsync().ConfigureAwait(false)}");
}

// End-to-end latency = TickerQ's own ExecutedAt stamp (taken when the handler returns) minus the instant the
// ticker's INSERT committed, recorded by EnqueueCommitStamp.
async Task<TickerQHostResponse> SamplesAsync()
{
    var endToEnd = new List<long>(enqueuedAt.Count);
    long? firstCompletion = null;
    long? lastCompletion = null;
    await using var command = dataSource.CreateCommand(
        """SELECT "Id", "ExecutedAt" FROM ticker."TimeTickers" WHERE "Status" IN (3, 4) AND "ExecutedAt" IS NOT NULL""");
    await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
    while (await reader.ReadAsync().ConfigureAwait(false))
    {
        var id = reader.GetGuid(0);
        var executedAt = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
        if (enqueuedAt.TryGetValue(id, out var startedAt))
        {
            endToEnd.Add((executedAt - startedAt.UtcDateTime).Ticks);
            firstCompletion = Math.Min(firstCompletion ?? long.MaxValue, executedAt.Ticks);
            lastCompletion = Math.Max(lastCompletion ?? long.MinValue, executedAt.Ticks);
        }
    }

    if (endToEnd.Count != enqueuedAt.Count)
    {
        return new TickerQHostResponse(false, $"{endToEnd.Count} end-to-end samples for {enqueuedAt.Count} committed tickers.");
    }

    return new TickerQHostResponse(true, EnqueueTicks: enqueueLatencies.Select(t => t.Ticks).ToArray(), EndToEndTicks: endToEnd.ToArray(),
        Executions: TickerQBenchmarkJob.Executions, FirstCompletionUtcTicks: firstCompletion, LastCompletionUtcTicks: lastCompletion,
        ProducerTicks: producerElapsed?.Ticks);
}

async ValueTask EnqueueAsync(ITimeTickerManager<TimeTickerEntity> manager, BenchJob job, CancellationToken cancellationToken)
{
    var ticker = new TimeTickerEntity
    {
        Function = TickerQBenchmarkJob.FunctionName,
        ExecutionTime = DateTime.UtcNow,
        Request = TickerHelper.CreateTickerRequest(job),
        Retries = 0,
    };
    var result = await manager.AddAsync(ticker, cancellationToken).ConfigureAwait(false);
    if (!result.IsSucceeded)
    {
        throw new InvalidOperationException("TickerQ refused an enqueue.", result.Exception);
    }
}

async Task WaitForFinalAsync(int target)
{
    var stallLimit = TimeSpan.FromSeconds(60);
    var lastCount = -1L;
    var lastProgress = DateTime.UtcNow;
    while (true)
    {
        var count = await CountAsync("""SELECT count(*) FROM ticker."TimeTickers" WHERE "Status" IN (3, 4, 5, 6, 7)""").ConfigureAwait(false);
        if (count >= target)
        {
            return;
        }

        if (count != lastCount)
        {
            lastCount = count;
            lastProgress = DateTime.UtcNow;
        }
        else if (DateTime.UtcNow - lastProgress > stallLimit)
        {
            throw new TimeoutException(
                $"No ticker reached a final status for {stallLimit.TotalSeconds:0}s: {await StatusBreakdownAsync().ConfigureAwait(false)}");
        }

        await Task.Delay(10).ConfigureAwait(false);
    }
}

async Task<long> CountAsync(string sql)
{
    await using var command = dataSource.CreateCommand(sql);
    return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L);
}

async Task<string> StatusBreakdownAsync()
{
    await using var command = dataSource.CreateCommand(
        """SELECT "Status", count(*), max("ExceptionMessage") FROM ticker."TimeTickers" GROUP BY "Status" ORDER BY "Status" """);
    await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
    var parts = new List<string>();
    while (await reader.ReadAsync().ConfigureAwait(false))
    {
        var status = (TickerStatus)reader.GetInt32(0);
        var error = reader.IsDBNull(2) ? "" : $" (e.g. {reader.GetString(2)})";
        parts.Add($"{status}={reader.GetInt64(1)}{error}");
    }

    return string.Join(", ", parts);
}
