using BackWave.Core;
using BackWave.Driver;
using BackWave.Storage;

namespace BackWave.Tests;

public class NodeDriverUnwrittenOutcomeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static NodeDriver Driver(int unwrittenOutcomeSlots, int poolSize = 4, int maxOutcomeBatch = 10) => new(new NodeOptions
    {
        WorkerId = "w1",
        Policy = new DispatchPolicy.Strict(["default"]),
        MaxClaimBatch = 10,
        MaxOutcomeBatch = maxOutcomeBatch,
        PoolSize = poolSize,
        UnwrittenOutcomeSlots = unwrittenOutcomeSlots,
    });

    private static JobRecord[] Jobs(int count) => [.. Enumerable.Range(0, count).Select(_ => new JobRecord
    {
        JobId = Guid.NewGuid(),
        WireName = "unwritten-outcome-test",
        Payload = "{}"u8.ToArray(),
        Queue = "default",
        State = JobState.Leased,
        DueTime = T0,
        Attempt = 1,
    })];

    private static Command.ClaimBatch? Claim(IReadOnlyList<Command> commands)
        => commands.OfType<Command.ClaimBatch>().SingleOrDefault();

    private static JobRecord[] Claimed(NodeDriver driver)
    {
        var claim = Assert.IsType<Command.ClaimBatch>(Claim(driver.Step(new NodeEvent.PollDue(T0))));
        var jobs = Jobs(claim.MaxJobs);
        driver.Step(new NodeEvent.ClaimCompleted(jobs, T0));
        return jobs;
    }

    private static IReadOnlyList<Command> Finish(NodeDriver driver, JobRecord job)
        => driver.Step(new NodeEvent.ExecutionSucceeded(job, T0));

    private static OutcomeReportResult[] Applied(Command.ReportOutcomeBatch write)
        => [.. write.Outcomes.Select(row => new OutcomeReportResult(row.JobId, OutcomeResult.Applied))];

    [Fact]
    public void WithNoHeadroom_AFinishedJobHoldsItsSlot_UntilItsWriteAnswers()
    {
        var driver = Driver(unwrittenOutcomeSlots: 0);
        var jobs = Claimed(driver);

        var finishes = jobs.Select(job => Finish(driver, job)).ToList();
        Assert.DoesNotContain(finishes.SelectMany(commands => commands), command => command is Command.RequestPoll);
        var write = Assert.Single(finishes[^1].OfType<Command.ReportOutcomeBatch>());

        Assert.Null(Claim(driver.Step(new NodeEvent.PollDue(T0))));

        driver.Step(new NodeEvent.OutcomeBatchReported(Applied(write), T0));
        Assert.Equal(4, Claim(driver.Step(new NodeEvent.PollDue(T0)))!.MaxJobs);
    }

    [Fact]
    public void WithHeadroom_TheFinishThatFreesAFullClaim_RepollsOnce_AndTheClaimRunsBesideTheWrite()
    {
        var driver = Driver(unwrittenOutcomeSlots: 4);
        var jobs = Claimed(driver);

        var finishes = jobs.Select(job => Finish(driver, job)).ToList();
        Assert.Equal(1, finishes.SelectMany(commands => commands).Count(command => command is Command.RequestPoll));
        Assert.Contains(finishes[^1], command => command is Command.RequestPoll);
        Assert.Single(finishes[^1].OfType<Command.ReportOutcomeBatch>());

        Assert.Equal(4, Claim(driver.Step(new NodeEvent.PollDue(T0)))!.MaxJobs);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(4, null)]
    public void AShortClaimWithAWaitingPoll_ChainsAPartialPass_OnlyWithNoHeadroom(int unwrittenOutcomeSlots, int? chained)
    {
        var driver = Driver(unwrittenOutcomeSlots);
        var claim = Assert.IsType<Command.ClaimBatch>(Claim(driver.Step(new NodeEvent.PollDue(T0))));
        Assert.Equal(4, claim.MaxJobs);
        driver.Step(new NodeEvent.PollDue(T0));

        var landed = driver.Step(new NodeEvent.ClaimCompleted(Jobs(2), T0));

        Assert.Equal(chained, Claim(landed)?.MaxJobs);
    }

    [Fact]
    public void WithHeadroom_TheJobsOfAShortClaim_RepollForTheWholePool_WhenTheyFinish()
    {
        var driver = Driver(unwrittenOutcomeSlots: 4);
        driver.Step(new NodeEvent.PollDue(T0));
        driver.Step(new NodeEvent.PollDue(T0));
        var jobs = Jobs(2);
        driver.Step(new NodeEvent.ClaimCompleted(jobs, T0));

        Assert.DoesNotContain(Finish(driver, jobs[0]), command => command is Command.RequestPoll);
        Assert.Contains(Finish(driver, jobs[1]), command => command is Command.RequestPoll);
        Assert.Equal(4, Claim(driver.Step(new NodeEvent.PollDue(T0)))!.MaxJobs);
    }

    [Fact]
    public void AFinish_WhenAFullClaimIsAlreadyFree_DoesNotRepoll()
    {
        var driver = Driver(unwrittenOutcomeSlots: 4, poolSize: 20);
        var claim = Assert.IsType<Command.ClaimBatch>(Claim(driver.Step(new NodeEvent.PollDue(T0))));
        var jobs = Jobs(3);
        driver.Step(new NodeEvent.ClaimCompleted(jobs, T0));
        Assert.Equal(10, claim.MaxJobs);

        Assert.DoesNotContain(
            jobs.SelectMany(job => Finish(driver, job)), command => command is Command.RequestPoll);
    }

    [Fact]
    public void WithHeadroom_AClaimCountsOnlyTheUnwrittenOutcomesBeyondIt()
    {
        var driver = Driver(unwrittenOutcomeSlots: 2);
        var jobs = Claimed(driver);

        var finishes = jobs.Select(job => Finish(driver, job)).ToList();
        Assert.DoesNotContain(finishes.SelectMany(commands => commands), command => command is Command.RequestPoll);
        Assert.Single(finishes[^1].OfType<Command.ReportOutcomeBatch>());

        // PoolSize 4 - Running 0 - (Unwritten 4 - headroom 2).
        Assert.Equal(2, Claim(driver.Step(new NodeEvent.PollDue(T0)))!.MaxJobs);
    }

    [Fact]
    public void AFaultedWrite_ClosesTheHeadroom_UntilAWriteAppliesRowsAgain()
    {
        var driver = Driver(unwrittenOutcomeSlots: 4);
        var first = Claimed(driver);
        var firstWrite = first.Select(job => Finish(driver, job)).SelectMany(commands => commands)
            .OfType<Command.ReportOutcomeBatch>().Single();
        Assert.Equal(4, firstWrite.Outcomes.Count);
        var second = Claimed(driver);
        Assert.Equal(4, second.Length);
        Finish(driver, second[0]);
        Finish(driver, second[1]);

        driver.Step(new NodeEvent.OutcomeBatchFaulted(T0));
        var afterFault = driver.Step(new NodeEvent.PollDue(T0));
        var retry = Assert.Single(afterFault.OfType<Command.ReportOutcomeBatch>());
        Assert.Null(Claim(afterFault));

        driver.Step(new NodeEvent.OutcomeBatchReported(Applied(retry), T0));
        Finish(driver, second[2]);

        // Headroom open again: PoolSize 4 - Running 1 - max(0, Unwritten 1 - 4).
        Assert.Equal(3, Claim(driver.Step(new NodeEvent.PollDue(T0)))!.MaxJobs);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, true)]
    public void AHeartbeat_RenewsTheBufferedOutcomes_OnlyWithHeadroom_AndNeverTheWriteInFlight(
        int unwrittenOutcomeSlots, bool renewsBuffered)
    {
        var driver = Driver(unwrittenOutcomeSlots, maxOutcomeBatch: 1);
        var jobs = Claimed(driver);
        var write = Assert.Single(Finish(driver, jobs[0]).OfType<Command.ReportOutcomeBatch>());
        Assert.Empty(Finish(driver, jobs[1]).OfType<Command.ReportOutcomeBatch>());

        var heartbeat = Assert.Single(driver.Step(new NodeEvent.HeartbeatDue(T0)).OfType<Command.Heartbeat>());

        Guid[] expected = renewsBuffered
            ? [jobs[2].JobId, jobs[3].JobId, jobs[1].JobId]
            : [jobs[2].JobId, jobs[3].JobId];
        Assert.Equal(expected.Order(), heartbeat.JobIds.Order());
        Assert.DoesNotContain(write.Outcomes[0].JobId, heartbeat.JobIds);
    }

    [Fact]
    public void WithHeadroom_AHintDuringAnEmptyClaim_ClaimsAtOnce_BesideALongRunningJob()
    {
        var driver = Driver(unwrittenOutcomeSlots: 4);
        Assert.Equal(4, Claim(driver.Step(new NodeEvent.PollDue(T0)))!.MaxJobs);
        driver.Step(new NodeEvent.ClaimCompleted(Jobs(1), T0));
        Assert.Equal(3, Claim(driver.Step(new NodeEvent.PollDue(T0)))!.MaxJobs);
        driver.Step(new NodeEvent.PollDue(T0));

        var landed = driver.Step(new NodeEvent.ClaimCompleted([], T0));

        Assert.Equal(3, Claim(landed)?.MaxJobs);
    }

    [Fact]
    public void WithHeadroom_TheJobsOfAShortClaim_RepollWhenTheyFinish_EvenBelowAFullClaim()
    {
        var driver = Driver(unwrittenOutcomeSlots: 4);
        driver.Step(new NodeEvent.PollDue(T0));
        driver.Step(new NodeEvent.ClaimCompleted(Jobs(1), T0));
        driver.Step(new NodeEvent.PollDue(T0));
        driver.Step(new NodeEvent.PollDue(T0));
        var shortJob = Assert.Single(Jobs(1));
        Assert.Null(Claim(driver.Step(new NodeEvent.ClaimCompleted([shortJob], T0))));

        Assert.Contains(Finish(driver, shortJob), command => command is Command.RequestPoll);
        Assert.Equal(3, Claim(driver.Step(new NodeEvent.PollDue(T0)))!.MaxJobs);
    }

    [Fact]
    public void WithHeadroom_TheJobsOfAShortClaim_RepollWhenTheirLeasesAreLost_EvenBelowAFullClaim()
    {
        var driver = Driver(unwrittenOutcomeSlots: 4);
        driver.Step(new NodeEvent.PollDue(T0));
        driver.Step(new NodeEvent.ClaimCompleted(Jobs(1), T0));
        driver.Step(new NodeEvent.PollDue(T0));
        driver.Step(new NodeEvent.PollDue(T0));
        var lostJob = Assert.Single(Jobs(1));
        driver.Step(new NodeEvent.ClaimCompleted([lostJob], T0));

        var heartbeat = driver.Step(new NodeEvent.HeartbeatCompleted([new HeartbeatResult(lostJob.JobId, Renewed: false, CancelRequested: false)], T0));

        Assert.Contains(heartbeat, command => command is Command.RequestPoll);
        Assert.Equal(3, Claim(driver.Step(new NodeEvent.PollDue(T0)))!.MaxJobs);
    }

    [Fact]
    public void AWeightedPass_ClaimsNothingIntoTheHeadroom_AfterAFaultBetweenItsBatches()
    {
        var driver = new NodeDriver(new NodeOptions
        {
            WorkerId = "w1",
            Policy = new DispatchPolicy.Weighted([("q1", 1), ("q2", 1)]),
            MaxClaimBatch = 10,
            MaxOutcomeBatch = 2,
            PoolSize = 4,
            UnwrittenOutcomeSlots = 4,
        });
        var first = Assert.IsType<Command.ClaimBatch>(Claim(driver.Step(new NodeEvent.PollDue(T0))));
        var cohort = Jobs(first.MaxJobs);
        var second = Assert.IsType<Command.ClaimBatch>(Claim(driver.Step(new NodeEvent.ClaimCompleted(cohort, T0))));
        cohort = [.. cohort, .. Jobs(second.MaxJobs)];
        Assert.Equal(4, cohort.Length);
        driver.Step(new NodeEvent.ClaimCompleted(cohort[first.MaxJobs..], T0));
        foreach (var job in cohort)
        {
            Finish(driver, job);
        }

        var nextPass = Assert.IsType<Command.ClaimBatch>(Claim(driver.Step(new NodeEvent.PollDue(T0))));
        driver.Step(new NodeEvent.OutcomeBatchFaulted(T0));
        var landed = driver.Step(new NodeEvent.ClaimCompleted(Jobs(nextPass.MaxJobs), T0));

        // The fault closed the headroom: PoolSize 4 - Running 2 - Unwritten 2 leaves no slot for the next batch.
        Assert.Null(Claim(landed));
    }

    [Fact]
    public void AHeartbeatResult_ForAJobThatNoLongerRuns_IsNotActedOn()
    {
        var driver = Driver(unwrittenOutcomeSlots: 4);
        var jobs = Claimed(driver);
        Finish(driver, jobs[0]);
        Finish(driver, jobs[1]);

        var reactions = driver.Step(new NodeEvent.HeartbeatCompleted(
        [
            new HeartbeatResult(jobs[0].JobId, Renewed: false, CancelRequested: false),
            new HeartbeatResult(jobs[1].JobId, Renewed: true, CancelRequested: true),
            new HeartbeatResult(jobs[2].JobId, Renewed: false, CancelRequested: false),
            new HeartbeatResult(jobs[3].JobId, Renewed: true, CancelRequested: true),
        ], T0));

        Assert.Equal(jobs[2].JobId, Assert.Single(reactions.OfType<Command.AbandonExecution>()).JobId);
        Assert.Equal(jobs[3].JobId, Assert.Single(reactions.OfType<Command.SignalCancellation>()).JobId);
    }
}
