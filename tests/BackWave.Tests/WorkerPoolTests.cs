using BackWave.Driver;
using BackWave.Storage;

namespace BackWave.Tests;

/// <summary>
/// PoolSize is a hard bound on concurrent executions: the Driver subtracts in-flight work
/// from every claim, so a node can never hold more handlers than its pool — not via the
/// poll tick, not via the minted-due-now re-poll.
/// </summary>
public class WorkerPoolTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static JobRecord Job() => new()
    {
        JobId = Guid.NewGuid(),
        WireName = "pool-test",
        Payload = "{}"u8.ToArray(),
        Queue = "default",
        State = JobState.Leased,
        DueTime = T0,
        Attempt = 1,
    };

    private static NodeDriver Driver(int poolSize) => new(new NodeOptions
    {
        WorkerId = "node-1",
        Policy = new Core.DispatchPolicy.Strict(["default"]),
        MaxClaimBatch = 32,
        PoolSize = poolSize,
    });

    [Fact]
    public void Claims_NeverExceedTheFreePoolCapacity()
    {
        var driver = Driver(poolSize: 2);

        // An empty pool claims exactly the pool size, not the full claim batch.
        var poll = driver.Step(new NodeEvent.PollDue(T0));
        var claim = Assert.Single(poll.OfType<Command.ClaimBatch>());
        Assert.Equal(2, claim.MaxJobs);

        // Both slots fill: the next poll claims nothing (everything else still runs).
        var first = Job();
        driver.Step(new NodeEvent.ClaimCompleted([first, Job()], T0));
        var whileFull = driver.Step(new NodeEvent.PollDue(T0.AddSeconds(1)));
        Assert.Empty(whileFull.OfType<Command.ClaimBatch>());
        Assert.NotEmpty(whileFull.OfType<Command.ExpireLeases>());

        // One execution finishes, but its outcome is buffered (ADR 0035) and still holds its store Lease,
        // so the slot stays occupied: this poll flushes the batch yet claims nothing.
        driver.Step(new NodeEvent.ExecutionSucceeded(first, T0.AddSeconds(2)));
        var flushPoll = driver.Step(new NodeEvent.PollDue(T0.AddSeconds(2)));
        Assert.Single(flushPoll.OfType<Command.ReportOutcomeBatch>());
        Assert.Empty(flushPoll.OfType<Command.ClaimBatch>());

        // Once the outcome is flushed the slot re-opens: the flush's re-poll claims exactly one job.
        var oneFree = driver.Step(new NodeEvent.PollDue(T0.AddSeconds(3)));
        Assert.Equal(1, Assert.Single(oneFree.OfType<Command.ClaimBatch>()).MaxJobs);
    }

    [Fact]
    public void TheMintedDueNowRePoll_HasTheSameBackpressure()
    {
        var driver = Driver(poolSize: 1);
        driver.Step(new NodeEvent.PollDue(T0));
        driver.Step(new NodeEvent.ClaimCompleted([Job()], T0));

        // The Shell re-polls after MintDue minted due-now work; a full pool still claims
        // nothing — the work waits for the next free slot, on this node or another.
        var rePoll = driver.Step(new NodeEvent.PollDue(T0));
        Assert.Empty(rePoll.OfType<Command.ClaimBatch>());
    }
}
