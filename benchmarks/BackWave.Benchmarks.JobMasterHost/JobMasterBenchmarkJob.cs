using JobMaster.Abstractions;
using JobMaster.Abstractions.Models;
using JobMaster.Abstractions.Models.Attributes;

namespace BackWave.Benchmarks.JobMasterHost;

/// <summary>
/// The JobMaster handler for the benchmark job: it mirrors BackWave's <c>bench</c> handler (sleep for the
/// configured delay, or return at once for noop). Every execution first awaits <see cref="Gate"/>, which
/// stays closed during preload so that jobs JobMaster already pulled into memory cannot run before the timed
/// window opens; <c>execute</c> opens it.
/// </summary>
[JobMasterDefinitionId("bench")]
internal sealed class JobMasterBenchmarkJob : IJobMasterHandler
{
    /// <summary>Message key carrying the payload string.</summary>
    public const string PayloadKey = "payload";

    /// <summary>Message key carrying the handler delay in milliseconds.</summary>
    public const string DelayKey = "delayMs";

    private static int _executions;

    /// <summary>Handler executions in this process; the host is fresh per run, so this is the run's count.</summary>
    public static int Executions => Volatile.Read(ref _executions);

    /// <summary>Completed when the timed window opens; every handler execution awaits it first.</summary>
    public static readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public async Task HandleAsync(JobContext job)
    {
        await Gate.Task.ConfigureAwait(false);
        Interlocked.Increment(ref _executions);
        var delayMs = job.MsgData.TryGetIntValue(DelayKey) ?? 0;
        if (delayMs > 0)
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
        }
    }
}
