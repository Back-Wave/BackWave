using BackWave.Storage;
using Npgsql;

namespace BackWave.Postgres.Tests;

/// <summary>
/// The one-statement claim reads every transition ordinal from the snapshot it started with. When a
/// transition for a job it claims commits after that snapshot, the insert collides on the primary key,
/// the statement rolls back whole, and the claim reruns as two statements. This forces that collision.
/// In production the late transition comes from another claimer that leased the job and handed it back
/// inside that window; here a row forged under <c>session_replication_role = replica</c> stands in for
/// that race.
/// </summary>
[Collection("postgres")]
public sealed class PostgresClaimOrdinalCollisionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private const string Claimer = "claim-ordinal-collision";

    [Fact]
    public async Task A_claim_that_collides_on_an_ordinal_falls_back_and_records_the_next_one()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync(applicationName: Claimer);
        var first = new NewJob(Guid.NewGuid(), "collision", "{}"u8.ToArray(), "collision", T0.AddMinutes(-1));
        var raced = new NewJob(Guid.NewGuid(), "collision", "{}"u8.ToArray(), "collision", T0);
        await store.EnqueueAsync(first, T0);
        await store.EnqueueAsync(raced, T0);

        // Stamps the queue unlimited, so the next claim takes the one-statement path.
        Assert.Equal(first.JobId, Assert.Single(await store.ClaimAsync(Claim())).JobId);

        // Stands in for a transition another transaction commits after the claim's snapshot. The
        // replica role skips the foreign-key trigger, so this transaction holds no lock on the job row:
        // the claim leases the job and then waits only on this ordinal.
        await using var other = new NpgsqlConnection(PostgresTestDatabase.ConnectionString);
        await other.OpenAsync();
        await using var otherTransaction = await other.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand(
            """
            SET LOCAL session_replication_role = replica;
            INSERT INTO backwave.job_transitions (job_id, ordinal, recorded_at, state, attempt)
            VALUES (@id, 1, @now, 2, 1)
            """, other, otherTransaction))
        {
            insert.Parameters.AddWithValue("id", raced.JobId);
            insert.Parameters.AddWithValue("now", T0.UtcDateTime);
            await insert.ExecuteNonQueryAsync();
        }

        var claim = Task.Run(async () => await store.ClaimAsync(Claim()));
        await PostgresTestDatabase.WaitForLockWaitAsync(Claimer, "transactionid");
        await otherTransaction.CommitAsync();
        var claimed = await claim;

        var job = Assert.Single(claimed);
        Assert.Equal(raced.JobId, job.JobId);
        Assert.Equal(1, job.Attempt); // the rolled-back statement consumed no attempt
        var history = await store.GetJobHistoryAsync(raced.JobId);
        Assert.Equal([0L, 1, 2], history.Select(t => t.Ordinal).ToList());
        Assert.Equal(JobState.Leased, history[^1].State);
    }

    private static ClaimRequest Claim() => new("w1", ["collision"], 1, Lease, T0);
}
