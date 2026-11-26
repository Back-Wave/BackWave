using BackWave.Core;
using BackWave.Storage;

namespace BackWave.Postgres.Tests;

/// <summary>
/// A batch that carries one job twice with different outcomes applies one of them. The child latch must
/// follow the outcome the job row took: a parent left Scheduled by a retry keeps its child latched.
/// </summary>
[Collection("postgres")]
public sealed class PostgresOutcomeDuplicateRowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_child_latch_follows_the_outcome_the_parent_row_took(bool retryFirst)
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var parent = new NewJob(Guid.NewGuid(), "dup", "{}"u8.ToArray(), "dup", T0);
        await store.EnqueueAsync(parent, T0);
        var child = new NewJob(Guid.NewGuid(), "dup", "{}"u8.ToArray(), "dup", T0)
        {
            Parents = [parent.JobId],
            Mode = DependencyMode.OnAnyTerminal,
        };
        await store.EnqueueAsync(child, T0);
        var claimed = Assert.Single(await store.ClaimAsync(new ClaimRequest("w1", ["dup"], 1, TimeSpan.FromMinutes(5), T0)));

        var retry = new OutcomeReport(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Failure(T0.AddMinutes(1), "boom"));
        var success = new OutcomeReport(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success());
        await store.ReportOutcomesAsync(retryFirst ? [retry, success] : [success, retry], T0);

        var parentState = (await store.GetJobAsync(parent.JobId))!.State;
        var latched = (await store.GetJobAsync(child.JobId))!;
        Assert.Equal(parentState.IsTerminal() ? 0 : 1, latched.ParentsRemaining);
    }
}
