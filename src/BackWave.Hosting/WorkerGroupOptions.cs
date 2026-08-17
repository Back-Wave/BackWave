using BackWave.Core;

namespace BackWave.Hosting;

/// <summary>
/// Configuration for a single worker group: which queues it serves, how it shares effort across them,
/// and how hard it runs. Each group registered with
/// <see cref="BackWaveBuilder.AddWorkerGroup(WorkerGroupOptions)"/> runs one or more background pumps —
/// its <see cref="Pumps"/> count.
/// </summary>
public sealed record WorkerGroupOptions
{
    /// <summary>
    /// The group's name. Must be unique within a single <c>AddBackWave</c> registration and appears in
    /// health, metrics, and logs to identify this group.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Which queues this group serves and how it shares effort across them — strict priority order or
    /// weighted fair-sharing.
    /// </summary>
    public required DispatchPolicy Policy { get; init; }

    /// <summary>
    /// The maximum number of jobs this group runs concurrently per node. Polling pauses while the pool
    /// is full and resumes as jobs finish. Defaults to 20.
    /// </summary>
    // A flat constant, deliberately NOT CPU-scaled (ADR 0036): the async pump has no
    // thread-per-job to starve, and a machine-independent admission bound protects the
    // low-connection identity. Consumers tune per-deployment via this knob.
    public int PoolSize { get; init; } = 20;

    /// <summary>
    /// How many independent pump loops this group runs in one process — its fetch-loop parallelism.
    /// Each pump is a self-contained claim → execute → report loop with its own <see cref="PoolSize"/>
    /// pool and its own claim stream, claiming under a distinct worker identity so the database hands
    /// each pump a disjoint set of jobs with no extra coordination. Defaults to <c>1</c>, which is
    /// exactly the single-loop behaviour; raising it is the lever for single-node throughput once one
    /// pump is bound by store round-trip latency rather than by concurrency or CPU. Must be at least 1.
    /// <para>
    /// Unlike <see cref="PoolSize"/>, whose idle slots cost nothing, each pump draws database
    /// connections whether busy or not — budget roughly <b>2–3 connections per pump</b> when sizing the
    /// client connection pool and the database's connection limit. A group with <c>Pumps = 4</c>, for
    /// example, draws on the order of 8–12 connections at steady state. Leave this at 1 unless a node is
    /// throughput-bound and has connection headroom to spare.
    /// </para>
    /// </summary>
    public int Pumps { get; init; } = 1;

    /// <summary>
    /// The maximum number of jobs claimed in a single poll. Capped at <see cref="PoolSize"/> in
    /// practice, since a group never claims more than it can run. Defaults to 32.
    /// </summary>
    public int MaxClaimBatch { get; init; } = 32;

    /// <summary>
    /// The maximum number of completed-job outcomes the group buffers before writing them to the store as
    /// one batched report, keeping the writer single-threaded so throughput rises without opening more
    /// database connections. A poll or heartbeat tick, or the group going idle, flushes any partial buffer
    /// first, so this never adds latency to a lone result. When <see langword="null"/>, defaults to
    /// <see cref="MaxClaimBatch"/>.
    /// </summary>
    public int? MaxOutcomeBatch { get; init; }

    // Batched, bounded polling is the sole correctness mechanism (ADR-0005); a wake-up hint only ever
    // brings a poll forward, never replaces it.
    /// <summary>
    /// How often the group polls for new work. A shorter interval lowers latency at the cost of more
    /// frequent store queries. Defaults to 1 second.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest the group waits between polls while it is idle. When set greater than
    /// <see cref="PollInterval"/>, an idle group backs off from <see cref="PollInterval"/> toward this
    /// ceiling - sleeping until the next scheduled job comes due, when the store reports it - instead of
    /// polling at the fixed rate, and returns to <see cref="PollInterval"/> the moment a poll claims work
    /// or finds work due now. A Wake-Up Hint triggers a poll but does not itself reset the floor. This
    /// lowers the store query load of an idle fleet.
    /// <para>
    /// Polling stays the sole correctness mechanism: the backoff is bounded by this ceiling, so
    /// the worst-case latency for newly enqueued work is at most this value even with no hint. On a store
    /// with a Wake-Up Hint channel (PostgreSQL, SQLite) an enqueue still wakes the group in milliseconds,
    /// so the ceiling only affects the rare lost-hint case. On a polling-only store (SQL Server) it is the
    /// direct latency ceiling for newly enqueued work.
    /// </para>
    /// <para>
    /// Defaults to <see cref="TimeSpan.Zero"/>, which disables idle backoff: the group polls at the fixed
    /// <see cref="PollInterval"/>, exactly as before. Any value at or below <see cref="PollInterval"/> is
    /// treated the same way.
    /// </para>
    /// </summary>
    public TimeSpan MaxPollInterval { get; init; }

    /// <summary>
    /// How often background maintenance runs — expiring lapsed leases, loading and minting recurring
    /// schedules, and purging retained history — separately from claim polling. Set it slower than
    /// <see cref="PollInterval"/> so claim polls stay a cheap fast path. A missed sweep only delays
    /// maintenance by this much; it never affects correctness. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a claimed job's lease is held before it lapses. If the worker crashes or stalls, the
    /// job becomes claimable by another node after this window. The group heartbeats to renew the
    /// lease while a job runs. Defaults to 60 seconds.
    /// </summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often the group renews the leases of its in-flight jobs. When <see langword="null"/>,
    /// defaults to one third of <see cref="LeaseDuration"/>, so a lease survives a couple of missed
    /// heartbeats before lapsing.
    /// </summary>
    public TimeSpan? HeartbeatInterval { get; init; }

    /// <summary>
    /// How failed jobs are retried: the backoff schedule and the attempt ceiling after which a job is
    /// dead-lettered. Defaults to the standard retry policy.
    /// </summary>
    public RetryPolicy RetryPolicy { get; init; } = RetryPolicy.Default;

    /// <summary>
    /// How terminal jobs are kept and then purged from the store. On by default; set to
    /// <see langword="null"/> to disable retention sweeping entirely.
    /// </summary>
    public RetentionPolicy? Retention { get; init; } = RetentionPolicy.Default;
}
