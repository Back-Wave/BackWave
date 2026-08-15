using System.Collections.Concurrent;
using System.Diagnostics;
using BackWave.Benchmarks.Workload;
using Hangfire;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;

namespace BackWave.Benchmarks.Targets;

/// <summary>
/// The Hangfire side of the fairness seam (bench-0140, ADR 0027 §5) — the head-to-head competitor. It owns
/// all the storage-independent machinery: tuning Hangfire to its best honest config (worker count matched to
/// BackWave's pool, aggressive fetch, automatic retries disabled for the happy-path workload), enqueuing
/// through a real <c>BackgroundJobClient</c>, running a real <c>BackgroundJobServer</c> per timed run,
/// detecting drain through the monitoring API, and reading per-job latency from Hangfire's own stored state.
/// A concrete adapter target only supplies how to build/reset/version its storage. The measurement — the
/// timed window and all the math — lives in the orchestrator, the <em>identical</em> code that wraps BackWave,
/// never here, so the comparison is provably apples-to-apples.
/// </summary>
public abstract class HangfireBenchmarkTarget : IBenchmarkTarget
{
    private JobStorage? _storage;
    private BackgroundJobClient? _client;
    private BackgroundJobServer? _server;

    private readonly List<TimeSpan> _enqueueLatencies = [];
    // Job id → the instant the harness committed its enqueue, so end-to-end latency can pair the store's
    // own SucceededAt against a single co-located clock — the same enqueue→terminal span BackWave derives
    // from its store timestamps (ADR 0027 §4). Concurrent because the parallel producers write it at once.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _enqueuedAt = new();
    private readonly TimeSpan _drainPollInterval = TimeSpan.FromMilliseconds(10);
    private readonly TimeSpan _connectionSampleInterval = TimeSpan.FromMilliseconds(20);
    private ResourceMetrics _lastResources = ResourceMetrics.None;

    /// <summary>
    /// Worker count, taken verbatim from BackWave's pump pool size so the two systems are provably matched on
    /// this neutralized dial — the single most load-bearing fairness knob (ADR 0027 §5).
    /// </summary>
    protected static int WorkerCount => BackWaveTarget.WorkerOptions().PoolSize;

    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public abstract string Engine { get; }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> TuningDials
    {
        get
        {
            var dials = new Dictionary<string, string>
            {
                // Neutralized config — matched against BackWave so the comparison is honest.
                ["worker-pool-size"] = WorkerCount.ToString(),
                ["db-connection-pool-size"] = "100 (ADO.NET default)",
                ["retry-policy"] = "automatic retry disabled (Attempts=0) — matched, 100% happy-path workload",
                // Surfaced architecture — left as Hangfire ships, never neutralized.
                ["claim-strategy"] = "single-row fetch per worker (architectural)",
                ["serialization"] = "reflection + Newtonsoft.Json (architectural, as shipped)",
            };
            foreach (var (key, value) in StorageDials())
            {
                dials[key] = value;
            }

            return dials;
        }
    }

    /// <summary>The live storage, valid after <see cref="SetupAsync"/>. Adapter subclasses may read it.</summary>
    protected JobStorage Storage => _storage ?? throw new InvalidOperationException("SetupAsync has not run.");

    // ── Adapter-specific hooks ───────────────────────────────────────────────

    /// <summary>
    /// Builds the tuned Hangfire <see cref="JobStorage"/> for this adapter (creating its schema if needed).
    /// The adapter chooses its own best honest fetch config; the values land in <see cref="StorageDials"/>.
    /// </summary>
    protected abstract JobStorage CreateStorage();

    /// <summary>The adapter-specific tuning dials (poll interval, fetch mode, schema) folded into the result.</summary>
    protected abstract IReadOnlyDictionary<string, string> StorageDials();

    /// <summary>Reads the storage engine's version string (e.g. from <c>SELECT version()</c>).</summary>
    protected abstract Task<string> ReadEngineVersionAsync(CancellationToken cancellationToken);

