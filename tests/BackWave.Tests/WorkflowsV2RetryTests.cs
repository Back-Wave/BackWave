using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Jobs;
using BackWave.Operations;
using BackWave.Pro;
using BackWave.Storage;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

// ── Steps, outputs, gate predicate, handlers, and recorder for retry/replay coverage ──

/// <summary>The output the flaky producer emits - each attempt writes a distinct Ref so a downstream
/// reader can prove which attempt's value persisted.</summary>
public sealed record RetryReceipt(string Ref);

/// <summary>The output the gate's observed ancestor produces.</summary>
public sealed record RetryTotal(int Cents);

/// <summary>Records which retry steps ran (with their attempt number) and what a reader observed.</summary>
public sealed class RetryRecorder
{
    public List<string> Ran { get; } = [];
    public DependencyOutput<RetryReceipt>? Seen { get; set; }
}

public sealed record GatePrice(int Cents) : IWorkflowStep<RetryTotal>;
public sealed record RetryArmA(string Note) : IWorkflowStep;
public sealed record RetryArmB(string Note) : IWorkflowStep;
public sealed record RetryOtherArm(string Note) : IWorkflowStep;
public sealed record FlakyInvoice(string OrderId) : IWorkflowStep<RetryReceipt>;
public sealed record ReadFlakyInvoice(string Note) : IWorkflowStep;
public sealed record RetryAlwaysFails(string Note) : IWorkflowStep<RetryReceipt>;
public sealed record FlakyUndo : IWorkflowStep;

/// <summary>The gate predicate: take the "then" arm only for a large total.</summary>
public sealed class BigRetryOrder : IWorkflowGate<GatePrice, RetryTotal>
{
    public bool Enter(DependencyOutput<RetryTotal> observed)
        => observed.HasOutput && observed.Output!.Cents > 100_000;
}

public sealed class GatePriceHandler(RetryRecorder recorder) : IJobHandler<GatePrice>
{
    public Task HandleAsync(GatePrice job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("price");
        context.SetOutput<GatePrice, RetryTotal>(new RetryTotal(job.Cents));
        return Task.CompletedTask;
    }
}

public sealed class RetryArmAHandler(RetryRecorder recorder) : IJobHandler<RetryArmA>
{
    public Task HandleAsync(RetryArmA job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("arm-a");
        return Task.CompletedTask;
    }
}

public sealed class RetryArmBHandler(RetryRecorder recorder) : IJobHandler<RetryArmB>
{
    public Task HandleAsync(RetryArmB job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("arm-b");
        return Task.CompletedTask;
    }
}

public sealed class RetryOtherArmHandler(RetryRecorder recorder) : IJobHandler<RetryOtherArm>
{
    public Task HandleAsync(RetryOtherArm job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("other");
        return Task.CompletedTask;
    }
}

// Buffers a distinct output on EVERY attempt, then fails the first one: the failed attempt's buffered
// output must be discarded with its outcome, and only the successful retry's value may persist.
public sealed class FlakyInvoiceHandler(RetryRecorder recorder) : IJobHandler<FlakyInvoice>
{
    public Task HandleAsync(FlakyInvoice job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add($"make:{context.Attempt}");
        context.SetOutput<FlakyInvoice, RetryReceipt>(new RetryReceipt($"attempt-{context.Attempt}"));
        if (context.Attempt == 1)
        {
            throw new InvalidOperationException("first attempt failed on purpose");
        }
        return Task.CompletedTask;
    }
}

public sealed class ReadFlakyInvoiceHandler(RetryRecorder recorder) : IJobHandler<ReadFlakyInvoice>
{
    public async Task HandleAsync(ReadFlakyInvoice job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("read");
        recorder.Seen = await context.Output<FlakyInvoice, RetryReceipt>(ct);
    }
}

public sealed class RetryAlwaysFailsHandler(RetryRecorder recorder) : IJobHandler<RetryAlwaysFails>
{
    public Task HandleAsync(RetryAlwaysFails job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add($"boom:{context.Attempt}");
        throw new InvalidOperationException("fails on every attempt on purpose");
    }
}

// A compensation whose own handler is flaky: its first attempt faults, its retry reads the protected
// work's decided state and undoes - the saga replay case.
public sealed class FlakyUndoHandler(RetryRecorder recorder) : IJobHandler<FlakyUndo>
{
    public async Task HandleAsync(FlakyUndo job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add($"undo-attempt:{context.Attempt}");
        if (context.Attempt == 1)
        {
            throw new InvalidOperationException("compensation first attempt failed on purpose");
        }
        var risky = await context.Output<RetryAlwaysFails, RetryReceipt>(ct);
        recorder.Ran.Add(risky.AncestorState == JobState.Succeeded ? "undo:noop" : "undo:undo");
    }
}

