using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackWave.Tests;

// The logs pillar (issue 0251): the source-generated [LoggerMessage] catalog and the job-context scopes.
// These drive a single job's enqueue → execute → retry → dead-letter path through the client and the
// deterministic pump with a captured logger, and assert the catalogued events land at their levels under
// a scope carrying job_id / wire_name / attempt / queue - and that a disabled logger emits nothing.

public sealed record LoggedWork(string Note);

// Always fails, so the retry ceiling is exercised: attempt 1 retries, attempt 2 dead-letters.
public sealed class LoggedWorkHandler : IJobHandler<LoggedWork>
{
    public Task HandleAsync(LoggedWork job, JobContext context, CancellationToken cancellationToken)
        => throw new InvalidOperationException($"boom (attempt {context.Attempt})");
}

[JsonSerializable(typeof(LoggedWork))]
internal sealed partial class LogCatalogJsonContext : JsonSerializerContext;

public class LogCatalogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Two attempts, a one-minute backoff: attempt 1 fails and retries, attempt 2 fails and dead-letters.
    private static readonly RetryPolicy TwoAttempts = new()
    {
        MaxAttempts = 2,
        Backoff = _ => TimeSpan.FromMinutes(1),
    };

    private static (BackWaveClient Client, DeterministicPump Pump, IJobStore Store) CreateFixture(
        LogCapture capture)
    {
        var services = new ServiceCollection()
            .AddTransient<IJobHandler<LoggedWork>, LoggedWorkHandler>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<LoggedWork, LoggedWorkHandler>(
                "logged-work", LogCatalogJsonContext.Default.LoggedWork),
        ]);
        var store = new InMemoryJobStore();
        var driver = new NodeDriver(new NodeOptions
        {
            WorkerId = "node-1",
            Policy = new DispatchPolicy.Strict(["default"]),
            RetryPolicy = TwoAttempts,
        });
        var factory = new CapturingLoggerFactory(capture);
        var pump = new DeterministicPump(
            driver, store, registry, services, logger: factory.CreateLogger("BackWave.Testing"));
        var client = new BackWaveClient(store, registry, loggerFactory: factory);
        return (client, pump, store);
    }

    [Fact]
    public async Task SingleJob_EnqueueThroughDeadLetter_EmitsCatalogEventsAtTheirLevelsUnderScope()
    {
        var capture = new LogCapture();
        var (client, pump, store) = CreateFixture(capture);

        var jobId = await client.EnqueueAsync(new LoggedWork("x"), dueTime: T0);
        await pump.PumpAsync(T0);              // attempt 1 → fails → retry scheduled
        await pump.PumpAsync(T0.AddMinutes(1)); // attempt 2 → fails → dead-lettered

        Assert.Equal(JobState.DeadLettered, (await store.GetJobAsync(jobId))!.State);

        // Job enqueued (Debug).
        var enqueued = Assert.Single(capture.Records, r => r.EventId == 1001);
        Assert.Equal(LogLevel.Debug, enqueued.Level);

        // Lease acquired (Trace), once per attempt.
        var leases = capture.Records.Where(r => r.EventId == 1101).ToList();
        Assert.Equal(2, leases.Count);
        Assert.All(leases, r => Assert.Equal(LogLevel.Trace, r.Level));

        // Execution start / complete (Debug), once per attempt, each under the job scope.
        Assert.Equal(2, capture.Records.Count(r => r.EventId == 1102 && r.Level == LogLevel.Debug));
        Assert.Equal(2, capture.Records.Count(r => r.EventId == 1103 && r.Level == LogLevel.Debug));

        // Retry scheduled (Information) after attempt 1; scope carries attempt 1.
        var retry = Assert.Single(capture.Records, r => r.EventId == 1201);
        Assert.Equal(LogLevel.Information, retry.Level);
        AssertScope(retry, jobId, attempt: 1);

        // Dead-lettered (Error) after attempt 2; scope carries attempt 2.
        var dead = Assert.Single(capture.Records, r => r.EventId == 1203);
        Assert.Equal(LogLevel.Error, dead.Level);
        AssertScope(dead, jobId, attempt: 2);

        // Every execution and settlement event sits under a scope stamping job_id/wire_name/attempt/queue.
        foreach (var record in capture.Records.Where(r => r.EventId is 1102 or 1103 or 1201 or 1203))
        {
            Assert.Equal(jobId, Assert.IsType<Guid>(ScopeValue(record, "job_id")));
            Assert.Equal("logged-work", ScopeValue(record, "wire_name"));
            Assert.Equal("default", ScopeValue(record, "queue"));
            Assert.NotNull(ScopeValue(record, "attempt"));
        }
    }

    [Fact]
    public async Task DisabledLogger_EmitsNothing_OnTheWholePath()
    {
        var capture = new LogCapture { Enabled = false };
        var (client, pump, store) = CreateFixture(capture);

        var jobId = await client.EnqueueAsync(new LoggedWork("x"), dueTime: T0);
        await pump.PumpAsync(T0);
        await pump.PumpAsync(T0.AddMinutes(1));

        // The behaviour is unchanged - the job still dead-letters - but the source-gen IsEnabled guard
        // suppresses every catalog call, so nothing is formatted or recorded (the zero-allocation path a
        // null ILoggerFactory / NullLogger takes).
        Assert.Equal(JobState.DeadLettered, (await store.GetJobAsync(jobId))!.State);
        Assert.Empty(capture.Records);
    }

    private static void AssertScope(LogRecord record, Guid jobId, int attempt)
    {
        Assert.Equal(jobId, ScopeValue(record, "job_id"));
        Assert.Equal("logged-work", ScopeValue(record, "wire_name"));
        Assert.Equal(attempt, ScopeValue(record, "attempt"));
        Assert.Equal("default", ScopeValue(record, "queue"));
    }

    private static object? ScopeValue(LogRecord record, string key)
        => record.Scope.First(kv => kv.Key == key).Value;
}
