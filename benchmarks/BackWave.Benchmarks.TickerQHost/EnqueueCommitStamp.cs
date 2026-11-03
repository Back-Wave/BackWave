using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TickerQ.Utilities.Entities;

namespace BackWave.Benchmarks.TickerQHost;

/// <summary>
/// Records the instant each ticker's INSERT committed, the enqueue side of end-to-end latency. A run-now
/// AddAsync on a processing instance goes on to acquire and dispatch the ticker before it returns, so a stamp
/// taken after AddAsync can land after the job already ran.
/// </summary>
internal sealed class EnqueueCommitStamp(ConcurrentDictionary<Guid, DateTimeOffset> committedAt) : SaveChangesInterceptor
{
    /// <inheritdoc/>
    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in eventData.Context!.ChangeTracker.Entries<TimeTickerEntity>())
        {
            committedAt.TryAdd(entry.Entity.Id, now);
        }

        return ValueTask.FromResult(result);
    }
}