[JsonSerializable(typeof(RetryReceipt))]
[JsonSerializable(typeof(RetryTotal))]
[JsonSerializable(typeof(GatePrice))]
[JsonSerializable(typeof(RetryArmA))]
[JsonSerializable(typeof(RetryArmB))]
[JsonSerializable(typeof(RetryOtherArm))]
[JsonSerializable(typeof(FlakyInvoice))]
[JsonSerializable(typeof(ReadFlakyInvoice))]
[JsonSerializable(typeof(RetryAlwaysFails))]
[JsonSerializable(typeof(FlakyUndo))]
[JsonSerializable(typeof(WorkflowGate<BigRetryOrder, GatePrice, RetryTotal>))]
internal sealed partial class WorkflowsV2RetryJsonContext : JsonSerializerContext;

/// <summary>
/// Workflows v2 retry/replay coverage: every other WorkflowsV2 harness pins MaxAttempts = 1, so these
/// tests give jobs a second attempt plus an injected first-attempt fault and pin the replay semantics -
/// the gate idempotently re-cancels the not-taken arm, a failed attempt's buffered output is discarded
/// in favor of the successful retry's, and a compensation handler that faults once undoes on replay.
/// </summary>
public class WorkflowsV2RetryTests
{
    // Same lazy-operator wiring as the conditional tests: the gate handler resolves a BackWaveOperator
    // from DI, and the operator needs the harness store (optionally wrapped to inject cancel faults)
    // and clock, which only exist once the harness is built.
    private sealed class OperatorHolder
    {
        public BackWaveOperator? Operator { get; set; }
    }

