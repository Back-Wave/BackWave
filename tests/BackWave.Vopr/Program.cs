using BackWave.Tests.Simulation;

// VOPR console — the continuous Deterministic Simulation Testing discovery engine (issue 0087).
// A pool of ProcessorCount workers draws uniform-random Seeds from a logged per-process entropy base,
// runs SwarmConfig.FromSeed through a fresh Simulator, and on a tripped oracle persists the realized Plan
// to a working corpus (deduped by invariant ID) — then continues, forever, printing a running tally.
//
// Usage:
//   dotnet run --project tests/BackWave.Vopr                  # forever-runner (Ctrl-C to stop)
//   dotnet run --project tests/BackWave.Vopr -- --max-runs N  # bounded smoke (stops at N runs)
//   dotnet run --project tests/BackWave.Vopr -- --duration 8h # wall-clock bound (stops after the window)
//   dotnet run --project tests/BackWave.Vopr -- --guided      # coverage-guided swarm (instead of uniform-random)
//   dotnet run --project tests/BackWave.Vopr -- --radioactive # catastrophic max-chaos swarm (safety oracles only)
//   dotnet run --project tests/BackWave.Vopr -- --stats       # roll up the cross-run coverage ledger (VOPR_LEDGER)
//   dotnet run --project tests/BackWave.Vopr -- --replay F.json # replay a persisted Plan; report REPRO vs CLEAN
//   dotnet run --project tests/BackWave.Vopr -- <seed>        # legacy single-seed shell
//
// --duration accepts a bare number (seconds) or a suffixed value: 90s, 30m, 8h, 1d. It self-terminates so an
// unattended run still flushes the entropy-base and failure summary — no interactive Ctrl-C required.
// --max-runs and --duration compose: whichever bound is reached first stops the run.
//
// --guided is an exclusive mode switch (issue 0129): absent = today's uniform-random VoprRunner; present = the
// CoverageGuidedSwarm. It shares --max-runs (the iteration cap), --duration, and Ctrl-C via the one token. In
// guided mode VOPR_CORPUS_DIR is still the bug-Plan sink; the coverage corpus lives in memory and is not persisted.
//
// Corpus dir: $VOPR_CORPUS_DIR, else a working dir under the temp path (never checked in).

long? maxRuns = null;
TimeSpan? duration = null;
ulong? singleSeed = null;
var guided = false;
var radioactive = false;
var showStats = false;
string? replayPath = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--max-runs" && i + 1 < args.Length && long.TryParse(args[i + 1], out var m))
    {
        maxRuns = m;
        i++;
    }
    else if (args[i] == "--duration" && i + 1 < args.Length && ParseDuration(args[i + 1]) is { } d)
    {
        duration = d;
        i++;
    }
    else if (args[i] == "--guided")
    {
        guided = true;
    }
    else if (args[i] == "--radioactive")
    {
        radioactive = true;
    }
    else if (args[i] == "--stats")
    {
        showStats = true;
    }
    else if (args[i] == "--replay" && i + 1 < args.Length)
    {
        replayPath = args[i + 1];
        i++;
    }
    else if (ulong.TryParse(args[i], out var s))
    {
        singleSeed = s;
    }
}

