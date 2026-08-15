using System.Text.Json;
using System.Text.Json.Serialization;
using BackWave.Jobs;
using BackWave.Pro;
using BackWave.Storage;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

// ── Steps, seed, handlers, and a recorder for the v2 typed builder (0263) ──────────────

/// <summary>Records the order steps ran and any Workflow Input a handler observed.</summary>
public sealed class V2Recorder
{
    public List<string> Ran { get; } = [];
    public string? SeenInput { get; set; }
}

public sealed record V2Seed(string OrderId);

public sealed record ChargeStep(string Note) : IWorkflowStep;
public sealed record ReceiptStep(string Note) : IWorkflowStep;
public sealed record NotifyStep(string Note) : IWorkflowStep;
public sealed record CloseStep(string Note) : IWorkflowStep;
public sealed record InputEchoStep(string Note) : IWorkflowStep;
public sealed record FailStep(string Note) : IWorkflowStep;

/// <summary>A step whose own payload declares a property in the reserved <c>$backwave.</c> namespace.</summary>
public sealed record ReservedKeyStep([property: JsonPropertyName("$backwave.reserved")] string Reserved) : IWorkflowStep;

public sealed class ChargeStepHandler(V2Recorder recorder) : IJobHandler<ChargeStep>
{
    public Task HandleAsync(ChargeStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("charge");
        return Task.CompletedTask;
    }
}

public sealed class InputEchoStepHandler(V2Recorder recorder) : IJobHandler<InputEchoStep>
{
    public Task HandleAsync(InputEchoStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("echo");
        recorder.SeenInput = context.Input(WorkflowsV2JsonContext.Default.V2Seed).OrderId;
        return Task.CompletedTask;
    }
}

public sealed class ReceiptStepHandler(V2Recorder recorder) : IJobHandler<ReceiptStep>
{
    public Task HandleAsync(ReceiptStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("receipt");
        return Task.CompletedTask;
    }
}

public sealed class NotifyStepHandler(V2Recorder recorder) : IJobHandler<NotifyStep>
{
    public Task HandleAsync(NotifyStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("notify");
        return Task.CompletedTask;
    }
}

public sealed class CloseStepHandler(V2Recorder recorder) : IJobHandler<CloseStep>
{
    public Task HandleAsync(CloseStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("close");
        return Task.CompletedTask;
    }
}

public sealed class FailStepHandler(V2Recorder recorder) : IJobHandler<FailStep>
{
    public Task HandleAsync(FailStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("fail");
        throw new InvalidOperationException("this step always fails");
    }
}

public sealed class ReservedKeyStepHandler(V2Recorder recorder) : IJobHandler<ReservedKeyStep>
{
    public Task HandleAsync(ReservedKeyStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("reserved");
        return Task.CompletedTask;
    }
}

[JsonSerializable(typeof(V2Seed))]
[JsonSerializable(typeof(ChargeStep))]
[JsonSerializable(typeof(ReceiptStep))]
[JsonSerializable(typeof(NotifyStep))]
[JsonSerializable(typeof(CloseStep))]
[JsonSerializable(typeof(InputEchoStep))]
[JsonSerializable(typeof(FailStep))]
[JsonSerializable(typeof(ReservedKeyStep))]
internal sealed partial class WorkflowsV2JsonContext : JsonSerializerContext;

/// <summary>A reusable definition: charge, then receipt, both seeded with the same Workflow Input.</summary>
public sealed class CheckoutWorkflow : IWorkflow<V2Seed>
{
    public void Build(TypedWorkflowBuilder builder, V2Seed seed)
        => builder.Then(new ChargeStep(seed.OrderId)).Then(new ReceiptStep(seed.OrderId));
}

