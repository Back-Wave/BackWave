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

    /// <summary>
    /// With headroom the jobs of a short claim give their slots back as they finish, so a pass chained at once would
    /// claim only the slots they leave and split the pool into two cohorts. The poll waits on those jobs instead, at
    /// the cost of a Wake-Up Hint that can wait for them while slots are free. With no headroom a finish frees no
    /// slot, so the pass chains at once.
    /// </summary>
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
    public void WithHeadroom_AShortClaimWithAWaitingPoll_ChainsAPassAtOnce_WhileAFullClaimIsStillFree()
    {
        var driver = Driver(unwrittenOutcomeSlots: 4, poolSize: 20);
        Assert.Equal(10, Claim(driver.Step(new NodeEvent.PollDue(T0)))!.MaxJobs);
        driver.Step(new NodeEvent.PollDue(T0));

        var landed = driver.Step(new NodeEvent.ClaimCompleted(Jobs(3), T0));

        // PoolSize 20 - Running 3 still fills a full claim of 10.
        Assert.Equal(10, Claim(landed)?.MaxJobs);
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
    public void AFaultedWrite_ClosesTheHeadroom_UntilAWriteLands()
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

    [Fact]
    public void AWriteNeverIssued_KeepsTheHeadroomOpen()
    {
        var driver = Driver(unwrittenOutcomeSlots: 4);
        var first = Claimed(driver);
        foreach (var job in first)
        {
            Finish(driver, job);
        }
        var second = Claimed(driver);
        Finish(driver, second[0]);
        Finish(driver, second[1]);

        driver.Step(new NodeEvent.OutcomeBatchNotIssued(T0));
        var afterNotIssued = driver.Step(new NodeEvent.PollDue(T0));

        Assert.Single(afterNotIssued.OfType<Command.ReportOutcomeBatch>());
        // PoolSize 4 - Running 2 - max(0, Unwritten 2 - 4).
        Assert.Equal(2, Claim(afterNotIssued)!.MaxJobs);
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
    public void WithHeadroom_TheJobsOfAShortClaim_RepollOnce_AfterTheLastOfThemFinishes()
    {
        var driver = Driver(unwrittenOutcomeSlots: 4);
        driver.Step(new NodeEvent.PollDue(T0));
        driver.Step(new NodeEvent.ClaimCompleted(Jobs(1), T0));
        driver.Step(new NodeEvent.PollDue(T0));
        driver.Step(new NodeEvent.PollDue(T0));
        var landed = Jobs(2);
        driver.Step(new NodeEvent.ClaimCompleted(landed, T0));

        Assert.DoesNotContain(Finish(driver, landed[0]), command => command is Command.RequestPoll);
        Assert.Contains(Finish(driver, landed[1]), command => command is Command.RequestPoll);
        Assert.DoesNotContain(driver.Step(new NodeEvent.HeartbeatDue(T0)), command => command is Command.RequestPoll);
    }

    [Fact]
    public void WithHeadroom_AClaimPassThatStartsFirst_PaysThePollThatWaitsOnAShortClaimsJobs()
    {
        var driver = Driver(unwrittenOutcomeSlots: 4);
        driver.Step(new NodeEvent.PollDue(T0));
        driver.Step(new NodeEvent.ClaimCompleted(Jobs(1), T0));
        driver.Step(new NodeEvent.PollDue(T0));
        driver.Step(new NodeEvent.PollDue(T0));
        var landed = Jobs(2);
        Assert.Null(Claim(driver.Step(new NodeEvent.ClaimCompleted(landed, T0))));

        Assert.Equal(1, Claim(driver.Step(new NodeEvent.PollDue(T0)))!.MaxJobs);
        driver.Step(new NodeEvent.ClaimCompleted([], T0));

        Assert.DoesNotContain(
            landed.SelectMany(job => Finish(driver, job)), command => command is Command.RequestPoll);
    }

    /// <summary>
    /// The production pump re-throws a faulted write's store fault before it runs the commands that event yields,
    /// so a re-poll the fault yielded would be lost there and run in the simulator. The poll stays owed instead.
    /// </summary>
    [Fact]
    public void AFaultedWrite_NeverPaysAPollThatWaitsOnJobs_TheNextEventDoes()
    {
        var driver = Driver(unwrittenOutcomeSlots: 1, poolSize: 2, maxOutcomeBatch: 2);
        foreach (var job in Claimed(driver))
        {
            Finish(driver, job);
        }
        Assert.Equal(1, Claim(driver.Step(new NodeEvent.PollDue(T0)))!.MaxJobs);
        driver.Step(new NodeEvent.PollDue(T0));
        var last = Jobs(1);
        Assert.Null(Claim(driver.Step(new NodeEvent.ClaimCompleted(last, T0))));
        Assert.DoesNotContain(Finish(driver, last[0]), command => command is Command.RequestPoll);

        // Dropping the 2 rows in flight frees the slot the second of them held past the headroom of 1.
        Assert.Empty(driver.Step(new NodeEvent.OutcomeBatchFaulted(T0)));

        Assert.Contains(driver.Step(new NodeEvent.HeartbeatDue(T0)), command => command is Command.RequestPoll);
    }

    /// <summary>
    /// A faulted write closes the headroom, so a short claim's jobs hold their slots as they finish and the rule
    /// that waits on them does not apply. With the pool full when the claim lands, the waiting poll is dropped as
    /// with no headroom: a slot those jobs free later pays nothing.
    /// </summary>
    [Fact]
    public void AShortClaimWithAWaitingPoll_WhileAFaultClosesTheHeadroom_DoesNotWaitOnItsJobs()
    {
        var driver = Driver(unwrittenOutcomeSlots: 4, maxOutcomeBatch: 2);
        var first = Claimed(driver);
        Assert.Single(Finish(driver, first[0]).Concat(Finish(driver, first[1])).OfType<Command.ReportOutcomeBatch>());
        Finish(driver, first[2]);
        Assert.Equal(3, Claim(driver.Step(new NodeEvent.PollDue(T0)))!.MaxJobs);
        driver.Step(new NodeEvent.PollDue(T0));
        driver.Step(new NodeEvent.OutcomeBatchFaulted(T0));

        // The fault closed the headroom: PoolSize 4 - Running 3 - Unwritten 1 leaves no slot.
        var shortClaim = Jobs(2);
        Assert.Null(Claim(driver.Step(new NodeEvent.ClaimCompleted(shortClaim, T0))));

        var lost = driver.Step(new NodeEvent.HeartbeatCompleted(
            [.. shortClaim.Select(job => new HeartbeatResult(job.JobId, Renewed: false, CancelRequested: false))], T0));

        Assert.Equal(2, lost.OfType<Command.AbandonExecution>().Count());
        Assert.DoesNotContain(lost, command => command is Command.RequestPoll);
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
    public void AWeightedPass_CutToTheFreeSlots_LeavesAWaitingPollWaitingOnTheJobsOfItsLastBatch()
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
        Assert.Equal(2, nextPass.MaxJobs);
        driver.Step(new NodeEvent.PollDue(T0));
        driver.Step(new NodeEvent.OutcomeBatchFaulted(T0));
        var firstBatchJob = Assert.Single(Jobs(1));
        var cut = Assert.IsType<Command.ClaimBatch>(Claim(driver.Step(new NodeEvent.ClaimCompleted([firstBatchJob], T0))));

        // The fault closed the headroom: PoolSize 4 - Running 1 - Unwritten 2 cuts the batch of 2 to 1.
        Assert.Equal(1, cut.MaxJobs);

        var retry = Assert.Single(driver.Step(new NodeEvent.HeartbeatDue(T0)).OfType<Command.ReportOutcomeBatch>());
        driver.Step(new NodeEvent.OutcomeBatchReported(Applied(retry), T0));
        var lastBatchJob = Assert.Single(Jobs(1));
        Assert.Null(Claim(driver.Step(new NodeEvent.ClaimCompleted([lastBatchJob], T0))));

        // The first batch's job still runs, so no full claim is free and the finish alone would not re-poll.
        Assert.Contains(Finish(driver, lastBatchJob), command => command is Command.RequestPoll);
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
