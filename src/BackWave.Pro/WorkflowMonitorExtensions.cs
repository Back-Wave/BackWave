using BackWave.Monitor;
using BackWave.Storage;

namespace BackWave.Pro;

/// <summary>
/// A read-only view of one workflow's full graph: its member jobs (as <see cref="JobSnapshot"/>s,
/// never payload bytes), the fixed structural <see cref="WorkflowEdge"/>s between them, and the
/// derived <see cref="WorkflowStatus"/>. Use it to render a workflow's graph and drill from a member
/// node into that job's detail.
/// </summary>
public sealed record WorkflowView
{
    /// <summary>The workflow's unique id.</summary>
    public required Guid WorkflowId { get; init; }

    /// <summary>An optional human-readable name for the workflow; null when none was given.</summary>
    public string? Name { get; init; }

    /// <summary>When the workflow was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The workflow's overall status, computed from its member jobs' states.</summary>
    public required WorkflowStatus Status { get; init; }

    /// <summary>The workflow's member jobs.</summary>
    public required IReadOnlyList<JobSnapshot> Members { get; init; }

    /// <summary>The fixed dependency edges between members, defining the run order within the workflow.</summary>
    public required IReadOnlyList<WorkflowEdge> Edges { get; init; }

    /// <summary>The workflow this one was restarted from, or null if it was created fresh.</summary>
    public Guid? RestartedFrom { get; init; }
}

/// <summary>
/// The BackWave Pro workflow read surface, attached to the base monitor. With the BackWave Pro package
/// referenced, these methods light up on the same monitor used for ordinary reads — list workflows and
/// read one workflow's full graph. Workflows are a Pro feature: referencing the package is the entire
/// boundary, and the license state never changes whether these run.
/// </summary>
public static class WorkflowMonitorExtensions
{
    /// <summary>
    /// Every workflow ordered by creation time, oldest first, each with its member count and derived
    /// status. The status is always computed from the member jobs' states, never stored separately.
    /// </summary>
    /// <param name="monitor">The monitor to read through.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The workflows oldest first; empty when none exist.</returns>
    public static ValueTask<IReadOnlyList<WorkflowSnapshot>> ListWorkflowsAsync(
        this BackWaveMonitor monitor, CancellationToken cancellationToken = default)
        => monitor.Store.ListWorkflowsAsync(cancellationToken);

    /// <summary>
    /// One workflow's full graph: its member jobs (as <see cref="JobSnapshot"/>s), the fixed structural
    /// edges between them, and the derived status. Use it to render a workflow's dependency graph.
    /// </summary>
    /// <param name="monitor">The monitor to read through.</param>
    /// <param name="workflowId">The id of the workflow to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The workflow graph, or <c>null</c> when no workflow with that id exists.</returns>
    public static async ValueTask<WorkflowView?> GetWorkflowAsync(
        this BackWaveMonitor monitor, Guid workflowId, CancellationToken cancellationToken = default)
        => await monitor.Store.GetWorkflowAsync(workflowId, cancellationToken).ConfigureAwait(false) is { } graph
            ? new WorkflowView
            {
                WorkflowId = graph.WorkflowId,
                Name = graph.Name,
                CreatedAt = graph.CreatedAt,
                Status = graph.Status,
                Members = [.. graph.Members.Select(BackWaveMonitor.ToSnapshot)],
                Edges = graph.Edges,
                RestartedFrom = graph.RestartedFrom,
            }
            : null;
}
