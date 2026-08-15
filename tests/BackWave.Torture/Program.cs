using System.Globalization;
using BackWave.Torture;

// Torture Suite, store mode (issue 0200 / ADR 0039): randomized concurrent workload + invariant
// audit against live adapters. Discovery-only — NEVER a PR gate. Exit codes: 0 clean, 1 violations
// (a torture failure is always a bug), 2 infrastructure error, 3 usage.

if (args.Length > 0 && args[0] == "client")
{
    return await RunChildVerbAsync(args);
}

if (args.Length > 0 && args[0] == "upgrade")
{
    return await RunUpgradeVerbAsync(args);
}

if (args.Length > 0 && args[0] == "--stats")
{
    return RunStatsVerb();
}

TortureAdapter? adapter = null;
ulong seed = (ulong)Random.Shared.NextInt64() ^ ((ulong)Random.Shared.NextInt64() << 1);
var duration = TimeSpan.FromSeconds(60);
var clients = 8;
var processes = 4;
var maxAttempts = 4;
var drainBound = TimeSpan.FromSeconds(90);
var artifacts = "torture-artifacts";
var governedLimit = 2;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--adapter" when i + 1 < args.Length:
            adapter = args[++i] switch
            {
                "postgres" or "pg" => TortureAdapter.Postgres,
                "sqlserver" or "mssql" => TortureAdapter.SqlServer,
                "sqlite" => TortureAdapter.Sqlite,
                "sqlite-multiprocess" or "sqlite-mp" => TortureAdapter.SqliteMultiProcess,
                var other => Fail<TortureAdapter?>($"unknown adapter '{other}'"),
            };
            break;
        case "--seed" when i + 1 < args.Length:
            seed = ParseSeed(args[++i]);
            break;
        case "--duration" when i + 1 < args.Length:
            duration = ParseDuration(args[++i]);
            break;
        case "--clients" when i + 1 < args.Length:
            clients = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--processes" when i + 1 < args.Length:
            processes = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--max-attempts" when i + 1 < args.Length:
            maxAttempts = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--drain-bound" when i + 1 < args.Length:
            drainBound = ParseDuration(args[++i]);
            break;
        case "--artifacts" when i + 1 < args.Length:
            artifacts = args[++i];
            break;
        case "--governed-limit" when i + 1 < args.Length:
            governedLimit = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        default:
            return Usage();
    }
}

if (adapter is null)
{
    return Usage();
}

return await TortureRun.RunAsync(new TortureOptions
{
    Adapter = adapter.Value,
    Seed = seed,
    Duration = duration,
    Clients = clients,
    Processes = processes,
    MaxAttempts = maxAttempts,
    DrainBound = drainBound,
    ArtifactsDir = artifacts,
    GovernedLimit = governedLimit,
});

static async Task<int> RunChildVerbAsync(string[] args)
{
    string? db = null, journal = null;
    ulong seed = 0;
    int clientBase = 0, clients = 1, maxAttempts = 4;
    var duration = TimeSpan.FromSeconds(60);
    long started = DateTimeOffset.UtcNow.UtcTicks;
    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--db": db = args[++i]; break;
            case "--seed": seed = ParseSeed(args[++i]); break;
            case "--client-base": clientBase = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--clients": clients = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--duration": duration = ParseDuration(args[++i]); break;
            case "--journal": journal = args[++i]; break;
            case "--max-attempts": maxAttempts = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--started": started = long.Parse(args[++i], CultureInfo.InvariantCulture); break;
            default: return Usage();
        }
    }
    if (db is null || journal is null)
    {
        return Usage();
    }
    return await TortureRun.RunChildAsync(db, seed, clientBase, clients, duration, journal, maxAttempts, started);
}

// In-place upgrade harness (issue 0202 / ADR 0038): migrate a populated vN-1 database to current, under
// a live workload, then audit with the Torture oracles. Postgres + SQL Server only (SQLite has no
// vN-1 → vN step yet). Exit codes match the torture verb: 0 clean, 1 violations, 2 infra, 3 usage.
static async Task<int> RunUpgradeVerbAsync(string[] args)
{
    UpgradeAdapter? adapter = null;
    var duration = TimeSpan.FromSeconds(12);
    var clients = 4;
    var sabotage = Environment.GetEnvironmentVariable("BACKWAVE_UPGRADE_SABOTAGE") == "1";
    ulong? seed = null;
    int? onlyVersion = null;

    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--adapter" when i + 1 < args.Length:
                adapter = args[++i] switch
                {
                    "postgres" or "pg" => UpgradeAdapter.Postgres,
                    "sqlserver" or "mssql" => UpgradeAdapter.SqlServer,
                    var other => Fail<UpgradeAdapter?>($"unknown adapter '{other}'"),
                };
                break;
            case "--seed" when i + 1 < args.Length:
                seed = ParseSeed(args[++i]);
                break;
            case "--duration" when i + 1 < args.Length:
                duration = ParseDuration(args[++i]);
                break;
            case "--clients" when i + 1 < args.Length:
                clients = int.Parse(args[++i], CultureInfo.InvariantCulture);
                break;
            case "--sabotage":
                sabotage = true;
                break;
            case "--only-version" when i + 1 < args.Length:
                onlyVersion = int.Parse(args[++i], CultureInfo.InvariantCulture);
                break;
            default:
                return UpgradeUsage();
        }
    }

    if (adapter is null)
    {
        return UpgradeUsage();
    }

    var options = new UpgradeOptions
    {
        Adapter = adapter.Value,
        WorkloadDuration = duration,
        Clients = clients,
        Sabotage = sabotage,
        OnlyPriorVersion = onlyVersion,
    };
    return await UpgradeRun.RunAsync(seed is { } s ? options with { Seed = s } : options);
}

