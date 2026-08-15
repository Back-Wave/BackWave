using System.Text.Json.Serialization;
using BackWave.Jobs;
using BackWave.Pro;
using BackWave.Storage;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

// ── Steps, seeds, handlers, and child definitions for child-workflow splicing (0268) ───────

/// <summary>Records the order child-workflow steps ran.</summary>
public sealed class ChildRecorder
{
    public List<string> Ran { get; } = [];
}

public sealed record ChildSeed(string Region);

public sealed record ParentAStep(string Note) : IWorkflowStep;
public sealed record ChildBStep(string Note) : IWorkflowStep;
public sealed record ChildCStep(string Note) : IWorkflowStep;
public sealed record ParentDStep(string Note) : IWorkflowStep;
public sealed record ChildBoomStep(string Note) : IWorkflowStep;
public sealed record IndependentStep(string Note) : IWorkflowStep;

public sealed class ParentAStepHandler(ChildRecorder recorder) : IJobHandler<ParentAStep>
{
    public Task HandleAsync(ParentAStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("a");
        return Task.CompletedTask;
    }
}

public sealed class ChildBStepHandler(ChildRecorder recorder) : IJobHandler<ChildBStep>
{
    public Task HandleAsync(ChildBStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add($"b:{job.Note}");
        return Task.CompletedTask;
    }
}

public sealed class ChildCStepHandler(ChildRecorder recorder) : IJobHandler<ChildCStep>
{
    public Task HandleAsync(ChildCStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add($"c:{job.Note}");
        return Task.CompletedTask;
    }
}

public sealed class ParentDStepHandler(ChildRecorder recorder) : IJobHandler<ParentDStep>
{
    public Task HandleAsync(ParentDStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("d");
        return Task.CompletedTask;
    }
}

public sealed class ChildBoomStepHandler(ChildRecorder recorder) : IJobHandler<ChildBoomStep>
{
    public Task HandleAsync(ChildBoomStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("boom");
        throw new InvalidOperationException("child step failed on purpose");
    }
}

public sealed class IndependentStepHandler(ChildRecorder recorder) : IJobHandler<IndependentStep>
{
    public Task HandleAsync(IndependentStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("independent");
        return Task.CompletedTask;
    }
}

[JsonSerializable(typeof(ChildSeed))]
[JsonSerializable(typeof(ParentAStep))]
[JsonSerializable(typeof(ChildBStep))]
[JsonSerializable(typeof(ChildCStep))]
[JsonSerializable(typeof(ParentDStep))]
[JsonSerializable(typeof(ChildBoomStep))]
[JsonSerializable(typeof(IndependentStep))]
internal sealed partial class WorkflowsV2ChildJsonContext : JsonSerializerContext;

/// <summary>A child definition: two linear steps whose payloads are shaped by the build-time seed.</summary>
public sealed class ShippingWorkflow : IWorkflow<ChildSeed>
{
    public void Build(TypedWorkflowBuilder builder, ChildSeed seed)
        => builder.Then(new ChildBStep(seed.Region)).Then(new ChildCStep(seed.Region));
}

/// <summary>A child whose second step always fails, to prove failure fails the parent graph.</summary>
public sealed class FailingChildWorkflow : IWorkflow<ChildSeed>
{
    public void Build(TypedWorkflowBuilder builder, ChildSeed seed)
        => builder.Then(new ChildBStep(seed.Region)).Then(new ChildBoomStep(seed.Region));
}

/// <summary>A child whose Build adds no step - splicing it must be rejected, not a silent no-op.</summary>
public sealed class EmptyChildWorkflow : IWorkflow<ChildSeed>
{
    public void Build(TypedWorkflowBuilder builder, ChildSeed seed)
    {
        // Intentionally empty.
    }
}