    private sealed class HarnessClock(BackWaveHarness harness) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => harness.Now;
    }

    private static BackWaveHarness NewHarness(
        out RetryRecorder recorder, Func<IJobStore, IJobStore>? wrapOperatorStore = null)
    {
        var holder = new OperatorHolder();
        var services = new ServiceCollection()
            .AddSingleton<RetryRecorder>()
            .AddSingleton(holder)
            .AddSingleton(sp => sp.GetRequiredService<OperatorHolder>().Operator!)
            .AddTransient<IJobHandler<GatePrice>, GatePriceHandler>()
            .AddTransient<IJobHandler<RetryArmA>, RetryArmAHandler>()
            .AddTransient<IJobHandler<RetryArmB>, RetryArmBHandler>()
            .AddTransient<IJobHandler<RetryOtherArm>, RetryOtherArmHandler>()
            .AddTransient<IJobHandler<FlakyInvoice>, FlakyInvoiceHandler>()
            .AddTransient<IJobHandler<ReadFlakyInvoice>, ReadFlakyInvoiceHandler>()
            .AddTransient<IJobHandler<RetryAlwaysFails>, RetryAlwaysFailsHandler>()
            .AddTransient<IJobHandler<FlakyUndo>, FlakyUndoHandler>()
            .AddTransient<IJobHandler<WorkflowGate<BigRetryOrder, GatePrice, RetryTotal>>,
                WorkflowGateHandler<BigRetryOrder, GatePrice, RetryTotal>>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<GatePrice, GatePriceHandler>(
                "retry-price", WorkflowsV2RetryJsonContext.Default.GatePrice,
                outputTypeInfo: WorkflowsV2RetryJsonContext.Default.RetryTotal),
            JobRegistration.Create<RetryArmA, RetryArmAHandler>("retry-arm-a", WorkflowsV2RetryJsonContext.Default.RetryArmA),
            JobRegistration.Create<RetryArmB, RetryArmBHandler>("retry-arm-b", WorkflowsV2RetryJsonContext.Default.RetryArmB),
            JobRegistration.Create<RetryOtherArm, RetryOtherArmHandler>("retry-other", WorkflowsV2RetryJsonContext.Default.RetryOtherArm),
            JobRegistration.Create<FlakyInvoice, FlakyInvoiceHandler>(
                "retry-flaky-invoice", WorkflowsV2RetryJsonContext.Default.FlakyInvoice,
                outputTypeInfo: WorkflowsV2RetryJsonContext.Default.RetryReceipt),
            JobRegistration.Create<ReadFlakyInvoice, ReadFlakyInvoiceHandler>(
                "retry-read-invoice", WorkflowsV2RetryJsonContext.Default.ReadFlakyInvoice),
            JobRegistration.Create<RetryAlwaysFails, RetryAlwaysFailsHandler>(
                "retry-always-fails", WorkflowsV2RetryJsonContext.Default.RetryAlwaysFails,
                outputTypeInfo: WorkflowsV2RetryJsonContext.Default.RetryReceipt),
            JobRegistration.Create<FlakyUndo, FlakyUndoHandler>("retry-flaky-undo", WorkflowsV2RetryJsonContext.Default.FlakyUndo),
            JobRegistration.Create<WorkflowGate<BigRetryOrder, GatePrice, RetryTotal>, WorkflowGateHandler<BigRetryOrder, GatePrice, RetryTotal>>(
                "retry-gate", WorkflowsV2RetryJsonContext.Default.WorkflowGateBigRetryOrderGatePriceRetryTotal),
        ]);
        recorder = services.GetRequiredService<RetryRecorder>();
        var harness = new BackWaveHarness(registry, services, new BackWaveHarnessOptions
        {
            RetryPolicy = new Core.RetryPolicy { MaxAttempts = 2 },
        });
        var operatorStore = wrapOperatorStore is null ? harness.Store : wrapOperatorStore(harness.Store);
        holder.Operator = new BackWaveOperator(operatorStore, new HarnessClock(harness));
        return harness;
    }

    // ── 1. The gate crashes mid-cancel-loop and idempotently re-cancels on retry ────

    [Fact]
    public async Task GateCrashMidCancelLoop_OnRetry_IdempotentlyReCancelsTheNotTakenArm()
    {
        CancelFaultStore? faultStore = null;
        var h = NewHarness(out var recorder, inner => faultStore = new CancelFaultStore(inner) { FaultOnCall = 2 });

        // A two-step then arm; the false predicate makes it the not-taken arm. The injected fault
        // crashes the gate's first attempt after it cancelled one arm member but not the other, so the
        // retry replays the whole cancel loop over a half-cancelled arm.
        var id = await h.Client.Workflow()
            .Then(new GatePrice(50))
            .If<BigRetryOrder, GatePrice, RetryTotal>(
                then: b => b.Then(new RetryArmA("x")).Then(new RetryArmB("x")),
                otherwise: b => b.Then(new RetryOtherArm("x")))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));

        // The first attempt reached the store twice (one cancel landed, the second faulted); the retry
        // re-ran the full loop over both members - the already-cancelled one must not break it.
        Assert.Equal(4, faultStore!.CancelCalls);

        var byWire = (await h.Monitor.GetWorkflowAsync(id))!.Members.ToDictionary(m => m.WireName);
        Assert.Equal(JobState.Succeeded, byWire["retry-gate"].State);
        Assert.Equal(2, byWire["retry-gate"].Attempt);                  // succeeded on the retry
        Assert.Equal(JobState.Cancelled, byWire["retry-arm-a"].State);  // whole arm cancelled
        Assert.Equal(JobState.Cancelled, byWire["retry-arm-b"].State);
        Assert.Equal(JobState.Succeeded, byWire["retry-other"].State);  // taken arm proceeded
        Assert.Contains("other", recorder.Ran);
        Assert.DoesNotContain("arm-a", recorder.Ran);
        Assert.DoesNotContain("arm-b", recorder.Ran);
    }

    // ── 2. SetOutput on a retry attempt: the successful attempt's value is the one persisted ──

    [Fact]
    public async Task SetOutput_OnARetryAttempt_PersistsTheSuccessfulAttemptsValueDownstream()
    {
        var h = NewHarness(out var recorder);

        await h.Client.Workflow()
            .Then(new FlakyInvoice("o"))
            .Then(new ReadFlakyInvoice("x"))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));

        // Attempt 1 buffered "attempt-1" and failed (buffered output discarded with the failed
        // outcome); attempt 2 buffered "attempt-2" and succeeded. Downstream must see attempt-2.
        Assert.Equal(["make:1", "make:2", "read"], recorder.Ran);
        var seen = Assert.IsType<DependencyOutput<RetryReceipt>>(recorder.Seen);
        Assert.True(seen.HasOutput);
        Assert.Equal(new RetryReceipt("attempt-2"), seen.Output);
    }

    // ── 3. Compensation replay: an undo that faults on its first attempt undoes on retry ──

    [Fact]
    public async Task CompensationHandler_FaultsOnFirstAttempt_UndoesOnItsRetry()
    {
        var h = NewHarness(out var recorder);

        var id = await h.Client.Workflow()
            .Then(new RetryAlwaysFails("x"))
            .WithCompensation<FlakyUndo>()
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));

        // The protected step burned both attempts and dead-lettered; the compensation released
        // OnAnyTerminal, faulted once, and undid on its replay.
        Assert.Equal(["boom:1", "boom:2", "undo-attempt:1", "undo-attempt:2", "undo:undo"], recorder.Ran);

        var byWire = (await h.Monitor.GetWorkflowAsync(id))!.Members.ToDictionary(m => m.WireName);
        Assert.Equal(JobState.DeadLettered, byWire["retry-always-fails"].State);
        Assert.Equal(JobState.Succeeded, byWire["retry-flaky-undo"].State);
        Assert.Equal(2, byWire["retry-flaky-undo"].Attempt);
        Assert.Equal(WorkflowStatus.Failed, (await h.Monitor.GetWorkflowAsync(id))!.Status);
    }
}

