using System.Diagnostics;
using System.Text.Json;
using BackWave.Benchmarks.Workload;
using Npgsql;

namespace BackWave.Benchmarks.Targets;

/// <summary>
/// The JobMaster competitor over Postgres, behind the same fairness seam as Hangfire. JobMaster's runtime
/// starts once per process, has no stop, and caches bucket ownership without a liveness check, so this
/// target holds no JobMaster code itself: every run spawns a fresh <c>BackWave.Benchmarks.JobMasterHost</c>
/// child (staged next to this executable at build time) that owns both the publisher and the worker, and
/// drives it over stdin/stdout. The parent keeps the parts that must be identical across targets: the store
/// reset, the connection probe, the CPU delta around the timed window, and the sample hand-off. The
/// measurement math still lives in the orchestrator.
/// </summary>
public sealed class JobMasterPostgresTarget : IBenchmarkTarget
{
    /// <summary>Environment variable holding the JobMaster Postgres connection string (DSN).</summary>
    public const string ConnectionStringEnvVar = JobMasterHostProtocol.ConnectionStringEnvVar;

    private const string JobMasterVersion = "0.0.11-alpha";
    private const string ChildNpgsqlVersion = "10.0.1";
    private const string HostDirectory = "jobmaster-host";
    private const string HostAssembly = "BackWave.Benchmarks.JobMasterHost.dll";

    private const string DefaultConnectionString =
        "Host=localhost;Port=5499;Username=backwave;Password=backwave;Database=jobmaster_test";

    private readonly string _connectionString;
    private readonly TimeSpan _connectionSampleInterval = TimeSpan.FromMilliseconds(20);
    private readonly TimeSpan _readyTimeout = TimeSpan.FromMinutes(2);

    private NpgsqlDataSource? _dataSource;
    private Process? _child;
    private ResourceMetrics _lastResources = ResourceMetrics.None;

    /// <summary>Creates the target using the DSN from the environment, or the local docker-compose default.</summary>
    public JobMasterPostgresTarget()
        : this(System.Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString)
    {
    }

    /// <summary>Creates the target against an explicit connection string.</summary>
    public JobMasterPostgresTarget(string connectionString)
        => _connectionString = connectionString;

    /// <inheritdoc/>
    public string Name => "JobMaster/Postgres";

    /// <inheritdoc/>
    public string Engine => "PostgreSQL";

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> TuningDials => new Dictionary<string, string>
    {
        // Neutralized config - matched against BackWave so the comparison is honest. JobMaster sizes its run
        // slots as 5 x parallelism factor per Medium bucket, so the factor is derived from BackWave's pool.
        ["worker-pool-size"] = (5 * ParallelismFactor * BucketCount).ToString(),
        ["parallelism-factor"] = $"{ParallelismFactor} (run slots = 5 x factor per Medium bucket)",
        ["db-connection-pool-size"] = "100 (ADO.NET default)",
        ["retry-policy"] = "DefaultMaxRetryCount=0 - matched, 100% happy-path workload",
        ["priorities"] = "Medium only (VeryLow/Low/High/Critical disabled) - one lane, like the single bench queue",
        // Tuned-to-best: JobMaster onboards at most bucket-buffer-size jobs per bucket every 3s. The default
        // buffer (250) sits right at the slot ceiling and leaves gaps between onboarding ticks; 1000 removes
        // them (measured 65 -> 75 j/s on a 5000-job noop drain). Larger buffers gained nothing more.
        ["buckets"] = BucketCount.ToString(),
        ["bucket-buffer-size"] = BucketBufferSize.ToString(),
        ["transfer-batch-size"] = "1000",
        // Surfaced architecture - left as JobMaster ships, never neutralized.
        ["slot-hold"] = "every run slot sleeps 500 ms after each Medium job (architectural, as shipped): ceiling = slots / (job time + 0.5 s)",
        ["claim-strategy"] = "bucket ownership + in-memory buffer, batch transfer (architectural)",
        ["serialization"] = "typed message-data dictionary (architectural, as shipped)",
        ["process-model"] = "publisher + worker in ONE fresh child process per run; handler gated until the window opens",
        ["preload-note"] = "at window open every job is durably ingested; up to bucket-buffer-size per bucket already sits in memory",
        ["jobmaster-version"] = $"{JobMasterVersion} (PRE-RELEASE)",
        ["npgsql"] = $"{ChildNpgsqlVersion} in the child process (BackWave and Hangfire run on 9.0.3)",
    };

