using System.Data.Common;
using System.Diagnostics;
using System.Text.Json.Nodes;
using BackWave.Diagnostics;
using BackWave.Jobs;
using BackWave.Storage;

namespace BackWave.Pro;

// InvalidWorkflowException lives in InvalidWorkflowException.cs and is thrown by this builder's
// empty-graph, cycle, and duplicate-identity validation.

/// <summary>
/// Builds a workflow graph with strongly-typed, compile-safe step references. A <b>step</b> is an
/// ordinary <c>[Job]</c> payload record wearing the <see cref="IWorkflowStep"/> marker; it is referenced
/// by its .NET type, so a typo or a rename is a compile error rather than a runtime one. Chain steps with
/// <see cref="Then{TStep}(TStep, DependencyMode, string?, string?, JobTags?)"/> (each depends on the current frontier and
/// becomes the new frontier - the linear common case), or fan in with
/// <see cref="Then{TStep}(TStep, IReadOnlyList{Type}, DependencyMode, IEnumerable{Guid}?, string?, string?, JobTags?)"/>,
/// naming several upstream step types. The whole graph lowers byte-identically to the below-boundary
/// prepared graph, so nothing about storage, adapters, or the determinism boundary changes. One
/// qualification on the byte-identical claim: a member that carries a Workflow Input seed or an upstream
/// dependency also carries a small reserved-namespace property in its payload, so only a seedless,
/// parentless member stays byte-for-byte identical to a standalone enqueue of the same step. Pure: it
/// touches no store and holds no I/O until <see cref="EnqueueAsync"/>.
/// </summary>
public sealed class TypedWorkflowBuilder
{
    private readonly BackWaveClient _client;
    private readonly string? _name;
    private readonly ReadOnlyMemory<byte>? _inputJson;
    private readonly bool _isAppend;
    private readonly List<Node> _nodes = [];
    private readonly List<Guid> _frontier = [];

    // True on a sub-builder handed to a Parallel/If branch lambda or an IWorkflow.Build child splice. Such
    // a builder shares the parent's node list and is meant only to accrete steps into the one graph; calling
    // Build or EnqueueAsync on it would enqueue a partial graph under the parent's WorkflowId, so both throw.
    private readonly bool _isSubBuilder;

    // The most recently attached compensation, if any. Each new compensation makes this one wait for it,
    // so successive compensations undo in reverse order of the work they protect (later-protected undoes
    // first). A side-branch state, kept separate from the main-chain frontier.
    private Guid? _lastCompensationId;

    // Every conditional gate's two arm-subtrees (each id set, head and descendants). Shared with sub-builders
    // like _nodes, so a nested .If records here too. Build validates that no OnSuccess join spans both arms
    // of one gate: an .If always cancels one arm, so such a join would itself always cancel.
    private readonly List<(Guid[] ThenIds, Guid[] OtherwiseIds)> _gateArms = [];

    internal TypedWorkflowBuilder(
        BackWaveClient client, string? name, ReadOnlyMemory<byte>? inputJson, Guid workflowId, bool isAppend)
    {
        _client = client;
        _name = name;
        _inputJson = inputJson;
        _isAppend = isAppend;
        WorkflowId = workflowId;
    }

    // Sub-builder used by Parallel: shares the parent's node list (so every branch step lands in the one
    // graph and one acyclic check) but carries its own local frontier seeded from the fan-out root. Build /
    // EnqueueAsync are never called on a sub-builder, so name / input / isAppend are left at their defaults.
    private TypedWorkflowBuilder(TypedWorkflowBuilder parent, IReadOnlyList<Guid> rootFrontier)
    {
        _client = parent._client;
        _nodes = parent._nodes;
        _gateArms = parent._gateArms;
        WorkflowId = parent.WorkflowId;
        _frontier.AddRange(rootFrontier);
        _isSubBuilder = true;
    }

    // Splice constructor for child-workflow grafting (.ThenWorkflow): a nested builder that shares the
    // parent's node list - so both live in one flat graph with one set of Guids and edges - and starts
    // its frontier at a COPY of the parent's current frontier, so the child's root steps depend on the
    // parent frontier while the child evolves its own frontier as it builds. Never enqueued directly;
    // the parent reads the child's resulting tips back and adopts them as the new frontier.
    private TypedWorkflowBuilder(TypedWorkflowBuilder parent)
    {
        _client = parent._client;
        _name = parent._name;
        _inputJson = parent._inputJson;
        _isAppend = parent._isAppend;
        WorkflowId = parent.WorkflowId;
        _nodes = parent._nodes;            // shared - one flat graph
        _gateArms = parent._gateArms;      // shared - a child's gates validate with the parent's
        _frontier = [.. parent._frontier]; // copied - the child owns its own frontier
        _isSubBuilder = true;
    }

    /// <summary>The identity of the workflow being built (a fresh Guid, or the target on an append).</summary>
    public Guid WorkflowId { get; }

    private void ThrowIfSubBuilder()
    {
        if (_isSubBuilder)
        {
            throw new InvalidOperationException(
                "This builder is a branch/child sub-builder that accretes steps into its parent workflow; " +
                "it cannot be built or enqueued on its own. Build and enqueue the top-level workflow instead.");
        }
    }

    private sealed record Node(
        Guid JobId, Type StepType, string? Disambiguator, string WireName, ReadOnlyMemory<byte> StepPayload,
        string Queue, DependencyMode Mode, IReadOnlyList<Guid> Parents, JobTags Tags);

    /// <summary>
    /// Chains a step that depends on the current <b>frontier</b> (the previously added step, or nothing for
    /// the first step) and becomes the new frontier - the linear common case. The step type is inferred
    /// from <paramref name="step"/>. After an <see cref="If{TGate, TStep, TOut}(Action{TypedWorkflowBuilder}, Action{TypedWorkflowBuilder}?, string?, string?, JobTags?)"/>
    /// the frontier holds both arms' tips, so a plain <see cref="DependencyMode.OnSuccess"/> continuation over
    /// them would always cancel (the gate always cancels one arm) - pass <paramref name="mode"/> as
    /// <see cref="DependencyMode.OnAnyTerminal"/> to converge past a conditional.
    /// </summary>
    /// <typeparam name="TStep">The step type; must be a registered <c>[Job]</c> payload wearing <see cref="IWorkflowStep"/>.</typeparam>
    /// <param name="step">The step payload instance to run.</param>
    /// <param name="mode">When this step releases relative to the frontier: by default only once every frontier step succeeded; <see cref="DependencyMode.OnAnyTerminal"/> releases once every frontier step is terminal whatever the outcome (needed to converge past an <c>If</c>).</param>
    /// <param name="name">An optional disambiguation name, required only when the same step type is used more than once in this workflow.</param>
    /// <param name="queue">The queue this step runs on. Defaults to the step type's registered queue.</param>
    /// <param name="tags">Extra tags to attach, unioned with the step type's default tags. Additive - defaults are never removed.</param>
    /// <returns>The same builder, so steps can be chained fluently.</returns>
    /// <exception cref="InvalidWorkflowException">The step identity (type plus optional name) is already used in this workflow.</exception>
    /// <exception cref="InvalidOperationException">The step type has no registration; register it as a job first.</exception>
    public TypedWorkflowBuilder Then<TStep>(
        TStep step, DependencyMode mode = DependencyMode.OnSuccess,
        string? name = null, string? queue = null, JobTags? tags = null)
        where TStep : IWorkflowStep
        => Append(step, [.. _frontier], mode, name, queue, tags);