/// <summary>
/// Wraps the In-Memory Store for the gate's operator and throws once, on the <see cref="FaultOnCall"/>-th
/// cancel, so a test can crash the gate handler mid-cancel-loop deterministically. Every other member
/// delegates untouched.
/// </summary>
internal sealed class CancelFaultStore(IJobStore inner) : IJobStore
{
    private int _cancelCalls;

    /// <summary>The 1-based cancel call that throws; every other call passes through.</summary>
    public required int FaultOnCall { get; init; }

    /// <summary>How many cancel calls reached the store, including the faulted one.</summary>
    public int CancelCalls => _cancelCalls;

    public ValueTask<CancelResult> CancelJobAsync(Guid jobId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (++_cancelCalls == FaultOnCall)
        {
            throw new InvalidOperationException("injected cancel fault (mid-cancel-loop crash)");
        }
        return inner.CancelJobAsync(jobId, actor, now, cancellationToken);
    }

    public bool SupportsTransactionalEnqueue => inner.SupportsTransactionalEnqueue;

    public ValueTask<EnqueueResult> EnqueueAsync(
        NewJob job, DateTimeOffset now, System.Data.Common.DbTransaction? transaction = null, CancellationToken cancellationToken = default)
        => inner.EnqueueAsync(job, now, transaction, cancellationToken);

