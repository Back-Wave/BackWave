using BackWave.Sqlite;
using BackWave.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BackWave.Sqlite.Tests;

/// <summary>
/// Provisions a fresh SQLite store backed by a unique temp file (no Docker — the Embedded Adapter
/// needs none). Disposing clears the connection pool and deletes the file plus its <c>-wal</c>/
/// <c>-shm</c> sidecars, so each test sees a clean store and leaves nothing behind.
/// </summary>
public sealed class TempSqliteStore : IAsyncDisposable
{
    private TempSqliteStore(SqliteJobStore store, string path)
    {
        Store = store;
        Path = path;
    }

    public SqliteJobStore Store { get; }

    public string Path { get; }

    public static TempSqliteStore Create(
        JobHistoryPolicy historyPolicy = JobHistoryPolicy.TransitionsAndFailureDetail,
        string tablePrefix = "backwave",
        ILoggerFactory? loggerFactory = null)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"backwave_sqlite_{Guid.NewGuid():N}.db");
        var store = new SqliteJobStore(new SqliteStoreOptions
        {
            ConnectionString = $"Data Source={path}",
            AutoMigrate = true,
            HistoryPolicy = historyPolicy,
            TablePrefix = tablePrefix,
            LoggerFactory = loggerFactory,
        });
        return new TempSqliteStore(store, path);
    }

    /// <summary>
    /// A store on a fresh temp file whose failpoint hook throws, so a test can force a store fault of
    /// its choosing. <paramref name="fault"/> is handed the failpoint name and returns the exception to
    /// raise, which lets a test pick the exception TYPE - the input to the adapter's transient/terminal
    /// classification.
    /// </summary>
    public static TempSqliteStore CreateFaultArmed(string failpoint, Func<string, Exception> fault)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"backwave_sqlite_{Guid.NewGuid():N}.db");
        var store = new SqliteJobStore(new SqliteStoreOptions
        {
            ConnectionString = $"Data Source={path}",
            AutoMigrate = true,
            FaultHook = (name, _) => name == failpoint
                ? throw fault(name)
                : Task.CompletedTask,
        });
        return new TempSqliteStore(store, path);
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(Path + suffix);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; a held handle on a busy CI box is not a test failure.
            }
        }
    }
}
