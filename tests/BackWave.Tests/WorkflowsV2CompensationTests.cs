using System.Text.Json.Serialization;
using BackWave.Jobs;
using BackWave.Pro;
using BackWave.Storage;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

// ── Saga steps, output type, undo steps, handlers, recorder (0267) ──────────────────────

/// <summary>The receipt a saga step produces - the id an undo step reads back to know what to reverse.</summary>
public sealed record SagaReceipt(string OrderId, string Ref);

/// <summary>Records the order saga steps and undo steps ran, and what each undo decided.</summary>
public sealed class SagaRecorder
{
    public List<string> Ran { get; } = [];
}

// Protected steps: each declares a Job Output so an undo can pull its id; SagaShip is the risky one.
public sealed record SagaCharge(string OrderId) : IWorkflowStep<SagaReceipt>;
public sealed record SagaReserve(string OrderId) : IWorkflowStep<SagaReceipt>;
public sealed record SagaShip(string OrderId, bool Fail) : IWorkflowStep<SagaReceipt>;

// Undo steps: parameterless side-branch steps whose handlers pull the protected work's decided state.
public sealed record CleanupShip : IWorkflowStep;
public sealed record RefundCharge : IWorkflowStep;
public sealed record ReleaseReserve : IWorkflowStep;

// A parallel region (0265) plus a compensation over its branch tips.
public sealed record SagaRoot(string Note) : IWorkflowStep;
public sealed record SagaBranchOk(string Note) : IWorkflowStep;
public sealed record SagaBranchBoom(string Note) : IWorkflowStep<SagaReceipt>;
public sealed record UndoParallel : IWorkflowStep;

public sealed class SagaChargeHandler(SagaRecorder recorder) : IJobHandler<SagaCharge>
{
    public Task HandleAsync(SagaCharge job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("charge");
        context.SetOutput<SagaCharge, SagaReceipt>(new SagaReceipt(job.OrderId, $"chg-{job.OrderId}"));
        return Task.CompletedTask;
    }
}

public sealed class SagaReserveHandler(SagaRecorder recorder) : IJobHandler<SagaReserve>
{
    public Task HandleAsync(SagaReserve job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("reserve");
        context.SetOutput<SagaReserve, SagaReceipt>(new SagaReceipt(job.OrderId, $"rsv-{job.OrderId}"));
        return Task.CompletedTask;
    }
}

public sealed class SagaShipHandler(SagaRecorder recorder) : IJobHandler<SagaShip>
{
    public Task HandleAsync(SagaShip job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("ship");
        if (job.Fail)
        {
            throw new InvalidOperationException("shipping failed on purpose");
        }
        context.SetOutput<SagaShip, SagaReceipt>(new SagaReceipt(job.OrderId, $"shp-{job.OrderId}"));
        return Task.CompletedTask;
    }
}

// Single-step compensation: reads its own protected step and undoes only when that step failed.
public sealed class CleanupShipHandler(SagaRecorder recorder) : IJobHandler<CleanupShip>
{
    public async Task HandleAsync(CleanupShip job, JobContext context, CancellationToken ct)
    {
        var ship = await context.Output<SagaShip, SagaReceipt>(ct);
        recorder.Ran.Add(ship.AncestorState == JobState.Succeeded ? "cleanup:noop" : "cleanup:undo");
    }
}

// Undoes SagaCharge, but only when the risky tail (SagaShip) failed; pulls the charge's id to reverse.
public sealed class RefundChargeHandler(SagaRecorder recorder) : IJobHandler<RefundCharge>
{
    public async Task HandleAsync(RefundCharge job, JobContext context, CancellationToken ct)
    {
        var ship = await context.Output<SagaShip, SagaReceipt>(ct);
        if (ship.AncestorState == JobState.Succeeded)
        {
            recorder.Ran.Add("refund:noop");
            return;
        }

        var charge = await context.Output<SagaCharge, SagaReceipt>(ct);
        recorder.Ran.Add(charge.HasOutput ? $"refund:{charge.Output!.Ref}" : "refund:absent");
    }
}

// Undoes SagaReserve when the risky tail failed; pulls the reservation id.
public sealed class ReleaseReserveHandler(SagaRecorder recorder) : IJobHandler<ReleaseReserve>
{
    public async Task HandleAsync(ReleaseReserve job, JobContext context, CancellationToken ct)
    {
        var ship = await context.Output<SagaShip, SagaReceipt>(ct);
        if (ship.AncestorState == JobState.Succeeded)
        {
            recorder.Ran.Add("release:noop");
            return;
        }

        var reserve = await context.Output<SagaReserve, SagaReceipt>(ct);
        recorder.Ran.Add(reserve.HasOutput ? $"release:{reserve.Output!.Ref}" : "release:absent");
    }
}

