using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Monitor;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackWave.Hosting.Tests;

public sealed record PingJob(string Name);

public sealed class PingHandler(PingRecorder recorder) : IJobHandler<PingJob>
{
    public Task HandleAsync(PingJob job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Handled.Add(job.Name);
        return Task.CompletedTask;
    }
}

public sealed class PingRecorder
{
    public List<string> Handled { get; } = [];
}

[JsonSerializable(typeof(PingJob))]
internal sealed partial class HostingJsonContext : JsonSerializerContext;

public class HostingShellTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(25);

    private static JobRegistry Registry(string queue = "default") => new(
    [
        JobRegistration.Create<PingJob, PingHandler>("ping", HostingJsonContext.Default.PingJob, queue),
    ]);

    private static WebApplication BuildHost(IJobStore store, JobRegistry registry, params WorkerGroupOptions[] groups)
        => BuildHost(store, registry, null, groups);

    private static WebApplication BuildHost(
        IJobStore store, JobRegistry registry, ILoggerProvider? loggerProvider, params WorkerGroupOptions[] groups)
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
            backwave.UseStore(store).UseRegistry(registry);
            foreach (var group in groups)
            {
                backwave.AddWorkerGroup(group);
            }
        });
        builder.Services.AddHealthChecks().AddCheck<BackWaveHealthCheck>("backwave");

        var app = builder.Build();
        app.MapHealthChecks("/health");
        app.MapGet("/ping", () => "pong");
        return app;
    }

    private static WorkerGroupOptions Group(string name, string queue, TimeSpan? leaseDuration = null) => new()
    {
        Name = name,
        Policy = new DispatchPolicy.Strict([queue]),
        PollInterval = FastPoll,
        LeaseDuration = leaseDuration ?? TimeSpan.FromSeconds(5),
    };

    private static async Task WaitForAsync(Func<ValueTask<bool>> condition, string description)
    {
        var deadline = DateTimeOffset.UtcNow + TestTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.Fail($"Timed out waiting for: {description}");
    }

    [Fact]
    public async Task HostApp_RegistersViaDi_AndRunsJobsEndToEnd()
    {
        await using var app = BuildHost(new InMemoryJobStore(), Registry(), Group("workers", "default"));
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new PingJob("first"), dueTime: DateTimeOffset.UtcNow);

        await WaitForAsync(
            async () => (await monitor.GetJobAsync(jobId))?.State == JobState.Succeeded,
            "the enqueued job to succeed through the hosted pump");
        Assert.Equal(["first"], app.Services.GetRequiredService<PingRecorder>().Handled);

        var health = await app.GetTestClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        await app.StopAsync();
    }

    [Fact]
    public async Task MultipleWorkerGroups_RunInOneHost()
    {
        var registry = new JobRegistry(
        [
            JobRegistration.Create<PingJob, PingHandler>("ping", HostingJsonContext.Default.PingJob, "critical"),
        ]);
        await using var app = BuildHost(
            new InMemoryJobStore(), registry,
            Group("critical-workers", "critical"),
            Group("bulk-workers", "bulk"));
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var criticalJob = await client.EnqueueAsync(new PingJob("critical-1"), dueTime: DateTimeOffset.UtcNow);
        var bulkJob = await client.EnqueueAsync(new PingJob("bulk-1"), dueTime: DateTimeOffset.UtcNow, queue: "bulk");

        await WaitForAsync(
            async () => (await monitor.GetJobAsync(criticalJob))?.State == JobState.Succeeded
                && (await monitor.GetJobAsync(bulkJob))?.State == JobState.Succeeded,
            "both Worker Groups to process their queues");

        await app.StopAsync();
    }

    [Fact]
    public async Task InvariantViolation_HaltsOnlyThatGroup_HostKeepsServingHttp()
    {
        var store = new FaultableStore(new InMemoryJobStore()) { PoisonedQueue = "poison" };
        await using var app = BuildHost(
            store, Registry(),
            Group("healthy-workers", "default"),
            Group("poisoned-workers", "poison"));
        await app.StartAsync();

        var health = app.Services.GetRequiredService<BackWaveHealth>();
        await WaitForAsync(
            () => ValueTask.FromResult(health.HaltedGroups.ContainsKey("poisoned-workers")),
            "the poisoned Worker Group to fail-stop");

        // The healthy group keeps processing the same host's work.
        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new PingJob("survivor"), dueTime: DateTimeOffset.UtcNow);
        await WaitForAsync(
            async () => (await monitor.GetJobAsync(jobId))?.State == JobState.Succeeded,
            "the healthy Worker Group to keep processing");

        // The host keeps serving traffic; the health check pages instead.
        var http = app.GetTestClient();
        var ping = await http.GetAsync("/ping");
        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        var healthResponse = await http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, healthResponse.StatusCode);

        await app.StopAsync();
    }

    [Fact]
    public async Task HandlerInternalCancellation_Retries_InsteadOfTerminalCancel()
    {
        var registry = new JobRegistry(
        [
            JobRegistration.Create<PingJob, HttpTimeoutHandler>("ping", HostingJsonContext.Default.PingJob),
        ]);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<PingRecorder>();
        builder.Services.AddTransient<IJobHandler<PingJob>, HttpTimeoutHandler>();
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(new InMemoryJobStore())
            .UseRegistry(registry)
            .AddWorkerGroup(Group("workers", "default") with
            {
                RetryPolicy = new RetryPolicy { MaxAttempts = 3, Backoff = _ => TimeSpan.FromMilliseconds(50) },
            }));
        await using var app = builder.Build();
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new PingJob("timeout-then-ok"), dueTime: DateTimeOffset.UtcNow);

        // A TaskCanceledException from inside the handler (an HttpClient timeout) is a
        // plain failure: the job retries and succeeds — it never goes terminal Cancelled.
        await WaitForAsync(
            async () => (await monitor.GetJobAsync(jobId))?.State == JobState.Succeeded,
            "the job to retry past the handler-internal cancellation");
        Assert.Equal(2, (await monitor.GetJobAsync(jobId))!.Attempt);

        await app.StopAsync();
    }

    [Fact]
    public async Task ADeadTicker_FailStopsTheGroup_Loudly()
    {
        // PeriodicTimer rejects a zero interval: the poll ticker dies on its first breath.
        // Fail-stop visibility (ADR-0007): the group must go red, not idle silently.
        await using var app = BuildHost(
            new InMemoryJobStore(), Registry(),
            Group("workers", "default") with { PollInterval = TimeSpan.Zero });
        await app.StartAsync();

        var health = app.Services.GetRequiredService<BackWaveHealth>();
        await WaitForAsync(
            () => ValueTask.FromResult(health.HaltedGroups.ContainsKey("workers")),
            "the group to fail-stop on the dead ticker");

        await app.StopAsync();
    }

    [Fact]
    public async Task HaltedNodesInFlightJob_IsRecoveredByAnotherNode_ViaLeaseExpiry()
    {
        var inner = new InMemoryJobStore();
        var faultable = new FaultableStore(inner);
        var gate = new RecoveryGate();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<PingJob, RecoveryHandler>("ping", HostingJsonContext.Default.PingJob),
        ]);

        WebApplication BuildNode(IJobStore store)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(gate);
            builder.Services.AddTransient<IJobHandler<PingJob>, RecoveryHandler>();
            builder.Services.AddBackWave(backwave => backwave
                .UseStore(store)
                .UseRegistry(registry)
                .AddWorkerGroup(Group("workers", "default", leaseDuration: TimeSpan.FromMilliseconds(400))));
            return builder.Build();
        }

        // Node 1 claims the job, blocks mid-execution, then fail-stops.
        await using var node1 = BuildNode(faultable);
        await node1.StartAsync();
        var client = new BackWaveClient(inner, registry);
        var jobId = await client.EnqueueAsync(new PingJob("recover-me"), dueTime: DateTimeOffset.UtcNow);
        await gate.FirstAttemptStarted.Task.WaitAsync(TestTimeout);
        faultable.FailEverything = true; // the forced invariant violation

        var health1 = node1.Services.GetRequiredService<BackWaveHealth>();
        await WaitForAsync(
            () => ValueTask.FromResult(!health1.IsHealthy),
            "node 1 to fail-stop while holding the Lease");

        // Node 2 (a separate host on the same store) inherits via Lease expiry.
        await using var node2 = BuildNode(inner);
        await node2.StartAsync();
        var monitor = new BackWaveMonitor(inner);
        await WaitForAsync(
            async () => (await monitor.GetJobAsync(jobId))?.State == JobState.Succeeded,
            "node 2 to recover the job after the Lease expired");

        var job = await monitor.GetJobAsync(jobId);
        Assert.Equal(2, job!.Attempt); // the claim that recovered it is the second Attempt
        Assert.Equal(2, gate.HandledAttempt);

        await node2.StopAsync();
        await node1.StopAsync();
    }

    // --- Fault classification: transient retries, invariant fail-stops (issue 0031) ---

    [Fact]
    public async Task TransientStoreFault_DoesNotHaltTheGroup_ProcessingResumesAfterRecovery()
    {
        var store = new FaultableStore(new InMemoryJobStore()) { TransientClaimFaults = 3 };
        var logs = new CapturingLoggerProvider();
        await using var app = BuildHost(store, Registry(), logs, Group("workers", "default"));
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var health = app.Services.GetRequiredService<BackWaveHealth>();
        var jobId = await client.EnqueueAsync(new PingJob("resilient"), dueTime: DateTimeOffset.UtcNow);

        await WaitForAsync(
            async () => (await monitor.GetJobAsync(jobId))?.State == JobState.Succeeded,
            "the job to process after the transient store faults recover");

        // Never halted; each transient fault logged the catalog's StoreFaultTransientRetry (1301) at
        // Warning naming the group, never a Critical fail-stop.
        Assert.True(health.IsHealthy);
        Assert.False(health.HaltedGroups.ContainsKey("workers"));
        var transientFaults = logs.Entries.Where(e => e.EventId == 1301).ToList();
        Assert.NotEmpty(transientFaults);
        Assert.All(transientFaults, e => Assert.Equal(LogLevel.Warning, e.Level));
        Assert.All(transientFaults, e => Assert.Contains("workers", e.Message));
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Critical);

        await app.StopAsync();
    }

    [Fact]
    public async Task InvariantViolation_FailStops_EmitsOneCriticalLog_AndNamesTheExceptionType()
    {
        var store = new FaultableStore(new InMemoryJobStore()) { PoisonedQueue = "poison" };
        var logs = new CapturingLoggerProvider();
        await using var app = BuildHost(store, Registry(), logs, Group("poisoned-workers", "poison"));
        await app.StartAsync();

        var health = app.Services.GetRequiredService<BackWaveHealth>();
        await WaitForAsync(
            () => ValueTask.FromResult(health.HaltedGroups.ContainsKey("poisoned-workers")),
            "the poisoned Worker Group to fail-stop");

        // The halted state names the exception type, not just its message.
        var halt = health.HaltedGroups["poisoned-workers"];
        Assert.Contains(nameof(InvalidOperationException), halt.ExceptionType);
        Assert.Contains("poison", halt.Message);

        // Exactly one Critical entry, carrying the full exception (type, message, stack).
        var critical = Assert.Single(logs.Entries, e => e.Level == LogLevel.Critical);
        Assert.IsType<InvalidOperationException>(critical.Exception);

        await app.StopAsync();
    }

    // --- Client clock consistency: DI TimeProvider governs the client (issue 0045) ---

    [Fact]
    public async Task DiRegisteredTimeProvider_GovernsTheClient()
    {
        var store = new InMemoryJobStore();
        var fixedTime = new DateTimeOffset(2030, 5, 5, 0, 0, 0, TimeSpan.Zero);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(fixedTime));
        services.AddSingleton<PingRecorder>();
        services.AddTransient<IJobHandler<PingJob>, PingHandler>();
        services.AddBackWave(backwave => backwave.UseStore(store).UseRegistry(Registry()));
        await using var provider = services.BuildServiceProvider();

        // The recurring upsert passes no instant: the container's TimeProvider must stamp it.
        var client = provider.GetRequiredService<BackWaveClient>();
        await client.UpsertRecurringAsync("nightly", Cron.Daily(3), new PingJob("digest"));

        var schedule = Assert.Single(await store.ListSchedulesAsync());
        Assert.Equal(fixedTime, schedule.Schedule.Cursor);
    }

    // --- Pump clock consistency: a DI TimeProvider governs the pump too, so cross-node clock
    //     skew is reproducible at the real host — the shape behind vopr-0139 ---

    [Fact]
    public async Task ANodeWhosePumpClockIsSkewedAhead_PrematurelyReclaimsAPeersStillValidLease()
    {
        // Node A holds a fresh 5s Lease measured on the real clock. A peer whose pump runs an hour
        // ahead sweeps expiry, sees that Lease as long past, and reclaims the job before it should —
        // the premature-reclamation hazard behind vopr-0139, now reproducible at the real host. This
        // has teeth ONLY because the pump reads its instants from the injected TimeProvider: on the
        // system clock the peer's sweep would find A's Lease valid and this test would time out.
        var inner = new InMemoryJobStore();
        var gate = new RecoveryGate();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<PingJob, RecoveryHandler>("ping", HostingJsonContext.Default.PingJob),
        ]);

        WebApplication BuildNode(TimeProvider? clock)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(gate);
            builder.Services.AddTransient<IJobHandler<PingJob>, RecoveryHandler>();
            if (clock is not null)
            {
                builder.Services.AddSingleton<TimeProvider>(clock);
            }
            builder.Services.AddBackWave(backwave => backwave
                .UseStore(inner)
                .UseRegistry(registry)
                .AddWorkerGroup(Group("workers", "default", leaseDuration: TimeSpan.FromSeconds(5))));
            return builder.Build();
        }

        // Node A (real clock) claims the job and blocks mid-execution, holding a live 5s Lease.
        await using var nodeA = BuildNode(clock: null);
        await nodeA.StartAsync();
        var client = new BackWaveClient(inner, registry);
        var jobId = await client.EnqueueAsync(new PingJob("skew-me"), dueTime: DateTimeOffset.UtcNow);
        await gate.FirstAttemptStarted.Task.WaitAsync(TestTimeout);

        // Node B's pump runs an hour ahead: its expiry sweep treats A's still-valid Lease as expired
        // and reclaims the job as a second Attempt while A is still blocked on the first.
        await using var nodeB = BuildNode(clock: new OffsetTimeProvider(TimeSpan.FromHours(1)));
        await nodeB.StartAsync();

        var monitor = new BackWaveMonitor(inner);
        await WaitForAsync(
            async () => (await monitor.GetJobAsync(jobId))?.State == JobState.Succeeded,
            "the clock-skewed peer to reclaim and complete the job while node A still holds attempt 1");

        var job = await monitor.GetJobAsync(jobId);
        Assert.Equal(2, job!.Attempt); // node A still holds attempt 1; the skewed peer reclaimed it as attempt 2
        Assert.Equal(2, gate.HandledAttempt);

        await nodeB.StopAsync();
        await nodeA.StopAsync();
    }

    // --- Driver-owned re-poll: a released dependency runs promptly in production (issue 0042) ---

    [Fact]
    public async Task DependencyReleasedByItsParentsOutcome_IsClaimedPromptly_NotAtTheNextPollInterval()
    {
        var pollInterval = TimeSpan.FromSeconds(4);
        await using var app = BuildHost(
            new InMemoryJobStore(), Registry(),
            Group("workers", "default") with { PollInterval = pollInterval });
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();

        // Both exist before the first poll; the dependency is released by the parent's
        // terminal outcome, and the Driver's re-poll must claim it in that same cycle.
        var parentId = await client.EnqueueAsync(new PingJob("parent"), dueTime: DateTimeOffset.UtcNow);
        var childId = await client.EnqueueDependencyAsync(new PingJob("child"), parentId);

        await WaitForAsync(
            async () => (await monitor.GetJobAsync(parentId))?.State == JobState.Succeeded,
            "the parent to succeed");
        var parentDoneAt = DateTimeOffset.UtcNow;

        await WaitForAsync(
            async () => (await monitor.GetJobAsync(childId))?.State == JobState.Succeeded,
            "the released dependency to run");
        var lag = DateTimeOffset.UtcNow - parentDoneAt;

        // Re-poll: claimed within the same cascade. Without it, the dependency would wait a
        // full poll interval for the next tick.
        Assert.True(lag < pollInterval / 2, $"dependency lagged {lag}, ~poll interval {pollInterval} — re-poll missing?");

        await app.StopAsync();
    }

    [Fact]
    public async Task AThrowingHandler_RecordsFailureDetail_OnTheFailingTransition_ButNoneOnSuccess()
    {
        // A throwing handler routed through the production pump captures the exception's full
        // type/message/stack and writes it onto the failing transition (§5.12, issue 0059). A
        // job that succeeds records no detail on any transition. Both run in one host.
        var registry = new JobRegistry(
        [
            JobRegistration.Create<PingJob, ThrowingHandler>("ping", HostingJsonContext.Default.PingJob),
        ]);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<PingRecorder>();
        builder.Services.AddTransient<IJobHandler<PingJob>, ThrowingHandler>();
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(new InMemoryJobStore())
            .UseRegistry(registry)
            // MaxAttempts = 1 ⇒ the first throw dead-letters at once, leaving the failing
            // transition terminal and easy to inspect.
            .AddWorkerGroup(Group("workers", "default") with
            {
                RetryPolicy = new RetryPolicy { MaxAttempts = 1, Backoff = _ => TimeSpan.Zero },
            }));
        await using var app = builder.Build();
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var failId = await client.EnqueueAsync(new PingJob("boom"), dueTime: DateTimeOffset.UtcNow);
        var okId = await client.EnqueueAsync(new PingJob("ok"), dueTime: DateTimeOffset.UtcNow);

        await WaitForAsync(
            async () => (await monitor.GetJobAsync(failId))?.State == JobState.DeadLettered,
            "the throwing job to dead-letter");
        await WaitForAsync(
            async () => (await monitor.GetJobAsync(okId))?.State == JobState.Succeeded,
            "the succeeding job to finish");

        var failHistory = await monitor.GetJobHistoryAsync(failId);
        var failing = Assert.Single(failHistory, t => t.State == JobState.DeadLettered);
        Assert.NotNull(failing.FailureDetail);
        Assert.Contains(nameof(InvalidOperationException), failing.FailureDetail);
        Assert.Contains(ThrowingHandler.Marker, failing.FailureDetail);
        Assert.Contains("at ", failing.FailureDetail); // a stack frame
        // No detail leaks onto the job's other transitions (Scheduled, Leased).
        Assert.All(failHistory.Where(t => t.State != JobState.DeadLettered), t => Assert.Null(t.FailureDetail));

        // The succeeding job records no Failure Detail anywhere.
        Assert.All(await monitor.GetJobHistoryAsync(okId), t => Assert.Null(t.FailureDetail));

        await app.StopAsync();
    }

    // --- Per-Attempt DI scope: scoped deps resolve and dispose per Attempt (ADR 0021, issue 0104) ---

    [Fact]
    public async Task HandlerWithScopedDependency_ResolvesFromPerAttemptScope_AndDisposesAtAttemptEnd()
    {
        // The pump opens a DI scope per Attempt and resolves the handler from it. A handler taking a
        // *scoped* dependency therefore runs — before ADR 0021 this threw "cannot resolve scoped
        // service from root provider" — and that dependency's IDisposable is disposed when the scope
        // closes at Attempt end, never captured by the root container for the process lifetime.
        var registry = new JobRegistry(
        [
            JobRegistration.Create<PingJob, ScopedDepHandler>("ping", HostingJsonContext.Default.PingJob),
        ]);
        var disposalLog = new DisposalLog();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<PingRecorder>();
        builder.Services.AddSingleton(disposalLog);
        builder.Services.AddScoped<ScopedProbe>();
        builder.Services.AddScoped<IJobHandler<PingJob>, ScopedDepHandler>();
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(new InMemoryJobStore())
            .UseRegistry(registry)
            .AddWorkerGroup(Group("workers", "default")));
        await using var app = builder.Build();
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new PingJob("scoped"), dueTime: DateTimeOffset.UtcNow);

        await WaitForAsync(
            async () => (await monitor.GetJobAsync(jobId))?.State == JobState.Succeeded,
            "the job with a scoped dependency to succeed through the per-Attempt scope");

        // It ran (so the scoped service resolved) ...
        Assert.Equal(["scoped"], app.Services.GetRequiredService<PingRecorder>().Handled);
        Assert.Equal(1, Volatile.Read(ref disposalLog.Constructed));
        // ... and the scoped disposable was released when the Attempt's scope closed.
        await WaitForAsync(
            () => ValueTask.FromResult(Volatile.Read(ref disposalLog.Disposed) == 1),
            "the scoped disposable dependency to be disposed at Attempt end");

        await app.StopAsync();
    }

    [Fact]
    public async Task UseJobs_RegistersRegistryAndScopedHandlers_InOneCall_AndRunsEndToEnd()
    {
        // UseJobs(JobModule) is the colocated registration: it registers the Job Registry AND a scoped
        // handler per [Job] from one module, with no hand-written AddTransient<IJobHandler<…>> list.
        // The generator emits this module as BackWaveJobs.Module; here we build the same shape by hand.
        var module = new JobModule
        {
            Registrations = [JobRegistration.Create<PingJob, PingHandler>("ping", HostingJsonContext.Default.PingJob)],
            Handlers = [new JobHandlerMapping(typeof(IJobHandler<PingJob>), typeof(PingHandler))],
            ContainingTypes = [], // PingHandler is class-based — no declaring class to register
        };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<PingRecorder>();
        // Note: no AddTransient<IJobHandler<PingJob>, …> — UseJobs registers the handler (scoped).
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(new InMemoryJobStore())
            .UseJobs(module)
            .AddWorkerGroup(Group("workers", "default")));
        await using var app = builder.Build();
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new PingJob("via-usejobs"), dueTime: DateTimeOffset.UtcNow);

        await WaitForAsync(
            async () => (await monitor.GetJobAsync(jobId))?.State == JobState.Succeeded,
            "the job to run through UseJobs-registered registry and scoped handler");
        Assert.Equal(["via-usejobs"], app.Services.GetRequiredService<PingRecorder>().Handled);

        await app.StopAsync();
    }

    [Fact]
    public async Task UseJobs_AutoRegistersMethodSugarDeclaringClass_Scoped_WithNoHandWrittenRegistration()
    {
        // Method sugar: the generated handler ctor-injects the class that declares the [Job] method and
        // forwards to it (here WelcomeHandler → WelcomeJobs). UseJobs registers that declaring class from
        // JobModule.ContainingTypes — scoped, with NO hand-written registration of WelcomeJobs — so its
        // scoped dependency (ScopedProbe) resolves and disposes per Attempt (ADR 0021 amendment, 0106).
        var disposalLog = new DisposalLog();
        var module = new JobModule
        {
            Registrations = [JobRegistration.Create<PingJob, WelcomeHandler>("ping", HostingJsonContext.Default.PingJob)],
            Handlers = [new JobHandlerMapping(typeof(IJobHandler<PingJob>), typeof(WelcomeHandler))],
            ContainingTypes = [typeof(WelcomeJobs)],
        };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<PingRecorder>();
        builder.Services.AddSingleton(disposalLog);
        builder.Services.AddScoped<ScopedProbe>();
        // Note: no AddScoped<WelcomeJobs>() — UseJobs registers the declaring class from ContainingTypes.
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(new InMemoryJobStore())
            .UseJobs(module)
            .AddWorkerGroup(Group("workers", "default")));
        await using var app = builder.Build();
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new PingJob("sugar"), dueTime: DateTimeOffset.UtcNow);

        await WaitForAsync(
            async () => (await monitor.GetJobAsync(jobId))?.State == JobState.Succeeded,
            "the method-sugar job to run through the auto-registered, scoped declaring class");
        Assert.Equal(["sugar"], app.Services.GetRequiredService<PingRecorder>().Handled);
        // The declaring class resolved from the Attempt scope (its scoped ScopedProbe was built once) and
        // that scope's disposable was released at Attempt end.
        Assert.Equal(1, Volatile.Read(ref disposalLog.Constructed));
        await WaitForAsync(
            () => ValueTask.FromResult(Volatile.Read(ref disposalLog.Disposed) == 1),
            "the declaring class's scoped dependency to be disposed at Attempt end");

        await app.StopAsync();
    }

    [Fact]
    public void UseJobs_DoesNotOverride_AHostPreRegisteredDeclaringClass()
    {
        // TryAddScoped, not AddScoped: a host that deliberately pre-registers the declaring class (the
        // rare singleton-with-state case) keeps its registration — UseJobs does not clobber it (0106).
        var module = new JobModule
        {
            Registrations = [JobRegistration.Create<PingJob, WelcomeHandler>("ping", HostingJsonContext.Default.PingJob)],
            Handlers = [new JobHandlerMapping(typeof(IJobHandler<PingJob>), typeof(WelcomeHandler))],
            ContainingTypes = [typeof(WelcomeJobs)],
        };
        var services = new ServiceCollection();
        services.AddSingleton<WelcomeJobs>(); // host's deliberate lifetime, declared first
        services.AddBackWave(backwave => backwave.UseStore(new InMemoryJobStore()).UseJobs(module));

        var welcome = Assert.Single(services, d => d.ServiceType == typeof(WelcomeJobs));
        Assert.Equal(ServiceLifetime.Singleton, welcome.Lifetime); // not replaced by the scoped default
    }
}