    /// <summary>
    /// Chains a parameterless step that depends on the current frontier and becomes the new frontier -
    /// type-only sugar for <c>Then(new TStep())</c>.
    /// </summary>
    /// <typeparam name="TStep">The parameterless step type; must be a registered <c>[Job]</c> payload wearing <see cref="IWorkflowStep"/>.</typeparam>
    /// <param name="mode">When this step releases relative to the frontier: by default only once every frontier step succeeded; <see cref="DependencyMode.OnAnyTerminal"/> releases once every frontier step is terminal whatever the outcome (needed to converge past an <c>If</c>).</param>
    /// <param name="name">An optional disambiguation name, required only when the same step type is used more than once.</param>
    /// <param name="queue">The queue this step runs on. Defaults to the step type's registered queue.</param>
    /// <param name="tags">Extra tags to attach, unioned with the step type's default tags.</param>
    /// <returns>The same builder, so steps can be chained fluently.</returns>
    /// <exception cref="InvalidWorkflowException">The step identity is already used in this workflow.</exception>
    /// <exception cref="InvalidOperationException">The step type has no registration; register it as a job first.</exception>
    public TypedWorkflowBuilder Then<TStep>(
        DependencyMode mode = DependencyMode.OnSuccess,
        string? name = null, string? queue = null, JobTags? tags = null)
        where TStep : IWorkflowStep, new()
        => Then(new TStep(), mode, name, queue, tags);

    /// <summary>
    /// Chains a fan-in step that depends on the named upstream step types (a join). Each type in
    /// <paramref name="after"/> is resolved to the step of that type already added to this workflow; the
    /// new step becomes the new frontier.
    /// </summary>
    /// <typeparam name="TStep">The join step type; must be a registered <c>[Job]</c> payload wearing <see cref="IWorkflowStep"/>.</typeparam>
    /// <param name="step">The join step payload instance to run.</param>
    /// <param name="after">The upstream step types this join waits on. Each must name exactly one step already in the workflow.</param>
    /// <param name="mode">When the join releases: by default only once every parent succeeded; <see cref="DependencyMode.OnAnyTerminal"/> releases once every parent is terminal whatever the outcome.</param>
    /// <param name="afterExisting">On an append builder, ids of existing workflow members this join also waits on. On a fresh (non-append) workflow these ids match no member, so the enqueue fails its dependency-containment check - supply them only when appending.</param>
    /// <param name="name">An optional disambiguation name, required only when the same step type is used more than once.</param>
    /// <param name="queue">The queue this step runs on. Defaults to the step type's registered queue.</param>
    /// <param name="tags">Extra tags to attach, unioned with the step type's default tags.</param>
    /// <returns>The same builder, so steps can be chained fluently.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="after"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidWorkflowException">A type in <paramref name="after"/> names no step, or more than one, in this workflow; or the step identity is already used.</exception>
    /// <exception cref="InvalidOperationException">The step type has no registration; register it as a job first.</exception>
    public TypedWorkflowBuilder Then<TStep>(
        TStep step,
        IReadOnlyList<Type> after,
        DependencyMode mode = DependencyMode.OnSuccess,
        IEnumerable<Guid>? afterExisting = null,
        string? name = null,
        string? queue = null,
        JobTags? tags = null)
        where TStep : IWorkflowStep
    {
        ArgumentNullException.ThrowIfNull(after);
        var parents = after.Select(t => ResolveByType(t)).Concat(afterExisting ?? []).ToList();
        return Append(step, parents, mode, name, queue, tags);
    }

    /// <summary>
    /// Chains a fan-in step that depends on the given upstream <see cref="WorkflowStepRef"/>s (a join),
    /// each of which may carry a disambiguation name to pick one of several steps of the same type. Use this
    /// overload when a fan-in must reference a repeated step type; a plain <see cref="System.Type"/> still
    /// converts implicitly, so a name-less reference reads exactly like the by-type overload.
    /// </summary>
    /// <typeparam name="TStep">The join step type; must be a registered <c>[Job]</c> payload wearing <see cref="IWorkflowStep"/>.</typeparam>
    /// <param name="step">The join step payload instance to run.</param>
    /// <param name="after">The upstream steps this join waits on. Each reference must name exactly one step already in the workflow, using its disambiguation name when its type is repeated.</param>
    /// <param name="mode">When the join releases: by default only once every parent succeeded; <see cref="DependencyMode.OnAnyTerminal"/> releases once every parent is terminal whatever the outcome.</param>
    /// <param name="afterExisting">On an append builder, ids of existing workflow members this join also waits on. On a fresh (non-append) workflow these ids match no member, so the enqueue fails its dependency-containment check - supply them only when appending.</param>
    /// <param name="name">An optional disambiguation name, required only when the same step type is used more than once.</param>
    /// <param name="queue">The queue this step runs on. Defaults to the step type's registered queue.</param>
    /// <param name="tags">Extra tags to attach, unioned with the step type's default tags.</param>
    /// <returns>The same builder, so steps can be chained fluently.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="after"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidWorkflowException">A reference in <paramref name="after"/> names no step, or more than one, in this workflow; or the step identity is already used.</exception>
    /// <exception cref="InvalidOperationException">The step type has no registration; register it as a job first.</exception>
    public TypedWorkflowBuilder Then<TStep>(
        TStep step,
        IReadOnlyList<WorkflowStepRef> after,
        DependencyMode mode = DependencyMode.OnSuccess,
        IEnumerable<Guid>? afterExisting = null,
        string? name = null,
        string? queue = null,
        JobTags? tags = null)
        where TStep : IWorkflowStep
    {
        ArgumentNullException.ThrowIfNull(after);
        var parents = after.Select(r => ResolveByType(r.StepType, r.Name)).Concat(afterExisting ?? []).ToList();
        return Append(step, parents, mode, name, queue, tags);
    }

    /// <summary>
    /// Fans out one branch per <paramref name="branches"/> entry from the current frontier, then makes the
    /// set of all branch tips the new frontier. Each branch is an independent sub-pipeline rooted at the
    /// frontier as it stood before this call: a <see cref="WorkflowBranch.Step{TStep}(TStep, string?, string?, JobTags?)"/>
    /// runs a single step, while a <see cref="WorkflowBranch.Do(Action{TypedWorkflowBuilder})"/> hands you a
    /// sub-builder to chain several steps (and nest further <c>Parallel</c> calls) inside that one branch.
    /// The next <see cref="Then{TStep}(TStep, DependencyMode, string?, string?, JobTags?)"/> depends on <b>every</b> branch
    /// tip and so becomes the join - there is no synthetic join node. Leaving no follow-on step keeps the
    /// branches as parallel leaves.
    /// </summary>
    /// <param name="branches">The branches to fan out; each is a single step or a multi-step sub-builder. At least one is required.</param>
    /// <returns>The same builder, so the join step (or more branches) can be chained fluently.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="branches"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidWorkflowException">No branch was supplied, a branch added no step, or a branch reused a step identity already in the workflow.</exception>
    /// <exception cref="InvalidOperationException">A branch step type has no registration; register it as a job first.</exception>
    /// <example>
    /// Charge, then two parallel branches (one a single step, one a two-step sub-pipeline), then a join that
    /// waits on both branch tips:
    /// <code>
    /// builder
    ///     .Then(new ChargeStep(orderId))
    ///     .Parallel(
    ///         WorkflowBranch.Step(new EmailReceiptStep(orderId)),
    ///         WorkflowBranch.Do(b => b.Then(new PackStep(orderId)).Then(new ShipStep(orderId))))
    ///     .Then(new CloseStep(orderId), after: [typeof(EmailReceiptStep), typeof(ShipStep)]);
    /// </code>
    /// </example>
    public TypedWorkflowBuilder Parallel(params WorkflowBranch[] branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        if (branches.Length == 0)
        {
            throw new InvalidWorkflowException("A parallel fan-out needs at least one branch.");
        }

        var root = _frontier.ToArray();
        var tips = new List<Guid>();
        foreach (var branch in branches)
        {
            var before = _nodes.Count;
            var sub = new TypedWorkflowBuilder(this, root);
            branch.Apply(sub);
            if (_nodes.Count == before)
            {
                throw new InvalidWorkflowException("A parallel branch must add at least one step.");
            }
            tips.AddRange(sub._frontier);
        }

        _frontier.Clear();
        _frontier.AddRange(tips);
        return this;
    }

