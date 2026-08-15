using System.Collections.Concurrent;
using BackWave.Core;
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
/// Hosting integration tests for the production Observer isolation hardening (issue 0101, ADR 0020):
/// off-loop dispatch (real per-Observer concurrency) and hung-callback containment (proceed-not-await
/// on <c>DeliveryTimeout</c>). Determinism lives in the Simulator, not in Hosting — so these drive a
/// real host with short intervals and poll-until-condition assertions, the same shape as
/// <see cref="ObserverDispatchServiceTests"/>.
/// </summary>
public class ObserverDispatchIsolationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(25);

    /// <summary>One spawnable Observer registration: id, subscription, and the concrete observer instance.</summary>
    private sealed record ObserverSpec(string Id, ObserverSubscription Subscription, ITransitionObserver Instance);

    private static JobRegistry Registry() => new(
    [
        JobRegistration.Create<PingJob, PingHandler>("ping", HostingJsonContext.Default.PingJob, "default"),
    ]);

    /// <summary>
    /// Build a real host wiring each <paramref name="observers"/> as a singleton instance (so the test
    /// holds a handle to its gate/counter), plus the pump configuration in <paramref name="configurePump"/>.
    /// </summary>
    private static WebApplication BuildHost(
        IJobStore store, Action<ObserverPumpOptions> configurePump, params ObserverSpec[] observers)
        => BuildHost(store, configurePump, (ILoggerProvider?)null, observers);

    private static WebApplication BuildHost(
        IJobStore store, Action<ObserverPumpOptions> configurePump,
        ILoggerProvider? loggerProvider, params ObserverSpec[] observers)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (loggerProvider is not null)
        {
            builder.Logging.AddProvider(loggerProvider);
        }
        builder.Services.AddSingleton<PingRecorder>();
        builder.Services.AddTransient<IJobHandler<PingJob>, PingHandler>();
        builder.Services.AddBackWave(backwave =>
        {
            backwave.UseStore(store).UseRegistry(Registry()).UseHistoryPolicy(JobHistoryPolicy.TransitionsAndFailureDetail);
            backwave.AddWorkerGroup(new WorkerGroupOptions
            {
                Name = "workers",
                Policy = new DispatchPolicy.Strict(["default"]),
                PollInterval = FastPoll,
                LeaseDuration = TimeSpan.FromSeconds(5),
            });
            backwave.AddObservers(obs =>
            {
                obs.ConfigurePump(o => { o.PollInterval = FastPoll; configurePump(o); });
                foreach (var spec in observers)
                {
                    AddTyped(obs, spec);
                }
            });
        });
        // Override the builder's default-constructed scoped registration with the exact instance the
        // test holds (last registration wins), keyed by the observer's CLR type so the per-delivery
        // scope resolves it back. Distinct concrete types per spec keep the keys disjoint.
        foreach (var spec in observers)
        {
            builder.Services.AddScoped(spec.Instance.GetType(), _ => spec.Instance);
        }
        return builder.Build();
    }

    /// <summary>Reflective <c>Add&lt;TObserver&gt;</c> so a test can register a concrete observer type by instance.</summary>
    private static void AddTyped(ObserverBuilder obs, ObserverSpec spec)
    {
        typeof(ObserverBuilder)
            .GetMethod(nameof(ObserverBuilder.Add))!
            .MakeGenericMethod(spec.Instance.GetType())
            .Invoke(obs, [spec.Id, spec.Subscription]);
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

    // ---- Observers -----------------------------------------------------------------------------

    /// <summary>A healthy observer: records the moment it delivered and returns immediately.</summary>
    private sealed class FastObserver : ITransitionObserver
    {
        public ConcurrentBag<DateTimeOffset> DeliveredAt { get; } = [];
        public ValueTask OnTransitionAsync(ObserverContext context, CancellationToken cancellationToken)
        {
            DeliveredAt.Add(DateTimeOffset.UtcNow);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A slow-but-completing observer: signals it has entered the callback, then blocks on a gate the
    /// test releases. Cooperative — it completes successfully once released, never times out.
    /// </summary>
    private sealed class GatedObserver : ITransitionObserver
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Completed;
        public async ValueTask OnTransitionAsync(ObserverContext context, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task;
            Interlocked.Increment(ref Completed);
        }
    }

    /// <summary>
    /// A fully-hung, token-IGNORING observer: it blocks forever on a gate that the test never releases
    /// (and never observes its CancellationToken), modelling a non-cooperative callback. After
    /// <c>DeliveryTimeout</c> the Shell must proceed without awaiting it.
    /// </summary>
    private sealed class HungObserver : ITransitionObserver
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NeverReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask OnTransitionAsync(ObserverContext context, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await NeverReleased.Task; // ignores the token on purpose.
        }
    }

    /// <summary>
    /// A token-ignoring observer that, after timing out, eventually THROWS — used to prove the orphaned
    /// task's exception is observed (no <see cref="System.Threading.Tasks.TaskScheduler.UnobservedTaskException"/>).
    /// </summary>
    private sealed class HungThenThrowObserver : ITransitionObserver
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Throw { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask OnTransitionAsync(ObserverContext context, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Throw.Task; // released by the test AFTER the timeout, then throws.
            throw new InvalidOperationException("token-ignoring callback threw after it was abandoned");
        }
    }

    // ---- Tests ---------------------------------------------------------------------------------

    [Fact]
    public async Task SlowObserver_DoesNotBlock_HealthyObserver_DeliveringConcurrently()
    {
        var store = new InMemoryJobStore();
        var gated = new GatedObserver();
        var fast = new FastObserver();
        await using var app = BuildHost(
            store,
            _ => { },
            new ObserverSpec("gated", new ObserverSubscription([JobState.Succeeded]) { WireName = "ping" }, gated),
            new ObserverSpec("fast", new ObserverSubscription([JobState.Succeeded]) { WireName = "ping" }, fast));
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        await client.EnqueueAsync(new PingJob("observed"), dueTime: DateTimeOffset.UtcNow);

        // The slow Observer is now inside its callback, blocked on the gate.
        await gated.Entered.Task.WaitAsync(TestTimeout);

        // The healthy Observer must deliver WHILE the slow one is still blocked — proof the loop is not
        // serialized behind the gated callback (off-loop dispatch, concurrency across Observers).
        await WaitForAsync(() => !fast.DeliveredAt.IsEmpty, "the healthy observer to deliver while the slow one is gated");
        Assert.Equal(0, Volatile.Read(ref gated.Completed)); // the slow observer is still blocked in its callback.

        // Releasing the gate lets the slow Observer complete too.
        gated.Release.TrySetResult();
        await WaitForAsync(() => Volatile.Read(ref gated.Completed) >= 1, "the slow observer to complete after release");

        await app.StopAsync();
    }

    [Fact]
    public async Task HungObserver_TimesOut_RecordsFailed_Proceeds_DeadLetters_CursorAdvances()
    {
        var store = new InMemoryJobStore();
        var hung = new HungObserver();
        var logs = new CapturingLoggerProvider();
        await using var app = BuildHost(
            store,
            o =>
            {
                o.DeliveryTimeout = TimeSpan.FromMilliseconds(150);
                // Low ceiling + instant backoff so the retries exhaust fast and it dead-letters quickly.
                o.DeliveryRetryPolicy = new RetryPolicy { MaxAttempts = 2, Backoff = _ => TimeSpan.Zero };
            },
            logs,
            new ObserverSpec("hung", new ObserverSubscription([JobState.Succeeded]) { WireName = "ping" }, hung));
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        await client.EnqueueAsync(new PingJob("observed"), dueTime: DateTimeOffset.UtcNow);

        // The hung callback entered (proof a delivery was attempted) and will never return.
        await hung.Entered.Task.WaitAsync(TestTimeout);

        // After the retry ceiling exhausts, the delivery dead-letters and the cursor advances off -1 —
        // the pump kept making progress without ever awaiting the still-running callback.
        await WaitForAsync(
            () => monitor.ListObserverDeadLettersAsync("hung").AsTask().GetAwaiter().GetResult().Count > 0,
            "the hung observer's delivery to dead-letter");
        await WaitForAsync(
            () => monitor.GetObserverCursorAsync("hung").AsTask().GetAwaiter().GetResult() >= 0,
            "the hung observer's cursor to advance past the dead-lettered delivery");

        var deadLetters = await monitor.ListObserverDeadLettersAsync("hung");
        Assert.Single(deadLetters);
        Assert.True(await monitor.GetObserverCursorAsync("hung") >= 0);

        // The dead-letter is reported at the catalog's ObserverDeliveryDeadLettered (1401, Warning),
        // naming the observer whose ceiling was spent.
        var deadLetterLog = Assert.Single(logs.Entries, e => e.EventId == 1401);
        Assert.Equal(LogLevel.Warning, deadLetterLog.Level);
        Assert.Contains("hung", deadLetterLog.Message);

        // The callback is STILL running (never released) — proof the loop proceeded, did not await it.
        Assert.False(hung.NeverReleased.Task.IsCompleted, "the hung callback was never released");

        await app.StopAsync();
    }

    [Fact]
    public async Task HungObserver_DoesNotStarve_OtherObserver_NoCascadeWedge()
    {
        var store = new InMemoryJobStore();
        var hung = new HungObserver();
        var fast = new FastObserver();
        await using var app = BuildHost(
            store,
            o => o.DeliveryTimeout = TimeSpan.FromSeconds(10), // long timeout: the hang would wedge a serial loop
            new ObserverSpec("hung", new ObserverSubscription([JobState.Succeeded]) { WireName = "ping" }, hung),
            new ObserverSpec("fast", new ObserverSubscription([JobState.Succeeded]) { WireName = "ping" }, fast));
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        await client.EnqueueAsync(new PingJob("observed"), dueTime: DateTimeOffset.UtcNow);

        await hung.Entered.Task.WaitAsync(TestTimeout);

        // Even with the hung Observer pinned inside a 10s timeout, the healthy Observer delivers — the
        // hang does not starve the other Observer's claim/delivery on the same node (off-loop dispatch).
        await WaitForAsync(() => !fast.DeliveredAt.IsEmpty, "the healthy observer to deliver despite the hung observer");
        Assert.False(hung.NeverReleased.Task.IsCompleted, "the hung callback is still pinned, did not wedge the loop");

        await app.StopAsync();
    }

    [Fact]
    public async Task TimedOutCallback_ThatLaterThrows_DoesNotRaise_UnobservedTaskException()
    {
        var unobserved = new ConcurrentBag<Exception>();
        void Handler(object? _, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var ex in e.Exception.InnerExceptions)
            {
                unobserved.Add(ex);
            }
        }
        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            var store = new InMemoryJobStore();
            var observer = new HungThenThrowObserver();
            await using var app = BuildHost(
                store,
                o =>
                {
                    o.DeliveryTimeout = TimeSpan.FromMilliseconds(100);
                    o.DeliveryRetryPolicy = new RetryPolicy { MaxAttempts = 1, Backoff = _ => TimeSpan.Zero };
                },
                new ObserverSpec("hung-throw", new ObserverSubscription([JobState.Succeeded]) { WireName = "ping" }, observer));
            await app.StartAsync();

            var client = app.Services.GetRequiredService<BackWaveClient>();
            await client.EnqueueAsync(new PingJob("observed"), dueTime: DateTimeOffset.UtcNow);

            // Wait for the callback to enter and then be abandoned (it times out at 100ms).
            await observer.Entered.Task.WaitAsync(TestTimeout);
            await Task.Delay(300); // let the timeout fire and the loop proceed.

            // Now release the abandoned callback so it throws — its exception must be OBSERVED by the
            // Shell's orphan dependency, never surfacing as an UnobservedTaskException.
            observer.Throw.TrySetResult();
            await Task.Delay(200);

            await app.StopAsync();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }

        // Force finalization: an unobserved faulted Task raises the event from its finalizer.
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.Empty(unobserved);
    }

    [Fact]
    public async Task DeliveryTimeout_IsConfigurableViaConfigurePump_AndNeverReachesTheCore()
    {
        // The Core run config (ObserverDispatchOptions) carries no DeliveryTimeout — it is a Shell-only
        // knob, so it cannot even be set on the Core type. This is a compile-time guarantee; we assert
        // the knob lives on ObserverPumpOptions with its 30s default and is honored by the pump.
        var options = new ObserverPumpOptions();
        Assert.Equal(TimeSpan.FromSeconds(30), options.DeliveryTimeout);
        Assert.DoesNotContain(
            typeof(ObserverDispatchOptions).GetProperties(),
            p => p.Name.Contains("Timeout", StringComparison.Ordinal));

        // And it is honored end-to-end: a short configured timeout abandons a hung callback quickly.
        var store = new InMemoryJobStore();
        var hung = new HungObserver();
        await using var app = BuildHost(
            store,
            o =>
            {
                o.DeliveryTimeout = TimeSpan.FromMilliseconds(100);
                o.DeliveryRetryPolicy = new RetryPolicy { MaxAttempts = 1, Backoff = _ => TimeSpan.Zero };
            },
            new ObserverSpec("hung", new ObserverSubscription([JobState.Succeeded]) { WireName = "ping" }, hung));
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        await client.EnqueueAsync(new PingJob("observed"), dueTime: DateTimeOffset.UtcNow);

        await WaitForAsync(
            () => monitor.GetObserverCursorAsync("hung").AsTask().GetAwaiter().GetResult() >= 0,
            "the short DeliveryTimeout to abandon the hung callback and advance the cursor");

        await app.StopAsync();
    }
}
