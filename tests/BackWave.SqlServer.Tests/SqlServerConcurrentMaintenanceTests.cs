using BackWave.Core;
using BackWave.Storage;
using Microsoft.Data.SqlClient;

namespace BackWave.SqlServer.Tests;

/// <summary>
/// Lock-footprint regression tests. A fleet runs maintenance on every node at once, so the store's
/// set-based statements must lock only the rows of their own batch. When one of them scans
/// backwave.jobs instead, two sweeps cross-lock and SQL Server kills one with error 1205.
/// </summary>
[Collection("sqlserver")]
public sealed class SqlServerConcurrentMaintenanceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    // 96 leased jobs per round is wide enough that the optimizer costs a full scan of backwave.jobs
    // below a seek per row, which is the plan that exposes an oversized lock footprint. The first
    // round caches that plan, so every later round runs against the worst case. Both history policies
    // run: Off exercises the disposition UPDATEs alone, and the full rung adds the transition insert.
    [Theory]
    [InlineData(JobHistoryPolicy.TransitionsAndFailureDetail)]
    [InlineData(JobHistoryPolicy.Off)]
    public async Task Concurrent_lease_sweeps_never_deadlock(JobHistoryPolicy policy)
    {
        const int Rounds = 15, Jobs = 96, Sweepers = 6;
        var disposition = new RetryPolicy { MaxAttempts = 5, Backoff = _ => TimeSpan.FromMinutes(1) }.ToDisposition();
        var deadlocks = 0;

        for (var round = 0; round < Rounds; round++)
        {
            var store = await SqlServerTestDatabase.CreateFreshStoreAsync(policy);
            for (var i = 0; i < Jobs; i++)
            {
                await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "t", "{}"u8.ToArray(), "default", T0), T0);
            }
            await store.ClaimAsync(new ClaimRequest("w", ["default"], Jobs, Lease, T0));

            var after = T0 + Lease + TimeSpan.FromSeconds(1);
            var results = await Task.WhenAll(Enumerable.Range(0, Sweepers).Select(_ => Task.Run(async () =>
            {
                try
                {
                    await store.ExpireLeasesAsync(after, maxJobs: Jobs, ["default"], disposition);
                    return 0;
                }
                catch (SqlException e) when (e.Number == 1205)
                {
                    return 1;
                }
            })));
            deadlocks += results.Sum();
        }

        Assert.Equal(0, deadlocks);
    }
}
