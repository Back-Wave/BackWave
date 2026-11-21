namespace BackWave.Benchmarks.Workload;

/// <summary>
/// Counts handler executions for the in-process targets (BackWave, Hangfire), so a run can prove each job
/// ran exactly once: a count above the job count is a duplicate execution, a count below is lost work.
/// </summary>
internal static class BenchExecutions
{
    private static int _count;

    /// <summary>Executions since the last <see cref="Reset"/>.</summary>
    public static int Count => Volatile.Read(ref _count);

    /// <summary>Records one handler execution.</summary>
    public static void Record() => Interlocked.Increment(ref _count);

    /// <summary>Zeroes the count before a run.</summary>
    public static void Reset() => Volatile.Write(ref _count, 0);
}
