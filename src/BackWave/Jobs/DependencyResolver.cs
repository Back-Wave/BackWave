using BackWave.Storage;

namespace BackWave.Jobs;

// Read side of River's LoadDeps (ADR 0026, issue 0132). Lives above the determinism boundary: a pure
// graph walk over the stored Workflow definition plus the dedicated output read, never touching the
// Core, a Command, or an oracle. The accessor on JobContext delegates here so resolution stays testable
// and a handler need know nothing about edges.
/// <summary>
/// Resolves a dependent's <c>name-or-JobId</c> handle to a transitive Dependency ancestor and
/// reads that ancestor's Job Output.
/// </summary>
internal interface IDependencyResolver
{
    /// <summary>
    /// Resolves the target of <paramref name="readerJobId"/>'s pull and reads its output. The handle
    /// is a node name (resolved against the reader's transitive ancestor set, by the ancestor's stored
    /// Wire Name) for a Workflow member, or a <see cref="Guid"/>-shaped string for a raw Dependency
    /// (which is itself the scope handle — a caller can only name a JobId it was handed). Returns the
    /// ancestor's terminal state and its output bytes (null when it emitted none), or null when the
    /// handle resolves to <b>no ancestor</b> — the scope guarantee, a non-ancestor sibling is
    /// physically unresolvable. Decoding the bytes to <c>T</c> is the caller's (the codec's) job.
    /// </summary>
    ValueTask<ResolvedDependencyOutput?> ResolveAsync(
        Guid readerJobId, string nameOrJobId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A resolved ancestor's already-decided state and raw output bytes — the store-level result the
/// <see cref="JobContext"/> accessor decodes into a typed <see cref="DependencyOutput{T}"/>.
/// </summary>
/// <param name="AncestorState">The resolved ancestor's current (terminal) job state.</param>
/// <param name="Output">The ancestor's persisted output blob, or null when it emitted none.</param>
internal sealed record ResolvedDependencyOutput(JobState AncestorState, ReadOnlyMemory<byte>? Output);

// The immutable structural edges (recorded once at enqueue, total for the Workflow's whole life per
// ADR 0023) are why an ancestor of an already-released reader still resolves: the live gating edges
// (IJobStore.GetDependencyEdgesAsync) have already resolved away as the parents terminated.
/// <summary>
/// The default <see cref="IDependencyResolver"/> over the Storage Contract: it walks the immutable
/// structural Workflow edges (<see cref="WorkflowGraph.Edges"/>) <b>up</b> from the reader to build
/// the transitive ancestor set, then resolves a name against that set by Wire Name and reads the
/// chosen ancestor's output.
/// </summary>
internal sealed class StoreDependencyResolver(IJobStore store) : IDependencyResolver
{
    public async ValueTask<ResolvedDependencyOutput?> ResolveAsync(
        Guid readerJobId, string nameOrJobId, CancellationToken cancellationToken = default)
    {
        if (await store.GetJobAsync(readerJobId, cancellationToken).ConfigureAwait(false) is not { } reader)
        {
            return null;
        }

        // A Guid-shaped handle is a raw-Dependency JobId: read it directly (no names exist for raw
        // dependencies, and the JobId the caller holds is itself the scope handle).
        if (Guid.TryParse(nameOrJobId, out var jobId))
        {
            return await ReadAsync(jobId, cancellationToken).ConfigureAwait(false);
        }

        // A name resolves only against the reader's transitive ancestors inside its Workflow — a
        // non-member (or non-ancestor sibling) is unresolvable. Member names are not persisted, so
        // the stored member identity is the Wire Name; the ancestors-only walk is the scope.
        if (reader.WorkflowId is not { } workflowId
            || await store.GetWorkflowAsync(workflowId, cancellationToken).ConfigureAwait(false) is not { } graph)
        {
            return null;
        }

        var ancestors = TransitiveAncestors(readerJobId, graph.Edges);
        var matches = graph.Members
            .Where(m => ancestors.Contains(m.JobId) && m.WireName == nameOrJobId)
            .ToList();
        if (matches.Count > 1)
        {
            // A repeated step type shares ONE Wire Name across its members (member names are not
            // persisted), so a name that resolves to several ancestors cannot be read by type alone.
            // Fail loudly with guidance rather than letting SingleOrDefault throw a bare sequence error.
            throw new InvalidOperationException(
                $"The handle '{nameOrJobId}' matches {matches.Count} ancestors of this job, so reading " +
                "its Job Output is ambiguous: the step type it names appears more than once among this " +
                "job's ancestors. Ancestor output is keyed by step type and repeats share one key, so a " +
                "repeated step type cannot be read by type alone. Restructure the workflow so at most one " +
                "ancestor of the reader is this step type, so the read resolves to a single step.");
        }
        return matches.Count == 0 ? null : await ReadAsync(matches[0].JobId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one already-resolved ancestor's terminal state and output blob.</summary>
    private async ValueTask<ResolvedDependencyOutput?> ReadAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (await store.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false) is not { } ancestor)
        {
            return null;
        }
        var output = await store.GetJobOutputAsync(jobId, cancellationToken).ConfigureAwait(false);
        return new ResolvedDependencyOutput(ancestor.State, output);
    }

    /// <summary>
    /// The transitive ancestor set of <paramref name="readerJobId"/> — every node reachable by
    /// walking the structural Parent→Child edges <b>up</b> (from child to parent). Pure BFS over a
    /// small DAG; the reader itself is excluded.
    /// </summary>
    private static HashSet<Guid> TransitiveAncestors(Guid readerJobId, IReadOnlyList<WorkflowEdge> edges)
    {
        var parentsByChild = edges
            .GroupBy(e => e.Child)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Parent).ToList());

        var ancestors = new HashSet<Guid>();
        var frontier = new Queue<Guid>();
        frontier.Enqueue(readerJobId);
        while (frontier.TryDequeue(out var node))
        {
            if (!parentsByChild.TryGetValue(node, out var parents))
            {
                continue;
            }
            foreach (var parent in parents)
            {
                if (ancestors.Add(parent))
                {
                    frontier.Enqueue(parent);
                }
            }
        }
        return ancestors;
    }
}
