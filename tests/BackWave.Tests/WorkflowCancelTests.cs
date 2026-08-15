using BackWave.Jobs;
using BackWave.Operations;
using BackWave.Storage;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

/// <summary>
/// The WorkflowCancel Operator Action (PRD 0006, issue 0118, ADR 0023): cancel a whole Workflow by
/// fanning the existing per-job Cancel out over its non-terminal members. A snapshot fan-out, not a
/// latch; gated by the existing Cancel Permission; already-terminal members untouched; the derived
/// status reads Cancelled (not Failed) because no member dead-letters. Reuses the
/// ChargeOrder/SendReceipt/ReleaseHold job types from <see cref="DependencyTests"/>.
/// </summary>
public class WorkflowCancelTests
{
    private static BackWaveHarness NewHarness()
    {
        var services = new ServiceCollection()
            .AddSingleton<DependencyRecorder>()
            .AddTransient<IJobHandler<ChargeOrder>, ChargeOrderHandler>()
            .AddTransient<IJobHandler<SendReceipt>, SendReceiptHandler>()
            .AddTransient<IJobHandler<ReleaseHold>, ReleaseHoldHandler>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<ChargeOrder, ChargeOrderHandler>(
                "charge-order", DependencyJsonContext.Default.ChargeOrder),
            JobRegistration.Create<SendReceipt, SendReceiptHandler>(
                "send-receipt", DependencyJsonContext.Default.SendReceipt),
            JobRegistration.Create<ReleaseHold, ReleaseHoldHandler>(
                "release-hold", DependencyJsonContext.Default.ReleaseHold),
        ]);
        return new BackWaveHarness(registry, services, new BackWaveHarnessOptions
        {
            RetryPolicy = new Core.RetryPolicy { MaxAttempts = 1 },
        });
    }

    [Fact]
    public async Task Cancel_MovesNonTerminalMembers_LeavesAlreadyTerminalUntouched()
    {
        var h = NewHarness();
        // Three independent members (all roots) so each cancels directly, with no latch cascade to
        // muddy which member the fan-out itself transitioned.
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var cId = Guid.NewGuid();
        var def = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = "flow",
            Members =
            [
                WorkflowGraphBuilder.Member(h.Client, aId, new ChargeOrder("o"), h.Now),
                WorkflowGraphBuilder.Member(h.Client, bId, new SendReceipt("o"), h.Now),
                WorkflowGraphBuilder.Member(h.Client, cId, new ReleaseHold("o"), h.Now),
            ],
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(def, h.Now));
        var id = def.WorkflowId;
        var t0 = h.Now;
        var t1 = h.Now.AddMinutes(1);

        // Pre-cancel "c" individually: it is already terminal when the Workflow cancel runs.
        await h.Store.CancelJobAsync(cId, "early", t0);

        var op = new BackWaveOperator(h.Store);
        var result = await op.CancelWorkflowAsync(id, "operator", t1);

        Assert.True(result.Found);
        Assert.Equal(2, result.CancelledImmediately);     // a and b, cancelled directly
        Assert.Equal(0, result.CancellationRequested);    // none were Leased

        var a = await h.Store.GetJobAsync(aId);
        var c = await h.Store.GetJobAsync(cId);
        Assert.Equal(JobState.Cancelled, a!.State);
        Assert.Equal("operator", a.TerminalCause);
        // The already-terminal member keeps its original cause and instant — untouched by the sweep.
        Assert.Equal(JobState.Cancelled, c!.State);
        Assert.Equal("early", c.TerminalCause);
        Assert.Equal(t0, c.TerminalAt);
    }

    [Fact]
    public async Task Cancel_NoFailures_DerivedStatusIsCancelledNotFailed()
    {
        var h = NewHarness();
        var chargeId = Guid.NewGuid();
        var def = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = "flow",
            Members =
            [
                WorkflowGraphBuilder.Member(h.Client, chargeId, new ChargeOrder("o"), h.Now),
                WorkflowGraphBuilder.Member(h.Client, Guid.NewGuid(), new SendReceipt("o"), h.Now, chargeId),
            ],
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(def, h.Now));
        var id = def.WorkflowId;

        var result = await new BackWaveOperator(h.Store).CancelWorkflowAsync(id, "operator", h.Now);

        Assert.True(result.Found);
        // No member dead-lettered, so the projection reads Cancelled — cleanly distinct from Failed.
        Assert.Equal(WorkflowStatus.Cancelled, (await h.Monitor.GetWorkflowAsync(id))!.Status);
    }

    [Fact]
    public async Task Cancel_RecordsPerMemberOperatorActions()
    {
        var h = NewHarness();
        var aId = Guid.NewGuid();
        var def = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = "flow",
            Members = [WorkflowGraphBuilder.Member(h.Client, aId, new ChargeOrder("o"), h.Now)],
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(def, h.Now));
        var id = def.WorkflowId;

        var op = new BackWaveOperator(h.Store);
        await op.CancelWorkflowAsync(id, "operator", h.Now);

        // The per-member cancel is recorded as a defined operator action against the member id —
        // never a raw row edit.
        var audit = await op.ListAuditRecordsAsync(aId.ToString());
        Assert.Contains(audit, r => r.Action == OperatorAction.Cancel && r.Actor == "operator");
    }

    [Fact]
    public async Task Cancel_LeasedMember_IsCooperative()
    {
        var h = NewHarness();
        var aId = Guid.NewGuid();
        var def = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = "flow",
            Members = [WorkflowGraphBuilder.Member(h.Client, aId, new ChargeOrder("o"), h.Now)],
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(def, h.Now));
        var id = def.WorkflowId;

        // Lease the root so it is executing when the Workflow cancel runs.
        var claimed = await h.Store.ClaimAsync(new ClaimRequest("w1", ["default"], 8, TimeSpan.FromMinutes(1), h.Now));
        Assert.Single(claimed);

        var result = await new BackWaveOperator(h.Store).CancelWorkflowAsync(id, "operator", h.Now);

        Assert.True(result.Found);
        Assert.Equal(0, result.CancelledImmediately);
        Assert.Equal(1, result.CancellationRequested);    // Leased member asked to stop cooperatively
        var member = await h.Store.GetJobAsync(aId);
        Assert.Equal(JobState.Leased, member!.State);      // still running until its handler observes the token
        Assert.True(member.CancelRequested);
    }

    [Fact]
    public async Task Cancel_UnknownWorkflow_ReturnsNotFound()
    {
        var h = NewHarness();
        var result = await new BackWaveOperator(h.Store).CancelWorkflowAsync(Guid.NewGuid(), "operator", h.Now);
        Assert.Same(WorkflowCancelResult.NotFound, result);
    }
}
