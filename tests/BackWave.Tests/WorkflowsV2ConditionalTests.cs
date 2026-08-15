using System.Text.Json.Serialization;
using BackWave.Jobs;
using BackWave.Operations;
using BackWave.Pro;
using BackWave.Storage;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackWave.Tests;

// ── Steps, output, gate predicate, handlers, and recorder for conditional branching (0266) ──

/// <summary>The output the pricing step produces - the value the gate predicate reads.</summary>
public sealed record OrderTotal(int Cents);

/// <summary>Records which conditional steps ran.</summary>
public sealed class CondRecorder
{
    public List<string> Ran { get; } = [];
}

public sealed record PriceStep(int Cents) : IWorkflowStep<OrderTotal>;
public sealed record ExpressStep(string Note) : IWorkflowStep;
public sealed record ExpressPackStep(string Note) : IWorkflowStep;
public sealed record StandardStep(string Note) : IWorkflowStep;
public sealed record CloseStep2(string Note) : IWorkflowStep;

/// <summary>The gate predicate: take the express ("then") arm only for a large order.</summary>
public sealed class BigOrder : IWorkflowGate<PriceStep, OrderTotal>
{
    public bool Enter(DependencyOutput<OrderTotal> observed)
        => observed.HasOutput && observed.Output!.Cents > 100_000;
}

public sealed class PriceStepHandler(CondRecorder recorder) : IJobHandler<PriceStep>
{
    public Task HandleAsync(PriceStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("price");
        context.SetOutput<PriceStep, OrderTotal>(new OrderTotal(job.Cents));
        return Task.CompletedTask;
    }
}

public sealed class ExpressStepHandler(CondRecorder recorder) : IJobHandler<ExpressStep>
{
    public Task HandleAsync(ExpressStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("express");
        return Task.CompletedTask;
    }
}

public sealed class ExpressPackStepHandler(CondRecorder recorder) : IJobHandler<ExpressPackStep>
{
    public Task HandleAsync(ExpressPackStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("express-pack");
        return Task.CompletedTask;
    }
}

public sealed class StandardStepHandler(CondRecorder recorder) : IJobHandler<StandardStep>
{
    public Task HandleAsync(StandardStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("standard");
        return Task.CompletedTask;
    }
}

public sealed class CloseStep2Handler(CondRecorder recorder) : IJobHandler<CloseStep2>
{
    public Task HandleAsync(CloseStep2 job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("close");
        return Task.CompletedTask;
    }
}

// ── Failure-path fixtures: a throwing predicate, a silent producer, a failing producer, a bridge ──

/// <summary>A producer that succeeds but emits no output - the gate observes a clean absence.</summary>
public sealed record SilentPriceStep(string Note) : IWorkflowStep<OrderTotal>;

/// <summary>A producer that always fails - the gate's observed ancestor dead-letters.</summary>
public sealed record FailingPriceStep(string Note) : IWorkflowStep<OrderTotal>;

/// <summary>An OnAnyTerminal bridge that keeps the gate reachable when the observed ancestor failed.</summary>
public sealed record GateBridgeStep(string Note) : IWorkflowStep;

/// <summary>A predicate that always throws - the gate handler itself faults.</summary>
public sealed class ThrowingGate : IWorkflowGate<PriceStep, OrderTotal>
{
    public bool Enter(DependencyOutput<OrderTotal> observed)
        => throw new InvalidOperationException("predicate failed on purpose");
}

/// <summary>Observes the silent producer, recording what the gate handed it (absence expected).</summary>
public sealed class SilentGatePredicate : IWorkflowGate<SilentPriceStep, OrderTotal>
{
    public static DependencyOutput<OrderTotal>? LastObserved;

    public bool Enter(DependencyOutput<OrderTotal> observed)
    {
        LastObserved = observed;
        return observed.HasOutput && observed.Output!.Cents > 100_000;
    }
}

/// <summary>Observes the failing producer, recording what the gate handed it (absence expected).</summary>
public sealed class FailingGatePredicate : IWorkflowGate<FailingPriceStep, OrderTotal>
{
    public static DependencyOutput<OrderTotal>? LastObserved;

    public bool Enter(DependencyOutput<OrderTotal> observed)
    {
        LastObserved = observed;
        return observed.HasOutput && observed.Output!.Cents > 100_000;
    }
}

public sealed class SilentPriceStepHandler(CondRecorder recorder) : IJobHandler<SilentPriceStep>
{
    public Task HandleAsync(SilentPriceStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("silent-price");
        return Task.CompletedTask;
    }
}

public sealed class FailingPriceStepHandler(CondRecorder recorder) : IJobHandler<FailingPriceStep>
{
    public Task HandleAsync(FailingPriceStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("failing-price");
        throw new InvalidOperationException("pricing failed on purpose");
    }
}

public sealed class GateBridgeStepHandler(CondRecorder recorder) : IJobHandler<GateBridgeStep>
{
    public Task HandleAsync(GateBridgeStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("bridge");
        return Task.CompletedTask;
    }
}

// ── Seed-aware gate fixtures (0273): a predicate that reads the Workflow Input seed too ──

/// <summary>The immutable Workflow Input this suite's seed-aware gates read - a free-shipping threshold.</summary>
public sealed record ShipSeed(int FreeShipCents) : IWorkflowInput;

/// <summary>A seed-aware gate mixing seed and ancestor output: express only when the total clears the seed threshold.</summary>
public sealed class OverSeedThreshold : IWorkflowGate<PriceStep, OrderTotal, ShipSeed>
{
    public static ShipSeed? LastSeed;
    public static DependencyOutput<OrderTotal>? LastObserved;

    public bool Enter(DependencyOutput<OrderTotal> observed, ShipSeed input)
    {
        LastSeed = input;
        LastObserved = observed;
        return observed.HasOutput && observed.Output!.Cents > input.FreeShipCents;
    }
}

