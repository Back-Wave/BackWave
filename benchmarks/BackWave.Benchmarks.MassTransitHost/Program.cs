using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using BackWave.Benchmarks.Targets;
using BackWave.Benchmarks.Workload;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

const string Schema = "transport";

var dsn = Required(MassTransitHostProtocol.ConnectionStringEnvVar);
var concurrentMessageLimit = RequiredInt(MassTransitHostProtocol.ConcurrentMessageLimitEnvVar);
var prefetchCount = RequiredInt(MassTransitHostProtocol.PrefetchCountEnvVar);
var pollingInterval = TimeSpan.FromMilliseconds(RequiredInt(MassTransitHostProtocol.PollingMsEnvVar));
var connectionLimit = RequiredInt(MassTransitHostProtocol.ConnectionLimitEnvVar);

var drainPollInterval = TimeSpan.FromMilliseconds(10);
var completions = new CompletionLog();
var enqueueLatencies = new List<TimeSpan>();
var enqueuedAt = new ConcurrentDictionary<string, DateTimeOffset>();
var sequence = 0;
TimeSpan? producerElapsed = null;
IHost? bus = null;
ISendEndpoint? sendEndpoint = null;

await using var dataSource = NpgsqlDataSource.Create(dsn);
var stdout = Console.Out;
Respond(new MassTransitHostResponse(true));

string? line;
while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) is not null)
{
    MassTransitHostResponse response;
    try
    {
        var request = JsonSerializer.Deserialize<MassTransitHostRequest>(line, MassTransitHostProtocol.Json)
            ?? throw new InvalidOperationException("Empty request.");
        response = request.Op switch
        {
            MassTransitHostProtocol.Setup => await SetupAsync().ConfigureAwait(false),
            MassTransitHostProtocol.Reset => await ResetAsync().ConfigureAwait(false),
            MassTransitHostProtocol.Preload => await PreloadAsync(Spec(request)).ConfigureAwait(false),
            MassTransitHostProtocol.Start => await StartAsync().ConfigureAwait(false),
            MassTransitHostProtocol.Execute => await ExecuteAsync(Spec(request)).ConfigureAwait(false),
            MassTransitHostProtocol.Cooldown => await CooldownAsync().ConfigureAwait(false),
            MassTransitHostProtocol.Samples => Samples(),
            _ => new MassTransitHostResponse(false, $"Unknown op '{request.Op}'."),
        };
    }
    catch (Exception ex)
    {
        response = new MassTransitHostResponse(false, ex.ToString());
    }

    Respond(response);
}

await CooldownAsync().ConfigureAwait(false);
return 0;

static string Required(string name)
    => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is not set.");

static int RequiredInt(string name)
    => int.Parse(Required(name), NumberStyles.Integer, CultureInfo.InvariantCulture);

static WorkloadSpec Spec(MassTransitHostRequest request)
    => request.Spec ?? throw new InvalidOperationException($"'{request.Op}' needs a workload spec.");

void Respond(MassTransitHostResponse response)
{
    stdout.Write(MassTransitHostProtocol.ResponsePrefix);
    stdout.WriteLine(JsonSerializer.Serialize(response, MassTransitHostProtocol.Json));
    stdout.Flush();
}

async Task<MassTransitHostResponse> SetupAsync()
{
    // Connecting the receive endpoint once creates the bench queue before the first run.
    using (var setup = BuildHost(migrate: true))
    {
        await setup.StartAsync().ConfigureAwait(false);
        var handle = setup.Services.GetRequiredService<IReceiveEndpointConnector>()
            .ConnectReceiveEndpoint(WorkloadSpec.BenchQueue, (_, endpoint) => endpoint.ConfigureConsumeTopology = false);
        await handle.Ready.ConfigureAwait(false);
        await handle.StopAsync().ConfigureAwait(false);
        await setup.StopAsync().ConfigureAwait(false);
    }

    await using var command = dataSource.CreateCommand("SHOW server_version");
    var version = await command.ExecuteScalarAsync().ConfigureAwait(false);
    return new MassTransitHostResponse(true, ServerVersion: $"PostgreSQL {version}");
}

