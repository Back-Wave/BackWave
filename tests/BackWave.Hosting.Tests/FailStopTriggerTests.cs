using System.Net;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Diagnostics;
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

/// <summary>A job whose handler parks until the test releases it, so a heartbeat can fire mid-execution.</summary>
public sealed record ParkedJob(string Name);

/// <summary>The latch a <see cref="ParkedJob"/> handler waits on, so one test owns exactly one gate.</summary>
public sealed class ExecutionGate
{
    public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class ParkedHandler(ExecutionGate gate) : IJobHandler<ParkedJob>
{
    public async Task HandleAsync(ParkedJob job, JobContext context, CancellationToken cancellationToken)
    {
        gate.Entered.TrySetResult();
        await gate.Released.Task.WaitAsync(cancellationToken);
    }
}

/// <summary>A job whose handler pulls a Dependency's output, so a member can read its Workflow.</summary>
public sealed record PullingJob(string Handle);

public sealed class PullingHandler : IJobHandler<PullingJob>
{
    public async Task HandleAsync(PullingJob job, JobContext context, CancellationToken cancellationToken)
        => await context.GetDependencyOutputAsync(job.Handle, FailStopJsonContext.Default.PullingJob, cancellationToken);
}

[JsonSerializable(typeof(ParkedJob))]
[JsonSerializable(typeof(PullingJob))]
internal sealed partial class FailStopJsonContext : JsonSerializerContext;

/// <summary>
/// Collects the action tag of every violation counted under one trigger, and only that one. File-scope
/// rather than nested, because the Job Output tests assert on this same ledger's ZEROS: the fence check
/// they exercise must count nothing at all.
/// </summary>
public sealed class ViolationRecorder : IDisposable
{
    private readonly MeterListener _listener;

    public ViolationRecorder(InvariantTrigger trigger)
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BackWaveDiagnostics.SourceName
                    && instrument.Name == "backwave.invariant.violations")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var pairs = tags.ToArray();
            if (pairs.FirstOrDefault(t => t.Key == "backwave.invariant.trigger").Value as string
                == trigger.ToString())
            {
                Measurements.Add(pairs.FirstOrDefault(t => t.Key == "backwave.invariant.action").Value as string);
            }
        });
        _listener.Start();
    }

    public ConcurrentBag<string?> Measurements { get; } = [];

    public void Dispose() => _listener.Dispose();
}

