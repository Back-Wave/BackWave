using System.Diagnostics;
using System.Text.Json;
using BackWave.Benchmarks.Workload;
using Npgsql;

namespace BackWave.Benchmarks.Targets;

/// <summary>
/// The TickerQ competitor over Postgres (EF Core operational store), behind the same fairness seam as
/// Hangfire. TickerQ's EF Core provider needs Npgsql 10 and keeps process-wide static state, so this target
/// holds no TickerQ code itself: every run spawns a fresh <c>BackWave.Benchmarks.TickerQHost</c> child (staged
/// next to this executable at build time) and drives it over stdin/stdout. The parent keeps the parts that
/// must be identical across targets: the store reset, the connection probe, the CPU delta around the timed
/// window, and the sample hand-off.
/// </summary>
public sealed class TickerQPostgresTarget : IBenchmarkTarget
{
    /// <summary>Environment variable holding the TickerQ Postgres connection string (DSN).</summary>
    public const string ConnectionStringEnvVar = TickerQHostProtocol.ConnectionStringEnvVar;

    private const string TickerQVersion = "10.4.0";
    private const string ChildNpgsqlVersion = "Npgsql.EntityFrameworkCore.PostgreSQL 10.0.0 (Npgsql 10)";
    private const string HostDirectory = "tickerq-host";
    private const string HostAssembly = "BackWave.Benchmarks.TickerQHost.dll";
    private const string LogTag = "[tickerq-host]";

    private const string DefaultConnectionString =
        "Host=localhost;Port=5499;Username=backwave;Password=backwave;Database=tickerq_test";

    private readonly string _connectionString;
    private readonly TimeSpan _connectionSampleInterval = TimeSpan.FromMilliseconds(20);
    private readonly TimeSpan _readyTimeout = TimeSpan.FromMinutes(2);

    private NpgsqlDataSource? _dataSource;
    private Process? _child;
    private ResourceMetrics _lastResources = ResourceMetrics.None;

    /// <summary>Creates the target using the DSN from the environment, or the local docker-compose default.</summary>
    public TickerQPostgresTarget()
        : this(System.Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString)
    {
    }

    /// <summary>Creates the target against an explicit connection string.</summary>
    public TickerQPostgresTarget(string connectionString)
        => _connectionString = connectionString;

    /// <inheritdoc/>
    public string Name => "TickerQ/Postgres";

    /// <inheritdoc/>
    public string Engine => "PostgreSQL";

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> TuningDials => new Dictionary<string, string>
    {
        ["worker-pool-size"] = MaxConcurrency.ToString(),
        ["concurrency-limit"] = "scheduler MaxConcurrency and the bench function's maxConcurrency both = worker-pool-size; the scheduler alone does not bound async jobs in flight in 10.4.0",
        ["db-connection-pool-size"] = "100 (ADO.NET default); EF Core DbContext pool 1024 (TickerQ default)",
        ["retry-policy"] = "Retries=0 on every ticker - matched, 100% happy-path workload",
        ["fallback-interval"] = $"{FallbackIntervalMs}ms (TickerQ ships 30s)",
        ["min-polling-interval"] = $"{MinPollingIntervalMs}ms (TickerQ ships 1s)",
        ["logging"] = "no logging providers (as BackWave)",
        ["dashboard"] = "not registered",
        ["drain-path"] = "tickers more than 1s overdue are only picked up by the fallback service, which claims the whole overdue set one UPDATE at a time on one connection before it dispatches that pass (architectural)",
        ["sustained-path"] = "run-now tickers are inserted, acquired, and handed to the local worker pool inside AddAsync, so producers wait on dispatch (architectural)",
        ["per-job-round-trips"] = "claim UPDATE, request SELECT, completion UPDATE, each its own autocommit statement (architectural)",
        ["serialization"] = "System.Text.Json reflection, request bytes stored on the ticker row (as shipped)",
        ["process-model"] = "producer-only instance (DisableBackgroundServices) persists the drain backlog; processing host in the same fresh child process per run",
        ["schema"] = "dropped before each run and recreated by EnsureCreated, so it always matches the TickerQ version under test",
        ["latency-source"] = "TickerQ's own ExecutedAt (taken when the handler returns) minus the instant the ticker INSERT committed (EF Core SaveChanges interceptor)",
        ["tickerq-version"] = $"{TickerQVersion} (newest MIT/Apache release; 10.5.x and 11.5.x need a license certificate to execute jobs, and 11.5.x targets net11.0 only)",
        ["npgsql"] = $"{ChildNpgsqlVersion} in the child process (BackWave and Hangfire run on Npgsql 9.0.3)",
    };

