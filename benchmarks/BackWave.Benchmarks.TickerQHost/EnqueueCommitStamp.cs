using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TickerQ.Utilities.Entities;

namespace BackWave.Benchmarks.TickerQHost;

/// <summary>Pairs each saved ticker id with the instant its enqueue call started.</summary>
internal sealed class EnqueueCommitStamp(ConcurrentDictionary<Guid, DateTimeOffset> committedAt) : SaveChangesInterceptor
{
    /// <summary>The instant the current flow's enqueue call started; null outside an enqueue.</summary>
    public static readonly AsyncLocal<DateTimeOffset?> CallStart = new();

    /// <inheritdoc/>
    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        var stamp = CallStart.Value ?? DateTimeOffset.UtcNow;
        foreach (var entry in eventData.Context!.ChangeTracker.Entries<TimeTickerEntity>())
        {
            committedAt.TryAdd(entry.Entity.Id, stamp);
        }

        return ValueTask.FromResult(result);
    }
}