// --stats: roll up the append-only cross-run coverage ledger (VOPR_LEDGER) into the headline — total
// equivalent CLUSTER-YEARS tested (summed virtual time, not a per-sim ceiling), simulations, wall-clock, and
// distinct invariants ever surfaced — and stamp the citable summary doc (VOPR_COVERAGE_DOC, else docs/vopr-coverage.md).
if (showStats)
{
    var ledgerPath = Environment.GetEnvironmentVariable("VOPR_LEDGER");
    if (string.IsNullOrWhiteSpace(ledgerPath))
    {
        Console.WriteLine("--stats needs VOPR_LEDGER set to the ledger file path.");
        Environment.ExitCode = 3;
        return;
    }

    var entries = CoverageLedger.Read(ledgerPath);
    var summary = CoverageLedger.Summarize(entries);
    Console.WriteLine($"VOPR coverage ledger — {ledgerPath} ({summary.Runs} run(s))");
    Console.WriteLine(
        $"  equivalent cluster-time tested: {summary.EquivalentYears:N1} years "
        + $"({summary.TotalVirtualSeconds / 3600.0:N0} cluster-hours)");
    Console.WriteLine($"  simulations: {summary.TotalSims:N0}");
    Console.WriteLine($"  wall-clock invested: {summary.TotalWallHours:N1} hours");
    Console.WriteLine(
        $"  distinct invariants surfaced: {summary.DistinctFindings} "
        + $"({summary.TotalFindingEvents:N0} total trip event(s))");
    foreach (var (id, count) in summary.FindingsByInvariant.OrderByDescending(kvp => kvp.Value))
    {
        Console.WriteLine($"    {id}: {count:N0}");
    }

    var docPath = Environment.GetEnvironmentVariable("VOPR_COVERAGE_DOC") ?? "docs/vopr-coverage.md";
    var docDir = Path.GetDirectoryName(Path.GetFullPath(docPath));
    if (!string.IsNullOrEmpty(docDir))
    {
        Directory.CreateDirectory(docDir);
    }
    File.WriteAllText(docPath, CoverageLedger.ToMarkdown(summary, DateTimeOffset.UtcNow.ToString("u")));
    Console.WriteLine($"  wrote {docPath}");
    return;
}

// --replay <plan.json>: load a persisted failing Plan and replay it deterministically, reporting whether it
// re-trips the SAME InvariantId (a real, reproducible bug) or converges clean (an artifact / since-fixed). The
// morning-triage one-liner for the overnight corpus; mirrors RegressionFixturesTests. Exit code: 0 repro,
// 1 clean, 2 mismatch, 3 load error — so a triage script can branch on the verdict.
if (replayPath is not null)
{
    Plan replayPlan;
    try
    {
        replayPlan = PlanStore.Load(replayPath);
    }
    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or ArgumentException)
    {
        Console.WriteLine($"LOAD-ERROR — could not load Plan from {replayPath}: {ex.Message}");
        Environment.ExitCode = 3;
        return;
    }

    var expected = replayPlan.Failure?.InvariantId;
    var replaySim = new Simulator(replayPlan.Scenario.ToOptions(), FaultPlan.Replay(replayPlan.Seed, replayPlan.FaultMap));
    try
    {
        replaySim.Run();
        Console.WriteLine(
            $"CLEAN  {replayPath} — replay converged, no oracle tripped"
            + (expected is { } e ? $" (Plan stamped {e}) — likely an artifact or already-fixed." : "."));
        Environment.ExitCode = 1;
    }
    catch (SimulationInvariantException ex)
    {
        if (expected is null || ex.InvariantId == expected)
        {
            Console.WriteLine($"REPRO  {replayPath} — re-tripped {ex.InvariantId}: {ex.Message}");
            Environment.ExitCode = 0;
        }
        else
        {
            Console.WriteLine($"MISMATCH  {replayPath} — tripped {ex.InvariantId} but Plan stamped {expected}: {ex.Message}");
            Environment.ExitCode = 2;
        }
    }
    return;
}

// Legacy single-seed shell — kept for quick one-off inspection.
if (singleSeed is { } seed && maxRuns is null)
{
    Console.WriteLine($"VOPR single-seed shell — running one seeded simulation (seed {seed})");
    var r = new Simulator(new SimulationOptions { Seed = seed }).Run();
    Console.WriteLine(
        $"seed {r.Seed}: {r.Steps} steps, {r.Succeeded} succeeded, "
        + $"{r.DeadLettered} dead-lettered, {r.Crashes} crashes, {r.StaleOutcomes} stale");
    return;
}

var corpusDir = Environment.GetEnvironmentVariable("VOPR_CORPUS_DIR")
    ?? Path.Combine(Path.GetTempPath(), "backwave-vopr-corpus");