    /// <summary>
    /// Fast path for a fan-out whose branches are each a single step: fans out one branch per step from the
    /// current frontier, then makes the set of those steps the new frontier. Equivalent to a
    /// <see cref="Parallel(WorkflowBranch[])"/> call wrapping each step in
    /// <see cref="WorkflowBranch.Step{TStep}(TStep, string?, string?, JobTags?)"/>. The next
    /// <see cref="Then{TStep}(TStep, DependencyMode, string?, string?, JobTags?)"/> depends on every step and becomes the join.
    /// </summary>
    /// <param name="steps">The single-step branches to fan out. At least one is required.</param>
    /// <returns>The same builder, so the join step (or more branches) can be chained fluently.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="steps"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidWorkflowException">No step was supplied, or a step reused an identity already in the workflow.</exception>
    /// <exception cref="InvalidOperationException">A step type has no registration; register it as a job first.</exception>
    /// <example>
    /// Charge, then notify and write a receipt in parallel, then close once both are done:
    /// <code>
    /// builder
    ///     .Then(new ChargeStep(orderId))
    ///     .Parallel(new NotifyStep(orderId), new ReceiptStep(orderId))
    ///     .Then(new CloseStep(orderId), after: [typeof(NotifyStep), typeof(ReceiptStep)]);
    /// </code>
    /// </example>
    public TypedWorkflowBuilder Parallel(params IWorkflowStep[] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Length == 0)
        {
            throw new InvalidWorkflowException("A parallel fan-out needs at least one branch.");
        }

