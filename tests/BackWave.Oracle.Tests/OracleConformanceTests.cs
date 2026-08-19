using System.Data.Common;
using BackWave.Conformance;
using BackWave.Storage;
using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle.Tests;

/// <summary>The Conformance Suite against real Oracle (spec §10).</summary>
[Collection("oracle")]
public sealed class OracleConformanceTests : ConformanceSuite
{
    protected override async ValueTask<IJobStore> CreateStoreAsync(JobHistoryPolicy historyPolicy)
        => await OracleTestDatabase.CreateFreshStoreAsync(historyPolicy);

    // The batch override applies the whole report inside one Oracle transaction (a per-row fenced UPDATE
    // loop, committed once), so a batch is all-or-nothing.
    protected override bool BatchOutcomesAreAtomic => true;

    // A second store on the same test database with the failpoint armed - no wipe, so it shares the state
    // the test sets up through the normal store.
    protected override ValueTask<IJobStore?> CreateFaultArmedStoreAsync(string failpoint)
        => new(new OracleJobStore(new OracleStoreOptions
        {
            ConnectionString = OracleTestDatabase.ConnectionString,
            FaultHook = (name, _) => name == failpoint
                ? throw new FaultInjectedException(failpoint)
                : Task.CompletedTask,
        }));

    // A second store on the same test database whose failpoint hook parks (rather than throws), so a test
    // can pin the first-config interleaving deterministically.
    protected override ValueTask<IJobStore?> CreateInterleavingStoreAsync(
        Func<string, CancellationToken, Task> onFailpoint)
        => new(new OracleJobStore(new OracleStoreOptions
        {
            ConnectionString = OracleTestDatabase.ConnectionString,
            FaultHook = (name, ct) => onFailpoint(name, ct),
        }));

    // Holds the queue_locks anchor row FOR UPDATE on its own connection+transaction, committing it on
    // disposal, so a test can pin the first-config serialization window. AcquireQueueConfigLockAsync
    // materializes this per-queue anchor and locks it FOR UPDATE, so holding it here blocks a concurrent
    // first pause/limit until release.
    protected override async ValueTask<IAsyncDisposable?> HoldQueueConfigLockAsync(string queue)
    {
        var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var transaction = (OracleTransaction)await connection.BeginTransactionAsync();
        await using (var ensure = new OracleCommand { Connection = connection, Transaction = transaction, BindByName = true })
        {
            ensure.CommandText =
                """
                INSERT INTO backwave.queue_locks (queue)
                SELECT :queue FROM dual
                WHERE NOT EXISTS (SELECT 1 FROM backwave.queue_locks WHERE queue = :queue)
                """;
            ensure.Parameters.Add(new OracleParameter("queue", OracleDbType.Varchar2) { Value = queue });
            await ensure.ExecuteNonQueryAsync();
        }
        await using (var applock = new OracleCommand { Connection = connection, Transaction = transaction, BindByName = true })
        {
            applock.CommandText = "SELECT queue FROM backwave.queue_locks WHERE queue = :queue FOR UPDATE";
            applock.Parameters.Add(new OracleParameter("queue", OracleDbType.Varchar2) { Value = queue });
            await using var reader = await applock.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
            }
        }
        return new HeldRow(connection, transaction);
    }

    // Holds an uncommitted duplicate job_tags row on its own connection+transaction, committing it on
    // disposal, so a test can pin the concurrent-duplicate window on the store's tag insert. Empty key/value
    // are encoded to the CHR(1) sentinel exactly as the store does, so the held row collides with the one
    // the store writes.
    protected override async ValueTask<IAsyncDisposable?> HoldTagRowAsync(Guid jobId, JobTag tag)
    {
        var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var transaction = (OracleTransaction)await connection.BeginTransactionAsync();
        await using (var insert = new OracleCommand { Connection = connection, Transaction = transaction, BindByName = true })
        {
            insert.CommandText = "INSERT INTO backwave.job_tags (job_id, key, value) VALUES (:id, :key, :value)";
            insert.Parameters.Add(new OracleParameter("id", OracleDbType.Raw) { Size = 16, Value = jobId.ToByteArray() });
            insert.Parameters.Add(new OracleParameter("key", OracleDbType.Varchar2) { Value = EncodeTag(tag.Key) });
            insert.Parameters.Add(new OracleParameter("value", OracleDbType.Varchar2) { Value = EncodeTag(tag.Value) });
            await insert.ExecuteNonQueryAsync();
        }
        return new HeldRow(connection, transaction);
    }

    // The workflow_edges twin.
    protected override async ValueTask<IAsyncDisposable?> HoldEdgeRowAsync(Guid workflowId, Guid parentId, Guid childId)
    {
        var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var transaction = (OracleTransaction)await connection.BeginTransactionAsync();
        await using (var insert = new OracleCommand { Connection = connection, Transaction = transaction, BindByName = true })
        {
            insert.CommandText =
                "INSERT INTO backwave.workflow_edges (workflow_id, parent_id, child_id) VALUES (:w, :p, :c)";
            insert.Parameters.Add(new OracleParameter("w", OracleDbType.Raw) { Size = 16, Value = workflowId.ToByteArray() });
            insert.Parameters.Add(new OracleParameter("p", OracleDbType.Raw) { Size = 16, Value = parentId.ToByteArray() });
            insert.Parameters.Add(new OracleParameter("c", OracleDbType.Raw) { Size = 16, Value = childId.ToByteArray() });
            await insert.ExecuteNonQueryAsync();
        }
        return new HeldRow(connection, transaction);
    }

    protected override DbTransaction BeginTransaction(IJobStore store)
    {
        // The caller's own ADO.NET transaction on the caller's own connection - exactly the shape
        // application code uses for Transactional Enqueue.
        var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        connection.Open();
        return connection.BeginTransaction();
    }

    // Oracle folds an empty string to NULL, but key/value are NOT NULL PK columns, so an empty Tag key or
    // value is stored as the CHR(1) control character - the same encoding OracleJobStore applies.
    private static string EncodeTag(string value) => value.Length == 0 ? "" : value;

    private sealed class HeldRow(OracleConnection connection, OracleTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.CommitAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
