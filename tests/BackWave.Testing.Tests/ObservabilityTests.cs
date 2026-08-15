using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Diagnostics;
using BackWave.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Testing.Tests;

public sealed record TraceProbe(string Name);

public sealed class TraceProbeHandler(TraceLog log) : IJobHandler<TraceProbe>
{
    public Task HandleAsync(TraceProbe job, JobContext context, CancellationToken cancellationToken)
    {
        log.Captured.Add(Activity.Current);
        // "flaky" fails its first attempt then succeeds; "poison" fails every attempt (so it
        // dead-letters once the ceiling is spent). "handler-cancel" raises a cancellation of its OWN -
        // what an HttpClient timeout looks like - which is a plain failure, not a cancel the pump asked
        // for. Every other name succeeds on the first pass.
        if ((job.Name == "flaky" && context.Attempt == 1) || job.Name == "poison")
        {
            throw new InvalidOperationException("first attempt fails");
        }
        return job.Name == "handler-cancel"
            ? throw new TaskCanceledException("the request timed out inside the handler")
            : Task.CompletedTask;
    }
}

public sealed class TraceLog
{
    public List<Activity?> Captured { get; } = [];
}

[JsonSerializable(typeof(TraceProbe))]
internal sealed partial class ObservabilityJsonContext : JsonSerializerContext;