/// <summary>
/// A method-sugar <i>declaring class</i> — the shape the generator emits a handler around. It takes a
/// scoped <see cref="ScopedProbe"/>, so resolving it proves the per-Attempt scope reached the declaring
/// class, not just the handler.
/// </summary>
public sealed class WelcomeJobs(PingRecorder recorder, ScopedProbe probe)
{
    public Task RunAsync(PingJob job)
    {
        probe.Touch();
        recorder.Handled.Add(job.Name);
        return Task.CompletedTask;
    }
}

/// <summary>The generated-style handler that ctor-injects the declaring class and forwards to it.</summary>
public sealed class WelcomeHandler(WelcomeJobs target) : IJobHandler<PingJob>
{
    public Task HandleAsync(PingJob job, JobContext context, CancellationToken cancellationToken)
        => target.RunAsync(job);
}

/// <summary>A shared tally of how often the scoped probe was constructed and disposed.</summary>
public sealed class DisposalLog
{
    public int Constructed;
    public int Disposed;
}

/// <summary>
/// A scoped, disposable probe: bumps <see cref="DisposalLog.Constructed"/> when the Attempt scope
/// resolves it and <see cref="DisposalLog.Disposed"/> when that scope is torn down at Attempt end.
/// </summary>
public sealed class ScopedProbe : IDisposable
{
    private readonly DisposalLog _log;

