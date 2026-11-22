namespace BackWave.Benchmarks.Targets;

/// <summary>What Acta's own tables say about the bench jobs of one run.</summary>
/// <param name="Jobs">Rows of the bench definition in <c>jobs</c>.</param>
/// <param name="Succeeded">Of those, rows whose <c>jobs_state.job_status_code</c> is Succeeded (100).</param>
/// <param name="SingleExecution">Of those, rows whose <c>jobs_state.execution_number</c> is 1: claimed exactly once.</param>
/// <param name="MaxExecutionNumber">The highest <c>execution_number</c> among them.</param>
/// <param name="RepeatedKeys">Harness keys of the bench jobs claimed more than once.</param>
/// <param name="OtherJobs">Rows of every other definition (the <c>sys.*</c> permanent jobs), which never count.</param>
internal sealed record ActaLedger(
    long Jobs,
    long Succeeded,
    long SingleExecution,
    long MaxExecutionNumber,
    IReadOnlyList<string> RepeatedKeys,
    long OtherJobs)
{
    /// <summary>One log line with every figure.</summary>
    public string Describe()
        => $"acta ledger: bench-jobs={Jobs} succeeded={Succeeded} single-execution={SingleExecution} " +
           $"max-execution-number={MaxExecutionNumber} other-jobs={OtherJobs}";
}

/// <summary>Decides whether one Acta run did exactly the work it was given.</summary>
internal static class ActaWorkCheck
{
    /// <summary>
    /// Every way the run differs from exactly-once work: each enqueued key completed by exactly one handler run, and
    /// the ledger holding exactly the enqueued jobs of the bench definition, all Succeeded and each claimed once.
    /// Empty when the run passes.
    /// </summary>
    public static IReadOnlyList<string> Problems(int enqueued, int distinctCompleted, int executions, ActaLedger ledger)
    {
        var problems = new List<string>();
        if (distinctCompleted != enqueued || executions != enqueued)
        {
            problems.Add($"{enqueued} enqueues completed {distinctCompleted} distinct jobs over {executions} handler runs " +
                         $"({executions - distinctCompleted} duplicate runs)");
        }

        if (ledger.Jobs != enqueued)
        {
            problems.Add($"ledger holds {ledger.Jobs} bench jobs for {enqueued} enqueues");
        }

        if (ledger.Succeeded != ledger.Jobs)
        {
            problems.Add($"{ledger.Jobs - ledger.Succeeded} bench jobs not Succeeded");
        }

        if (ledger.SingleExecution != ledger.Jobs)
        {
            problems.Add($"{ledger.Jobs - ledger.SingleExecution} bench jobs claimed more than once " +
                         $"(max execution_number {ledger.MaxExecutionNumber}; keys {string.Join(",", ledger.RepeatedKeys.Take(20))})");
        }

        return problems;
    }
}
