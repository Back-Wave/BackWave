using System.Collections.Concurrent;

namespace BackWave.Benchmarks.Workload;

/// <summary>
/// The instant each competitor job's handler finished, keyed by the job's identity. The competitors that use it
/// delete a job's row when it completes, so the store keeps no completion timestamp to read back afterwards.
/// </summary>
public sealed class CompletionLog
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _completedAt = new();
    private int _executions;

    /// <summary>The handler-finished instant per job key.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> CompletedAt => _completedAt;

    /// <summary>How many times the handler ran, so a job run twice is not hidden by the keyed map.</summary>
    public int Executions => Volatile.Read(ref _executions);

    /// <summary>Records that the job <paramref name="key"/> finished now.</summary>
    public void Record(string key)
    {
        _completedAt[key] = DateTimeOffset.UtcNow;
        Interlocked.Increment(ref _executions);
    }

    /// <summary>Forgets every recorded completion, before the next run.</summary>
    public void Clear()
    {
        _completedAt.Clear();
        _executions = 0;
    }
}