    public ScopedProbe(DisposalLog log)
    {
        _log = log;
        Interlocked.Increment(ref log.Constructed);
    }

    /// <summary>A no-op the handler calls so the injected probe is unambiguously used.</summary>
    public void Touch() { }

    public void Dispose() => Interlocked.Increment(ref _log.Disposed);
}

/// <summary>Depends on a scoped <see cref="ScopedProbe"/> — unresolvable from the root provider.</summary>
public sealed class ScopedDepHandler(PingRecorder recorder, ScopedProbe probe) : IJobHandler<PingJob>
{
    public Task HandleAsync(PingJob job, JobContext context, CancellationToken cancellationToken)
    {
        probe.Touch();
        recorder.Handled.Add(job.Name);
        return Task.CompletedTask;
    }
}

/// <summary>A fixed clock for asserting the DI-registered TimeProvider reaches the client.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

// A clock that runs a fixed offset ahead of (or behind) the system clock while still advancing with
// real time — models a node whose wall clock is skewed, unlike FixedTimeProvider which freezes. Timer
// cadence is left untouched (CreateTimer is not overridden), so only the instants a pump STAMPS are
// skewed: exactly the cross-node clock-skew condition, with the poll/heartbeat cadence still real.
public sealed class OffsetTimeProvider(TimeSpan offset) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() + offset;
}

