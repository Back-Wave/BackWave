using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BackWave.Benchmarks.Workload;
using Npgsql;

namespace BackWave.Benchmarks.Targets;

/// <summary>
/// Acta on Postgres, behind the same fairness seam as the other competitors. Acta.Postgres needs Npgsql 10 and a
/// newer Microsoft.Extensions than this harness runs, so this target holds no Acta code itself: it spawns one
/// <c>BackWave.Benchmarks.ActaHost</c> child per cell (staged next to this executable at build time) and drives it
/// over stdin/stdout. The child provisions a fresh schema and builds a fresh worker host per run; the parent keeps the
/// parts that must be identical across targets: the connection probe, the CPU delta around the timed window, and the
/// sample hand-off.
/// </summary>
public sealed class ActaPostgresTarget : IBenchmarkTarget
{
    /// <summary>Environment variable holding the Acta Postgres connection string (DSN).</summary>
    public const string ConnectionStringEnvVar = ActaHostProtocol.ConnectionStringEnvVar;

    private const string ActaVersion = "1.0.0-rc.15";
    private const string HostDirectory = "acta-host";
    private const string HostAssembly = "BackWave.Benchmarks.ActaHost.dll";
    private const string LogTag = "[acta-host]";

    private const string DefaultConnectionString =
        "Host=localhost;Port=5499;Username=backwave;Password=backwave;Database=acta_test";

    private readonly string _connectionString;
    private readonly int _maxConcurrentExecutors;
    private readonly TimeSpan _connectionSampleInterval = TimeSpan.FromMilliseconds(20);
    private readonly TimeSpan _readyTimeout = TimeSpan.FromMinutes(2);

    private NpgsqlDataSource? _dataSource;
    private Process? _child;
    private ResourceMetrics _lastResources = ResourceMetrics.None;

    /// <summary>Creates the target using the DSN from the environment, or the local docker-compose default.</summary>
    public ActaPostgresTarget()
        : this(System.Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString)
    {
    }

    /// <summary>Creates the target against an explicit connection string.</summary>
    public ActaPostgresTarget(string connectionString)
    {
        _connectionString = connectionString;
        _maxConcurrentExecutors = int.TryParse(
            System.Environment.GetEnvironmentVariable(ActaHostProtocol.MaxConcurrentExecutorsEnvVar),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var executors)
            ? executors
            : BackWaveTarget.WorkerOptions().PoolSize;
    }

    /// <inheritdoc/>
    public string Name => "Acta/Postgres";

    /// <inheritdoc/>
    public string Engine => "PostgreSQL";

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> TuningDials => new Dictionary<string, string>
    {
        ["worker-pool-size"] = Invariant(_maxConcurrentExecutors),
        ["db-connection-pool-size"] = "100 (Npgsql default)",
        ["retry-policy"] = "Acta default (MaxAttempts 15, backoff 1m..1d x2 ~10%); the handler never throws, so no retry runs",
        ["max-concurrent-executors"] = $"{Invariant(_maxConcurrentExecutors)} (one worker, one Run; matched to the slot count; Acta default clamp(cores x 4, 8, 64), the same value up to 16 cores)",
        ["safety-poll-interval"] = "1s (Acta default and minimum)",
        ["heartbeat-interval"] = "45s (Acta default; lease 180s derived)",
        ["wake-up"] = "in-process (Acta default IWorkerWakeup): sustained enqueues go through the worker host's own IJobs, so each wakes the claim loop; the safety poll is the fallback",
        ["claim-strategy"] = "claim_jobs takes one row per free executor in one statement (architectural)",
        ["completion-write"] = "WorkerFinalizer batches up to 128 completions, waiting up to 50 ms for more when it finds a backlog (architectural)",
        ["event-recording"] = "Audit (Acta default): every lifecycle event goes to the events table",
        ["system-jobs"] = "on (Acta default): sys.alerts and sys.retention permanent jobs and crash recovery; their rows never count",
        ["serialization"] = "System.Text.Json by reflection (Acta default), stored as bytea",
        ["completion-timestamp"] = "stamped by the handler on return, before Acta's batched completion write",
        ["drain-detection"] = $"no job of the {ActaHostProtocol.JobName} definition left Ready, Suspended, or Executing, polled every 10 ms",
        ["schema"] = $"Acta {ActaVersion} migrations and deploy, through MigrateOnStartup on a provisioning host, into a schema \"{ActaHostProtocol.Schema}\" dropped before each run",
        ["enqueue-shape"] = $"IJobs.EnqueueAsync(input), one job and one transaction per call, namespace \"{ActaHostProtocol.Namespace}\"",
        ["acta-version"] = $"{ActaVersion} (release candidate)",
        ["npgsql-version"] = "10.0.3 in the Acta host (Acta.Postgres floor and certified major); the harness stays on 9.0.3",
        ["process"] = "its own host process (BackWave.Benchmarks.ActaHost), started once per cell; a fresh worker host per run, so warmup runs warm the JIT as for the in-process targets",
    };