var store = new PlanStore(corpusDir);
var runner = new VoprRunner(store);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // graceful: let workers finish their current run
    cts.Cancel();
    Console.WriteLine("\nstopping… (finishing in-flight runs)");
};
// A wall-clock bound cancels the same token the workers watch, so the run drains gracefully and the tally
// (entropy-base + failure breakdown) still prints — exactly the Ctrl-C path, just fired by a timer.
if (duration is { } window)
{
    cts.CancelAfter(window);
}

// ── Coverage-guided swarm (issue 0129) ──────────────────────────────────────────────────────────────
// Exclusive mode: one engine owns all ProcessorCount workers, so the productivity tally is cleanly
// attributable. The live snapshot surfaces the productivity pulse (corpus size + interaction-tuples);
// the final summary adds the guided-only counters to the same shape as the random block below.
if (guided)
{
    var swarm = new CoverageGuidedSwarm(store);
    Console.WriteLine(
        $"VOPR coverage-guided swarm — {Environment.ProcessorCount} workers, bug sink {store.CorpusDir}"
        + (maxRuns is { } gcap ? $", bounded to {gcap} iterations" : "")
        + (duration is { } gw ? $", stopping after {gw}" : "")
        + (maxRuns is null && duration is null ? " (Ctrl-C to stop)" : " (Ctrl-C to stop early)"));

    // Cross-session corpus persistence (opt-in via VOPR_GUIDED_CORPUS_DIR, separate from the bug sink): reseed the
    // coverage corpus from the prior cycle's Plans so a fresh process stands on its shoulders instead of re-warming
    // from empty. Absent the env var, the coverage corpus lives only in memory (today's behavior). Fresh entropy is
    // still drawn per process, so cycles compound the corpus without collapsing into the same trajectory.
    var guidedCorpusDir = Environment.GetEnvironmentVariable("VOPR_GUIDED_CORPUS_DIR");
    var guidedCorpus = string.IsNullOrWhiteSpace(guidedCorpusDir) ? null : new GuidedCorpusStore(guidedCorpusDir);
    if (guidedCorpus is null)
    {
        Console.WriteLine(
            "  guided mode: VOPR_CORPUS_DIR is the bug-Plan sink; the coverage corpus lives in memory (not persisted)");
    }
    else
    {
        var seedPlans = guidedCorpus.LoadAll();
        if (seedPlans.Count == 0)
        {
            Console.WriteLine($"  coverage corpus persistence ON ({guidedCorpus.Dir}) — starting fresh (empty)");
        }
        else
        {
            var (reloaded, regressed) = swarm.Seed(seedPlans, cts.Token);
            Console.WriteLine(
                $"  reseeded {reloaded} corpus Plan(s) from {guidedCorpus.Dir}"
                + (regressed > 0 ? $" — {regressed} now trip an oracle → persisted to the bug sink as findings" : ""));
        }
    }

    var guidedTally = swarm.Run(
        maxIterations: maxRuns,
        cancellationToken: cts.Token,
        onTally: s => Console.WriteLine(
            $"  {s.Iterations} iters, {s.IterationsPerSecond:F0} iters/sec, {s.UniqueFailures} unique failures, "
            + $"corpus {s.CorpusSize}, tuples {s.InteractionTuples}"));

    // Log the search-RNG base FIRST — replays the whole single-threaded run (multi-threaded artifacts
    // still replay from themselves via FaultPlan.Replay + invariant-ID match, ADR 0018).
    Console.WriteLine(
        $"search-rng-base 0x{guidedTally.EntropyBase:X16}  "
        + "(replay with CoverageGuidedSwarm.Run(entropyBase: …, workerCount: 1))");
    Console.WriteLine(
        $"done: {guidedTally.Iterations} iterations ({guidedTally.TraceMutations} trace-mutations) in "
        + $"{guidedTally.Elapsed.TotalSeconds:F1}s ({guidedTally.IterationsPerSecond:F0} iters/sec), "
        + $"corpus {guidedTally.CorpusSize}, {guidedTally.InteractionTuples} interaction-tuples, "
        + $"{guidedTally.UniqueFailures} unique failures across {guidedTally.TotalFailures} failing runs");
    foreach (var (id, count) in guidedTally.FailuresByInvariant.OrderByDescending(kvp => kvp.Value))
    {
        Console.WriteLine($"  {id}: {count} hit(s) → {store.PathFor(id)}");
    }
    Console.WriteLine(guidedTally.Coverage);
    if (guidedCorpus is not null)
    {
        var corpusPlans = swarm.CorpusPlans;
        guidedCorpus.SaveAll(corpusPlans);
        Console.WriteLine($"  saved {corpusPlans.Count} corpus Plan(s) to {guidedCorpus.Dir} (next cycle reseeds from here)");
    }
    AppendLedger("guided", guidedTally.Elapsed.TotalSeconds, guidedTally.Iterations, guidedTally.TotalVirtualTime, guidedTally.EntropyBase, guidedTally.FailuresByInvariant);
    return;
}