    private static int MaxConcurrency
        => ReadInt(TickerQHostProtocol.MaxConcurrencyEnvVar, BackWaveTarget.WorkerOptions().PoolSize);

    private static int FallbackIntervalMs => ReadInt(TickerQHostProtocol.FallbackIntervalEnvVar, 10);

    private static int MinPollingIntervalMs => ReadInt(TickerQHostProtocol.MinPollingIntervalEnvVar, 0);

    /// <inheritdoc/>
    public async Task<string> SetupAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(HostPath))
        {
            throw new InvalidOperationException(
                $"The TickerQ host is missing at '{HostPath}'. Build benchmarks/BackWave.Benchmarks (it stages the host).");
        }

        PostgresDatabase.EnsureExists(_connectionString);
        _dataSource = NpgsqlDataSource.Create(_connectionString);
        await using var command = _dataSource.CreateCommand("SHOW server_version");
        var version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return $"PostgreSQL {version}";
    }

    /// <inheritdoc/>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await TerminateChildAsync().ConfigureAwait(false);
        await DropTickerQSchemaAsync(cancellationToken).ConfigureAwait(false);
        await SpawnChildAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task PreloadAsync(WorkloadSpec spec, CancellationToken cancellationToken)
        => SendAsync(TickerQHostProtocol.Preload, spec, cancellationToken);

    /// <inheritdoc/>
    public Task WarmAsync(WorkloadSpec spec, CancellationToken cancellationToken)
        => SendAsync(TickerQHostProtocol.Start, null, cancellationToken);

    /// <inheritdoc/>
    public async Task ExecuteAsync(WorkloadSpec spec, CancellationToken cancellationToken)
    {
        var child = _child ?? throw new InvalidOperationException("ResetAsync has not run.");
        child.Refresh();
        var cpuStart = child.TotalProcessorTime;
        var wall = Stopwatch.StartNew();

        using var samplerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connectionSampler = SampleConnectionsAsync(samplerCts.Token);

        await SendAsync(TickerQHostProtocol.Execute, spec, cancellationToken).ConfigureAwait(false);

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
        var response = await SendAsync(TickerQHostProtocol.Samples, null, cancellationToken).ConfigureAwait(false);
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

    private static string HostPath => Path.Combine(AppContext.BaseDirectory, HostDirectory, HostAssembly);

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
        startInfo.Environment[TickerQHostProtocol.ConnectionStringEnvVar] = _connectionString;
        startInfo.Environment[TickerQHostProtocol.MaxConcurrencyEnvVar] = MaxConcurrency.ToString();
        startInfo.Environment[TickerQHostProtocol.FallbackIntervalEnvVar] = FallbackIntervalMs.ToString();
        startInfo.Environment[TickerQHostProtocol.MinPollingIntervalEnvVar] = MinPollingIntervalMs.ToString();

        var child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the TickerQ host process.");
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
            throw new TimeoutException($"The TickerQ host did not become ready within {_readyTimeout}.");
        }
    }

    private async Task<TickerQHostResponse> SendAsync(string op, WorkloadSpec? spec, CancellationToken cancellationToken)
    {
        var child = _child ?? throw new InvalidOperationException("ResetAsync has not run.");
        var request = new TickerQHostRequest(op, spec);
        await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, TickerQHostProtocol.Json).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await child.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(child, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TickerQHostResponse> ReadResponseAsync(Process child, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await child.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new InvalidOperationException(
                    $"The TickerQ host exited (code {(child.HasExited ? child.ExitCode : "?")}) before answering.");
            }

            if (!line.StartsWith(TickerQHostProtocol.ResponsePrefix, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"{LogTag} {line}");
                continue;
            }

            var response = JsonSerializer.Deserialize<TickerQHostResponse>(
                line.AsSpan(TickerQHostProtocol.ResponsePrefix.Length), TickerQHostProtocol.Json)
                ?? throw new InvalidOperationException("Empty response from the TickerQ host.");
            if (!response.Ok)
            {
                throw new InvalidOperationException($"The TickerQ host failed: {response.Error}");
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

    private NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException("SetupAsync has not run.");

    private async Task DropTickerQSchemaAsync(CancellationToken cancellationToken)
    {
        // The child recreates the schema with EnsureCreated, which never alters an existing one, so dropping it
        // keeps each run on the schema of the TickerQ version under test.
        await using var command = DataSource.CreateCommand("DROP SCHEMA IF EXISTS ticker CASCADE");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        return int.TryParse(raw, out var value) && value >= 0 ? value : fallback;
    }
}
