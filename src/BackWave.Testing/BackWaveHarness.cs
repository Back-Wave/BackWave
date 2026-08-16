using System.Data.Common;
using System.Runtime.CompilerServices;
using BackWave.Core;
using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Monitor;
using BackWave.Storage;
using BackWave.Storage.InMemory;

namespace BackWave.Testing;

/// <summary>
/// A deterministic, in-memory test harness for your real jobs. It pairs an in-memory job
/// store with a virtual clock: time only moves when you advance it, and every instant where
/// something becomes due — a scheduled enqueue, a retry backoff, a lease expiry, a released
/// dependency, a recurring tick — is processed synchronously and in order before the clock
/// moves on. There is no wall clock, no sleeping, and no polling, so the same test input
/// always produces the same execution order and a simulated year of activity runs in
/// milliseconds.
/// </summary>
/// <remarks>
/// The workflow is always the same: build the harness from your job registry and DI services,
/// enqueue real jobs, advance virtual time, then assert through <see cref="Monitor"/>. Because
/// the clock is virtual, you test time-dependent behavior (retries, schedules, retention)
/// directly instead of waiting for real time to pass.
/// </remarks>
/// <example>
/// Enqueue a job, advance virtual time past its due instant, and assert the outcome:
/// <code>
/// // The job and handler your app ships.
/// [Job("send-invoice")]
/// public sealed record SendInvoice(string OrderId);
///
/// public sealed class SendInvoiceHandler(IInvoiceGateway gateway) : IJobHandler&lt;SendInvoice&gt;
/// {
///     public Task HandleAsync(SendInvoice job, JobContext context, CancellationToken cancellationToken)
///         =&gt; gateway.SendAsync(job.OrderId, cancellationToken);
/// }
///
/// // Build the harness from your registry and DI services.
/// var services = new ServiceCollection()
///     .AddSingleton&lt;IInvoiceGateway, FakeInvoiceGateway&gt;()
///     .AddTransient&lt;IJobHandler&lt;SendInvoice&gt;, SendInvoiceHandler&gt;()
///     .BuildServiceProvider();
/// var harness = new BackWaveHarness(BackWaveJobs.CreateRegistry(), services);
///
/// // Enqueue due in two days, then advance three — everything due in between runs.
/// var jobId = await harness.EnqueueAsync(new SendInvoice("order-42"), delay: TimeSpan.FromDays(2));
/// await harness.AdvanceAsync(TimeSpan.FromDays(3));
///
/// // Assert through the Monitor API — the same surface production observability uses.
/// var job = await harness.Monitor.GetJobAsync(jobId);
/// Assert.Equal(JobState.Succeeded, job!.State);
/// </code>
/// </example>
public sealed class BackWaveHarness
{
    private readonly DeterministicPump _pump;

    /// <summary>
    /// Creates a harness over a fresh in-memory store and a virtual clock that starts at
    /// <see cref="BackWaveHarnessOptions.StartTime"/>.
    /// </summary>
    /// <param name="registry">
    /// The job registry the harness runs against — typically the generated
    /// <c>BackWaveJobs.CreateRegistry()</c> your app ships. By default the harness serves every
    /// queue this registry declares (plus the queue named <c>"default"</c>).
    /// </param>
    /// <param name="services">
    /// The service provider used to resolve job handlers and their dependencies, exactly as the
    /// production runtime would. Register your real handlers (and any fakes they depend on) here.
    /// </param>
    /// <param name="options">
    /// Optional tuning. Null applies the defaults, which suit almost every test: time starts at a
    /// fixed instant, the default retry policy applies, and retention is off so assertions can see
    /// the whole job history.
    /// </param>
    public BackWaveHarness(JobRegistry registry, IServiceProvider services, BackWaveHarnessOptions? options = null)
    {
        options ??= new BackWaveHarnessOptions();
        Now = options.StartTime;
        Store = new InMemoryJobStore(options.Bounds);
        var clock = new VirtualClock(this);
        Client = new BackWaveClient(Store, registry, clock);
        Monitor = new BackWaveMonitor(Store);

        // Default policy: serve every Queue the registry declares, plus "default".
        var policy = options.Policy ?? new DispatchPolicy.Strict(
            [.. registry.Registrations.Select(r => r.Queue).Append("default").Distinct().Order()]);
        var driver = new NodeDriver(new NodeOptions
        {
            WorkerId = "test-node",
            Policy = policy,
            RetryPolicy = options.RetryPolicy,
            RetryOverrides = registry.RetryOverrides, // honor per-job-type [Retry] overrides as production does (0051)
            Retention = options.Retention,
        });
        // Thread Virtual Time into the pump too, so the messaging.process.duration histogram it emits at the
        // execute edge measures virtual elapsed rather than wall-clock. Without this the pump falls back
        // to TimeProvider.System and records nondeterministic wall-clock noise under the harness.
        _pump = new DeterministicPump(driver, Store, registry, services, clock);
    }

