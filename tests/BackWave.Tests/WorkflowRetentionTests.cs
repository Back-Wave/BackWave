using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

/// <summary>
/// Workflow-aware retention (PRD 0006, issue 0119, ADR 0023, §5.11): a Workflow's members are
/// retained <b>as a unit</b> — none becomes purge-eligible until the whole Workflow drains (all
/// members terminal), and then the retention window starts from the <b>drain point</b> (max member
/// TerminalAt). Non-workflow jobs keep today's per-job rule. Reuses the
/// ChargeOrder/SendReceipt/ReleaseHold job types from <see cref="DependencyTests"/>.
/// </summary>
public class WorkflowRetentionTests
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

    private const int Lots = 1000;
    private static readonly DateTimeOffset FarFuture = DateTimeOffset.MaxValue;

    [Fact]
    public async Task NoMemberPurged_WhileAnySiblingNonTerminal()
    {
        var h = NewHarness();
        // Two independent members so one can be terminal while the other stays live.
        var aId = Guid.NewGuid();
        var def = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = "flow",
            Members =
            [
                WorkflowGraphBuilder.Member(h.Client, aId, new ChargeOrder("o"), h.Now),
                WorkflowGraphBuilder.Member(h.Client, Guid.NewGuid(), new SendReceipt("o"), h.Now),
            ],
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(def, h.Now));
        await h.Store.CancelJobAsync(aId, "op", h.Now); // a terminal, b still Scheduled

        // Even with an unbounded window, the drained-as-a-unit rule keeps the terminal member while
        // its sibling is live.
        var purged = await h.Store.PurgeTerminalAsync(TerminalStateClass.SucceededOrCancelled, FarFuture, Lots);

        Assert.Equal(0, purged);
        Assert.NotNull(await h.Store.GetJobAsync(aId));
    }

    [Fact]
    public async Task OnceDrained_EligibleAsAUnit_WindowStartsAtDrainPoint()
    {
        var h = NewHarness();
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var def = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = "flow",
            Members =
            [
                WorkflowGraphBuilder.Member(h.Client, aId, new ChargeOrder("o"), h.Now),
                WorkflowGraphBuilder.Member(h.Client, bId, new SendReceipt("o"), h.Now),
            ],
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(def, h.Now));
        var t0 = h.Now;
        var drain = h.Now.AddMinutes(10);
        await h.Store.CancelJobAsync(aId, "op", t0);    // first member terminal at t0
        await h.Store.CancelJobAsync(bId, "op", drain); // drain point is the LATER instant

        // A window that already covers a's own terminal instant but NOT the drain point purges
        // nothing — the unit's window starts at the drain point, not each member's terminal time.
        Assert.Equal(0, await h.Store.PurgeTerminalAsync(
            TerminalStateClass.SucceededOrCancelled, t0.AddMinutes(5), Lots));
        Assert.NotNull(await h.Store.GetJobAsync(aId));

        // A window reaching the drain point purges the whole unit together.
        Assert.Equal(2, await h.Store.PurgeTerminalAsync(
            TerminalStateClass.SucceededOrCancelled, drain, Lots));
        Assert.Null(await h.Store.GetJobAsync(aId));
        Assert.Null(await h.Store.GetJobAsync(bId));
    }

    [Fact]
    public async Task NonWorkflowJobs_RetainExactlyAsBefore()
    {
        var h = NewHarness();
        var jobId = await h.Client.EnqueueAsync(new ChargeOrder("solo"), h.Now);
        await h.Store.CancelJobAsync(jobId, "op", h.Now); // plain cancelled job, no WorkflowId

        // Per-job rule intact: a non-workflow job is eligible the instant its own TerminalAt is in window.
        var purged = await h.Store.PurgeTerminalAsync(TerminalStateClass.SucceededOrCancelled, h.Now, Lots);

        Assert.Equal(1, purged);
        Assert.Null(await h.Store.GetJobAsync(jobId));
    }

    [Fact]
    public async Task WorkflowRow_Dropped_WhenLastMemberPurged()
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
        await h.Store.CancelJobAsync(aId, "op", h.Now);

        Assert.Equal(1, await h.Store.PurgeTerminalAsync(TerminalStateClass.SucceededOrCancelled, FarFuture, Lots));
        // The now-orphaned Workflows row + structural edges are dropped with the last member.
        Assert.Null(await h.Monitor.GetWorkflowAsync(id));
        Assert.DoesNotContain(await h.Monitor.ListWorkflowsAsync(), w => w.WorkflowId == id);
    }

    [Fact]
    public async Task DrainedWorkflow_PurgedAcrossBothTerminalClasses()
    {
        var h = NewHarness(out var recorder);
        recorder.ChargeFails = true; // "charge" dead-letters; "settle" we cancel into Succeeded class

        var chargeId = Guid.NewGuid();
        var settleId = Guid.NewGuid();
        var def = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = "flow",
            Members =
            [
                WorkflowGraphBuilder.Member(h.Client, chargeId, new ChargeOrder("o"), h.Now),  // will dead-letter
                WorkflowGraphBuilder.Member(h.Client, settleId, new SendReceipt("o"), h.Now),  // independent; we cancel it
            ],
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(def, h.Now));
        await h.Store.CancelJobAsync(settleId, "op", h.Now);
        await h.AdvanceAsync(TimeSpan.Zero); // run charge to DeadLettered — the Workflow is now drained

        // A SucceededOrCancelled pass takes the cancelled member; the failed member survives that pass.
        Assert.Equal(1, await h.Store.PurgeTerminalAsync(TerminalStateClass.SucceededOrCancelled, FarFuture, Lots));
        Assert.Null(await h.Store.GetJobAsync(settleId));
        Assert.NotNull(await h.Store.GetJobAsync(chargeId));

        // The DeadLetteredOrQuarantined pass takes the rest; the Workflow row is then gone.
        Assert.Equal(1, await h.Store.PurgeTerminalAsync(TerminalStateClass.DeadLetteredOrQuarantined, FarFuture, Lots));
        Assert.Null(await h.Store.GetJobAsync(chargeId));
    }

    private static BackWaveHarness NewHarness(out DependencyRecorder recorder)
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
        recorder = services.GetRequiredService<DependencyRecorder>();
        return new BackWaveHarness(registry, services, new BackWaveHarnessOptions
        {
            RetryPolicy = new Core.RetryPolicy { MaxAttempts = 1 },
        });
    }
}