[JsonSerializable(typeof(OrderTotal))]
[JsonSerializable(typeof(PriceStep))]
[JsonSerializable(typeof(ExpressStep))]
[JsonSerializable(typeof(ExpressPackStep))]
[JsonSerializable(typeof(StandardStep))]
[JsonSerializable(typeof(CloseStep2))]
[JsonSerializable(typeof(SilentPriceStep))]
[JsonSerializable(typeof(FailingPriceStep))]
[JsonSerializable(typeof(GateBridgeStep))]
[JsonSerializable(typeof(ShipSeed))]
[JsonSerializable(typeof(WorkflowGate<BigOrder, PriceStep, OrderTotal>))]
[JsonSerializable(typeof(WorkflowGate<ThrowingGate, PriceStep, OrderTotal>))]
[JsonSerializable(typeof(WorkflowGate<SilentGatePredicate, SilentPriceStep, OrderTotal>))]
[JsonSerializable(typeof(WorkflowGate<FailingGatePredicate, FailingPriceStep, OrderTotal>))]
[JsonSerializable(typeof(WorkflowGate<OverSeedThreshold, PriceStep, OrderTotal, ShipSeed>))]
internal sealed partial class WorkflowsV2CondJsonContext : JsonSerializerContext;

/// <summary>
/// Workflows v2 conditional branching (issue 0266): <c>.If</c> lowers to a statically-wired gate step
/// that reads a decided ancestor output at run time and cancels the not-taken arm through the workflow
/// cancel operator, so exactly one arm runs and the other reaches the terminal cancelled state - all
/// above the frozen below-boundary spine, with no new dependency mode or storage op.
/// </summary>
public class WorkflowsV2ConditionalTests
{
    // The gate handler cancels the not-taken arm through a BackWaveOperator resolved from DI. The operator
    // needs the harness store and clock, which only exist after the harness is built, so a holder hands
    // them to the operator factory lazily; the gate resolves the operator when it runs, well after wiring.
    private sealed class OperatorHolder
    {
        public BackWaveOperator? Operator { get; set; }
    }

