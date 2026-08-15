using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using BackWave.Core;
using BackWave.Diagnostics;
using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Monitor;
using BackWave.Observers;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackWave.Hosting.Tests;

/// <summary>
/// Hosting integration tests for the production Observer pump (issue 0100, ADR 0020). Determinism
/// lives in the Simulator, not in Hosting — so these drive a real host with short intervals and
/// poll-until-condition assertions (the same shape as <see cref="HostingShellTests"/>).
/// </summary>
public class ObserverDispatchServiceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(25);

    /// <summary>The transition facts (and proof of the scoped resolve + payload read) one delivery saw.</summary>
    public sealed record DeliveredRecord(
        Guid JobId, string WireName, string Queue, JobState State, int Attempt, bool PayloadAvailable, int PayloadLength);

    public sealed class DeliverySink
    {
        public ConcurrentBag<DeliveredRecord> Records { get; } = [];
    }

    /// <summary>A scoped dependency (stand-in for a DbContext) — its successful resolve inside the callback
    /// proves each delivery runs in its own DI scope (ADR 0020).</summary>
    public sealed class ScopedMarker;

    public sealed class RecordingObserver(DeliverySink sink, ScopedMarker marker) : ITransitionObserver
    {
        public async ValueTask OnTransitionAsync(ObserverContext context, CancellationToken cancellationToken)
        {
            _ = marker; // resolved from the per-delivery scope; touching it proves the scope is real.
            var payload = await context.Payload.GetAsync(cancellationToken);
            sink.Records.Add(new DeliveredRecord(
                context.JobId, context.WireName, context.Queue, context.State, context.Attempt,
                payload.Available, payload.Bytes.Length));
        }
    }

    private static JobRegistry Registry() => new(
    [
        JobRegistration.Create<PingJob, PingHandler>("ping", HostingJsonContext.Default.PingJob, "default"),
    ]);

    private static WebApplication BuildHost(
        IJobStore store, DeliverySink sink, ObserverSubscription subscription,
        JobHistoryPolicy historyPolicy = JobHistoryPolicy.TransitionsAndFailureDetail)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<PingRecorder>();
        builder.Services.AddTransient<IJobHandler<PingJob>, PingHandler>();
        builder.Services.AddSingleton(sink);
        builder.Services.AddScoped<ScopedMarker>();
        builder.Services.AddBackWave(backwave =>
        {
            backwave.UseStore(store).UseRegistry(Registry()).UseHistoryPolicy(historyPolicy);
            backwave.AddWorkerGroup(new WorkerGroupOptions
            {
                Name = "workers",
                Policy = new DispatchPolicy.Strict(["default"]),
                PollInterval = FastPoll,
                LeaseDuration = TimeSpan.FromSeconds(5),
            });
            backwave.AddObservers(obs =>
            {
                obs.ConfigurePump(o => o.PollInterval = FastPoll);
                obs.Add<RecordingObserver>("test-obs", subscription);
            });
        });
        return builder.Build();
    }

    private static async Task WaitForAsync(Func<bool> condition, string description)
    {
        var deadline = DateTimeOffset.UtcNow + TestTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.Fail($"Timed out waiting for: {description}");
    }

    [Fact]
    public async Task RegisteredObserver_ReceivesTransition_FromScopedDelivery_CursorAdvances_CountersMove()
    {
        var store = new InMemoryJobStore();
        var sink = new DeliverySink();
        using var counters = new ObserverCounterCapture("test-obs");
        await using var app = BuildHost(store, sink, new ObserverSubscription([JobState.Succeeded]) { WireName = "ping" });
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new PingJob("observed"), dueTime: DateTimeOffset.UtcNow);

        await WaitForAsync(() => sink.Records.Any(r => r.JobId == jobId), "the transition to be delivered to the observer");

        var delivered = Assert.Single(sink.Records, r => r.JobId == jobId);
        Assert.Equal("ping", delivered.WireName);
        Assert.Equal("default", delivered.Queue);
        Assert.Equal(JobState.Succeeded, delivered.State);
        Assert.True(delivered.PayloadAvailable, "the lazy payload body read should succeed in a real host");
        Assert.True(delivered.PayloadLength > 0, "the payload body should be readable end-to-end");

        // The per-Observer cursor advanced off its never-claimed sentinel (-1).
        await WaitForAsync(
            () => monitor.GetObserverCursorAsync("test-obs").AsTask().GetAwaiter().GetResult() >= 0,
            "the observer cursor to advance");
        Assert.True(await monitor.GetObserverCursorAsync("test-obs") >= 0);

        // OTel counters moved at the delivery edge, observer-id attributed.
        Assert.True(counters.Attempted >= 1, "attempted counter should move");
        Assert.True(counters.Succeeded >= 1, "succeeded counter should move");

        await app.StopAsync();
    }

    [Fact]
    public async Task ObserverDispatch_RecordsDispatchDuration_AroundEachDelivery()
    {
        var store = new InMemoryJobStore();
        var sink = new DeliverySink();
        using var counters = new ObserverCounterCapture("test-obs");
        await using var app = BuildHost(store, sink, new ObserverSubscription([JobState.Succeeded]) { WireName = "ping" });
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var jobId = await client.EnqueueAsync(new PingJob("observed"), dueTime: DateTimeOffset.UtcNow);

        await WaitForAsync(() => sink.Records.Any(r => r.JobId == jobId), "the transition to be delivered to the observer");
        // The dispatch-latency histogram records around the invocation, attributed to the Observer.
        await WaitForAsync(() => counters.DispatchMeasured >= 1, "the observer.dispatch.duration histogram to record");
        Assert.True(counters.DispatchMeasured >= 1, "dispatch duration should be measured around each delivery");

        await app.StopAsync();
    }

    [Fact]
    public async Task AddObservers_RegistersSingleHostedPump_AndCanonicalRegistrationList()
    {
        var store = new InMemoryJobStore();
        await using var app = BuildHost(store, new DeliverySink(), new ObserverSubscription([JobState.Succeeded]));
        await app.StartAsync();

        // One pump per process (not one per Observer, not one per group).
        Assert.Single(app.Services.GetServices<IHostedService>().OfType<ObserverDispatchService>());

        // The canonical list is in DI, one entry per Add — the same list the dashboard reads.
        var registrations = app.Services.GetRequiredService<IReadOnlyList<ObserverRegistration>>();
        var registration = Assert.Single(registrations);
        Assert.Equal("test-obs", registration.Id);

        await app.StopAsync();
    }

    [Fact]
    public void RegisteringObserver_WhileHistoryPolicyOff_ThrowsAtComposition()
    {
        // The EnsureDeliverableUnder guard runs in Apply() — at container composition, not first tick.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            BuildHost(
                new InMemoryJobStore(historyPolicy: JobHistoryPolicy.Off),
                new DeliverySink(),
                new ObserverSubscription([JobState.Succeeded]),
                historyPolicy: JobHistoryPolicy.Off));
        Assert.Contains("test-obs", exception.Message);
        Assert.Contains("Job History Policy", exception.Message);
    }

    [Fact]
    public async Task PumpLoopFault_IsLoggedAndStopsPump_WithoutFaultingTheHost()
    {
        // Force the dead-ticker loud-failure path (ADR 0020): a zero PollInterval makes the pump's
        // PeriodicTimer ctor throw, the ticker completes the channel with that exception, and
        // ReadAllAsync rethrows it INTO the pump loop — a non-cancellation throw. Before the fail-soft
        // catch-all this faulted ExecuteAsync and, under the default BackgroundService behavior, stopped
        // the whole host. Drive a real instance via its internal ctor (no host builder) so the seam is
        // deterministic.
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        await using var provider = new ServiceCollection().BuildServiceProvider();

        var binding = new ObserverBinding(
            new ObserverRegistration("test-obs", new ObserverSubscription([JobState.Succeeded])),
            typeof(RecordingObserver));
        var service = new ObserverDispatchService(
            new ObserverPumpOptions { PollInterval = TimeSpan.Zero },
            [binding],
            new InMemoryJobStore(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            loggerFactory.CreateLogger<ObserverDispatchService>());

        // StartAsync kicks off ExecuteAsync; the pump faults on the dead ticker almost immediately.
        await service.StartAsync(CancellationToken.None);

        // The pump task completes NORMALLY — it is not faulted out of the BackgroundService — proving a
        // non-cancellation fault in the pump loop no longer takes the host down.
        await service.ExecuteTask!.WaitAsync(TestTimeout);
        Assert.True(
            service.ExecuteTask!.IsCompletedSuccessfully,
            "the observer pump must complete normally on a non-cancellation fault, never fault the host");

        await service.StopAsync(CancellationToken.None);

        // The dead-ticker loud-failure path is preserved: the fault is still loud, via a carried Error log.
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Exception is not null);
    }

    /// <summary>Captures the observer-delivery counters for one Observer id off the static BackWave Meter.</summary>
    private sealed class ObserverCounterCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly string _observerId;
        private long _attempted;
        private long _succeeded;
        private long _deadLettered;
        private long _dispatchMeasured;

        public long Attempted => Interlocked.Read(ref _attempted);
        public long Succeeded => Interlocked.Read(ref _succeeded);
        public long DeadLettered => Interlocked.Read(ref _deadLettered);

        /// <summary>How many backwave.observer.dispatch.duration measurements this Observer recorded.</summary>
        public long DispatchMeasured => Interlocked.Read(ref _dispatchMeasured);

        public ObserverCounterCapture(string observerId)
        {
            _observerId = observerId;
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BackWaveDiagnostics.SourceName
                    && (instrument.Name.StartsWith("backwave.observer.deliveries.", StringComparison.Ordinal)
                        || instrument.Name == "backwave.observer.dispatch.duration"))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                if (!IsMine(tags))
                {
                    return;
                }
                switch (instrument.Name)
                {
                    case "backwave.observer.deliveries.attempted": Interlocked.Add(ref _attempted, measurement); break;
                    case "backwave.observer.deliveries.succeeded": Interlocked.Add(ref _succeeded, measurement); break;
                    case "backwave.observer.deliveries.dead_lettered": Interlocked.Add(ref _deadLettered, measurement); break;
                }
            });
            _listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            {
                if (IsMine(tags) && instrument.Name == "backwave.observer.dispatch.duration")
                {
                    Interlocked.Increment(ref _dispatchMeasured);
                }
            });
            _listener.Start();
        }

        private bool IsMine(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "backwave.observer_id" && !Equals(tag.Value, _observerId))
                {
                    return false;
                }
            }
            return true;
        }

        public void Dispose() => _listener.Dispose();
    }
}
