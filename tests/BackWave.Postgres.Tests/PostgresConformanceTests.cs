using System.Data.Common;
using BackWave.Conformance;
using BackWave.Storage;
using Npgsql;

namespace BackWave.Postgres.Tests;

/// <summary>The Conformance Suite against real Postgres (spec §10).</summary>
[Collection("postgres")]
public sealed class PostgresConformanceTests : ConformanceSuite
{
    protected override async ValueTask<IJobStore> CreateStoreAsync(JobHistoryPolicy historyPolicy)
        => await PostgresTestDatabase.CreateFreshStoreAsync(historyPolicy);

    // The native batch override applies the whole report in one transaction (multi-row UPDATE FROM unnest).
    protected override bool BatchOutcomesAreAtomic => true;

    // A second store on the same test database with the failpoint armed — no truncation, so it
    // shares the state the test sets up through the normal store (issue 0034).
    protected override ValueTask<IJobStore?> CreateFaultArmedStoreAsync(string failpoint)
        => new(new PostgresJobStore(new PostgresStoreOptions
        {
            ConnectionString = PostgresTestDatabase.ConnectionString,
            FaultHook = (name, _) => name == failpoint
                ? throw new FaultInjectedException(failpoint)
                : Task.CompletedTask,
        }));

    // A second store on the same test database whose failpoint hook parks (rather than throws), so a
    // test can pin the 0193 first-config interleaving deterministically.
    protected override ValueTask<IJobStore?> CreateInterleavingStoreAsync(
        Func<string, CancellationToken, Task> onFailpoint)
        => new(new PostgresJobStore(new PostgresStoreOptions
        {
            ConnectionString = PostgresTestDatabase.ConnectionString,
            FaultHook = (name, ct) => onFailpoint(name, ct),
        }));

    // Holds, on its own connection, the per-Queue advisory lock the store's claim read path and config
    // setters take (issue 0193). Session-level pg_advisory_lock shares Postgres's advisory lock space
    // with the store's pg_advisory_xact_lock, so it blocks a claim that takes it. The hashtext(queue)
    // key MUST match PostgresJobStore.AcquireQueueConfigLockAsync.
    protected override async ValueTask<IAsyncDisposable?> HoldQueueConfigLockAsync(string queue)
    {
        var connection = new NpgsqlConnection(PostgresTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(hashtext(@queue))", connection))
        {
            acquire.Parameters.AddWithValue("queue", queue);
            await acquire.ExecuteNonQueryAsync();
        }
        return new AdvisoryLockHolder(connection, queue);
    }

    // Holds an uncommitted duplicate job_tags row on its own connection+transaction, committing it on
    // disposal, so a test can pin the concurrent-duplicate window on the store's tag insert (issue 0195).
    // Postgres's ON CONFLICT DO NOTHING already converges, so this stays green — the parity target.
    protected override async ValueTask<IAsyncDisposable?> HoldTagRowAsync(Guid jobId, JobTag tag)
    {
        var connection = new NpgsqlConnection(PostgresTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO backwave.job_tags (job_id, key, value) VALUES (@id, @key, @value)",
            connection, transaction))
        {
            insert.Parameters.AddWithValue("id", jobId);
            insert.Parameters.AddWithValue("key", tag.Key);
            insert.Parameters.AddWithValue("value", tag.Value);
            await insert.ExecuteNonQueryAsync();
        }
        return new HeldRow(connection, transaction);
    }

    // The workflow_edges twin (issue 0195).
    protected override async ValueTask<IAsyncDisposable?> HoldEdgeRowAsync(Guid workflowId, Guid parentId, Guid childId)
    {
        var connection = new NpgsqlConnection(PostgresTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO backwave.workflow_edges (workflow_id, parent_id, child_id) VALUES (@w, @p, @c)",
            connection, transaction))
        {
            insert.Parameters.AddWithValue("w", workflowId);
            insert.Parameters.AddWithValue("p", parentId);
            insert.Parameters.AddWithValue("c", childId);
            await insert.ExecuteNonQueryAsync();
        }
        return new HeldRow(connection, transaction);
    }

    private sealed class HeldRow(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.CommitAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class AdvisoryLockHolder(NpgsqlConnection connection, string queue) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtext(@queue))", connection))
            {
                release.Parameters.AddWithValue("queue", queue);
                await release.ExecuteNonQueryAsync();
            }
            await connection.DisposeAsync();
        }
    }

    protected override DbTransaction BeginTransaction(IJobStore store)
    {
        // The caller's own ADO.NET transaction on the caller's own connection — exactly
        // the shape application code uses for Transactional Enqueue.
        var connection = new NpgsqlConnection(PostgresTestDatabase.ConnectionString);
        connection.Open();
        return connection.BeginTransaction();
    }
}
