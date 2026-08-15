using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

/// <summary>
/// Restart (PRD 0006, issue 0122, ADR 0023): re-instantiate a Workflow's definition as a brand-new
/// Workflow with fresh job identities — <b>full-redo, not resume/retry</b> (ADR 0024 out of scope).
/// Covers re-running the builder, the <see cref="WorkflowDefinition.RestartAsNew"/> helper, the
/// optional <c>RestartedFrom</c> lineage pointer, and that no terminal state is reanimated. Reuses
/// the ChargeOrder/SendReceipt/ReleaseHold job types from <see cref="DependencyTests"/>.
/// </summary>
public class WorkflowRestartTests
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

    private static WorkflowDefinition ThreeNodeFlow(BackWaveHarness h)
    {
        var charge = Guid.NewGuid();
        return new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = "flow",
            Members =
            [
                WorkflowGraphBuilder.Member(h.Client, charge, new ChargeOrder("o"), h.Now),
                WorkflowGraphBuilder.Member(h.Client, Guid.NewGuid(), new SendReceipt("o"), h.Now, charge),
                WorkflowGraphBuilder.Member(h.Client, Guid.NewGuid(), new ReleaseHold("o"), h.Now, charge),
            ],
        };
    }

    [Fact]
    public async Task RestartAsNew_FreshIdentities_IdenticalShape_WithLineage()
    {
        var h = NewHarness();
        var original = ThreeNodeFlow(h);
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(original, h.Now));

        var restart = original.RestartAsNew();

        // Fresh Workflow identity, lineage recorded, members all re-keyed.
        Assert.NotEqual(original.WorkflowId, restart.WorkflowId);
        Assert.Equal(original.WorkflowId, restart.RestartedFrom);
        Assert.Null(original.RestartedFrom);
        Assert.False(restart.IsAppend);
        var originalIds = original.Members.Select(m => m.JobId).ToHashSet();
        Assert.All(restart.Members, m => Assert.DoesNotContain(m.JobId, originalIds));

        // Identical shape: members correspond by order, and each member's parents are exactly the
        // originals' parents mapped through the same fresh-id remap.
        Assert.Equal(original.Members.Count, restart.Members.Count);
        var map = original.Members.Zip(restart.Members).ToDictionary(p => p.First.JobId, p => p.Second.JobId);
        foreach (var (before, after) in original.Members.Zip(restart.Members))
        {
            Assert.Equal(before.WireName, after.WireName); // same job types in the same positions
            Assert.Equal(
                before.Parents.Select(p => map[p]).ToHashSet(),
                after.Parents.ToHashSet());
        }
    }

    [Fact]
    public async Task RestartAsNew_EnqueuesAlongsideOriginal_BothCoexist()
    {
        var h = NewHarness();
        var original = ThreeNodeFlow(h);
        var originalId = original.WorkflowId;
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(original, h.Now));

        var restart = original.RestartAsNew();
        var restartId = restart.WorkflowId;
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(restart, h.Now));

        var originalGraph = await h.Monitor.GetWorkflowAsync(originalId);
        var restartGraph = await h.Monitor.GetWorkflowAsync(restartId);
        Assert.NotNull(originalGraph);
        Assert.NotNull(restartGraph);
        Assert.Null(originalGraph!.RestartedFrom);
        Assert.Equal(originalId, restartGraph!.RestartedFrom);            // lineage visible through the Monitor
        Assert.Equal(originalGraph.Edges.Count, restartGraph.Edges.Count); // same shape
        // The listing carries lineage too.
        var listed = await h.Monitor.ListWorkflowsAsync();
        Assert.Equal(originalId, listed.Single(w => w.WorkflowId == restartId).RestartedFrom);
    }

    [Fact]
    public async Task Restart_IsFullRedo_RerunsSucceededStepsWithoutReanimatingOriginal()
    {
        var h = NewHarness();
        var original = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = "flow",
            Members = [WorkflowGraphBuilder.Member(h.Client, Guid.NewGuid(), new ChargeOrder("o"), h.Now)],
        };
        var originalId = original.WorkflowId;
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(original, h.Now));

        await h.AdvanceAsync(TimeSpan.Zero); // drive the original to Succeeded
        Assert.Equal(WorkflowStatus.Succeeded, (await h.Monitor.GetWorkflowAsync(originalId))!.Status);

        var restart = original.RestartAsNew();
        var restartId = restart.WorkflowId;
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(restart, h.Now));

        // Full-redo: the restarted member is Scheduled to run again from the start...
        Assert.Equal(JobState.Scheduled, (await h.Store.GetJobAsync(restart.Members.Single().JobId))!.State);
        Assert.Equal(WorkflowStatus.Running, (await h.Monitor.GetWorkflowAsync(restartId))!.Status);
        // ...and the original's terminal member is untouched — no terminal-state reanimation (ADR 0024).
        Assert.Equal(JobState.Succeeded, (await h.Store.GetJobAsync(original.Members.Single().JobId))!.State);
        Assert.Equal(WorkflowStatus.Succeeded, (await h.Monitor.GetWorkflowAsync(originalId))!.Status);
    }

    [Fact]
    public async Task ReRunningTheBuilder_ProducesFreshWorkflow_WithNoLineage()
    {
        var h = NewHarness();
        // Re-executing the building code (not the RestartAsNew helper) is the primary Restart pattern:
        // the application already holds the definition. Each build yields fresh identities.
        var first = ThreeNodeFlow(h);
        var second = ThreeNodeFlow(h);
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(first, h.Now));
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(second, h.Now));

        Assert.NotEqual(first.WorkflowId, second.WorkflowId);
        Assert.Null(second.RestartedFrom); // re-running the builder carries no lineage (the helper does)
        Assert.Empty(first.Members.Select(m => m.JobId).Intersect(second.Members.Select(m => m.JobId)));
        Assert.NotNull(await h.Monitor.GetWorkflowAsync(first.WorkflowId));
        Assert.NotNull(await h.Monitor.GetWorkflowAsync(second.WorkflowId));
    }
}