    /// <summary>
    /// The current virtual-time instant. It starts at the configured start time and moves only
    /// when you call <see cref="AdvanceAsync"/>; enqueues and schedules are stamped against it.
    /// </summary>
    public DateTimeOffset Now { get; private set; }

    /// <summary>
    /// The harness's in-memory job store. Use it for direct, low-level assertions (such as
    /// fetching a job record by id) when the higher-level <see cref="Monitor"/> surface does not
    /// expose what your test needs to check.
    /// </summary>
    public InMemoryJobStore Store { get; }

    /// <summary>
    /// The job client wired to the harness's store and virtual clock. The enqueue and schedule
    /// helpers on the harness delegate to it; reach for it directly only when you need a client
    /// overload the harness does not surface.
    /// </summary>
    public BackWaveClient Client { get; }

    /// <summary>
    /// The read-only observability surface for assertions — the same one production monitoring and
    /// the dashboard use. Query job state, queue depths, and schedules through it after advancing.
    /// </summary>
    public BackWaveMonitor Monitor { get; }

    /// <summary>
    /// Enqueues a job to run at the current virtual instant, or <paramref name="delay"/> later.
    /// </summary>
    /// <typeparam name="TJob">The registered job payload type.</typeparam>
    /// <param name="job">The job payload, serialized through its registration.</param>
    /// <param name="delay">How far past the current instant the job becomes due. Null or zero means due now.</param>
    /// <param name="queue">The queue to enqueue into. Null uses the job type's registered queue.</param>
    /// <param name="tags">Optional tags attached at enqueue. The job type's default tags are always added on top of these.</param>
    /// <param name="transaction">
    /// Optional. When supplied (typically from <see cref="BeginTransaction"/>), the job commits or
    /// rolls back atomically with the transaction — rolling back means the job never existed.
    /// </param>
    /// <returns>The new job's id, for asserting against later or for use as a dependency parent.</returns>
    /// <param name="callerFilePath">The source file of the enqueue call site. Supplied by the compiler; do not pass it.</param>
    /// <param name="callerMemberName">The member that made the enqueue call. Supplied by the compiler; do not pass it.</param>
    /// <param name="callerLineNumber">The source line of the enqueue call site. Supplied by the compiler; do not pass it.</param>
    /// <exception cref="InvalidOperationException">The payload type is not registered in the harness's registry.</exception>
    /// <exception cref="ArgumentException">The serialized payload exceeds the store's size bound.</exception>
    public ValueTask<Guid> EnqueueAsync<TJob>(
        TJob job, TimeSpan? delay = null, string? queue = null, JobTags? tags = null,
        DbTransaction? transaction = null,
        [CallerFilePath] string callerFilePath = "",
        [CallerMemberName] string callerMemberName = "",
        [CallerLineNumber] int callerLineNumber = 0)
        where TJob : notnull
        => Client.EnqueueAsync(
            job, Now + (delay ?? TimeSpan.Zero), queue, tags, transaction,
            callerFilePath: callerFilePath, callerMemberName: callerMemberName, callerLineNumber: callerLineNumber);