    public ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default)
        => inner.ClaimAsync(request, cancellationToken);

    public ValueTask<OutcomeResult> ReportOutcomeAsync(
        Guid jobId, string workerId, int attempt, JobOutcome outcome, DateTimeOffset now,
        string? failureDetail = null, JobTags? addedTags = null, ReadOnlyMemory<byte>? output = null,
        CancellationToken cancellationToken = default)
        => inner.ReportOutcomeAsync(jobId, workerId, attempt, outcome, now, failureDetail, addedTags, output, cancellationToken);

    public ValueTask<ReadOnlyMemory<byte>?> GetJobOutputAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetJobOutputAsync(jobId, cancellationToken);

    public ValueTask<IReadOnlyList<HeartbeatResult>> HeartbeatAsync(
        string workerId, IReadOnlyList<Guid> jobIds, TimeSpan leaseDuration, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.HeartbeatAsync(workerId, jobIds, leaseDuration, now, cancellationToken);

    public ValueTask<int> ExpireLeasesAsync(
        DateTimeOffset now, int maxJobs, IReadOnlyList<string> queues, RetryDisposition disposition, CancellationToken cancellationToken = default)
        => inner.ExpireLeasesAsync(now, maxJobs, queues, disposition, cancellationToken);

    public ValueTask<RequeueResult> RequeueAsync(Guid jobId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.RequeueAsync(jobId, actor, now, cancellationToken);

    public ValueTask PauseQueueAsync(string queue, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.PauseQueueAsync(queue, actor, now, cancellationToken);

    public ValueTask ResumeQueueAsync(string queue, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.ResumeQueueAsync(queue, actor, now, cancellationToken);

    public ValueTask<TriggerScheduleResult> TriggerScheduleNowAsync(string scheduleId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.TriggerScheduleNowAsync(scheduleId, actor, now, cancellationToken);

    public ValueTask<IReadOnlyList<OperatorAuditRecord>> ListAuditRecordsAsync(string target, CancellationToken cancellationToken = default)
        => inner.ListAuditRecordsAsync(target, cancellationToken);

    public ValueTask UpsertScheduleAsync(ScheduleRecord schedule, CancellationToken cancellationToken = default)
        => inner.UpsertScheduleAsync(schedule, cancellationToken);

    public ValueTask RemoveScheduleAsync(string scheduleId, CancellationToken cancellationToken = default)
        => inner.RemoveScheduleAsync(scheduleId, cancellationToken);

    public ValueTask<IReadOnlyList<ScheduleSnapshot>> ListSchedulesAsync(CancellationToken cancellationToken = default)
        => inner.ListSchedulesAsync(cancellationToken);

    public ValueTask<int> MintDueAsync(IReadOnlyList<MintDecision> decisions, CancellationToken cancellationToken = default)
        => inner.MintDueAsync(decisions, cancellationToken);

    public ValueTask SetConcurrencyLimitAsync(string queue, int? limit, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.SetConcurrencyLimitAsync(queue, limit, actor, now, cancellationToken);

    public ValueTask<JobRecord?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetJobAsync(jobId, cancellationToken);

    public ValueTask<IReadOnlyList<JobTransition>> GetJobHistoryAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetJobHistoryAsync(jobId, cancellationToken);

    public ValueTask<IReadOnlyList<JobRecord>> ListJobsAsync(JobQuery query, CancellationToken cancellationToken = default)
        => inner.ListJobsAsync(query, cancellationToken);

    public ValueTask<IReadOnlyList<QueueStateCount>> CountJobsAsync(CancellationToken cancellationToken = default)
        => inner.CountJobsAsync(cancellationToken);

    public ValueTask<IReadOnlyList<TagFacet>> FacetAsync(
        string key, JobQuery? baseQuery = null, int maxResults = int.MaxValue, CancellationToken cancellationToken = default)
        => inner.FacetAsync(key, baseQuery, maxResults, cancellationToken);

    public ValueTask<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(TagSuggestQuery query, CancellationToken cancellationToken = default)
        => inner.SuggestTagsAsync(query, cancellationToken);

    public ValueTask<IReadOnlyList<QueueSettings>> ListQueueSettingsAsync(CancellationToken cancellationToken = default)
        => inner.ListQueueSettingsAsync(cancellationToken);

    public ValueTask<DependencyEdges> GetDependencyEdgesAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetDependencyEdgesAsync(jobId, cancellationToken);

    public ValueTask<WorkflowEnqueueResult> EnqueueWorkflowAsync(
        WorkflowDefinition workflow, DateTimeOffset now, System.Data.Common.DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
        => inner.EnqueueWorkflowAsync(workflow, now, transaction, cancellationToken);

    public ValueTask<IReadOnlyList<WorkflowSnapshot>> ListWorkflowsAsync(CancellationToken cancellationToken = default)
        => inner.ListWorkflowsAsync(cancellationToken);

    public ValueTask<WorkflowGraph?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default)
        => inner.GetWorkflowAsync(workflowId, cancellationToken);

    public ValueTask<int> PurgeTerminalAsync(
        TerminalStateClass stateClass, DateTimeOffset terminalBefore, int maxJobs, CancellationToken cancellationToken = default)
        => inner.PurgeTerminalAsync(stateClass, terminalBefore, maxJobs, cancellationToken);

    public ValueTask<ObserverClaim> ClaimObserverDeliveriesAsync(
        ObserverClaimRequest request, CancellationToken cancellationToken = default)
        => inner.ClaimObserverDeliveriesAsync(request, cancellationToken);

    public ValueTask ReportObserverDeliveriesAsync(
        ObserverDeliveryReport report, CancellationToken cancellationToken = default)
        => inner.ReportObserverDeliveriesAsync(report, cancellationToken);

    public ValueTask<long> GetObserverCursorAsync(string observerId, CancellationToken cancellationToken = default)
        => inner.GetObserverCursorAsync(observerId, cancellationToken);

    public ValueTask<ObserverLag> GetObserverLagAsync(ObserverLagRequest request, CancellationToken cancellationToken = default)
        => inner.GetObserverLagAsync(request, cancellationToken);

    public ValueTask<IReadOnlyList<ObserverDeadLetterRecord>> ListObserverDeadLettersAsync(
        string observerId, CancellationToken cancellationToken = default)
        => inner.ListObserverDeadLettersAsync(observerId, cancellationToken);
}