    /// <inheritdoc/>
    public async Task<string> SetupAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(HostPath))
        {
            throw new InvalidOperationException(
                $"The Acta host is missing at '{HostPath}'. Build benchmarks/BackWave.Benchmarks (it stages the host).");
        }

        PostgresDatabase.EnsureExists(_connectionString);
        _dataSource = NpgsqlDataSource.Create(_connectionString);
        await SpawnChildAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendAsync(ActaHostProtocol.Setup, null, cancellationToken).ConfigureAwait(false);
        return response.ServerVersion ?? throw new InvalidOperationException("The Acta host sent no server version.");
    }

    /// <inheritdoc/>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        _lastResources = ResourceMetrics.None;
        await SendAsync(ActaHostProtocol.Reset, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task PreloadAsync(WorkloadSpec spec, CancellationToken cancellationToken)
        => SendAsync(ActaHostProtocol.Preload, spec, cancellationToken);

    /// <inheritdoc/>
    public Task WarmAsync(WorkloadSpec spec, CancellationToken cancellationToken)
        => SendAsync(ActaHostProtocol.Start, null, cancellationToken);

    /// <inheritdoc/>
    public async Task ExecuteAsync(WorkloadSpec spec, CancellationToken cancellationToken)
    {
        var child = Child;
        child.Refresh();
        var cpuStart = child.TotalProcessorTime;
        var wall = Stopwatch.StartNew();

        using var samplerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connectionSampler = SampleConnectionsAsync(samplerCts.Token);

        await SendAsync(ActaHostProtocol.Execute, spec, cancellationToken).ConfigureAwait(false);

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
    public async Task CooldownAsync(CancellationToken cancellationToken)
    {
        if (_child is not null)
        {
            await SendAsync(ActaHostProtocol.Cooldown, null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<TargetSamples> CollectSamplesAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(ActaHostProtocol.Samples, null, cancellationToken).ConfigureAwait(false);
        var enqueue = (response.EnqueueTicks ?? []).Select(TimeSpan.FromTicks).ToArray();
        var endToEnd = (response.EndToEndTicks ?? []).Select(TimeSpan.FromTicks).ToArray();
        var work = new WorkEvidence(
            response.Executions,
            response.FirstCompletionUtcTicks is { } first ? new DateTimeOffset(first, TimeSpan.Zero) : null,
            response.LastCompletionUtcTicks is { } last ? new DateTimeOffset(last, TimeSpan.Zero) : null,
            response.ProducerTicks is { } producer ? TimeSpan.FromTicks(producer) : null);
        return new TargetSamples(enqueue, endToEnd, _lastResources, work);
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

    private static string HostPath => Path.Combine(AppContext.BaseDirectory, HostDirectory, HostAssembly);

    private Process Child => _child ?? throw new InvalidOperationException("SetupAsync has not run.");

    private NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException("SetupAsync has not run.");

    private async Task SpawnChildAsync(CancellationToken cancellationToken)
    {
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
        startInfo.Environment[ActaHostProtocol.ConnectionStringEnvVar] = _connectionString;
        startInfo.Environment[ActaHostProtocol.MaxConcurrentExecutorsEnvVar] = Invariant(_maxConcurrentExecutors);

        var child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the Acta host process.");
        _child = child;
        _ = ForwardStandardErrorAsync(child);

        using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readyCts.CancelAfter(_readyTimeout);
        try
        {
            await ReadResponseAsync(child, readyCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The Acta host did not become ready within {_readyTimeout}.");
        }
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private async Task<ActaHostResponse> SendAsync(string op, WorkloadSpec? spec, CancellationToken cancellationToken)
    {
        var child = Child;
        var request = new ActaHostRequest(op, spec);
        await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, ActaHostProtocol.Json).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await child.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(child, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ActaHostResponse> ReadResponseAsync(Process child, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await child.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new InvalidOperationException(
                    $"The Acta host exited (code {(child.HasExited ? child.ExitCode : "?")}) before answering.");
            }

            if (!line.StartsWith(ActaHostProtocol.ResponsePrefix, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"{LogTag} {line}");
                continue;
            }

            var response = JsonSerializer.Deserialize<ActaHostResponse>(
                line.AsSpan(ActaHostProtocol.ResponsePrefix.Length), ActaHostProtocol.Json)
                ?? throw new InvalidOperationException("Empty response from the Acta host.");
            if (!response.Ok)
            {
                throw new InvalidOperationException($"The Acta host failed: {response.Error}");
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
                Console.Error.WriteLine($"{LogTag} {line}");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
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
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await child.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        child.Dispose();
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
}