public sealed class SagaRootHandler(SagaRecorder recorder) : IJobHandler<SagaRoot>
{
    public Task HandleAsync(SagaRoot job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("root");
        return Task.CompletedTask;
    }
}

public sealed class SagaBranchOkHandler(SagaRecorder recorder) : IJobHandler<SagaBranchOk>
{
    public Task HandleAsync(SagaBranchOk job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("branch-ok");
        return Task.CompletedTask;
    }
}

public sealed class SagaBranchBoomHandler(SagaRecorder recorder) : IJobHandler<SagaBranchBoom>
{
    public Task HandleAsync(SagaBranchBoom job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("branch-boom");
        throw new InvalidOperationException("parallel branch failed on purpose");
    }
}

public sealed class UndoParallelHandler(SagaRecorder recorder) : IJobHandler<UndoParallel>
{
    public async Task HandleAsync(UndoParallel job, JobContext context, CancellationToken ct)
    {
        var boom = await context.Output<SagaBranchBoom, SagaReceipt>(ct);
        recorder.Ran.Add(boom.AncestorState == JobState.Succeeded ? "undo-parallel:noop" : "undo-parallel:undo");
    }
}

[JsonSerializable(typeof(SagaReceipt))]
[JsonSerializable(typeof(SagaCharge))]
[JsonSerializable(typeof(SagaReserve))]
[JsonSerializable(typeof(SagaShip))]
[JsonSerializable(typeof(CleanupShip))]
[JsonSerializable(typeof(RefundCharge))]
[JsonSerializable(typeof(ReleaseReserve))]
[JsonSerializable(typeof(SagaRoot))]
[JsonSerializable(typeof(SagaBranchOk))]
[JsonSerializable(typeof(SagaBranchBoom))]
[JsonSerializable(typeof(UndoParallel))]
internal sealed partial class WorkflowsV2CompensationJsonContext : JsonSerializerContext;

