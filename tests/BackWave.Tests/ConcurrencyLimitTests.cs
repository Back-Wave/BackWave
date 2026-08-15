using BackWave.Core;
using BackWave.Storage;
using BackWave.Storage.InMemory;

namespace BackWave.Tests;

/// <summary>
/// Concurrency Limits are enforced at claim time through the Storage Contract, so these
/// tests exercise the store directly — the cluster-wide property is independent of any
/// node's Driver.
/// </summary>
public class ConcurrencyLimitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    private static NewJob Job(int n) => new(
        Guid.NewGuid(), "work", ReadOnlyMemory<byte>.Empty, "sendgrid", T0);

    private static ClaimRequest Claim(string worker, DateTimeOffset now, int max = 32)
        => new(worker, ["sendgrid"], max, Lease, now);

    private static async Task<InMemoryJobStore> StoreWithLimitAndJobs(int limit, int jobs)
    {
        var store = new InMemoryJobStore();
        await store.SetConcurrencyLimitAsync("sendgrid", limit, "test", T0);
        for (var i = 0; i < jobs; i++)
        {
            Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(Job(i), T0));
        }
        return store;
    }

    [Fact]
    public async Task ClaimsAcrossWorkers_NeverExceedTheLimit()
    {
        var store = await StoreWithLimitAndJobs(limit: 5, jobs: 10);

        var first = await store.ClaimAsync(Claim("node-a", T0));
        var second = await store.ClaimAsync(Claim("node-b", T0));

        Assert.Equal(5, first.Count);
        Assert.Empty(second);
    }

    [Fact]
    public async Task TerminalState_ReleasesSlots()
    {
        var store = await StoreWithLimitAndJobs(limit: 5, jobs: 10);
        var leased = await store.ClaimAsync(Claim("node-a", T0));

        foreach (var job in leased.Take(2))
        {
            await store.ReportOutcomeAsync(job.JobId, "node-a", job.Attempt, new JobOutcome.Success(), T0);
        }

        Assert.Equal(2, (await store.ClaimAsync(Claim("node-b", T0))).Count);
    }

    [Fact]
    public async Task LeaseExpiry_ReleasesACrashedWorkersSlots()
    {
        var store = await StoreWithLimitAndJobs(limit: 5, jobs: 5);
        Assert.Equal(5, (await store.ClaimAsync(Claim("node-a", T0))).Count);

        // node-a crashes; before expiry nothing is claimable, after expiry all five
        // slots are free again — never leaked.
        var beforeExpiry = T0 + Lease - TimeSpan.FromSeconds(1);
        Assert.Empty(await store.ClaimAsync(Claim("node-b", beforeExpiry)));

        var afterExpiry = T0 + Lease + TimeSpan.FromSeconds(1);
        await store.ExpireLeasesAsync(afterExpiry, 32, ["sendgrid"], new RetryPolicy { Backoff = _ => TimeSpan.Zero }.ToDisposition());

        var reclaimed = await store.ClaimAsync(Claim("node-b", afterExpiry));
        Assert.Equal(5, reclaimed.Count);
        Assert.All(reclaimed, j => Assert.Equal(2, j.Attempt));
    }

    [Fact]
    public async Task ClearingTheLimit_RemovesTheCap()
    {
        var store = await StoreWithLimitAndJobs(limit: 2, jobs: 10);
        Assert.Equal(2, (await store.ClaimAsync(Claim("node-a", T0))).Count);

        await store.SetConcurrencyLimitAsync("sendgrid", null, "test", T0);
        Assert.Equal(8, (await store.ClaimAsync(Claim("node-a", T0))).Count);
    }

    [Fact]
    public async Task UnlimitedQueues_AreUnaffectedByOtherQueuesLimits()
    {
        var store = new InMemoryJobStore();
        await store.SetConcurrencyLimitAsync("sendgrid", 1, "test", T0);
        await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "work", ReadOnlyMemory<byte>.Empty, "sendgrid", T0), T0);
        await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "work", ReadOnlyMemory<byte>.Empty, "reports", T0), T0);
        await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "work", ReadOnlyMemory<byte>.Empty, "reports", T0), T0);

        var claimed = await store.ClaimAsync(new ClaimRequest("node-a", ["sendgrid", "reports"], 32, Lease, T0));
        Assert.Equal(3, claimed.Count);
    }
}