    /// <summary>
    /// Enqueues a job that waits for another job (<paramref name="parentId"/>) to reach a terminal
    /// state before it becomes eligible. By default it runs only if the parent succeeded; set
    /// <paramref name="mode"/> to release it once the parent reaches any terminal state.
    /// </summary>
    /// <typeparam name="TJob">The registered job payload type.</typeparam>
    /// <param name="job">The dependent job's payload, serialized through its registration.</param>
    /// <param name="parentId">The id of the job this one waits on — typically the return value of an earlier enqueue.</param>
    /// <param name="mode">Whether to release only on the parent's success, or once the parent reaches any terminal state.</param>
    /// <param name="queue">The queue to enqueue into. Null uses the job type's registered queue.</param>
    /// <returns>The new dependent job's id.</returns>
    /// <param name="callerFilePath">The source file of the enqueue call site. Supplied by the compiler; do not pass it.</param>
    /// <param name="callerMemberName">The member that made the enqueue call. Supplied by the compiler; do not pass it.</param>
    /// <param name="callerLineNumber">The source line of the enqueue call site. Supplied by the compiler; do not pass it.</param>
    /// <exception cref="InvalidOperationException">The payload type is not registered in the harness's registry.</exception>
    /// <exception cref="ArgumentException">No job exists with the given <paramref name="parentId"/>.</exception>
    public ValueTask<Guid> EnqueueDependencyAsync<TJob>(
        TJob job, Guid parentId, DependencyMode mode = DependencyMode.OnSuccess, string? queue = null,
        [CallerFilePath] string callerFilePath = "",
        [CallerMemberName] string callerMemberName = "",
        [CallerLineNumber] int callerLineNumber = 0)
        where TJob : notnull
        // No caller-passed instant: the client's injected VirtualClock stamps Virtual Time.
        => Client.EnqueueDependencyAsync(
            job, parentId, mode: mode, queue: queue,
            callerFilePath: callerFilePath, callerMemberName: callerMemberName, callerLineNumber: callerLineNumber);

    /// <summary>
    /// Defines or updates a recurring schedule. Its first tick is computed from the current virtual
    /// instant, so only future ticks mint; advancing virtual time runs each tick deterministically.
    /// </summary>
    /// <typeparam name="TJob">The registered job payload type.</typeparam>
    /// <param name="scheduleId">A stable id for the schedule. Re-using it updates the existing schedule in place.</param>
    /// <param name="cron">The recurrence, as a parsed cron expression.</param>
    /// <param name="template">The payload minted on each tick.</param>
    /// <param name="queue">The queue minted jobs land in. Null uses the job type's registered queue.</param>
    /// <param name="timeZone">An IANA time-zone id the cron is evaluated in. Null evaluates in UTC.</param>
    /// <param name="catchUp">Whether ticks missed while time was not advancing are skipped or replayed.</param>
    /// <param name="noOverlap">When true, a tick is suppressed while a prior minted run of this schedule is still active.</param>
    /// <returns>A task that completes once the schedule has been stored.</returns>
    /// <exception cref="InvalidOperationException">The payload type is not registered in the harness's registry.</exception>
    /// <exception cref="ArgumentException">The <paramref name="timeZone"/> id cannot be resolved on this host.</exception>
    public ValueTask UpsertRecurringAsync<TJob>(
        string scheduleId,
        CronExpression cron,
        TJob template,
        string? queue = null,
        string? timeZone = null,
        CatchUpPolicy catchUp = CatchUpPolicy.Skip,
        bool noOverlap = false)
        where TJob : notnull
        // No caller-passed instant: the client's injected VirtualClock stamps the Cursor.
        => Client.UpsertRecurringAsync(
            scheduleId, cron, template, queue: queue, timeZone: timeZone, catchUp: catchUp, noOverlap: noOverlap);

    // Workflows are a BackWave Pro feature: the build/enqueue surface lives in the BackWave.Pro
    // package as extension methods on the client. A test that exercises workflows references
    // BackWave.Pro and drives them through the harness's public Client (for example
    // harness.Client.Workflow(...).Then(...).EnqueueAsync()).