        return Parallel(steps.Select(s => WorkflowBranch.Step(s)).ToArray());
    }

    /// <summary>
    /// Adds a <b>runtime conditional branch</b> that never reshapes the graph. Both arms are enqueued up
    /// front and a <b>gate step</b> is inserted after the current frontier; at run time the gate reads the
    /// already-decided output of the ancestor its <typeparamref name="TGate"/> predicate observes, and
    /// cancels the arm that was not taken (its whole subtree), letting the taken arm proceed. No node is
    /// created, skipped, or reordered on a result - a node that was always in the graph is cancelled - so
    /// the below-boundary graph shape is fixed at build time. The gate is a child of the current frontier,
    /// and each arm is rooted at the gate (its first step depends on the gate, not on the frontier); the set
    /// of both arms' tips becomes the new frontier, so a following fan-in join over both tips with a
    /// release-on-any-terminal mode converges after exactly one arm ran (the other having reached the
    /// terminal cancelled state). Register the gate step
    /// <see cref="WorkflowGate{TGate, TStep, TOut}"/> and its handler
    /// <see cref="WorkflowGateHandler{TGate, TStep, TOut}"/> as a job, and make the observed
    /// <typeparamref name="TStep"/> a prior step, so the gate can pull its output at run time.
    /// <para>
    /// <b>Status.</b> Because the not-taken arm reaches the terminal <b>cancelled</b> state, a workflow that
    /// uses <c>If</c> derives a <b>Cancelled</b> status even when every step that ran succeeded - a cancelled
    /// member makes the whole-workflow status Cancelled (never Failed). This is expected for conditional
    /// workflows; read per-step state, not the derived status, to tell a healthy conditional run from a
    /// genuinely aborted one.
    /// </para>
    /// </summary>
    /// <typeparam name="TGate">The predicate type deciding which arm runs; it reads the observed ancestor's output.</typeparam>
    /// <typeparam name="TStep">The ancestor step the predicate observes; it must be a prior step so the gate can pull its output.</typeparam>
    /// <typeparam name="TOut">The output value type <typeparamref name="TStep"/> declares it produces.</typeparam>
    /// <param name="then">Builds the primary arm on a sub-builder rooted at the gate. Must add at least one step; it runs when the predicate returns <see langword="true"/>.</param>
    /// <param name="otherwise">Optionally builds the alternate arm on a sub-builder rooted at the gate; it runs when the predicate returns <see langword="false"/>. When omitted, a <see langword="false"/> predicate simply cancels the primary arm and runs nothing in its place.</param>
    /// <param name="name">An optional disambiguation name for the gate step, required only when the same gate type is used more than once in this workflow.</param>
    /// <param name="queue">The queue the gate step runs on. Defaults to the gate step type's registered queue.</param>
    /// <param name="tags">Extra tags to attach to the gate step, unioned with its default tags. Additive - defaults are never removed.</param>
    /// <returns>The same builder, with both arms' tips as the new frontier, so a converging join can be chained fluently.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="then"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidWorkflowException">An arm added no step, an arm reused a step identity already in the workflow, or the gate step identity is already used.</exception>
    /// <exception cref="InvalidOperationException">The gate step type has no registration; register <see cref="WorkflowGate{TGate, TStep, TOut}"/> and its handler as a job.</exception>
    /// <example>
    /// Price an order, then run the express arm when the total is large and the standard arm otherwise,
    /// and converge once whichever arm ran is done:
    /// <code>
    /// public sealed class LargeOrder : IWorkflowGate&lt;PriceOrder, OrderTotal&gt;
    /// {
    ///     public bool Enter(DependencyOutput&lt;OrderTotal&gt; observed)
    ///         =&gt; observed.HasOutput &amp;&amp; observed.Output!.Cents &gt; 100_000;
    /// }
    ///
    /// builder
    ///     .Then(new PriceOrder(orderId))
    ///     .If&lt;LargeOrder, PriceOrder, OrderTotal&gt;(
    ///         then: b =&gt; b.Then(new ExpressShip(orderId)),
    ///         otherwise: b =&gt; b.Then(new StandardShip(orderId)))
    ///     .Then(new CloseOrder(orderId),
    ///         after: [typeof(ExpressShip), typeof(StandardShip)],
    ///         mode: DependencyMode.OnAnyTerminal);
    /// </code>
    /// </example>
    public TypedWorkflowBuilder If<TGate, TStep, TOut>(
        Action<TypedWorkflowBuilder> then,
        Action<TypedWorkflowBuilder>? otherwise = null,
        string? name = null,
        string? queue = null,
        JobTags? tags = null)
        where TGate : IWorkflowGate<TStep, TOut>, new()
        where TStep : IWorkflowStep<TOut>
    {
        ArgumentNullException.ThrowIfNull(then);

        // Mint the gate id first so both arms can depend on it, then build the arms (recording every id
        // each adds, head and descendants alike) so the gate can bake in exactly which subtree to cancel.
        // The gate's own identity check is deferred to AddNode below - run AFTER the arms are built, so a
        // same-identity gate nested inside an arm is caught rather than passing an inline pre-arm check.
        var gateId = Guid.NewGuid();
        var gateParents = _frontier.ToArray();

        var thenIds = BuildArm(gateId, then, "then", out var thenTips);
        var otherwiseTips = Array.Empty<Guid>();
        var otherwiseIds = otherwise is null
            ? []
            : BuildArm(gateId, otherwise, "otherwise", out otherwiseTips);

        var gatePayload = new WorkflowGate<TGate, TStep, TOut>(thenIds, otherwiseIds);
        AddNode(gatePayload, gateParents, DependencyMode.OnSuccess, name, queue, tags, id: gateId);
        // Record both arm-subtrees so Build can reject an OnSuccess join that spans them (which would
        // always cancel, since the gate always cancels one arm - the .If continuation footgun).
        _gateArms.Add((thenIds, otherwiseIds));

        // Both arms' tips become the frontier: a following fan-in over them (release-on-any-terminal)
        // converges once one arm succeeded and the other reached the terminal cancelled state.
        _frontier.Clear();
        _frontier.AddRange(thenTips);
        _frontier.AddRange(otherwiseTips);
        return this;
    }

    /// <summary>
    /// Adds a <b>seed-aware runtime conditional branch</b> that never reshapes the graph. Like
    /// <see cref="If{TGate, TStep, TOut}(Action{TypedWorkflowBuilder}, Action{TypedWorkflowBuilder}?, string?, string?, JobTags?)"/>,
    /// both arms are enqueued up front and a <b>gate step</b> is inserted after the current frontier; at run
    /// time the gate reads already-decided data - the observed ancestor's output <b>and</b> the workflow's
    /// immutable Workflow Input seed - evaluates its <typeparamref name="TGate"/> predicate, and cancels the
    /// arm that was not taken (its whole subtree), letting the taken arm proceed. Use this overload when the
    /// decision mixes the seed with an ancestor's output; when the decision needs no ancestor output at all,
    /// prefer a build-time branch that shapes the graph from the seed instead. The predicate is read-only: it
    /// receives only the observed output and the typed seed, never this builder or the job context, so it
    /// cannot enqueue, cancel, or emit output. No node is created, skipped, or reordered on a result - a node
    /// that was always in the graph is cancelled - so the below-boundary graph shape is fixed at build time.
    /// The gate is a child of the current frontier, and each arm is rooted at the gate; the set of both arms'
    /// tips becomes the new frontier, so a following fan-in join over both tips with a release-on-any-terminal
    /// mode converges after exactly one arm ran (the other having reached the terminal cancelled state).
    /// Register the gate step <see cref="WorkflowGate{TGate, TStep, TOut, TInput}"/> and its handler
    /// <see cref="WorkflowGateHandler{TGate, TStep, TOut, TInput}"/> as a job, make the observed
    /// <typeparamref name="TStep"/> a prior step so the gate can pull its output, and start the workflow with
    /// a <typeparamref name="TInput"/> seed so the gate can read it.
    /// <para>
    /// <b>Status.</b> Because the not-taken arm reaches the terminal <b>cancelled</b> state, a workflow that
    /// uses this branch derives a <b>Cancelled</b> status even when every step that ran succeeded - a
    /// cancelled member makes the whole-workflow status Cancelled (never Failed). This is expected for
    /// conditional workflows; read per-step state, not the derived status, to tell a healthy conditional run
    /// from a genuinely aborted one.
    /// </para>
    /// </summary>
    /// <typeparam name="TGate">The predicate type deciding which arm runs; it reads the observed ancestor's output and the Workflow Input seed.</typeparam>
    /// <typeparam name="TStep">The ancestor step the predicate observes; it must be a prior step so the gate can pull its output.</typeparam>
    /// <typeparam name="TOut">The output value type <typeparamref name="TStep"/> declares it produces.</typeparam>
    /// <typeparam name="TInput">The Workflow Input seed type the predicate reads; the same type the workflow was started with.</typeparam>
    /// <param name="then">Builds the primary arm on a sub-builder rooted at the gate. Must add at least one step; it runs when the predicate returns <see langword="true"/>.</param>
    /// <param name="otherwise">Optionally builds the alternate arm on a sub-builder rooted at the gate; it runs when the predicate returns <see langword="false"/>. When omitted, a <see langword="false"/> predicate simply cancels the primary arm and runs nothing in its place.</param>
    /// <param name="name">An optional disambiguation name for the gate step, required only when the same gate type is used more than once in this workflow.</param>
    /// <param name="queue">The queue the gate step runs on. Defaults to the gate step type's registered queue.</param>
    /// <param name="tags">Extra tags to attach to the gate step, unioned with its default tags. Additive - defaults are never removed.</param>
    /// <returns>The same builder, with both arms' tips as the new frontier, so a converging join can be chained fluently.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="then"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidWorkflowException">An arm added no step, an arm reused a step identity already in the workflow, or the gate step identity is already used.</exception>
    /// <exception cref="InvalidOperationException">The gate step type has no registration; register <see cref="WorkflowGate{TGate, TStep, TOut, TInput}"/> and its handler as a job.</exception>
    /// <example>
    /// Price an order, then run the express arm when the total clears a seed-supplied threshold and the
    /// standard arm otherwise, and converge once whichever arm ran is done:
    /// <code>
    /// public sealed record CheckoutSeed(int FreeShipCents) : IWorkflowInput;
    ///
    /// public sealed class OverThreshold : IWorkflowGate&lt;PriceOrder, OrderTotal, CheckoutSeed&gt;
    /// {
    ///     public bool Enter(DependencyOutput&lt;OrderTotal&gt; observed, CheckoutSeed input)
    ///         =&gt; observed.HasOutput &amp;&amp; observed.Output!.Cents &gt; input.FreeShipCents;
    /// }
    ///
    /// await client.Workflow(new CheckoutSeed(FreeShipCents: 100_000))
    ///     .Then(new PriceOrder(orderId))
    ///     .If&lt;OverThreshold, PriceOrder, OrderTotal, CheckoutSeed&gt;(
    ///         then: b =&gt; b.Then(new ExpressShip(orderId)),
    ///         otherwise: b =&gt; b.Then(new StandardShip(orderId)))
    ///     .Then(new CloseOrder(orderId),
    ///         after: [typeof(ExpressShip), typeof(StandardShip)],
    ///         mode: DependencyMode.OnAnyTerminal)
    ///     .EnqueueAsync();
    /// </code>
    /// </example>
    public TypedWorkflowBuilder If<TGate, TStep, TOut, TInput>(
        Action<TypedWorkflowBuilder> then,
        Action<TypedWorkflowBuilder>? otherwise = null,
        string? name = null,
        string? queue = null,
        JobTags? tags = null)
        where TGate : IWorkflowGate<TStep, TOut, TInput>, new()
        where TStep : IWorkflowStep<TOut>
        where TInput : IWorkflowInput
    {
        ArgumentNullException.ThrowIfNull(then);

        // Mint the gate id first so both arms can depend on it, then build the arms (recording every id
        // each adds, head and descendants alike) so the gate can bake in exactly which subtree to cancel.
        // The gate's own identity check is deferred to AddNode below - run AFTER the arms are built, so a
        // same-identity gate nested inside an arm is caught rather than passing an inline pre-arm check.
        var gateId = Guid.NewGuid();
        var gateParents = _frontier.ToArray();

        var thenIds = BuildArm(gateId, then, "then", out var thenTips);
        var otherwiseTips = Array.Empty<Guid>();
        var otherwiseIds = otherwise is null
            ? []
            : BuildArm(gateId, otherwise, "otherwise", out otherwiseTips);

        var gatePayload = new WorkflowGate<TGate, TStep, TOut, TInput>(thenIds, otherwiseIds);
        AddNode(gatePayload, gateParents, DependencyMode.OnSuccess, name, queue, tags, id: gateId);
        // Record both arm-subtrees so Build can reject an OnSuccess join that spans them (which would
        // always cancel, since the gate always cancels one arm - the .If continuation footgun).
        _gateArms.Add((thenIds, otherwiseIds));

        // Both arms' tips become the frontier: a following fan-in over them (release-on-any-terminal)
        // converges once one arm succeeded and the other reached the terminal cancelled state.
        _frontier.Clear();
        _frontier.AddRange(thenTips);
        _frontier.AddRange(otherwiseTips);
        return this;
    }

    // Builds one arm of a conditional on a sub-builder rooted at the gate, returning every id the arm
    // added (head and descendants) and, via tips, the arm's frontier for the converging join. The returned
    // ids are ordered ancestors-first, so the gate handler's reverse iteration cancels the arm
    // descendants-first (see TopologicalArmOrder).
    private Guid[] BuildArm(Guid gateId, Action<TypedWorkflowBuilder> build, string armLabel, out Guid[] tips)
    {
        var before = _nodes.Count;
        var sub = new TypedWorkflowBuilder(this, [gateId]);
        build(sub);
        if (_nodes.Count == before)
        {
            throw new InvalidWorkflowException(
                $"The '{armLabel}' arm of a conditional branch must add at least one step.");
        }

        tips = sub._frontier.ToArray();

        var armNodes = _nodes.Skip(before).ToList();
        var armIds = armNodes.Select(n => n.JobId).ToHashSet();

        // Every arm node must root on the gate or another node in this same arm. A node that depends on a
        // step OUTSIDE the arm - a pre-gate step reached with after:, or an afterExisting id - would run
        // whenever that outside step succeeded, regardless of the gate's decision, and so escape the cancel
        // a not-taken arm relies on (a later cancel is a no-op once the step already succeeded). Reject it
        // at build rather than shipping an arm step that silently ignores the branch.
        foreach (var node in armNodes)
        {
            foreach (var parent in node.Parents)
            {
                if (parent != gateId && !armIds.Contains(parent))
                {
                    throw new InvalidWorkflowException(
                        $"Step '{node.StepType.Name}' in the '{armLabel}' arm depends on a step outside the " +
                        "arm, so it would run regardless of the gate's decision and escape the cancellation of " +
                        "a not-taken arm. An arm step may depend only on the gate or another step in the same " +
                        "arm; move a shared dependency before the If, or converge after the If with " +
                        "mode: DependencyMode.OnAnyTerminal.");
                }
            }
        }

        return TopologicalArmOrder(armNodes, armIds);
    }

    // Orders an arm's nodes ancestors-first, so the gate handler cancelling in reverse walks
    // descendants-first and never transiently releases a latched in-arm node (one with a release-on-any-
    // terminal edge) into a claimable Scheduled state before its own cancel lands. Add order is NOT always
    // topological: a nested .If appends its gate node AFTER its own arm steps, and a compensation retro-
    // wires an earlier node to depend on a later one - both put a descendant before its ancestor. The sort
    // is stable, preserving add order whenever it is already valid, so a flat arm's baked cancel-list stays
    // byte-identical. Out-of-arm parents (only the gate, after the containment check above) count as
    // already-satisfied roots. A pick can fail only on a cycle Build later rejects; append the remainder in
    // add order rather than spinning.
    private static Guid[] TopologicalArmOrder(List<Node> armNodes, HashSet<Guid> armIds)
    {
        var ordered = new List<Guid>(armNodes.Count);
        var emitted = new HashSet<Guid>();
        var remaining = new List<Node>(armNodes);
        while (remaining.Count > 0)
        {
            var next = remaining.FindIndex(
                n => n.Parents.All(parent => !armIds.Contains(parent) || emitted.Contains(parent)));
            if (next < 0)
            {
                ordered.AddRange(remaining.Select(n => n.JobId));
                break;
            }
            ordered.Add(remaining[next].JobId);
            emitted.Add(remaining[next].JobId);
            remaining.RemoveAt(next);
        }
        return [.. ordered];
    }

    /// <summary>
    /// Splices a reusable child workflow definition into this graph as an <b>inline</b> subgraph: the
    /// child's <c>Build</c> runs now, at construction, and its steps are grafted onto the same graph rooted
    /// at the current frontier, so the child's first steps depend on the frontier and the child's leaf steps
    /// become the new frontier for whatever you chain next. There is no nested identity - the result is one
    /// flat graph, one workflow row, one derived status, and one retention unit, so a failing step inside the
    /// child fails this workflow exactly like any other in-graph step. The child receives a build-time
    /// <paramref name="seed"/> to shape its graph and construct its step payloads; any per-run values a child
    /// step needs from an upstream step flow through ordinary Job Output, not shared state. The
    /// <paramref name="seed"/> is <b>build-time only</b>: the spliced child shares the parent's Workflow Input,
    /// so a child step calling <c>ctx.Input&lt;T&gt;()</c> reads the parent's input, not the seed - read the
    /// seed's values into the child's step payloads at build time instead. When you instead
    /// want the child to run under its own independent identity with no join back into this graph, do not use
    /// this method - have a step call <c>client.StartWorkflow&lt;TChild, TSeed&gt;(seed, seedTypeInfo)</c> to
    /// start it fire-and-forget.
    /// </summary>
    /// <typeparam name="TChild">The child workflow definition to splice in.</typeparam>
    /// <typeparam name="TSeed">The child definition's build-time seed type.</typeparam>
    /// <param name="seed">The build-time seed handed to the child's <c>Build</c> to shape its graph and construct its step payloads. Not available to a child step at run time via <c>ctx.Input</c> - the child shares the parent's Workflow Input.</param>
    /// <returns>The same builder, with the child's leaf steps as the new frontier, so chaining continues fluently.</returns>
    /// <exception cref="InvalidWorkflowException">The child added no step, or a child step's identity (type plus optional name) collides with a step already in the combined graph; give the repeated step a disambiguation name.</exception>
    /// <exception cref="InvalidOperationException">A child step type has no registration; register it as a job first.</exception>
    /// <example>
    /// <code>
    /// // Run the child's steps after ChargeCard, then continue on the child's exit point.
    /// await client.Workflow("checkout")
    ///     .Then(new ChargeCard(orderId))
    ///     .ThenWorkflow&lt;FulfilmentWorkflow, FulfilmentSeed&gt;(new FulfilmentSeed(orderId))
    ///     .Then(new SendReceipt(orderId))
    ///     .EnqueueAsync();
    /// </code>
    /// </example>
    public TypedWorkflowBuilder ThenWorkflow<TChild, TSeed>(TSeed seed)
        where TChild : IWorkflow<TSeed>, new()
    {
        var before = _nodes.Count;
        var childBuilder = new TypedWorkflowBuilder(this);
        new TChild().Build(childBuilder, seed);
        if (_nodes.Count == before)
        {
            throw new InvalidWorkflowException(
                $"The spliced child workflow '{typeof(TChild).Name}' added no step; its Build must add at least one.");
        }
        // Adopt the child's leaf steps (its evolved frontier) as this graph's new frontier.
        _frontier.Clear();
        _frontier.AddRange(childBuilder._frontier);
        return this;
    }

    /// <summary>
    /// Attaches a <b>compensation</b> (a saga-style undo) that guards the work built so far - the current
    /// frontier. The compensation is an ordinary step that always becomes reachable once the protected
    /// work is terminal, whatever the outcome; its handler <b>reads</b> the protected work's already-decided
    /// state and decides whether to undo. Undo when the protected work failed; do nothing when it
    /// succeeded - so the compensation always runs, but usually just no-ops. The handler learns what to
    /// reverse (an id, an amount) by reading the protected step's Job Output, naming that step by its type;
    /// give a protected step that needs undoing an <see cref="IWorkflowStep{TOut}"/> output the undo can pull.
    /// The main chain is unaffected: whatever you chain after this still depends on the protected work, not
    /// on the undo. Call it again for earlier steps to build a saga - successive compensations undo in
    /// <b>reverse</b> order of the work they protect, wired as static edges, so the last-protected step's
    /// undo runs first. The reverse-order chain is scoped to the builder it is called on: a compensation
    /// added inside a <c>Parallel</c>/<c>If</c> branch or a spliced <c>ThenWorkflow</c> child chains only
    /// with other compensations added on that same sub-builder, so reverse-order undo does not span a splice.
    /// </summary>
    /// <typeparam name="TUndo">The undo step type; must be a registered <c>[Job]</c> payload wearing <see cref="IWorkflowStep"/>.</typeparam>
    /// <param name="undo">The undo step payload instance to run.</param>
    /// <param name="name">An optional disambiguation name, required only when the same undo step type is used more than once in this workflow.</param>
    /// <param name="queue">The queue the undo runs on. Defaults to the undo type's registered queue.</param>
    /// <param name="tags">Extra tags to attach, unioned with the undo type's default tags. Additive - defaults are never removed.</param>
    /// <returns>The same builder, so steps can be chained fluently; the frontier is left on the protected work.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="undo"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidWorkflowException">There is no preceding step to protect, or the undo step identity (type plus optional name) is already used in this workflow.</exception>
    /// <exception cref="InvalidOperationException">The undo step type has no registration; register it as a job first.</exception>
    /// <example>
    /// Charge, reserve, then ship; if shipping fails, release the reservation and refund the charge, in that
    /// reverse order. Each undo handler reads the risky step's terminal state to decide, and pulls the step
    /// it reverses for the id to undo:
    /// <code>
    /// await client.Workflow("checkout")
    ///     .Then(new ChargeCard(orderId))
    ///     .Then(new ReserveStock(orderId))
    ///     .Then(new ShipOrder(orderId))
    ///     .WithCompensation(new RefundCharge())     // undoes ChargeCard; runs last
    ///     .WithCompensation(new ReleaseStock())     // undoes ReserveStock; runs first
    ///     .EnqueueAsync();
    ///
    /// public sealed class RefundChargeHandler : IJobHandler&lt;RefundCharge&gt;
    /// {
    ///     public async Task HandleAsync(RefundCharge job, JobContext ctx, CancellationToken ct)
    ///     {
    ///         var ship = await ctx.Output&lt;ShipOrder, ShipResult&gt;(ct);
    ///         if (ship.AncestorState == JobState.Succeeded) return;   // saga succeeded - nothing to undo
    ///         var charge = await ctx.Output&lt;ChargeCard, ChargeResult&gt;(ct);
    ///         if (charge.HasOutput) { /* refund charge.Output.ChargeId */ }
    ///     }
    /// }
    /// </code>
    /// </example>
    public TypedWorkflowBuilder WithCompensation<TUndo>(
        TUndo undo, string? name = null, string? queue = null, JobTags? tags = null)
        where TUndo : IWorkflowStep
    {
        ArgumentNullException.ThrowIfNull(undo);
        if (_frontier.Count == 0)
        {
            throw new InvalidWorkflowException(
                "WithCompensation needs a preceding step to protect; add a step with Then first.");
        }

        // OnAnyTerminal makes the compensation reachable whatever the protected work's outcome (a plain
        // OnSuccess child would be cancelled the moment the protected work fails - the opposite of what a
        // compensation needs). When the protected work is a parallel region, the frontier holds every
        // branch tip, so this fans the compensation in over all of them - reusing the existing terminal
        // fan-in mode rather than any compensation-specific mechanism.
        var protectedChain = _frontier.ToList();
        var compensation = AddNode(undo, protectedChain, DependencyMode.OnAnyTerminal, name, queue, tags);

        // Reverse-order undo as static edges: the previously registered compensation is made to wait for
        // this newer one, so the later-protected step's undo runs first. No new mode, no runtime reshaping;
        // only which compensations actually undo (versus no-op) is decided at run time by their handlers.
        if (_lastCompensationId is { } previous)
        {
            var index = _nodes.FindIndex(n => n.JobId == previous);
            _nodes[index] = _nodes[index] with
            {
                Parents = [.. _nodes[index].Parents, compensation.JobId],
            };
        }

        _lastCompensationId = compensation.JobId;
        // The frontier is deliberately left on the protected work: a compensation is a side-branch.
        return this;
    }

    /// <summary>
    /// Attaches a parameterless <b>compensation</b> that guards the current frontier - type-only sugar for
    /// <c>WithCompensation(new TUndo())</c>. The compensation always becomes reachable once the protected
    /// work is terminal; its handler reads the protected work's decided state and undoes only when it
    /// failed, else no-ops. Successive compensations undo in reverse order of the work they protect.
    /// </summary>
    /// <typeparam name="TUndo">The parameterless undo step type; must be a registered <c>[Job]</c> payload wearing <see cref="IWorkflowStep"/>.</typeparam>
    /// <param name="name">An optional disambiguation name, required only when the same undo step type is used more than once.</param>
    /// <param name="queue">The queue the undo runs on. Defaults to the undo type's registered queue.</param>
    /// <param name="tags">Extra tags to attach, unioned with the undo type's default tags. Additive - defaults are never removed.</param>
    /// <returns>The same builder, so steps can be chained fluently; the frontier is left on the protected work.</returns>
    /// <exception cref="InvalidWorkflowException">There is no preceding step to protect, or the undo step identity is already used in this workflow.</exception>
    /// <exception cref="InvalidOperationException">The undo step type has no registration; register it as a job first.</exception>
    /// <example>
    /// <code>
    /// await client.Workflow("checkout")
    ///     .Then(new ChargeCard(orderId))
    ///     .Then(new ShipOrder(orderId))
    ///     .WithCompensation&lt;RefundCharge&gt;()   // undoes ChargeCard when shipping fails
    ///     .EnqueueAsync();
    /// </code>
    /// </example>
    public TypedWorkflowBuilder WithCompensation<TUndo>(
        string? name = null, string? queue = null, JobTags? tags = null)
        where TUndo : IWorkflowStep, new()
        => WithCompensation(new TUndo(), name, queue, tags);

    // Resolves a fan-in reference to the id of the one matching step. A null name matches on type alone
    // (the common case) and is ambiguous when the type is repeated; a name additionally filters on the
    // step's disambiguator, so it picks exactly one of several same-type steps - identity (type + name) is
    // unique, so a named lookup can never be ambiguous.
    private Guid ResolveByType(Type stepType, string? name = null)
    {
        var matches = name is null
            ? _nodes.Where(n => n.StepType == stepType).ToList()
            : _nodes.Where(n => n.StepType == stepType && n.Disambiguator == name).ToList();
        return matches.Count switch
        {
            1 => matches[0].JobId,
            0 => throw new InvalidWorkflowException(name is null
                ? $"Fan-in 'after' names step type '{stepType.Name}', which is not a step in this workflow."
                : $"Fan-in 'after' names step type '{stepType.Name}' named '{name}', which is not a step in this workflow."),
            _ => throw new InvalidWorkflowException(
                $"Fan-in 'after' names step type '{stepType.Name}', which appears more than once; " +
                "disambiguate it with a name, for example after: [new WorkflowStepRef(typeof(...), \"name\")]."),
        };
    }

    private TypedWorkflowBuilder Append<TStep>(
        TStep step, IReadOnlyList<Guid> parents, DependencyMode mode, string? name, string? queue, JobTags? tags)
        where TStep : IWorkflowStep
    {
        var node = AddNode(step, parents, mode, name, queue, tags);
        _frontier.Clear();
        _frontier.Add(node.JobId);
        return this;
    }

    // Creates and records a graph node WITHOUT touching the main-chain frontier, so a caller that adds a
    // side-branch (a compensation) can leave whatever is chained next depending on the protected work
    // rather than on the side-branch. The identity check, Tag union, and payload serialization are shared
    // with the frontier-advancing Append. A caller that must know the node's id before it is added (the
    // conditional gate, whose arms root on the gate id before the gate node itself is appended) passes a
    // pre-minted id; the identity check still runs here at append time, so a same-identity node nested in an
    // arm is caught. Everyone else lets AddNode mint the id.
    private Node AddNode<TStep>(
        TStep step, IReadOnlyList<Guid> parents, DependencyMode mode, string? name, string? queue, JobTags? tags,
        Guid? id = null)
        where TStep : IWorkflowStep
    {
        var stepType = step.GetType();
        if (_nodes.Any(n => n.StepType == stepType && n.Disambiguator == name))
        {
            var which = name is null ? $"'{stepType.Name}'" : $"'{stepType.Name}' named '{name}'";
            throw new InvalidWorkflowException(
                $"Duplicate step identity {which} in the workflow; give the repeated step a disambiguation name.");
        }

        var registration = _client.Registry.GetByJobType(stepType);
        // Type-default Tags union in just like a plain enqueue (ADR 0022) - additive only.
        var mergedTags = tags is null || tags.Count == 0
            ? registration.DefaultTags
            : JobTags.From(registration.DefaultTags.Concat(tags));
        var node = new Node(
            id ?? Guid.NewGuid(),
            stepType,
            name,
            registration.WireName,
            registration.Serialize(step),
            queue ?? registration.Queue,
            mode,
            parents,
            mergedTags);
        _nodes.Add(node);
        return node;
    }

    /// <summary>
    /// Validates the graph (non-empty, no duplicate identity, acyclic) and emits the prepared
    /// <see cref="WorkflowDefinition"/> the below-boundary spine consumes. When a Workflow Input seed was
    /// supplied, it is baked into every member's payload here, and each member also carries the ambient
    /// trace context so each step's execution span links back to the enclosing trace; a seedless,
    /// parentless step's payload stays byte-identical to a standalone enqueue of the same step.
    /// </summary>
    /// <returns>The validated workflow definition, ready to enqueue.</returns>
    /// <exception cref="InvalidWorkflowException">The graph is empty, has a dependency cycle, or a step's own payload declares a property in the reserved <c>$backwave.</c> namespace BackWave uses to carry workflow metadata.</exception>
    /// <exception cref="InvalidOperationException">Called on a sub-builder handed to a <see cref="Parallel(WorkflowBranch[])"/> / <see cref="If{TGate, TStep, TOut}(Action{TypedWorkflowBuilder}, Action{TypedWorkflowBuilder}?, string?, string?, JobTags?)"/> branch or an <see cref="IWorkflow{TSeed}"/> child; those accrete into the parent graph and are never built or enqueued on their own.</exception>
    public WorkflowDefinition Build()
    {
        ThrowIfSubBuilder();
        return Build(BackWaveDiagnostics.EncodeTraceContext(Activity.Current));
    }

    // The trace-baking build. The workflow-root traceparent is stamped onto every member's TraceContext
    // (the same field a plain enqueue writes), so each member's execution span becomes a flat child of the
    // one workflow-start span; each member also carries its parent wire names for the after-edge tag.
    private WorkflowDefinition Build(string? workflowTraceContext)
    {
        if (_nodes.Count == 0)
        {
            throw new InvalidWorkflowException("A workflow needs at least one step.");
        }

        EnsureNoOnSuccessJoinSpansAConditional();
        EnsureAcyclic();

        var wireNamesById = _nodes.ToDictionary(n => n.JobId, n => n.WireName);
        var now = _client.Clock.GetUtcNow();
        // Parse the immutable seed once for the whole batch; each member takes a deep clone in Splice, so
        // the seed bytes are tokenized once here rather than re-parsed once per member.
        var hasSeed = _inputJson is not null;
        var seedTemplate = _inputJson is { } seedBytes ? JsonNode.Parse(seedBytes.Span) : null;
        var members = _nodes
            .Select(node => new NewJob(
                node.JobId,
                node.WireName,
                WorkflowInputEnvelope.Splice(
                    node.StepPayload, seedTemplate, hasSeed, ParentWireNames(node, wireNamesById), [], node.WireName),
                node.Queue,
                now)
            {
                Parents = node.Parents,
                Mode = node.Mode,
                Tags = node.Tags,
                // The workflow-root span context, baked in verbatim like any enqueue's traceparent, so
                // the member's execution span links to the workflow root.
                TraceContext = workflowTraceContext,
            })
            .ToList();

        return new WorkflowDefinition
        {
            WorkflowId = WorkflowId,
            Name = _name,
            Members = members,
            IsAppend = _isAppend,
        };
    }

    // The enqueue-time build. Unlike the pure Build above, it emits one brief PRODUCER "send" span per
    // member (each a child of the workflow-root span) and bakes that span's context as the member's
    // TraceContext - so every member's process span LINKS to its own creation, and a fan-in member also
    // links to each parent step's send context (carried opaquely in the payload envelope). This is the one
    // place the telemetry rebase touches how a member is stored; the contexts are above-boundary metadata
    // the Core never reads for scheduling.
    private WorkflowDefinition BuildWithMemberSends(Activity? workflowRoot)
    {
        if (_nodes.Count == 0)
        {
            throw new InvalidWorkflowException("A workflow needs at least one step.");
        }

        EnsureNoOnSuccessJoinSpansAConditional();
        EnsureAcyclic();

        var wireNamesById = _nodes.ToDictionary(n => n.JobId, n => n.WireName);
        var now = _client.Clock.GetUtcNow();
        var hasSeed = _inputJson is not null;
        var seedTemplate = _inputJson is { } seedBytes ? JsonNode.Parse(seedBytes.Span) : null;

        // Emit each member's send span up front and capture its context, so a fan-in member can reference
        // its parents' contexts (a parent always precedes it here, but the map is built in one pass first
        // to keep the member projection below free of ordering assumptions).
        var sendContextById = _nodes.ToDictionary(
            n => n.JobId,
            n => BackWaveDiagnostics.EmitMemberSend(workflowRoot, n.WireName, n.Queue, n.JobId));

        var members = _nodes
            .Select(node => new NewJob(
                node.JobId,
                node.WireName,
                WorkflowInputEnvelope.Splice(
                    node.StepPayload, seedTemplate, hasSeed,
                    ParentWireNames(node, wireNamesById),
                    ParentSendContexts(node, sendContextById),
                    node.WireName),
                node.Queue,
                now)
            {
                Parents = node.Parents,
                Mode = node.Mode,
                Tags = node.Tags,
                // The member's OWN send-span context: its process span links back to it, exactly as a
                // plain enqueue's process span links to its send span.
                TraceContext = sendContextById[node.JobId],
            })
            .ToList();

        return new WorkflowDefinition
        {
            WorkflowId = WorkflowId,
            Name = _name,
            Members = members,
            IsAppend = _isAppend,
        };
    }

    // The send-span trace contexts of a node's in-batch parents, for the fan-in link set. An append
    // builder may name a parent this batch does not hold; such a parent has no context here and is
    // omitted (the same treatment ParentWireNames gives an out-of-batch parent).
    private static IReadOnlyList<string> ParentSendContexts(Node node, Dictionary<Guid, string?> sendContextById)
        => [.. node.Parents
            .Where(p => sendContextById.TryGetValue(p, out var context) && context is not null)
            .Select(p => sendContextById[p]!)];

    // Resolves a node's parent ids to their wire names for the flat trace's after-edge tag. An append
    // builder may name an existing member (via afterExisting) that this batch does not hold; such a parent
    // is simply omitted, since its wire name is not known here without a store read.
    private static IReadOnlyList<string> ParentWireNames(Node node, Dictionary<Guid, string> wireNamesById)
        => [.. node.Parents.Where(wireNamesById.ContainsKey).Select(p => wireNamesById[p])];

    /// <summary>
    /// Validates and enqueues the whole workflow atomically: every member job and the workflow record are
    /// written in one all-or-nothing transaction. When a <paramref name="transaction"/> is supplied, the
    /// whole graph commits or rolls back together with your own writes on the same transaction.
    /// </summary>
    /// <param name="transaction">Optional. When supplied, the workflow commits or rolls back atomically with your own writes; the storage adapter must support transactional enqueue.</param>
    /// <param name="cancellationToken">Cancels the enqueue request.</param>
    /// <returns>The workflow's id.</returns>
    /// <exception cref="InvalidWorkflowException">The graph is empty, has a dependency cycle, or a step's own payload declares a property in the reserved <c>$backwave.</c> namespace BackWave uses to carry workflow metadata.</exception>
    /// <exception cref="NotSupportedException">A <paramref name="transaction"/> was supplied but the storage adapter does not support transactional enqueue.</exception>
    /// <exception cref="InvalidOperationException">The store rejected the workflow (for example a duplicate id, or a dependency on a non-member job), or this is a sub-builder handed to a <see cref="Parallel(WorkflowBranch[])"/> / <see cref="If{TGate, TStep, TOut}(Action{TypedWorkflowBuilder}, Action{TypedWorkflowBuilder}?, string?, string?, JobTags?)"/> branch or an <see cref="IWorkflow{TSeed}"/> child, which accrete into the parent graph and are never enqueued on their own.</exception>
    public async ValueTask<Guid> EnqueueAsync(
        DbTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        ThrowIfSubBuilder();
        if (transaction is not null && !_client.Store.SupportsTransactionalEnqueue)
        {
            throw new NotSupportedException(
                "This storage adapter does not support Transactional Enqueue " +
                "(SupportsTransactionalEnqueue is false); enqueue the workflow without a transaction instead.");
        }

        // A brief PRODUCER "send" root span marks the whole workflow start; each member gets its own send
        // span beneath it, whose context becomes the member's TraceContext. It closes as soon as the
        // enqueue returns - a start marker, not a parent that stays open while the steps run.
        using var workflowSpan = BackWaveDiagnostics.StartWorkflow(_name, _nodes.Count, _isAppend);
        var definition = BuildWithMemberSends(workflowSpan);
        var result = await _client.Store
            .EnqueueWorkflowAsync(definition, _client.Clock.GetUtcNow(), transaction, cancellationToken)
            .ConfigureAwait(false);
        return result == WorkflowEnqueueResult.Ok
            ? definition.WorkflowId
            : throw new InvalidOperationException($"Workflow enqueue failed: {result}.");
    }

    // A join over BOTH arms of one conditional must release on any terminal outcome: an .If always cancels
    // one arm, so an OnSuccess join over both arms would always cascade-cancel - the continuation silently
    // never runs. Caught at Build with a pointer to the fix (mode: OnAnyTerminal) rather than shipped as a
    // dead branch. A join over a single arm is left alone: that is a deliberate "only if this arm ran" edge.
    private void EnsureNoOnSuccessJoinSpansAConditional()
    {
        foreach (var (thenIds, otherwiseIds) in _gateArms)
        {
            var thenSet = thenIds.ToHashSet();
            var otherwiseSet = otherwiseIds.ToHashSet();
            foreach (var node in _nodes)
            {
                if (node.Mode == DependencyMode.OnSuccess
                    && node.Parents.Any(thenSet.Contains)
                    && node.Parents.Any(otherwiseSet.Contains))
                {
                    throw new InvalidWorkflowException(
                        $"Step '{node.StepType.Name}' joins both arms of a conditional with OnSuccess, so it " +
                        "would always be cancelled when the gate cancels one arm. Converge past an If with " +
                        "mode: DependencyMode.OnAnyTerminal.");
                }
            }
        }
    }

    /// <summary>DFS three-colour cycle detection over the member id → parents graph (a self-edge is a cycle).</summary>
    private void EnsureAcyclic()
    {
        var parentsById = _nodes.ToDictionary(n => n.JobId, n => n.Parents);
        var state = new Dictionary<Guid, int>(); // 0 unvisited, 1 on-stack, 2 done
        foreach (var node in _nodes)
        {
            if (state.GetValueOrDefault(node.JobId) == 0)
            {
                Visit(node.JobId, parentsById, state);
            }
        }
    }

    private static void Visit(Guid id, Dictionary<Guid, IReadOnlyList<Guid>> parentsById, Dictionary<Guid, int> state)
    {
        state[id] = 1;
        foreach (var parent in parentsById.GetValueOrDefault(id, []))
        {
            var color = state.GetValueOrDefault(parent);
            if (color == 1)
            {
                throw new InvalidWorkflowException("The workflow has a dependency cycle.");
            }
            if (color == 0 && parentsById.ContainsKey(parent))
            {
                Visit(parent, parentsById, state);
            }
        }
        state[id] = 2;
    }
}
