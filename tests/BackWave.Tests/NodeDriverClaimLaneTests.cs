using BackWave.Core;
using BackWave.Diagnostics;
using BackWave.Driver;
using BackWave.Storage;

namespace BackWave.Tests;

public class NodeDriverClaimLaneTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static NodeDriver Driver(int poolSize = 100, DispatchPolicy? policy = null) => new(new NodeOptions
    {
        WorkerId = "w1",
        Policy = policy ?? new DispatchPolicy.Strict(["default"]),
        MaxClaimBatch = 10,
        PoolSize = poolSize,
    });

    private static JobRecord[] Jobs(int count) => [.. Enumerable.Range(0, count).Select(_ => new JobRecord
    {
        JobId = Guid.NewGuid(),
        WireName = "claim-lane-test",
        Payload = "{}"u8.ToArray(),
        Queue = "default",
        State = JobState.Leased,
        DueTime = T0,
        Attempt = 1,
    })];

    private static Command.ClaimBatch[] Claims(IReadOnlyList<Command> commands) => [.. commands.OfType<Command.ClaimBatch>()];

    private static Command.ClaimBatch[] Completed(NodeDriver driver, int jobs)
        => Claims(driver.Step(new NodeEvent.ClaimCompleted(Jobs(jobs), T0)));

    [Fact]
    public void APoll_WhileAClaimIsOut_WaitsForIt_AndClaimsEvenWhenItComesBackShort()
    {
        var driver = Driver();
        Assert.Single(Claims(driver.Step(new NodeEvent.PollDue(T0))));

        Assert.Empty(Claims(driver.Step(new NodeEvent.PollDue(T0))));

        var waiting = Assert.Single(Completed(driver, jobs: 3));
        Assert.Equal(10, waiting.MaxJobs);
    }

    [Fact]
    public void PollsFromBeforeAClaim_ChainPasses_OnlyWhileClaimsComeBackFull()
    {
        var driver = Driver();
        Assert.Single(Claims(driver.Step(new NodeEvent.PollDue(T0))));
        for (var poll = 0; poll < 3; poll++)
        {
            Assert.Empty(Claims(driver.Step(new NodeEvent.PollDue(T0))));
        }

        Assert.Single(Completed(driver, jobs: 10));
        Assert.Single(Completed(driver, jobs: 10));

        Assert.Empty(Completed(driver, jobs: 4));
        Assert.Single(Claims(driver.Step(new NodeEvent.PollDue(T0))));
    }

    [Fact]
    public void WaitingPolls_ThatFindNoFreeSlot_AreDropped()
    {
        var driver = Driver(poolSize: 10);
        Assert.Single(Claims(driver.Step(new NodeEvent.PollDue(T0))));
        Assert.Empty(Claims(driver.Step(new NodeEvent.PollDue(T0))));
        Assert.Empty(Claims(driver.Step(new NodeEvent.PollDue(T0))));

        var claimed = Jobs(10);
        Assert.Empty(Claims(driver.Step(new NodeEvent.ClaimCompleted(claimed, T0))));
        driver.Step(new NodeEvent.ExecutionSucceeded(claimed[0], T0));
        var write = Assert.Single(driver.Step(new NodeEvent.PollDue(T0)).OfType<Command.ReportOutcomeBatch>());
        Assert.Single(driver.Step(new NodeEvent.OutcomeBatchReported(
            [new OutcomeReportResult(write.Outcomes[0].JobId, OutcomeResult.Applied)], T0)).OfType<Command.RequestPoll>());

        var refill = Assert.Single(Claims(driver.Step(new NodeEvent.PollDue(T0))));
        Assert.Equal(1, refill.MaxJobs);
        Assert.Empty(Completed(driver, jobs: 0));
    }

    [Fact]
    public void APoll_DuringAWeightedPass_WaitsForThePassToFinish()
    {
        var driver = Driver(policy: new DispatchPolicy.Weighted([("a", 1), ("b", 1)]));
        var first = Assert.Single(Claims(driver.Step(new NodeEvent.PollDue(T0))));
        Assert.Equal("a", first.Queues[0]);
        Assert.Empty(Claims(driver.Step(new NodeEvent.PollDue(T0))));

        var second = Assert.Single(Completed(driver, jobs: first.MaxJobs));
        Assert.Equal("b", second.Queues[0]);

        Assert.Single(Completed(driver, jobs: 1));
    }

    [Fact]
    public void ACompletion_ThatReturnsMoreThanItsClaimAsked_Halts()
    {
        var driver = Driver();
        var claim = Assert.Single(Claims(driver.Step(new NodeEvent.PollDue(T0))));

        var halt = Assert.Throws<InvariantViolationException>(() => Completed(driver, jobs: claim.MaxJobs + 1));
        Assert.Equal(InvariantTrigger.ClaimBatchOverrun, halt.Trigger);
    }
}
