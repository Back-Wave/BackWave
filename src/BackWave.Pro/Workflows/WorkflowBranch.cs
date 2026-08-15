using BackWave.Jobs;
using BackWave.Storage;

namespace BackWave.Pro;

/// <summary>
/// One branch of a parallel fan-out on a <see cref="TypedWorkflowBuilder"/>. A branch is either a single
/// step (<see cref="Step{TStep}(TStep, string?, string?, JobTags?)"/>) or a multi-step sub-pipeline built by
/// a lambda (<see cref="Do(Action{TypedWorkflowBuilder})"/>); both forms may be mixed freely in one
/// <see cref="TypedWorkflowBuilder.Parallel(WorkflowBranch[])"/> call. A branch holds no store and does no
/// I/O; it is a recipe the builder runs, rooted at the frontier as it stood when <c>Parallel</c> was called.
/// </summary>
public sealed class WorkflowBranch
{
    private readonly Action<TypedWorkflowBuilder> _build;

    private WorkflowBranch(Action<TypedWorkflowBuilder> build) => _build = build;

    /// <summary>
    /// A branch that runs a single step. Sugar for <c>WorkflowBranch.Do(b =&gt; b.Then(step, name, queue, tags))</c>.
    /// </summary>
    /// <typeparam name="TStep">The step type; must be a registered <c>[Job]</c> payload wearing <see cref="IWorkflowStep"/>.</typeparam>
    /// <param name="step">The step payload instance to run in this branch.</param>
    /// <param name="name">An optional disambiguation name, required only when the same step type is used more than once in the workflow.</param>
    /// <param name="queue">The queue this step runs on. Defaults to the step type's registered queue.</param>
    /// <param name="tags">Extra tags to attach, unioned with the step type's default tags. Additive - defaults are never removed.</param>
    /// <returns>A branch that adds the single step when the fan-out runs.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="step"/> is <see langword="null"/>.</exception>
    public static WorkflowBranch Step<TStep>(
        TStep step, string? name = null, string? queue = null, JobTags? tags = null)
        where TStep : IWorkflowStep
    {
        ArgumentNullException.ThrowIfNull(step);
        return new WorkflowBranch(b => b.Then(step, name: name, queue: queue, tags: tags));
    }

    /// <summary>
    /// A branch built by a lambda that receives a sub-builder rooted at the fan-out frontier. Chain several
    /// <see cref="TypedWorkflowBuilder.Then{TStep}(TStep, DependencyMode, string?, string?, JobTags?)"/> calls (and nest
    /// further <see cref="TypedWorkflowBuilder.Parallel(WorkflowBranch[])"/> calls) to shape a multi-step
    /// sub-pipeline; the sub-pipeline's tip becomes this branch's tip.
    /// </summary>
    /// <param name="build">Builds the branch on the supplied sub-builder. Must add at least one step.</param>
    /// <returns>A branch that runs <paramref name="build"/> when the fan-out runs.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="build"/> is <see langword="null"/>.</exception>
    public static WorkflowBranch Do(Action<TypedWorkflowBuilder> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return new WorkflowBranch(build);
    }

    // Runs the branch recipe against the per-branch sub-builder minted by Parallel.
    internal void Apply(TypedWorkflowBuilder branchBuilder) => _build(branchBuilder);
}