/// <summary>
/// Workflows v2 compensation / sagas (issue 0267): <c>.WithCompensation&lt;TUndo&gt;</c> wires an always-reachable
/// undo step to the protected work with the existing terminal fan-in mode - no new dependency mode. The undo
/// handler pulls the protected work's decided state and undoes only on failure, else no-ops; successive
/// compensations undo in reverse order via static edges. All above the frozen below-boundary spine.
/// </summary>
public class WorkflowsV2CompensationTests
{
    private static BackWaveHarness NewHarness(out SagaRecorder recorder)
    {
        var services = new ServiceCollection()
            .AddSingleton<SagaRecorder>()
            .AddTransient<IJobHandler<SagaCharge>, SagaChargeHandler>()
            .AddTransient<IJobHandler<SagaReserve>, SagaReserveHandler>()
            .AddTransient<IJobHandler<SagaShip>, SagaShipHandler>()
            .AddTransient<IJobHandler<CleanupShip>, CleanupShipHandler>()
            .AddTransient<IJobHandler<RefundCharge>, RefundChargeHandler>()
            .AddTransient<IJobHandler<ReleaseReserve>, ReleaseReserveHandler>()
            .AddTransient<IJobHandler<SagaRoot>, SagaRootHandler>()
            .AddTransient<IJobHandler<SagaBranchOk>, SagaBranchOkHandler>()
            .AddTransient<IJobHandler<SagaBranchBoom>, SagaBranchBoomHandler>()
            .AddTransient<IJobHandler<UndoParallel>, UndoParallelHandler>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<SagaCharge, SagaChargeHandler>(
                "saga-charge", WorkflowsV2CompensationJsonContext.Default.SagaCharge,
                outputTypeInfo: WorkflowsV2CompensationJsonContext.Default.SagaReceipt),
            JobRegistration.Create<SagaReserve, SagaReserveHandler>(
                "saga-reserve", WorkflowsV2CompensationJsonContext.Default.SagaReserve,
                outputTypeInfo: WorkflowsV2CompensationJsonContext.Default.SagaReceipt),
            JobRegistration.Create<SagaShip, SagaShipHandler>(
                "saga-ship", WorkflowsV2CompensationJsonContext.Default.SagaShip,
                outputTypeInfo: WorkflowsV2CompensationJsonContext.Default.SagaReceipt),
            JobRegistration.Create<CleanupShip, CleanupShipHandler>(
                "saga-cleanup-ship", WorkflowsV2CompensationJsonContext.Default.CleanupShip),
            JobRegistration.Create<RefundCharge, RefundChargeHandler>(
                "saga-refund-charge", WorkflowsV2CompensationJsonContext.Default.RefundCharge),
            JobRegistration.Create<ReleaseReserve, ReleaseReserveHandler>(
                "saga-release-reserve", WorkflowsV2CompensationJsonContext.Default.ReleaseReserve),
            JobRegistration.Create<SagaRoot, SagaRootHandler>(
                "saga-root", WorkflowsV2CompensationJsonContext.Default.SagaRoot),
            JobRegistration.Create<SagaBranchOk, SagaBranchOkHandler>(
                "saga-branch-ok", WorkflowsV2CompensationJsonContext.Default.SagaBranchOk),
            JobRegistration.Create<SagaBranchBoom, SagaBranchBoomHandler>(
                "saga-branch-boom", WorkflowsV2CompensationJsonContext.Default.SagaBranchBoom,
                outputTypeInfo: WorkflowsV2CompensationJsonContext.Default.SagaReceipt),
            JobRegistration.Create<UndoParallel, UndoParallelHandler>(
                "saga-undo-parallel", WorkflowsV2CompensationJsonContext.Default.UndoParallel),
        ]);
        recorder = services.GetRequiredService<SagaRecorder>();
        return new BackWaveHarness(registry, services, new BackWaveHarnessOptions
        {
            RetryPolicy = new Core.RetryPolicy { MaxAttempts = 1 },
        });
    }

    // ── Lowering seam (pure module): OnAnyTerminal, protected-frontier edge, side-branch frontier ──

    [Fact]
    public void Lowering_Compensation_IsWiredOnAnyTerminal_ToTheProtectedStep_WithoutAdvancingTheFrontier()
    {
        var h = NewHarness(out _);

        var def = h.Client.Workflow()
            .Then(new SagaCharge("o"))
            .WithCompensation<RefundCharge>()      // side-branch off charge
            .Then(new SagaShip("o", Fail: false))  // must still depend on charge, not on the undo
            .Build();

        var byWire = def.Members.ToDictionary(m => m.WireName);
        var chargeId = byWire["saga-charge"].JobId;

        // The compensation always becomes reachable: OnAnyTerminal on exactly the protected step.
        Assert.Equal(DependencyMode.OnAnyTerminal, byWire["saga-refund-charge"].Mode);
        Assert.Equal([chargeId], byWire["saga-refund-charge"].Parents);

        // The frontier was left on the protected work: the next step depends on charge, never on the undo.
        Assert.Equal([chargeId], byWire["saga-ship"].Parents);
    }

    [Fact]
    public void Lowering_MultipleCompensations_WireReverseOrderEdges_LaterProtectedUndoesFirst()
    {
        var h = NewHarness(out _);

        // Charge -> reserve -> ship(risky); compensate charge, then reserve. Both guard the ship tip.
        var def = h.Client.Workflow()
            .Then(new SagaCharge("o"))
            .Then(new SagaReserve("o"))
            .Then(new SagaShip("o", Fail: true))
            .WithCompensation<RefundCharge>()     // registered first -> runs last
            .WithCompensation<ReleaseReserve>()   // registered second -> runs first
            .Build();

        var byWire = def.Members.ToDictionary(m => m.WireName);
        var shipId = byWire["saga-ship"].JobId;
        var releaseId = byWire["saga-release-reserve"].JobId;

        // The newer compensation guards the ship tip alone.
        Assert.Equal(DependencyMode.OnAnyTerminal, byWire["saga-release-reserve"].Mode);
        Assert.Equal([shipId], byWire["saga-release-reserve"].Parents);

        // The earlier compensation now also waits for the newer one (static reverse-order edge), so the
        // later-protected step's undo runs first.
        Assert.Equal(DependencyMode.OnAnyTerminal, byWire["saga-refund-charge"].Mode);
        Assert.Equal(
            new HashSet<Guid> { shipId, releaseId },
            byWire["saga-refund-charge"].Parents.ToHashSet());
    }

    // ── End-to-end through the in-memory harness ───────────────────────────────────

    [Fact]
    public async Task ProtectedStepFails_CompensationRunsTheUndo()
    {
        var h = NewHarness(out var recorder);

        await h.Client.Workflow()
            .Then(new SagaShip("o", Fail: true))
            .WithCompensation<CleanupShip>()
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Contains("ship", recorder.Ran);
        Assert.Contains("cleanup:undo", recorder.Ran);        // ran because the protected step failed
        Assert.DoesNotContain("cleanup:noop", recorder.Ran);
    }

    [Fact]
    public async Task ProtectedStepSucceeds_CompensationNoOps()
    {
        var h = NewHarness(out var recorder);

        await h.Client.Workflow()
            .Then(new SagaShip("o", Fail: false))
            .WithCompensation<CleanupShip>()
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Contains("ship", recorder.Ran);
        Assert.Contains("cleanup:noop", recorder.Ran);        // always ran, but no-op'd on success
        Assert.DoesNotContain("cleanup:undo", recorder.Ran);
    }

    [Fact]
    public async Task TwoStepChain_SecondStepFails_TriggersTheFirstStepsCompensation()
    {
        var h = NewHarness(out var recorder);

        // Charge succeeds, ship fails; the compensation guarding the chain refunds the charge.
        await h.Client.Workflow()
            .Then(new SagaCharge("o"))
            .Then(new SagaShip("o", Fail: true))
            .WithCompensation<RefundCharge>()
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Contains("charge", recorder.Ran);
        Assert.Contains("ship", recorder.Ran);
        Assert.Contains("refund:chg-o", recorder.Ran);        // the first step's committed work was undone
    }

    [Fact]
    public async Task TwoStepChain_WholeChainSucceeds_TheCompensationNoOps()
    {
        var h = NewHarness(out var recorder);

        await h.Client.Workflow()
            .Then(new SagaCharge("o"))
            .Then(new SagaShip("o", Fail: false))
            .WithCompensation<RefundCharge>()
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Contains("refund:noop", recorder.Ran);
        Assert.DoesNotContain("refund:chg-o", recorder.Ran);
    }

    [Fact]
    public async Task MultipleCompensations_UndoInReverseOrder_WhenTheSagaFails()
    {
        var h = NewHarness(out var recorder);

        await h.Client.Workflow()
            .Then(new SagaCharge("o"))
            .Then(new SagaReserve("o"))
            .Then(new SagaShip("o", Fail: true))
            .WithCompensation<RefundCharge>()     // undoes charge; runs last
            .WithCompensation<ReleaseReserve>()   // undoes reserve; runs first
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        // Both undos ran because the saga failed, and in reverse order of the work they protect:
        // the later-protected reservation is released before the earlier charge is refunded.
        var release = recorder.Ran.IndexOf("release:rsv-o");
        var refund = recorder.Ran.IndexOf("refund:chg-o");
        Assert.True(release >= 0, "release did not run");
        Assert.True(refund >= 0, "refund did not run");
        Assert.True(release < refund, $"expected release before refund; got {string.Join(",", recorder.Ran)}");
    }

    [Fact]
    public async Task Compensation_ComposesWithParallel_OverTheBranchTips()
    {
        var h = NewHarness(out var recorder);

        // A compensation over a parallel region fans in over both branch tips OnAnyTerminal; one branch
        // fails, and the undo runs.
        var id = await h.Client.Workflow()
            .Then(new SagaRoot("o"))
            .Parallel(
                WorkflowBranch.Step(new SagaBranchOk("o")),
                WorkflowBranch.Step(new SagaBranchBoom("o")))
            .WithCompensation<UndoParallel>()
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Contains("branch-ok", recorder.Ran);
        Assert.Contains("branch-boom", recorder.Ran);
        Assert.Contains("undo-parallel:undo", recorder.Ran);   // released and undid despite a failed branch
        Assert.Equal(WorkflowStatus.Failed, (await h.Monitor.GetWorkflowAsync(id))!.Status);
    }

    [Fact]
    public void Compensation_JoinsEveryParallelBranchTip_OnAnyTerminal()
    {
        var h = NewHarness(out _);

        var def = h.Client.Workflow()
            .Then(new SagaRoot("o"))
            .Parallel(
                WorkflowBranch.Step(new SagaBranchOk("o")),
                WorkflowBranch.Step(new SagaBranchBoom("o")))
            .WithCompensation<UndoParallel>()
            .Build();

        var byWire = def.Members.ToDictionary(m => m.WireName);
        Assert.Equal(DependencyMode.OnAnyTerminal, byWire["saga-undo-parallel"].Mode);
        Assert.Equal(
            new HashSet<Guid> { byWire["saga-branch-ok"].JobId, byWire["saga-branch-boom"].JobId },
            byWire["saga-undo-parallel"].Parents.ToHashSet());
    }

    // ── Validation ─────────────────────────────────────────────────────────────────

    [Fact]
    public void WithCompensation_WithNoPrecedingStep_Throws()
    {
        var h = NewHarness(out _);
        Assert.Throws<InvalidWorkflowException>(() =>
            h.Client.Workflow().WithCompensation<CleanupShip>());
    }
}