async Task<MassTransitHostResponse> ResetAsync()
{
    enqueueLatencies.Clear();
    enqueuedAt.Clear();
    completions.Clear();
    producerElapsed = null;

    await using (var command = dataSource.CreateCommand(
        $"TRUNCATE \"{Schema}\".message, \"{Schema}\".message_delivery, " +
        $"\"{Schema}\".queue_metric_capture, \"{Schema}\".queue_metric"))
    {
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    bus = BuildHost(migrate: false);
    await bus.StartAsync().ConfigureAwait(false);
    sendEndpoint = await bus.Services.GetRequiredService<ISendEndpointProvider>()
        .GetSendEndpoint(new Uri($"queue:{WorkloadSpec.BenchQueue}")).ConfigureAwait(false);
    return new MassTransitHostResponse(true);
}

async Task<MassTransitHostResponse> PreloadAsync(WorkloadSpec spec)
{
    if (spec.Arrival == ArrivalMode.Drain)
    {
        await EnqueueAllAsync(spec, paceRatePerSecond: 0).ConfigureAwait(false);
    }

    return new MassTransitHostResponse(true);
}

async Task<MassTransitHostResponse> StartAsync()
{
    var host = bus ?? throw new InvalidOperationException("'reset' has not run.");
    var handle = host.Services.GetRequiredService<IReceiveEndpointConnector>()
        .ConnectReceiveEndpoint(WorkloadSpec.BenchQueue, (context, endpoint) =>
        {
            endpoint.ConfigureConsumeTopology = false;
            endpoint.PrefetchCount = prefetchCount;
            endpoint.ConcurrentMessageLimit = concurrentMessageLimit;
            ((ISqlReceiveEndpointConfigurator)endpoint).PollingInterval = pollingInterval;
            endpoint.ConfigureConsumer<MassTransitBenchmarkConsumer>(context);
        });
    await handle.Ready.ConfigureAwait(false);
    return new MassTransitHostResponse(true);
}

async Task<MassTransitHostResponse> ExecuteAsync(WorkloadSpec spec)
{
    if (spec.Arrival == ArrivalMode.Sustained)
    {
        await EnqueueAllAsync(spec, spec.SustainedRatePerSecond).ConfigureAwait(false);
    }

    await WaitForDrainAsync().ConfigureAwait(false);
    return new MassTransitHostResponse(true);
}

async Task<MassTransitHostResponse> CooldownAsync()
{
    sendEndpoint = null;
    if (bus is not null)
    {
        await bus.StopAsync().ConfigureAwait(false);
        bus.Dispose();
        bus = null;
    }

    return new MassTransitHostResponse(true);
}

MassTransitHostResponse Samples()
{
    var completed = completions.CompletedAt;
    if (completed.Count != enqueuedAt.Count || completions.Executions != enqueuedAt.Count)
    {
        return new MassTransitHostResponse(false,
            $"MassTransit drained {enqueuedAt.Count} sends with {completed.Count} distinct messages consumed " +
            $"over {completions.Executions} consumer runs.");
    }

    var endToEnd = new List<long>(enqueuedAt.Count);
    foreach (var (key, sentAt) in enqueuedAt)
    {
        endToEnd.Add((completed[key] - sentAt).Ticks);
    }

    var instants = completed.Values;
    return new MassTransitHostResponse(true,
        EnqueueTicks: enqueueLatencies.Select(t => t.Ticks).ToArray(),
        EndToEndTicks: endToEnd.ToArray(),
        Executions: completions.Executions,
        FirstCompletionUtcTicks: instants.Min().UtcTicks,
        LastCompletionUtcTicks: instants.Max().UtcTicks,
        ProducerTicks: producerElapsed?.Ticks);
}

IHost BuildHost(bool migrate)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);

    builder.Services.AddSingleton(completions);
    builder.Services.Configure<SqlTransportOptions>(options =>
    {
        options.ConnectionString = dsn;
        options.Schema = Schema;
        options.ConnectionLimit = connectionLimit;
    });
    builder.Services.Configure<MassTransitHostOptions>(options => options.WaitUntilStarted = true);
    if (migrate)
    {
        builder.Services.AddPostgresMigrationHostedService(options =>
        {
            options.CreateDatabase = false;
            options.CreateSchema = true;
            options.CreateInfrastructure = true;
        });
    }

    builder.Services.AddMassTransit(configurator =>
    {
        configurator.AddConsumer<MassTransitBenchmarkConsumer>();
        configurator.UsingPostgres();
    });

    return builder.Build();
}

async Task EnqueueAllAsync(WorkloadSpec spec, double paceRatePerSecond)
{
    var endpoint = sendEndpoint ?? throw new InvalidOperationException("'reset' has not run.");
    var latencies = await ParallelProducer.RunAsync(
        spec, paceRatePerSecond,
        async (job, token) =>
        {
            var key = Interlocked.Increment(ref sequence).ToString(CultureInfo.InvariantCulture);
            var calledAt = DateTimeOffset.UtcNow;
            await endpoint.Send(new MassTransitBenchMessage(key, job.Payload, job.DelayMs), token).ConfigureAwait(false);
            enqueuedAt[key] = calledAt;
        },
        CancellationToken.None).ConfigureAwait(false);
    enqueueLatencies.AddRange(latencies);
    producerElapsed = ParallelProducer.LastElapsed;
}

async Task WaitForDrainAsync()
{
    await using var command = dataSource.CreateCommand(
        $"SELECT EXISTS (SELECT 1 FROM \"{Schema}\".message_delivery md " +
        $"JOIN \"{Schema}\".queue q ON q.id = md.queue_id WHERE q.name = $1 AND q.type = 1)");
    command.Parameters.Add(new NpgsqlParameter { Value = WorkloadSpec.BenchQueue });
    while (true)
    {
        if (await command.ExecuteScalarAsync().ConfigureAwait(false) is false)
        {
            return;
        }

        await Task.Delay(drainPollInterval).ConfigureAwait(false);
    }
}