    /// <summary>
    /// Begins a transaction over the harness's store. Enqueues that pass this transaction commit or
    /// roll back with it; rolling back means those jobs never existed — they are not claimable and
    /// never appear in the monitor. Use it to test outbox-replacement code paths.
    /// </summary>
    /// <returns>A transaction to pass to enqueue calls and then commit or roll back. Dispose it when done.</returns>
    public InMemoryTransaction BeginTransaction() => Store.BeginTransaction();

    /// <summary>
    /// Runs everything that is due at the current instant without moving the clock — an "advance by
    /// zero". Useful to drain jobs you just enqueued due-now before asserting.
    /// </summary>
    /// <returns>A task that completes once all work due at the current instant has been processed.</returns>
    public Task RunDueAsync() => _pump.PumpAsync(Now);

    /// <summary>
    /// Advances virtual time by <paramref name="duration"/>, stopping at every instant in between
    /// where anything becomes due — a recurring tick, a retry backoff, a lease expiry, a released
    /// dependency — and fully processing it before moving on. Time-based effects (such as retention
    /// purges) at the final instant are applied too.
    /// </summary>
    /// <param name="duration">How far to advance. Must be zero or positive — virtual time only moves forward.</param>
    /// <returns>A task that completes once the clock has reached the target instant and all intervening work has run.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> is negative.</exception>
    public async Task AdvanceAsync(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Virtual Time only moves forward.");
        }

        await _pump.PumpAsync(Now).ConfigureAwait(false);
        var target = Now + duration;
        while (Store.NextActivityAfter(Now) is { } next && next <= target)
        {
            Now = next;
            await _pump.HeartbeatAsync(Now).ConfigureAwait(false); // cancellation round-trip
            await _pump.PumpAsync(Now).ConfigureAwait(false);
        }
        Now = target;
        await _pump.PumpAsync(Now).ConfigureAwait(false); // time-based effects (e.g. retention) at the target instant
    }

    /// <summary>The client's clock IS Virtual Time: enqueue-time stamps stay deterministic.</summary>
    private sealed class VirtualClock(BackWaveHarness harness) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => harness.Now;

        // Duration measurement (messaging.process.duration) reads GetTimestamp()/GetElapsedTime, not
        // GetUtcNow — and TimeProvider's defaults for those read the system stopwatch. Derive them from
        // Virtual Time too, at one-tick resolution, so the histogram records virtual elapsed instead of
        // wall-clock noise. A handler that consumes no Virtual Time (the harness executes inline at a
        // single instant) therefore records a deterministic zero.
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => harness.Now.UtcTicks;
    }
}

/// <summary>
/// Optional tuning for <see cref="BackWaveHarness"/>. Every value has a default that suits almost
/// every test; override only the one your test needs to exercise.
/// </summary>
public sealed record BackWaveHarnessOptions
{
    /// <summary>
    /// The instant virtual time starts at. A fixed instant keeps tests reproducible. Defaults to
    /// midnight UTC on 1 January 2026.
    /// </summary>
    public DateTimeOffset StartTime { get; init; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Which queues the harness serves. Null serves every queue the registry declares (plus the
    /// queue named <c>"default"</c>), which is what you want unless you are testing queue routing.
    /// </summary>
    public DispatchPolicy? Policy { get; init; }

    /// <summary>
    /// The retry policy — how many attempts a failing job gets and the backoff before each. Defaults
    /// to the framework's standard policy. Set it to test a specific retry or dead-letter behavior.
    /// </summary>
    public RetryPolicy RetryPolicy { get; init; } = RetryPolicy.Default;

    /// <summary>
    /// How long terminal jobs are kept before being purged. Off by default so assertions can see the
    /// whole job history; set a policy to test retention itself. The retention clock for a job starts
    /// at the instant it became terminal.
    /// </summary>
    public RetentionPolicy? Retention { get; init; }

    /// <summary>
    /// Size and paging limits the in-memory store enforces (such as maximum payload bytes and monitor
    /// page size). Null applies the store's defaults. Set it to test behavior at a bound.
    /// </summary>
    public StoreBounds? Bounds { get; init; }
}
