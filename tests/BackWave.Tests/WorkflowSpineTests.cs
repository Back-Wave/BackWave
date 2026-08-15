using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

/// <summary>
/// The Workflow spine (PRD 0006, issue 0116, ADR 0023): the atomic enqueue Storage Contract op, the
/// status projection, and the Monitor read - all on the In-Memory Store. Members are built directly as
/// prepared <see cref="WorkflowDefinition"/> graphs (the byte-identical lowering of the above-boundary
/// authoring builder), keeping these tests on the storage contract. Reuses the
/// ChargeOrder/SendReceipt/ReleaseHold job types from <see cref="DependencyTests"/>.
/// </summary>
public class WorkflowSpineTests
{
    private static BackWaveHarness NewHarness() => NewHarness(out _);

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
        // One attempt: a failing job dead-letters immediately, so a single pump drives the cascade.
        return new BackWaveHarness(registry, services, new BackWaveHarnessOptions
        {
            RetryPolicy = new Core.RetryPolicy { MaxAttempts = 1 },
        });
    }

    // ── Status projection (exhaustive precedence table) ──────────────────────────

    [Fact]
    public void StatusProjection_PrecedenceTable()
    {
        // Running dominates: any non-terminal member.
        Assert.Equal(WorkflowStatus.Running, Project(JobState.Scheduled, JobState.Succeeded));
        Assert.Equal(WorkflowStatus.Running, Project(JobState.AwaitingParent, JobState.Leased));
        Assert.Equal(WorkflowStatus.Running, Project(JobState.Leased, JobState.DeadLettered)); // non-terminal beats failure

        // Failure dominates a fully-terminal set, even beside a Succeeded or Cancelled sibling.
        Assert.Equal(WorkflowStatus.Failed, Project(JobState.Succeeded, JobState.DeadLettered));
        Assert.Equal(WorkflowStatus.Failed, Project(JobState.Succeeded, JobState.Quarantined));
        Assert.Equal(WorkflowStatus.Failed, Project(JobState.Cancelled, JobState.DeadLettered));
        Assert.Equal(WorkflowStatus.Failed, Project(JobState.Succeeded, JobState.Cancelled, JobState.DeadLettered));

        // Cancelled: all terminal, no failures, at least one Cancelled (the operator-cancel shape).
        Assert.Equal(WorkflowStatus.Cancelled, Project(JobState.Succeeded, JobState.Cancelled));
        Assert.Equal(WorkflowStatus.Cancelled, Project(JobState.Cancelled, JobState.Cancelled));

        // Succeeded: every member Succeeded (and the vacuous empty case).
        Assert.Equal(WorkflowStatus.Succeeded, Project(JobState.Succeeded, JobState.Succeeded));
        Assert.Equal(WorkflowStatus.Succeeded, Project());

        static WorkflowStatus Project(params JobState[] states) => WorkflowStatusProjection.Project(states);
    }

    // ── Atomic enqueue + Monitor read (Storage Contract) ─────────────────────────

    [Fact]
    public async Task AtomicEnqueue_InsertsWholeGraphAndRow_MonitorReadsEdges()
    {
        var h = NewHarness();
        var chargeId = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        var notifyId = Guid.NewGuid();
        var def = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = "flow",
            Members =
            [
                WorkflowGraphBuilder.Member(h.Client, chargeId, new ChargeOrder("o1"), h.Now),
                WorkflowGraphBuilder.Member(h.Client, receiptId, new SendReceipt("o1"), h.Now, chargeId),
                WorkflowGraphBuilder.Member(h.Client, notifyId, new ReleaseHold("o1"), h.Now, chargeId),
            ],
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(def, h.Now));
        var id = def.WorkflowId;

        // Every member committed, carrying the Workflow scalar; the root is Scheduled, children await.
        var charge = await h.Store.GetJobAsync(chargeId);
        var receipt = await h.Store.GetJobAsync(receiptId);
        Assert.Equal(id, charge!.WorkflowId);
        Assert.Equal(id, receipt!.WorkflowId);
        Assert.Equal(JobState.Scheduled, charge.State);
        Assert.Equal(JobState.AwaitingParent, receipt.State);

        var graph = await h.Monitor.GetWorkflowAsync(id);
        Assert.NotNull(graph);
        Assert.Equal("flow", graph!.Name);
        Assert.Equal(WorkflowStatus.Running, graph.Status);
        Assert.Equal(3, graph.Members.Count);
        Assert.Equal(
            new HashSet<WorkflowEdge>
            {
                new(chargeId, receiptId),
                new(chargeId, notifyId),
            },
            graph.Edges.ToHashSet());
    }

    [Fact]
    public async Task ContainmentViolation_RejectedAtEnqueue_InsertsNothing()
    {
        var h = NewHarness();
        var outsider = Guid.NewGuid(); // a parent that is NOT a member of the Workflow
        var member = new NewJob(Guid.NewGuid(), "send-receipt", default, "default", h.Now)
        {
            Parents = [outsider],
        };
        var def = new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [member] };

        var result = await h.Store.EnqueueWorkflowAsync(def, h.Now);

        Assert.Equal(WorkflowEnqueueResult.ContainmentViolation, result);
        Assert.Null(await h.Store.GetJobAsync(member.JobId));
        Assert.Null(await h.Monitor.GetWorkflowAsync(def.WorkflowId));
    }

    [Fact]
    public async Task ForcedMidInsertFailure_LeavesNoMembersAndNoRow()
    {
        var h = NewHarness();
        // A pre-existing job whose id collides with the SECOND member: the first member is valid,
        // so a non-atomic insert would leave it orphaned. All-or-nothing must leave nothing.
        var collision = await h.Client.EnqueueAsync(new ChargeOrder("pre"), h.Now);
        var good = new NewJob(Guid.NewGuid(), "charge-order", default, "default", h.Now);
        var clashing = new NewJob(collision, "send-receipt", default, "default", h.Now);
        var def = new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [good, clashing] };

        var result = await h.Store.EnqueueWorkflowAsync(def, h.Now);

        Assert.Equal(WorkflowEnqueueResult.DuplicateMember, result);
        Assert.Null(await h.Store.GetJobAsync(good.JobId));       // the valid member was NOT inserted
        Assert.Null(await h.Monitor.GetWorkflowAsync(def.WorkflowId)); // and no Workflows row
    }

    [Fact]
    public async Task TransactionalEnqueue_RollbackLeavesNothing_CommitMakesGraphVisible()
    {
        var h = NewHarness();
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var def = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = "tx",
            Members =
            [
                WorkflowGraphBuilder.Member(h.Client, aId, new ChargeOrder("o"), h.Now),
                WorkflowGraphBuilder.Member(h.Client, bId, new SendReceipt("o"), h.Now, aId),
            ],
        };

        // Rollback: buffered then discarded — never visible.
        using (var tx = h.BeginTransaction())
        {
            Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(def, h.Now, tx));
            Assert.Null(await h.Monitor.GetWorkflowAsync(def.WorkflowId)); // invisible until commit
            tx.Rollback();
        }
        Assert.Null(await h.Monitor.GetWorkflowAsync(def.WorkflowId));
        Assert.Null(await h.Store.GetJobAsync(aId));

        // Commit: the whole graph appears atomically.
        using (var tx = h.BeginTransaction())
        {
            Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(def, h.Now, tx));
            tx.Commit();
        }
        Assert.NotNull(await h.Monitor.GetWorkflowAsync(def.WorkflowId));
        Assert.Equal(def.WorkflowId, (await h.Store.GetJobAsync(aId))!.WorkflowId);
    }

    [Fact]
    public async Task ListWorkflows_OrderedByCreatedAt()
    {
        var h = NewHarness();
        var firstDef = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = "first",
            Members = [WorkflowGraphBuilder.Member(h.Client, Guid.NewGuid(), new ChargeOrder("1"), h.Now)],
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(firstDef, h.Now));
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));
        var secondDef = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = "second",
            Members = [WorkflowGraphBuilder.Member(h.Client, Guid.NewGuid(), new ChargeOrder("2"), h.Now)],
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(secondDef, h.Now));

        var list = await h.Monitor.ListWorkflowsAsync();
        Assert.Equal([firstDef.WorkflowId, secondDef.WorkflowId], list.Select(w => w.WorkflowId));
    }

    [Fact]
    public async Task DuplicateWorkflowId_Rejected()
    {
        var h = NewHarness();
        var def = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = "w",
            Members = [WorkflowGraphBuilder.Member(h.Client, Guid.NewGuid(), new ChargeOrder("o"), h.Now)],
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(def, h.Now));
        Assert.Equal(WorkflowEnqueueResult.DuplicateWorkflow, await h.Store.EnqueueWorkflowAsync(def, h.Now));
    }

    [Fact]
    public async Task PlainEnqueuedJob_HasNoWorkflowId()
    {
        var h = NewHarness();
        var jobId = await h.Client.EnqueueAsync(new ChargeOrder("solo"), h.Now);
        Assert.Null((await h.Store.GetJobAsync(jobId))!.WorkflowId);
    }

    [Fact]
    public async Task WorkflowStatus_FailureDominates_EndToEnd()
    {
        var h = NewHarness(out var recorder);
        recorder.ChargeFails = true; // the charge step will dead-letter

        // charge → receipt (on-success): charge fails ⇒ dead-lettered, receipt cancelled by cascade.
        var chargeId = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        var def = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = "billing",
            Members =
            [
                WorkflowGraphBuilder.Member(h.Client, chargeId, new ChargeOrder("o"), h.Now),
                WorkflowGraphBuilder.Member(h.Client, receiptId, new SendReceipt("o"), h.Now, chargeId),
            ],
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(def, h.Now));

        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Equal(JobState.DeadLettered, (await h.Store.GetJobAsync(chargeId))!.State);
        Assert.Equal(JobState.Cancelled, (await h.Store.GetJobAsync(receiptId))!.State);
        // Failure dominates: a Dead-Lettered member makes the whole Workflow Failed, not Cancelled.
        Assert.Equal(WorkflowStatus.Failed, (await h.Monitor.GetWorkflowAsync(def.WorkflowId))!.Status);
    }

    // ── Immutability ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task WorkflowId_SurvivesStateTransitions()
    {
        var h = NewHarness();
        var aId = Guid.NewGuid();
        var def = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = "w",
            Members = [WorkflowGraphBuilder.Member(h.Client, aId, new ChargeOrder("o"), h.Now)],
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await h.Store.EnqueueWorkflowAsync(def, h.Now));

        await h.AdvanceAsync(TimeSpan.Zero); // run the root to terminal
        var job = await h.Store.GetJobAsync(aId);
        Assert.Equal(JobState.Succeeded, job!.State);
        Assert.Equal(def.WorkflowId, job.WorkflowId); // the scalar is unchanged after the transition
    }
}
