using BackWave.Storage;
using Microsoft.Extensions.Logging;

namespace BackWave.Sqlite;

/// <summary>
/// Configures a <see cref="SqliteJobStore"/>: which SQLite file it uses and how it behaves. The
/// connection string is the one knob that decides your deployment shape — point it at your own
/// application's database file to run <b>co-resident</b> (so a job can be enqueued in the same
/// transaction as your business writes), or at a separate file to run <b>dedicated</b>.
/// </summary>
public sealed record SqliteStoreOptions
{
    /// <summary>
    /// The SQLite connection string, for example <c>Data Source=app.db</c>. The path you choose is
    /// what selects co-resident vs dedicated: a co-resident deployment points this at your
    /// application's own database file (so transactional enqueue commits a job atomically with your
    /// own writes); a dedicated deployment gives BackWave its own file. There is no default — you
    /// must supply this. The store rewrites the string to force the pragmas it depends on for
    /// correctness (foreign-key enforcement, connection pooling, and a default command timeout
    /// derived from <see cref="BusyTimeout"/>), so those are never left to chance.
    /// </summary>
    public required string ConnectionString { get; init; }

    /// <summary>
    /// When <c>true</c>, the store creates or upgrades its schema in the target file the first time it
    /// is used. <b>Default: <c>false</c></b> — by default you apply the schema yourself as part of
    /// your normal database migration process. Either way, if the file already has a schema from a
    /// different version of the adapter, the store refuses to start rather than risk corrupting job
    /// state.
    /// </summary>
    public bool AutoMigrate { get; init; }

    /// <summary>
    /// The prefix on every BackWave table and index name in the SQLite file. SQLite has no schemas,
    /// so this is the analogue of a schema name: it namespaces BackWave's objects (for example
    /// <c>jobs</c> becomes <c>backwave_jobs</c>). <b>Default: <c>backwave</c>.</b> Change it to keep
    /// BackWave's tables clear of your own when running co-resident in your application's file, or to
    /// fit a naming convention. The same prefix must be used everywhere the store touches this file —
    /// auto-migrate, out-of-band migration, and every query all read it from here — so set it once and
    /// leave it fixed for the life of the data. Must be a valid identifier root: 1–64 characters, a
    /// letter or underscore followed by letters, digits, or underscores; any other value is rejected
    /// when the store is created.
    /// </summary>
    public string TablePrefix { get; init; } = "backwave";

    /// <summary>
    /// The size and count limits this store enforces — maximum job payload, output, tag, and history
    /// sizes. <b>Default: <see cref="StoreBounds.Default"/></b>, which is suitable for most
    /// applications. Tighten or loosen these only if you know your workload needs it.
    /// </summary>
    public StoreBounds Bounds { get; init; } = StoreBounds.Default;

    /// <summary>
    /// How much job history the store records — full transition history with failure detail, just
    /// transitions, or nothing. <b>Default: <see cref="JobHistoryPolicy.TransitionsAndFailureDetail"/></b>,
    /// so the dashboard timeline works out of the box. This controls only what is written; changing it
    /// is a configuration change, not a schema migration.
    /// </summary>
    public JobHistoryPolicy HistoryPolicy { get; init; } = JobHistoryPolicy.TransitionsAndFailureDetail;

    /// <summary>
    /// How long a writer waits for the single SQLite write lock before giving up with a "database is
    /// busy" error. <b>Default: 5 seconds.</b> SQLite allows only one writer at a time, so under heavy
    /// multi-process contention a value that is too short can cause a worker to lose its lease and a
    /// job to run twice (which BackWave tolerates, since jobs may run at least once). The generous
    /// default keeps that rare. This value also becomes the default command timeout on every
    /// connection the store opens.
    /// </summary>
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// When <c>true</c> (the default), auto-migration runs the schema scripts inside a single
    /// <c>BEGIN IMMEDIATE</c> write transaction, so if several processes open the same file and
    /// migrate at once, exactly one applies the schema while the others block on SQLite's single-writer
    /// write lock, re-check, and no-op — and the whole migration is atomic (all-or-nothing). For SQLite
    /// this write lock <em>is</em> the coordination: there is no distributed SQLite, so the guarantee is
    /// per-host (across processes sharing the file), and this option exists mainly for API symmetry with
    /// the client-server adapters. It applies only when <see cref="AutoMigrate"/> is on. Setting it to
    /// <c>false</c> runs the scripts without an explicit transaction and is safe only when your
    /// deployment already serializes migration itself.
    /// </summary>
    public bool CoordinateMigration { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, the store nudges worker pumps in the <em>same process</em> to poll sooner
    /// right after it commits new work, trimming latency. <b>Default: <c>true</c>.</b> This is a
    /// best-effort optimisation only and never affects correctness: a missed nudge just means a pump
    /// waits for its next ordinary poll. The nudge fires only on the store's own commits, never on a
    /// transactional enqueue you commit yourself.
    /// </summary>
    public bool EnableInProcessHints { get; init; } = true;

    /// <summary>
    /// Optional logger factory. When supplied and <see cref="AutoMigrate"/> is on, the store records a
    /// schema-migration event at Information level after applying the schema on first use. Null (the
    /// default) disables that log with no allocation and does not otherwise affect the store.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    // Test-only failpoint; null in production. The adapter invokes it inside an open write
    // transaction before commit; throwing aborts the transaction, which must roll back every effect
    // (all-or-nothing). The argument is the failpoint name. Issue 0034.
    /// <summary>
    /// Reserved for BackWave's own testing; leave this <c>null</c> in production. It is invoked inside
    /// the store's open write transaction before commit, with the name of the operation in progress.
    /// </summary>
    public Func<string, CancellationToken, Task>? FaultHook { get; init; }
}
