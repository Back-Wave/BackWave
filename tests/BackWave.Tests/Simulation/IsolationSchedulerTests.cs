namespace BackWave.Tests.Simulation;

/// <summary>
/// Direct unit tests for the Isolation Scheduler (issue 0068): the deep module that draws Node
/// Isolation episodes deterministically and owns the N−1 fault budget. The scheduler is also
/// exercised indirectly through the seeded isolation regimes in <see cref="SimulatorTests"/>; here
/// we pin its two contracts — deterministic draws and "never isolate the last reachable node" — in
/// isolation so a regression surfaces as a focused failure rather than a vague run divergence.
/// </summary>
public class IsolationSchedulerTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromHours(2);
    private static readonly TimeSpan Min = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(180);

    [Theory]
    [InlineData(1UL)]
    [InlineData(1337UL)]
    [InlineData(0xDEADBEEFUL)]
    public void Plan_SameSeed_DrawsTheIdenticalEpisodes(ulong seed)
    {
        var first = new IsolationScheduler(seed, 3).Plan(20, Start, Window, Min, Max);
        var second = new IsolationScheduler(seed, 3).Plan(20, Start, Window, Min, Max);

        Assert.Equal(first, second); // records compare structurally — node, start, and duration all match
        Assert.All(first, e =>
        {
            Assert.InRange(e.Node, 0, 2);
            Assert.InRange(e.StartAt, Start, Start + Window);
            Assert.InRange(e.Duration, Min, Max);
        });
    }

    [Fact]
    public void Plan_DifferentSeeds_DrawDifferentEpisodes()
    {
        var first = new IsolationScheduler(1, 3).Plan(20, Start, Window, Min, Max);
        var second = new IsolationScheduler(2, 3).Plan(20, Start, Window, Min, Max);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Plan_ZeroCount_MakesNoDraws_AndReturnsEmpty()
    {
        // A zero count is the off switch: no episodes, and — because no draws are taken — a scheduler
        // that then plans is identical to one constructed fresh, the property the byte-identical
        // determinism battery relies on.
        var scheduler = new IsolationScheduler(7, 3);
        Assert.Empty(scheduler.Plan(0, Start, Window, Min, Max));
        Assert.Equal(
            new IsolationScheduler(7, 3).Plan(5, Start, Window, Min, Max),
            scheduler.Plan(5, Start, Window, Min, Max));
    }

    [Fact]
    public void TryBegin_NeverIsolatesTheLastReachableNode()
    {
        var scheduler = new IsolationScheduler(1, 3);

        Assert.True(scheduler.TryBegin(0));
        Assert.True(scheduler.TryBegin(1));      // two of three may be cut off at once
        Assert.False(scheduler.TryBegin(2));     // the third would leave no reachable node — refused
        Assert.Equal(2, scheduler.IsolatedCount);

        scheduler.Heal(0);                        // free a slot
        Assert.True(scheduler.TryBegin(2));       // now the budget admits another
        Assert.Equal(2, scheduler.IsolatedCount);
    }

    [Fact]
    public void TryBegin_RefusesAnAlreadyIsolatedNode()
    {
        var scheduler = new IsolationScheduler(1, 3);
        Assert.True(scheduler.TryBegin(0));
        Assert.False(scheduler.TryBegin(0)); // idempotent: no double-isolate, no budget consumed twice
        Assert.Equal(1, scheduler.IsolatedCount);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(42UL)]
    [InlineData(0x5EEDUL)]
    public void TryBegin_AcrossASeededEpisodeStream_NeverExceedsTheNMinus1Budget(ulong seed)
    {
        // Replay a full planned stream through the budget in virtual-time order, healing each begun
        // episode at its duration — the same begin/heal interleaving the Simulator drives. At no point
        // may more than N−1 nodes be isolated, for any node count from 2 up.
        for (var nodeCount = 2; nodeCount <= 5; nodeCount++)
        {
            var scheduler = new IsolationScheduler(seed, nodeCount);
            var episodes = scheduler.Plan(60, Start, Window, Min, Max);

            var heals = new PriorityQueue<int, DateTimeOffset>();
            foreach (var episode in episodes.OrderBy(e => e.StartAt))
            {
                while (heals.TryPeek(out _, out var healAt) && healAt <= episode.StartAt)
                {
                    scheduler.Heal(heals.Dequeue());
                }
                if (scheduler.TryBegin(episode.Node))
                {
                    heals.Enqueue(episode.Node, episode.StartAt + episode.Duration);
                }
                Assert.True(scheduler.IsolatedCount <= nodeCount - 1,
                    $"seed {seed}, N={nodeCount}: {scheduler.IsolatedCount} nodes isolated at once");
            }
        }
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(42UL)]
    [InlineData(0x5EEDUL)]
    public void PermanentLoss_NeverMakesTheLastReachableNodeLost(ulong seed)
    {
        // Permanent loss is a never-healing isolation (issue 0069), so permanently-lost nodes accumulate
        // in the isolated set and never free a budget slot. Replay an all-permanent stream WITHOUT healing
        // anything: once N−1 nodes are lost, every further begin is refused — at least one node stays
        // reachable forever, so full convergence remains a required liveness property. Holds for any N ≥ 2.
        for (var nodeCount = 2; nodeCount <= 5; nodeCount++)
        {
            var scheduler = new IsolationScheduler(seed, nodeCount);
            var episodes = scheduler.Plan(60, Start, Window, Min, Max, permanentLossProbability: 1.0);

            foreach (var episode in episodes.OrderBy(e => e.StartAt))
            {
                Assert.True(episode.Permanent); // probability 1.0 — every episode is a permanent loss
                scheduler.TryBegin(episode.Node); // never healed: permanent losses never free the budget
                Assert.True(scheduler.IsolatedCount <= nodeCount - 1,
                    $"seed {seed}, N={nodeCount}: {scheduler.IsolatedCount} nodes permanently lost — none reachable");
            }
        }
    }
}
