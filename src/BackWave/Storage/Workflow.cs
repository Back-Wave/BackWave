namespace BackWave.Storage;

/// <summary>
/// A workflow's derived status. It is always a <b>projection</b> of its member jobs' states, never
/// authoritative stored state, so it recomputes anytime and may legitimately reopen from Succeeded
/// back to Running when live work is appended. The precedence rules are in
/// <see cref="WorkflowStatusProjection"/>.
/// </summary>
public enum WorkflowStatus
{
    /// <summary>At least one member is still non-terminal.</summary>
    Running,

    /// <summary>All members terminal and at least one dead-lettered or quarantined — failure dominates.</summary>
    Failed,

    /// <summary>
    /// All members terminal, none failed, and at least one cancelled - for example by an operator, or by a
    /// Pro conditional workflow whose not-taken branch is cancelled by design (such a workflow derives
    /// Cancelled even when every step that ran succeeded).
    /// </summary>
    Cancelled,

    /// <summary>Every member succeeded.</summary>
    Succeeded,
}

/// <summary>How a workflow's member jobs are retained.</summary>
public enum WorkflowRetentionPolicy
{
    /// <summary>
    /// Members are retained <b>as a unit until the whole workflow drains</b> (every member terminal),
    /// and only then does the per-job retention window start, measured from that drain point. This
    /// keeps the workflow's graph coherent for its whole life. The default.
    /// </summary>
    UnitUntilDrained,
}

/// <summary>
/// The stored workflow row: identity and configuration only. A workflow's status is never stored
/// here — it is always derived from member states by <see cref="WorkflowStatusProjection"/>.
/// </summary>
public sealed record WorkflowRecord
{
    /// <summary>The workflow's stable, unique identifier.</summary>
    public required Guid WorkflowId { get; init; }

    /// <summary>An optional, non-unique human-readable label; null for an unnamed workflow.</summary>
    public string? Name { get; init; }

    /// <summary>When the workflow was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>How the workflow's members are retained. Defaults to retaining them as a unit until the workflow drains.</summary>
    public WorkflowRetentionPolicy Retention { get; init; } = WorkflowRetentionPolicy.UnitUntilDrained;

    /// <summary>
    /// The id of the workflow this one was restarted from, or null if it was created fresh. A restart
    /// re-runs a definition as a brand-new workflow with new job identities (a full redo, not a
    /// resume), so this is the only link back to the original.
    /// </summary>
    public Guid? RestartedFrom { get; init; }
}

/// <summary>One structural dependency edge inside a workflow: <paramref name="Child"/> depends on <paramref name="Parent"/>.</summary>
/// <remarks>
/// Unlike live gating edges, which resolve away as parents terminate, a workflow's structural edges
/// are immutable and recorded at enqueue, so the graph view stays complete for the workflow's whole
/// life.
/// </remarks>
/// <param name="Parent">The depended-upon job.</param>
/// <param name="Child">The job that depends on the parent.</param>
public sealed record WorkflowEdge(Guid Parent, Guid Child);

/// <summary>
/// A prepared workflow graph ready for atomic enqueue: the members (job rows, each with its parents
/// already resolved to real ids) plus the workflow identity. The workflow builder emits this after
/// validating acyclicity, dependency-name resolution, and that every dependency stays within the
/// workflow.
/// </summary>
public sealed record WorkflowDefinition
{
    /// <summary>The workflow's identifier. Fresh on a new workflow; an existing one when appending.</summary>
    public required Guid WorkflowId { get; init; }

    /// <summary>An optional human-readable label for the workflow; null for an unnamed workflow.</summary>
    public string? Name { get; init; }

    /// <summary>The member jobs, each carrying its already-resolved parent ids.</summary>
    public required IReadOnlyList<NewJob> Members { get; init; }

    /// <summary>How the workflow's members are retained. Defaults to retaining them as a unit until the workflow drains.</summary>
    public WorkflowRetentionPolicy Retention { get; init; } = WorkflowRetentionPolicy.UnitUntilDrained;

    /// <summary>
    /// Set when this definition is a restart, to the original workflow's id; null for a fresh creation.
    /// It is recorded on the new workflow row as a lineage pointer, and is ignored on an append.
    /// </summary>
    public Guid? RestartedFrom { get; init; }

    /// <summary>
    /// Re-instantiates this whole graph as a <b>brand-new workflow</b>: a fresh workflow id and fresh
    /// member job ids, with identical shape, and with <see cref="RestartedFrom"/> set to this
    /// workflow. This is a restart, not a resume or retry: the new workflow re-runs every step from
    /// the start — including already-succeeded ones — so non-idempotent steps re-execute. It only
    /// creates new jobs; no terminal state is ever reanimated. It expects a complete (non-append)
    /// definition; an edge to a non-member is carried through verbatim and rejected by the store's
    /// containment check, exactly as on any enqueue.
    /// </summary>
    /// <returns>A new definition for an independent workflow that re-runs this one's graph from scratch.</returns>
    public WorkflowDefinition RestartAsNew()
    {
        var remap = Members.ToDictionary(m => m.JobId, _ => Guid.NewGuid());
        var members = Members
            .Select(m => m with
            {
                JobId = remap[m.JobId],
                Parents = [.. m.Parents.Select(p => remap.GetValueOrDefault(p, p))],
            })
            .ToList();
        return new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = Name,
            Members = members,
            Retention = Retention,
            RestartedFrom = WorkflowId,
            // IsAppend stays false: a Restart always creates a new Workflow, never appends.
        };
    }

    /// <summary>
    /// True when this enqueue <b>appends</b> new members into an existing workflow rather than creating
    /// one: the <see cref="WorkflowId"/> must already exist, the workflow row is left untouched, and a
    /// new member may name an existing member as a parent (the within-workflow containment rule still
    /// applies). An existing member's dependencies are never rewritten, so this is append-only
    /// expansion, never a result-driven graph.
    /// </summary>
    public bool IsAppend { get; init; }
}