// ── Radioactive swarm (catastrophic max-chaos) ──────────────────────────────────────────────────────
// Uniform-random over SwarmConfig.Radioactive instead of the calm in-envelope SwarmConfig.FromSeed: every
// fault axis maxed, isolation can be permanent, throttles combined. The world never converges by design, so
// the bound-dependent liveness oracles are disarmed (set in the config's RadioactiveMode); every SAFETY
// oracle stays armed, so a persisted finding here is a real consistency bug, not an over-intensity artifact.
if (radioactive)
{
    var radioactiveRunner = new VoprRunner(store, SwarmConfig.Radioactive);
    Console.WriteLine(
        $"VOPR RADIOACTIVE swarm — {Environment.ProcessorCount} workers, corpus {store.CorpusDir}"
        + (maxRuns is { } rcap ? $", bounded to {rcap} runs" : "")
        + (duration is { } rw ? $", stopping after {rw}" : "")
        + (maxRuns is null && duration is null ? " (Ctrl-C to stop)" : " (Ctrl-C to stop early)"));
    Console.WriteLine(
        "  catastrophic regime: all fault axes maxed + permanent node loss + combined throttles; bound-dependent"
        + " liveness oracles (DrainLiveness, ExecuteLiveness) are DISARMED — only safety-oracle trips are findings");

    var radioactiveTally = radioactiveRunner.Run(
        maxRuns: maxRuns,
        cancellationToken: cts.Token,
        onTally: s => Console.WriteLine(
            $"  {s.TotalRuns} runs, {s.UniqueFailures} unique failures, {s.RunsPerSecond:F0} runs/sec"));

    Console.WriteLine($"entropy-base 0x{radioactiveTally.EntropyBase:X16}  (replay with VoprRunner.Run(entropyBase: …) + SwarmConfig.Radioactive)");
    Console.WriteLine(
        $"done: {radioactiveTally.TotalRuns} runs in {radioactiveTally.Elapsed.TotalSeconds:F1}s "
        + $"({radioactiveTally.RunsPerSecond:F0} runs/sec), {radioactiveTally.UniqueFailures} unique safety failures "
        + $"across {radioactiveTally.TotalFailures} failing runs");
    foreach (var (id, count) in radioactiveTally.FailuresByInvariant.OrderByDescending(kvp => kvp.Value))
    {
        Console.WriteLine($"  {id}: {count} hit(s) → {store.PathFor(id)}");
    }
    AppendLedger("radioactive", radioactiveTally.Elapsed.TotalSeconds, radioactiveTally.TotalRuns, radioactiveTally.TotalVirtualTime, radioactiveTally.EntropyBase, radioactiveTally.FailuresByInvariant);
    return;
}

