using System.Data;
using System.Data.Common;

namespace BackWave.Storage.InMemory;

/// <summary>
/// The in-memory store's transactional-enqueue scope. Enqueues and reschedules buffer here and become
/// visible atomically on <see cref="Commit"/>. A rollback, explicit or by disposing without a commit,
/// means they never happened: the jobs never existed and no due time moved, with no trace.
/// </summary>
public sealed class InMemoryTransaction : DbTransaction
{
    private readonly List<(NewJob Job, DateTimeOffset Now)> _pending = [];
    private readonly List<(WorkflowDefinition Def, DateTimeOffset Now)> _pendingWorkflows = [];
    private readonly List<PendingReschedule> _pendingReschedules = [];
    private bool _completed;

    internal InMemoryTransaction(InMemoryJobStore store) => Store = store;

    internal InMemoryJobStore Store { get; }

    /// <inheritdoc/>
    public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;

    /// <inheritdoc/>
    protected override DbConnection? DbConnection => null;

    internal bool HasPending(Guid jobId) => FindPending(jobId) is not null;

    /// <summary>Every buffered job, plain or a workflow member, in the order buffered.</summary>
    internal IEnumerable<NewJob> PendingJobs
        => _pending.Select(p => p.Job).Concat(_pendingWorkflows.SelectMany(w => w.Def.Members));

    /// <summary>The job buffered under <paramref name="jobId"/>, plain or a workflow member; null when none is.</summary>
    internal NewJob? FindPending(Guid jobId) => PendingJobs.FirstOrDefault(j => j.JobId == jobId);

    internal bool HasPendingWorkflow(Guid workflowId) => _pendingWorkflows.Any(w => w.Def.WorkflowId == workflowId);

    internal void AddPending(NewJob job, DateTimeOffset now)
    {
        ThrowIfCompleted();
        _pending.Add((job, now));
    }

    internal void AddPendingWorkflow(WorkflowDefinition def, DateTimeOffset now)
    {
        ThrowIfCompleted();
        _pendingWorkflows.Add((def, now));
    }

    internal void AddPendingReschedule(PendingReschedule reschedule)
    {
        ThrowIfCompleted();
        _pendingReschedules.Add(reschedule);
    }

    /// <inheritdoc/>
    public override void Commit()
    {
        ThrowIfCompleted();
        Store.CommitTransaction(_pending, _pendingWorkflows, _pendingReschedules, PendingJobs);
        _completed = true;
        _pending.Clear();
        _pendingWorkflows.Clear();
        _pendingReschedules.Clear();
    }

    /// <inheritdoc/>
    public override void Rollback()
    {
        ThrowIfCompleted();
        _completed = true;
        _pending.Clear();
        _pendingWorkflows.Clear();
        _pendingReschedules.Clear();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed)
        {
            Rollback();
        }
        base.Dispose(disposing);
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException("This transaction has already been committed or rolled back.");
        }
    }
}

/// <summary>
/// One reschedule buffered in an <see cref="InMemoryTransaction"/> until it commits. <paramref name="Seen"/>
/// is the committed job as the call saw it, so the commit can tell whether it changed in between; null for
/// a job enqueued earlier in the same transaction.
/// </summary>
internal readonly record struct PendingReschedule(
    Guid JobId, DateTimeOffset DueTime, string Actor, DateTimeOffset Now, JobRecord? Seen);
