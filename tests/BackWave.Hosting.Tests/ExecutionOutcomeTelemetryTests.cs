using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using BackWave.Core;
using BackWave.Diagnostics;
using BackWave.Jobs;
using BackWave.Monitor;
using BackWave.Operations;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Hosting.Tests;

/// <summary>
/// The execution edge classifies an <see cref="OperationCanceledException"/>; the record edge is TOLD
/// that verdict rather than re-deriving it from the exception type. These pin the three outcomes end to
/// end through a real host: a handler-raised cancellation is a plain FAILURE, while an operator cancel
/// and a host shutdown are not.
/// </summary>
public class ExecutionOutcomeTelemetryTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Every test registers its own Wire Name and filters the (process-global) BackWave meter down to
    /// it, so a concurrently running host in another test class can never bleed into these assertions.
    /// </summary>
    private static WebApplication BuildHost<THandler>(
        IJobStore store, string wireName, RetryPolicy? retryPolicy = null)
        where THandler : class, IJobHandler<PingJob>
    {
        var registry = new JobRegistry(
            [JobRegistration.Create<PingJob, THandler>(wireName, HostingJsonContext.Default.PingJob)]);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<PingRecorder>();
        builder.Services.AddSingleton<BlockingGate>();
        builder.Services.AddTransient<IJobHandler<PingJob>, THandler>();
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(store)
            .UseRegistry(registry)
            .AddWorkerGroup(new WorkerGroupOptions
            {
                Name = "workers",
                Policy = new DispatchPolicy.Strict(["default"]),
                PollInterval = FastPoll,
                LeaseDuration = TimeSpan.FromSeconds(5),
                RetryPolicy = retryPolicy ?? RetryPolicy.Default,
            }));
        return builder.Build();
    }

    private static async Task WaitForAsync(Func<ValueTask<bool>> condition, string description)
    {
        var deadline = DateTimeOffset.UtcNow + TestTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail($"timed out waiting for {description}");
    }

    [Fact]
    public async Task AHandlerRaisedCancellation_CountsAsAFailure_WithErrorType_AndPersistsItsFailureDetail()
    {
        const string wireName = "handler-cancel-probe";
        using var capture = new MeterCapture(wireName);
        var spans = new ConcurrentBag<Activity>();
        using var listener = ListenToBackWave(spans);

        // A ceiling of one Attempt: the job fails its only Attempt and dead-letters.
        var store = new InMemoryJobStore();
        await using var app = BuildHost<HttpTimeoutHandler>(
            store, wireName, new RetryPolicy { MaxAttempts = 1 });
        await app.StartAsync();

        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await app.Services.GetRequiredService<BackWaveClient>()
            .EnqueueAsync(new PingJob("always-times-out"), dueTime: DateTimeOffset.UtcNow);
        await WaitForAsync(
            async () => (await monitor.GetJobAsync(jobId))?.State == JobState.DeadLettered,
            "the handler-raised cancellation to dead-letter");
        await app.StopAsync();

        var expectedType = typeof(TaskCanceledException).FullName!;

        // The TaskCanceledException an HttpClient timeout raises INSIDE the handler is a plain failure:
        // it counts, it carries error.type, and its latency is sampled. The old record edge saw only
        // "an OperationCanceledException" and dropped all three on the floor.
        var failed = Assert.Single(capture.For("backwave.jobs.failed"));
        Assert.Equal(1, failed.Value);
        Assert.Equal(expectedType, failed.Tags.GetValueOrDefault("error.type"));
        var duration = Assert.Single(capture.For("messaging.process.duration"));
        Assert.Equal(expectedType, duration.Tags.GetValueOrDefault("error.type"));
        Assert.Equal(0, capture.Sum("messaging.client.consumed.messages"));

        // ...so dead_lettered stays the terminal SUBSET of failed. Before the fix this attempt
        // dead-lettered while being invisible to the failed counter, breaking that documented invariant.
        Assert.Equal(1, capture.Sum("backwave.jobs.dead_lettered"));

        // The process span carries the OTel exception convention: an error.type tag plus an "exception"
        // event with the full stack.
        var process = ProcessSpanFor(spans, wireName);
        Assert.Equal(expectedType, process.GetTagItem("error.type"));
        Assert.Equal(ActivityStatusCode.Error, process.Status);
        var exceptionEvent = Assert.Single(process.Events, e => e.Name == "exception");
        Assert.Equal(expectedType, exceptionEvent.Tags.Single(t => t.Key == "exception.type").Value);
        Assert.Contains(
            nameof(HttpTimeoutHandler),
            exceptionEvent.Tags.Single(t => t.Key == "exception.stacktrace").Value?.ToString());

        // Failure Detail (§5.12) is stashed on the OCE path too, so the persisted failure carries a
        // stack trace rather than a blank detail.
        var history = await monitor.GetJobHistoryAsync(jobId);
        var detail = Assert.Single(history, t => t.FailureDetail is not null).FailureDetail;
        Assert.NotNull(detail);
        Assert.Contains(expectedType, detail);
        Assert.Contains(nameof(HttpTimeoutHandler), detail);
    }

    [Fact]
    public async Task AnOperatorCancel_IsNotAFailure_AndSamplesNoDuration()
    {
        const string wireName = "operator-cancel-probe";
        using var capture = new MeterCapture(wireName);
        var spans = new ConcurrentBag<Activity>();
        using var listener = ListenToBackWave(spans);

        var store = new InMemoryJobStore();
        await using var app = BuildHost<BlockingHandler>(store, wireName);
        await app.StartAsync();

        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var gate = app.Services.GetRequiredService<BlockingGate>();
        var jobId = await app.Services.GetRequiredService<BackWaveClient>()
            .EnqueueAsync(new PingJob("blocks-until-cancelled"), dueTime: DateTimeOffset.UtcNow);
        await gate.Started.Task.WaitAsync(TestTimeout);

        await new BackWaveOperator(store).CancelJobAsync(jobId, "operator-cancel");
        await WaitForAsync(
            async () => (await monitor.GetJobAsync(jobId))?.State == JobState.Cancelled,
            "the operator cancel to settle the job Cancelled");
        await app.StopAsync();

        // A cancel the Shell ASKED for is deliberate non-execution: neither consumed nor failed, and no
        // latency sample. Unchanged by the fix - this pins that it stayed that way.
        Assert.Equal(0, capture.Sum("backwave.jobs.failed"));
        Assert.Empty(capture.For("messaging.process.duration"));
        Assert.Equal(0, capture.Sum("messaging.client.consumed.messages"));

        var process = ProcessSpanFor(spans, wireName);
        Assert.Equal(ActivityStatusCode.Error, process.Status);
        Assert.Equal("cancelled", process.StatusDescription);
        Assert.DoesNotContain(process.Events, e => e.Name == "exception");

        // An operator cancel is not a failure, so it stashes no Failure Detail.
        Assert.DoesNotContain(await monitor.GetJobHistoryAsync(jobId), t => t.FailureDetail is not null);
    }

    [Fact]
    public async Task AHostShutdown_IsNotAFailure_AndSamplesNoDuration()
    {
        const string wireName = "shutdown-cancel-probe";
        using var capture = new MeterCapture(wireName);
        var spans = new ConcurrentBag<Activity>();
        using var listener = ListenToBackWave(spans);

        var store = new InMemoryJobStore();
        var app = BuildHost<BlockingHandler>(store, wireName);
        await app.StartAsync();

        var gate = app.Services.GetRequiredService<BlockingGate>();
        await app.Services.GetRequiredService<BackWaveClient>()
            .EnqueueAsync(new PingJob("blocks-until-shutdown"), dueTime: DateTimeOffset.UtcNow);
        await gate.Started.Task.WaitAsync(TestTimeout);

        // Shutdown cancels the in-flight handler: the Lease simply lapses and another node inherits.
        await app.StopAsync();
        await app.DisposeAsync();

        // Shutdown reports nothing and is not an execution: no failure, no latency sample.
        Assert.Equal(0, capture.Sum("backwave.jobs.failed"));
        Assert.Empty(capture.For("messaging.process.duration"));
        Assert.Equal(0, capture.Sum("messaging.client.consumed.messages"));

        // The process span closes as the worker unwinds during shutdown, which can lag DisposeAsync.
        // Wait for it before asserting, or the bag is occasionally empty.
        await WaitForAsync(
            () => new ValueTask<bool>(spans.Any(a =>
                a.OperationName == "process"
                && Equals(a.GetTagItem("messaging.destination.template"), wireName))),
            "the shutdown-cancelled process span to be captured");

        var process = ProcessSpanFor(spans, wireName);
        Assert.Equal(ActivityStatusCode.Error, process.Status);
        Assert.Equal("cancelled", process.StatusDescription);
        Assert.DoesNotContain(process.Events, e => e.Name == "exception");
    }

    // The single process span for THIS test's Wire Name: a concurrently running host in another test
    // class emits its own process spans onto the same process-global ActivitySource, so scope by the
    // destination template (the Wire Name) rather than assume this bag holds only ours.
    private static Activity ProcessSpanFor(IEnumerable<Activity> spans, string wireName) =>
        Assert.Single(spans, a =>
            a.OperationName == "process"
            && Equals(a.GetTagItem("messaging.destination.template"), wireName));

    private static ActivityListener ListenToBackWave(ConcurrentBag<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BackWaveDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>
    /// Captures every BackWave meter measurement for ONE Wire Name, so a host running concurrently in
    /// another test class cannot bleed into the assertions through the process-global meter.
    /// </summary>
    private sealed class MeterCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentBag<(string Name, double Value, Dictionary<string, object?> Tags)> _measurements = [];

        public MeterCapture(string wireName)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BackWaveDiagnostics.SourceName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            {
                var map = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
                if (Equals(map.GetValueOrDefault("messaging.destination.template"), wireName))
                {
                    _measurements.Add((instrument.Name, value, map));
                }
            }
            _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
            _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
            _listener.Start();
        }

        public IEnumerable<(string Name, double Value, Dictionary<string, object?> Tags)> For(string instrument) =>
            _measurements.Where(m => m.Name == instrument);

        public double Sum(string instrument) => For(instrument).Sum(m => m.Value);

        public void Dispose() => _listener.Dispose();
    }
}

/// <summary>Records that it started, then blocks until its Attempt is cancelled.</summary>
public sealed class BlockingHandler(BlockingGate gate) : IJobHandler<PingJob>
{
    public async Task HandleAsync(PingJob job, JobContext context, CancellationToken cancellationToken)
    {
        gate.Started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}

public sealed class BlockingGate
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
