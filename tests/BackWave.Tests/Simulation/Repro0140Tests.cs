namespace BackWave.Tests.Simulation;

/// <summary>
/// Regression guard for vopr-0140 (PerNodeCap overshoot under operator churn). A 12h VOPR run found two
/// independent worlds where a single node briefly ran exactly one execution past its <c>PoolSize</c>, with
/// every fault axis at zero — only operator-action churn and clock skew active.
///
/// Root cause was a Simulator model artifact, not a product bug: the Driver sizes a poll's claim up front
/// (correctly reserving a slot per buffered-but-unreported outcome), then flushes the outcome buffer; the
/// flushed outcome re-polls (OutcomeReported → RequestPoll). Both production pumps ENQUEUE that re-poll on
/// their FIFO event queue, so it runs only after the outer poll's ClaimCompleted has landed its claimed jobs
/// in the executing set — no over-admission. The Simulator instead drove the re-poll INLINE (depth-first
/// recursion), so the nested poll sized its claim against a pool that did not yet reflect the outer claim and
/// double-booked the freed buffer slot. The fix defers a re-polled PollDue to the top of the Drive cascade
/// (Simulator._pendingRePolls), matching the pumps' FIFO order; production never had the bug.
/// </summary>
public class Repro0140Tests
{
    [Theory]
    [InlineData(13020077931337472759UL)] // cycle-1 world: PoolSize 4, node-1 had reached 5
    [InlineData(2349016995823808492UL)]  // cycle-2 world: PoolSize 3, node-2 had reached 4
    public void OperatorChurnWorld_HoldsThePerNodeCap(ulong seed)
    {
        var sim = new Simulator(SwarmConfig.FromSeed(seed));

        // Before the fix both seeds threw SimulationInvariantException(PerNodeCap) deterministically at a
        // fixed step/instant; the node must now stay within its pool and the run must converge.
        var result = sim.Run();

        Assert.NotNull(result);
    }
}