/// <summary>
/// Workflows v2 typed builder (issue 0263): compile-safe step references, Workflow Input, reusable
/// definitions + StartWorkflow, the inline form, typed fan-in, transactional co-commit, and the
/// byte-identical lowering seam - all above the frozen below-boundary spine.
/// </summary>
public class WorkflowsV2BuilderTests
{
    private static BackWaveHarness NewHarness(out V2Recorder recorder)
    {
        var services = new ServiceCollection()
            .AddSingleton<V2Recorder>()
            .AddTransient<IJobHandler<ChargeStep>, ChargeStepHandler>()
            .AddTransient<IJobHandler<ReceiptStep>, ReceiptStepHandler>()
            .AddTransient<IJobHandler<NotifyStep>, NotifyStepHandler>()
            .AddTransient<IJobHandler<CloseStep>, CloseStepHandler>()
            .AddTransient<IJobHandler<InputEchoStep>, InputEchoStepHandler>()
            .AddTransient<IJobHandler<FailStep>, FailStepHandler>()
            .AddTransient<IJobHandler<ReservedKeyStep>, ReservedKeyStepHandler>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<ChargeStep, ChargeStepHandler>("v2-charge", WorkflowsV2JsonContext.Default.ChargeStep),
            JobRegistration.Create<ReceiptStep, ReceiptStepHandler>("v2-receipt", WorkflowsV2JsonContext.Default.ReceiptStep),
            JobRegistration.Create<NotifyStep, NotifyStepHandler>("v2-notify", WorkflowsV2JsonContext.Default.NotifyStep),
            JobRegistration.Create<CloseStep, CloseStepHandler>("v2-close", WorkflowsV2JsonContext.Default.CloseStep),
            JobRegistration.Create<InputEchoStep, InputEchoStepHandler>("v2-echo", WorkflowsV2JsonContext.Default.InputEchoStep),
            JobRegistration.Create<FailStep, FailStepHandler>("v2-fail", WorkflowsV2JsonContext.Default.FailStep),
            JobRegistration.Create<ReservedKeyStep, ReservedKeyStepHandler>("v2-reserved", WorkflowsV2JsonContext.Default.ReservedKeyStep),
        ]);
        recorder = services.GetRequiredService<V2Recorder>();
        return new BackWaveHarness(registry, services, new BackWaveHarnessOptions
        {
            RetryPolicy = new Core.RetryPolicy { MaxAttempts = 1 },
        });
    }

    // ── Lowering seam (pure module) ────────────────────────────────────────────────

    [Fact]
    public void Lowering_FanOutAndFanIn_ProducesTheExpectedPreparedGraph()
    {
        var h = NewHarness(out _);
        var b = h.Client.Workflow("order-flow")
            .Then(new ChargeStep("o1"))                                   // root
            .Then(new ReceiptStep("o1"))                                  // linear on charge
            .Then(new NotifyStep("o1"), after: [typeof(ChargeStep)])      // fan-out: also a child of charge
            .Then(new CloseStep("o1"), after: [typeof(ReceiptStep), typeof(NotifyStep)]); // fan-in
        var def = b.Build();

        Assert.Equal("order-flow", def.Name);
        Assert.Equal(4, def.Members.Count);
        var byWire = def.Members.ToDictionary(m => m.WireName);
        var chargeId = byWire["v2-charge"].JobId;
        var receiptId = byWire["v2-receipt"].JobId;
        var notifyId = byWire["v2-notify"].JobId;

        Assert.Empty(byWire["v2-charge"].Parents);
        Assert.Equal([chargeId], byWire["v2-receipt"].Parents);
        Assert.Equal([chargeId], byWire["v2-notify"].Parents);
        Assert.Equal(
            new HashSet<Guid> { receiptId, notifyId },
            byWire["v2-close"].Parents.ToHashSet());
    }

    [Fact]
    public void Lowering_Seedless_PayloadIsByteIdenticalToAStandaloneEnqueue()
    {
        var h = NewHarness(out _);
        var member = h.Client.Workflow().Then(new ChargeStep("o1")).Build().Members.Single();
        var standalone = JsonSerializer.SerializeToUtf8Bytes(new ChargeStep("o1"), WorkflowsV2JsonContext.Default.ChargeStep);

        Assert.Equal(standalone, member.Payload.ToArray());
    }

    [Fact]
    public void Lowering_Seeded_BakesAnEnvelope_ThatIsTransparentToTheStepDecoder()
    {
        var h = NewHarness(out _);
        var member = h.Client.Workflow(new V2Seed("o7"), WorkflowsV2JsonContext.Default.V2Seed)
            .Then(new ChargeStep("charged"))
            .Build().Members.Single();
        var seedless = JsonSerializer.SerializeToUtf8Bytes(new ChargeStep("charged"), WorkflowsV2JsonContext.Default.ChargeStep);

        // The seed was baked in (so the bytes differ from a seedless enqueue)...
        Assert.NotEqual(seedless, member.Payload.ToArray());
        // ...yet the member still deserializes cleanly to its own step type (the envelope is an unknown
        // property the step decoder skips). ctx.Input reading the seed back is covered end-to-end below.
        var step = JsonSerializer.Deserialize(member.Payload.Span, WorkflowsV2JsonContext.Default.ChargeStep);
        Assert.Equal("charged", step!.Note);
    }

    // ── End-to-end through the in-memory harness ───────────────────────────────────

    [Fact]
    public async Task StartWorkflow_RunsBothSteps_InDependencyOrder()
    {
        var h = NewHarness(out var recorder);

        await h.Client.StartWorkflow<CheckoutWorkflow, V2Seed>(
            new V2Seed("order-42"), WorkflowsV2JsonContext.Default.V2Seed);
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Equal(["charge", "receipt"], recorder.Ran);
    }

    [Fact]
    public async Task Seeded_Handler_ReadsTheWorkflowInput_ViaCtxInput()
    {
        var h = NewHarness(out var recorder);

        await h.Client.Workflow(new V2Seed("order-99"), WorkflowsV2JsonContext.Default.V2Seed)
            .Then(new InputEchoStep("x"))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Equal(["echo"], recorder.Ran);
        Assert.Equal("order-99", recorder.SeenInput);
    }

    [Fact]
    public async Task Inline_Form_BuildsAndEnqueues_AnUnnamedGraph()
    {
        var h = NewHarness(out var recorder);

        var id = await h.Client.Workflow()
            .Then(new NotifyStep("x"))
            .Then(new CloseStep("x"))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Null((await h.Monitor.GetWorkflowAsync(id))!.Name);
        Assert.Equal(["notify", "close"], recorder.Ran);
    }

    [Fact]
    public async Task Fanin_After_ProducesAMultiParentEdge_ThatReleasesOnAllParents()
    {
        var h = NewHarness(out var recorder);

        await h.Client.Workflow()
            .Then(new ChargeStep("o"))
            .Then(new NotifyStep("o"), after: [typeof(ChargeStep)])
            .Then(new CloseStep("o"), after: [typeof(ChargeStep), typeof(NotifyStep)])
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        // close runs only after both of its parents; it is last.
        Assert.Equal("close", recorder.Ran[^1]);
        Assert.Equal(3, recorder.Ran.Count);
    }

    // ── Transactional co-commit survives on the typed path ─────────────────────────

    [Fact]
    public async Task EnqueueAsync_WithTransaction_CoCommitsTheWholeGraphAtomically()
    {
        var h = NewHarness(out var recorder);

        using (var tx = h.Store.BeginTransaction())
        {
            await h.Client.Workflow().Then(new ChargeStep("o")).Then(new ReceiptStep("o")).EnqueueAsync(tx);
            // Invisible until commit.
            Assert.Empty(await h.Monitor.ListWorkflowsAsync());
            tx.Commit();
        }

        await h.AdvanceAsync(TimeSpan.Zero);
        Assert.Equal(["charge", "receipt"], recorder.Ran);
    }

    // ── Parallel fan-out / fan-in (issue 0265) ─────────────────────────────────────

    // Structural fingerprint of a lowered graph, keyed by wire name and independent of the random JobIds:
    // each member maps to (its own mode, the set of its parents' wire names). Two builds that draw the same
    // multi-parent edges compare equal.
    private static Dictionary<string, (DependencyMode Mode, string Parents)> Fingerprint(WorkflowDefinition def)
    {
        var wireById = def.Members.ToDictionary(m => m.JobId, m => m.WireName);
        return def.Members.ToDictionary(
            m => m.WireName,
            m => (m.Mode, string.Join(",", m.Parents.Select(p => wireById[p]).OrderBy(w => w))));
    }

    [Fact]
    public void Parallel_StepArray_FansOutFromTheFrontier_AndTheNextThenJoins()
    {
        var h = NewHarness(out _);

        // Fast path: charge, then notify + receipt in parallel, then close joins on both.
        var actual = h.Client.Workflow()
            .Then(new ChargeStep("o"))
            .Parallel(new NotifyStep("o"), new ReceiptStep("o"))
            .Then(new CloseStep("o"), after: [typeof(NotifyStep), typeof(ReceiptStep)])
            .Build();

        // Hand-wired equivalent using only the existing linear/fan-in API.
        var wired = h.Client.Workflow()
            .Then(new ChargeStep("o"))
            .Then(new NotifyStep("o"))                                    // linear child of charge
            .Then(new ReceiptStep("o"), after: [typeof(ChargeStep)])      // also a child of charge
            .Then(new CloseStep("o"), after: [typeof(NotifyStep), typeof(ReceiptStep)])
            .Build();

        Assert.Equal(4, actual.Members.Count);
        Assert.Equal(Fingerprint(wired), Fingerprint(actual));

        var byWire = actual.Members.ToDictionary(m => m.WireName);
        var chargeId = byWire["v2-charge"].JobId;
        Assert.Equal([chargeId], byWire["v2-notify"].Parents);
        Assert.Equal([chargeId], byWire["v2-receipt"].Parents);
        Assert.Equal(
            new HashSet<Guid> { byWire["v2-notify"].JobId, byWire["v2-receipt"].JobId },
            byWire["v2-close"].Parents.ToHashSet());
    }

    [Fact]
    public void Parallel_MixesSingleStepAndMultiStepLambdaBranches_InOneCall()
    {
        var h = NewHarness(out _);

        // Branch A = a single step; branch B = a two-step sub-pipeline. The join waits on both branch tips.
        var actual = h.Client.Workflow()
            .Then(new ChargeStep("o"))
            .Parallel(
                WorkflowBranch.Step(new NotifyStep("o")),
                WorkflowBranch.Do(b => b.Then(new ReceiptStep("o")).Then(new CloseStep("o"))))
            .Then(new InputEchoStep("o"), after: [typeof(NotifyStep), typeof(CloseStep)])
            .Build();

        var wired = h.Client.Workflow()
            .Then(new ChargeStep("o"))
            .Then(new NotifyStep("o"))                                    // branch A tip
            .Then(new ReceiptStep("o"), after: [typeof(ChargeStep)])      // branch B step 1
            .Then(new CloseStep("o"))                                     // branch B step 2 (tip)
            .Then(new InputEchoStep("o"), after: [typeof(NotifyStep), typeof(CloseStep)])
            .Build();

        Assert.Equal(Fingerprint(wired), Fingerprint(actual));

        var byWire = actual.Members.ToDictionary(m => m.WireName);
        Assert.Equal([byWire["v2-charge"].JobId], byWire["v2-notify"].Parents);
        Assert.Equal([byWire["v2-charge"].JobId], byWire["v2-receipt"].Parents);
        Assert.Equal([byWire["v2-receipt"].JobId], byWire["v2-close"].Parents);  // linear inside branch B
        Assert.Equal(
            new HashSet<Guid> { byWire["v2-notify"].JobId, byWire["v2-close"].JobId },
            byWire["v2-echo"].Parents.ToHashSet());
    }

    [Fact]
    public void Parallel_NestedInsideABranch_Composes_AllTipsFeedTheJoin()
    {
        var h = NewHarness(out _);

        // Branch A nests another Parallel (no inner join → two tips); branch B is a single step. The outer
        // join therefore pulls all three tips.
        var actual = h.Client.Workflow()
            .Then(new ChargeStep("o"))
            .Parallel(
                WorkflowBranch.Do(b => b.Parallel(new NotifyStep("o"), new ReceiptStep("o"))),
                WorkflowBranch.Step(new CloseStep("o")))
            .Then(new InputEchoStep("o"), after: [typeof(NotifyStep), typeof(ReceiptStep), typeof(CloseStep)])
            .Build();

        var byWire = actual.Members.ToDictionary(m => m.WireName);
        var chargeId = byWire["v2-charge"].JobId;
        Assert.Equal([chargeId], byWire["v2-notify"].Parents);
        Assert.Equal([chargeId], byWire["v2-receipt"].Parents);
        Assert.Equal([chargeId], byWire["v2-close"].Parents);
        Assert.Equal(
            new HashSet<Guid> { byWire["v2-notify"].JobId, byWire["v2-receipt"].JobId, byWire["v2-close"].JobId },
            byWire["v2-echo"].Parents.ToHashSet());
    }

    [Fact]
    public void Parallel_JoinMode_DefaultsToOnSuccess_AndOnAnyTerminalIsCarriedToTheJoinMember()
    {
        var h = NewHarness(out _);

        var onSuccess = h.Client.Workflow()
            .Then(new ChargeStep("o"))
            .Parallel(new NotifyStep("o"), new ReceiptStep("o"))
            .Then(new CloseStep("o"), after: [typeof(NotifyStep), typeof(ReceiptStep)])
            .Build();
        Assert.Equal(
            DependencyMode.OnSuccess,
            onSuccess.Members.Single(m => m.WireName == "v2-close").Mode);

        var onAnyTerminal = h.Client.Workflow()
            .Then(new ChargeStep("o"))
            .Parallel(new NotifyStep("o"), new ReceiptStep("o"))
            .Then(new CloseStep("o"), after: [typeof(NotifyStep), typeof(ReceiptStep)],
                mode: DependencyMode.OnAnyTerminal)
            .Build();
        Assert.Equal(
            DependencyMode.OnAnyTerminal,
            onAnyTerminal.Members.Single(m => m.WireName == "v2-close").Mode);
    }

    [Fact]
    public async Task Parallel_RunsBranchesAndJoinsOnSuccess_EndToEnd()
    {
        var h = NewHarness(out var recorder);

        await h.Client.Workflow()
            .Then(new ChargeStep("o"))
            .Parallel(new NotifyStep("o"), new ReceiptStep("o"))
            .Then(new CloseStep("o"), after: [typeof(NotifyStep), typeof(ReceiptStep)])
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Equal("charge", recorder.Ran[0]);                          // root runs first
        Assert.Equal("close", recorder.Ran[^1]);                          // join runs last
        Assert.Equal(new[] { "notify", "receipt" }.ToHashSet(), recorder.Ran[1..3].ToHashSet());
    }

    [Fact]
    public async Task Parallel_JoinWithOnAnyTerminal_ReleasesEvenWhenABranchFails_EndToEnd()
    {
        var h = NewHarness(out var recorder);

        // One branch fails terminally (MaxAttempts = 1). An OnAnyTerminal join still releases.
        await h.Client.Workflow()
            .Then(new ChargeStep("o"))
            .Parallel(
                WorkflowBranch.Step(new NotifyStep("o")),
                WorkflowBranch.Step(new FailStep("o")))
            .Then(new CloseStep("o"), after: [typeof(NotifyStep), typeof(FailStep)],
                mode: DependencyMode.OnAnyTerminal)
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Contains("notify", recorder.Ran);
        Assert.Contains("fail", recorder.Ran);
        Assert.Contains("close", recorder.Ran);                          // released despite the failed parent
    }

    [Fact]
    public async Task Build_OrEnqueue_OnASubBuilderHandedToABranch_Throws()
    {
        var h = NewHarness(out _);

        TypedWorkflowBuilder? captured = null;
        h.Client.Workflow()
            .Then(new ChargeStep("o"))
            .Parallel(WorkflowBranch.Do(b =>
            {
                captured = b;
                b.Then(new NotifyStep("o"));
            }));

        // The captured sub-builder shares the parent's node list; building or enqueuing it on its own would
        // emit a partial graph under the parent's WorkflowId, so both guard-throw.
        Assert.Throws<InvalidOperationException>(() => captured!.Build());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await captured!.EnqueueAsync());
    }

    [Fact]
    public void Parallel_NoBranches_Throws()
    {
        var h = NewHarness(out _);
        Assert.Throws<InvalidWorkflowException>(() =>
            h.Client.Workflow().Then(new ChargeStep("o")).Parallel(Array.Empty<IWorkflowStep>()));
    }

    [Fact]
    public void Parallel_BranchThatAddsNoStep_Throws()
    {
        var h = NewHarness(out _);
        Assert.Throws<InvalidWorkflowException>(() =>
            h.Client.Workflow().Then(new ChargeStep("o")).Parallel(WorkflowBranch.Do(_ => { })));
    }

    // ── Validation ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_EmptyGraph_Throws()
    {
        var h = NewHarness(out _);
        Assert.Throws<InvalidWorkflowException>(() => h.Client.Workflow().Build());
    }

    [Fact]
    public void Then_DuplicateStepIdentity_Throws_UnlessDisambiguated()
    {
        var h = NewHarness(out _);
        Assert.Throws<InvalidWorkflowException>(() =>
            h.Client.Workflow().Then(new ChargeStep("a")).Then(new ChargeStep("b")).Build());

        // A disambiguation name makes the twin legal.
        var def = h.Client.Workflow()
            .Then(new ChargeStep("a"))
            .Then(new ChargeStep("b"), name: "retry")
            .Build();
        Assert.Equal(2, def.Members.Count);
    }

    [Fact]
    public void FanIn_AfterNamingATypeNotInTheWorkflow_Throws()
    {
        var h = NewHarness(out _);
        var ex = Assert.Throws<InvalidWorkflowException>(() =>
            h.Client.Workflow()
                .Then(new ChargeStep("o"))
                .Then(new CloseStep("o"), after: [typeof(ReceiptStep)]));   // receipt was never added
        Assert.Contains("not a step in this workflow", ex.Message);
    }

    [Fact]
    public void FanIn_AfterNamingARepeatedStepType_Throws()
    {
        var h = NewHarness(out _);
        // Two (legally disambiguated) steps of the same type make a fan-in on that type ambiguous.
        var ex = Assert.Throws<InvalidWorkflowException>(() =>
            h.Client.Workflow()
                .Then(new ChargeStep("a"))
                .Then(new ChargeStep("b"), name: "second")
                .Then(new CloseStep("o"), after: [typeof(ChargeStep)]));
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void FanIn_WithAWorkflowStepRefName_ResolvesTheDisambiguatedRepeatedStep()
    {
        var h = NewHarness(out _);

        // A repeated step type: the fan-in picks the named one via a WorkflowStepRef, and a bare typeof for
        // the sole other type still converts implicitly - both forms mix in one after: list.
        var def = h.Client.Workflow()
            .Then(new ChargeStep("a"))
            .Then(new ChargeStep("b"), name: "second")
            .Then(new NotifyStep("o"))
            .Then(new CloseStep("o"),
                after: [new WorkflowStepRef(typeof(ChargeStep), "second"), typeof(NotifyStep)])
            .Build();

        var notifyId = def.Members.Single(m => m.WireName == "v2-notify").JobId;
        var closeParents = def.Members.Single(m => m.WireName == "v2-close").Parents;
        // The second charge (the disambiguated one) is the non-root charge - it has a parent, the first does not.
        var secondChargeId = def.Members.Single(m => m.WireName == "v2-charge" && m.Parents.Count == 1).JobId;
        // close waits on exactly the second charge and notify - not the first (root) charge.
        Assert.Equal(
            new HashSet<Guid> { secondChargeId, notifyId },
            closeParents.ToHashSet());
    }

    // Cycle detection is defense-in-depth: the forward-only fluent surface can only make a new step
    // depend on steps already added, so ordinary chaining cannot produce a cycle. The one reachable
    // construction threads the afterExisting id escape hatch together with the compensation
    // reverse-order edge (which retro-wires an EARLIER node to wait on a LATER one). There is no
    // internal seam to drive EnsureAcyclic directly - it is private, and BackWave.Pro grants
    // InternalsVisibleTo to BackWave.Pro.Tests, not to this assembly - so this public-API
    // construction is the cycle gate's test.
    [Fact]
    public void Build_GraphWithADependencyCycle_Throws()
    {
        var h = NewHarness(out _);

        // charge, guarded by undo-1 (notify). Build() is pure and repeatable, so an intermediate
        // Build exposes undo-1's minted id for the afterExisting edge below.
        var builder = h.Client.Workflow()
            .Then(new ChargeStep("o"))
            .WithCompensation(new NotifyStep("undo-1"));
        var undo1Id = builder.Build().Members.Single(m => m.WireName == "v2-notify").JobId;

        // close waits on undo-1 via afterExisting; a second compensation (receipt, undo-2) then
        // guards close AND retro-wires undo-1 to wait on undo-2. That closes the loop:
        // undo-1 -> undo-2 -> close -> undo-1.
        builder
            .Then(new CloseStep("o"), after: [typeof(ChargeStep)], afterExisting: [undo1Id])
            .WithCompensation(new ReceiptStep("undo-2"));

        var ex = Assert.Throws<InvalidWorkflowException>(() => builder.Build());
        Assert.Contains("cycle", ex.Message);
    }

    [Fact]
    public void Build_StepWhosePayloadClaimsTheReservedNamespace_ThrowsThroughAWorkflow()
    {
        var h = NewHarness(out _);

        // The reserved-$backwave. fail-fast is only reachable once a member carries an envelope (a seed or
        // a parent); an unseeded standalone step skips the check entirely. A seeded one-step workflow whose
        // own payload declares a $backwave. property drives the collision through the builder's lowering.
        var ex = Assert.Throws<InvalidWorkflowException>(() =>
            h.Client.Workflow(new V2Seed("o"), WorkflowsV2JsonContext.Default.V2Seed)
                .Then(new ReservedKeyStep("oops"))
                .Build());
        Assert.Contains("$backwave.", ex.Message);
    }
}
