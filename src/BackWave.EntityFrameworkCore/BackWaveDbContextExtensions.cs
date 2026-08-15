using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BackWave.EntityFrameworkCore;

/// <summary>
/// Entity Framework Core extensions for <see cref="BackWaveClient"/>. Lets you enqueue a job inside
/// the same database transaction as your own writes, using your existing <see cref="DbContext"/> as
/// the unit of work — so the job exists only if your business write commits, with no outbox table.
/// </summary>
public static class BackWaveDbContextExtensions
{
    /// <summary>
    /// Enqueues a job inside <paramref name="unitOfWork"/>'s current transaction: the business write
    /// and the job commit or roll back together, with no outbox table. One line in an existing unit
    /// of work; the transaction is read from the <see cref="DbContext"/>, never passed explicitly.
    /// </summary>
    /// <remarks>
    /// Requires an explicitly opened transaction (<c>context.Database.BeginTransaction()</c> or its
    /// async form). EF's implicit <c>SaveChanges</c> transaction is not visible here, so without an
    /// explicit one the job cannot be written atomically with your data — this throws rather than
    /// silently falling back to a plain, non-transactional enqueue. For a deliberate non-transactional
    /// enqueue, call <see cref="BackWaveClient.EnqueueAsync{TJob}"/> directly (no <see cref="DbContext"/>).
    /// The storage adapter must support transactional enqueue.
    /// </remarks>
    /// <typeparam name="TJob">The registered job payload type. Its registration controls serialization and the default Queue.</typeparam>
    /// <param name="client">The BackWave client to enqueue through.</param>
    /// <param name="job">The job payload to enqueue. Serialized through its registration.</param>
    /// <param name="unitOfWork">
    /// The <see cref="DbContext"/> whose open transaction the job joins. Begin a transaction on it
    /// first; the job is written on that transaction and shares its commit or rollback.
    /// </param>
    /// <param name="dueTime">When the job becomes eligible to run. A future time defers it; the current instant runs it as soon as a worker is free.</param>
    /// <param name="queue">The Queue to enqueue into. Null uses the job type's registered Queue.</param>
    /// <param name="cancellationToken">Cancels the enqueue request.</param>
    /// <returns>The new job's id, for later tracking or for use as a dependency parent.</returns>
    /// <exception cref="InvalidOperationException">
    /// No transaction is open on <paramref name="unitOfWork"/>, or the store rejected the enqueue
    /// for another reason (for example, a duplicate job id).
    /// </exception>
    /// <exception cref="NotSupportedException">The storage adapter does not support transactional enqueue.</exception>
    /// <exception cref="ArgumentException">The serialized payload exceeds the store's maximum payload size; store a reference (an id or blob key) instead of the data itself.</exception>
    /// <example>
    /// <code>
    /// await using var tx = await context.Database.BeginTransactionAsync();
    /// context.Orders.Add(order);
    /// await context.SaveChangesAsync();
    /// var jobId = await client.EnqueueAsync(new SendReceipt(order.Id), context, DateTimeOffset.UtcNow);
    /// await tx.CommitAsync(); // the order and the job commit together
    /// </code>
    /// </example>
    public static ValueTask<Guid> EnqueueAsync<TJob>(
        this BackWaveClient client,
        TJob job,
        DbContext unitOfWork,
        DateTimeOffset dueTime,
        string? queue = null,
        CancellationToken cancellationToken = default)
        where TJob : notnull
    {
        var transaction = unitOfWork.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "Transactional Enqueue requires an open transaction on the DbContext, but none is active. " +
                "EF's implicit SaveChanges transaction is not ambient — begin one explicitly with " +
                "context.Database.BeginTransaction() (or BeginTransactionAsync()) so the business write and " +
                "the job commit or roll back together. For a deliberate non-transactional enqueue, call " +
                "client.EnqueueAsync(job, dueTime) directly instead of this overload.");

        return client.EnqueueAsync(
            job, dueTime, queue, transaction: transaction.GetDbTransaction(), cancellationToken: cancellationToken);
    }
}
