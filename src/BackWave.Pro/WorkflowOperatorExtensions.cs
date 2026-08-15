using BackWave.Operations;
using BackWave.Storage;

namespace BackWave.Pro;

/// <summary>
/// The outcome of cancelling a whole workflow. Reports whether the workflow existed and, of its
/// members that were still running when cancellation fanned out, how many were cancelled immediately
/// versus asked to stop cooperatively. Members that had already finished are not counted.
/// </summary>
/// <param name="Found">True when a workflow with the given id existed; false means nothing was cancelled.</param>
/// <param name="CancelledImmediately">How many still-pending members were cancelled outright.</param>
/// <param name="CancellationRequested">How many running (leased) members were asked to stop cooperatively and will cancel when their handler next checks for it.</param>
public sealed record WorkflowCancelResult(bool Found, int CancelledImmediately, int CancellationRequested)
{
    /// <summary>No workflow with the given id existed — nothing was cancelled.</summary>
    public static readonly WorkflowCancelResult NotFound = new(Found: false, 0, 0);
}

/// <summary>
/// The BackWave Pro workflow-cancel operator action, attached to the base operator. With the
/// BackWave Pro package referenced, this lights up on the same operator used for single-job actions.
/// Cancelling workflows is a Pro feature: referencing the package is the entire boundary, and the
/// license state never changes whether it runs.
/// </summary>
public static class WorkflowOperatorExtensions
{
    /// <summary>
    /// Cancels a whole workflow by cancelling each of its members that is still running at the moment
    /// of the call — a one-time snapshot, not a standing rule, so a member that starts after this
    /// returns is unaffected. Members that have already finished are left untouched. Each per-member
    /// cancel is recorded against the acting operator. Because an operator cancel produces no failed
    /// members, the workflow's derived status reads as cancelled rather than failed. A running member
    /// cancels cooperatively, exactly as a single-job cancel does.
    /// </summary>
    /// <param name="operator">The operator surface the cancellation is applied through.</param>
    /// <param name="workflowId">The id of the workflow to cancel.</param>
    /// <param name="actor">The acting operator's identity, recorded in the audit log for each member cancelled.</param>
    /// <param name="now">The instant to stamp the actions with. Defaults to the current instant.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the workflow existed and how many members were cancelled immediately versus asked to stop cooperatively.</returns>
    public static async ValueTask<WorkflowCancelResult> CancelWorkflowAsync(
        this BackWaveOperator @operator,
        Guid workflowId,
        string actor,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var instant = now ?? @operator.Clock.GetUtcNow();
        var graph = await @operator.Store.GetWorkflowAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (graph is null)
        {
            return WorkflowCancelResult.NotFound;
        }

        // Snapshot fan-out: cancel each member non-terminal in the graph read above. A member that
        // terminates between the read and its cancel simply comes back NotCancellable and is counted
        // as neither — no latch, no re-read.
        var cancelledImmediately = 0;
        var cancellationRequested = 0;
        foreach (var member in graph.Members)
        {
            if (member.State.IsTerminal())
            {
                continue;
            }
            var result = await @operator.Store.CancelJobAsync(member.JobId, actor, instant, cancellationToken).ConfigureAwait(false);
            if (result == CancelResult.CancelledImmediately)
            {
                cancelledImmediately++;
            }
            else if (result == CancelResult.CancellationRequested)
            {
                cancellationRequested++;
            }
        }

        return new WorkflowCancelResult(Found: true, cancelledImmediately, cancellationRequested);
    }
}
