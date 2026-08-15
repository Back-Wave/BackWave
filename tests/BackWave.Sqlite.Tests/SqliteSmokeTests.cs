using BackWave.Storage;

namespace BackWave.Sqlite.Tests;

/// <summary>
/// The 0092 smoke path: the minimal lifecycle (enqueue → claim → succeed) plus its fencing edges,
/// against a real temp-file SQLite store. Proves the scaffold — project, schema, migrator, codec,
/// normalizer, and the minimal store — hangs together end to end.
/// </summary>
public sealed class SqliteSmokeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Enqueue_claim_succeed_round_trips()
    {
        await using var temp = TempSqliteStore.Create();
        var store = temp.Store;
        var jobId = Guid.NewGuid();

        var enqueue = await store.EnqueueAsync(
            new NewJob(jobId, "demo", new byte[] { 1, 2, 3 }, "default", T0), T0);
        Assert.Equal(EnqueueResult.Ok, enqueue);

        var claimed = await store.ClaimAsync(
            new ClaimRequest("worker-1", ["default"], MaxJobs: 10, TimeSpan.FromMinutes(1), T0));
        var job = Assert.Single(claimed);
        Assert.Equal(jobId, job.JobId);
        Assert.Equal("demo", job.WireName);
        Assert.Equal(new byte[] { 1, 2, 3 }, job.Payload.ToArray());
        Assert.Equal(JobState.Leased, job.State);
        Assert.Equal(1, job.Attempt);
        Assert.Equal("worker-1", job.LeaseOwner);

        var outcome = await store.ReportOutcomeAsync(jobId, "worker-1", job.Attempt, new JobOutcome.Success(), T0);
        Assert.Equal(OutcomeResult.Applied, outcome);
    }

    [Fact]
    public async Task Duplicate_enqueue_is_rejected_not_replaced()
    {
        await using var temp = TempSqliteStore.Create();
        var store = temp.Store;
        var jobId = Guid.NewGuid();
        var job = new NewJob(jobId, "demo", new byte[] { 1 }, "default", T0);

        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(job, T0));
        Assert.Equal(EnqueueResult.Duplicate, await store.EnqueueAsync(job, T0));
    }

    [Fact]
    public async Task Payload_over_the_bound_is_rejected()
    {
        await using var temp = TempSqliteStore.Create();
        var oversize = new byte[StoreBounds.Default.MaxPayloadBytes + 1];

        var result = await temp.Store.EnqueueAsync(
            new NewJob(Guid.NewGuid(), "demo", oversize, "default", T0), T0);

        Assert.Equal(EnqueueResult.PayloadTooLarge, result);
    }

    [Fact]
    public async Task Claim_leaves_a_not_yet_due_job_alone()
    {
        await using var temp = TempSqliteStore.Create();
        var store = temp.Store;
        await store.EnqueueAsync(
            new NewJob(Guid.NewGuid(), "demo", default, "default", T0.AddMinutes(10)), T0);

        var claimed = await store.ClaimAsync(
            new ClaimRequest("worker-1", ["default"], 10, TimeSpan.FromMinutes(1), T0));

        Assert.Empty(claimed);
    }

    [Fact]
    public async Task Outcome_with_the_wrong_attempt_is_fenced_out()
    {
        await using var temp = TempSqliteStore.Create();
        var store = temp.Store;
        var jobId = Guid.NewGuid();
        await store.EnqueueAsync(new NewJob(jobId, "demo", default, "default", T0), T0);
        var claimed = await store.ClaimAsync(new ClaimRequest("worker-1", ["default"], 10, TimeSpan.FromMinutes(1), T0));
        var attempt = claimed[0].Attempt;

        // Wrong worker and wrong attempt both miss the (workerId, attempt) fence.
        Assert.Equal(OutcomeResult.StaleLease,
            await store.ReportOutcomeAsync(jobId, "intruder", attempt, new JobOutcome.Success(), T0));
        Assert.Equal(OutcomeResult.StaleLease,
            await store.ReportOutcomeAsync(jobId, "worker-1", attempt + 1, new JobOutcome.Success(), T0));
        Assert.Equal(OutcomeResult.Applied,
            await store.ReportOutcomeAsync(jobId, "worker-1", attempt, new JobOutcome.Success(), T0));
    }

    [Fact]
    public async Task Two_claimers_never_double_lease_the_same_job()
    {
        await using var temp = TempSqliteStore.Create();
        var store = temp.Store;
        await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "demo", default, "default", T0), T0);

        var a = store.ClaimAsync(new ClaimRequest("worker-a", ["default"], 10, TimeSpan.FromMinutes(1), T0));
        var b = store.ClaimAsync(new ClaimRequest("worker-b", ["default"], 10, TimeSpan.FromMinutes(1), T0));
        var results = await Task.WhenAll(a.AsTask(), b.AsTask());

        Assert.Equal(1, results.Sum(r => r.Count));
    }
}
