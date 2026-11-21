using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using BackWave.Benchmarks.Workload;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quartz;

namespace BackWave.Benchmarks.Targets;

/// <summary>How one enqueue is spelled in Quartz.</summary>
internal enum QuartzEnqueueShape
{
    /// <summary>
    /// Quartz 4's one-call <c>ScheduleJob&lt;TJob, TInput&gt;</c>: one durable job per type, one one-shot
    /// trigger per enqueue carrying the input.
    /// </summary>
    OneOff,

    /// <summary>A non-durable JobDetail plus a one-shot SimpleTrigger per enqueue, both deleted on completion.</summary>
    JobPerEnqueue,
}

/// <summary>The Quartz settings one run uses, each overridable by a <c>BACKWAVE_QUARTZ_*</c> variable.</summary>
internal sealed record QuartzTuning(
    int MaxConcurrency,
    int MaxBatchSize,
    TimeSpan FireAheadWindow,
    TimeSpan IdleWaitTime,
    bool Clustered,
    bool AcquireTriggersWithinLock,
    bool LockOnInsert,
    bool LockFreeFiringRounds,
    QuartzEnqueueShape Shape)
{
    public const string ClusteredEnvVar = "BACKWAVE_QUARTZ_CLUSTERED";
    public const string MaxBatchSizeEnvVar = "BACKWAVE_QUARTZ_MAX_BATCH_SIZE";
    public const string FireAheadMsEnvVar = "BACKWAVE_QUARTZ_FIRE_AHEAD_MS";
    public const string IdleWaitMsEnvVar = "BACKWAVE_QUARTZ_IDLE_WAIT_MS";
    public const string AcquireWithinLockEnvVar = "BACKWAVE_QUARTZ_ACQUIRE_WITHIN_LOCK";
    public const string LockOnInsertEnvVar = "BACKWAVE_QUARTZ_LOCK_ON_INSERT";
    public const string LockFreeFiringEnvVar = "BACKWAVE_QUARTZ_LOCK_FREE_FIRING";
    public const string ShapeEnvVar = "BACKWAVE_QUARTZ_ENQUEUE_SHAPE";

    public static QuartzTuning FromEnvironment(int workerCount) => new(
        MaxConcurrency: workerCount,
        MaxBatchSize: ReadInt(MaxBatchSizeEnvVar, workerCount),
        FireAheadWindow: TimeSpan.FromMilliseconds(ReadInt(FireAheadMsEnvVar, 0)),
        IdleWaitTime: TimeSpan.FromMilliseconds(ReadInt(IdleWaitMsEnvVar, 1000)),
        Clustered: ReadBool(ClusteredEnvVar, true),
        AcquireTriggersWithinLock: ReadBool(AcquireWithinLockEnvVar, false),
        LockOnInsert: ReadBool(LockOnInsertEnvVar, false),
        LockFreeFiringRounds: ReadBool(LockFreeFiringEnvVar, true),
        Shape: System.Environment.GetEnvironmentVariable(ShapeEnvVar) switch
        {
            null or "" or "one-off" => QuartzEnqueueShape.OneOff,
            "job-per-enqueue" => QuartzEnqueueShape.JobPerEnqueue,
            var other => throw new ArgumentException($"{ShapeEnvVar}='{other}': expected 'one-off' or 'job-per-enqueue'."),
        });

    private static int ReadInt(string name, int fallback)
        => int.TryParse(System.Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool ReadBool(string name, bool fallback)
        => bool.TryParse(System.Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
}

/// <summary>
/// Quartz.NET over Postgres through its ADO.NET job store, behind the same fairness seam as Hangfire. An
/// enqueue is one fire-now one-shot trigger; a fresh scheduler is built per run, started in
/// <see cref="WarmAsync"/> and shut down in <see cref="CooldownAsync"/>, so its startup and shutdown stay out
/// of the timed window. Drain is read from the store: the run is over when no trigger and no fired-trigger row
/// is left, which Quartz reaches only after each firing's completion transaction commits.
/// </summary>
public sealed class QuartzPostgresTarget : IBenchmarkTarget
{
    /// <summary>Environment variable holding the Quartz Postgres connection string (DSN).</summary>
    public const string ConnectionStringEnvVar = "BACKWAVE_QUARTZ_POSTGRES_DSN";

    private const string QuartzVersion = "4.7.3";
    private const string SchedulerName = "backwave-bench";

    private const string DefaultConnectionString =
        "Host=localhost;Port=5499;Username=backwave;Password=backwave;Database=quartz_test";

    private readonly string _connectionString;
    private readonly QuartzTuning _tuning;
    private readonly QuartzCompletionLog _completions = new();
    private readonly List<TimeSpan> _enqueueLatencies = [];
    private readonly ConcurrentDictionary<string, DateTimeOffset> _enqueuedAt = new();
    private readonly TimeSpan _drainPollInterval = TimeSpan.FromMilliseconds(10);
    private readonly TimeSpan _connectionSampleInterval = TimeSpan.FromMilliseconds(20);

    private NpgsqlDataSource? _dataSource;
    private StandaloneSchedulerFactory? _factory;
    private IScheduler? _scheduler;
    private int _sequence;
    private ResourceMetrics _lastResources = ResourceMetrics.None;
    private TimeSpan? _producerElapsed;

    /// <summary>Creates the target using the DSN from the environment, or the local docker-compose default.</summary>
    public QuartzPostgresTarget()
        : this(System.Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString)
    {
    }

    /// <summary>Creates the target against an explicit connection string.</summary>
    public QuartzPostgresTarget(string connectionString)
    {
        _connectionString = connectionString;
        _tuning = QuartzTuning.FromEnvironment(BackWaveTarget.WorkerOptions().PoolSize);
    }

    /// <inheritdoc/>
    public string Name => "Quartz/Postgres";

    /// <inheritdoc/>
    public string Engine => "PostgreSQL";

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> TuningDials => new Dictionary<string, string>
    {
        ["worker-pool-size"] = _tuning.MaxConcurrency.ToString(CultureInfo.InvariantCulture),
        ["db-connection-pool-size"] = "100 (ADO.NET default)",
        ["retry-policy"] = "none: the job never throws and Quartz has no automatic retry",
        ["max-batch-size"] = $"{_tuning.MaxBatchSize} (triggers acquired per round; Quartz's automatic value is the pool size)",
        ["fire-ahead-window"] = $"{_tuning.FireAheadWindow.TotalMilliseconds:0}ms",
        ["idle-wait-time"] = $"{_tuning.IdleWaitTime.TotalMilliseconds:0}ms (Quartz minimum is 1s; an in-process schedule wakes the loop at once)",
        ["clustered"] = _tuning.Clustered
            ? "true (like-for-like: every other system is a multi-node-safe DB queue)"
            : "false (DIAGNOSTIC: single-node store, not multi-node safe)",
        ["acquire-triggers-within-lock"] = _tuning.AcquireTriggersWithinLock.ToString(),
        ["lock-on-insert"] = _tuning.LockOnInsert
            ? "true (Quartz default: every enqueue queues on the QRTZ_LOCKS TRIGGER_ACCESS row)"
            : "false (tuned: Quartz documents it as a large insert speedup; unique trigger keys leave nothing for the lock to arbitrate)",
        ["lock-free-firing-rounds"] = _tuning.LockFreeFiringRounds.ToString(),
        ["misfire"] = "60s threshold (default), DefaultMisfireHandling.IgnoreMisfires (MISFIRE_INSTR -1): a backlog older than the threshold stays acquirable and is skipped by the misfire scan",
        ["request-recovery"] = "true (a firing cut off by a crash re-runs, like a reclaimed lease)",
        ["enqueue-shape"] = _tuning.Shape == QuartzEnqueueShape.OneOff
            ? "ScheduleJob<TJob, TInput> with zero delay: one durable job, one fire-now one-shot SimpleTrigger per enqueue"
            : "JobDetail + one-shot SimpleTrigger (StartNow) per enqueue, both deleted on completion",
        ["claim-strategy"] = "one scheduler thread per node acquires and fires up to max-batch-size due triggers per round, then waits for free pool threads (architectural)",
        ["serialization"] = "System.Text.Json input stored on the trigger (architectural, as shipped)",
        ["completion-timestamp"] = "stamped by the job on return, before Quartz's completion commit (Quartz deletes completed triggers)",
        ["drain-detection"] = "no row left in qrtz_triggers or qrtz_fired_triggers",
        ["schema"] = $"Quartz {QuartzVersion} SchemaProvisioning.CreateIfMissing (embedded create_postgres.sql); the catalog it creates matches database/tables/tables_postgres.sql at tag v{QuartzVersion}",
        ["quartz-version"] = QuartzVersion,
    };

    /// <inheritdoc/>
    public async Task<string> SetupAsync(CancellationToken cancellationToken)
    {
        PostgresDatabase.EnsureExists(_connectionString);
        _dataSource = NpgsqlDataSource.Create(_connectionString);

        // The store provisions its schema when a scheduler starts, so start one with nothing to fire.
        var factory = BuildFactory();
        var scheduler = await factory.GetScheduler(cancellationToken).ConfigureAwait(false);
        await scheduler.Start(cancellationToken).ConfigureAwait(false);
        await scheduler.Shutdown(waitForJobsToComplete: true, cancellationToken).ConfigureAwait(false);
        await factory.DisposeAsync().ConfigureAwait(false);

        await using var command = _dataSource.CreateCommand("SHOW server_version");
        var version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return $"PostgreSQL {version}";
    }

    /// <inheritdoc/>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        _enqueueLatencies.Clear();
        _enqueuedAt.Clear();
        _completions.Clear();
        _lastResources = ResourceMetrics.None;
        _producerElapsed = null;

        await using (var command = DataSource.CreateCommand(
            "DO $$ BEGIN EXECUTE (SELECT 'TRUNCATE ' || string_agg(quote_ident(tablename), ', ') " +
            "FROM pg_tables WHERE schemaname = current_schema() AND tablename LIKE 'qrtz\\_%'); END $$"))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // A scheduler cannot restart after shutdown, so each run gets a fresh one. It stays in standby until
        // WarmAsync, which lets drain preload schedule the whole backlog before anything fires.
        _factory = BuildFactory();
        _scheduler = await _factory.GetScheduler(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task PreloadAsync(WorkloadSpec spec, CancellationToken cancellationToken)
    {
        if (spec.Arrival != ArrivalMode.Drain)
        {
            return;
        }

        await EnqueueAllAsync(spec, paceRatePerSecond: 0, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task WarmAsync(WorkloadSpec spec, CancellationToken cancellationToken)
        => Scheduler.Start(cancellationToken).AsTask();

    /// <inheritdoc/>
    public async Task ExecuteAsync(WorkloadSpec spec, CancellationToken cancellationToken)
    {
        var cpuStart = ProcessCpuTime();
        var wall = Stopwatch.StartNew();

        using var samplerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connectionSampler = SampleConnectionsAsync(samplerCts.Token);

        if (spec.Arrival == ArrivalMode.Sustained)
        {
            await EnqueueAllAsync(spec, spec.SustainedRatePerSecond, cancellationToken).ConfigureAwait(false);
        }

        await WaitForDrainAsync(cancellationToken).ConfigureAwait(false);

        wall.Stop();
        samplerCts.Cancel();
        var peakConnections = await connectionSampler.ConfigureAwait(false);

        var cpuSeconds = (ProcessCpuTime() - cpuStart).TotalSeconds;
        var wallSeconds = wall.Elapsed.TotalSeconds;
        var cpuPercent = wallSeconds > 0
            ? cpuSeconds / (wallSeconds * System.Environment.ProcessorCount) * 100d
            : 0d;

        _lastResources = new ResourceMetrics(peakConnections, cpuPercent, default);
    }

    /// <inheritdoc/>
    public async Task CooldownAsync(CancellationToken cancellationToken)
    {
        if (_scheduler is not null)
        {
            await _scheduler.Shutdown(waitForJobsToComplete: true, cancellationToken).ConfigureAwait(false);
            _scheduler = null;
        }

        if (_factory is not null)
        {
            await _factory.DisposeAsync().ConfigureAwait(false);
            _factory = null;
        }
    }

    /// <inheritdoc/>
    public Task<TargetSamples> CollectSamplesAsync(CancellationToken cancellationToken)
    {
        var completed = _completions.CompletedAt;
        if (completed.Count != _enqueuedAt.Count || _completions.Executions != _enqueuedAt.Count)
        {
            throw new InvalidOperationException(
                $"Quartz drained {_enqueuedAt.Count} enqueues with {completed.Count} distinct triggers completed " +
                $"over {_completions.Executions} handler runs.");
        }

        var endToEnd = new List<TimeSpan>(_enqueuedAt.Count);
        foreach (var (trigger, enqueuedAt) in _enqueuedAt)
        {
            endToEnd.Add(completed[trigger] - enqueuedAt);
        }

        var instants = completed.Values;
        return Task.FromResult(new TargetSamples(_enqueueLatencies.ToArray(), endToEnd, _lastResources,
            new WorkEvidence(_completions.Executions, instants.Min(), instants.Max(), _producerElapsed)));
    }

    /// <inheritdoc/>
    public Task TeardownAsync(CancellationToken cancellationToken) => CooldownAsync(cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync().ConfigureAwait(false);
        }

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    private NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException("SetupAsync has not run.");

    private IScheduler Scheduler => _scheduler ?? throw new InvalidOperationException("ResetAsync has not run.");

    private StandaloneSchedulerFactory BuildFactory() => QuartzSchedulerBuilder.Create(quartz =>
    {
        quartz.Services.AddSingleton(_completions);
        quartz.ConfigureScheduler(scheduler =>
        {
            scheduler.InstanceName = SchedulerName;
            scheduler.MaxBatchSize = _tuning.MaxBatchSize;
            scheduler.BatchTriggerAcquisitionFireAheadTimeWindow = _tuning.FireAheadWindow;
            scheduler.IdleWaitTime = _tuning.IdleWaitTime;
            scheduler.DefaultMisfireHandling = DefaultMisfireHandling.IgnoreMisfires;
        });
        quartz.UseDefaultThreadPool(_tuning.MaxConcurrency);
        quartz.UsePersistentStore(store =>
        {
            store.UsePostgres(_connectionString);
            store.UseSystemTextJsonSerializer();
            store.ProvisionSchema();
            if (_tuning.Clustered)
            {
                store.UseClustering();
            }

            store.ConfigureStore(options =>
            {
                options.AcquireTriggersWithinLock = _tuning.AcquireTriggersWithinLock;
                options.LockOnInsert = _tuning.LockOnInsert;
                options.LockFreeFiringRounds = _tuning.LockFreeFiringRounds;
            });
        });
    }).Build();

    private async Task EnqueueAllAsync(WorkloadSpec spec, double paceRatePerSecond, CancellationToken cancellationToken)
    {
        var scheduler = Scheduler;
        var latencies = await ParallelProducer.RunAsync(
            spec, paceRatePerSecond,
            async (job, token) =>
            {
                var input = new QuartzBenchInput(job.Payload, job.DelayMs);
                var trigger = await ScheduleAsync(scheduler, input, token).ConfigureAwait(false);
                _enqueuedAt[trigger] = DateTimeOffset.UtcNow;
            },
            cancellationToken).ConfigureAwait(false);
        _enqueueLatencies.AddRange(latencies);
        _producerElapsed = ParallelProducer.LastElapsed;
    }

    private async ValueTask<string> ScheduleAsync(IScheduler scheduler, QuartzBenchInput input, CancellationToken cancellationToken)
    {
        if (_tuning.Shape == QuartzEnqueueShape.OneOff)
        {
            var scheduled = await scheduler.ScheduleJob<QuartzBenchmarkJob, QuartzBenchInput>(
                input,
                TimeSpan.Zero,
                new OneOffJobOptions { RequestRecovery = true },
                cancellationToken).ConfigureAwait(false);
            return scheduled.TriggerKey.Name;
        }

        var name = Interlocked.Increment(ref _sequence).ToString(CultureInfo.InvariantCulture);
        var detail = JobBuilder.Create<QuartzBenchmarkJob>()
            .WithIdentity(name, WorkloadSpec.BenchQueue)
            .UsingInput(input)
            .RequestRecovery()
            .Build();
        var trigger = TriggerBuilder.Create()
            .WithIdentity(name, WorkloadSpec.BenchQueue)
            .StartNow()
            .Build();
        await scheduler.ScheduleJob(detail, trigger, new ScheduleJobOptions(), cancellationToken).ConfigureAwait(false);
        return name;
    }

    private async Task WaitForDrainAsync(CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM qrtz_triggers) OR EXISTS (SELECT 1 FROM qrtz_fired_triggers)");
        while (true)
        {
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is false)
            {
                return;
            }

            await Task.Delay(_drainPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<int> SampleConnectionsAsync(CancellationToken cancellationToken)
    {
        var peak = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var command = DataSource.CreateCommand(
                    "SELECT count(*) FROM pg_stat_activity " +
                    "WHERE datname = current_database() AND pid <> pg_backend_pid()");
                var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                peak = Math.Max(peak, result is long count ? (int)count : 0);
                await Task.Delay(_connectionSampleInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return peak;
    }

    private static TimeSpan ProcessCpuTime()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }
}
