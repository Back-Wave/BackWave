using System.Data.Common;
using BackWave.Conformance;
using BackWave.Storage;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace BackWave.Sqlite.Tests;

/// <summary>
/// The Conformance Suite against real SQLite (spec §10), provisioned per test on a unique temp file —
/// no Docker (the Embedded Adapter needs none). Both the ordinary store and the fault-armed store
/// (issue 0034/0096) point at the SAME file, so the armed store shares the state the test set up. The
/// caller transaction for §5.1 Transactional Enqueue opens a raw connection on that same file —
/// exactly the shape co-resident application code uses (issue 0095). One BEGIN IMMEDIATE writer at a
/// time means two operations can never interleave and no row can be held uncommitted against a
/// second writer, so the forced-interleaving, queue-config-lock, and held-row capabilities are
/// left undeclared and those clauses skip.
/// </summary>
public sealed class SqliteConformanceTests(ITestOutputHelper output) : ConformanceSuite(output), IAsyncLifetime
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"backwave_sqlite_conf_{Guid.NewGuid():N}.db");
    private readonly List<SqliteJobStore> _stores = [];
    private readonly List<SqliteConnection> _callerConnections = [];

    private string ConnectionString => $"Data Source={_path}";

    // The store computes NextDue, hands leases back, names its observer refusals, and applies a batch in
    // one BEGIN IMMEDIATE transaction; the fault-armed store and the out-of-band state write are the only
    // hooks a single-writer file can honor.
    protected override ConformanceCapabilities Capabilities
        => ConformanceCapabilities.NextDue
        | ConformanceCapabilities.LeaseRelinquish
        | ConformanceCapabilities.ObserverReportOutcomes
        | ConformanceCapabilities.AtomicBatchOutcomes
        | ConformanceCapabilities.FaultInjection
        | ConformanceCapabilities.OutOfBandStateWrite;

    /// <summary>Migrate the file once up front, so a §5.1 caller transaction never races first-use migration.</summary>
    public async Task InitializeAsync()
    {
        await using var warmup = NewStore(JobHistoryPolicy.TransitionsAndFailureDetail);
        await warmup.CountJobsAsync();
    }

    protected override async ValueTask<IJobStore> CreateStoreAsync(JobHistoryPolicy historyPolicy)
    {
        var store = NewStore(historyPolicy);
        // Force first-use migration NOW, before any §5.1 caller transaction grabs the write lock —
        // in production the store is long-since ready before a co-resident Transactional Enqueue, so
        // migration never races a held lock. A trivial read does it.
        await store.CountJobsAsync();
        return store;
    }

    protected override ValueTask<IJobStore?> CreateFaultArmedStoreAsync(string failpoint)
    {
        var store = new SqliteJobStore(new SqliteStoreOptions
        {
            ConnectionString = ConnectionString,
            AutoMigrate = true,
            FaultHook = (name, _) => name == failpoint
                ? throw new FaultInjectedException(failpoint)
                : Task.CompletedTask,
        });
        _stores.Add(store);
        return new(store);
    }

    // Rewrites one job's state column out of band, past every path the store owns, so the read
    // meets a value outside JobState. Only an external writer can produce that value.
    protected override async ValueTask<bool> TryStoreUndefinedJobStateAsync(Guid jobId, int state)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE backwave_jobs SET state = $state WHERE job_id = $id";
        update.Parameters.AddWithValue("$state", state);
        // The store writes the job id as canonical lowercase "D" text (SqliteValueCodec.ToText).
        update.Parameters.AddWithValue("$id", jobId.ToString("D"));
        return await update.ExecuteNonQueryAsync() == 1;
    }

    protected override DbTransaction BeginTransaction(IJobStore store)
    {
        // The caller's own ADO.NET transaction on the caller's own connection to the same file.
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        _callerConnections.Add(connection);
        return connection.BeginTransaction();
    }

    private SqliteJobStore NewStore(JobHistoryPolicy historyPolicy)
    {
        var store = new SqliteJobStore(new SqliteStoreOptions
        {
            ConnectionString = ConnectionString,
            AutoMigrate = true,
            HistoryPolicy = historyPolicy,
        });
        _stores.Add(store);
        return store;
    }

    public async Task DisposeAsync()
    {
        foreach (var connection in _callerConnections)
        {
            await connection.DisposeAsync();
        }
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_path + suffix);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; a held handle on a busy CI box is not a test failure.
            }
        }
    }
}