/// <summary>Captures every log entry so tests can assert on level, event id, and the carried exception.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentBag<(LogLevel Level, string Message, Exception? Exception, int EventId)> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

    public void Dispose() { }

    private sealed class CapturingLogger(ConcurrentBag<(LogLevel, string, Exception?, int)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => entries.Add((logLevel, formatter(state, exception), exception, eventId.Id));
    }
}

/// <summary>
/// Throws a distinctive exception for the job named "boom" (so its Failure Detail is
/// recognizable); succeeds for every other job, recording it like <see cref="PingHandler"/>.
/// </summary>
public sealed class ThrowingHandler(PingRecorder recorder) : IJobHandler<PingJob>
{
    public const string Marker = "deliberate failure for Failure Detail capture";

    public Task HandleAsync(PingJob job, JobContext context, CancellationToken cancellationToken)
    {
        if (job.Name == "boom")
        {
            throw new InvalidOperationException(Marker);
        }
        recorder.Handled.Add(job.Name);
        return Task.CompletedTask;
    }
}

/// <summary>Simulates an HttpClient timeout on the first Attempt; succeeds afterwards.</summary>
public sealed class HttpTimeoutHandler(PingRecorder recorder) : IJobHandler<PingJob>
{
    public Task HandleAsync(PingJob job, JobContext context, CancellationToken cancellationToken)
    {
        if (context.Attempt == 1)
        {
            throw new TaskCanceledException("the request timed out inside the handler");
        }
        recorder.Handled.Add(job.Name);
        return Task.CompletedTask;
    }
}

/// <summary>Blocks the first Attempt until cancelled; completes any later Attempt.</summary>
public sealed class RecoveryHandler(RecoveryGate gate) : IJobHandler<PingJob>
{
    public async Task HandleAsync(PingJob job, JobContext context, CancellationToken cancellationToken)
    {
        if (context.Attempt == 1)
        {
            gate.FirstAttemptStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        gate.HandledAttempt = context.Attempt;
    }
}

public sealed class RecoveryGate
{
    public TaskCompletionSource FirstAttemptStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int HandledAttempt { get; set; }
}