static int UpgradeUsage()
{
    Console.Error.WriteLine("""
        usage: BackWave.Torture upgrade --adapter <postgres|sqlserver> [options]

        options:
          --seed <n|0xHEX>       workload + fixture seed (default: fixed, so runs are reproducible)
          --duration <n[s|m]>    live-workload time box per prior version (default 12s)
          --clients <n>          synthetic live-workload clients (default 4)
          --only-version <n>     run just this single prior version (default: the whole v1..v(current-1) matrix)
          --sabotage             hand-break the migration (delete a populated job) to prove the harness goes RED

        Postgres/SQL Server need: docker compose up -d postgres sqlserver
        Migrates every shipped prior version (v1..v(current-1)) to current, audits with the Torture oracle set.
        """);
    return 3;
}

// --stats: fold the append-only cross-run coverage ledger (BACKWAVE_TORTURE_LEDGER) into the headline —
// real hours hammered, jobs audited, store ops, and duplicate-key races, per adapter — and stamp the
// citable summary doc (BACKWAVE_TORTURE_COVERAGE_DOC, else docs/torture-coverage.md).
static int RunStatsVerb()
{
    var ledgerPath = Environment.GetEnvironmentVariable("BACKWAVE_TORTURE_LEDGER");
    if (string.IsNullOrWhiteSpace(ledgerPath))
    {
        Console.Error.WriteLine("--stats needs BACKWAVE_TORTURE_LEDGER set to the ledger file path.");
        return 3;
    }

    var summary = TortureLedger.Summarize(TortureLedger.Read(ledgerPath));
    Console.WriteLine($"torture coverage ledger — {ledgerPath} ({summary.Runs} run(s))");
    Console.WriteLine(
        $"  real load hammered: {summary.TotalHammerHours:N1} hours "
        + $"({summary.TotalWallHours:N1} wall-hours incl. drain + audit)");
    Console.WriteLine($"  jobs audited: {summary.TotalJobsAudited:N0}");
    Console.WriteLine($"  store operations exercised: {summary.TotalOps:N0}");
    Console.WriteLine(
        $"  duplicate-key races provoked: {summary.TotalDuplicateEnqueueAttempts:N0} enqueue, "
        + $"{summary.TotalDuplicateWorkflowAttempts:N0} workflow");
    Console.WriteLine(
        $"  distinct invariants surfaced: {summary.DistinctFindings} "
        + $"({summary.TotalFindingEvents:N0} total trip event(s))");
    foreach (var a in summary.ByAdapter)
    {
        Console.WriteLine($"    {a.Adapter}: {a.Runs} run(s), {a.HammerHours:N1}h, {a.JobsAudited:N0} jobs, {a.Violations} violation(s)");
    }

    var docPath = Environment.GetEnvironmentVariable("BACKWAVE_TORTURE_COVERAGE_DOC") ?? "docs/torture-coverage.md";
    var docDir = Path.GetDirectoryName(Path.GetFullPath(docPath));
    if (!string.IsNullOrEmpty(docDir))
    {
        Directory.CreateDirectory(docDir);
    }
    File.WriteAllText(docPath, TortureLedger.ToMarkdown(summary, DateTimeOffset.UtcNow.ToString("u")));
    Console.WriteLine($"  wrote {docPath}");
    return 0;
}

static ulong ParseSeed(string value)
    => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? ulong.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : ulong.Parse(value, CultureInfo.InvariantCulture);

static TimeSpan ParseDuration(string value)
{
    if (value.Length > 1 && char.IsLetter(value[^1]))
    {
        var magnitude = double.Parse(value[..^1], CultureInfo.InvariantCulture);
        return value[^1] switch
        {
            's' => TimeSpan.FromSeconds(magnitude),
            'm' => TimeSpan.FromMinutes(magnitude),
            'h' => TimeSpan.FromHours(magnitude),
            _ => throw new ArgumentException($"unknown duration suffix in '{value}'"),
        };
    }
    return TimeSpan.FromSeconds(double.Parse(value, CultureInfo.InvariantCulture));
}

static T Fail<T>(string message) => throw new ArgumentException(message);

static int Usage()
{
    Console.Error.WriteLine("""
        usage: BackWave.Torture --adapter <postgres|sqlserver|sqlite|sqlite-multiprocess> [options]

        options:
          --seed <n|0xHEX>       workload seed (default: random; always logged)
          --duration <n[s|m|h]>  workload time box (default 60s)
          --clients <n>          synthetic clients (default 8)
          --processes <n>        child processes for sqlite-multiprocess (default 4)
          --max-attempts <n>     attempt ceiling the clients enforce (default 4)
          --drain-bound <n[s|m]> bounded wait for post-workload quiescence (default 90s)
          --artifacts <dir>      artifact bundle directory (default torture-artifacts)
          --governed-limit <n>   static concurrency limit on the governed queue (default 2)

        Postgres/SQL Server need: docker compose up -d postgres sqlserver
        Discovery-only. Never wire this into a PR gate (ADR 0039); nightly CI is issue 0199.
        """);
    return 3;
}
