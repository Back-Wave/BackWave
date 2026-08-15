using System.Diagnostics;
using BackWave;
using BackWave.Benchmarks.Workload;
using BackWave.Core;
using BackWave.Generated;
using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackWave.Benchmarks.Targets;

/// <summary>
/// The BackWave side of the fairness seam, shared across every Storage Adapter. It owns all the
/// adapter-independent machinery — building the registry-driven enqueue client, pacing the preload,
/// running a fresh real <c>WorkerGroupService</c> pump per run, detecting drain, and collecting per-job
/// latency from the store — so a concrete adapter target only supplies how to build/migrate/reset its
/// store and read its engine version. The measurement (the timed window, the math) lives in the
/// orchestrator, never here (ADR 0027 §5).
/// </summary>
public abstract class BackWaveTarget : IBenchmarkTarget
{
    private readonly JobRegistry _registry = BackWaveJobs.CreateRegistry();
    private IJobStore? _store;
    private BackWaveClient? _client;
    private IHost? _runHost;
    private WorkerGroupOptions? _runWorkerOptions;
    private int _runWorkerGroupCount = 1;

    private readonly List<TimeSpan> _enqueueLatencies = [];
    private readonly TimeSpan _drainPollInterval = TimeSpan.FromMilliseconds(10);
    private readonly TimeSpan _connectionSampleInterval = TimeSpan.FromMilliseconds(20);
    private ResourceMetrics _lastResources = ResourceMetrics.None;

    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public abstract string Engine { get; }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> TuningDials
    {
        get
        {
            var workers = _runWorkerOptions ?? WorkerOptions();
            return new Dictionary<string, string>
            {
                // Neutralized config — matched against the competitor so the comparison is honest.
                ["worker-pool-size"] = workers.PoolSize.ToString(),
                ["worker-groups"] = _runWorkerGroupCount.ToString(),
                ["poll-interval"] = $"{workers.PollInterval.TotalMilliseconds:0}ms",
                ["db-connection-pool-size"] = "100 (ADO.NET default)",
                ["retry-policy"] = "no retries exercised (100% happy-path workload)",
                // Surfaced architecture — the product, won fairly, not hidden.
                ["claim-strategy"] = $"batch-claim up to {workers.MaxClaimBatch} per poll, {ClaimStrategy} (architectural)",
                ["serialization"] = "source-generated, no reflection (architectural)",
            };
        }
    }

    /// <summary>The live store, valid after <see cref="SetupAsync"/>. Adapter subclasses may read it.</summary>
    protected IJobStore Store => _store ?? throw new InvalidOperationException("SetupAsync has not run.");

    /// <summary>
    /// The adapter's contended-claim dialect, surfaced verbatim in <see cref="TuningDials"/> as an
    /// architectural difference (e.g. "FOR UPDATE SKIP LOCKED" on Postgres, "UPDLOCK, READPAST" on SQL Server).
    /// </summary>
    protected abstract string ClaimStrategy { get; }

    // ── Adapter-specific hooks ───────────────────────────────────────────────

    /// <summary>Applies the adapter's schema migration to the target database.</summary>
    protected abstract Task MigrateAsync(CancellationToken cancellationToken);

    /// <summary>Constructs the live <see cref="IJobStore"/> for this adapter.</summary>
    protected abstract IJobStore CreateStore();

    /// <summary>Reads the storage engine's version string (e.g. from <c>SELECT version()</c>).</summary>
    protected abstract Task<string> ReadEngineVersionAsync(CancellationToken cancellationToken);

    /// <summary>Empties every benchmark table so the next run starts clean.</summary>
    protected abstract Task ResetStoreAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Counts the DB connections this process currently holds against the target database, used by the
    /// background sampler to track peak concurrency under load. The default returns 0 — an adapter that can
    /// observe its own connection count (e.g. via a server-side activity view) overrides this. Computed the
    /// same way for any target so it stays a fair cross-system metric.
    /// </summary>
    protected virtual Task<int> SampleConnectionCountAsync(CancellationToken cancellationToken)
        => Task.FromResult(0);

