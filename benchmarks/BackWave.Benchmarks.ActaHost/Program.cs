using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Acta;
using Acta.Postgres;
using BackWave.Benchmarks.ActaHost;
using BackWave.Benchmarks.Targets;
using BackWave.Benchmarks.Workload;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

const string Schema = ActaHostProtocol.Schema;
const short Succeeded = 100;

var dsn = Required(ActaHostProtocol.ConnectionStringEnvVar);
var maxConcurrentExecutors = RequiredInt(ActaHostProtocol.MaxConcurrentExecutorsEnvVar);

var drainPollInterval = TimeSpan.FromMilliseconds(10);
var completions = new CompletionLog();
var enqueueLatencies = new List<TimeSpan>();
var enqueuedAt = new ConcurrentDictionary<string, DateTimeOffset>();
var sequence = 0;
var benchDefinitionId = 0;
var benchNamespaceId = 0;
TimeSpan? producerElapsed = null;
IHost? worker = null;

await using var dataSource = NpgsqlDataSource.Create(dsn);
var stdout = Console.Out;
Respond(new ActaHostResponse(true));

string? line;
while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) is not null)
{
    ActaHostResponse response;
    try
    {
        var request = JsonSerializer.Deserialize<ActaHostRequest>(line, ActaHostProtocol.Json)
            ?? throw new InvalidOperationException("Empty request.");
        response = request.Op switch
        {
            ActaHostProtocol.Setup => await SetupAsync().ConfigureAwait(false),
            ActaHostProtocol.Reset => await ResetAsync().ConfigureAwait(false),
            ActaHostProtocol.Preload => await PreloadAsync(Spec(request)).ConfigureAwait(false),
            ActaHostProtocol.Start => await StartAsync().ConfigureAwait(false),
            ActaHostProtocol.Execute => await ExecuteAsync(Spec(request)).ConfigureAwait(false),
            ActaHostProtocol.Cooldown => await CooldownAsync().ConfigureAwait(false),
            ActaHostProtocol.Samples => await SamplesAsync().ConfigureAwait(false),
            _ => new ActaHostResponse(false, $"Unknown op '{request.Op}'."),
        };
    }
    catch (Exception ex)
    {
        response = new ActaHostResponse(false, ex.ToString());
    }

    Respond(response);
}

await CooldownAsync().ConfigureAwait(false);
return 0;

static string Required(string name)
    => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is not set.");

static int RequiredInt(string name)
    => int.Parse(Required(name), NumberStyles.Integer, CultureInfo.InvariantCulture);

static WorkloadSpec Spec(ActaHostRequest request)
    => request.Spec ?? throw new InvalidOperationException($"'{request.Op}' needs a workload spec.");

void Respond(ActaHostResponse response)
{
    stdout.Write(ActaHostProtocol.ResponsePrefix);
    stdout.WriteLine(JsonSerializer.Serialize(response, ActaHostProtocol.Json));
    stdout.Flush();
}

async Task<ActaHostResponse> SetupAsync()
{
    await using var command = dataSource.CreateCommand("SHOW server_version");
    var version = await command.ExecuteScalarAsync().ConfigureAwait(false);
    return new ActaHostResponse(true, ServerVersion: $"PostgreSQL {version}");
}

