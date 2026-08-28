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

    // Every writer shares one transition-log INSERT, so its plan is compiled once and then reused. A
    // wide lease sweep compiles it at 96 rows, where the optimizer serves the foreign-key check to
    // backwave.jobs with a full scan, and every later caller inherits that plan whatever its own batch
    // size holds. This test poisons the plan cache that way on purpose, then reports the narrow outcome
    // batches a real fleet reports. That is the shape that lost a deadlock before the bounded retry.
    [Fact]
    public async Task Concurrent_outcome_reports_never_deadlock()
    {
        const int Poison = 96, Rounds = 30, Workers = 6, PerWorker = 16;
        var store = await SqlServerTestDatabase.CreateFreshStoreAsync(JobHistoryPolicy.TransitionsAndFailureDetail);
        var deadLetter = new RetryPolicy { MaxAttempts = 1, Backoff = _ => TimeSpan.FromMinutes(1) }.ToDisposition();

        for (var i = 0; i < Poison; i++)
        {
            await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "t", "{}"u8.ToArray(), "poison", T0), T0);
        }
        await store.ClaimAsync(new ClaimRequest("sweeper", ["poison"], Poison, Lease, T0));
        // MaxAttempts 1 dead-letters the swept jobs, so they never return to the claimable set.
        await store.ExpireLeasesAsync(T0 + Lease + TimeSpan.FromSeconds(1), Poison, ["poison"], deadLetter);

        var deadlocks = 0;
        for (var round = 0; round < Rounds; round++)
        {
            var now = T0 + TimeSpan.FromHours(round + 1);
            for (var i = 0; i < Workers * PerWorker; i++)
            {
                await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "t", "{}"u8.ToArray(), "default", now), now);
            }

            var batches = new List<OutcomeReport[]>();
            for (var w = 0; w < Workers; w++)
            {
                var worker = $"w{w}";
                var claimed = await store.ClaimAsync(new ClaimRequest(worker, ["default"], PerWorker, Lease, now));
                batches.Add([.. claimed.Select(job =>
                    new OutcomeReport(job.JobId, worker, job.Attempt, new JobOutcome.Success()))]);
            }

            var results = await Task.WhenAll(batches.Select(batch => Task.Run(async () =>
            {
                try
                {
                    await store.ReportOutcomesAsync(batch, now);
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
