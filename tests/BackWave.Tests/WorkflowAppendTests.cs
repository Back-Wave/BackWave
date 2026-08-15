using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

/// <summary>
/// Append-only expansion (PRD 0006, issue 0117, ADR 0023): growing an existing Workflow by appending
/// NEW members whose Dependency edges may point at existing members, via the same atomic enqueue op.
/// Edges stay static per job — an existing member's dependencies are never rewritten — so this is as
/// dynamic as River/Hangfire, never a result-driven graph (ADR 0003). Reuses the
/// ChargeOrder/SendReceipt/ReleaseHold job types from <see cref="DependencyTests"/>.
/// </summary>
public class WorkflowAppendTests
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

    // A single-root "flow" workflow (one ChargeOrder member), returning its id and the root member's id.
    private static async Task<(Guid WorkflowId, Guid ChargeId)> CreateChargeFlow(BackWaveHarness h)
    {
        var chargeId = Guid.NewGuid();
        var def = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = "flow",
            Members = [WorkflowGraphBuilder.Member(h.Client, chargeId, new ChargeOrder("o1"), h.Now)],
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(def, h.Now));
        return (def.WorkflowId, chargeId);
    }

    [Fact]
    public async Task Append_NewMemberOnExistingMember_GraphShowsAddedNodeAndEdge()
    {
        var h = NewHarness();
        var (id, charge) = await CreateChargeFlow(h);

        // Append a new member depending on the existing "charge" member (by id via its parent set).
        var receipt = Guid.NewGuid();
        var append = new WorkflowDefinition
        {
            WorkflowId = id,
            Members = [WorkflowGraphBuilder.Member(h.Client, receipt, new SendReceipt("o1"), h.Now, charge)],
            IsAppend = true,
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(append, h.Now));

        var graph = await h.Monitor.GetWorkflowAsync(id);
        Assert.NotNull(graph);
        Assert.Equal(2, graph!.Members.Count);                       // the added node
        Assert.Contains(graph.Members, m => m.JobId == receipt);
        Assert.Contains(new WorkflowEdge(charge, receipt), graph.Edges); // the added edge
        // The new member carries the same Workflow scalar and gates on its still-running parent.
        var receiptJob = await h.Store.GetJobAsync(receipt);
        Assert.Equal(id, receiptJob!.WorkflowId);
        Assert.Equal(JobState.AwaitingParent, receiptJob.State);
    }

    [Fact]
    public async Task Append_ToDrainedWorkflow_ReopensStatusToRunning()
    {
        var h = NewHarness();
        var (id, charge) = await CreateChargeFlow(h);

        await h.AdvanceAsync(TimeSpan.Zero); // drain the single member to Succeeded
        Assert.Equal(WorkflowStatus.Succeeded, (await h.Monitor.GetWorkflowAsync(id))!.Status);

        // Appending live work to a drained Workflow legitimately reopens its derived status —
        // status is a pure projection of member states, never stored.
        var append = new WorkflowDefinition
        {
            WorkflowId = id,
            Members = [WorkflowGraphBuilder.Member(h.Client, Guid.NewGuid(), new SendReceipt("o"), h.Now, charge)],
            IsAppend = true,
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(append, h.Now));
        Assert.Equal(WorkflowStatus.Running, (await h.Monitor.GetWorkflowAsync(id))!.Status);
    }

    [Fact]
    public async Task Append_ParentOutsideWorkflow_RejectedAsContainmentViolation()
    {
        var h = NewHarness();
        var (id, _) = await CreateChargeFlow(h);
        var outsider = await h.Client.EnqueueAsync(new ChargeOrder("loose"), h.Now); // a job, but not a member

        var receipt = Guid.NewGuid();
        var append = new WorkflowDefinition
        {
            WorkflowId = id,
            Members = [WorkflowGraphBuilder.Member(h.Client, receipt, new SendReceipt("o"), h.Now, outsider)],
            IsAppend = true,
        };

        Assert.Equal(WorkflowEnqueueResult.ContainmentViolation, await h.Store.EnqueueWorkflowAsync(append, h.Now));
        Assert.Null(await h.Store.GetJobAsync(receipt));                // nothing inserted
        Assert.Single((await h.Monitor.GetWorkflowAsync(id))!.Members); // the Workflow is unchanged
    }

    [Fact]
    public async Task Append_DoesNotRewriteExistingMemberDependencies()
    {
        var h = NewHarness();
        // A two-member Workflow: charge (root) → receipt.
        var charge = Guid.NewGuid();
        var receipt = Guid.NewGuid();
        var create = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = "flow",
            Members =
            [
                WorkflowGraphBuilder.Member(h.Client, charge, new ChargeOrder("o"), h.Now),
                WorkflowGraphBuilder.Member(h.Client, receipt, new SendReceipt("o"), h.Now, charge),
            ],
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(create, h.Now));
        var id = create.WorkflowId;
        var edgesBefore = (await h.Monitor.GetWorkflowAsync(id))!.Edges.ToHashSet();

        // Append "notify" depending on "receipt". The only structural change is the new node + edge;
        // no member's existing dependencies are rewritten.
        var notify = Guid.NewGuid();
        var append = new WorkflowDefinition
        {
            WorkflowId = id,
            Members = [WorkflowGraphBuilder.Member(h.Client, notify, new ReleaseHold("o"), h.Now, receipt)],
            IsAppend = true,
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(append, h.Now));

        var edgesAfter = (await h.Monitor.GetWorkflowAsync(id))!.Edges.ToHashSet();
        // Every pre-existing edge survives verbatim and exactly one edge was added.
        Assert.Subset(edgesAfter, edgesBefore);
        Assert.Equal(edgesBefore.Count + 1, edgesAfter.Count);
        Assert.Contains(new WorkflowEdge(receipt, notify), edgesAfter);
        // charge keeps its single child (receipt); no edge now terminates at charge or rewrites receipt's parent.
        Assert.DoesNotContain(edgesAfter, e => e.Child == charge);
        Assert.Equal([charge], edgesAfter.Where(e => e.Child == receipt).Select(e => e.Parent));
    }

    [Fact]
    public async Task Append_IsAtomic_ForcedCollisionLeavesWorkflowUnchanged()
    {
        var h = NewHarness();
        var (id, charge) = await CreateChargeFlow(h);

        // An append batch: one valid new member plus one whose JobId collides with the existing root.
        // A non-atomic apply would leave the valid member orphaned; all-or-nothing must leave nothing.
        var good = new NewJob(Guid.NewGuid(), "send-receipt", default, "default", h.Now) { Parents = [charge] };
        var clashing = new NewJob(charge, "release-hold", default, "default", h.Now) { Parents = [charge] };
        var def = new WorkflowDefinition { WorkflowId = id, Members = [good, clashing], IsAppend = true };

        Assert.Equal(WorkflowEnqueueResult.DuplicateMember, await h.Store.EnqueueWorkflowAsync(def, h.Now));
        Assert.Null(await h.Store.GetJobAsync(good.JobId));             // the valid member was NOT inserted
        Assert.Single((await h.Monitor.GetWorkflowAsync(id))!.Members); // the Workflow still has just its root
    }

    [Fact]
    public async Task Append_ToNonExistentWorkflow_RejectedAsWorkflowNotFound()
    {
        var h = NewHarness();
        var orphan = Guid.NewGuid();
        var append = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Members = [WorkflowGraphBuilder.Member(h.Client, orphan, new ChargeOrder("o"), h.Now)],
            IsAppend = true,
        };

        Assert.Equal(WorkflowEnqueueResult.WorkflowNotFound, await h.Store.EnqueueWorkflowAsync(append, h.Now));
        Assert.Null(await h.Store.GetJobAsync(orphan)); // nothing inserted
    }

    [Fact]
    public async Task Append_WithinTransaction_CommitsAtomicallyWithCallerWrites()
    {
        var h = NewHarness();
        var (id, charge) = await CreateChargeFlow(h);

        var receipt = Guid.NewGuid();
        var def = new WorkflowDefinition
        {
            WorkflowId = id,
            Members = [WorkflowGraphBuilder.Member(h.Client, receipt, new SendReceipt("o"), h.Now, charge)],
            IsAppend = true,
        };

        // Rollback: the append is buffered, then discarded — never visible.
        using (var tx = h.BeginTransaction())
        {
            Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(def, h.Now, tx));
            Assert.Single((await h.Monitor.GetWorkflowAsync(id))!.Members); // invisible until commit
            tx.Rollback();
        }
        Assert.Single((await h.Monitor.GetWorkflowAsync(id))!.Members);
        Assert.Null(await h.Store.GetJobAsync(receipt));

        // Commit: the appended member appears atomically.
        using (var tx = h.BeginTransaction())
        {
            Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(def, h.Now, tx));
            tx.Commit();
        }
        Assert.Equal(2, (await h.Monitor.GetWorkflowAsync(id))!.Members.Count);
        Assert.Equal(id, (await h.Store.GetJobAsync(receipt))!.WorkflowId);
    }
}

