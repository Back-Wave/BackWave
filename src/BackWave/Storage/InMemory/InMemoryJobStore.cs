using System.Data.Common;

namespace BackWave.Storage.InMemory;

/// <summary>
/// The in-memory reference implementation of the job store. It is deterministic and clock-free —
/// every time-dependent decision uses the instants callers pass in — so it can run on virtual time.
/// It keeps all state in process memory and persists nothing, so it cannot carry BackWave's
/// execution guarantee: a restart loses in-flight work, and it is single-process. Its home is
/// tests and local development. For the same zero-ops shape with durability, use the SQLite adapter.
/// </summary>
/// <param name="bounds">The size and batch limits to enforce; the default bounds when null.</param>
/// <param name="historyPolicy">How much of each job's transition log to record; the full log by default.</param>
public sealed class InMemoryJobStore(
    StoreBounds? bounds = null, JobHistoryPolicy historyPolicy = JobHistoryPolicy.TransitionsAndFailureDetail)
    : IJobStore
{
    private readonly StoreBounds _bounds = bounds ?? StoreBounds.Default;

    // The EFFECTIVE Job History Policy (§5.12, ADR 0011): the configured rung, with the top one
    // downgraded by the Failure Detail env kill-switch. Resolved once at construction — env is an
    // input to the run, so reading it here keeps recording deterministic for a given environment.
    private readonly JobHistoryPolicy _historyPolicy = JobHistoryPolicyResolver.Resolve(historyPolicy);
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _gate = new();
#else
    // System.Threading.Lock is net9+; on net8 a plain object gives the same lock-statement semantics.
    private readonly object _gate = new();
#endif
    private readonly Dictionary<Guid, JobRecord> _jobs = [];
    private readonly Dictionary<string, ScheduleRecord> _schedules = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, List<Guid>> _childrenByParent = [];

    // Workflows (ADR 0023): the Workflows rows (identity + config) and each Workflow's IMMUTABLE
    // structural edges. Both live above the determinism boundary — the Core never reads them. The
    // structural edges are kept separately from _childrenByParent (which resolves away as parents
    // terminate) so the graph view stays total for the Workflow's whole life. Members are found by
    // scanning _jobs for the WorkflowId scalar, so there is no separate membership index to keep.
    private readonly Dictionary<Guid, WorkflowRecord> _workflows = [];
    private readonly Dictionary<Guid, List<WorkflowEdge>> _workflowEdges = [];
    private readonly Dictionary<string, int?> _queueLimits = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pausedQueues = new(StringComparer.Ordinal);
    private readonly List<OperatorAuditRecord> _audit = [];

    // The Transition Log (§5.12): per-job append-only history, bounded by MaxTransitionsPerJob.
    // Off the hot path (its own map, never the job row); deleted with the job under §5.11.
    // _nextOrdinal tracks the next ordinal even after oldest entries age out beyond the cap.
    private readonly Dictionary<Guid, List<JobTransition>> _transitions = [];
    private readonly Dictionary<Guid, long> _nextOrdinal = [];
    private long _nextSequence;

    // The Observer-delivery capability (§5.13, ADR 0017). The Transition Log seen as a single
    // append-ordered global stream the Observer cursors walk: every recorded transition gets a
    // global Position here (alongside its per-job ordinal) carrying the facts an ObserverContext
    // needs. Appended only when the history policy records the transition at all — so an Off policy
    // leaves nothing to observe, history-gating the feature for free. Never truncated by the
    // per-job cap (it is a delivery queue, not the per-job timeline); _observers holds each
    // Observer's durable cursor, claim Lease, in-flight delivery progress, and dead-letter log.
    private readonly List<ObserverLogEntry> _observerLog = [];
    private readonly Dictionary<string, ObserverState> _observers = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public bool SupportsTransactionalEnqueue => true;

    /// <inheritdoc/>
    public JobHistoryPolicy HistoryPolicy => _historyPolicy;

    /// <inheritdoc/>
    public StoreBounds Bounds => _bounds;

    /// <summary>
    /// Starts a transactional-enqueue scope. Enqueues passing the returned transaction buffer until it
    /// commits; rolling back (or disposing without committing) means the jobs never existed — they are
    /// never claimable, never visible to reads, and leave no trace.
    /// </summary>
    /// <returns>A new transaction scope to pass into enqueue calls and then commit or roll back.</returns>
    public InMemoryTransaction BeginTransaction() => new(this);

    /// <inheritdoc/>
    public ValueTask<EnqueueResult> EnqueueAsync(
        NewJob job, DateTimeOffset now, DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        // The parent set is a set (§5.1): duplicate ids collapse before any rule applies.
        if (job.Parents.Count > 1)
        {
            job = job with { Parents = job.Parents.Distinct().ToArray() };
        }

        if (job.Payload.Length > _bounds.MaxPayloadBytes)
        {
            return ValueTask.FromResult(EnqueueResult.PayloadTooLarge);
        }

        if (job.WireName.Length > _bounds.MaxWireNameLength)
        {
            return ValueTask.FromResult(EnqueueResult.WireNameTooLong);
        }

        if (job.Parents.Count > _bounds.MaxParentsPerJob)
        {
            return ValueTask.FromResult(EnqueueResult.TooManyParents);
        }

        if (transaction is not null)
        {
            if (transaction is not InMemoryTransaction memoryTransaction || memoryTransaction.Store != this)
            {
                throw new ArgumentException(
                    "The In-Memory Store only enlists in transactions created by its own BeginTransaction().",
                    nameof(transaction));
            }

            lock (_gate)
            {
                // Validate now (clear error at the call site), apply at commit. Visible to
                // nothing — not Claim, not Monitor reads — until the caller commits.
                var result = ValidateLocked(job, id => _jobs.ContainsKey(id) || memoryTransaction.HasPending(id));
                if (result == EnqueueResult.Ok)
                {
                    memoryTransaction.AddPending(job, now);
                }
                return ValueTask.FromResult(result);
            }
        }

        lock (_gate)
        {
            return ValueTask.FromResult(EnqueueLocked(job, now));
        }
    }

    /// <summary>Applies a transaction's buffered enqueues atomically with respect to readers.</summary>
    internal void CommitTransaction(
        IReadOnlyList<(NewJob Job, DateTimeOffset Now)> pending,
        IReadOnlyList<(WorkflowDefinition Def, DateTimeOffset Now)> pendingWorkflows)
    {
        lock (_gate)
        {
            // All-or-nothing: re-run the full §5.1 / ADR 0023 admission rules for every buffered job
            // and Workflow before mutating anything, like constraint checks failing a real database
            // commit. JobId collisions are detected across BOTH buffers.
            var pendingMemberIds = pendingWorkflows
                .SelectMany(w => w.Def.Members.Select(m => m.JobId))
                .ToHashSet();

            // A pending job may satisfy another pending job's parent reference; a buffered Workflow
            // member (applied first below) is also a valid parent for a plain pending job.
            for (var i = 0; i < pending.Count; i++)
            {
                var precedingPending = pending.Take(i);
                var result = ValidateLocked(
                    pending[i].Job,
                    id => _jobs.ContainsKey(id) || precedingPending.Any(p => p.Job.JobId == id) || pendingMemberIds.Contains(id));
                if (result != EnqueueResult.Ok)
                {
                    throw new InvalidOperationException(
                        $"Transactional Enqueue commit failed for job {pending[i].Job.JobId}: {result}.");
                }
            }

            var plainJobIds = pending.Select(p => p.Job.JobId).ToHashSet();
            for (var i = 0; i < pendingWorkflows.Count; i++)
            {
                var precedingMemberIds = pendingWorkflows.Take(i)
                    .SelectMany(w => w.Def.Members.Select(m => m.JobId))
                    .ToHashSet();
                var def = pendingWorkflows[i].Def;
                // Existing members of an append target: committed members plus any added by a
                // preceding pending workflow with the same id (create-then-append within one txn).
                var existingMembers = def.IsAppend
                    ? (HashSet<Guid>)
                        [.. MembersOfLocked(def.WorkflowId),
                         .. pendingWorkflows.Take(i).Where(w => w.Def.WorkflowId == def.WorkflowId)
                            .SelectMany(w => w.Def.Members.Select(m => m.JobId))]
                    : EmptyGuidSet;
                var result = ValidateWorkflowLocked(
                    def,
                    jobExists: id => _jobs.ContainsKey(id) || plainJobIds.Contains(id) || precedingMemberIds.Contains(id),
                    workflowExists: id => _workflows.ContainsKey(id)
                        || pendingWorkflows.Take(i).Any(w => w.Def.WorkflowId == id),
                    existingMembers: existingMembers);
                if (result != WorkflowEnqueueResult.Ok)
                {
                    throw new InvalidOperationException(
                        $"Transactional Workflow enqueue commit failed for {pendingWorkflows[i].Def.WorkflowId}: {result}.");
                }
            }

            // Apply Workflows first so a plain pending job may legitimately depend on a member.
            foreach (var (def, now) in pendingWorkflows)
            {
                ApplyWorkflowLocked(def, now);
            }

            foreach (var (job, now) in pending)
            {
                var applied = EnqueueLocked(job, now);
                if (applied != EnqueueResult.Ok) // always-on assertion: validated above
                {
                    throw new InvalidOperationException(
                        $"Transactional Enqueue commit failed for job {job.JobId}: {applied}.");
                }
            }
        }
    }

    /// <summary>
    /// The job-admission rules, shared by the direct, buffered, and commit paths.
    /// <paramref name="exists"/> defines which jobs are visible to the checks.
    /// </summary>
    private static EnqueueResult ValidateLocked(NewJob job, Func<Guid, bool> exists)
    {
        if (exists(job.JobId))
        {
            return EnqueueResult.Duplicate;
        }
        if (job.Parents.Any(p => !exists(p)))
        {
            return EnqueueResult.UnknownParent;
        }
        return EnqueueResult.Ok;
    }

    /// <summary>
    /// The single insert path; the caller must hold the gate. <paramref name="workflowId"/>
    /// stamps the immutable workflow-membership scalar when inserting a workflow member;
    /// null for an ordinary job.
    /// </summary>
    private EnqueueResult EnqueueLocked(NewJob job, DateTimeOffset now, Guid? workflowId = null)
    {
        var validation = ValidateLocked(job, _jobs.ContainsKey);
        if (validation != EnqueueResult.Ok)
        {
            return validation;
        }

        // Resolve parents already terminal at enqueue: each is an edge that will never
        // fire later, so it must count against the latch (or cancel) right now.
        var pendingParents = new List<Guid>();
        var cancelledByParent = (JobState?)null;
        foreach (var parentId in job.Parents)
        {
            var parentState = _jobs[parentId].State;
            if (!parentState.IsTerminal())
            {
                pendingParents.Add(parentId);
            }
            else if (job.Mode == DependencyMode.OnSuccess && parentState != JobState.Succeeded)
            {
                cancelledByParent = parentState;
            }
        }

        var record = new JobRecord
        {
            JobId = job.JobId,
            WireName = job.WireName,
            Payload = job.Payload,
            TraceContext = job.TraceContext,
            Queue = job.Queue,
            State = cancelledByParent is not null ? JobState.Cancelled
                : pendingParents.Count > 0 ? JobState.AwaitingParent
                : JobState.Scheduled,
            DueTime = job.DueTime,
            ParentsRemaining = pendingParents.Count,
            Mode = job.Mode,
            TerminalAt = cancelledByParent is not null ? now : null,
            TerminalCause = cancelledByParent is not null ? ParentFailureCause(cancelledByParent.Value) : null,
            Sequence = _nextSequence++,
            // Tags are a set already (JobTags collapses duplicates); stored verbatim, never parsed (ADR 0022).
            Tags = job.Tags,
            // Workflow membership (ADR 0023): the immutable scalar, set once here at enqueue.
            WorkflowId = workflowId,
        };
        _jobs.Add(job.JobId, record);
        // Transition Log (§5.12): record the actual resulting state — Scheduled, AwaitingParent,
        // or Cancelled when an already-terminal parent resolved against an on-success child.
        RecordTransition(record.JobId, record.State, record.Attempt, now);
        foreach (var parentId in pendingParents)
        {
            if (!_childrenByParent.TryGetValue(parentId, out var children))
            {
                _childrenByParent[parentId] = children = [];
            }
            children.Add(job.JobId);
        }
        return EnqueueResult.Ok;
    }

    private static string ParentFailureCause(JobState parentState) => $"parent-failure:{parentState}";

    // ── Workflows (ADR 0023) ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public ValueTask<WorkflowEnqueueResult> EnqueueWorkflowAsync(
        WorkflowDefinition workflow, DateTimeOffset now, DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        if (transaction is not null)
        {
            if (transaction is not InMemoryTransaction memoryTransaction || memoryTransaction.Store != this)
            {
                throw new ArgumentException(
                    "The In-Memory Store only enlists in transactions created by its own BeginTransaction().",
                    nameof(transaction));
            }

            lock (_gate)
            {
                // Validate now (clear error at the call site), apply at commit — atomic with the
                // caller's own writes (the co-resident whole-Workflow guarantee). A buffered member
                // or Workflow is visible to nothing until the caller commits.
                var result = ValidateWorkflowLocked(
                    workflow,
                    jobExists: id => _jobs.ContainsKey(id) || memoryTransaction.HasPending(id),
                    workflowExists: id => _workflows.ContainsKey(id) || memoryTransaction.HasPendingWorkflow(id),
                    existingMembers: workflow.IsAppend ? MembersOfLocked(workflow.WorkflowId) : EmptyGuidSet);
                if (result == WorkflowEnqueueResult.Ok)
                {
                    memoryTransaction.AddPendingWorkflow(workflow, now);
                }
                return ValueTask.FromResult(result);
            }
        }

        lock (_gate)
        {
            var result = ValidateWorkflowLocked(
                workflow, jobExists: _jobs.ContainsKey, workflowExists: _workflows.ContainsKey,
                existingMembers: workflow.IsAppend ? MembersOfLocked(workflow.WorkflowId) : EmptyGuidSet);
            if (result == WorkflowEnqueueResult.Ok)
            {
                ApplyWorkflowLocked(workflow, now);
            }
            return ValueTask.FromResult(result);
        }
    }

    private static readonly HashSet<Guid> EmptyGuidSet = [];

    /// <summary>The id set of the jobs currently carrying <paramref name="workflowId"/> (caller holds the gate).</summary>
    private HashSet<Guid> MembersOfLocked(Guid workflowId)
        => [.. _jobs.Values.Where(j => j.WorkflowId == workflowId).Select(j => j.JobId)];

    /// <summary>
    /// The admission rules for a whole workflow, shared by the direct and buffered
    /// paths. All-or-nothing: every member and the Workflows row are validated before anything is
    /// inserted, so a single bad member leaves the store untouched. <paramref name="jobExists"/> and
    /// <paramref name="workflowExists"/> define what is already visible (committed, or pending in a
    /// transaction). The caller must hold the gate.
    /// </summary>
    private WorkflowEnqueueResult ValidateWorkflowLocked(
        WorkflowDefinition workflow, Func<Guid, bool> jobExists, Func<Guid, bool> workflowExists,
        IReadOnlySet<Guid> existingMembers)
    {
        if (workflow.Members.Count == 0)
        {
            return WorkflowEnqueueResult.EmptyWorkflow;
        }
        if (workflow.IsAppend)
        {
            if (!workflowExists(workflow.WorkflowId))
            {
                return WorkflowEnqueueResult.WorkflowNotFound; // nothing to append to
            }
        }
        else if (workflowExists(workflow.WorkflowId))
        {
            return WorkflowEnqueueResult.DuplicateWorkflow;
        }

        // Containment (ADR 0023): a member's gating parent must be a member of THIS Workflow. On the
        // creation path that means another member of this same batch; on the append path it may also
        // be an already-existing member. Build the allowed-parent id set (new members ∪ existing).
        var memberIds = new HashSet<Guid>();
        foreach (var member in workflow.Members)
        {
            if (!memberIds.Add(member.JobId))
            {
                return WorkflowEnqueueResult.DuplicateMember; // the same JobId twice in one batch
            }
        }
        var allowedParents = new HashSet<Guid>(memberIds);
        allowedParents.UnionWith(existingMembers);

        foreach (var member in workflow.Members)
        {
            if (jobExists(member.JobId))
            {
                return WorkflowEnqueueResult.DuplicateMember;
            }
            if (member.Payload.Length > _bounds.MaxPayloadBytes)
            {
                return WorkflowEnqueueResult.PayloadTooLarge;
            }
            if (member.WireName.Length > _bounds.MaxWireNameLength)
            {
                return WorkflowEnqueueResult.WireNameTooLong;
            }
            var parents = member.Parents.Distinct().ToArray();
            if (parents.Length > _bounds.MaxParentsPerJob)
            {
                return WorkflowEnqueueResult.TooManyParents;
            }
            if (parents.Any(p => !allowedParents.Contains(p)))
            {
                return WorkflowEnqueueResult.ContainmentViolation;
            }
        }

        return WorkflowEnqueueResult.Ok;
    }

    /// <summary>
    /// Applies a validated workflow: inserts the workflow row, then every member in
    /// dependency order (parents before children, possible because the graph is acyclic), and records
    /// the immutable structural edges. The caller must hold the gate and have validated first.
    /// </summary>
    private void ApplyWorkflowLocked(WorkflowDefinition workflow, DateTimeOffset now)
    {
        // Append leaves the existing Workflows row untouched (its CreatedAt/name stand); only a
        // creation writes the row.
        if (!workflow.IsAppend)
        {
            _workflows[workflow.WorkflowId] = new WorkflowRecord
            {
                WorkflowId = workflow.WorkflowId,
                Name = workflow.Name,
                CreatedAt = now,
                Retention = workflow.Retention,
                RestartedFrom = workflow.RestartedFrom, // lineage pointer set on a Restart (ADR 0023)
            };
        }

        foreach (var member in TopologicallyOrdered(workflow.Members))
        {
            var applied = EnqueueLocked(member, now, workflow.WorkflowId);
            if (applied != EnqueueResult.Ok) // always-on assertion: validated above
            {
                throw new InvalidOperationException(
                    $"Workflow enqueue commit failed for member {member.JobId}: {applied}.");
            }
        }

        // Structural edges: immutable, recorded once, so the graph view stays total even after
        // parents terminate and the live gating edges (_childrenByParent) resolve away.
        var edges = _workflowEdges.TryGetValue(workflow.WorkflowId, out var existing) ? existing : [];
        foreach (var member in workflow.Members)
        {
            foreach (var parent in member.Parents.Distinct())
            {
                edges.Add(new WorkflowEdge(parent, member.JobId));
            }
        }
        _workflowEdges[workflow.WorkflowId] = edges;
    }

    /// <summary>
    /// Orders members so every member follows its in-batch parents (Kahn's algorithm). Parents that
    /// are NOT in this batch (an append's existing members) are already inserted, so they impose no
    /// ordering. The builder guarantees acyclicity, so a complete order always exists; insertion order
    /// breaks ties to stay deterministic.
    /// </summary>
    private static IReadOnlyList<NewJob> TopologicallyOrdered(IReadOnlyList<NewJob> members)
    {
        var byId = members.ToDictionary(m => m.JobId);
        var indegree = members.ToDictionary(m => m.JobId, m => m.Parents.Distinct().Count(byId.ContainsKey));
        var ready = new Queue<NewJob>(members.Where(m => indegree[m.JobId] == 0));
        var children = new Dictionary<Guid, List<Guid>>();
        foreach (var m in members)
        {
            foreach (var p in m.Parents.Distinct().Where(byId.ContainsKey))
            {
                (children.TryGetValue(p, out var list) ? list : children[p] = []).Add(m.JobId);
            }
        }

        var ordered = new List<NewJob>(members.Count);
        while (ready.Count > 0)
        {
            var m = ready.Dequeue();
            ordered.Add(m);
            if (children.TryGetValue(m.JobId, out var kids))
            {
                foreach (var kid in kids)
                {
                    if (--indegree[kid] == 0)
                    {
                        ready.Enqueue(byId[kid]);
                    }
                }
            }
        }

        // A complete order means acyclic; a short result would mean a cycle slipped past the builder.
        return ordered.Count == members.Count ? ordered : members;
    }

    /// <summary>
    /// Appends one transition-log entry for a job's resulting state, in the same
    /// critical section as the state change it records — the In-Memory analogue of the
    /// adapters' "same transaction" atomicity. <paramref name="now"/> is the store's clock
    /// input (Virtual Time under simulation), so the log is deterministic. The bound drops the
    /// oldest entry once the cap is exceeded; the ordinal keeps climbing so it never repeats.
    /// <paramref name="failureDetail"/> is the Shell-captured exception text written only on a
    /// failing transition; it is clamped to <c>MaxFailureDetailBytes</c> and null on every
    /// other transition. The caller must hold the gate.
    /// </summary>
    private void RecordTransition(
        Guid jobId, JobState state, int attempt, DateTimeOffset now, string? failureDetail = null)
    {
        // Job History Policy (§5.12, ADR 0011) gates writes, not schema. Off records no row at all;
        // Transitions records the row but drops Failure Detail; the full rung keeps the clamped
        // detail. The map always exists — flipping the policy is config, never a migration.
        if (_historyPolicy == JobHistoryPolicy.Off)
        {
            return;
        }
        if (_historyPolicy == JobHistoryPolicy.Transitions)
        {
            failureDetail = null; // record the transition, but never the detail it would have carried
        }

        if (!_transitions.TryGetValue(jobId, out var history))
        {
            _transitions[jobId] = history = [];
        }
        var ordinal = _nextOrdinal.GetValueOrDefault(jobId);
        _nextOrdinal[jobId] = ordinal + 1;
        var clampedDetail = _bounds.ClampFailureDetail(failureDetail);
        history.Add(new JobTransition(ordinal, now, state, attempt, clampedDetail));
        if (history.Count > _bounds.MaxTransitionsPerJob)
        {
            history.RemoveAt(0); // oldest dropped; ordinal preserved on survivors
        }

        // Observer-delivery capability (§5.13): the same recorded transition, given a global log
        // Position the Observer cursors walk. The job row is present (every transition belongs to a
        // live job at record time), so its Wire Name / Queue ride along for subscription filtering.
        if (_jobs.TryGetValue(jobId, out var owner))
        {
            _observerLog.Add(new ObserverLogEntry(
                _observerLog.Count, jobId, ordinal, owner.WireName, owner.Queue, state, attempt, now, clampedDetail));
        }
    }

    /// <summary>
    /// The dependency latch: runs inside the same lock as every terminal
    /// transition, so a crash interleaving can never leave a fired parent with an
    /// unresolved child. Each parent-child edge resolves exactly once.
    /// </summary>
    private void ResolveChildLatches(Guid parentId, JobState parentState, DateTimeOffset now)
    {
        if (!_childrenByParent.Remove(parentId, out var children))
        {
            return;
        }

        foreach (var childId in children)
        {
            var child = _jobs[childId];
            if (child.State != JobState.AwaitingParent)
            {
                continue; // already cancelled via another failed parent
            }

            if (child.Mode == DependencyMode.OnSuccess && parentState != JobState.Succeeded)
            {
                _jobs[childId] = child with
                {
                    State = JobState.Cancelled,
                    ParentsRemaining = 0,
                    TerminalAt = now,
                    TerminalCause = ParentFailureCause(parentState),
                };
                RecordTransition(childId, JobState.Cancelled, child.Attempt, now);
                ResolveChildLatches(childId, JobState.Cancelled, now); // cascade
                continue;
            }

            var remaining = child.ParentsRemaining - 1;
            _jobs[childId] = remaining > 0
                ? child with { ParentsRemaining = remaining }
                : child with
                {
                    State = JobState.Scheduled,
                    ParentsRemaining = 0,
                    DueTime = child.DueTime > now ? child.DueTime : now,
                };
            // Only the latch RELEASE (last parent terminal) is a state change worth a transition;
            // a mere decrement of ParentsRemaining keeps the child in AwaitingParent (§5.12).
            if (remaining <= 0)
            {
                RecordTransition(childId, JobState.Scheduled, child.Attempt, now);
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default)
    {
        var maxJobs = Math.Min(request.MaxJobs, _bounds.MaxClaimBatch);
        var claimed = new List<JobRecord>();

        lock (_gate)
        {
            foreach (var queue in request.Queues)
            {
                if (claimed.Count >= maxJobs)
                {
                    break;
                }

                if (_pausedQueues.Contains(queue))
                {
                    continue; // a Paused Queue yields nothing to Claim (§5.8)
                }

                // Concurrency Limit (I3): slot usage is the live Leased count in this Queue.
                var slots = _queueLimits.GetValueOrDefault(queue) is { } limit
                    ? limit - _jobs.Values.Count(j => j.State == JobState.Leased && j.Queue == queue)
                    : int.MaxValue;
                if (slots <= 0)
                {
                    continue;
                }

                // Due-time order within a Queue, enqueue sequence as the deterministic tiebreak.
                var due = _jobs.Values
                    .Where(j => j.State == JobState.Scheduled
                        && j.Queue == queue
                        && j.DueTime <= request.Now)
                    .OrderBy(j => j.DueTime)
                    .ThenBy(j => j.Sequence)
                    .ToList();

                foreach (var job in due)
                {
                    if (claimed.Count >= maxJobs || slots <= 0)
                    {
                        break;
                    }
                    slots--;

                    var leased = job with
                    {
                        State = JobState.Leased,
                        Attempt = job.Attempt + 1,
                        LeaseOwner = request.WorkerId,
                        LeaseExpiry = request.Now + request.LeaseDuration,
                    };
                    _jobs[leased.JobId] = leased;
                    RecordTransition(leased.JobId, JobState.Leased, leased.Attempt, request.Now);
                    claimed.Add(leased);
                }
            }
        }

        return ValueTask.FromResult<IReadOnlyList<JobRecord>>(claimed);
    }

    /// <inheritdoc/>
    public ValueTask<OutcomeResult> ReportOutcomeAsync(
        Guid jobId, string workerId, int attempt, JobOutcome outcome, DateTimeOffset now,
        string? failureDetail = null,
        JobTags? addedTags = null,
        ReadOnlyMemory<byte>? output = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job)
                || job.State != JobState.Leased
                || job.LeaseOwner != workerId
                || job.Attempt != attempt
                || job.LeaseExpiry <= now)
            {
                // StaleLease: nothing changes, so the buffered Tag delta and Job Output are discarded
                // with the fenced-out outcome (ADR 0022/0026) — no split-brain writes from a stale node.
                return ValueTask.FromResult(OutcomeResult.StaleLease);
            }

            // Applied: the runtime Tag delta rides the fence, unioning onto the job's Tags (set
            // semantics, so it accumulates across Attempts and re-adding a Tag is a no-op).
            if (addedTags is { Count: > 0 })
            {
                var unioned = job.Tags;
                foreach (var tag in addedTags)
                {
                    unioned = unioned.With(tag);
                }
                job = job with { Tags = unioned };
            }

            var next = outcome switch
            {
                JobOutcome.Success => job with
                {
                    State = JobState.Succeeded,
                    LeaseOwner = null,
                    LeaseExpiry = null,
                    TerminalAt = now,
                },
                JobOutcome.Failure { NextDueTime: { } retryAt } => job with
                {
                    State = JobState.Scheduled,
                    DueTime = retryAt,
                    LeaseOwner = null,
                    LeaseExpiry = null,
                },
                JobOutcome.Failure failure => job with
                {
                    State = JobState.DeadLettered,
                    LeaseOwner = null,
                    LeaseExpiry = null,
                    TerminalAt = now,
                    TerminalCause = failure.Error,
                },
                JobOutcome.Cancelled cancelled => job with
                {
                    State = JobState.Cancelled,
                    LeaseOwner = null,
                    LeaseExpiry = null,
                    CancelRequested = false,
                    TerminalAt = now,
                    TerminalCause = cancelled.Cause,
                },
                JobOutcome.Unroutable unroutable => job with
                {
                    State = JobState.Quarantined,
                    LeaseOwner = null,
                    LeaseExpiry = null,
                    TerminalAt = now,
                    TerminalCause = unroutable.Reason,
                },
                _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
            };

            // Job Output (ADR 0026) rides the same fence but persists ONLY on success — every other
            // outcome (including a graceful Failure) writes none. Over MaxOutputBytes it is REJECTED
            // loudly, never truncated (a clipped blob is undeserializable). The check precedes the
            // commit below, so an over-limit write leaves the store untouched (Effect-Once holds).
            if (outcome is JobOutcome.Success && output is { } outputBlob)
            {
                if (outputBlob.Length > _bounds.MaxOutputBytes)
                {
                    throw new JobOutputTooLargeException(jobId, outputBlob.Length, _bounds.MaxOutputBytes);
                }
                next = next with { Output = outputBlob };
            }

            _jobs[jobId] = next;
            // Failure Detail rides only the failing transition (§5.12); every other outcome
            // records null. The Core never reads it — it only learned the Attempt failed.
            RecordTransition(
                jobId, next.State, next.Attempt, now,
                outcome is JobOutcome.Failure ? failureDetail : null);
            if (next.State.IsTerminal())
            {
                ResolveChildLatches(jobId, next.State, now);
            }
            return ValueTask.FromResult(OutcomeResult.Applied);
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<HeartbeatResult>> HeartbeatAsync(
        string workerId,
        IReadOnlyList<Guid> jobIds,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var results = new List<HeartbeatResult>(jobIds.Count);

        lock (_gate)
        {
            foreach (var jobId in jobIds)
            {
                if (_jobs.TryGetValue(jobId, out var job)
                    && job.State == JobState.Leased
                    && job.LeaseOwner == workerId
                    && job.LeaseExpiry > now)
                {
                    _jobs[jobId] = job with { LeaseExpiry = now + leaseDuration };
                    results.Add(new HeartbeatResult(jobId, Renewed: true, job.CancelRequested));
                }
                else
                {
                    results.Add(new HeartbeatResult(jobId, Renewed: false, CancelRequested: false));
                }
            }
        }

        return ValueTask.FromResult<IReadOnlyList<HeartbeatResult>>(results);
    }

    /// <inheritdoc/>
    public ValueTask<int> ExpireLeasesAsync(
        DateTimeOffset now, int maxJobs, IReadOnlyList<string> queues, Core.RetryDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        if (queues.Count == 0)
        {
            return ValueTask.FromResult(0);
        }

        var served = queues as ISet<string> ?? new HashSet<string>(queues);
        var expired = 0;

        lock (_gate)
        {
            var dueForExpiry = _jobs.Values
                .Where(j => j.State == JobState.Leased && j.LeaseExpiry <= now && served.Contains(j.Queue))
                .OrderBy(j => j.LeaseExpiry)
                .ThenBy(j => j.Sequence)
                .Take(maxJobs)
                .ToList();

            foreach (var job in dueForExpiry)
            {
                // The claim already counted this Attempt, so expiry just disposes it.
                var retryAt = disposition.NextAttemptAt(job.Attempt, now);
                var next = retryAt is { } dueTime
                    ? job with
                    {
                        State = JobState.Scheduled,
                        DueTime = dueTime,
                        LeaseOwner = null,
                        LeaseExpiry = null,
                    }
                    : job with
                    {
                        State = JobState.DeadLettered,
                        LeaseOwner = null,
                        LeaseExpiry = null,
                        TerminalAt = now,
                        TerminalCause = $"Lease expired on attempt {job.Attempt} (attempt ceiling reached).",
                    };
                _jobs[next.JobId] = next;
                RecordTransition(next.JobId, next.State, next.Attempt, now);
                if (next.State == JobState.DeadLettered)
                {
                    ResolveChildLatches(next.JobId, next.State, now);
                }
                expired++;
            }
        }

        return ValueTask.FromResult(expired);
    }

    /// <inheritdoc/>
    public ValueTask<CancelResult> CancelJobAsync(
        Guid jobId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return ValueTask.FromResult(CancelResult.NotCancellable);
            }

            switch (job.State)
            {
                case JobState.Scheduled or JobState.AwaitingParent:
                    _jobs[jobId] = job with
                    {
                        State = JobState.Cancelled,
                        TerminalAt = now,
                        TerminalCause = actor,
                    };
                    RecordTransition(jobId, JobState.Cancelled, job.Attempt, now);
                    ResolveChildLatches(jobId, JobState.Cancelled, now);
                    AppendAudit(actor, OperatorAction.Cancel, jobId.ToString(), now);
                    return ValueTask.FromResult(CancelResult.CancelledImmediately);

                case JobState.Leased:
                    _jobs[jobId] = job with { CancelRequested = true };
                    AppendAudit(actor, OperatorAction.Cancel, jobId.ToString(), now);
                    return ValueTask.FromResult(CancelResult.CancellationRequested);

                default:
                    return ValueTask.FromResult(CancelResult.NotCancellable);
            }
        }
    }

    // ── §5.8 Operator Actions ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public ValueTask<RequeueResult> RequeueAsync(
        Guid jobId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Only the two dead states are recoverable (§3); anything else is rejected without effect.
            if (!_jobs.TryGetValue(jobId, out var job)
                || job.State is not (JobState.DeadLettered or JobState.Quarantined))
            {
                return ValueTask.FromResult(RequeueResult.NotRequeueable);
            }

            _jobs[jobId] = job with
            {
                State = JobState.Scheduled,
                Attempt = 0, // requeue resets the Attempt budget (§3)
                DueTime = now,
                LeaseOwner = null,
                LeaseExpiry = null,
                CancelRequested = false,
                TerminalAt = null,
                TerminalCause = null,
            };
            RecordTransition(jobId, JobState.Scheduled, 0, now); // Attempt budget reset (§3)
            AppendAudit(actor, OperatorAction.Requeue, jobId.ToString(), now);
            return ValueTask.FromResult(RequeueResult.Requeued);
        }
    }

    /// <inheritdoc/>
    public ValueTask PauseQueueAsync(
        string queue, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _pausedQueues.Add(queue);
            AppendAudit(actor, OperatorAction.PauseQueue, queue, now);
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask ResumeQueueAsync(
        string queue, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _pausedQueues.Remove(queue);
            AppendAudit(actor, OperatorAction.ResumeQueue, queue, now);
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<TriggerScheduleResult> TriggerScheduleNowAsync(
        string scheduleId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_schedules.TryGetValue(scheduleId, out var schedule))
            {
                return ValueTask.FromResult(TriggerScheduleResult.ScheduleNotFound);
            }

            // One instance due now; the Cursor and recorded ticks are left exactly as they were.
            var record = new JobRecord
            {
                JobId = JobIds.ForMintedTick(scheduleId, now),
                WireName = schedule.WireName,
                Payload = schedule.Payload,
                Queue = schedule.Queue,
                State = JobState.Scheduled,
                DueTime = now,
                ScheduleId = scheduleId,
                Sequence = _nextSequence,
            };
            if (_jobs.TryAdd(record.JobId, record))
            {
                _nextSequence++;
                RecordTransition(record.JobId, JobState.Scheduled, record.Attempt, now);
            }
            AppendAudit(actor, OperatorAction.TriggerScheduleNow, scheduleId, now);
            return ValueTask.FromResult(TriggerScheduleResult.Triggered);
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<OperatorAuditRecord>> ListAuditRecordsAsync(
        string target, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Insertion order is chronological (the gate serializes every appending action).
            return ValueTask.FromResult<IReadOnlyList<OperatorAuditRecord>>(
                [.. _audit.Where(a => a.Target == target)]);
        }
    }

    /// <summary>Append-only audit log; the caller must hold the gate.</summary>
    private void AppendAudit(string actor, OperatorAction action, string target, DateTimeOffset now)
        => _audit.Add(new OperatorAuditRecord(actor, action, target, now));

    /// <inheritdoc/>
    public ValueTask SetConcurrencyLimitAsync(
        string queue, int? limit, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _queueLimits[queue] = limit;
            AppendAudit(actor, OperatorAction.SetConcurrencyLimit, queue, now);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The next instant strictly after <paramref name="now"/> at which anything becomes due: a
    /// scheduled job's due time, a lease expiry, or a recurring schedule's next tick. A virtual-time
    /// harness can jump straight to it instead of waiting in real time.
    /// </summary>
    /// <param name="now">The instant to look forward from.</param>
    /// <returns>The earliest future activity instant, or null when nothing is pending.</returns>
    public DateTimeOffset? NextActivityAfter(DateTimeOffset now)
    {
        lock (_gate)
        {
            DateTimeOffset? next = null;
            void Consider(DateTimeOffset? candidate)
            {
                if (candidate > now && (next is null || candidate < next))
                {
                    next = candidate;
                }
            }

            foreach (var job in _jobs.Values)
            {
                switch (job.State)
                {
                    case JobState.Scheduled:
                        Consider(job.DueTime);
                        break;
                    case JobState.Leased:
                        Consider(job.LeaseExpiry);
                        break;
                }
            }
            foreach (var schedule in _schedules.Values)
            {
                // A poisoned schedule row never throws the probe — it simply contributes no
                // next-activity instant, the same isolation the mint planner applies.
                if (Core.ScheduleValidation.TryResolve(
                        schedule.Cron, schedule.TimeZoneId, out var cron, out var zone, out _))
                {
                    Consider(Core.ZonedCron.NextAfter(cron!, schedule.Cursor, zone));
                }
            }
            return next;
        }
    }

    /// <inheritdoc/>
    public ValueTask<JobRecord?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_jobs.TryGetValue(jobId, out var job) ? job : null);
        }
    }

    /// <inheritdoc/>
    public ValueTask<ReadOnlyMemory<byte>?> GetJobOutputAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Job Output (ADR 0026) lives on the job row, so it is fetched here and deleted with the
            // job under retention for free. Null for an unknown job or one that never set output.
            return ValueTask.FromResult(_jobs.TryGetValue(jobId, out var job) ? job.Output : null);
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<JobTransition>> GetJobHistoryAsync(
        Guid jobId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Oldest first; the list is already append-ordered (the gate serializes recording).
            return ValueTask.FromResult<IReadOnlyList<JobTransition>>(
                _transitions.TryGetValue(jobId, out var history) ? [.. history] : []);
        }
    }

    /// <inheritdoc/>
    public ValueTask<ObserverClaim> ClaimObserverDeliveriesAsync(
        ObserverClaimRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_observers.TryGetValue(request.ObserverId, out var observer))
            {
                _observers[request.ObserverId] = observer = new ObserverState();
            }
            // Remember the subscription so cursor advance (on report) can tell matching rows from
            // rows this Observer ignores. Run config, so it never changes within a run.
            observer.States = request.States;
            observer.WireName = request.WireName;
            observer.Queue = request.Queue;

            // Claim Lease (§5.13): exactly one node advances a given Observer's cursor at a time. A
            // live Lease held by a different worker means that node is delivering — back off. This
            // is what gives single delivery in the happy path while staying leaderless (ADR 0006).
            if (observer.LeaseOwner is { } held
                && !string.Equals(held, request.WorkerId, StringComparison.Ordinal)
                && observer.LeaseExpiry > request.Now)
            {
                return ValueTask.FromResult(ObserverClaim.None(request.ObserverId));
            }

            var deliveries = new List<ObserverClaimedDelivery>();
            foreach (var entry in _observerLog)
            {
                if (entry.Position <= observer.Cursor)
                {
                    continue; // already delivered-or-dead-lettered (or irrelevant) — behind the cursor
                }
                if (!Matches(entry, request.States, request.WireName, request.Queue))
                {
                    continue; // a transition this Observer does not watch — never blocks, never delivered
                }
                var progress = observer.InFlight.GetValueOrDefault(entry.Position);
                if (progress is { Resolution: not ObserverResolution.Pending })
                {
                    continue; // resolved but the cursor has not swept past it yet — not for redelivery
                }
                // Head-of-line (§0077): a row still in its backoff window holds the cursor, so we
                // claim nothing past it — in-order-per-Observer falls out of a single moving cursor.
                if (progress is { NextAttemptAt: { } next } && next > request.Now)
                {
                    break;
                }
                if (deliveries.Count >= request.MaxRows)
                {
                    break;
                }
                progress ??= new ObserverDeliveryProgress();
                progress.DeliveryAttempt++; // the claim is the start of a delivery Attempt (§5.13)
                progress.NextAttemptAt = null;
                observer.InFlight[entry.Position] = progress;
                deliveries.Add(new ObserverClaimedDelivery(
                    entry.Position, entry.JobId, entry.Ordinal, entry.WireName, entry.Queue,
                    entry.State, entry.Attempt, entry.Timestamp, entry.FailureDetail, progress.DeliveryAttempt));
            }

            if (deliveries.Count == 0)
            {
                return ValueTask.FromResult(ObserverClaim.None(request.ObserverId));
            }

            observer.LeaseOwner = request.WorkerId;
            observer.LeaseExpiry = request.Now + request.LeaseDuration;
            return ValueTask.FromResult(new ObserverClaim(request.ObserverId, Acquired: true, deliveries));
        }
    }

    /// <inheritdoc/>
    public ValueTask ReportObserverDeliveriesAsync(
        ObserverDeliveryReport report, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_observers.TryGetValue(report.ObserverId, out var observer))
            {
                return ValueTask.CompletedTask;
            }
            // Fence (§5.13): only the live claim-Lease holder may resolve deliveries and advance the
            // cursor. A stale survivor of a lapsed claim reports into the void — at-least-once intact.
            if (!string.Equals(observer.LeaseOwner, report.WorkerId, StringComparison.Ordinal)
                || observer.LeaseExpiry <= report.Now)
            {
                return ValueTask.CompletedTask;
            }

            foreach (var outcome in report.Outcomes)
            {
                if (!observer.InFlight.TryGetValue(outcome.Position, out var progress))
                {
                    continue; // already advanced past — a duplicate or stale report row
                }
                progress.Resolution = outcome.Disposition switch
                {
                    ObserverDeliveryDisposition.Delivered => ObserverResolution.Delivered,
                    ObserverDeliveryDisposition.DeadLettered => ObserverResolution.DeadLettered,
                    _ => ObserverResolution.Pending, // Retry: held, cursor will stall on it
                };
                if (outcome.Disposition == ObserverDeliveryDisposition.Retry)
                {
                    progress.NextAttemptAt = outcome.NextAttemptAt;
                }
            }

            AdvanceObserverCursor(observer, report.Now);
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public ValueTask<long> GetObserverCursorAsync(string observerId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_observers.TryGetValue(observerId, out var observer) ? observer.Cursor : -1L);
        }
    }

    /// <inheritdoc/>
    public ValueTask<ObserverLag> GetObserverLagAsync(
        ObserverLagRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var cursor = _observers.TryGetValue(request.ObserverId, out var observer) ? observer.Cursor : -1L;
            var pending = 0;
            DateTimeOffset? oldestPendingAt = null;
            foreach (var entry in _observerLog)
            {
                if (entry.Position <= cursor)
                {
                    continue; // the cursor has already swept past this row
                }
                if (!Matches(entry, request.States, request.WireName, request.Queue))
                {
                    continue; // a transition this Observer does not watch — never counted as lag
                }
                pending++;
                // The log is append-ordered, so the first match after the cursor is the one that has
                // waited longest — its Timestamp is the oldest-pending age.
                oldestPendingAt ??= entry.Timestamp;
            }
            return ValueTask.FromResult(new ObserverLag(cursor, pending, oldestPendingAt));
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<ObserverDeadLetterRecord>> ListObserverDeadLettersAsync(
        string observerId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<ObserverDeadLetterRecord>>(
                _observers.TryGetValue(observerId, out var observer) ? [.. observer.DeadLettered] : []);
        }
    }

    /// <summary>
    /// Sweeps the cursor forward over the contiguous prefix of resolved (Delivered or DeadLettered)
    /// matching rows — and over every non-matching row, which needs no delivery — stopping at the
    /// first matching row still pending. A dead-lettered row is recorded loudly as the cursor passes
    /// it. The caller holds the gate.
    /// </summary>
    private void AdvanceObserverCursor(ObserverState observer, DateTimeOffset now)
    {
        foreach (var entry in _observerLog)
        {
            if (entry.Position <= observer.Cursor)
            {
                continue;
            }
            if (!Matches(entry, observer.States, observer.WireName, observer.Queue))
            {
                observer.Cursor = entry.Position; // irrelevant to this Observer — sweep over it
                continue;
            }
            if (observer.InFlight.TryGetValue(entry.Position, out var progress)
                && progress.Resolution != ObserverResolution.Pending)
            {
                if (progress.Resolution == ObserverResolution.DeadLettered)
                {
                    observer.DeadLettered.Add(new ObserverDeadLetterRecord(
                        entry.Position, entry.JobId, entry.Ordinal, entry.State, entry.Attempt,
                        progress.DeliveryAttempt, now));
                }
                observer.InFlight.Remove(entry.Position);
                observer.Cursor = entry.Position;
                continue;
            }
            break; // matching row not yet resolved — head-of-line; the cursor holds here
        }
    }

    private static bool Matches(ObserverLogEntry entry, IReadOnlyList<JobState> states, string? wireName, string? queue)
        => states.Contains(entry.State)
            && (wireName is null || string.Equals(wireName, entry.WireName, StringComparison.Ordinal))
            && (queue is null || string.Equals(queue, entry.Queue, StringComparison.Ordinal));

    /// <summary>One global transition-log row the observer cursors walk.</summary>
    private sealed record ObserverLogEntry(
        long Position, Guid JobId, long Ordinal, string WireName, string Queue,
        JobState State, int Attempt, DateTimeOffset Timestamp, string? FailureDetail);

    private enum ObserverResolution { Pending, Delivered, DeadLettered }

    /// <summary>Per-row delivery bookkeeping for an observer: attempt count + resolution.</summary>
    private sealed class ObserverDeliveryProgress
    {
        public int DeliveryAttempt;
        public ObserverResolution Resolution = ObserverResolution.Pending;
        public DateTimeOffset? NextAttemptAt;
    }

    /// <summary>One observer's durable delivery state: cursor, claim lease, in-flight rows, dead-letters.</summary>
    private sealed class ObserverState
    {
        public long Cursor = -1; // nothing delivered yet → deliver from log position 0
        public string? LeaseOwner;
        public DateTimeOffset? LeaseExpiry;
        public IReadOnlyList<JobState> States = [];
        public string? WireName;
        public string? Queue;
        public readonly Dictionary<long, ObserverDeliveryProgress> InFlight = [];
        public readonly List<ObserverDeadLetterRecord> DeadLettered = [];
    }

    /// <inheritdoc/>
    public ValueTask<int> PurgeTerminalAsync(
        TerminalStateClass stateClass, DateTimeOffset terminalBefore, int maxJobs,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Workflow-aware retention (ADR 0023, §5.11): a Workflow member is retention-eligible only
            // once the WHOLE Workflow has drained (every member terminal), and then the window starts
            // from the DRAIN point (max member TerminalAt) — not each member's own terminal instant —
            // so the graph stays coherent (and materialized for Restart) for the Workflow's whole life.
            // Non-workflow jobs keep today's per-job rule. Drain reads WorkflowId at retention time
            // only, never the scheduling hot path.
            var drainByWorkflow = WorkflowDrainInstantsLocked();
            var purgeable = _jobs.Values
                .Where(j => InClass(j.State, stateClass) && RetentionEligible(j, terminalBefore, drainByWorkflow))
                .OrderBy(j => j.TerminalAt)
                .ThenBy(j => j.Sequence)
                .Take(Math.Min(maxJobs, _bounds.MaxPurgeBatch))
                .ToList();
            var touchedWorkflows = new HashSet<Guid>();
            foreach (var job in purgeable)
            {
                _jobs.Remove(job.JobId);
                // §5.11: Transition Log rows are deleted WITH the job (same-key delete here, FK
                // cascade in the SQL adapters) — the log lives exactly as long as the job.
                _transitions.Remove(job.JobId);
                _nextOrdinal.Remove(job.JobId);
                if (job.WorkflowId is { } wf)
                {
                    touchedWorkflows.Add(wf);
                }
            }
            // When a Workflow's last member is purged, drop its now-orphaned identity + structural
            // edges so the maps do not leak rows for Workflows that no longer have any jobs.
            foreach (var wf in touchedWorkflows)
            {
                if (!_jobs.Values.Any(j => j.WorkflowId == wf))
                {
                    _workflows.Remove(wf);
                    _workflowEdges.Remove(wf);
                }
            }
            return ValueTask.FromResult(purgeable.Count);
        }
    }

    /// <summary>
    /// Per-workflow drain instant: for each workflow, the <c>max</c> member
    /// <see cref="JobRecord.TerminalAt"/> when EVERY member is terminal, else <c>null</c> (still
    /// live). The caller holds the gate.
    /// </summary>
    private Dictionary<Guid, DateTimeOffset?> WorkflowDrainInstantsLocked()
    {
        var drain = new Dictionary<Guid, DateTimeOffset?>();
        var live = new HashSet<Guid>();
        foreach (var job in _jobs.Values)
        {
            if (job.WorkflowId is not { } wf)
            {
                continue;
            }
            if (!job.State.IsTerminal())
            {
                live.Add(wf); // a single live member means the whole Workflow has not drained
                drain[wf] = null;
                continue;
            }
            if (live.Contains(wf))
            {
                continue;
            }
            var running = drain.GetValueOrDefault(wf);
            drain[wf] = running is { } prev && prev >= job.TerminalAt ? prev : job.TerminalAt;
        }
        return drain;
    }

    private static bool RetentionEligible(
        JobRecord j, DateTimeOffset terminalBefore, IReadOnlyDictionary<Guid, DateTimeOffset?> drainByWorkflow)
        => j.WorkflowId is { } wf
            ? drainByWorkflow.GetValueOrDefault(wf) is { } drain && drain <= terminalBefore
            : j.TerminalAt <= terminalBefore;

    private static bool InClass(JobState state, TerminalStateClass stateClass)
        => stateClass == TerminalStateClass.SucceededOrCancelled
            ? state is JobState.Succeeded or JobState.Cancelled
            : state is JobState.DeadLettered or JobState.Quarantined;

    /// <inheritdoc/>
    public ValueTask UpsertScheduleAsync(ScheduleRecord schedule, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Replacing a schedule keeps its Cursor: redefining "the nightly sync" must not
            // replay or skip ticks already resolved.
            _schedules[schedule.ScheduleId] = _schedules.TryGetValue(schedule.ScheduleId, out var existing)
                ? schedule with { Cursor = existing.Cursor }
                : schedule;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask RemoveScheduleAsync(string scheduleId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _schedules.Remove(scheduleId);
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<ScheduleSnapshot>> ListSchedulesAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<ScheduleSnapshot>>(
                _schedules.Values
                    .OrderBy(s => s.ScheduleId, StringComparer.Ordinal)
                    .Select(s => new ScheduleSnapshot(
                        // Payload is not part of the listing (§5.7 hot path); MintDue re-reads it
                        // from the stored row, so the per-poll load never carries blobs (0039).
                        s with { Payload = ReadOnlyMemory<byte>.Empty },
                        HasLiveInstance: _jobs.Values.Any(j =>
                            j.ScheduleId == s.ScheduleId && !j.State.IsTerminal())))
                    .ToList());
        }
    }

    /// <inheritdoc/>
    public ValueTask<int> MintDueAsync(
        IReadOnlyList<Core.MintDecision> decisions, CancellationToken cancellationToken = default)
    {
        var minted = 0;

        lock (_gate)
        {
            foreach (var decision in decisions)
            {
                // Cursor fencing: a decision computed from a stale cursor is skipped whole —
                // another node already minted those ticks (cluster-wide exactly-once).
                if (!_schedules.TryGetValue(decision.ScheduleId, out var schedule)
                    || schedule.Cursor != decision.ExpectedCursor)
                {
                    continue;
                }

                _schedules[decision.ScheduleId] = schedule with
                {
                    Cursor = decision.NewCursor,
                    SkippedTicks = [.. ((List<DateTimeOffset>)
                        [.. schedule.SkippedTicks, .. decision.SkippedTicks]).TakeLast(_bounds.MaxRecordedSkippedTicks)],
                };
                foreach (var tick in decision.Ticks)
                {
                    var record = new JobRecord
                    {
                        JobId = JobIds.ForMintedTick(schedule.ScheduleId, tick),
                        WireName = schedule.WireName,
                        Payload = schedule.Payload,
                        Queue = schedule.Queue,
                        State = JobState.Scheduled,
                        DueTime = tick,
                        ScheduleId = schedule.ScheduleId,
                        Sequence = _nextSequence,
                    };
                    if (_jobs.TryAdd(record.JobId, record))
                    {
                        _nextSequence++;
                        minted++;
                        // MintDue carries no `now`; the tick (the minted instance's due instant)
                        // is the deterministic timestamp for its first Scheduled transition (§5.12).
                        RecordTransition(record.JobId, JobState.Scheduled, record.Attempt, tick);
                    }
                }
            }
        }

        return ValueTask.FromResult(minted);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<JobRecord>> ListJobsAsync(
        JobQuery query, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var newestFirst = query.SortDirection == JobSortDirection.NewestFirst;
            var filtered = _jobs.Values
                .Where(j => MatchesScope(j, query)
                    // The cursor is direction-relative: newest-first continues toward OLDER jobs.
                    && (query.AfterSequence is null
                        || (newestFirst ? j.Sequence < query.AfterSequence : j.Sequence > query.AfterSequence)));
            var ordered = newestFirst
                ? filtered.OrderByDescending(j => j.Sequence)
                : filtered.OrderBy(j => j.Sequence);
            return ValueTask.FromResult<IReadOnlyList<JobRecord>>(
                ordered
                    .Take(Math.Min(query.MaxResults, _bounds.MaxMonitorPageSize))
                    .ToList());
        }
    }

    /// <summary>
    /// The <em>scope</em> predicate shared by <see cref="ListJobsAsync"/> and
    /// <see cref="FacetAsync"/>: the scalar filters AND-ed with the tag predicates. Pagination
    /// (cursor/sort/take) is NOT part of the scope — facets count the whole matching population.
    /// </summary>
    private static bool MatchesScope(JobRecord j, JobQuery query)
        => (query.State is null || j.State == query.State)
            && (query.Queue is null || j.Queue == query.Queue)
            && (query.WireName is null || j.WireName == query.WireName)
            && (query.ScheduleId is null || j.ScheduleId == query.ScheduleId)
            // Tag predicates are AND-ed (ADR 0022): a job must satisfy EVERY predicate.
            // An empty list adds no constraint (All over empty is true). OR is out of scope.
            && query.TagPredicates.All(p => p.Matches(j.Tags));

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<TagFacet>> FacetAsync(
        string key, JobQuery? baseQuery = null, int maxResults = int.MaxValue, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Scope the population first (ADR 0022): the same scalar + tag predicates ListJobs uses,
            // never its pagination. A null baseQuery facets over every job.
            var scoped = baseQuery is null
                ? _jobs.Values
                : _jobs.Values.Where(j => MatchesScope(j, baseQuery));

            // Count DISTINCT JOBS per value under the requested key (key="" ⇒ Labels). A job's Tags
            // are a set, so it appears once per distinct value it carries — a multi-value key counts
            // the job under each value, and the same Tag on a job never double-counts.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var job in scoped)
            {
                foreach (var tag in job.Tags)
                {
                    if (string.Equals(tag.Key, key, StringComparison.Ordinal))
                    {
                        counts[tag.Value] = counts.GetValueOrDefault(tag.Value) + 1;
                    }
                }
            }

            // Order by count DESC, value ASC (ordinal) as the stable tiebreak — deterministic and
            // identical to the adapters' ORDER BY — then cap to the top maxResults buckets (ADR 0042).
            return ValueTask.FromResult<IReadOnlyList<TagFacet>>(
                counts
                    .OrderByDescending(kvp => kvp.Value)
                    .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
                    .Take(Math.Max(0, maxResults))
                    .Select(kvp => new TagFacet(kvp.Key, kvp.Value))
                    .ToList());
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(
        TagSuggestQuery query, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(query.MaxResults, 1, TagSuggestQuery.MaxSuggestResults);
        var foldedPrefix = TagSuggestFold.Lower(query.Prefix);

        lock (_gate)
        {
            // Global read (ADR 0042): every distinct Tag in the store, never scoped to a job filter.
            if (query.Key is not null)
            {
                // Stage two: distinct values carried under one key (key="" ⇒ the Label dimension),
                // ASCII-CI prefix-matched, lexicographic, keyset-paged by the last returned value.
                var values = new HashSet<string>(StringComparer.Ordinal);
                foreach (var job in _jobs.Values)
                {
                    foreach (var tag in job.Tags)
                    {
                        if (string.Equals(tag.Key, query.Key, StringComparison.Ordinal)
                            && TagSuggestFold.PrefixMatch(tag.Value, foldedPrefix))
                        {
                            values.Add(tag.Value);
                        }
                    }
                }

                var afterValue = query.After?.Value;
                var page = values
                    .Where(v => afterValue is null || TagSuggestFold.Compare(v, afterValue) > 0)
                    .OrderBy(v => v, TagSuggestComparer.Instance)
                    .Take(limit)
                    .Select(v => new TagSuggestion(query.Key, v))
                    .ToList();

                return ValueTask.FromResult<IReadOnlyList<TagSuggestion>>(page);
            }

            // Stage one: Labels (a block) then keys (a block), each ASCII-CI prefix-matched and
            // lexicographic. Labels sort first because a Label's key is the empty string.
            var labels = new HashSet<string>(StringComparer.Ordinal);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var job in _jobs.Values)
            {
                foreach (var tag in job.Tags)
                {
                    if (tag.Key.Length == 0)
                    {
                        if (TagSuggestFold.PrefixMatch(tag.Value, foldedPrefix))
                        {
                            labels.Add(tag.Value);
                        }
                    }
                    else if (TagSuggestFold.PrefixMatch(tag.Key, foldedPrefix))
                    {
                        keys.Add(tag.Key);
                    }
                }
            }

            var ordered = new List<TagSuggestion>(labels.Count + keys.Count);
            ordered.AddRange(labels.OrderBy(v => v, TagSuggestComparer.Instance).Select(v => new TagSuggestion(string.Empty, v)));
            ordered.AddRange(keys.OrderBy(k => k, TagSuggestComparer.Instance).Select(k => new TagSuggestion(k, string.Empty)));

            // Keyset cursor over the concatenated (labels, then keys) order — return only suggestions
            // strictly after the cursor, so a cursor whose Tag has since vanished still resumes at the
            // right position rather than duplicating or skipping a window.
            var cursor = query.After;
            var window = ordered
                .Where(s => cursor is null || CompareStageOne(s, cursor) > 0)
                .Take(limit)
                .ToList();

            return ValueTask.FromResult<IReadOnlyList<TagSuggestion>>(window);
        }
    }

    // Total order over stage-one suggestions: Labels (block 0) before keys (block 1), each block
    // ordered by the ASCII-folded token with the canonical token as the tiebreak. Mirrors the SQL
    // ORDER BY / keyset predicate the adapters build.
    private static int CompareStageOne(TagSuggestion a, TagSuggestion b)
    {
        var blockA = a.IsLabel ? 0 : 1;
        var blockB = b.IsLabel ? 0 : 1;
        if (blockA != blockB)
        {
            return blockA - blockB;
        }

        var nameA = a.IsLabel ? a.Value : a.Key;
        var nameB = b.IsLabel ? b.Value : b.Key;
        return TagSuggestFold.Compare(nameA, nameB);
    }

    private sealed class TagSuggestComparer : IComparer<string>
    {
        public static readonly TagSuggestComparer Instance = new();

        public int Compare(string? x, string? y) => TagSuggestFold.Compare(x ?? string.Empty, y ?? string.Empty);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<QueueStateCount>> CountJobsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<QueueStateCount>>(
                _jobs.Values
                    .GroupBy(j => (j.Queue, j.State))
                    .OrderBy(g => g.Key.Queue, StringComparer.Ordinal)
                    .ThenBy(g => g.Key.State)
                    .Select(g => new QueueStateCount(g.Key.Queue, g.Key.State, g.Count()))
                    .ToList());
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<QueueSettings>> ListQueueSettingsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // A Queue can be paused with or without a limit and vice versa: the row is the
            // union of both write paths' keys (§5.8, §5.10).
            return ValueTask.FromResult<IReadOnlyList<QueueSettings>>(
                _pausedQueues.Union(_queueLimits.Keys, StringComparer.Ordinal)
                    .OrderBy(q => q, StringComparer.Ordinal)
                    .Select(q => new QueueSettings(
                        q, _pausedQueues.Contains(q), _queueLimits.GetValueOrDefault(q)))
                    .ToList());
        }
    }

    /// <inheritdoc/>
    public ValueTask<DependencyEdges> GetDependencyEdgesAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Edges are deleted as each parent terminates (the latch cascade), so a child's
            // surviving edges name exactly its still-gating parents — never the full original
            // set (ADR 0009).
            var gatingParents = _childrenByParent
                .Where(kvp => kvp.Value.Contains(jobId))
                .Select(kvp => kvp.Key)
                .OrderBy(id => id)
                .ToList();
            var children = _childrenByParent.TryGetValue(jobId, out var edges)
                ? edges.OrderBy(id => id).ToList()
                : [];
            return ValueTask.FromResult(new DependencyEdges(gatingParents, children));
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<WorkflowSnapshot>> ListWorkflowsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Members are found by the WorkflowId scalar; group once so each Workflow's status is a
            // single pass over its members. Ordered by CreatedAt (oldest first), WorkflowId as a
            // stable tiebreak so the listing is deterministic across adapters.
            var statesByWorkflow = MemberStatesByWorkflowLocked();
            return ValueTask.FromResult<IReadOnlyList<WorkflowSnapshot>>(
                _workflows.Values
                    .OrderBy(w => w.CreatedAt)
                    .ThenBy(w => w.WorkflowId)
                    .Select(w =>
                    {
                        var states = statesByWorkflow.GetValueOrDefault(w.WorkflowId) ?? [];
                        return new WorkflowSnapshot
                        {
                            WorkflowId = w.WorkflowId,
                            Name = w.Name,
                            CreatedAt = w.CreatedAt,
                            Status = WorkflowStatusProjection.Project(states),
                            MemberCount = states.Count,
                            RestartedFrom = w.RestartedFrom,
                        };
                    })
                    .ToList());
        }
    }

    /// <inheritdoc/>
    public ValueTask<WorkflowGraph?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_workflows.TryGetValue(workflowId, out var workflow))
            {
                return ValueTask.FromResult<WorkflowGraph?>(null);
            }

            var members = _jobs.Values
                .Where(j => j.WorkflowId == workflowId)
                .OrderBy(j => j.Sequence)
                .ToList();
            var edges = (_workflowEdges.TryGetValue(workflowId, out var e) ? e : [])
                .OrderBy(edge => edge.Parent).ThenBy(edge => edge.Child)
                .ToList();
            return ValueTask.FromResult<WorkflowGraph?>(new WorkflowGraph
            {
                WorkflowId = workflow.WorkflowId,
                Name = workflow.Name,
                CreatedAt = workflow.CreatedAt,
                Status = WorkflowStatusProjection.Project(members.Select(m => m.State)),
                Members = members,
                Edges = edges,
                RestartedFrom = workflow.RestartedFrom,
            });
        }
    }

    /// <summary>The current member states grouped by Workflow (caller holds the gate).</summary>
    private Dictionary<Guid, List<JobState>> MemberStatesByWorkflowLocked()
    {
        var states = new Dictionary<Guid, List<JobState>>();
        foreach (var job in _jobs.Values)
        {
            if (job.WorkflowId is { } wf)
            {
                (states.TryGetValue(wf, out var list) ? list : states[wf] = []).Add(job.State);
            }
        }
        return states;
    }
}