/// <summary>
/// The armed impossible-state checks, each driven through the real hosted pump against a store that
/// answers the way only a broken store could. Every check is asserted on all three of its surfaces: the
/// halted group's typed trigger, the Critical halt log, and the tagged violation counter.
/// </summary>
public class FailStopTriggerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(25);

    [Fact]
    public async Task ClaimThatReturnsATerminalJob_FailStops_WithClaimedJobTerminal()
    {
        var store = new FaultableStore(new InMemoryJobStore()) { RewriteClaimedState = JobState.Succeeded };
        var logs = new CapturingLoggerProvider();
        using var violations = new ViolationRecorder(InvariantTrigger.ClaimedJobTerminal);

        await using var app = BuildHost(store, logs, Group("workers"));
        await app.StartAsync();
        await app.Services.GetRequiredService<BackWaveClient>()
            .EnqueueAsync(new ParkedJob("terminal"), dueTime: DateTimeOffset.UtcNow);

        await AssertHaltedAsync(app, logs, violations, InvariantTrigger.ClaimedJobTerminal);
        await app.StopAsync();
    }

    [Fact]
    public async Task ClaimThatReturnsMoreJobsThanItWasBoundTo_FailStops_WithClaimBatchOverrun()
    {
        var store = new FaultableStore(new InMemoryJobStore()) { OverClaimBy = 1 };
        var logs = new CapturingLoggerProvider();
        using var violations = new ViolationRecorder(InvariantTrigger.ClaimBatchOverrun);

        await using var app = BuildHost(store, logs, Group("workers"));
        await app.StartAsync();
        await app.Services.GetRequiredService<BackWaveClient>()
            .EnqueueAsync(new ParkedJob("over-claimed"), dueTime: DateTimeOffset.UtcNow);

        await AssertHaltedAsync(app, logs, violations, InvariantTrigger.ClaimBatchOverrun);
        await app.StopAsync();
    }

    [Fact]
    public async Task OutcomeBatchAnsweredWithFewerRowsThanItWasGiven_FailStops_WithOutcomeBatchCountMismatch()
    {
        var store = new FaultableStore(new InMemoryJobStore()) { DropOutcomeResults = 1 };
        var logs = new CapturingLoggerProvider();
        using var violations = new ViolationRecorder(InvariantTrigger.OutcomeBatchCountMismatch);
        var gate = new ExecutionGate();
        gate.Released.SetResult();  // the handler runs straight through, so the outcome reports at once

        await using var app = BuildHost(store, logs, Group("workers"), gate);
        await app.StartAsync();
        await app.Services.GetRequiredService<BackWaveClient>()
            .EnqueueAsync(new ParkedJob("reported"), dueTime: DateTimeOffset.UtcNow);

        await AssertHaltedAsync(app, logs, violations, InvariantTrigger.OutcomeBatchCountMismatch);
        await app.StopAsync();
    }

    [Fact]
    public async Task HeartbeatAnsweredWithFewerRowsThanItWasGiven_FailStops_WithHeartbeatBatchCountMismatch()
    {
        var store = new FaultableStore(new InMemoryJobStore()) { DropHeartbeatResults = 1 };
        var logs = new CapturingLoggerProvider();
        using var violations = new ViolationRecorder(InvariantTrigger.HeartbeatBatchCountMismatch);
        var gate = new ExecutionGate();

        var group = Group("workers") with { HeartbeatInterval = TimeSpan.FromMilliseconds(50) };
        await using var app = BuildHost(store, logs, group, gate);
        await app.StartAsync();
        await app.Services.GetRequiredService<BackWaveClient>()
            .EnqueueAsync(new ParkedJob("parked"), dueTime: DateTimeOffset.UtcNow);

        // The heartbeat only carries job ids while something is executing, so park inside the handler.
        await gate.Entered.Task.WaitAsync(TestTimeout);
        await AssertHaltedAsync(app, logs, violations, InvariantTrigger.HeartbeatBatchCountMismatch);

        gate.Released.TrySetResult();
        await app.StopAsync();
    }

    [Fact]
    public async Task FencedOutOutcome_Degrades_WithOutcomeFenceRejected_AndTheGroupKeepsRunning()
    {
        var store = new FaultableStore(new InMemoryJobStore()) { FenceOutOutcomes = true };
        var logs = new CapturingLoggerProvider();
        using var violations = new ViolationRecorder(InvariantTrigger.OutcomeFenceRejected);
        var gate = new ExecutionGate();
        gate.Released.SetResult();

        await using var app = BuildHost(store, logs, Group("workers"), gate);
        await app.StartAsync();
        var jobId = await app.Services.GetRequiredService<BackWaveClient>()
            .EnqueueAsync(new ParkedJob("fenced"), dueTime: DateTimeOffset.UtcNow);

        await WaitForAsync(
            () => ValueTask.FromResult(!violations.Measurements.IsEmpty),
            "the fenced-out outcome to be counted");

        // Not a lost race: the Lease this pump believes it holds runs for another five seconds, so a store
        // answering "not this worker's" contradicts it. Counted and logged at Warning, group stays up.
        var violation = Assert.Single(violations.Measurements);
        Assert.Equal("Degrade", violation);
        var warning = Assert.Single(
            logs.Entries,
            e => e.EventId == 1601 && e.Message.Contains(nameof(InvariantTrigger.OutcomeFenceRejected)));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(jobId.ToString(), warning.Message);
        // The group-altitude counterpart (2003), the only one of the pair that names the worker group -
        // nothing puts the group on a log scope. Both fire; the counter stays welded to 1601.
        var atGroupAltitude = Assert.Single(logs.Entries, e => e.EventId == 2003);
        Assert.Equal(LogLevel.Warning, atGroupAltitude.Level);
        Assert.Contains("workers", atGroupAltitude.Message);
        Assert.Contains(nameof(InvariantTrigger.OutcomeFenceRejected), atGroupAltitude.Message);
        Assert.Empty(app.Services.GetRequiredService<BackWaveHealth>().HaltedGroups);
        Assert.Equal(
            HttpStatusCode.OK, (await app.GetTestClient().GetAsync("/health")).StatusCode);

        await app.StopAsync();
    }

    [Fact]
    public async Task LiveWorkflowMemberWhoseWorkflowRowIsGone_Raises_WorkflowMemberWithoutWorkflow()
    {
        var store = new FaultableStore(new InMemoryJobStore());
        var member = new NewJob(
            Guid.NewGuid(), "pulling", ReadOnlyMemory<byte>.Empty, "default", DateTimeOffset.UtcNow);
        Assert.Equal(
            WorkflowEnqueueResult.Ok,
            await store.EnqueueWorkflowAsync(
                new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [member] }, DateTimeOffset.UtcNow));

        store.HideWorkflows = true;
        var resolver = new StoreDependencyResolver(store);
        var raised = await Assert.ThrowsAsync<InvariantViolationException>(
            async () => await resolver.ResolveAsync(member.JobId, "parent"));
        Assert.Equal(InvariantTrigger.WorkflowMemberWithoutWorkflow, raised.Trigger);
    }

    [Fact]
    public async Task TerminalWorkflowMemberWhoseWorkflowRowIsGone_ResolvesToNothing()
    {
        // Retention prunes a Workflow only once no job references it, so a terminal reader CAN legally
        // outlive its Workflow row between the resolver's two un-transacted reads. That race must not raise.
        var store = new FaultableStore(new InMemoryJobStore());
        var now = DateTimeOffset.UtcNow;
        var member = new NewJob(Guid.NewGuid(), "pulling", ReadOnlyMemory<byte>.Empty, "default", now);
        Assert.Equal(
            WorkflowEnqueueResult.Ok,
            await store.EnqueueWorkflowAsync(
                new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [member] }, now));
        await store.ClaimAsync(new ClaimRequest("w1", ["default"], 32, TimeSpan.FromMinutes(1), now));
        Assert.Equal(
            OutcomeResult.Applied,
            await store.ReportOutcomeAsync(member.JobId, "w1", 1, new JobOutcome.Success(), now));

        store.HideWorkflows = true;
        Assert.Null(await new StoreDependencyResolver(store).ResolveAsync(member.JobId, "parent"));
    }

    [Fact]
    public async Task WorkflowMemberWithoutWorkflow_RaisedInsideAHandler_FailStopsTheGroup()
    {
        // The resolver runs above the execution boundary, where every OTHER handler throw becomes job
        // data. This one may not: a live member whose Workflow row is gone proves an impossible state no
        // retry can fix, so it must reach the group's halt site rather than degrade into one more failed
        // Attempt. The execution is fire-and-forget, so the event channel carries it: the run completes
        // the writer with the violation, the pump's own read throws it, and the halt site classifies it
        // off the exception's own type exactly as it does an in-cycle one.
        var store = new FaultableStore(new InMemoryJobStore());
        var logs = new CapturingLoggerProvider();
        using var violations = new ViolationRecorder(InvariantTrigger.WorkflowMemberWithoutWorkflow);
        var now = DateTimeOffset.UtcNow;
        var member = new NewJob(Guid.NewGuid(), "pulling", JobPayload(new PullingJob("parent")), "default", now);
        Assert.Equal(
            WorkflowEnqueueResult.Ok,
            await store.EnqueueWorkflowAsync(
                new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [member] }, now));
        store.HideWorkflows = true;

        await using var app = BuildHost(store, logs, Group("workers"));
        await app.StartAsync();

        await AssertHaltedAsync(app, logs, violations, InvariantTrigger.WorkflowMemberWithoutWorkflow);

        // The halting Attempt reports nothing - a halted pump writes no more to the store - so the member
        // stays Leased and lapses for a healthy node to inherit, exactly as every other halt leaves it.
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        Assert.Equal(JobState.Leased, (await monitor.GetJobAsync(member.JobId))!.State);
        await app.StopAsync();
    }

    [Fact]
    public async Task FenceRefusingALeaseThisPumpWatchedLapse_IsNotCounted_AndLogsAtDebug()
    {
        // The benign half of the fence answer, and the one a healthy fleet actually produces: the Lease
        // lapsed before its outcome could be reported, so another node owns the job now and the store is
        // right to refuse the write. The invariant ledger is the surface a promotion rule reads FOR ZEROS,
        // so this path must leave it untouched.
        var store = new FaultableStore(new InMemoryJobStore()) { FenceOutOutcomes = true };
        var logs = new CapturingLoggerProvider();
        using var violations = new ViolationRecorder(InvariantTrigger.OutcomeFenceRejected);
        var gate = new ExecutionGate();

        // A Lease shorter than the handler parks for, with no heartbeat to renew it, so the pump has
        // already watched its own belief expire by the time the outcome settles.
        var group = Group("workers") with { LeaseDuration = TimeSpan.FromMilliseconds(50) };
        await using var app = BuildHost(store, logs, group, gate);
        await app.StartAsync();
        var jobId = await app.Services.GetRequiredService<BackWaveClient>()
            .EnqueueAsync(new ParkedJob("lapsed"), dueTime: DateTimeOffset.UtcNow);

        await gate.Entered.Task.WaitAsync(TestTimeout);
        await Task.Delay(200);  // outlive the Lease, then let the outcome report against the lapsed belief
        gate.Released.SetResult();

        await WaitForAsync(
            () => ValueTask.FromResult(logs.Entries.Any(e => e.EventId == 1208)),
            "the fenced-out outcome to be logged at Debug");

        var debug = Assert.Single(logs.Entries, e => e.EventId == 1208);
        Assert.Equal(LogLevel.Debug, debug.Level);
        Assert.Contains(jobId.ToString(), debug.Message);
        Assert.Empty(violations.Measurements);
        Assert.DoesNotContain(logs.Entries, e => e.EventId is 1601 or 2003);
        Assert.Empty(app.Services.GetRequiredService<BackWaveHealth>().HaltedGroups);

        await app.StopAsync();
    }

    [Fact]
    public async Task InvariantRaisedByTheShutdownHandBack_FailStops_InsteadOfReadingAsAHiccup()
    {
        // Every adapter can raise a named check out of its relinquish path, and the hand-back's blanket
        // catch would have logged that as a routine shutdown hiccup, left the group green, and lost the
        // trigger nothing else records. It has to reach the halt site like any other proven violation.
        var store = new FaultableStore(new InMemoryJobStore())
        {
            RelinquishInvariant = InvariantTrigger.DanglingGatingEdge,
        };
        var logs = new CapturingLoggerProvider();
        using var violations = new ViolationRecorder(InvariantTrigger.DanglingGatingEdge);
        var gate = new ExecutionGate();  // parked, so the pump still holds a Lease to hand back

        await using var app = BuildHost(store, logs, Group("workers"), gate);
        await app.StartAsync();
        await app.Services.GetRequiredService<BackWaveClient>()
            .EnqueueAsync(new ParkedJob("held"), dueTime: DateTimeOffset.UtcNow);
        await gate.Entered.Task.WaitAsync(TestTimeout);

        await app.StopAsync().WaitAsync(TestTimeout);  // a halt still never blocks the host from exiting

        Assert.Equal(1, store.RelinquishCalls);
        await AssertHaltedAsync(app, logs, violations, InvariantTrigger.DanglingGatingEdge);
        Assert.DoesNotContain(logs.Entries, e => e.EventId == 1206);  // NOT the shutdown-hiccup warning
    }

    // --- harness ---------------------------------------------------------------------------------

    private static ReadOnlyMemory<byte> JobPayload(PullingJob job)
        => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(job, FailStopJsonContext.Default.PullingJob);

    private static async Task AssertHaltedAsync(
        WebApplication app, CapturingLoggerProvider logs, ViolationRecorder violations, InvariantTrigger expected)
    {
        var health = app.Services.GetRequiredService<BackWaveHealth>();
        await WaitForAsync(
            () => ValueTask.FromResult(health.HaltedGroups.ContainsKey("workers")),
            $"the Worker Group to fail-stop on {expected}");

        // One id, three surfaces: the halted state's typed component, the Critical halt log, and the counter.
        Assert.Equal(expected, health.HaltedGroups["workers"].Trigger);
        var critical = Assert.Single(logs.Entries, e => e.Level == LogLevel.Critical);
        Assert.Equal(2001, critical.EventId);
        Assert.Contains(expected.ToString(), critical.Message);
        Assert.Equal("Halt", Assert.Single(violations.Measurements));
    }

    private static WorkerGroupOptions Group(string name) => new()
    {
        Name = name,
        Policy = new DispatchPolicy.Strict(["default"]),
        PollInterval = FastPoll,
        LeaseDuration = TimeSpan.FromSeconds(5),
        HeartbeatInterval = TimeSpan.FromMinutes(10),  // never fires unless a test asks for it
    };

    private static WebApplication BuildHost(
        IJobStore store, ILoggerProvider logs, WorkerGroupOptions group, ExecutionGate? gate = null)
    {
        var registry = new JobRegistry(
        [
            JobRegistration.Create<ParkedJob, ParkedHandler>(
                "parked", FailStopJsonContext.Default.ParkedJob, "default"),
            JobRegistration.Create<PullingJob, PullingHandler>(
                "pulling", FailStopJsonContext.Default.PullingJob, "default"),
        ]);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Debug);  // the benign fence path logs there and nowhere else
        builder.Services.AddSingleton(gate ?? new ExecutionGate());
        builder.Services.AddTransient<IJobHandler<ParkedJob>, ParkedHandler>();
        builder.Services.AddTransient<IJobHandler<PullingJob>, PullingHandler>();
        builder.Services.AddBackWave(backwave =>
        {
            backwave.UseStore(store).UseRegistry(registry);
            backwave.AddWorkerGroup(group);
        });
        builder.Services.AddHealthChecks().AddCheck<BackWaveHealthCheck>("backwave");

        var app = builder.Build();
        app.MapHealthChecks("/health");
        return app;
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
            await Task.Delay(25);
        }
        Assert.Fail($"Timed out waiting for: {description}");
    }
}
