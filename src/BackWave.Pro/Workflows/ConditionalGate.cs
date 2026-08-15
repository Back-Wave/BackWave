using BackWave.Jobs;
using BackWave.Operations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackWave.Pro;

// The convergence primitive, face 1 (conditional branching). A gate is a statically-wired node whose
// handler pulls a decided ancestor output, evaluates a predicate, and cancels the not-taken arm through
// the above-boundary workflow-cancel operator. The graph shape never changes at runtime - both arms are
// always enqueued - so nothing below the determinism boundary reasons about the decision.

/// <summary>
/// A <b>conditional gate</b>: the predicate that decides which arm of a runtime branch runs. Implement it
/// on a small parameterless type and hand that type to the workflow builder's conditional branch; the
/// implementation runs inside a job handler at run time, reads the already-decided output of the ancestor
/// step it observes, and returns whether the primary ("then") arm should run. Because a .NET delegate
/// cannot be serialized and shipped to the worker that later runs the gate, the decision is carried as a
/// type - registered like any other job - rather than as an inline lambda.
/// </summary>
/// <typeparam name="TStep">The ancestor step whose output the predicate reads; it declares the output type it produces.</typeparam>
/// <typeparam name="TOut">The output value type <typeparamref name="TStep"/> declares it produces.</typeparam>
/// <example>
/// <code>
/// public sealed class LargeOrder : IWorkflowGate&lt;PriceOrder, OrderTotal&gt;
/// {
///     public bool Enter(DependencyOutput&lt;OrderTotal&gt; observed)
///         =&gt; observed.HasOutput &amp;&amp; observed.Output!.Cents &gt; 100_000;
/// }
/// </code>
/// </example>
public interface IWorkflowGate<TStep, TOut>
    where TStep : IWorkflowStep<TOut>
{
    /// <summary>
    /// Decides, at run time, which arm of the branch runs. Returns <see langword="true"/> to run the
    /// primary ("then") arm and cancel the alternate arm, or <see langword="false"/> to run the alternate
    /// ("otherwise") arm and cancel the primary arm. The read of <paramref name="observed"/> is over
    /// already-decided state, so the predicate is a pure function of it; <b>absence is normal</b> - an
    /// ancestor that failed, was cancelled, or succeeded emitting nothing arrives with
    /// <see cref="DependencyOutput{T}.HasOutput"/> false, and the predicate must handle that case.
    /// </summary>
    /// <param name="observed">The ancestor step's terminal state and its output, or a clean absence when it emitted none.</param>
    /// <returns><see langword="true"/> to run the primary arm; <see langword="false"/> to run the alternate arm.</returns>
    bool Enter(DependencyOutput<TOut> observed);
}

/// <summary>
/// The statically-wired <b>gate step</b> a conditional branch lowers to: an ordinary workflow step whose
/// payload carries the job ids of each arm. Both arms are enqueued up front, so the graph shape is fixed;
/// at run time the gate's handler evaluates its <typeparamref name="TGate"/> predicate and cancels the
/// arm that was not taken. You never construct this type - the workflow builder's conditional branch mints
/// it and bakes in the arm ids - but you must register it (and its handler) as a job so it can run, one
/// registration per distinct gate the workflow uses.
/// </summary>
/// <typeparam name="TGate">The predicate type that decides which arm runs.</typeparam>
/// <typeparam name="TStep">The ancestor step whose output the predicate reads.</typeparam>
/// <typeparam name="TOut">The output value type <typeparamref name="TStep"/> declares it produces.</typeparam>
/// <param name="ThenArm">The job ids of the primary arm and all its descendants, cancelled when the predicate returns <see langword="false"/>.</param>
/// <param name="OtherwiseArm">The job ids of the alternate arm and all its descendants, cancelled when the predicate returns <see langword="true"/>.</param>
/// <example>
/// Declare the gate serializable, then register it in one call after <c>AddBackWave</c>:
/// <code>
/// [JsonSerializable(typeof(WorkflowGate&lt;LargeOrder, PriceOrder, OrderTotal&gt;))]
/// public partial class AppJsonContext : JsonSerializerContext;
///
/// services.AddWorkflowGate&lt;LargeOrder, PriceOrder, OrderTotal&gt;(
///     "large-order-gate", AppJsonContext.Default.WorkflowGateLargeOrderPriceOrderOrderTotal);
/// </code>
/// </example>
public sealed record WorkflowGate<TGate, TStep, TOut>(Guid[] ThenArm, Guid[] OtherwiseArm) : IWorkflowStep
    where TGate : IWorkflowGate<TStep, TOut>, new()
    where TStep : IWorkflowStep<TOut>;