public class ObservabilityTests
{
    private static (BackWaveHarness Harness, TraceLog Log) CreateHarness(RetryPolicy? retryPolicy = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<TraceLog>()
            .AddTransient<IJobHandler<TraceProbe>, TraceProbeHandler>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<TraceProbe, TraceProbeHandler>(
                "trace-probe", ObservabilityJsonContext.Default.TraceProbe),
        ]);
        var options = retryPolicy is null
            ? new BackWaveHarnessOptions()
            : new BackWaveHarnessOptions { RetryPolicy = retryPolicy };
        return (new BackWaveHarness(registry, services, options), services.GetRequiredService<TraceLog>());
    }

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

    [Fact]
    public async Task ProcessSpan_LinksToTheSendSpan_AcrossAVirtualTimeGap()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = ListenToBackWave(stopped);

        var (harness, log) = CreateHarness();

        // The "incoming request" that enqueues; six Virtual Time hours pass before execution.
        using var request = new Activity("incoming-request");
        request.Start();
        await harness.EnqueueAsync(new TraceProbe("hours-later"), delay: TimeSpan.FromHours(6));
        request.Stop();

        await harness.AdvanceAsync(TimeSpan.FromHours(6));

        // The handler ran under a CONSUMER "process" span - the messaging-convention execution span.
        var processSpan = Assert.Single(log.Captured);
        Assert.NotNull(processSpan);
        Assert.Equal("process", processSpan.OperationName);
        Assert.Equal(ActivityKind.Consumer, processSpan.Kind);
        Assert.Equal("backwave", processSpan.GetTagItem("messaging.system"));
        Assert.Equal("process", processSpan.GetTagItem("messaging.operation.type"));

        // The enqueue emitted a PRODUCER "send" span, itself a child of the request.
        var sendSpan = stopped.Single(a =>
            a.OperationName == "send" && a.TraceId == request.TraceId);
        Assert.Equal(ActivityKind.Producer, sendSpan.Kind);
        Assert.Equal(request.SpanId, sendSpan.ParentSpanId);

        // The messaging model correlates a consumer to its producer by LINK, not by re-parenting: the
        // process span is a root in its OWN trace, carrying a link back to the send context.
        Assert.NotEqual(request.TraceId, processSpan.TraceId);
        Assert.Equal(default, processSpan.ParentSpanId);
        Assert.Contains(processSpan.Links, l =>
            l.Context.TraceId == sendSpan.TraceId && l.Context.SpanId == sendSpan.SpanId);

        // The claim that started the Attempt produced its own CLIENT "receive" span.
        Assert.Contains(stopped, a => a.OperationName == "receive" && a.Kind == ActivityKind.Client);
    }

    [Fact]
    public async Task ProcessSpan_WithoutATraceContext_IsARootWithNoLinks()
    {
        var stopped = new ConcurrentBag<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BackWaveDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };

        var (harness, log) = CreateHarness();
        // Enqueue before the listener exists: no send span, so no stored TraceContext to link to.
        await harness.EnqueueAsync(new TraceProbe("uncorrelated"));

        ActivitySource.AddActivityListener(listener);
        using (listener)
        {
            await harness.AdvanceAsync(TimeSpan.Zero);
        }

        var processSpan = Assert.Single(log.Captured);
        Assert.NotNull(processSpan);
        Assert.Equal("process", processSpan.OperationName);
        Assert.Equal(default, processSpan.ParentSpanId);
        Assert.Empty(processSpan.Links);
    }

    [Fact]
    public async Task ProcessSpan_RecordsException_AndRetryScheduledEvent_OnAFailedThenRetriedAttempt()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = ListenToBackWave(stopped);

        var (harness, log) = CreateHarness();
        await harness.EnqueueAsync(new TraceProbe("flaky")); // fails Attempt 1, retries, succeeds Attempt 2
        await harness.AdvanceAsync(TimeSpan.FromHours(1));   // past the retry backoff

        // Two executions: the failed first Attempt and the successful retry.
        Assert.Equal(2, log.Captured.Count);
        var firstAttempt = Assert.Single(log.Captured, a => Equals(a!.GetTagItem("backwave.attempt"), 1));
        Assert.NotNull(firstAttempt);

        // The failing exception is recorded per the OTel convention: an error.type tag plus an
        // "exception" event carrying the type.
        Assert.Equal(typeof(InvalidOperationException).FullName, firstAttempt.GetTagItem("error.type"));
        Assert.Equal(ActivityStatusCode.Error, firstAttempt.Status);
        var exceptionEvent = Assert.Single(firstAttempt.Events, e => e.Name == "exception");
        Assert.Equal(
            typeof(InvalidOperationException).FullName,
            exceptionEvent.Tags.Single(t => t.Key == "exception.type").Value);

        // A retry is due (the ceiling is not spent), so the settled span carries retry-scheduled - and
        // NOT dead-lettered.
        Assert.Contains(firstAttempt.Events, e => e.Name == "retry-scheduled");
        Assert.DoesNotContain(firstAttempt.Events, e => e.Name == "dead-lettered");

        // The successful retry records neither an exception nor a settlement event.
        var secondAttempt = Assert.Single(log.Captured, a => Equals(a!.GetTagItem("backwave.attempt"), 2));
        Assert.NotNull(secondAttempt);
        Assert.Equal(ActivityStatusCode.Ok, secondAttempt.Status);
        Assert.DoesNotContain(secondAttempt.Events, e => e.Name is "retry-scheduled" or "dead-lettered");
    }

    [Fact]
    public async Task ProcessSpan_RecordsDeadLetteredEvent_WhenTheAttemptCeilingIsSpent()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = ListenToBackWave(stopped);

        // A ceiling of one Attempt: a job that fails its only Attempt dead-letters with no retry.
        var (harness, log) = CreateHarness(new RetryPolicy { MaxAttempts = 1 });
        await harness.EnqueueAsync(new TraceProbe("poison"));
        await harness.AdvanceAsync(TimeSpan.Zero);

        var processSpan = Assert.Single(log.Captured);
        Assert.NotNull(processSpan);
        Assert.Equal(typeof(InvalidOperationException).FullName, processSpan.GetTagItem("error.type"));
        Assert.Contains(processSpan.Events, e => e.Name == "exception");
        // The ceiling is spent, so the settled span carries dead-lettered, not retry-scheduled.
        Assert.Contains(processSpan.Events, e => e.Name == "dead-lettered");
        Assert.DoesNotContain(processSpan.Events, e => e.Name == "retry-scheduled");
    }

    [Fact]
    public async Task Meters_EmitQueueDepth_Throughput_Failures_AndAttempts()
    {
        var measurements = new ConcurrentBag<(string Instrument, long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BackWaveDiagnostics.SourceName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();

        var (harness, log) = CreateHarness();
        using var depthSource = BackWaveDiagnostics.RegisterQueueDepthSource(
            () => harness.Store.CountJobsAsync().AsTask().Result); // In-Memory completes synchronously

        await harness.EnqueueAsync(new TraceProbe("ok"));
        await harness.EnqueueAsync(new TraceProbe("flaky"));
        await harness.AdvanceAsync(TimeSpan.FromHours(1)); // past the retry backoff

        // Counter tags isolate this test's jobs from concurrently running harnesses. The Wire Name rides
        // on the messaging convention's destination-template attribute now.
        long Sum(string instrument) => measurements
            .Where(m => m.Instrument == instrument
                && Equals(m.Tags.GetValueOrDefault("messaging.destination.template"), "trace-probe"))
            .Sum(m => m.Value);

        Assert.Equal(2, Sum("messaging.client.sent.messages"));
        Assert.Equal(3, Sum("backwave.job.attempts")); // each claim starts an Attempt; flaky claimed twice
        Assert.Equal(2, Sum("messaging.client.consumed.messages"));
        Assert.Equal(1, Sum("backwave.jobs.failed"));

        listener.RecordObservableInstruments();
        var depth = Assert.Single(measurements, m => m.Instrument == "backwave.queue.depth");
        Assert.Equal(2, depth.Value);
        Assert.Equal("default", depth.Tags["backwave.queue"]);
        Assert.Equal("Succeeded", depth.Tags["backwave.state"]);
    }

    [Fact]
    public async Task Meters_EmitProcessDuration_Histogram_InSeconds_OnSuccessAndFailure_TaggedWithDestination()
    {
        var durations = new ConcurrentBag<(double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BackWaveDiagnostics.SourceName
                    && instrument.Name == "messaging.process.duration")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            durations.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();

        var (harness, _) = CreateHarness();
        await harness.EnqueueAsync(new TraceProbe("ok"));
        await harness.EnqueueAsync(new TraceProbe("flaky")); // fails Attempt 1, succeeds Attempt 2
        await harness.AdvanceAsync(TimeSpan.FromHours(1)); // past the retry backoff

        // Isolate this test's jobs from any concurrently running harness, keyed on the destination template.
        var mine = durations
            .Where(d => Equals(d.Tags.GetValueOrDefault("messaging.destination.template"), "trace-probe"))
            .ToList();

        // One measurement per real execution - the same outcomes the consumed/failed counters count:
        // ok succeeds (1), flaky fails then succeeds (2). Cancellation would contribute none.
        Assert.Equal(3, mine.Count);
        Assert.All(mine, d =>
        {
            Assert.Equal("trace-probe", d.Tags["messaging.destination.template"]);
            Assert.Equal("default", d.Tags["messaging.destination.name"]);
            // The histogram measures Virtual Time in SECONDS: the harness runs each handler inline at a
            // single virtual instant, so a handler that consumes no Virtual Time records a deterministic
            // zero. A wall-clock reading (the pre-fix TimeProvider.System fallback) would report
            // nondeterministic sub-millisecond noise here instead - this pins the injected clock.
            Assert.Equal(0d, d.Value);
        });
    }

    [Fact]
    public async Task ScheduleDelay_IsMeasuredFromTheScheduledTime_NotEnqueue_InVirtualTime()
    {
        using var capture = new MeterCapture();
        var (harness, _) = CreateHarness();

        // Enqueue six Virtual Time hours out, then advance exactly to its due instant and run it.
        await harness.EnqueueAsync(new TraceProbe("hours-later"), delay: TimeSpan.FromHours(6));
        await harness.AdvanceAsync(TimeSpan.FromHours(6));

        var delay = Assert.Single(capture.ForProbe("backwave.schedule.delay"));
        // Drift is measured from the DueTime (the scheduled instant), which the clock reaches exactly, so
        // the drift is a deterministic zero. Measured from enqueue it would be 21600s; a wall-clock read
        // would report sub-millisecond noise. This pins "from ScheduledAt, not enqueue" and "virtual time".
        Assert.Equal(0d, delay.Value);
        Assert.Equal("default", delay.Tags["messaging.destination.name"]);
    }

    [Fact]
    public async Task QueueWait_IsRecordedAtClaim_InVirtualTime_TaggedWithDestination()
    {
        using var capture = new MeterCapture();
        var (harness, _) = CreateHarness();

        await harness.EnqueueAsync(new TraceProbe("due-now"));
        await harness.AdvanceAsync(TimeSpan.Zero);

        var wait = Assert.Single(capture.ForProbe("backwave.job.queue.wait"));
        // Claimed at its due instant under Virtual Time: a deterministic zero wait, in seconds.
        Assert.Equal(0d, wait.Value);
        Assert.Equal("trace-probe", wait.Tags["messaging.destination.template"]);
        Assert.Equal("default", wait.Tags["messaging.destination.name"]);
    }

    [Fact]
    public async Task ScheduleDelayAndQueueWait_MeasureOverdueDrift_InSeconds_NotMilliseconds()
    {
        using var capture = new MeterCapture();
        var (harness, _) = CreateHarness();

        // Enqueue a job already five Virtual Time SECONDS overdue (a due instant in the past), then drain
        // at the current instant. Every other case here runs a job exactly at its due instant, where the
        // drift is a structural zero - and 0 ms == 0 s, so a seconds-vs-milliseconds unit slip would pass
        // unnoticed. A known non-zero drift is what pins the unit: five seconds must read 5.0, never 5000.
        await harness.EnqueueAsync(new TraceProbe("overdue"), delay: TimeSpan.FromSeconds(-5));
        await harness.AdvanceAsync(TimeSpan.Zero);

        // Both histograms are in SECONDS. queue.wait is the due-to-claim latency (claim instant minus the
        // scheduled/due instant); schedule.delay is the scheduled-to-execution drift. Claimed and executed
        // at the same virtual instant, they coincide at exactly 5.0 - and the positive drift is not clamped.
        var wait = Assert.Single(capture.ForProbe("backwave.job.queue.wait"));
        Assert.Equal(5d, wait.Value);
        var delay = Assert.Single(capture.ForProbe("backwave.schedule.delay"));
        Assert.Equal(5d, delay.Value);
    }

    [Fact]
    public async Task WorkerSlotsActive_TracksInFlight_AndReturnsToZeroAtDrain()
    {
        using var capture = new MeterCapture();
        var (harness, _) = CreateHarness();

        await harness.EnqueueAsync(new TraceProbe("slot-a"));
        await harness.EnqueueAsync(new TraceProbe("slot-b"));
        await harness.AdvanceAsync(TimeSpan.Zero);

        // Two executions each occupy a slot (+1) then release it (-1): four deltas, keyed on this test's
        // destination template to isolate from any concurrently running harness.
        var slots = capture.ForProbe("backwave.worker.slots.active").ToList();
        Assert.Equal(2, slots.Count(m => m.Value == 1d));
        Assert.Equal(2, slots.Count(m => m.Value == -1d));
        // The pool drained, so the net in-flight count is back to zero.
        Assert.Equal(0d, slots.Sum(m => m.Value));
    }

    [Fact]
    public async Task DeadLettered_CountsTerminalFailures_DistinctFromTheRetryableFailedCounter()
    {
        using var capture = new MeterCapture();

        // A ceiling of one Attempt: a job that fails its only Attempt dead-letters with no retry.
        var (harness, _) = CreateHarness(new RetryPolicy { MaxAttempts = 1 });
        await harness.EnqueueAsync(new TraceProbe("poison"));
        await harness.AdvanceAsync(TimeSpan.Zero);

        // The single terminal failure counts once on dead_lettered AND once on the (retryable-or-not)
        // failed counter - dead_lettered is the terminal subset.
        Assert.Equal(1, capture.SumProbe("backwave.jobs.dead_lettered"));
        Assert.Equal(1, capture.SumProbe("backwave.jobs.failed"));
        var deadLetter = Assert.Single(capture.ForProbe("backwave.jobs.dead_lettered"));
        Assert.Equal("default", deadLetter.Tags["messaging.destination.name"]);
    }

    [Fact]
    public async Task Failures_CarryErrorType_OnTheFailedCounter_AndTheProcessDuration()
    {
        using var capture = new MeterCapture();

        var (harness, _) = CreateHarness();
        await harness.EnqueueAsync(new TraceProbe("flaky")); // throws InvalidOperationException on Attempt 1
        await harness.AdvanceAsync(TimeSpan.FromHours(1));   // past the retry backoff, then succeeds

        var expectedType = typeof(InvalidOperationException).FullName;

        // The failed counter breaks the failure down by exception type.
        var failed = Assert.Single(capture.ForProbe("backwave.jobs.failed"));
        Assert.Equal(expectedType, failed.Tags.GetValueOrDefault("error.type"));

        // The process-duration histogram carries error.type on the failed execution and NOT on the two
        // successes (the ok-path attempt records no error.type tag).
        var durations = capture.ForProbe("messaging.process.duration").ToList();
        Assert.Single(durations, d => Equals(d.Tags.GetValueOrDefault("error.type"), expectedType));
        Assert.Contains(durations, d => !d.Tags.ContainsKey("error.type"));
    }

    [Fact]
    public async Task AHandlerRaisedCancellation_IsAPlainFailure_NotACancel()
    {
        using var capture = new MeterCapture();
        var stopped = new ConcurrentBag<Activity>();
        using var listener = ListenToBackWave(stopped);

        // A ceiling of one Attempt: the job fails its only Attempt and dead-letters.
        var (harness, log) = CreateHarness(new RetryPolicy { MaxAttempts = 1 });
        var jobId = await harness.EnqueueAsync(new TraceProbe("handler-cancel"));
        await harness.AdvanceAsync(TimeSpan.Zero);

        var expectedType = typeof(TaskCanceledException).FullName!;

        // The pump did not ask for this cancel - the handler raised it - so the verdict is Failure: it
        // counts, it carries error.type, and its latency is sampled, exactly like any other throw.
        var failed = Assert.Single(capture.ForProbe("backwave.jobs.failed"));
        Assert.Equal(expectedType, failed.Tags.GetValueOrDefault("error.type"));
        var duration = Assert.Single(capture.ForProbe("messaging.process.duration"));
        Assert.Equal(expectedType, duration.Tags.GetValueOrDefault("error.type"));
        Assert.Equal(0, capture.SumProbe("messaging.client.consumed.messages"));
        // dead_lettered stays the terminal SUBSET of failed: both count exactly once.
        Assert.Equal(1, capture.SumProbe("backwave.jobs.dead_lettered"));

        var processSpan = Assert.Single(log.Captured);
        Assert.NotNull(processSpan);
        Assert.Equal(expectedType, processSpan.GetTagItem("error.type"));
        Assert.Equal(ActivityStatusCode.Error, processSpan.Status);
        Assert.Contains(processSpan.Events, e => e.Name == "exception");

        // The failing Attempt persists its Failure Detail with a stack, like any other failure.
        var detail = Assert.Single(
            await harness.Monitor.GetJobHistoryAsync(jobId), t => t.FailureDetail is not null).FailureDetail;
        Assert.NotNull(detail);
        Assert.Contains(expectedType, detail);
        Assert.Contains(nameof(TraceProbeHandler), detail);
    }

    /// <summary>Captures every BackWave meter measurement (long and double) with its tags, for assertions.</summary>
    private sealed class MeterCapture : IDisposable
    {
        private readonly MeterListener _listener = new();

        public ConcurrentBag<(string Name, double Value, Dictionary<string, object?> Tags)> Measurements { get; } = [];

        public MeterCapture()
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BackWaveDiagnostics.SourceName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                Measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
            _listener.Start();
        }

        // Only this test's jobs, keyed on the destination template, to isolate from any concurrently
        // running harness sharing the static BackWave meter.
        public IEnumerable<(string Name, double Value, Dictionary<string, object?> Tags)> ForProbe(string instrument) =>
            Measurements.Where(m => m.Name == instrument
                && Equals(m.Tags.GetValueOrDefault("messaging.destination.template"), "trace-probe"));

        public double SumProbe(string instrument) => ForProbe(instrument).Sum(m => m.Value);

        public void Dispose() => _listener.Dispose();
    }
}