async Task<ActaHostResponse> ResetAsync()
{
    enqueueLatencies.Clear();
    enqueuedAt.Clear();
    completions.Clear();
    producerElapsed = null;

    // Every run starts from an empty schema, provisioned the way Acta's own Verify tool provisions a trial: drop the
    // schema, then start and stop a worker host under MigrateOnStartup, which applies Acta's migrations and writes the
    // namespace's definitions. The measured worker host then starts with MigrateOnStartup off, as in production.
    await using (var command = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE"))
    {
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    using (var provisioner = BuildHost(runsWorker: true, migrate: true))
    {
        await provisioner.StartAsync().ConfigureAwait(false);
        await provisioner.StopAsync().ConfigureAwait(false);
    }

    await using (var command = dataSource.CreateCommand(
        $"SELECT d.definition_id, d.namespace_id FROM \"{Schema}\".definitions d " +
        $"JOIN \"{Schema}\".namespaces n ON n.namespace_id = d.namespace_id " +
        "WHERE n.namespace_name = $1 AND d.definition_name = $2"))
    {
        command.Parameters.Add(new NpgsqlParameter { Value = ActaHostProtocol.Namespace });
        command.Parameters.Add(new NpgsqlParameter { Value = ActaHostProtocol.JobName });
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException("Provisioning wrote no bench job definition.");
        }

        benchDefinitionId = reader.GetInt32(0);
        benchNamespaceId = reader.GetInt32(1);
    }

    worker = BuildHost(runsWorker: true, migrate: false);
    return new ActaHostResponse(true);
}

async Task<ActaHostResponse> PreloadAsync(WorkloadSpec spec)
{
    if (spec.Arrival == ArrivalMode.Drain)
    {
        // An enqueue-only host, so no worker claims a job before the timed window opens.
        using var producer = BuildHost(runsWorker: false, migrate: false);
        await producer.StartAsync().ConfigureAwait(false);
        await EnqueueAllAsync(producer, spec, paceRatePerSecond: 0).ConfigureAwait(false);
        await producer.StopAsync().ConfigureAwait(false);
    }

    return new ActaHostResponse(true);
}

async Task<ActaHostResponse> StartAsync()
{
    var host = worker ?? throw new InvalidOperationException("'reset' has not run.");
    await host.StartAsync().ConfigureAwait(false);
    return new ActaHostResponse(true);
}

async Task<ActaHostResponse> ExecuteAsync(WorkloadSpec spec)
{
    if (spec.Arrival == ArrivalMode.Sustained)
    {
        // Through the worker host's own IJobs: Acta's default wake-up is in-process, so an enqueue wakes the worker only
        // from the host it runs in, as in a single Acta host.
        var host = worker ?? throw new InvalidOperationException("'reset' has not run.");
        await EnqueueAllAsync(host, spec, spec.SustainedRatePerSecond).ConfigureAwait(false);
    }

    await WaitForDrainAsync().ConfigureAwait(false);
    return new ActaHostResponse(true);
}

async Task<ActaHostResponse> CooldownAsync()
{
    if (worker is not null)
    {
        await worker.StopAsync().ConfigureAwait(false);
        worker.Dispose();
        worker = null;
    }

    return new ActaHostResponse(true);
}

async Task<ActaHostResponse> SamplesAsync()
{
    var ledger = await ReadLedgerAsync().ConfigureAwait(false);
    Console.Error.WriteLine(ledger.Describe());
    var completed = completions.CompletedAt;
    var problems = ActaWorkCheck.Problems(enqueuedAt.Count, completed.Count, completions.Executions, ledger);
    if (problems.Count > 0)
    {
        return new ActaHostResponse(false, "Acta work check failed: " + string.Join("; ", problems));
    }

    var endToEnd = new List<long>(enqueuedAt.Count);
    foreach (var (key, calledAt) in enqueuedAt)
    {
        endToEnd.Add((completed[key] - calledAt).Ticks);
    }

    var instants = completed.Values;
    return new ActaHostResponse(true,
        EnqueueTicks: enqueueLatencies.Select(t => t.Ticks).ToArray(),
        EndToEndTicks: endToEnd.ToArray(),
        Executions: completions.Executions,
        FirstCompletionUtcTicks: instants.Min().UtcTicks,
        LastCompletionUtcTicks: instants.Max().UtcTicks,
        ProducerTicks: producerElapsed?.Ticks);
}

IHost BuildHost(bool runsWorker, bool migrate)
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
    builder.Services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(
        options => options.LogToStandardErrorThreshold = LogLevel.Trace);

    builder.Services.AddSingleton(completions);
    builder.Services.UseActa(acta =>
    {
        acta.UsePostgres(pg =>
        {
            pg.ConnectionString = dsn;
            pg.Schema = Schema;
            pg.MigrateOnStartup = migrate;
        });
        acta.ConfigureOptions(options => options.MaxConcurrentExecutors = maxConcurrentExecutors);
        if (runsWorker)
        {
            acta.Run<ActaHostJobs>(ActaHostProtocol.Namespace);
        }
        else
        {
            acta.Reference<ActaHostJobs>(ActaHostProtocol.Namespace);
        }
    });

    return builder.Build();
}

