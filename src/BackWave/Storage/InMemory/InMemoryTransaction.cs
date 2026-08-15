using System.Data;
using System.Data.Common;

namespace BackWave.Storage.InMemory;

/// <summary>
/// The in-memory store's transactional-enqueue scope. Enqueues buffer here and become visible
/// atomically on <see cref="Commit"/>; rolling back — explicitly or by disposing without committing —
/// means the jobs never existed: never claimable, never visible to reads, no trace.
/// </summary>
public sealed class InMemoryTransaction : DbTransaction
{
    private readonly List<(NewJob Job, DateTimeOffset Now)> _pending = [];
    private readonly List<(WorkflowDefinition Def, DateTimeOffset Now)> _pendingWorkflows = [];
    private bool _completed;

    internal InMemoryTransaction(InMemoryJobStore store) => Store = store;

    internal InMemoryJobStore Store { get; }

    /// <inheritdoc/>
    public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;

    /// <inheritdoc/>
    protected override DbConnection? DbConnection => null;

    internal bool HasPending(Guid jobId)
        => _pending.Any(p => p.Job.JobId == jobId)
            || _pendingWorkflows.Any(w => w.Def.Members.Any(m => m.JobId == jobId));

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

    /// <inheritdoc/>
    public override void Commit()
    {
        ThrowIfCompleted();
        Store.CommitTransaction(_pending, _pendingWorkflows);
        _completed = true;
        _pending.Clear();
        _pendingWorkflows.Clear();
    }

    /// <inheritdoc/>
    public override void Rollback()
    {
        ThrowIfCompleted();
        _completed = true;
        _pending.Clear();
        _pendingWorkflows.Clear();
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