    /// <summary>Empties every Hangfire table so the next run starts from a clean store.</summary>
    protected abstract Task ResetStoreAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Counts the DB connections this process currently holds against the Hangfire database, defined the same
    /// way as the BackWave probe (this process's server-side sessions, excluding the probe itself) so peak DB
    /// connections stays a fair cross-system metric.
    /// </summary>
    protected abstract Task<int> SampleConnectionCountAsync(CancellationToken cancellationToken);

    // ── IBenchmarkTarget ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<string> SetupAsync(CancellationToken cancellationToken)
    {
        _storage = CreateStorage();
        JobStorage.Current = _storage;
        _client = new BackgroundJobClient(_storage);

        // Disable automatic retries: a transient blip must not retry-inflate or -deflate a happy-path number,
        // matching BackWave's bench workload, which configures no retries. Hangfire ships a global
        // AutomaticRetry(10), so replace it rather than stacking a second filter.
        foreach (var filter in GlobalJobFilters.Filters.Where(f => f.Instance is AutomaticRetryAttribute).ToList())
        {
            GlobalJobFilters.Filters.Remove(filter.Instance);
        }

        GlobalJobFilters.Filters.Add(new AutomaticRetryAttribute { Attempts = 0 });

        return await ReadEngineVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task ResetAsync(CancellationToken cancellationToken)
    {
        _enqueueLatencies.Clear();
        _enqueuedAt.Clear();
        _lastResources = ResourceMetrics.None;
        return ResetStoreAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task PreloadAsync(WorkloadSpec spec, CancellationToken cancellationToken)
    {
        // Drain headline: the whole backlog is committed before the timed window opens. Sustained mode defers
        // production into ExecuteAsync, so preload is a no-op there — exactly as BackWave does it.
        if (spec.Arrival != ArrivalMode.Drain)
        {
            return;
        }

        await EnqueueAllAsync(spec, paceRatePerSecond: 0, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task WarmAsync(WorkloadSpec spec, CancellationToken cancellationToken)
    {
        _ = spec;
        _ = cancellationToken;
        // Constructing the server starts it claiming immediately, so do it BEFORE the window — server spin-up
        // stays out of the throughput denominator, exactly as BackWave starts its pump in WarmAsync. This is
        // the symmetry that keeps the head-to-head comparison apples-to-apples (ADR 0027 §2, §5).
        _server = new BackgroundJobServer(ServerOptions(), Storage);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task ExecuteAsync(WorkloadSpec spec, CancellationToken cancellationToken)
    {
        // Resource-cost capture wraps the timed window — and ONLY the window (server started in WarmAsync,
        // stopped in CooldownAsync), captured the same way as BackWave: CPU as a before/after process-time
        // delta, DB connections sampled by a background task tracking the peak. Allocations/GC are
        // deliberately NOT captured for the competitor — process-wide GC against a reflection-JSON system is
        // a cross-system fairness minefield (ADR 0027 §4), so Internal stays None.
        var cpuStart = ProcessCpuTime();
        var wall = Stopwatch.StartNew();

        using var samplerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connectionSampler = SampleConnectionsAsync(samplerCts.Token);

        // Sustained: producers feed the queue at the target rate while the server claims concurrently.
        // Drain: the backlog is already loaded; just empty it.
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

        _lastResources = new ResourceMetrics(peakConnections, cpuPercent, default);
    }

    /// <inheritdoc/>
    public async Task CooldownAsync(CancellationToken cancellationToken)
    {
        // SendStop + WaitForShutdown drains in-flight workers cleanly; it happens AFTER the window closes so
        // the graceful shutdown never lands in the throughput denominator (matches BackWave's CooldownAsync).
        if (_server is null)
        {
            return;
        }

        _server.SendStop();
        await _server.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        _server.Dispose();
        _server = null;
    }

    /// <inheritdoc/>
    public async Task<TargetSamples> CollectSamplesAsync(CancellationToken cancellationToken)
    {
        var endToEnd = await Task.Run(CollectEndToEndLatencies, cancellationToken).ConfigureAwait(false);
        return new TargetSamples(_enqueueLatencies.ToArray(), endToEnd, _lastResources);
    }

    /// <inheritdoc/>
    public virtual Task TeardownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        // Safety net: if ExecuteAsync threw before CooldownAsync ran, the server is still up — dispose it here
        // so no Hangfire server is ever leaked against the shared storage.
        _server?.Dispose();
        if (_storage is IDisposable disposable)
        {
            disposable.Dispose();
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private async Task EnqueueAllAsync(WorkloadSpec spec, double paceRatePerSecond, CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException("SetupAsync has not run.");
        // The shared fan-out runs spec.ProducerCount producers (identical to BackWave); the enqueue itself is
        // Hangfire's synchronous client. Recording the enqueue instant is a concurrent-dictionary write, so
        // the parallel producers are safe (ADR 0027 §5).
        var latencies = await ParallelProducer.RunAsync(
            spec, paceRatePerSecond,
            (job, _) =>
            {
                var payload = job.Payload;
                var delayMs = job.DelayMs;
                var id = client.Enqueue(() => HangfireBenchmarkJob.ExecuteAsync(payload, delayMs));
                _enqueuedAt[id] = DateTimeOffset.UtcNow;
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        _enqueueLatencies.AddRange(latencies);
    }

    private async Task WaitForDrainAsync(CancellationToken cancellationToken)
    {
        var monitoring = Storage.GetMonitoringApi();
        while (true)
        {
            var enqueued = monitoring.EnqueuedCount(WorkloadSpec.BenchQueue);
            var fetched = monitoring.FetchedCount(WorkloadSpec.BenchQueue);
            var processing = monitoring.ProcessingCount();
            if (enqueued == 0 && fetched == 0 && processing == 0)
            {
                return;
            }

            await Task.Delay(_drainPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private IReadOnlyList<TimeSpan> CollectEndToEndLatencies()
    {
        // End-to-end = enqueue→terminal, read from Hangfire's own succeeded-state timestamp paired with the
        // harness-recorded enqueue instant (one co-located clock) — the symmetric counterpart to BackWave's
        // store DueTime→TerminalAt span.
        var monitoring = Storage.GetMonitoringApi();
        var latencies = new List<TimeSpan>();
        const int pageSize = 1000;
        var total = monitoring.SucceededListCount();
        for (var from = 0L; from < total; from += pageSize)
        {
            var page = monitoring.SucceededJobs((int)from, pageSize);
            foreach (var (id, dto) in page)
            {
                if (dto.SucceededAt is { } succeeded && _enqueuedAt.TryGetValue(id, out var enqueuedAt))
                {
                    // Hangfire records SucceededAt in UTC but the monitoring API hands it back as a DateTime
                    // with Kind=Unspecified; ToUniversalTime() would wrongly re-shift it by the local offset,
                    // so stamp it UTC explicitly to pair against the UTC enqueue instant on one clock.
                    var succeededUtc = DateTime.SpecifyKind(succeeded, DateTimeKind.Utc);
                    latencies.Add(succeededUtc - enqueuedAt.UtcDateTime);
                }
            }
        }

        return latencies;
    }

    private async Task<int> SampleConnectionsAsync(CancellationToken cancellationToken)
    {
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

    private static BackgroundJobServerOptions ServerOptions() => new()
    {
        WorkerCount = WorkerCount,
        Queues = [WorkloadSpec.BenchQueue],
        // Schedule polling is irrelevant (no scheduled jobs) but kept tight so a stray default never skews a run.
        SchedulePollingInterval = TimeSpan.FromMilliseconds(100),
    };
}
