using BackWave.Benchmarks.Targets;

namespace BackWave.Benchmarks.Tests;

/// <summary>
/// The Acta host fails a run unless the work happened exactly once: every enqueued key completed by one handler run,
/// and Acta's own ledger holding exactly those jobs, all Succeeded and each claimed once.
/// </summary>
public sealed class ActaWorkCheckTests
{
    private static ActaLedger Clean(long jobs) => new(jobs, jobs, jobs, 1, [], OtherJobs: 2);

    [Fact]
    public void Exactly_once_work_passes_and_system_jobs_do_not_count()
    {
        Assert.Empty(ActaWorkCheck.Problems(enqueued: 100, distinctCompleted: 100, executions: 100, Clean(100)));
    }

    [Fact]
    public void A_handler_run_twice_fails_and_names_the_duplicate_count()
    {
        var problem = Assert.Single(ActaWorkCheck.Problems(100, 100, 101, Clean(100)));

        Assert.Contains("1 duplicate runs", problem);
    }

    [Fact]
    public void A_lost_job_fails_in_memory_and_in_the_ledger()
    {
        var ledger = new ActaLedger(100, 99, 99, 1, [], 2);

        var problems = ActaWorkCheck.Problems(100, 99, 99, ledger);

        Assert.Contains(problems, p => p.Contains("completed 99 distinct jobs"));
        Assert.Contains(problems, p => p.Contains("1 bench jobs not Succeeded"));
    }

    [Fact]
    public void A_job_claimed_twice_fails_and_names_its_key()
    {
        var ledger = new ActaLedger(100, 100, 99, 2, ["42"], 2);

        var problem = Assert.Single(ActaWorkCheck.Problems(100, 100, 100, ledger));

        Assert.Contains("1 bench jobs claimed more than once", problem);
        Assert.Contains("keys 42", problem);
    }

    [Fact]
    public void A_ledger_with_more_bench_jobs_than_enqueues_fails()
    {
        var problem = Assert.Single(ActaWorkCheck.Problems(100, 100, 100, Clean(101)));

        Assert.Contains("ledger holds 101 bench jobs for 100 enqueues", problem);
    }
}