/// <summary>A workflow as a monitor listing shows it: identity, creation time, derived status, and size.</summary>
public sealed record WorkflowSnapshot
{
    /// <summary>The workflow's identifier.</summary>
    public required Guid WorkflowId { get; init; }

    /// <summary>The workflow's human-readable label, or null if unnamed.</summary>
    public string? Name { get; init; }

    /// <summary>When the workflow was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The workflow's status, derived from its members' current states.</summary>
    public required WorkflowStatus Status { get; init; }

    /// <summary>How many member jobs the workflow has.</summary>
    public required int MemberCount { get; init; }

    /// <summary>The workflow this one was restarted from, or null if created fresh.</summary>
    public Guid? RestartedFrom { get; init; }
}

/// <summary>
/// One workflow's full graph: its members (each with its current job state), the immutable structural
/// edges between them, and the derived status.
/// </summary>
public sealed record WorkflowGraph
{
    /// <summary>The workflow's identifier.</summary>
    public required Guid WorkflowId { get; init; }

    /// <summary>The workflow's human-readable label, or null if unnamed.</summary>
    public string? Name { get; init; }

    /// <summary>When the workflow was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The workflow's status, derived from its members' current states.</summary>
    public required WorkflowStatus Status { get; init; }

    /// <summary>The member jobs, each as a current snapshot.</summary>
    public required IReadOnlyList<JobRecord> Members { get; init; }

    /// <summary>The immutable structural dependency edges between members; complete for the workflow's whole life.</summary>
    public required IReadOnlyList<WorkflowEdge> Edges { get; init; }

    /// <summary>The workflow this one was restarted from, or null if created fresh.</summary>
    public Guid? RestartedFrom { get; init; }
}

/// <summary>The outcome of an atomic workflow enqueue. Anything other than <see cref="Ok"/> inserts nothing.</summary>
public enum WorkflowEnqueueResult
{
    /// <summary>The workflow was enqueued successfully.</summary>
    Ok,

    /// <summary>A workflow with this id already exists. (This is the creation path; appending is a separate operation.)</summary>
    DuplicateWorkflow,

    /// <summary>An append targeted a workflow id that does not exist.</summary>
    WorkflowNotFound,

    /// <summary>A member's job id already exists — either in the store, or twice within this batch.</summary>
    DuplicateMember,

    /// <summary>A member's gating parent is not a member of this same workflow; the store enforces containment.</summary>
    ContainmentViolation,

    /// <summary>The definition has no members.</summary>
    EmptyWorkflow,

    /// <summary>A member's payload exceeds the store's maximum payload size.</summary>
    PayloadTooLarge,

    /// <summary>A member's wire name exceeds the store's maximum wire-name length.</summary>
    WireNameTooLong,

    /// <summary>A member declares more parents than the store's per-job parent limit.</summary>
    TooManyParents,
}

/// <summary>
/// The workflow status <b>projection</b>: a pure function of the multiset of member-job states. The
/// precedence is first-match-wins — <b>Running, then Failed, then Cancelled, then Succeeded</b> — so
/// failure dominates (a dead-lettered or quarantined member makes the whole workflow Failed even with
/// a succeeded sibling), while an operator cancel with no failed members reads as Cancelled. There is
/// no partial state; the graph view shows which branch died.
/// </summary>
public static class WorkflowStatusProjection
{
    /// <summary>
    /// Projects a workflow's derived status from its members' current states, applying the precedence
    /// Running &gt; Failed &gt; Cancelled &gt; Succeeded (first match wins).
    /// </summary>
    /// <param name="memberStates">The current states of the workflow's member jobs.</param>
    /// <returns>
    /// The derived status: Running if any member is non-terminal; otherwise Failed if any member
    /// dead-lettered or quarantined; otherwise Cancelled if any member cancelled; otherwise Succeeded
    /// (including the vacuous case of no members).
    /// </returns>
    public static WorkflowStatus Project(IEnumerable<JobState> memberStates)
    {
        var anyNonTerminal = false;
        var anyFailed = false;
        var anyCancelled = false;

        foreach (var state in memberStates)
        {
            if (!state.IsTerminal())
            {
                anyNonTerminal = true;
            }
            else if (state is JobState.DeadLettered or JobState.Quarantined)
            {
                anyFailed = true;
            }
            else if (state is JobState.Cancelled)
            {
                anyCancelled = true;
            }
        }

        // First match wins: Running dominates everything, then failure dominates a mixed terminal set,
        // then a cancel, else all members Succeeded (also the vacuous empty case).
        return anyNonTerminal ? WorkflowStatus.Running
            : anyFailed ? WorkflowStatus.Failed
            : anyCancelled ? WorkflowStatus.Cancelled
            : WorkflowStatus.Succeeded;
    }
}