    // ── IBenchmarkTarget ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<string> SetupAsync(CancellationToken cancellationToken)
    {
        await MigrateAsync(cancellationToken).ConfigureAwait(false);
        _store = CreateStore();
        _client = new BackWaveClient(_store, _registry);
        return await ReadEngineVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task ResetAsync(CancellationToken cancellationToken)
    {
        _enqueueLatencies.Clear();
        _lastResources = ResourceMetrics.None;
        return ResetStoreAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task PreloadAsync(WorkloadSpec spec, CancellationToken cancellationToken)
    {
        // Drain headline: the whole backlog is committed before the timed window opens. Sustained mode
        // defers all production into ExecuteAsync, so preload is a no-op there.
        if (spec.Arrival != ArrivalMode.Drain)
        {
            return;
        }

        await EnqueueAllAsync(spec, paceRatePerSecond: 0, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task WarmAsync(WorkloadSpec spec, CancellationToken cancellationToken)
    {
        // Build and start the pump BEFORE the timed window so host build + worker-group startup never land in
        // the throughput denominator — the headline isolates the engine (claim/dispatch/outcome-write/lease
        // churn), not process spin-up (ADR 0027 §2). The window, opened by the orchestrator right after this
        // returns, sees the pump already claiming.
        _runHost = BuildPumpHost(spec);
        return _runHost.StartAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ExecuteAsync(WorkloadSpec spec, CancellationToken cancellationToken)
    {
        // Resource-cost capture wraps the timed window (ADR 0027 §4) — and ONLY the window: the pump is
        // already running (WarmAsync) and is torn down afterward (CooldownAsync), so CPU%, peak connections,
        // and the alloc/GC deltas reflect steady-state work, not spin-up or graceful shutdown. CPU and GC are
        // before/after deltas; DB connections are sampled by a background task that tracks the peak. The
        // wall-clock used for CPU% is captured here too so the ratio is self-consistent.
        var cpuStart = ProcessCpuTime();
        var allocatedStart = GC.GetTotalAllocatedBytes();
        var gen0Start = GC.CollectionCount(0);
        var gen1Start = GC.CollectionCount(1);
        var gen2Start = GC.CollectionCount(2);
        var wall = Stopwatch.StartNew();

        using var samplerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connectionSampler = SampleConnectionsAsync(samplerCts.Token);

        // Sustained: producers feed the queue at the target rate while the pump claims concurrently, exposing
        // the enqueue-vs-claim contention. Drain: the backlog is already loaded; just empty it.
        var producer = spec.Arrival == ArrivalMode.Sustained
            ? EnqueueAllAsync(spec, spec.SustainedRatePerSecond, cancellationToken)
            : Task.CompletedTask;

        await producer.ConfigureAwait(false);
        await WaitForDrainAsync(cancellationToken).ConfigureAwait(false);

        wall.Stop();
        samplerCts.Cancel();
        var peakConnections = await connectionSampler.ConfigureAwait(false);

        var cpuSeconds = (ProcessCpuTime() - cpuStart).TotalSeconds;
        var wallSeconds = wall.Elapsed.TotalSeconds;
        var cpuPercent = wallSeconds > 0
            ? cpuSeconds / (wallSeconds * System.Environment.ProcessorCount) * 100d
            : 0d;

        var allocatedBytes = GC.GetTotalAllocatedBytes() - allocatedStart;
        var allocatedPerJob = spec.JobCount > 0 ? allocatedBytes / spec.JobCount : 0L;

        _lastResources = new ResourceMetrics(
            peakConnections,
            cpuPercent,
            new InternalAllocationMetrics(
                allocatedPerJob,
                GC.CollectionCount(0) - gen0Start,
                GC.CollectionCount(1) - gen1Start,
                GC.CollectionCount(2) - gen2Start));
    }

    /// <inheritdoc/>
    public async Task CooldownAsync(CancellationToken cancellationToken)
    {
        // Graceful shutdown happens AFTER the window closes — drain already confirmed every job terminal, so
        // StopAsync/Dispose is pure dead time that must not inflate the throughput denominator (ADR 0027 §2).
        if (_runHost is null)
        {
            return;
        }

        await _runHost.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_runHost is IAsyncDisposable asyncHost)
        {
            await asyncHost.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            _runHost.Dispose();
        }

        _runHost = null;
    }

    /// <inheritdoc/>
    public async Task<TargetSamples> CollectSamplesAsync(CancellationToken cancellationToken)
    {
        var endToEnd = await CollectEndToEndLatenciesAsync(cancellationToken).ConfigureAwait(false);
        return new TargetSamples(_enqueueLatencies.ToArray(), endToEnd, _lastResources);
    }

    /// <inheritdoc/>
    public virtual Task TeardownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public virtual async ValueTask DisposeAsync()
    {
        if (_runHost is not null)
        {
            await _runHost.StopAsync().ConfigureAwait(false);
            _runHost.Dispose();
        }

        if (_store is IAsyncDisposable asyncStore)
        {
            await asyncStore.DisposeAsync().ConfigureAwait(false);
        }
        else if (_store is IDisposable disposableStore)
        {
            disposableStore.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private async Task EnqueueAllAsync(WorkloadSpec spec, double paceRatePerSecond, CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException("SetupAsync has not run.");
        // The shared fan-out runs spec.ProducerCount producers so the arrival side can outpace the pump in
        // sustained mode; the enqueue call itself is BackWave's async client (ADR 0027 §5).
        var latencies = await ParallelProducer.RunAsync(
            spec, paceRatePerSecond,
            async (job, ct) => await client.EnqueueAsync(job, DateTimeOffset.UtcNow, cancellationToken: ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        _enqueueLatencies.AddRange(latencies);
    }

    private async Task<int> SampleConnectionsAsync(CancellationToken cancellationToken)
    {
        // Polls the adapter's connection count until the window closes (cancellation), keeping the peak.
        var peak = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                peak = Math.Max(peak, await SampleConnectionCountAsync(cancellationToken).ConfigureAwait(false));
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

    private async Task WaitForDrainAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var counts = await Store.CountJobsAsync(cancellationToken).ConfigureAwait(false);
            var pending = counts.Where(c => !c.State.IsTerminal()).Sum(c => c.Count);
            if (pending == 0)
            {
                return;
            }

            await Task.Delay(_drainPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<TimeSpan>> CollectEndToEndLatenciesAsync(CancellationToken cancellationToken)
    {
        // End-to-end = enqueue→terminal, taken from the store's own timestamps so both endpoints share one
        // clock: jobs are enqueued due-now, so DueTime is the enqueue instant and TerminalAt the completion.
        var latencies = new List<TimeSpan>();
        long? cursor = null;
        while (true)
        {
            var page = await Store.ListJobsAsync(
                new JobQuery { AfterSequence = cursor, MaxResults = 200 },
                cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var job in page)
            {
                if (job.TerminalAt is { } terminal)
                {
                    latencies.Add(terminal - job.DueTime);
                }
            }

            cursor = page[^1].Sequence;
        }

        return latencies;
    }

    private IHost BuildPumpHost(WorkloadSpec spec)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // The pump host gets its OWN store instance (a node's own connection pool): the DI container
        // disposes the singletons it owns when the per-run host is torn down, so it must not own the
        // harness's long-lived store used for enqueue/reset/collect. Both point at the same database.
        // Remember the options this run actually used so TuningDials reports the swept pool size, not the default.
        _runWorkerOptions = WorkerOptions(spec.PumpPoolSize);
        _runWorkerGroupCount = Math.Max(1, spec.WorkerGroupCount);
        builder.Services.AddBackWave(backwave =>
        {
            backwave
                .UseStore(_ => CreateStore())
                .UseJobs(BackWaveJobs.Module);
            // Run N independent pumps in this process, all serving the same queue, to measure in-process
            // fan-out. Names must be unique; SKIP LOCKED hands each pump a disjoint slice of the backlog.
            for (var i = 0; i < _runWorkerGroupCount; i++)
            {
                backwave.AddWorkerGroup(_runWorkerOptions with { Name = $"benchmark-{i}" });
            }
        });

        return builder.Build();
    }

    /// <summary>
    /// The pump tuning for a run. Aggressive by design (small poll interval, wide pool, large claim batch)
    /// so the headline measures the engine, not a conservative default — the "tune to best" rule (ADR 0027 §5).
    /// Internal so the scale-out Node subprocess pumps with the same tuning as the single-run headline
    /// (bench-0141), keeping the curve comparable to the headline number.
    /// </summary>
    internal static WorkerGroupOptions WorkerOptions(int? poolSize = null) => new()
    {
        Name = "benchmark",
        Policy = new DispatchPolicy.Strict([WorkloadSpec.BenchQueue]),
        PoolSize = poolSize ?? Math.Max(8, System.Environment.ProcessorCount * 4),
        MaxClaimBatch = 128,
        PollInterval = TimeSpan.FromMilliseconds(25),
        // A short handler-delay workload still wants a generous lease so heartbeats never enter the picture.
        LeaseDuration = TimeSpan.FromSeconds(30),
    };
}
