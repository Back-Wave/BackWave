using BackWave.Sqlite;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Microsoft.Data.Sqlite;

namespace BackWave.Tests.Simulation;

/// <summary>
/// Which store implementation a run drives. The default is the In-Memory Store, so every existing
/// seed replays byte-identically. The SQLite kinds put the REAL adapter under the deterministic
/// event loop (dst-0001): the Simulator is single threaded and injects Virtual Time into every
/// store call, so the adapter is a pure function of its SQL plus the injected instant.
/// </summary>
internal enum SimStoreKind
{
    /// <summary>The shipped <c>InMemoryJobStore</c>. The default. Takes no file handles and no schema migration.</summary>
    InMemory,

    /// <summary>The real <c>SqliteJobStore</c> on a private SQLite in-memory database. One per Simulator.</summary>
    SqliteMemory,

    /// <summary>The real <c>SqliteJobStore</c> on a private temporary file. One per Simulator.</summary>
    SqliteFile,
}

/// <summary>
/// Owns the store a run drives and everything that must be released after it. Each scope builds its
/// OWN database, so two Simulators - in one process or in two parallel VOPR workers - never share
/// storage. Disposal is idempotent.
/// </summary>
internal sealed class SimStoreScope : IDisposable
{
    private readonly SqliteJobStore? _sqlite;

    // Shared-cache in-memory SQLite lives only while a connection to it is open. This handle keeps
    // the database alive between the adapter's pooled connections and drops it on disposal.
    private readonly SqliteConnection? _keepAlive;
    private readonly string? _path;
    private bool _disposed;

    private SimStoreScope(IJobStore store, SqliteJobStore? sqlite, SqliteConnection? keepAlive, string? path)
    {
        Store = store;
        _sqlite = sqlite;
        _keepAlive = keepAlive;
        _path = path;
    }

    /// <summary>The store handed to the Simulator.</summary>
    public IJobStore Store { get; }

    /// <summary>
    /// Builds a scope for <paramref name="kind"/>. The identity in the connection string comes from
    /// <see cref="Guid.NewGuid"/>, which names the database only. It never reaches the run, so the
    /// timeline stays a function of the seed alone.
    /// </summary>
    public static SimStoreScope Create(SimStoreKind kind)
    {
        if (kind == SimStoreKind.InMemory)
        {
            return new SimStoreScope(new InMemoryJobStore(), null, null, null);
        }

        var id = Guid.NewGuid().ToString("N");
        string connectionString;
        SqliteConnection? keepAlive = null;
        string? path = null;
        if (kind == SimStoreKind.SqliteMemory)
        {
            connectionString = $"Data Source=backwave_sim_{id};Mode=Memory;Cache=Shared";
            keepAlive = new SqliteConnection(connectionString);
            keepAlive.Open();
        }
        else
        {
            path = Path.Combine(Path.GetTempPath(), $"backwave_sim_{id}.db");
            connectionString = $"Data Source={path}";
        }

        var sqlite = new SqliteJobStore(new SqliteStoreOptions
        {
            ConnectionString = connectionString,
            AutoMigrate = true,
            // The Simulator subscribes to no hint source and drives its own poll cadence, so the
            // in-process nudge would only add work off the event loop.
            EnableInProcessHints = false,
        });
        return new SimStoreScope(sqlite, sqlite, keepAlive, path);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _sqlite?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (_keepAlive is not null)
        {
            SqliteConnection.ClearPool(_keepAlive);
            _keepAlive.Dispose();
        }
        if (_path is null)
        {
            return;
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
                // Best-effort temp cleanup. A held handle is not a simulation failure.
            }
        }
    }
}
