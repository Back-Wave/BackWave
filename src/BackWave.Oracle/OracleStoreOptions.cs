using BackWave.Storage;
using Microsoft.Extensions.Logging;

namespace BackWave.Oracle;

/// <summary>
/// Configuration for the Oracle storage adapter. Supply at minimum a <see cref="ConnectionString"/>;
/// the remaining settings have safe production defaults.
/// </summary>
public sealed record OracleStoreOptions
{
    /// <summary>
    /// The ODP.NET connection string the adapter uses for every database operation. Required. Point it
    /// at the database that holds (or will hold) the BackWave schema.
    /// </summary>
    public required string ConnectionString { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the adapter applies its versioned schema scripts the first time it
    /// touches the database. When <see langword="false"/> (the default), it assumes the schema is
    /// already in place and refuses to run against a missing one. Leave it off in production and apply
    /// the schema as part of your deployment pipeline; turn it on for tests and local development where
    /// creating the schema on demand is convenient. Either way, a schema-version mismatch stops the
    /// workers before any job state can be corrupted.
    /// </summary>
    public bool AutoMigrate { get; init; }

    /// <summary>
    /// When <see langword="true"/> (the default), a cold-booting fleet survives the transient connection
    /// faults of a first-boot handshake storm: the migration retries with a bounded backoff instead of
    /// crashing startup. On Oracle the schema script is idempotent and concurrency-safe on its own - every
    /// object is created through a guarded block that swallows the duplicate-object and duplicate-key
    /// races, and Oracle serializes DDL on each object inside the database - so several nodes can apply it
    /// at once without a database lock. Setting this to <see langword="false"/> runs the script once with
    /// no transient retry; it is safe when the deployment already serializes migration itself (a single
    /// migration job, or one node booting before the rest). Applies only when <see cref="AutoMigrate"/> is
    /// on; has no effect otherwise.
    /// </summary>
    public bool CoordinateMigration { get; init; } = true;

    /// <summary>
    /// The Oracle schema (the owning user) that holds every BackWave table, index, and sequence. Defaults
    /// to <c>backwave</c>. Change it to place the job store under a schema that fits your naming
    /// conventions or to keep it clear of other applications' objects in a shared database. The same name
    /// must be used everywhere the store touches this database - auto-migrate, out-of-band migration, and
    /// every query all read it from here - so set it once and leave it fixed for the life of the data.
    /// Must be a valid unqualified identifier: 1-128 characters, a letter or underscore followed by
    /// letters, digits, or underscores; any other value is rejected when the store is created.
    /// </summary>
    public string SchemaName { get; init; } = "backwave";

    /// <summary>
    /// The size and batch limits the store enforces - maximum payload size, claim batch size, and the
    /// like. Defaults to <see cref="StoreBounds.Default"/>. Tighten or loosen these to match your
    /// database's capacity and your jobs' payload sizes.
    /// </summary>
    public StoreBounds Bounds { get; init; } = StoreBounds.Default;

    /// <summary>
    /// How much per-job history the store records: nothing, state transitions only, or transitions plus
    /// captured failure detail. Defaults to recording transitions and failure detail, so the dashboard
    /// timeline and error messages work out of the box. Changing this affects only what new transitions
    /// write - it is a configuration change, never a schema change. Lower it to reduce write volume when
    /// you do not need the history.
    /// </summary>
    public JobHistoryPolicy HistoryPolicy { get; init; } = JobHistoryPolicy.TransitionsAndFailureDetail;

    /// <summary>
    /// Optional logger factory. When supplied and <see cref="AutoMigrate"/> is on, the adapter records a
    /// schema-migration event at Information level after applying the schema on first use. Null (the
    /// default) disables that log with no allocation and does not otherwise affect the store.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// A test-only hook for fault-injection; leave <see langword="null"/> (the default) in
    /// production. When set, the adapter invokes it at a named point partway through a multi-step
    /// operation, inside the open transaction and before commit. Throwing from the hook aborts that
    /// transaction, which must roll back every effect - letting a test prove the store's writes are
    /// all-or-nothing. The argument is the named point reached (for example <c>"claim"</c> or
    /// <c>"enqueue"</c>).
    /// </summary>
    // Internal: this is a test-affordance for the conformance/unit suites, not a
    // consumer API; granted to the test assemblies via [InternalsVisibleTo].
    internal Func<string, CancellationToken, Task>? FaultHook { get; init; }
}
