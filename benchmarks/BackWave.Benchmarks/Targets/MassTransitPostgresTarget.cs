using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BackWave.Benchmarks.Workload;
using Npgsql;

namespace BackWave.Benchmarks.Targets;

/// <summary>The MassTransit settings one run uses, each overridable by a <c>BACKWAVE_MASSTRANSIT_*</c> variable.</summary>
internal sealed record MassTransitTuning(
    int ConcurrentMessageLimit,
    int PrefetchCount,
    TimeSpan PollingInterval,
    int ConnectionLimit)
{
    public static MassTransitTuning FromEnvironment(int workerCount)
    {
        var limit = ReadInt(MassTransitHostProtocol.ConcurrentMessageLimitEnvVar, workerCount);
        return new MassTransitTuning(
            ConcurrentMessageLimit: limit,
            PrefetchCount: ReadInt(MassTransitHostProtocol.PrefetchCountEnvVar, DerivedPrefetchCount(limit)),
            PollingInterval: TimeSpan.FromMilliseconds(ReadInt(MassTransitHostProtocol.PollingMsEnvVar, 1000)),
            ConnectionLimit: ReadInt(MassTransitHostProtocol.ConnectionLimitEnvVar, limit));
    }

    /// <summary>
    /// The prefetch MassTransit derives when an endpoint definition sets only a concurrent message limit. The default
    /// for an endpoint configured by hand, max(2 x cores, 16), is less than the slot count.
    /// </summary>
    private static int DerivedPrefetchCount(int concurrentMessageLimit) => concurrentMessageLimit * 12 / 10;

    private static int ReadInt(string name, int fallback)
        => int.TryParse(System.Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}

/// <summary>
/// MassTransit's SQL transport on Postgres, behind the same fairness seam as the other competitors. The
/// transport needs a newer Dapper than Hangfire.PostgreSql resolves in this harness, so this target holds no
/// MassTransit code itself: it spawns one <c>BackWave.Benchmarks.MassTransitHost</c> child per cell (staged next
/// to this executable at build time) and drives it over stdin/stdout. The child builds and starts a fresh bus per
/// run; the parent keeps the parts that must be identical across targets: the connection probe, the CPU delta
/// around the timed window, and the sample hand-off.
/// </summary>
public sealed class MassTransitPostgresTarget : IBenchmarkTarget
{
    /// <summary>Environment variable holding the MassTransit Postgres connection string (DSN).</summary>
    public const string ConnectionStringEnvVar = MassTransitHostProtocol.ConnectionStringEnvVar;

    private const string MassTransitVersion = "8.5.12";
    private const string Schema = "transport";
    private const string HostDirectory = "masstransit-host";
    private const string HostAssembly = "BackWave.Benchmarks.MassTransitHost.dll";
    private const string LogTag = "[masstransit-host]";

    private const string DefaultConnectionString =
        "Host=localhost;Port=5499;Username=backwave;Password=backwave;Database=masstransit_test";

    private readonly string _connectionString;
    private readonly MassTransitTuning _tuning;
    private readonly TimeSpan _connectionSampleInterval = TimeSpan.FromMilliseconds(20);
    private readonly TimeSpan _readyTimeout = TimeSpan.FromMinutes(2);

    private NpgsqlDataSource? _dataSource;
    private Process? _child;
    private ResourceMetrics _lastResources = ResourceMetrics.None;

    /// <summary>Creates the target using the DSN from the environment, or the local docker-compose default.</summary>
    public MassTransitPostgresTarget()
        : this(System.Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString)
    {
    }

    /// <summary>Creates the target against an explicit connection string.</summary>
    public MassTransitPostgresTarget(string connectionString)
    {
        _connectionString = connectionString;
        _tuning = MassTransitTuning.FromEnvironment(BackWaveTarget.WorkerOptions().PoolSize);
    }

    /// <inheritdoc/>
    public string Name => "MassTransit/Postgres";

    /// <inheritdoc/>
    public string Engine => "PostgreSQL";

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> TuningDials => new Dictionary<string, string>
    {
        ["worker-pool-size"] = _tuning.ConcurrentMessageLimit.ToString(CultureInfo.InvariantCulture),
        ["db-connection-pool-size"] = "100 (ADO.NET default)",
        ["retry-policy"] = "none: the consumer never throws and MassTransit retries only when UseMessageRetry is configured, which it is not",
        ["concurrent-message-limit"] = _tuning.ConcurrentMessageLimit.ToString(CultureInfo.InvariantCulture),
        ["prefetch-count"] = $"{_tuning.PrefetchCount} (limit * 1.2, as MassTransit derives it when an endpoint definition sets only the limit; the hand-configured default max(2 x cores, 16) is less than the slot count)",
        ["polling-interval"] = $"{_tuning.PollingInterval.TotalMilliseconds:0}ms (MassTransit default 1s)",
        ["connection-limit"] = $"{_tuning.ConnectionLimit} (concurrent transport DB operations per host, matched to the slot count; MassTransit default 10)",
        ["lock-duration"] = "1 min (MassTransit default)",
        ["wake-up"] = "LISTEN/NOTIFY: a trigger on message_delivery calls pg_notify; the poll is the fallback",
        ["claim-strategy"] = "fetch_messages locks up to prefetch-count delivery rows per query (architectural)",
        ["serialization"] = "MassTransit System.Text.Json envelope in a jsonb body (architectural, as shipped)",
        ["completion-timestamp"] = "stamped by the consumer on return, before MassTransit deletes the delivery row",
        ["drain-detection"] = $"no message_delivery row left on the {WorkloadSpec.BenchQueue} main queue",
        ["schema"] = $"MassTransit {MassTransitVersion} AddPostgresMigrationHostedService (CreateSchema + CreateInfrastructure) into schema \"{Schema}\"",
        ["enqueue-shape"] = $"ISendEndpoint.Send to queue:{WorkloadSpec.BenchQueue}, one message per job",
        ["masstransit-version"] = MassTransitVersion,
        ["process"] = "its own host process (BackWave.Benchmarks.MassTransitHost, Npgsql 9.0.3 as in the harness), started once per cell; a fresh bus per run, so warmup runs warm the JIT as for the in-process targets",
    };

    /// <inheritdoc/>
    public async Task<string> SetupAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(HostPath))
        {
            throw new InvalidOperationException(
                $"The MassTransit host is missing at '{HostPath}'. Build benchmarks/BackWave.Benchmarks (it stages the host).");
        }

        PostgresDatabase.EnsureExists(_connectionString);
        _dataSource = NpgsqlDataSource.Create(_connectionString);
        await SpawnChildAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendAsync(MassTransitHostProtocol.Setup, null, cancellationToken).ConfigureAwait(false);
        return response.ServerVersion ?? throw new InvalidOperationException("The MassTransit host sent no server version.");
    }

    /// <inheritdoc/>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        _lastResources = ResourceMetrics.None;
        await SendAsync(MassTransitHostProtocol.Reset, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task PreloadAsync(WorkloadSpec spec, CancellationToken cancellationToken)
        => SendAsync(MassTransitHostProtocol.Preload, spec, cancellationToken);

    /// <inheritdoc/>
    public Task WarmAsync(WorkloadSpec spec, CancellationToken cancellationToken)
        => SendAsync(MassTransitHostProtocol.Start, null, cancellationToken);

    /// <inheritdoc/>
    public async Task ExecuteAsync(WorkloadSpec spec, CancellationToken cancellationToken)
    {
        var child = Child;
        child.Refresh();
        var cpuStart = child.TotalProcessorTime;
        var wall = Stopwatch.StartNew();

        using var samplerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connectionSampler = SampleConnectionsAsync(samplerCts.Token);

        await SendAsync(MassTransitHostProtocol.Execute, spec, cancellationToken).ConfigureAwait(false);

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
            await SendAsync(MassTransitHostProtocol.Cooldown, null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<TargetSamples> CollectSamplesAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(MassTransitHostProtocol.Samples, null, cancellationToken).ConfigureAwait(false);
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
        startInfo.Environment[MassTransitHostProtocol.ConnectionStringEnvVar] = _connectionString;
        startInfo.Environment[MassTransitHostProtocol.ConcurrentMessageLimitEnvVar] = Invariant(_tuning.ConcurrentMessageLimit);
        startInfo.Environment[MassTransitHostProtocol.PrefetchCountEnvVar] = Invariant(_tuning.PrefetchCount);
        startInfo.Environment[MassTransitHostProtocol.PollingMsEnvVar] = Invariant((int)_tuning.PollingInterval.TotalMilliseconds);
        startInfo.Environment[MassTransitHostProtocol.ConnectionLimitEnvVar] = Invariant(_tuning.ConnectionLimit);

        var child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the MassTransit host process.");
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
            throw new TimeoutException($"The MassTransit host did not become ready within {_readyTimeout}.");
        }
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private async Task<MassTransitHostResponse> SendAsync(string op, WorkloadSpec? spec, CancellationToken cancellationToken)
    {
        var child = Child;
        var request = new MassTransitHostRequest(op, spec);
        await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, MassTransitHostProtocol.Json).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await child.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(child, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MassTransitHostResponse> ReadResponseAsync(Process child, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await child.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new InvalidOperationException(
                    $"The MassTransit host exited (code {(child.HasExited ? child.ExitCode : "?")}) before answering.");
            }

            if (!line.StartsWith(MassTransitHostProtocol.ResponsePrefix, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"{LogTag} {line}");
                continue;
            }

            var response = JsonSerializer.Deserialize<MassTransitHostResponse>(
                line.AsSpan(MassTransitHostProtocol.ResponsePrefix.Length), MassTransitHostProtocol.Json)
                ?? throw new InvalidOperationException("Empty response from the MassTransit host.");
            if (!response.Ok)
            {
                throw new InvalidOperationException($"The MassTransit host failed: {response.Error}");
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
