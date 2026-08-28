using System.Data;
using BackWave.Storage;
using Microsoft.Data.SqlClient;

namespace BackWave.SqlServer.Tests;

/// <summary>
/// The per-job-life transition cap on the single-row write path. The single-row recorder skips its prune
/// DELETE unless the entry it just wrote reached <c>MaxTransitionsPerJob</c>, so the cap is enforced by a
/// statement that usually does not run. The Conformance Suite's cap clause interleaves the batch recorder,
/// which re-prunes on the very next transition and so hides an off-by-one in the skip; this pins the
/// boundary on the single-row path alone.
/// </summary>
[Collection("sqlserver")]
public sealed class SqlServerTransitionPruneTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private static NewJob Job(Guid id) => new(id, "prune-test", "{}"u8.ToArray(), "prune", T0);

    [Fact]
    public async Task The_single_row_recorder_keeps_a_job_at_the_cap_and_prunes_it_one_transition_later()
    {
        // The boundary in both directions. A job holding exactly MaxTransitionsPerJob entries is AT the
        // cap, not over it, and must keep every one; the very next single-row transition is the first that
        // exceeds the cap, and it must cost exactly the oldest entry.
        var store = await SqlServerTestDatabase.CreateFreshStoreAsync();
        var cap = store.Bounds.MaxTransitionsPerJob;
        var jobId = Guid.NewGuid();
        await store.EnqueueAsync(Job(jobId), T0);

        // Enqueue wrote ordinal 0. Fill to ordinal cap - 2, so the claim below lands on cap - 1 and takes
        // the log to exactly cap entries.
        await FillTransitionsUpToAsync(jobId, cap - 2);
        var claimed = Assert.Single(
            await store.ClaimAsync(new ClaimRequest("w1", ["prune"], 32, Lease, T0)));

        var atCap = await store.GetJobHistoryAsync(jobId);
        Assert.Equal(cap, atCap.Count);
        Assert.Equal(0L, atCap[0].Ordinal); // nothing was pruned: the oldest entry is still the first one
        Assert.Equal(cap - 1L, atCap[^1].Ordinal);

        // One more transition, through the single-row recorder (ReportOutcomeAsync), which is the first
        // to exceed the cap.
        Assert.Equal(
            OutcomeResult.Applied,
            await store.ReportOutcomeAsync(
                jobId, "w1", claimed.Attempt, new JobOutcome.Failure(T0.AddMinutes(10), "again"), T0));

        var pastCap = await store.GetJobHistoryAsync(jobId);
        Assert.Equal(cap, pastCap.Count);
        Assert.Equal(1L, pastCap[0].Ordinal); // exactly one entry aged out - the oldest
        Assert.Equal(cap, (int)pastCap[^1].Ordinal);
    }

    // Extends a job's Transition Log with filler entries so its highest ordinal is exactly
    // <paramref name="highestOrdinal"/>. Walking the log there through the store would be dozens of
    // claim/report cycles of setup noise; the fixture writes the rows directly instead, and every
    // assertion reads the log back through the store, so a fixture that lied would fail them.
    private static async Task FillTransitionsUpToAsync(Guid jobId, int highestOrdinal)
    {
        await using var connection = new SqlConnection(SqlServerTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var fill = connection.CreateCommand();
        fill.CommandText =
            """
            DECLARE @ordinal bigint =
                (SELECT COALESCE(MAX(ordinal), -1) + 1 FROM backwave.job_transitions WHERE job_id = @id);
            WHILE @ordinal <= @top
            BEGIN
                INSERT INTO backwave.job_transitions
                    (job_id, ordinal, recorded_at, state, attempt, failure_detail)
                VALUES (@id, @ordinal, @now, @state, 0, NULL);
                SET @ordinal = @ordinal + 1;
            END
            """;
        fill.Parameters.AddWithValue("id", jobId);
        fill.Parameters.Add("now", SqlDbType.DateTimeOffset).Value = T0;
        fill.Parameters.AddWithValue("state", (int)JobState.Leased);
        fill.Parameters.AddWithValue("top", (long)highestOrdinal);
        await fill.ExecuteNonQueryAsync();
    }
}