/// <summary>
/// Child workflows via inline splice (issue 0268): <c>.ThenWorkflow&lt;TChild, TSeed&gt;</c> grafts the
/// child definition's steps onto the parent graph at the current frontier, producing one flat graph, one
/// workflow row, and one derived status - with the fire-and-forget <c>StartWorkflow</c> escape hatch left
/// intact for the independent-identity case.
/// </summary>
public class WorkflowsV2ChildTests
{
    private static BackWaveHarness NewHarness(out ChildRecorder recorder)
    {
        var services = new ServiceCollection()
            .AddSingleton<ChildRecorder>()
            .AddTransient<IJobHandler<ParentAStep>, ParentAStepHandler>()
            .AddTransient<IJobHandler<ChildBStep>, ChildBStepHandler>()
            .AddTransient<IJobHandler<ChildCStep>, ChildCStepHandler>()
            .AddTransient<IJobHandler<ParentDStep>, ParentDStepHandler>()
            .AddTransient<IJobHandler<ChildBoomStep>, ChildBoomStepHandler>()
            .AddTransient<IJobHandler<IndependentStep>, IndependentStepHandler>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<ParentAStep, ParentAStepHandler>("child-a", WorkflowsV2ChildJsonContext.Default.ParentAStep),
            JobRegistration.Create<ChildBStep, ChildBStepHandler>("child-b", WorkflowsV2ChildJsonContext.Default.ChildBStep),
            JobRegistration.Create<ChildCStep, ChildCStepHandler>("child-c", WorkflowsV2ChildJsonContext.Default.ChildCStep),
            JobRegistration.Create<ParentDStep, ParentDStepHandler>("child-d", WorkflowsV2ChildJsonContext.Default.ParentDStep),
            JobRegistration.Create<ChildBoomStep, ChildBoomStepHandler>("child-boom", WorkflowsV2ChildJsonContext.Default.ChildBoomStep),
            JobRegistration.Create<IndependentStep, IndependentStepHandler>("child-independent", WorkflowsV2ChildJsonContext.Default.IndependentStep),
        ]);
        recorder = services.GetRequiredService<ChildRecorder>();
        return new BackWaveHarness(registry, services, new BackWaveHarnessOptions
        {
            RetryPolicy = new Core.RetryPolicy { MaxAttempts = 1 },
        });
    }

    // ── Lowering seam (pure module) ────────────────────────────────────────────────

    [Fact]
    public void ThenWorkflow_SplicesChildStepsAtFrontier_AndTheTipsBecomeTheNewFrontier()
    {
        var h = NewHarness(out _);

        var def = h.Client.Workflow("outer")
            .Then(new ParentAStep("a"))                                       // parent root
            .ThenWorkflow<ShippingWorkflow, ChildSeed>(new ChildSeed("eu"))    // splice B -> C onto A
            .Then(new ParentDStep("d"))                                       // chains on the child's tip (C)
            .Build();

        // One flat graph: parent A + child B, C + parent D = four members, one definition.
        Assert.Equal(4, def.Members.Count);
        var byWire = def.Members.ToDictionary(m => m.WireName);
        var aId = byWire["child-a"].JobId;
        var bId = byWire["child-b"].JobId;
        var cId = byWire["child-c"].JobId;

        Assert.Empty(byWire["child-a"].Parents);              // A is the root
        Assert.Equal([aId], byWire["child-b"].Parents);       // child root depends on the parent frontier (A)
        Assert.Equal([bId], byWire["child-c"].Parents);       // child's internal edge preserved
        Assert.Equal([cId], byWire["child-d"].Parents);       // D chains on the child's tip (C)
    }

    [Fact]
    public void ThenWorkflow_ChildThatAddsNoStep_Throws()
    {
        var h = NewHarness(out _);

        // A child whose Build adds nothing would splice as a silent no-op that leaves the frontier
        // unchanged; reject it the way Parallel/If reject an empty arm.
        var ex = Assert.Throws<InvalidWorkflowException>(() =>
            h.Client.Workflow()
                .Then(new ParentAStep("a"))
                .ThenWorkflow<EmptyChildWorkflow, ChildSeed>(new ChildSeed("eu")));
        Assert.Contains("added no step", ex.Message);
    }

    // ── End-to-end through the in-memory harness ───────────────────────────────────

    [Fact]
    public async Task ThenWorkflow_ProducesOneWorkflowRow_WithEveryStepAsAMember()
    {
        var h = NewHarness(out var recorder);

        var id = await h.Client.Workflow("outer")
            .Then(new ParentAStep("a"))
            .ThenWorkflow<ShippingWorkflow, ChildSeed>(new ChildSeed("eu"))
            .Then(new ParentDStep("d"))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        var graph = await h.Monitor.GetWorkflowAsync(id);
        Assert.NotNull(graph);
        Assert.Equal("outer", graph!.Name);
        Assert.Equal(4, graph.Members.Count);                 // a single row containing every spliced step
        Assert.Equal(WorkflowStatus.Succeeded, graph.Status); // one derived status over the flat graph
        // Ran in dependency order: parent A, child B then C, parent D last.
        Assert.Equal(["a", "b:eu", "c:eu", "d"], recorder.Ran);
    }

    [Fact]
    public async Task FailingChildStep_FailsTheParentWorkflow_LikeAnyInGraphStep()
    {
        var h = NewHarness(out var recorder);

        var id = await h.Client.Workflow("outer")
            .Then(new ParentAStep("a"))
            .ThenWorkflow<FailingChildWorkflow, ChildSeed>(new ChildSeed("eu")) // B -> Boom (Boom dead-letters)
            .Then(new ParentDStep("d"))                                         // cancelled by the cascade
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        // The child's failing step is an ordinary in-graph failure: the whole (single) workflow is Failed.
        Assert.Equal(WorkflowStatus.Failed, (await h.Monitor.GetWorkflowAsync(id))!.Status);
        Assert.Contains("boom", recorder.Ran);
        Assert.DoesNotContain("d", recorder.Ran); // downstream parent step never ran
    }

    // ── The independent-identity escape hatch still works ──────────────────────────

    [Fact]
    public async Task StartWorkflow_FireAndForget_RemainsTheIndependentIdentityEscapeHatch()
    {
        var h = NewHarness(out var recorder);

        var id = await h.Client.StartWorkflow<ShippingWorkflow, ChildSeed>(
            new ChildSeed("us"), WorkflowsV2ChildJsonContext.Default.ChildSeed);
        await h.AdvanceAsync(TimeSpan.Zero);

        // Started under its own workflow identity, independent of any parent graph.
        var graph = await h.Monitor.GetWorkflowAsync(id);
        Assert.NotNull(graph);
        Assert.Equal(2, graph!.Members.Count);
        Assert.Equal(WorkflowStatus.Succeeded, graph.Status);
        Assert.Equal(["b:us", "c:us"], recorder.Ran);
    }
}