/// <summary>
/// Append through the strongly-typed Pro front door (issue 0263): <c>client.WorkflowAppend(id)</c> grows an
/// existing workflow with new typed members whose dependencies may point at existing members via
/// <c>afterExisting</c>. Reuses the v2 step types registered elsewhere in the suite.
/// </summary>
public class AppendTypedClientTests
{
    private static BackWaveHarness NewHarness(out V2Recorder recorder)
    {
        var services = new ServiceCollection()
            .AddSingleton<V2Recorder>()
            .AddTransient<IJobHandler<ChargeStep>, ChargeStepHandler>()
            .AddTransient<IJobHandler<ReceiptStep>, ReceiptStepHandler>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<ChargeStep, ChargeStepHandler>("v2-charge", WorkflowsV2JsonContext.Default.ChargeStep),
            JobRegistration.Create<ReceiptStep, ReceiptStepHandler>("v2-receipt", WorkflowsV2JsonContext.Default.ReceiptStep),
        ]);
        recorder = services.GetRequiredService<V2Recorder>();
        return new BackWaveHarness(registry, services, new BackWaveHarnessOptions
        {
            RetryPolicy = new Core.RetryPolicy { MaxAttempts = 1 },
        });
    }

    [Fact]
    public async Task WorkflowAppend_TypedClientPath_AppendedStepRunsAfterAnExistingMember()
    {
        var h = NewHarness(out var recorder);

        // Create a one-step workflow through the typed client; capture the root member's id (Build is pure
        // and repeatable - ids are minted when the step is added - so the id it returns is the one enqueued).
        var create = h.Client.Workflow("append-flow").Then(new ChargeStep("o"));
        var chargeId = create.Build().Members.Single().JobId;
        var wfId = await create.EnqueueAsync();

        // Append a new member, through client.WorkflowAppend, that depends on the existing charge member.
        var append = h.Client.WorkflowAppend(wfId)
            .Then(new ReceiptStep("o"), after: Array.Empty<Type>(), afterExisting: [chargeId]);
        var receiptId = append.Build().Members.Single().JobId;
        await append.EnqueueAsync();

        // The append grafted exactly one node and one edge onto the existing workflow, under the same id.
        var graph = await h.Monitor.GetWorkflowAsync(wfId);
        Assert.NotNull(graph);
        Assert.Equal(2, graph!.Members.Count);
        Assert.Contains(graph.Members, m => m.JobId == receiptId);
        Assert.Contains(new WorkflowEdge(chargeId, receiptId), graph.Edges);
        Assert.Equal(wfId, (await h.Store.GetJobAsync(receiptId))!.WorkflowId);

        // End to end: the existing member runs, then the appended member releases and runs after it.
        await h.AdvanceAsync(TimeSpan.Zero);
        Assert.Equal(["charge", "receipt"], recorder.Ran);
    }
}
