using BackWave.Core;
using BackWave.Storage;
using Npgsql;

namespace BackWave.Postgres.Tests;

/// <summary>
/// A child enqueue locks its parent's row, writes the gating edge, then commits. An outcome write that
/// makes that parent terminal while the enqueue still holds the lock must see the edge once the lock
/// frees, and resolve the latch in the same transaction (invariant I2). The one-round-trip write reads
/// its child-edge check in a snapshot taken after its row locks for exactly this case: a snapshot taken
/// before the wait would miss the edge and leave the child latched forever.
/// </summary>
[Collection("postgres")]
public sealed class PostgresOutcomeChildEdgeRaceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Reporter = "outcome-child-edge-race";

    [Fact]
    public async Task A_parent_finished_while_its_child_enqueue_holds_the_lock_still_releases_the_child()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync(applicationName: Reporter);
        var parent = new NewJob(Guid.NewGuid(), "race", "{}"u8.ToArray(), "race", T0);
        await store.EnqueueAsync(parent, T0);
        var claimed = Assert.Single(await store.ClaimAsync(new ClaimRequest("w1", ["race"], 1, TimeSpan.FromMinutes(5), T0)));

        // The child enqueue parks after it locked the parent and wrote the child row, before the edge.
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var enqueuer = new PostgresJobStore(new PostgresStoreOptions
        {
            ConnectionString = PostgresTestDatabase.ConnectionString,
            FaultHook = async (name, _) =>
            {
                if (name == "enqueue")
                {
                    parked.TrySetResult();
                    await release.Task;
                }
            },
        });
        var child = new NewJob(Guid.NewGuid(), "race", "{}"u8.ToArray(), "race", T0)
        {
            Parents = [parent.JobId],
            Mode = DependencyMode.OnAnyTerminal,
        };
        var enqueue = Task.Run(async () => await enqueuer.EnqueueAsync(child, T0));
        await parked.Task;

        var report = Task.Run(async () => await store.ReportOutcomesAsync(
            [new OutcomeReport(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success())], T0));
        await PostgresTestDatabase.WaitForLockWaitAsync(Reporter);
        release.SetResult();

        Assert.Equal(EnqueueResult.Ok, await enqueue);
        Assert.Equal(OutcomeResult.Applied, Assert.Single(await report).Result);
        Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(parent.JobId))!.State);
        var released = (await store.GetJobAsync(child.JobId))!;
        Assert.Equal(JobState.Scheduled, released.State);
        Assert.Equal(0, released.ParentsRemaining);
    }
}
