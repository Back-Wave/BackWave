using BackWave.Benchmarks.Targets;
using BackWave.Benchmarks.Workload;

namespace BackWave.Benchmarks;

/// <summary>
/// Checks that a run measured the work it claims: every job succeeded and ran exactly once, and every
/// completion landed inside the timed window. A failed audit does not stop the run; it is printed and
/// recorded, so a cell with lost, duplicated, or out-of-window work is visible next to its number.
/// </summary>
/// <param name="JobCount">Jobs in the stream.</param>
/// <param name="Succeeded">Jobs with a successful completion (the end-to-end sample count).</param>
/// <param name="EnqueueCalls">Enqueue calls timed.</param>
/// <param name="Executions">Handler executions the target counted; null when it cannot count them.</param>
/// <param name="FirstCompletionAfterOpenMs">Window open to the first completion; negative means work ran before the window.</param>
/// <param name="LastCompletionBeforeCloseMs">Last completion to window close; negative means work ran after the window.</param>
/// <param name="ProducerSeconds">First to last enqueue of the producers.</param>
/// <param name="ArrivalJobsPerSecond">Jobs over <paramref name="ProducerSeconds"/>: the rate the producers delivered.</param>
/// <param name="Problems">Every check that failed; empty when the audit passed.</param>
public sealed record WorkAudit(
    int JobCount,
    int Succeeded,
    int EnqueueCalls,
    int? Executions,
    double? FirstCompletionAfterOpenMs,
    double? LastCompletionBeforeCloseMs,
    double? ProducerSeconds,
    double? ArrivalJobsPerSecond,
    IReadOnlyList<string> Problems)
{
    /// <summary>True when every check passed.</summary>
    public bool Passed => Problems.Count == 0;

    /// <summary>Builds the audit for one run from the target's samples and the window instants.</summary>
    public static WorkAudit Check(WorkloadSpec spec, TargetSamples samples, DateTimeOffset windowOpen, DateTimeOffset windowClose)
    {
        var work = samples.Work;
        var problems = new List<string>();
        var succeeded = samples.EndToEndLatencies.Count;
        var enqueueCalls = samples.EnqueueLatencies.Count;
        if (succeeded != spec.JobCount)
        {
            problems.Add($"succeeded {succeeded} of {spec.JobCount}");
        }

        if (enqueueCalls != spec.JobCount)
        {
            problems.Add($"enqueue calls {enqueueCalls} of {spec.JobCount}");
        }

        if (work.Executions is { } executions && executions != spec.JobCount)
        {
            problems.Add($"executions {executions} for {spec.JobCount} jobs");
        }

        double? afterOpen = work.FirstCompletion is { } first ? (first - windowOpen).TotalMilliseconds : null;
        double? beforeClose = work.LastCompletion is { } last ? (windowClose - last).TotalMilliseconds : null;
        if (spec.Arrival == ArrivalMode.Drain && afterOpen < 0)
        {
            problems.Add($"first completion {-afterOpen:0.0} ms before the window opened");
        }

        if (beforeClose < 0)
        {
            problems.Add($"last completion {-beforeClose:0.0} ms after the window closed");
        }

        if (work.FirstCompletion is null || work.LastCompletion is null)
        {
            problems.Add("no completion instants reported");
        }

        var producerSeconds = work.ProducerElapsed?.TotalSeconds;
        double? arrival = producerSeconds > 0 ? spec.JobCount / producerSeconds : null;
        return new WorkAudit(spec.JobCount, succeeded, enqueueCalls, work.Executions, afterOpen, beforeClose,
            producerSeconds, arrival, problems);
    }

    /// <summary>One log line with every audited figure.</summary>
    public string Describe()
        => $"work audit {(Passed ? "PASS" : "FAIL: " + string.Join("; ", Problems))}  jobs={JobCount}  " +
           $"succeeded={Succeeded}  enqueue-calls={EnqueueCalls}  executions={Executions?.ToString() ?? "n/a"}  " +
           $"first-completion-after-open={FirstCompletionAfterOpenMs:0.0}ms  " +
           $"last-completion-before-close={LastCompletionBeforeCloseMs:0.0}ms  " +
           $"producer-phase={ProducerSeconds:0.000}s  arrival={ArrivalJobsPerSecond:0}j/s";
}