    private sealed class HarnessClock(BackWaveHarness harness) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => harness.Now;
    }

    private static BackWaveHarness NewHarness(out CondRecorder recorder, LogCapture? capture = null)
    {
        var holder = new OperatorHolder();
        var baseServices = new ServiceCollection();
        // When a capture is supplied, register it as the ILoggerFactory the gate handler resolves through
        // (it creates "BackWave.Pro.Workflows" from the ambient factory, else NullLogger), so a test can
        // observe the gate's catalog events. Left unregistered otherwise, keeping every other test silent.
        if (capture is not null)
        {
            baseServices.AddSingleton<ILoggerFactory>(new CapturingLoggerFactory(capture));
        }
        var services = baseServices
            .AddSingleton<CondRecorder>()
            .AddSingleton(holder)
            .AddSingleton(sp => sp.GetRequiredService<OperatorHolder>().Operator!)
            .AddTransient<IJobHandler<PriceStep>, PriceStepHandler>()
            .AddTransient<IJobHandler<ExpressStep>, ExpressStepHandler>()
            .AddTransient<IJobHandler<ExpressPackStep>, ExpressPackStepHandler>()
            .AddTransient<IJobHandler<StandardStep>, StandardStepHandler>()
            .AddTransient<IJobHandler<CloseStep2>, CloseStep2Handler>()
            .AddTransient<IJobHandler<SilentPriceStep>, SilentPriceStepHandler>()
            .AddTransient<IJobHandler<FailingPriceStep>, FailingPriceStepHandler>()
            .AddTransient<IJobHandler<GateBridgeStep>, GateBridgeStepHandler>()
            .AddTransient<IJobHandler<WorkflowGate<BigOrder, PriceStep, OrderTotal>>,
                WorkflowGateHandler<BigOrder, PriceStep, OrderTotal>>()
            .AddTransient<IJobHandler<WorkflowGate<ThrowingGate, PriceStep, OrderTotal>>,
                WorkflowGateHandler<ThrowingGate, PriceStep, OrderTotal>>()
            .AddTransient<IJobHandler<WorkflowGate<SilentGatePredicate, SilentPriceStep, OrderTotal>>,
                WorkflowGateHandler<SilentGatePredicate, SilentPriceStep, OrderTotal>>()
            .AddTransient<IJobHandler<WorkflowGate<FailingGatePredicate, FailingPriceStep, OrderTotal>>,
                WorkflowGateHandler<FailingGatePredicate, FailingPriceStep, OrderTotal>>()
            .AddTransient<IJobHandler<WorkflowGate<OverSeedThreshold, PriceStep, OrderTotal, ShipSeed>>,
                WorkflowGateHandler<OverSeedThreshold, PriceStep, OrderTotal, ShipSeed>>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<PriceStep, PriceStepHandler>(
                "cond-price", WorkflowsV2CondJsonContext.Default.PriceStep,
                outputTypeInfo: WorkflowsV2CondJsonContext.Default.OrderTotal),
            JobRegistration.Create<ExpressStep, ExpressStepHandler>("cond-express", WorkflowsV2CondJsonContext.Default.ExpressStep),
            JobRegistration.Create<ExpressPackStep, ExpressPackStepHandler>("cond-express-pack", WorkflowsV2CondJsonContext.Default.ExpressPackStep),
            JobRegistration.Create<StandardStep, StandardStepHandler>("cond-standard", WorkflowsV2CondJsonContext.Default.StandardStep),
            JobRegistration.Create<CloseStep2, CloseStep2Handler>("cond-close", WorkflowsV2CondJsonContext.Default.CloseStep2),
            JobRegistration.Create<SilentPriceStep, SilentPriceStepHandler>(
                "cond-silent-price", WorkflowsV2CondJsonContext.Default.SilentPriceStep,
                outputTypeInfo: WorkflowsV2CondJsonContext.Default.OrderTotal),
            JobRegistration.Create<FailingPriceStep, FailingPriceStepHandler>(
                "cond-failing-price", WorkflowsV2CondJsonContext.Default.FailingPriceStep,
                outputTypeInfo: WorkflowsV2CondJsonContext.Default.OrderTotal),
            JobRegistration.Create<GateBridgeStep, GateBridgeStepHandler>("cond-bridge", WorkflowsV2CondJsonContext.Default.GateBridgeStep),
            JobRegistration.Create<WorkflowGate<BigOrder, PriceStep, OrderTotal>, WorkflowGateHandler<BigOrder, PriceStep, OrderTotal>>(
                "cond-gate", WorkflowsV2CondJsonContext.Default.WorkflowGateBigOrderPriceStepOrderTotal),
            JobRegistration.Create<WorkflowGate<ThrowingGate, PriceStep, OrderTotal>, WorkflowGateHandler<ThrowingGate, PriceStep, OrderTotal>>(
                "cond-gate-throwing", WorkflowsV2CondJsonContext.Default.WorkflowGateThrowingGatePriceStepOrderTotal),
            JobRegistration.Create<WorkflowGate<SilentGatePredicate, SilentPriceStep, OrderTotal>, WorkflowGateHandler<SilentGatePredicate, SilentPriceStep, OrderTotal>>(
                "cond-gate-silent", WorkflowsV2CondJsonContext.Default.WorkflowGateSilentGatePredicateSilentPriceStepOrderTotal),
            JobRegistration.Create<WorkflowGate<FailingGatePredicate, FailingPriceStep, OrderTotal>, WorkflowGateHandler<FailingGatePredicate, FailingPriceStep, OrderTotal>>(
                "cond-gate-failing", WorkflowsV2CondJsonContext.Default.WorkflowGateFailingGatePredicateFailingPriceStepOrderTotal),
            JobRegistration.Create<WorkflowGate<OverSeedThreshold, PriceStep, OrderTotal, ShipSeed>, WorkflowGateHandler<OverSeedThreshold, PriceStep, OrderTotal, ShipSeed>>(
                "cond-gate-seed", WorkflowsV2CondJsonContext.Default.WorkflowGateOverSeedThresholdPriceStepOrderTotalShipSeed),
        ],
        new Dictionary<Type, System.Text.Json.Serialization.Metadata.JsonTypeInfo>
        {
            [typeof(ShipSeed)] = WorkflowsV2CondJsonContext.Default.ShipSeed,
        });
        recorder = services.GetRequiredService<CondRecorder>();
        var harness = new BackWaveHarness(registry, services, new BackWaveHarnessOptions
        {
            RetryPolicy = new Core.RetryPolicy { MaxAttempts = 1 },
        });
        holder.Operator = new BackWaveOperator(harness.Store, new HarnessClock(harness));
        return harness;
    }

    // ── Lowering seam (pure module) ────────────────────────────────────────────────

    [Fact]
    public void If_EnqueuesBothArmsUpFront_WithTheGateWiredToTheFrontier()
    {
        var h = NewHarness(out _);

        var def = h.Client.Workflow()
            .Then(new PriceStep(50))
            .If<BigOrder, PriceStep, OrderTotal>(
                then: b => b.Then(new ExpressStep("x")),
                otherwise: b => b.Then(new StandardStep("x")))
            .Build();

        // price + gate + express + standard = four members; the shape is fixed at build time.
        Assert.Equal(4, def.Members.Count);
        var byWire = def.Members.ToDictionary(m => m.WireName);
        var priceId = byWire["cond-price"].JobId;
        var gateId = byWire["cond-gate"].JobId;

        Assert.Equal([priceId], byWire["cond-gate"].Parents);      // gate depends on the observed ancestor
        Assert.Equal([gateId], byWire["cond-express"].Parents);    // both arms depend on the gate
        Assert.Equal([gateId], byWire["cond-standard"].Parents);
        // Every member depends only on OnSuccess edges - no new dependency mode is introduced by .If.
        Assert.All(def.Members, m => Assert.Equal(DependencyMode.OnSuccess, m.Mode));
    }

    [Fact]
    public void If_ArmThatAddsNoStep_Throws()
    {
        var h = NewHarness(out _);
        Assert.Throws<InvalidWorkflowException>(() =>
            h.Client.Workflow()
                .Then(new PriceStep(1))
                .If<BigOrder, PriceStep, OrderTotal>(then: _ => { }));
    }

    [Fact]
    public void If_GateOfTheSameIdentityNestedInsideAnArm_Throws_UnlessDisambiguated()
    {
        var h = NewHarness(out _);

        // A same-identity gate nested inside an arm evaded the old inline pre-arm dup-check (which ran
        // before the arm was built). The check now runs at append time, after the arms exist, so the
        // outer gate's append sees the inner gate of the same type-plus-name and throws.
        var ex = Assert.Throws<InvalidWorkflowException>(() =>
            h.Client.Workflow()
                .Then(new PriceStep(50))
                .If<BigOrder, PriceStep, OrderTotal>(
                    then: b => b
                        .Then(new ExpressStep("x"))
                        .If<BigOrder, PriceStep, OrderTotal>(then: bb => bb.Then(new StandardStep("x"))))
                .Build());
        Assert.Contains("Duplicate step identity", ex.Message);

        // A disambiguation name on the nested gate makes the twin legal - five distinct members.
        var def = h.Client.Workflow()
            .Then(new PriceStep(50))
            .If<BigOrder, PriceStep, OrderTotal>(
                then: b => b
                    .Then(new ExpressStep("x"))
                    .If<BigOrder, PriceStep, OrderTotal>(
                        then: bb => bb.Then(new StandardStep("x")), name: "inner"))
            .Build();
        Assert.Equal(5, def.Members.Count);
    }

    // ── Converging past a conditional: the frontier .Then must release on any terminal ──

    [Fact]
    public void If_FrontierThenOverBothArmsWithoutOnAnyTerminal_ThrowsAtBuild_PointingToTheFix()
    {
        var h = NewHarness(out _);

        // After .If the frontier holds both arms' tips; a plain OnSuccess continuation over both would
        // always be cancelled when the gate cancels one arm (the continuation would silently never run).
        // Build catches it and names the fix rather than shipping a dead branch.
        var ex = Assert.Throws<InvalidWorkflowException>(() =>
            h.Client.Workflow()
                .Then(new PriceStep(50))
                .If<BigOrder, PriceStep, OrderTotal>(
                    then: b => b.Then(new ExpressStep("x")),
                    otherwise: b => b.Then(new StandardStep("x")))
                .Then(new CloseStep2("x"))
                .Build());
        Assert.Contains("OnAnyTerminal", ex.Message);
    }

    [Fact]
    public async Task If_FrontierThenWithOnAnyTerminal_ConvergesPastTheConditional_EndToEnd()
    {
        var h = NewHarness(out var recorder);

        // The frontier .Then (no explicit after:) with mode OnAnyTerminal converges over both arms: the
        // one arm that ran and the cancelled one are both terminal, so the continuation releases and runs.
        await h.Client.Workflow()
            .Then(new PriceStep(50))
            .If<BigOrder, PriceStep, OrderTotal>(
                then: b => b.Then(new ExpressStep("x")),
                otherwise: b => b.Then(new StandardStep("x")))
            .Then(new CloseStep2("x"), mode: DependencyMode.OnAnyTerminal)
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Contains("standard", recorder.Ran);
        Assert.DoesNotContain("express", recorder.Ran);
        Assert.Contains("close", recorder.Ran);        // the continuation ran (silently cancelled before the fix)
    }

    // ── End-to-end: the false predicate cancels the not-taken (then) arm ────────────

    [Fact]
    public async Task If_FalsePredicate_RunsTheOtherwiseArm_AndCancelsTheThenArmAndDescendants()
    {
        var h = NewHarness(out var recorder);

        // A small order (50 cents): the predicate is false, so the express ("then") arm - a two-step
        // subtree - is cancelled whole, and the standard ("otherwise") arm runs.
        var id = await h.Client.Workflow()
            .Then(new PriceStep(50))
            .If<BigOrder, PriceStep, OrderTotal>(
                then: b => b.Then(new ExpressStep("x")).Then(new ExpressPackStep("x")),
                otherwise: b => b.Then(new StandardStep("x")))
            .Then(new CloseStep2("x"),
                after: [typeof(ExpressPackStep), typeof(StandardStep)],
                mode: DependencyMode.OnAnyTerminal)
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Contains("standard", recorder.Ran);
        Assert.DoesNotContain("express", recorder.Ran);        // then arm head never ran
        Assert.DoesNotContain("express-pack", recorder.Ran);   // then arm descendant never ran
        Assert.Contains("close", recorder.Ran);                // converged once the one arm was done

        var byWire = (await h.Monitor.GetWorkflowAsync(id))!.Members.ToDictionary(m => m.WireName);
        Assert.Equal(JobState.Cancelled, byWire["cond-express"].State);        // whole subtree cancelled
        Assert.Equal(JobState.Cancelled, byWire["cond-express-pack"].State);
        Assert.Equal(JobState.Succeeded, byWire["cond-standard"].State);
        Assert.Equal(JobState.Succeeded, byWire["cond-close"].State);
        Assert.Equal(JobState.Succeeded, byWire["cond-gate"].State);           // the gate itself succeeds
    }

    // ── End-to-end: the true predicate cancels the not-taken (otherwise) arm - symmetric ──

    [Fact]
    public async Task If_TruePredicate_RunsTheThenArm_AndCancelsTheOtherwiseArm()
    {
        var h = NewHarness(out var recorder);

        // A large order (150_000 cents): the predicate is true, so the express ("then") arm runs and the
        // standard ("otherwise") arm is cancelled.
        var id = await h.Client.Workflow()
            .Then(new PriceStep(150_000))
            .If<BigOrder, PriceStep, OrderTotal>(
                then: b => b.Then(new ExpressStep("x")),
                otherwise: b => b.Then(new StandardStep("x")))
            .Then(new CloseStep2("x"),
                after: [typeof(ExpressStep), typeof(StandardStep)],
                mode: DependencyMode.OnAnyTerminal)
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Contains("express", recorder.Ran);
        Assert.DoesNotContain("standard", recorder.Ran);
        Assert.Contains("close", recorder.Ran);

        var view = (await h.Monitor.GetWorkflowAsync(id))!;
        var byWire = view.Members.ToDictionary(m => m.WireName);
        Assert.Equal(JobState.Succeeded, byWire["cond-express"].State);
        Assert.Equal(JobState.Cancelled, byWire["cond-standard"].State);
        Assert.Equal(JobState.Succeeded, byWire["cond-close"].State);
        // A cancelled member (not a failed one) keeps the derived status coherent: cancelled, never failed.
        Assert.Equal(WorkflowStatus.Cancelled, view.Status);
    }

    [Fact]
    public async Task If_WhenTheGateDecides_LogsGateDecided_WithTheTakenArmAndNotTakenCount()
    {
        var capture = new LogCapture();
        var h = NewHarness(out var recorder, capture);

        // A large order takes the "then" arm; the single-member "otherwise" arm is the not-taken one.
        await h.Client.Workflow()
            .Then(new PriceStep(150_000))
            .If<BigOrder, PriceStep, OrderTotal>(
                then: b => b.Then(new ExpressStep("x")),
                otherwise: b => b.Then(new StandardStep("x")))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Contains("express", recorder.Ran);

        // The gate logs its decision once (Information, EventId 3001): which predicate, which arm it
        // entered, and how many steps of the not-taken arm it is cancelling (the lone StandardStep).
        var decided = Assert.Single(capture.Records, r => r.EventId == 3001);
        Assert.Equal(LogLevel.Information, decided.Level);
        Assert.Contains(nameof(BigOrder), decided.Message);
        Assert.Contains("then", decided.Message);
        Assert.Contains("1 step", decided.Message);
    }

    // ── The not-taken arm is cancelled descendants-first, so no member is ever claimable ──

    [Fact]
    public async Task If_NotTakenArmWithAnInArmOnAnyTerminalNode_IsNeverReleasedIntoAClaimableState()
    {
        var h = NewHarness(out var recorder);

        // The then arm carries an in-arm OnAnyTerminal edge: express-pack waits on express and releases
        // on ANY terminal outcome. Cancelling the arm head first would release express-pack into a
        // claimable Scheduled state mid-cancel-loop - a window where a concurrent worker could run a
        // step of the not-taken arm. The gate must cancel descendants first, closing that window.
        var id = await h.Client.Workflow()
            .Then(new PriceStep(50))
            .If<BigOrder, PriceStep, OrderTotal>(
                then: b => b
                    .Then(new ExpressStep("x"))
                    .Then(new ExpressPackStep("x"), after: [typeof(ExpressStep)], mode: DependencyMode.OnAnyTerminal),
                otherwise: b => b.Then(new StandardStep("x")))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Contains("standard", recorder.Ran);
        Assert.DoesNotContain("express", recorder.Ran);
        Assert.DoesNotContain("express-pack", recorder.Ran);

        var byWire = (await h.Monitor.GetWorkflowAsync(id))!.Members.ToDictionary(m => m.WireName);
        Assert.Equal(JobState.Cancelled, byWire["cond-express"].State);
        Assert.Equal(JobState.Cancelled, byWire["cond-express-pack"].State);

        // The race regression proper: the transition log records every state the node ever reached,
        // so a mid-loop release would leave a Scheduled entry even though the end state is Cancelled.
        // Descendants-first cancellation must cancel it straight out of AwaitingParent - the node is
        // never claimable at any instant.
        var history = await h.Store.GetJobHistoryAsync(byWire["cond-express-pack"].JobId);
        Assert.DoesNotContain(history, t => t.State == JobState.Scheduled);
    }

    // ── The same race, one .If deeper: the OnAnyTerminal node lives in a NESTED arm ──

    [Fact]
    public async Task If_NestedNotTakenArmWithAnInArmOnAnyTerminalNode_IsNeverReleasedIntoAClaimableState()
    {
        var h = NewHarness(out var recorder);

        // The not-taken OUTER arm nests an inner .If whose then arm carries the in-arm OnAnyTerminal edge
        // (express-pack waits on express, releasing on ANY terminal outcome). A nested .If appends its gate
        // node AFTER its own arm steps, so the outer arm's add order [express, express-pack, inner-gate] is
        // not topological. Cancelling in reverse-of-ADD order cancels the inner gate first, cascade-cancels
        // express, and thereby releases express-pack into a claimable Scheduled state before its own cancel
        // lands. Only a reverse-of-TOPOLOGICAL cancel (descendants first) closes that window.
        var id = await h.Client.Workflow()
            .Then(new PriceStep(50))
            .If<BigOrder, PriceStep, OrderTotal>(
                then: outer => outer
                    .If<BigOrder, PriceStep, OrderTotal>(
                        then: inner => inner
                            .Then(new ExpressStep("x"))
                            .Then(new ExpressPackStep("x"), after: [typeof(ExpressStep)], mode: DependencyMode.OnAnyTerminal),
                        name: "inner"),
                otherwise: outer => outer.Then(new StandardStep("x")))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Contains("standard", recorder.Ran);
        Assert.DoesNotContain("express", recorder.Ran);
        Assert.DoesNotContain("express-pack", recorder.Ran);

        // The outer and inner gates share one wire name (it is per gate type, not per disambiguator), so
        // members are selected by their unique step wire names rather than a wire-name dictionary.
        var members = (await h.Monitor.GetWorkflowAsync(id))!.Members;
        var expressPack = members.Single(m => m.WireName == "cond-express-pack");
        Assert.Equal(JobState.Cancelled, members.Single(m => m.WireName == "cond-express").State);
        Assert.Equal(JobState.Cancelled, expressPack.State);

        // The race regression proper: a mid-cancel-loop release would leave a Scheduled entry in the
        // transition log even though the end state is Cancelled. Descendants-first cancellation cancels
        // express-pack straight out of AwaitingParent - it is never claimable at any instant.
        var history = await h.Store.GetJobHistoryAsync(expressPack.JobId);
        Assert.DoesNotContain(history, t => t.State == JobState.Scheduled);
    }

    // ── An arm may not fan in to a step outside the arm: it would escape the gate's cancellation ──

    [Fact]
    public void If_ArmFansInToAPreGateStep_ThrowsAtBuild()
    {
        var h = NewHarness(out _);

        // The then arm fans in to PriceStep - a step BEFORE the gate. That parent edge roots the arm step
        // outside the arm, so it would run whenever PriceStep succeeded regardless of the gate's decision,
        // and the gate's later cancel would be a no-op on an already-succeeded step. An arm step may depend
        // only on the gate or another step in the same arm; this is rejected as the .If arm is built.
        var ex = Assert.Throws<InvalidWorkflowException>(() =>
            h.Client.Workflow()
                .Then(new PriceStep(50))
                .If<BigOrder, PriceStep, OrderTotal>(
                    then: b => b.Then(new ExpressStep("x"), after: [typeof(PriceStep)]),
                    otherwise: b => b.Then(new StandardStep("x"))));
        Assert.Contains("outside the arm", ex.Message);
    }

    // ── The alternate arm is optional: a false predicate with no otherwise cancels the then arm ──

    [Fact]
    public async Task If_NoOtherwiseArm_FalsePredicate_CancelsTheThenArm_AndRunsNothingInItsPlace()
    {
        var h = NewHarness(out var recorder);

        await h.Client.Workflow()
            .Then(new PriceStep(50))
            .If<BigOrder, PriceStep, OrderTotal>(then: b => b.Then(new ExpressStep("x")))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Equal(["price"], recorder.Ran);                 // the gate ran, then cancelled the only arm
        Assert.DoesNotContain("express", recorder.Ran);
    }

    [Fact]
    public async Task If_NoOtherwiseArm_TruePredicate_RunsTheThenArm_AndTheOnAnyTerminalContinuationRuns()
    {
        var h = NewHarness(out var recorder);

        // A large order (150_000 cents): the predicate is true, so the then-only arm runs. With no
        // otherwise arm the frontier is just the then arm's tip, and an OnAnyTerminal continuation must
        // still converge and RUN - the single-arm counterpart to the both-arm join, and the taken-arm
        // half of the .If().Then() continuation footgun (a continuation was once silently cancelled).
        var id = await h.Client.Workflow()
            .Then(new PriceStep(150_000))
            .If<BigOrder, PriceStep, OrderTotal>(then: b => b.Then(new ExpressStep("x")))
            .Then(new CloseStep2("x"), mode: DependencyMode.OnAnyTerminal)
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Contains("express", recorder.Ran);
        Assert.Contains("close", recorder.Ran);                // the continuation ran past the single-arm .If

        var view = (await h.Monitor.GetWorkflowAsync(id))!;
        var byWire = view.Members.ToDictionary(m => m.WireName);
        Assert.Equal(JobState.Succeeded, byWire["cond-express"].State);
        Assert.Equal(JobState.Succeeded, byWire["cond-close"].State);
        Assert.Equal(JobState.Succeeded, byWire["cond-gate"].State);
        // The taken single arm cancels nothing, so the run is cleanly Succeeded (not the Cancelled a
        // both-arm .If always derives from its not-taken arm).
        Assert.Equal(WorkflowStatus.Succeeded, view.Status);
    }

    // ── Failure paths: the predicate throws, the observed output is absent, the ancestor failed ──

    [Fact]
    public async Task If_PredicateThrows_GateDeadLetters_AndBothArmsCascadeCancel()
    {
        var h = NewHarness(out var recorder);

        var id = await h.Client.Workflow()
            .Then(new PriceStep(50))
            .If<ThrowingGate, PriceStep, OrderTotal>(
                then: b => b.Then(new ExpressStep("x")).Then(new ExpressPackStep("x")),
                otherwise: b => b.Then(new StandardStep("x")))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        // The gate burned its only attempt on the predicate throw and dead-lettered; with no decision
        // ever made, BOTH arms (descendants included) cascade-cancel off the failed gate, and the
        // failed member makes the whole workflow Failed.
        var view = (await h.Monitor.GetWorkflowAsync(id))!;
        var byWire = view.Members.ToDictionary(m => m.WireName);
        Assert.Equal(JobState.DeadLettered, byWire["cond-gate-throwing"].State);
        Assert.Equal(JobState.Cancelled, byWire["cond-express"].State);
        Assert.Equal(JobState.Cancelled, byWire["cond-express-pack"].State);
        Assert.Equal(JobState.Cancelled, byWire["cond-standard"].State);
        Assert.Equal(WorkflowStatus.Failed, view.Status);
        Assert.Equal(["price"], recorder.Ran);                 // no arm step ever ran
    }

    [Fact]
    public async Task If_ObservedAncestorSucceededWithoutOutput_PredicateSeesAbsence_AndDecidesNormally()
    {
        SilentGatePredicate.LastObserved = null;
        var h = NewHarness(out var recorder);

        var id = await h.Client.Workflow()
            .Then(new SilentPriceStep("x"))
            .If<SilentGatePredicate, SilentPriceStep, OrderTotal>(
                then: b => b.Then(new ExpressStep("x")),
                otherwise: b => b.Then(new StandardStep("x")))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        // Absence is normal: the succeeded-but-silent ancestor reaches the predicate as a clean
        // HasOutput = false (never a throw), and the predicate decides with it - here false, so the
        // otherwise arm runs and the then arm is cancelled.
        var observed = Assert.IsType<DependencyOutput<OrderTotal>>(SilentGatePredicate.LastObserved);
        Assert.False(observed.HasOutput);
        Assert.Null(observed.Output);
        Assert.Equal(JobState.Succeeded, observed.AncestorState);

        var byWire = (await h.Monitor.GetWorkflowAsync(id))!.Members.ToDictionary(m => m.WireName);
        Assert.Equal(JobState.Succeeded, byWire["cond-gate-silent"].State);
        Assert.Equal(JobState.Cancelled, byWire["cond-express"].State);
        Assert.Contains("standard", recorder.Ran);
        Assert.DoesNotContain("express", recorder.Ran);
    }

    [Fact]
    public async Task If_ObservedAncestorFails_GateAndBothArmsCascadeCancel_WithoutRunningThePredicate()
    {
        FailingGatePredicate.LastObserved = null;
        var h = NewHarness(out var recorder);

        // The observed ancestor is also the gate's parent (the common shape): when it dead-letters,
        // the gate - an ordinary OnSuccess child - cascade-cancels, and both arms cancel with it. The
        // predicate never runs; nothing below the failed step does.
        var id = await h.Client.Workflow()
            .Then(new FailingPriceStep("x"))
            .If<FailingGatePredicate, FailingPriceStep, OrderTotal>(
                then: b => b.Then(new ExpressStep("x")),
                otherwise: b => b.Then(new StandardStep("x")))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Null(FailingGatePredicate.LastObserved);        // the predicate was never evaluated

        var view = (await h.Monitor.GetWorkflowAsync(id))!;
        var byWire = view.Members.ToDictionary(m => m.WireName);
        Assert.Equal(JobState.DeadLettered, byWire["cond-failing-price"].State);
        Assert.Equal(JobState.Cancelled, byWire["cond-gate-failing"].State);
        Assert.Equal(JobState.Cancelled, byWire["cond-express"].State);
        Assert.Equal(JobState.Cancelled, byWire["cond-standard"].State);
        Assert.Equal(WorkflowStatus.Failed, view.Status);
        Assert.Equal(["failing-price"], recorder.Ran);
    }

    [Fact]
    public async Task If_ObservedAncestorFails_ButTheGateStaysReachable_PredicateSeesTheFailureAsAbsence()
    {
        FailingGatePredicate.LastObserved = null;
        var h = NewHarness(out var recorder);

        // An OnAnyTerminal bridge keeps the gate reachable past the dead-lettered producer: the gate
        // runs, and the failed ancestor reaches the predicate as a clean absence carrying the
        // ancestor's terminal state - the predicate decides with it rather than faulting.
        var id = await h.Client.Workflow()
            .Then(new FailingPriceStep("x"))
            .Then(new GateBridgeStep("x"), after: [typeof(FailingPriceStep)], mode: DependencyMode.OnAnyTerminal)
            .If<FailingGatePredicate, FailingPriceStep, OrderTotal>(
                then: b => b.Then(new ExpressStep("x")),
                otherwise: b => b.Then(new StandardStep("x")))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        var observed = Assert.IsType<DependencyOutput<OrderTotal>>(FailingGatePredicate.LastObserved);
        Assert.False(observed.HasOutput);
        Assert.Equal(JobState.DeadLettered, observed.AncestorState);

        var byWire = (await h.Monitor.GetWorkflowAsync(id))!.Members.ToDictionary(m => m.WireName);
        Assert.Equal(JobState.Succeeded, byWire["cond-gate-failing"].State);
        Assert.Equal(JobState.Cancelled, byWire["cond-express"].State);   // false decision: then arm cancelled
        Assert.Contains("standard", recorder.Ran);
        Assert.DoesNotContain("express", recorder.Ran);
    }

    // ── Seed-aware gate (0273): the predicate reads the Workflow Input seed alongside ancestor output ──

    [Fact]
    public async Task If_SeedAwarePredicate_TrueOverSeedThreshold_RunsTheThenArm_AndCancelsTheOtherwiseArm()
    {
        OverSeedThreshold.LastSeed = null;
        OverSeedThreshold.LastObserved = null;
        var h = NewHarness(out var recorder);

        // Seed carries a 100_000-cent free-ship threshold; the order prices at 150_000, so the seed-aware
        // predicate is true (over threshold) and the express ("then") arm runs, the standard arm cancels.
        // The consumer passes NO serializer: Workflow(seed) resolves the seed codec off the registry.
        var id = await h.Client.Workflow(new ShipSeed(FreeShipCents: 100_000))
            .Then(new PriceStep(150_000))
            .If<OverSeedThreshold, PriceStep, OrderTotal, ShipSeed>(
                then: b => b.Then(new ExpressStep("x")),
                otherwise: b => b.Then(new StandardStep("x")))
            .Then(new CloseStep2("x"),
                after: [typeof(ExpressStep), typeof(StandardStep)],
                mode: DependencyMode.OnAnyTerminal)
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Contains("express", recorder.Ran);
        Assert.DoesNotContain("standard", recorder.Ran);
        Assert.Contains("close", recorder.Ran);

        var byWire = (await h.Monitor.GetWorkflowAsync(id))!.Members.ToDictionary(m => m.WireName);
        Assert.Equal(JobState.Succeeded, byWire["cond-express"].State);
        Assert.Equal(JobState.Cancelled, byWire["cond-standard"].State);   // not-taken arm cancelled
        Assert.Equal(JobState.Succeeded, byWire["cond-gate-seed"].State);
    }

    [Fact]
    public async Task If_SeedAwarePredicate_FalseUnderSeedThreshold_RunsTheOtherwiseArm_AndCancelsTheThenArm()
    {
        OverSeedThreshold.LastSeed = null;
        OverSeedThreshold.LastObserved = null;
        var h = NewHarness(out var recorder);

        // Same 100_000 threshold, but the order prices at 50_000 - under threshold - so the predicate is
        // false: the standard ("otherwise") arm runs and the express ("then") arm cancels.
        var id = await h.Client.Workflow(new ShipSeed(FreeShipCents: 100_000))
            .Then(new PriceStep(50_000))
            .If<OverSeedThreshold, PriceStep, OrderTotal, ShipSeed>(
                then: b => b.Then(new ExpressStep("x")),
                otherwise: b => b.Then(new StandardStep("x")))
            .Then(new CloseStep2("x"),
                after: [typeof(ExpressStep), typeof(StandardStep)],
                mode: DependencyMode.OnAnyTerminal)
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Contains("standard", recorder.Ran);
        Assert.DoesNotContain("express", recorder.Ran);
        Assert.Contains("close", recorder.Ran);

        var byWire = (await h.Monitor.GetWorkflowAsync(id))!.Members.ToDictionary(m => m.WireName);
        Assert.Equal(JobState.Cancelled, byWire["cond-express"].State);    // not-taken arm cancelled
        Assert.Equal(JobState.Succeeded, byWire["cond-standard"].State);
        Assert.Equal(JobState.Succeeded, byWire["cond-gate-seed"].State);
    }

    [Fact]
    public async Task If_SeedAwarePredicate_EvaluatesBothSeedAndAncestorOutput_FromAlreadyDecidedData()
    {
        OverSeedThreshold.LastSeed = null;
        OverSeedThreshold.LastObserved = null;
        var h = NewHarness(out _);

        // The predicate read BOTH inputs from already-decided data: the seed baked in when the workflow
        // started (no serializer passed by the consumer) and the ancestor's decided Job Output.
        await h.Client.Workflow(new ShipSeed(FreeShipCents: 100_000))
            .Then(new PriceStep(150_000))
            .If<OverSeedThreshold, PriceStep, OrderTotal, ShipSeed>(
                then: b => b.Then(new ExpressStep("x")),
                otherwise: b => b.Then(new StandardStep("x")))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        var seed = Assert.IsType<ShipSeed>(OverSeedThreshold.LastSeed);
        Assert.Equal(100_000, seed.FreeShipCents);                          // the seed was resolved and read
        var observed = Assert.IsType<DependencyOutput<OrderTotal>>(OverSeedThreshold.LastObserved);
        Assert.True(observed.HasOutput);                                    // the ancestor output was read
        Assert.Equal(150_000, observed.Output!.Cents);
        Assert.Equal(JobState.Succeeded, observed.AncestorState);
    }

    // ── AddWorkflowGate: one-call gate registration, and the JobRegistry.WithAdditional merge behind it ──

    [Fact]
    public void AddWorkflowGate_RegistersScopedHandler_AndContributesSingletonRegistration()
    {
        var services = new ServiceCollection();

        services.AddWorkflowGate<BigOrder, PriceStep, OrderTotal>(
            "cond-gate", WorkflowsV2CondJsonContext.Default.WorkflowGateBigOrderPriceStepOrderTotal);

        // The handler registers scoped against the gate's IJobHandler - the module handler path, so its
        // scoped dependencies resolve and dispose per attempt.
        var handler = services.Single(
            d => d.ServiceType == typeof(IJobHandler<WorkflowGate<BigOrder, PriceStep, OrderTotal>>));
        Assert.Equal(ServiceLifetime.Scoped, handler.Lifetime);
        Assert.Equal(typeof(WorkflowGateHandler<BigOrder, PriceStep, OrderTotal>), handler.ImplementationType);

        // The registration is contributed as a singleton, so Hosting can fold it into the module registry.
        var descriptor = services.Single(d => d.ServiceType == typeof(JobRegistration));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        using var provider = services.BuildServiceProvider();
        var registration = Assert.Single(provider.GetServices<JobRegistration>());
        Assert.Equal("cond-gate", registration.WireName);
        Assert.Equal(typeof(WorkflowGate<BigOrder, PriceStep, OrderTotal>), registration.JobType);
    }

    [Fact]
    public void AddWorkflowGate_SeedAware_RegistersScopedHandler_AndContributesRegistration()
    {
        var services = new ServiceCollection();

        services.AddWorkflowGate<OverSeedThreshold, PriceStep, OrderTotal, ShipSeed>(
            "cond-gate-seed",
            WorkflowsV2CondJsonContext.Default.WorkflowGateOverSeedThresholdPriceStepOrderTotalShipSeed);

        var handler = services.Single(
            d => d.ServiceType == typeof(IJobHandler<WorkflowGate<OverSeedThreshold, PriceStep, OrderTotal, ShipSeed>>));
        Assert.Equal(ServiceLifetime.Scoped, handler.Lifetime);
        Assert.Equal(
            typeof(WorkflowGateHandler<OverSeedThreshold, PriceStep, OrderTotal, ShipSeed>),
            handler.ImplementationType);

        using var provider = services.BuildServiceProvider();
        var registration = Assert.Single(provider.GetServices<JobRegistration>());
        Assert.Equal("cond-gate-seed", registration.WireName);
        Assert.Equal(typeof(WorkflowGate<OverSeedThreshold, PriceStep, OrderTotal, ShipSeed>), registration.JobType);
    }

    [Fact]
    public void AddWorkflowGate_CalledForTwoGates_ContributesBothRegistrations()
    {
        var services = new ServiceCollection();

        // Two distinct gates. A TryAddEnumerable registration would dedupe by the JobRegistration impl type
        // and silently drop the second; the plain AddSingleton this helper uses accumulates both.
        services.AddWorkflowGate<BigOrder, PriceStep, OrderTotal>(
            "gate-a", WorkflowsV2CondJsonContext.Default.WorkflowGateBigOrderPriceStepOrderTotal);
        services.AddWorkflowGate<OverSeedThreshold, PriceStep, OrderTotal, ShipSeed>(
            "gate-b", WorkflowsV2CondJsonContext.Default.WorkflowGateOverSeedThresholdPriceStepOrderTotalShipSeed);

        using var provider = services.BuildServiceProvider();
        var wireNames = provider.GetServices<JobRegistration>().Select(r => r.WireName).ToList();
        Assert.Equal(2, wireNames.Count);
        Assert.Contains("gate-a", wireNames);
        Assert.Contains("gate-b", wireNames);
    }

    [Fact]
    public void JobRegistry_WithAdditional_FoldsContributedGate_AndPreservesSeedCodecs()
    {
        // A module-style base registry: one step plus a seed codec for the workflow input.
        var baseRegistry = new JobRegistry(
        [
            JobRegistration.Create<PriceStep, PriceStepHandler>(
                "cond-price", WorkflowsV2CondJsonContext.Default.PriceStep,
                outputTypeInfo: WorkflowsV2CondJsonContext.Default.OrderTotal),
        ],
        new Dictionary<Type, System.Text.Json.Serialization.Metadata.JsonTypeInfo>
        {
            [typeof(ShipSeed)] = WorkflowsV2CondJsonContext.Default.ShipSeed,
        });

        var contributed = JobRegistration.Create<
            WorkflowGate<BigOrder, PriceStep, OrderTotal>, WorkflowGateHandler<BigOrder, PriceStep, OrderTotal>>(
            "cond-gate", WorkflowsV2CondJsonContext.Default.WorkflowGateBigOrderPriceStepOrderTotal);

        var merged = baseRegistry.WithAdditional([contributed]);

        Assert.True(merged.TryGetByWireName("cond-gate", out _));        // the contributed gate folded in
        Assert.True(merged.TryGetByWireName("cond-price", out _));       // the base registration survived
        Assert.NotNull(merged.FindSeedCodec(typeof(ShipSeed)));          // and so did the seed codec
    }

    [Fact]
    public void JobRegistry_WithAdditional_ContributedWireNameCollision_Throws()
    {
        var baseRegistry = new JobRegistry(
        [
            JobRegistration.Create<PriceStep, PriceStepHandler>(
                "cond-price", WorkflowsV2CondJsonContext.Default.PriceStep,
                outputTypeInfo: WorkflowsV2CondJsonContext.Default.OrderTotal),
        ]);

        // A contributed registration reusing an existing Wire Name is rejected by the ctor's duplicate
        // check, thrown synchronously as WithAdditional materializes the concatenated sequence.
        var clash = JobRegistration.Create<ExpressStep, ExpressStepHandler>(
            "cond-price", WorkflowsV2CondJsonContext.Default.ExpressStep);

        Assert.Throws<InvalidOperationException>(() => baseRegistry.WithAdditional([clash]));
    }
}