/// <summary>
/// The handler for a conditional <see cref="WorkflowGate{TGate, TStep, TOut}"/>. It pulls the observed
/// ancestor's decided output, evaluates the <typeparamref name="TGate"/> predicate, and cancels the arm
/// that was not taken through the workflow-cancel operator - so the not-taken arm and its descendants
/// reach a cancelled (terminal) state rather than running, while the taken arm proceeds. Cancellation
/// produces no failed member, so the workflow's derived status stays coherent. Register it against
/// <see cref="WorkflowGate{TGate, TStep, TOut}"/> for the same three type arguments.
/// </summary>
/// <typeparam name="TGate">The predicate type that decides which arm runs.</typeparam>
/// <typeparam name="TStep">The ancestor step whose output the predicate reads.</typeparam>
/// <typeparam name="TOut">The output value type <typeparamref name="TStep"/> declares it produces.</typeparam>
/// <param name="operator">The operator surface the not-taken arm is cancelled through.</param>
/// <param name="loggerFactory">
/// Optional. Supplies the logger the gate writes its decision (which arm ran) to at Information level.
/// Null (the default) disables the log with no allocation; the gate behaves identically either way.
/// </param>
public sealed class WorkflowGateHandler<TGate, TStep, TOut>(BackWaveOperator @operator, ILoggerFactory? loggerFactory = null)
    : IJobHandler<WorkflowGate<TGate, TStep, TOut>>
    where TGate : IWorkflowGate<TStep, TOut>, new()
    where TStep : IWorkflowStep<TOut>
{
    // The identity recorded against every per-member cancel this gate drives, so the cancellation shows
    // up in the operator audit trail as a workflow decision rather than a human action.
    private const string Actor = "workflow-conditional-gate";

    private readonly ILogger _log = loggerFactory?.CreateLogger("BackWave.Pro.Workflows") ?? NullLogger.Instance;

    /// <summary>
    /// Runs the gate: reads the observed ancestor's output, evaluates the predicate, and cancels the arm
    /// that was not taken (its whole subtree, by baked-in id).
    /// </summary>
    /// <param name="job">The gate step, carrying the two arms' baked-in job ids.</param>
    /// <param name="context">The handler's execution context, used to pull the observed ancestor's output.</param>
    /// <param name="cancellationToken">Signaled to abandon the work.</param>
    /// <returns>A task that completes once the not-taken arm has been cancelled.</returns>
    public async Task HandleAsync(
        WorkflowGate<TGate, TStep, TOut> job, JobContext context, CancellationToken cancellationToken)
    {
        var observed = await context.Output<TStep, TOut>(cancellationToken).ConfigureAwait(false);
        var enterThen = new TGate().Enter(observed);
        var notTaken = enterThen ? job.OtherwiseArm : job.ThenArm;
        WorkflowLog.GateDecided(_log, typeof(TGate).Name, enterThen ? "then" : "otherwise", notTaken.Length);
        // Cancel in reverse of the arm's baked order - descendants before ancestors. The builder bakes
        // each arm ancestors-first (topological order), so iterating it in reverse walks descendants
        // first. Cancelling an ancestor first would release an in-arm node that waits on it with a
        // release-on-any-terminal edge into a claimable scheduled state mid-loop, where a worker could
        // grab it before its own cancel arrives; descendants-first closes that window - every descendant
        // is still awaiting its parent when its cancel lands, and an ancestor's later cancel cascade skips
        // the already-terminal children. A per-member sequential cancel (rather than one workflow-scoped
        // batch op) is correct and sufficient here: it is ordered (descendants-first), replay-idempotent
        // (re-cancelling an already-terminal job is a no-op), and every arm id is a workflow member baked
        // in at build, so no per-id membership re-check is needed.
        for (var i = notTaken.Length - 1; i >= 0; i--)
        {
            await @operator.CancelJobAsync(notTaken[i], Actor, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A <b>seed-aware conditional gate</b>: the predicate that decides which arm of a runtime branch runs,
/// reading both the observed ancestor's output and the workflow's immutable Workflow Input seed. Implement
/// it on a small parameterless type and hand that type to the workflow builder's conditional branch; the
/// implementation runs inside a job handler at run time, reads already-decided state - the ancestor step's
/// output and the seed baked in when the workflow started - and returns whether the primary ("then") arm
/// should run. The predicate is read-only: it receives only the observed output and the seed, never the job
/// context, so it cannot enqueue, cancel, or emit output. Because a .NET delegate cannot be serialized and
/// shipped to the worker that later runs the gate, the decision is carried as a type - registered like any
/// other job - rather than as an inline lambda.
/// </summary>
/// <typeparam name="TStep">The ancestor step whose output the predicate reads; it declares the output type it produces.</typeparam>
/// <typeparam name="TOut">The output value type <typeparamref name="TStep"/> declares it produces.</typeparam>
/// <typeparam name="TInput">The Workflow Input seed type the predicate reads; the same type the workflow was started with.</typeparam>
/// <example>
/// <code>
/// public sealed record CheckoutSeed(int FreeShipCents) : IWorkflowInput;
///
/// public sealed class OverThreshold : IWorkflowGate&lt;PriceOrder, OrderTotal, CheckoutSeed&gt;
/// {
///     public bool Enter(DependencyOutput&lt;OrderTotal&gt; observed, CheckoutSeed input)
///         =&gt; observed.HasOutput &amp;&amp; observed.Output!.Cents &gt; input.FreeShipCents;
/// }
/// </code>
/// </example>
public interface IWorkflowGate<TStep, TOut, TInput>
    where TStep : IWorkflowStep<TOut>
    where TInput : IWorkflowInput
{
    /// <summary>
    /// Decides, at run time, which arm of the branch runs, reading the observed ancestor's output and the
    /// workflow's immutable Workflow Input seed. Returns <see langword="true"/> to run the primary ("then")
    /// arm and cancel the alternate arm, or <see langword="false"/> to run the alternate ("otherwise") arm
    /// and cancel the primary arm. Both reads are over already-decided state, so the predicate is a pure
    /// function of them; <b>absence is normal</b> for <paramref name="observed"/> - an ancestor that failed,
    /// was cancelled, or succeeded emitting nothing arrives with
    /// <see cref="DependencyOutput{T}.HasOutput"/> false, and the predicate must handle that case. The seed
    /// is always present, being workflow-wide constant data set when the workflow started.
    /// </summary>
    /// <param name="observed">The ancestor step's terminal state and its output, or a clean absence when it emitted none.</param>
    /// <param name="input">The immutable Workflow Input seed the workflow was started with.</param>
    /// <returns><see langword="true"/> to run the primary arm; <see langword="false"/> to run the alternate arm.</returns>
    bool Enter(DependencyOutput<TOut> observed, TInput input);
}

/// <summary>
/// The statically-wired <b>gate step</b> a seed-aware conditional branch lowers to: an ordinary workflow
/// step whose payload carries the job ids of each arm. Both arms are enqueued up front, so the graph shape
/// is fixed; at run time the gate's handler evaluates its <typeparamref name="TGate"/> predicate - over the
/// observed ancestor's output and the Workflow Input seed - and cancels the arm that was not taken. You
/// never construct this type - the workflow builder's conditional branch mints it and bakes in the arm ids -
/// but you must register it (and its handler) as a job so it can run, one registration per distinct gate the
/// workflow uses.
/// </summary>
/// <typeparam name="TGate">The predicate type that decides which arm runs.</typeparam>
/// <typeparam name="TStep">The ancestor step whose output the predicate reads.</typeparam>
/// <typeparam name="TOut">The output value type <typeparamref name="TStep"/> declares it produces.</typeparam>
/// <typeparam name="TInput">The Workflow Input seed type the predicate reads.</typeparam>
/// <param name="ThenArm">The job ids of the primary arm and all its descendants, cancelled when the predicate returns <see langword="false"/>.</param>
/// <param name="OtherwiseArm">The job ids of the alternate arm and all its descendants, cancelled when the predicate returns <see langword="true"/>.</param>
/// <example>
/// Declare the seed-aware gate serializable, then register it in one call after <c>AddBackWave</c>:
/// <code>
/// [JsonSerializable(typeof(WorkflowGate&lt;OverThreshold, PriceOrder, OrderTotal, CheckoutSeed&gt;))]
/// public partial class AppJsonContext : JsonSerializerContext;
///
/// services.AddWorkflowGate&lt;OverThreshold, PriceOrder, OrderTotal, CheckoutSeed&gt;(
///     "over-threshold-gate",
///     AppJsonContext.Default.WorkflowGateOverThresholdPriceOrderOrderTotalCheckoutSeed);
/// </code>
/// </example>
public sealed record WorkflowGate<TGate, TStep, TOut, TInput>(Guid[] ThenArm, Guid[] OtherwiseArm) : IWorkflowStep
    where TGate : IWorkflowGate<TStep, TOut, TInput>, new()
    where TStep : IWorkflowStep<TOut>
    where TInput : IWorkflowInput;

/// <summary>
/// The handler for a seed-aware conditional <see cref="WorkflowGate{TGate, TStep, TOut, TInput}"/>. It pulls
/// the observed ancestor's decided output and reads the workflow's immutable Workflow Input seed, evaluates
/// the <typeparamref name="TGate"/> predicate, and cancels the arm that was not taken through the
/// workflow-cancel operator - so the not-taken arm and its descendants reach a cancelled (terminal) state
/// rather than running, while the taken arm proceeds. Cancellation produces no failed member, so the
/// workflow's derived status stays coherent. Register it against
/// <see cref="WorkflowGate{TGate, TStep, TOut, TInput}"/> for the same four type arguments.
/// </summary>
/// <typeparam name="TGate">The predicate type that decides which arm runs.</typeparam>
/// <typeparam name="TStep">The ancestor step whose output the predicate reads.</typeparam>
/// <typeparam name="TOut">The output value type <typeparamref name="TStep"/> declares it produces.</typeparam>
/// <typeparam name="TInput">The Workflow Input seed type the predicate reads.</typeparam>
/// <param name="operator">The operator surface the not-taken arm is cancelled through.</param>
/// <param name="loggerFactory">
/// Optional. Supplies the logger the gate writes its decision (which arm ran) to at Information level.
/// Null (the default) disables the log with no allocation; the gate behaves identically either way.
/// </param>
public sealed class WorkflowGateHandler<TGate, TStep, TOut, TInput>(
    BackWaveOperator @operator, ILoggerFactory? loggerFactory = null)
    : IJobHandler<WorkflowGate<TGate, TStep, TOut, TInput>>
    where TGate : IWorkflowGate<TStep, TOut, TInput>, new()
    where TStep : IWorkflowStep<TOut>
    where TInput : IWorkflowInput
{
    // The identity recorded against every per-member cancel this gate drives, so the cancellation shows
    // up in the operator audit trail as a workflow decision rather than a human action.
    private const string Actor = "workflow-conditional-gate";

    private readonly ILogger _log = loggerFactory?.CreateLogger("BackWave.Pro.Workflows") ?? NullLogger.Instance;

    /// <summary>
    /// Runs the gate: reads the observed ancestor's output and the Workflow Input seed, evaluates the
    /// predicate, and cancels the arm that was not taken (its whole subtree, by baked-in id).
    /// </summary>
    /// <param name="job">The gate step, carrying the two arms' baked-in job ids.</param>
    /// <param name="context">The handler's execution context, used to pull the observed ancestor's output and read the seed.</param>
    /// <param name="cancellationToken">Signaled to abandon the work.</param>
    /// <returns>A task that completes once the not-taken arm has been cancelled.</returns>
    public async Task HandleAsync(
        WorkflowGate<TGate, TStep, TOut, TInput> job, JobContext context, CancellationToken cancellationToken)
    {
        var observed = await context.Output<TStep, TOut>(cancellationToken).ConfigureAwait(false);
        var input = context.Input<TInput>();
        var enterThen = new TGate().Enter(observed, input);
        var notTaken = enterThen ? job.OtherwiseArm : job.ThenArm;
        WorkflowLog.GateDecided(_log, typeof(TGate).Name, enterThen ? "then" : "otherwise", notTaken.Length);
        // Cancel in reverse of the arm's baked order - descendants before ancestors. The builder bakes
        // each arm ancestors-first (topological order), so iterating it in reverse walks descendants
        // first. Cancelling an ancestor first would release an in-arm node that waits on it with a
        // release-on-any-terminal edge into a claimable scheduled state mid-loop, where a worker could
        // grab it before its own cancel arrives; descendants-first closes that window - every descendant
        // is still awaiting its parent when its cancel lands, and an ancestor's later cancel cascade skips
        // the already-terminal children. A per-member sequential cancel (rather than one workflow-scoped
        // batch op) is correct and sufficient here: it is ordered (descendants-first), replay-idempotent
        // (re-cancelling an already-terminal job is a no-op), and every arm id is a workflow member baked
        // in at build, so no per-id membership re-check is needed.
        for (var i = notTaken.Length - 1; i >= 0; i--)
        {
            await @operator.CancelJobAsync(notTaken[i], Actor, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