Console.WriteLine(
    $"VOPR forever-runner — {Environment.ProcessorCount} workers, corpus {store.CorpusDir}"
    + (maxRuns is { } cap ? $", bounded to {cap} runs" : "")
    + (duration is { } w ? $", stopping after {w}" : "")
    + (maxRuns is null && duration is null ? " (Ctrl-C to stop)" : " (Ctrl-C to stop early)"));

var tally = runner.Run(
    maxRuns: maxRuns,
    cancellationToken: cts.Token,
    onTally: s => Console.WriteLine(
        $"  {s.TotalRuns} runs, {s.UniqueFailures} unique failures, {s.RunsPerSecond:F0} runs/sec"));

// Log the entropy base FIRST — it is the single key that replays the whole run.
Console.WriteLine($"entropy-base 0x{tally.EntropyBase:X16}  (replay with VoprRunner.Run(entropyBase: …))");
Console.WriteLine(
    $"done: {tally.TotalRuns} runs in {tally.Elapsed.TotalSeconds:F1}s ({tally.RunsPerSecond:F0} runs/sec), "
    + $"{tally.UniqueFailures} unique failures across {tally.TotalFailures} failing runs");
foreach (var (id, count) in tally.FailuresByInvariant.OrderByDescending(kvp => kvp.Value))
{
    Console.WriteLine($"  {id}: {count} hit(s) → {store.PathFor(id)}");
}
// The unioned coverage complement (issue 0090): surfaced here so a random run reports edge/Situation
// saturation as honestly as the guided run does — the tally already carries it (issue 0201).
if (tally.Coverage is { } coverage)
{
    Console.WriteLine(coverage);
}
AppendLedger("random", tally.Elapsed.TotalSeconds, tally.TotalRuns, tally.TotalVirtualTime, tally.EntropyBase, tally.FailuresByInvariant);

// Appends one run to the cross-run coverage ledger when VOPR_LEDGER is set (mirrors VOPR_CORPUS_DIR — a casual
// run leaves it unset and records nothing). Stamps the wall-clock instant and the optional build SHA (VOPR_RUN_SHA).
static void AppendLedger(
    string mode, double wallSeconds, long sims, TimeSpan virtualTime, ulong entropyBase,
    IReadOnlyDictionary<InvariantId, long> findings)
{
    var ledgerPath = Environment.GetEnvironmentVariable("VOPR_LEDGER");
    if (string.IsNullOrWhiteSpace(ledgerPath))
    {
        return;
    }
    CoverageLedger.Append(ledgerPath, new LedgerEntry
    {
        TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
        Mode = mode,
        WallSeconds = wallSeconds,
        Sims = sims,
        VirtualSeconds = virtualTime.TotalSeconds,
        EntropyBase = $"0x{entropyBase:X16}",
        FindingsByInvariant = findings.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
        GitSha = Environment.GetEnvironmentVariable("VOPR_RUN_SHA"),
    });
    Console.WriteLine(
        $"ledger: appended {mode} run ({sims:N0} sims, {virtualTime.TotalHours:N0} cluster-hours) to {ledgerPath}");
}

// Parses a duration: a bare number is seconds; a trailing s/m/h/d multiplies accordingly (e.g. 90s, 30m, 8h, 1d).
// Returns null on anything unparseable so the arg loop simply ignores a malformed --duration value.
static TimeSpan? ParseDuration(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }
    if (double.TryParse(value, out var bareSeconds))
    {
        return bareSeconds > 0 ? TimeSpan.FromSeconds(bareSeconds) : null;
    }
    var unit = value[^1];
    if (!double.TryParse(value[..^1], out var amount) || amount <= 0)
    {
        return null;
    }
    return char.ToLowerInvariant(unit) switch
    {
        's' => TimeSpan.FromSeconds(amount),
        'm' => TimeSpan.FromMinutes(amount),
        'h' => TimeSpan.FromHours(amount),
        'd' => TimeSpan.FromDays(amount),
        _ => null,
    };
}