    private static int BucketCount => ReadInt(JobMasterHostProtocol.BucketCountEnvVar, 1);
    private static int BucketBufferSize => ReadInt(JobMasterHostProtocol.BucketBufferSizeEnvVar, 1000);
    private static int ParallelismFactor
        => ReadInt(JobMasterHostProtocol.ParallelismFactorEnvVar, (int)Math.Ceiling(BackWaveTarget.WorkerOptions().PoolSize / 5d));

    /// <inheritdoc/>
    public async Task<string> SetupAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(HostPath))
        {
            throw new InvalidOperationException(
                $"The JobMaster host is missing at '{HostPath}'. Build benchmarks/BackWave.Benchmarks (it stages the host).");
        }

        _dataSource = NpgsqlDataSource.Create(_connectionString);
        await using var command = _dataSource.CreateCommand("SHOW server_version");
        var version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return $"PostgreSQL {version}";
    }

    /// <inheritdoc/>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        // A fresh child per run: the previous child's bucket ownership and buffers must not leak into the
        // next run, and JobMaster's dead-worker recovery is minutes-slow, so the tables are wiped between.
        await TerminateChildAsync().ConfigureAwait(false);
        await TruncateJobMasterTablesAsync(cancellationToken).ConfigureAwait(false);
        await SpawnChildAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task PreloadAsync(WorkloadSpec spec, CancellationToken cancellationToken)
        => SendAsync(JobMasterHostProtocol.Preload, spec, cancellationToken);

    /// <inheritdoc/>
    public Task WarmAsync(WorkloadSpec spec, CancellationToken cancellationToken)
        // The child's runtime is already up (readiness waited for it) and its worker owns the bucket; there is
        // nothing further to warm without letting gated jobs run.
        => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task ExecuteAsync(WorkloadSpec spec, CancellationToken cancellationToken)
    {
        // Resource-cost capture wraps the timed window exactly as for Hangfire, except the CPU delta is read
        // from the CHILD process, because that is where JobMaster runs. Allocations/GC are not captured for a
        // competitor (Internal stays default).
        var child = _child ?? throw new InvalidOperationException("ResetAsync has not run.");
        child.Refresh();
        var cpuStart = child.TotalProcessorTime;
        var wall = Stopwatch.StartNew();

        using var samplerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connectionSampler = SampleConnectionsAsync(samplerCts.Token);

        await SendAsync(JobMasterHostProtocol.Execute, spec, cancellationToken).ConfigureAwait(false);

        wall.Stop();
        child.Refresh();
        var cpuSeconds = (child.TotalProcessorTime - cpuStart).TotalSeconds;
        samplerCts.Cancel();
        var peakConnections = await connectionSampler.ConfigureAwait(false);

        var wallSeconds = wall.Elapsed.TotalSeconds;
        var cpuPercent = wallSeconds > 0
            ? cpuSeconds / (wallSeconds * System.Environment.ProcessorCount) * 100d
            : 0d;

        _lastResources = new ResourceMetrics(peakConnections, cpuPercent, default);
    }

    /// <inheritdoc/>
    public Task CooldownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task<TargetSamples> CollectSamplesAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(JobMasterHostProtocol.Samples, null, cancellationToken).ConfigureAwait(false);
        var enqueue = (response.EnqueueTicks ?? []).Select(TimeSpan.FromTicks).ToArray();
        var endToEnd = (response.EndToEndTicks ?? []).Select(TimeSpan.FromTicks).ToArray();
        return new TargetSamples(enqueue, endToEnd, _lastResources);
    }

    /// <inheritdoc/>
    public Task TeardownAsync(CancellationToken cancellationToken) => TerminateChildAsync();

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await TerminateChildAsync().ConfigureAwait(false);
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ── Child process ────────────────────────────────────────────────────────

    private static string HostPath => Path.Combine(AppContext.BaseDirectory, HostDirectory, HostAssembly);

    private async Task SpawnChildAsync(CancellationToken cancellationToken)
    {
        // The staged host is a framework-dependent DLL, so it always runs under the dotnet muxer: our own
        // muxer when the harness itself runs under one, otherwise `dotnet` from PATH.
        var processPath = System.Environment.ProcessPath;
        var muxer = processPath is not null
            && string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? processPath
            : "dotnet";

        var startInfo = new ProcessStartInfo
        {
            FileName = muxer,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(HostPath);
        startInfo.Environment[JobMasterHostProtocol.ConnectionStringEnvVar] = _connectionString;
        startInfo.Environment[JobMasterHostProtocol.ParallelismFactorEnvVar] = ParallelismFactor.ToString();
        startInfo.Environment[JobMasterHostProtocol.BucketCountEnvVar] = BucketCount.ToString();
        startInfo.Environment[JobMasterHostProtocol.BucketBufferSizeEnvVar] = BucketBufferSize.ToString();

        var child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the JobMaster host process.");
        _child = child;
        _ = ForwardStandardErrorAsync(child);

        // Readiness = the child's first protocol line, sent once StartJobMasterRuntimeAsync completed.
        using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readyCts.CancelAfter(_readyTimeout);
        try
        {
            await ReadResponseAsync(child, readyCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The JobMaster host did not become ready within {_readyTimeout}.");
        }
    }

    private async Task<JobMasterHostResponse> SendAsync(string op, WorkloadSpec? spec, CancellationToken cancellationToken)
    {
        var child = _child ?? throw new InvalidOperationException("ResetAsync has not run.");
        var request = new JobMasterHostRequest(op, spec);
        await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, JobMasterHostProtocol.Json).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await child.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(child, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JobMasterHostResponse> ReadResponseAsync(Process child, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await child.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new InvalidOperationException(
                    $"The JobMaster host exited (code {(child.HasExited ? child.ExitCode : "?")}) before answering.");
            }

            if (!line.StartsWith(JobMasterHostProtocol.ResponsePrefix, StringComparison.Ordinal))
            {
                // Anything the child prints that is not protocol is noise; keep it visible, never parse it.
                Console.Error.WriteLine($"[jobmaster-host] {line}");
                continue;
            }

            var response = JsonSerializer.Deserialize<JobMasterHostResponse>(
                line.AsSpan(JobMasterHostProtocol.ResponsePrefix.Length), JobMasterHostProtocol.Json)
                ?? throw new InvalidOperationException("Empty response from the JobMaster host.");
            if (!response.Ok)
            {
                throw new InvalidOperationException($"The JobMaster host failed: {response.Error}");
            }

            return response;
        }
    }

    private static async Task ForwardStandardErrorAsync(Process child)
    {
        try
        {
            string? line;
            while ((line = await child.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                Console.Error.WriteLine($"[jobmaster-host] {line}");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The child was killed under us; nothing more to forward.
        }
    }

    private async Task TerminateChildAsync()
    {
        var child = _child;
        _child = null;
        if (child is null)
        {
            return;
        }

        try
        {
            child.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await child.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Give up waiting; the process is gone or wedged, and Dispose releases our handle either way.
        }

        child.Dispose();
    }

    // ── Store ────────────────────────────────────────────────────────────────

    private NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException("SetupAsync has not run.");

    private async Task TruncateJobMasterTablesAsync(CancellationToken cancellationToken)
    {
        // JobMaster provisions its own tables (default prefix JM_, lowercased) on first start, so on the very
        // first run there is nothing to wipe yet.
        var tables = new List<string>();
        await using (var list = DataSource.CreateCommand(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename LIKE 'jm\\_%'"))
        await using (var reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(reader.GetString(0));
            }
        }

        if (tables.Count == 0)
        {
            return;
        }

        var quoted = string.Join(", ", tables.Select(t => $"\"{t}\""));
        await using var truncate = DataSource.CreateCommand($"TRUNCATE {quoted} RESTART IDENTITY CASCADE");
        await truncate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<int> SampleConnectionCountAsync(CancellationToken cancellationToken)
    {
        await using var command = DataSource.CreateCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()");
        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(count, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int ReadInt(string name, int fallback)
    {
        var raw = System.Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }
}
