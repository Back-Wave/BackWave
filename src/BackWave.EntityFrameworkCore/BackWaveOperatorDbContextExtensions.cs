using BackWave.Operations;
using BackWave.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BackWave.EntityFrameworkCore;

/// <summary>
/// Entity Framework Core extensions for <see cref="BackWaveOperator"/>. Lets you move a waiting job's
/// due time inside the same database transaction as your own writes, using your existing
/// <see cref="DbContext"/> as the unit of work - so the new due time exists only if your business
/// write commits.
/// </summary>
public static class BackWaveOperatorDbContextExtensions
{
    /// <summary>
    /// Moves a waiting job's due time inside <paramref name="unitOfWork"/>'s current transaction: the
    /// business write and the new due time commit or roll back together. A job that is scheduled, or
    /// waiting on its parents, keeps its id, payload, queue, and attempt count. A running, finished,
    /// or unknown job is rejected unchanged, so a rejection means it is too late to move the job. The
    /// action is recorded against the acting operator.
    /// </summary>
    /// <remarks>
    /// Requires an explicitly opened transaction (<c>context.Database.BeginTransaction()</c> or its
    /// async form). EF's implicit <c>SaveChanges</c> transaction is not visible here, so without an
    /// explicit one the move cannot be written atomically with your data - this throws rather than
    /// silently falling back to a plain, non-transactional reschedule. For a deliberate
    /// non-transactional reschedule, call <see cref="BackWaveOperator.RescheduleAsync"/> directly
    /// (no <see cref="DbContext"/>). The storage adapter must support transactional writes.
    /// <para>
    /// Keep the transaction short. The job row stays locked until you commit. When the job waits on a
    /// parent, the parent's outcome report waits for your commit, because the report locks the child
    /// rows to release them. On SQLite, your transaction holds the database write lock, so every other
    /// write waits for your commit. Under REPEATABLE READ, SERIALIZABLE, or SNAPSHOT isolation, your
    /// transaction can get a serialization error from the database instead of
    /// <see cref="RescheduleResult.NotReschedulable"/>.
    /// </para>
    /// </remarks>
    /// <param name="backWaveOperator">The BackWave operator to reschedule through.</param>
    /// <param name="jobId">The id of the job to reschedule.</param>
    /// <param name="dueTime">The job's new due time. A time at or before now makes the job run as soon as a worker is free.</param>
    /// <param name="actor">The acting operator's identity, recorded in the audit log.</param>
    /// <param name="unitOfWork">
    /// The <see cref="DbContext"/> whose open transaction the move joins. Begin a transaction on it
    /// first; the new due time and the audit record are written on that transaction and share its
    /// commit or rollback.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the job was rescheduled, or was not in a state whose due time can move.</returns>
    /// <exception cref="InvalidOperationException">No transaction is open on <paramref name="unitOfWork"/>.</exception>
    /// <exception cref="NotSupportedException">The storage adapter does not support transactional writes.</exception>
    /// <exception cref="ArgumentException">The transaction on <paramref name="unitOfWork"/> is not a transaction of the storage adapter's database provider.</exception>
    /// <example>
    /// <code>
    /// await using var tx = await context.Database.BeginTransactionAsync();
    /// campaign.SendAt = newSendAt;
    /// await context.SaveChangesAsync();
    /// var result = await backWaveOperator.RescheduleAsync(campaign.SendJobId, newSendAt, userId, context);
    /// if (result == RescheduleResult.NotReschedulable)
    /// {
    ///     await tx.RollbackAsync(); // the campaign is already sending or sent
    ///     return;
    /// }
    /// await tx.CommitAsync(); // the campaign date and the job's due time commit together
    /// </code>
    /// </example>
    public static ValueTask<RescheduleResult> RescheduleAsync(
        this BackWaveOperator backWaveOperator,
        Guid jobId,
        DateTimeOffset dueTime,
        string actor,
        DbContext unitOfWork,
        CancellationToken cancellationToken = default)
    {
        var transaction = unitOfWork.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "A transactional reschedule requires an open transaction on the DbContext, but none is active. " +
                "EF's implicit SaveChanges transaction is not ambient - begin one explicitly with " +
                "context.Database.BeginTransaction() (or BeginTransactionAsync()) so the business write and " +
                "the new due time commit or roll back together. For a deliberate non-transactional reschedule, " +
                "call backWaveOperator.RescheduleAsync(jobId, dueTime, actor) directly instead of this overload.");

        return backWaveOperator.RescheduleAsync(
            jobId, dueTime, actor, transaction.GetDbTransaction(), cancellationToken: cancellationToken);
    }
}