async Task EnqueueAllAsync(IHost host, WorkloadSpec spec, double paceRatePerSecond)
{
    var jobs = host.Services.GetRequiredService<IJobs>();
    var latencies = await ParallelProducer.RunAsync(
        spec, paceRatePerSecond,
        async (job, token) =>
        {
            var key = Interlocked.Increment(ref sequence).ToString(CultureInfo.InvariantCulture);
            var calledAt = DateTimeOffset.UtcNow;
            await jobs.EnqueueAsync(new ActaBenchInput(key, job.Payload, job.DelayMs), ct: token).ConfigureAwait(false);
            enqueuedAt[key] = calledAt;
        },
        CancellationToken.None).ConfigureAwait(false);
    enqueueLatencies.AddRange(latencies);
    producerElapsed = ParallelProducer.LastElapsed;
}

async Task WaitForDrainAsync()
{
    // True while a bench job is still Ready, Suspended, or Executing. Each arm restates the predicate of one partial
    // index (ix_jobs_state_claim_ready, ix_jobs_state_worker_inflight), so a poll reads only unfinished rows and stops
    // at the first; the join on definition_id leaves out sys.alerts and sys.retention, whose permanent jobs share the
    // namespace and can be Ready at any time. The Ready arm orders by the claim index's keys under LIMIT 1, so the
    // planner keeps that index even when the stats still count the drained rows as Ready; as a plain EXISTS it fell
    // back to scanning the whole table on each poll.
    await using var command = dataSource.CreateCommand(
        $"SELECT (SELECT 1 FROM \"{Schema}\".jobs_state s JOIN \"{Schema}\".jobs j ON j.job_id = s.job_id " +
        "WHERE s.namespace_id = $2 AND s.job_status_code IN (10, 20) AND s.next_run_at_utc IS NOT NULL " +
        "AND j.definition_id = $1 " +
        "ORDER BY s.namespace_id, s.job_priority_code DESC, s.next_run_at_utc, s.job_id LIMIT 1) IS NOT NULL " +
        $"OR EXISTS (SELECT 1 FROM \"{Schema}\".jobs_state s JOIN \"{Schema}\".jobs j ON j.job_id = s.job_id " +
        "WHERE s.leased_by_worker_id IS NOT NULL AND s.job_status_code = 50 AND j.definition_id = $1)");
    command.Parameters.Add(new NpgsqlParameter { Value = benchDefinitionId });
    command.Parameters.Add(new NpgsqlParameter { Value = benchNamespaceId });
    while (true)
    {
        if (await command.ExecuteScalarAsync().ConfigureAwait(false) is false)
        {
            return;
        }

        await Task.Delay(drainPollInterval).ConfigureAwait(false);
    }
}

async Task<ActaLedger> ReadLedgerAsync()
{
    await using var command = dataSource.CreateCommand(
        "SELECT count(*), " +
        $"count(*) FILTER (WHERE s.job_status_code = {Succeeded}), " +
        "count(*) FILTER (WHERE s.execution_number = 1), " +
        "COALESCE(max(s.execution_number), 0), " +
        "COALESCE(string_agg(convert_from(j.input, 'UTF8')::jsonb ->> 'key', ',') FILTER (WHERE s.execution_number > 1), ''), " +
        $"(SELECT count(*) FROM \"{Schema}\".jobs WHERE definition_id <> $1) " +
        $"FROM \"{Schema}\".jobs j JOIN \"{Schema}\".jobs_state s ON s.job_id = j.job_id WHERE j.definition_id = $1");
    command.Parameters.Add(new NpgsqlParameter { Value = benchDefinitionId });
    await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
    await reader.ReadAsync().ConfigureAwait(false);
    var repeated = reader.GetString(4);
    return new ActaLedger(
        Jobs: reader.GetInt64(0),
        Succeeded: reader.GetInt64(1),
        SingleExecution: reader.GetInt64(2),
        MaxExecutionNumber: reader.GetInt64(3),
        RepeatedKeys: repeated.Length == 0 ? [] : repeated.Split(','),
        OtherJobs: reader.GetInt64(5));
}
