using System.Data.Common;
using BackWave.Conformance;
using BackWave.Storage;
using Microsoft.Data.Sqlite;

namespace BackWave.Sqlite.Tests;

/// <summary>
/// The Conformance Suite against real SQLite (spec §10), provisioned per test on a unique temp file —
/// no Docker (the Embedded Adapter needs none). Both the ordinary store and the fault-armed store
/// (issue 0034/0096) point at the SAME file, so the armed store shares the state the test set up. The
/// caller transaction for §5.1 Transactional Enqueue opens a raw connection on that same file —
/// exactly the shape co-resident application code uses (issue 0095).
/// </summary>
public sealed class SqliteConformanceTests : ConformanceSuite, IAsyncLifetime
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"backwave_sqlite_conf_{Guid.NewGuid():N}.db");
    private readonly List<SqliteJobStore> _stores = [];
    private readonly List<SqliteConnection> _callerConnections = [];

    private string ConnectionString => $"Data Source={_path}";

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

    // The native batch override applies the whole report in one BEGIN IMMEDIATE transaction.
    protected override bool BatchOutcomesAreAtomic => true;

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
