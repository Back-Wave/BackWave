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

        // A lost race, not a broken invariant: counted and logged at Warning, and the group stays up.
        var violation = Assert.Single(violations.Measurements);
        Assert.Equal("Degrade", violation);
        var warning = Assert.Single(
            logs.Entries,
            e => e.EventId == 1601 && e.Message.Contains(nameof(InvariantTrigger.OutcomeFenceRejected)));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(jobId.ToString(), warning.Message);
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
    public async Task WorkflowMemberWithoutWorkflow_FailsTheJob_ButNotTheGroup()
    {
        // The resolver runs above the execution boundary, where every handler throw becomes job data. The
        // trigger therefore ends the Attempt loudly rather than halting the group - it never reaches the
        // halt call site, so no Halt is counted and the group keeps serving.
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

        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        await WaitForAsync(
            async () => (await monitor.GetJobHistoryAsync(member.JobId))
                .Any(t => t.FailureDetail?.Contains(nameof(InvariantViolationException)) == true),
            "the member's Attempt to fail on the raised invariant");

        // The id itself is deliberately absent from the message, so this site names the condition in the
        // failure detail and reaches none of the three trigger-id surfaces.
        var failed = Assert.Single(
            await monitor.GetJobHistoryAsync(member.JobId),
            t => t.FailureDetail?.Contains(nameof(InvariantViolationException)) == true);
        var detail = failed.FailureDetail;
        Assert.Contains("workflow's row is absent", detail);
        Assert.DoesNotContain(nameof(InvariantTrigger.WorkflowMemberWithoutWorkflow), detail);

        Assert.Empty(app.Services.GetRequiredService<BackWaveHealth>().HaltedGroups);
        Assert.Empty(violations.Measurements);
        await app.StopAsync();
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

    /// <summary>Collects the action tag of every violation counted under one trigger, and only that one.</summary>
    private sealed class ViolationRecorder : IDisposable
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
