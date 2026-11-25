using BackWave.Core;
using BackWave.Driver;
using BackWave.Storage;

namespace BackWave.Tests;

/// <summary>
/// Core-side outcome coalescing (ADR 0035): the Driver buffers terminal outcomes and flushes them as one
/// <see cref="Command.ReportOutcomeBatch"/> so the pump stays single-writer. A flush fires on whichever
/// comes first — the buffer reaching <c>MaxOutcomeBatch</c>, the next poll/heartbeat tick, or the node
/// going idle (<c>_executing == 0</c>, the drain-tail). The singular per-job report is gone; a lone
/// outcome flushes as a batch-of-one.
/// </summary>
public class NodeDriverOutcomeBatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static NodeDriver Driver(int maxOutcomeBatch = 32, int poolSize = int.MaxValue) => new(new NodeOptions
    {
        WorkerId = "w1",
        Policy = new DispatchPolicy.Strict(["default"]),
        MaxOutcomeBatch = maxOutcomeBatch,
        PoolSize = poolSize,
    });

    private static JobRecord Job(int attempt = 1) => new()
    {
        JobId = Guid.NewGuid(),
        WireName = "batch-test",
        Payload = "{}"u8.ToArray(),
        Queue = "default",
        State = JobState.Leased,
        DueTime = T0,
        Attempt = attempt,
    };

    private static void Claim(NodeDriver driver, params JobRecord[] jobs)
        => driver.Step(new NodeEvent.ClaimCompleted(jobs, T0));

    [Fact]
    public void ALoneOutcome_OnAnIdleNode_FlushesImmediately_AsABatchOfOne()
    {
        var driver = Driver();
        var job = Job();
        Claim(driver, job);

        var commands = driver.Step(new NodeEvent.ExecutionSucceeded(job, T0));

        var batch = Assert.IsType<Command.ReportOutcomeBatch>(Assert.Single(commands));
        var only = Assert.Single(batch.Outcomes);
        Assert.Equal(job.JobId, only.JobId);
        Assert.Equal("w1", only.WorkerId);
        Assert.Equal(job.Attempt, only.Attempt);
        Assert.IsType<JobOutcome.Success>(only.Outcome);
    }

    [Fact]
    public void OutcomesBuffer_WhileOtherJobsStillExecute_ThenFlushOnDrainTail_InCompletionOrder()
    {
        var driver = Driver();
        var a = Job();
        var b = Job();
        Claim(driver, a, b);

        // b finishes first, a still executing → buffered, no flush yet.
        Assert.Empty(driver.Step(new NodeEvent.ExecutionSucceeded(b, T0)).OfType<Command.ReportOutcomeBatch>());

        // a finishes → node idle → the whole buffer flushes in completion order (b then a).
        var commands = driver.Step(new NodeEvent.ExecutionSucceeded(a, T0.AddSeconds(1)));
        var batch = Assert.IsType<Command.ReportOutcomeBatch>(Assert.Single(commands));
        Assert.Equal([b.JobId, a.JobId], batch.Outcomes.Select(o => o.JobId).ToArray());
    }

    [Fact]
    public void TheBuffer_FlushesAtTheSizeCap_EvenWhileWorkStillExecutes()
    {
        var driver = Driver(maxOutcomeBatch: 2);
        var a = Job();
        var b = Job();
        var c = Job();
        Claim(driver, a, b, c);

        // First completion: 1 buffered, two still executing, under cap → no flush.
        Assert.Empty(driver.Step(new NodeEvent.ExecutionSucceeded(a, T0)).OfType<Command.ReportOutcomeBatch>());

        // Second completion: buffer hits the cap of 2 → flush, even though c is still executing.
        var commands = driver.Step(new NodeEvent.ExecutionSucceeded(b, T0));
        var batch = Assert.IsType<Command.ReportOutcomeBatch>(Assert.Single(commands));
        Assert.Equal([a.JobId, b.JobId], batch.Outcomes.Select(o => o.JobId).ToArray());
    }

    [Fact]
    public void APollTick_FlushesAPartialBuffer_BeforeClaiming()
    {
        var driver = Driver(poolSize: 8);
        var a = Job();
        var b = Job();
        Claim(driver, a, b);

        // a done, b still executing, under cap → buffered, no flush.
        Assert.Empty(driver.Step(new NodeEvent.ExecutionSucceeded(a, T0)).OfType<Command.ReportOutcomeBatch>());

        // The next poll flushes the partial buffer (and still does its claim pass).
        var poll = driver.Step(new NodeEvent.PollDue(T0.AddSeconds(1)));
        var batch = Assert.IsType<Command.ReportOutcomeBatch>(Assert.Single(poll.OfType<Command.ReportOutcomeBatch>()));
        Assert.Equal(a.JobId, Assert.Single(batch.Outcomes).JobId);
        Assert.Single(poll.OfType<Command.ClaimBatch>()); // claim pass still runs
    }

    [Fact]
    public void AHeartbeatTick_FlushesAPartialBuffer_AndStillHeartbeatsTheRemainingWork()
    {
        var driver = Driver();
        var a = Job();
        var b = Job();
        Claim(driver, a, b);

        Assert.Empty(driver.Step(new NodeEvent.ExecutionSucceeded(a, T0)).OfType<Command.ReportOutcomeBatch>());

        var heartbeat = driver.Step(new NodeEvent.HeartbeatDue(T0.AddSeconds(1)));
        Assert.Single(heartbeat.OfType<Command.ReportOutcomeBatch>());
        var hb = Assert.Single(heartbeat.OfType<Command.Heartbeat>());
        Assert.Equal([b.JobId], hb.JobIds.ToArray()); // only the still-executing job is renewed
    }

    [Fact]
    public void EachTerminalOutcomeKind_Buffers_AndCarriesItsPayload()
    {
        var driver = Driver();
        var failed = Job();
        Claim(driver, failed);

        var commands = driver.Step(new NodeEvent.ExecutionFailed(failed, "boom", T0));

        var batch = Assert.IsType<Command.ReportOutcomeBatch>(Assert.Single(commands));
        var failure = Assert.IsType<JobOutcome.Failure>(Assert.Single(batch.Outcomes).Outcome);
        Assert.Equal("boom", failure.Error);
    }

    private static NodeEvent.OutcomeBatchReported Applied(Command.ReportOutcomeBatch batch) => new(
        [.. batch.Outcomes.Select(row => new OutcomeReportResult(row.JobId, OutcomeResult.Applied))], T0);

    private static Command.ReportOutcomeBatch Flushed(IReadOnlyList<Command> commands)
        => Assert.IsType<Command.ReportOutcomeBatch>(Assert.Single(commands.OfType<Command.ReportOutcomeBatch>()));

    private static Guid[] Rows(Command.ReportOutcomeBatch batch) => [.. batch.Outcomes.Select(o => o.JobId)];

    [Fact]
    public void AFlushTrigger_WhileAWriteIsInFlight_WaitsForItsAnswer_ThenFlushesEverythingThatFinished()
    {
        var driver = Driver();
        var a = Job();
        var b = Job();
        Claim(driver, a);
        var first = Flushed(driver.Step(new NodeEvent.ExecutionSucceeded(a, T0)));

        Claim(driver, b);
        Assert.Empty(driver.Step(new NodeEvent.ExecutionSucceeded(b, T0)));
        Assert.DoesNotContain(driver.Step(new NodeEvent.PollDue(T0)), command => command is Command.ReportOutcomeBatch);
        Assert.DoesNotContain(driver.Step(new NodeEvent.HeartbeatDue(T0)), command => command is Command.ReportOutcomeBatch);

        var answered = driver.Step(Applied(first));
        Assert.Single(answered.OfType<Command.RequestPoll>());
        Assert.Equal([b.JobId], Rows(Flushed(answered)));
    }

    [Fact]
    public void AnAnswer_WithNoTriggerWaiting_LeavesTheBufferForTheNextTrigger()
    {
        var driver = Driver();
        var a = Job();
        var b = Job();
        var c = Job();
        Claim(driver, a, b, c);
        driver.Step(new NodeEvent.ExecutionSucceeded(a, T0));
        var first = Flushed(driver.Step(new NodeEvent.PollDue(T0)));

        Assert.Empty(driver.Step(new NodeEvent.ExecutionSucceeded(b, T0)));
        Assert.DoesNotContain(driver.Step(Applied(first)), command => command is Command.ReportOutcomeBatch);
        Assert.Equal(1, driver.BufferedOutcomeCount);
    }

    [Fact]
    public void AnEmptyAnswer_FreesTheWrite_WithoutStartingAnother()
    {
        var driver = Driver();
        var a = Job();
        var b = Job();
        Claim(driver, a);
        Flushed(driver.Step(new NodeEvent.ExecutionSucceeded(a, T0)));
        Claim(driver, b);
        Assert.Empty(driver.Step(new NodeEvent.ExecutionSucceeded(b, T0)));

        Assert.Empty(driver.Step(new NodeEvent.OutcomeBatchFaulted(T0)));
        Assert.Equal(1, driver.BufferedOutcomeCount);

        Assert.Equal([b.JobId], Rows(Flushed(driver.Step(new NodeEvent.PollDue(T0.AddSeconds(1))))));
    }

    [Fact]
    public void RowsInFlight_KeepTheirPoolSlots_UntilTheirAnswerLands()
    {
        var driver = Driver(maxOutcomeBatch: 2, poolSize: 4);
        var jobs = Enumerable.Range(0, 4).Select(_ => Job()).ToArray();
        Claim(driver, jobs);
        driver.Step(new NodeEvent.ExecutionSucceeded(jobs[0], T0));
        var write = Flushed(driver.Step(new NodeEvent.ExecutionSucceeded(jobs[1], T0)));

        Assert.DoesNotContain(driver.Step(new NodeEvent.PollDue(T0)), command => command is Command.ClaimBatch);

        driver.Step(Applied(write));
        var claim = Assert.Single(driver.Step(new NodeEvent.PollDue(T0)).OfType<Command.ClaimBatch>());
        Assert.Equal(2, claim.MaxJobs);
    }

    [Fact]
    public void ABufferThatOutgrowsTheBatchCap_WhileAWriteIsInFlight_FlushesInCappedBatches()
    {
        var driver = Driver(maxOutcomeBatch: 2);
        var jobs = Enumerable.Range(0, 5).Select(_ => Job()).ToArray();
        Claim(driver, jobs);
        driver.Step(new NodeEvent.ExecutionSucceeded(jobs[0], T0));
        var first = Flushed(driver.Step(new NodeEvent.ExecutionSucceeded(jobs[1], T0)));
        foreach (var job in jobs[2..])
        {
            Assert.Empty(driver.Step(new NodeEvent.ExecutionSucceeded(job, T0)));
        }

        var second = Flushed(driver.Step(Applied(first)));
        Assert.Equal([jobs[2].JobId, jobs[3].JobId], Rows(second));

        Assert.Equal([jobs[4].JobId], Rows(Flushed(driver.Step(Applied(second)))));
    }

    [Fact]
    public void TheStopDrain_TakesCappedBatches_EvenWithAWriteInFlight()
    {
        var driver = Driver(maxOutcomeBatch: 2);
        var jobs = Enumerable.Range(0, 5).Select(_ => Job()).ToArray();
        Claim(driver, jobs);
        driver.Step(new NodeEvent.ExecutionSucceeded(jobs[0], T0));
        Flushed(driver.Step(new NodeEvent.ExecutionSucceeded(jobs[1], T0)));
        foreach (var job in jobs[2..])
        {
            driver.Step(new NodeEvent.ExecutionSucceeded(job, T0));
        }

        Assert.Equal([jobs[2].JobId, jobs[3].JobId], Rows(driver.DrainBufferedOutcomes()!));
        Assert.Equal([jobs[4].JobId], Rows(driver.DrainBufferedOutcomes()!));
        Assert.Null(driver.DrainBufferedOutcomes());
    }
}
