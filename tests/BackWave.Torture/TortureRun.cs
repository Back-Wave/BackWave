using System.Diagnostics;
using BackWave.Diagnostics;
using BackWave.Storage;

namespace BackWave.Torture;

/// <summary>
/// Orchestrates one torture run: provision the target, set the governed queue's static limit,
/// hammer the store with N clients for the time box (in-process tasks, or real OS child processes
/// for the SQLite cross-process shape), drain to quiescence, run the full oracle audit, and either
/// report coverage stats (clean) or write the artifact bundle (violations).
/// </summary>
internal static class TortureRun
{
    public static async Task<int> RunAsync(TortureOptions options)
    {
        Console.WriteLine($"torture: seed 0x{options.Seed:x16} ({options.Seed})");
        Console.WriteLine(
            $"torture: adapter={options.Adapter} clients={options.Clients} duration={options.Duration.TotalSeconds:F0}s " +
            $"maxAttempts={options.MaxAttempts} governedLimit={options.GovernedLimit}");

        var keys = new KeySpace(options.Seed);
        await using var target = CreateTarget(options);

        try
        {
            await target.InitializeAsync(CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"torture: {exception.Message}");
            return 2;
        }

        var setup = target.CreateStore();
        await setup.SetConcurrencyLimitAsync(keys.GovernedQueue, options.GovernedLimit, "torture-setup", DateTimeOffset.UtcNow);

        var started = DateTimeOffset.UtcNow;
        var journal = new Journal();
        // Nothing else in this process can see a degraded trigger: it throws nothing, and a torture process
        // registers no log provider, so the backwave.invariant.violations counter is its only surface here.
        // Subscribed for the whole run - the drain and the audit included, since those drive the adapter too.
        using var degrades = new DegradeWatch(journal, "torture-run");
        var wall = Stopwatch.StartNew();

        // One time box for the whole workload, shared by the clients, the progress line and the
        // mid-run audit - which cancels it early on the first finding, so the store stays near the cause.
        using var timebox = new CancellationTokenSource(options.Duration);
        var midRunViolations = new ViolationSink(TorturePhase.Workload);
        var midRun = new MidRunAudit(target, journal, keys, options, midRunViolations);
        Console.WriteLine($"torture: mid-run audit every {midRun.Interval.TotalSeconds:F0}s");
        var midRunLoop = midRun.RunAsync(timebox);

        if (options.Adapter == TortureAdapter.SqliteMultiProcess)
        {
            await RunChildProcessesAsync((SqliteTarget)target, options, journal, started, timebox.Token);
        }
        else
        {
            await RunInProcessClientsAsync(target, keys, options, journal, started, timebox.Token);
        }
        // The mid-run pass drives the same adapter the clients do, so its own reads can trip the same
        // production fail-stop triggers. Caught here for the same reason the drain and the audit below are:
        // an unguarded await would crash the process and the run would report nothing and write no bundle.
        try
        {
            await midRunLoop;
        }
        catch (InvariantViolationException violation)
        {
            midRunViolations.Add(new TortureViolation(
                TortureInvariant.HaltTriggerFired,
                $"Halt trigger {violation.Trigger} fired during the mid-run audit - a production worker group " +
                $"would have fail-stopped: {violation.Message}"));
        }
        var workloadSeconds = wall.Elapsed.TotalSeconds;

        var entries = journal.Entries;
        var midRunPercent = workloadSeconds > 0 ? midRun.Cost.TotalSeconds / workloadSeconds * 100 : 0;
        Console.WriteLine(
            $"torture: mid-run audit - {midRun.Passes} pass(es), {midRun.TransitionsWalked} transition(s) walked, " +
            $"{midRun.Skips} skip(s), {midRun.Cost.TotalSeconds:F2}s ({midRunPercent:F2}% of wall)");

        var violations = new List<TortureViolation>(midRunViolations.Snapshot());
        Auditor? auditor = null;
        string? postDrainAbsent = null;
        if (violations.Count > 0)
        {
            // Fail fast: draining first would rewrite exactly the state that explains the finding.
            postDrainAbsent = "the mid-run audit found a violation; the drain and the post-drain audit were skipped " +
                "so the store stays as close to the cause as the run can leave it";
            Console.WriteLine($"torture: workload cut short - {entries.Count} journal entries; skipping the drain.");
        }
        else
        {
            Console.WriteLine($"torture: workload done — {entries.Count} journal entries; draining (bound {options.DrainBound.TotalSeconds:F0}s)…");
            var drainViolations = new ViolationSink(TorturePhase.Drain);
            var postDrain = new ViolationSink(TorturePhase.PostDrain);
            // The drainer and the auditor drive the same adapter the clients did, so they can trip the
            // same production fail-stop triggers. Caught here rather than left to crash the process, so
            // the run still reports RED naming the trigger and still writes its artifact bundle.
            try
            {
                var drainSeconds = Stopwatch.StartNew();
                var drainer = new Drainer(target.CreateStore(), keys, options, target.IsTransientFault);
                drainViolations.AddRange(await drainer.DrainAsync(CancellationToken.None));
                Console.WriteLine($"torture: quiescent after {drainSeconds.Elapsed.TotalSeconds:F1}s - auditing…");

                var auditSeconds = Stopwatch.StartNew();
                auditor = new Auditor(target.CreateStore(), keys, options);
                await auditor.AuditAsync(entries, postDrain, CancellationToken.None);
                postDrain.AddRange(await target.RawAuditAsync(CancellationToken.None));
                Console.WriteLine(
                    $"torture: post-drain audit - {auditor.ScannedJobs.Count} jobs in {auditSeconds.Elapsed.TotalSeconds:F1}s");
            }
            catch (InvariantViolationException violation)
            {
                postDrain.Add(new TortureViolation(
                    TortureInvariant.HaltTriggerFired,
                    $"Halt trigger {violation.Trigger} fired during the drain/audit - a production worker group " +
                    $"would have fail-stopped: {violation.Message}"));
            }

            violations.AddRange(drainViolations.Snapshot());
            violations.AddRange(postDrain.Snapshot());
        }

        // The Degrade sweep, once, over the WHOLE journal - this process's counted triggers and every child
        // process's alike. It runs on both branches and after the drain on purpose: the fail-fast branch
        // never reaches an audit, and the audit's journal view is pinned before the drain, so a trigger
        // degraded by the drain or the audit itself would be invisible to either.
        var degradeFindings = new ViolationSink(TorturePhase.PostDrain);
        Checks.DegradedTriggers(journal.Entries, degradeFindings);
        violations.AddRange(degradeFindings.Snapshot());

        var stats = new WorkloadStats(entries, keys);
        Console.WriteLine(stats.Render());

        // Cross-run coverage ledger (the store-mode twin of VOPR's): append one line per run — clean or
        // red — so cumulative real-load coverage accrues in docs/torture-coverage.md. Gated on the path env.
        var ledgerPath = Environment.GetEnvironmentVariable("BACKWAVE_TORTURE_LEDGER");
        if (!string.IsNullOrWhiteSpace(ledgerPath))
        {
            TortureLedger.Append(ledgerPath, TortureLedger.BuildEntry(
                options, Environment.GetEnvironmentVariable("BACKWAVE_TORTURE_SHA") ?? "",
                entries, keys, auditor?.ScannedJobs.Count ?? 0, violations, (DateTimeOffset.UtcNow - started).TotalSeconds));
        }

        if (violations.Count == 0)
        {
            Console.WriteLine("torture: CLEAN — every oracle held.");
            return 0;
        }

        Console.WriteLine($"torture: RED — {violations.Count} violation(s):");
        foreach (var group in violations.GroupBy(v => v.Invariant))
        {
            Console.WriteLine($"  {group.Key}: {group.Count()} hit(s)");
            foreach (var violation in group.Take(5))
            {
                Console.WriteLine($"    - {violation.Message}");
            }
            if (group.Count() > 5)
            {
                Console.WriteLine($"    … and {group.Count() - 5} more");
            }
        }

        // journal.Entries, not the pinned `entries`: the bundle should carry everything recorded, including a
        // trigger degraded during the drain or the audit, whose finding is in violations.json either way.
        var bundle = await ArtifactWriter.WriteAsync(
            options, journal.Entries, violations, auditor, postDrainAbsent, target, stats, midRun, CancellationToken.None);
        Console.WriteLine($"torture: artifact bundle → {bundle}");
        Console.WriteLine("torture: file the finding as torture-NNNN and distill every confirmed bug into a deterministic");
        Console.WriteLine("torture: Conformance clause (docs/adapter-concurrency-review-checklist.md, 0196 pattern).");
        return 1;
    }

    private static ITortureTarget CreateTarget(TortureOptions options) => options.Adapter switch
    {
        TortureAdapter.Postgres => new PostgresTarget(),
        TortureAdapter.SqlServer => new SqlServerTarget(),
        TortureAdapter.Oracle => new OracleTarget(),
        // Fresh file per run — a reused file would put a previous run's jobs in front of the audit.
        TortureAdapter.Sqlite or TortureAdapter.SqliteMultiProcess => new SqliteTarget(
            Path.Combine(Path.GetTempPath(), $"backwave_torture_{options.Seed:x16}_{Guid.NewGuid():N}.db"), migrate: true),
        _ => throw new ArgumentOutOfRangeException(nameof(options)),
    };

    private static async Task RunInProcessClientsAsync(
        ITortureTarget target, KeySpace keys, TortureOptions options, Journal journal, DateTimeOffset started,
        CancellationToken timebox)
    {
        var clients = Enumerable.Range(0, options.Clients)
            .Select(i => new WorkloadClient(i, target.CreateStore(), keys, journal, options, started)
            {
                IsTransient = target.IsTransientFault,
            })
            .ToList();

        var progress = ProgressAsync(() => clients.Sum(c => c.OpsIssued), timebox);
        await Task.WhenAll(clients.Select(c => Task.Run(() => c.RunAsync(timebox))));
        await progress;
    }

    /// <summary>
    /// The SQLite cross-process shape: real OS child processes (this same executable, verb
    /// <c>client</c>) all hammering one WAL file. The parent only coordinates — and then drains
    /// and audits over the merged child journals.
    /// </summary>
    private static async Task RunChildProcessesAsync(
        SqliteTarget target, TortureOptions options, Journal journal, DateTimeOffset started,
        CancellationToken timebox)
    {
        var perChild = Math.Max(1, options.Clients / options.Processes);
        var children = new List<(Process Process, string JournalPath, int Base)>();

        for (var p = 0; p < options.Processes; p++)
        {
            var journalPath = Path.Combine(Path.GetTempPath(), $"backwave_torture_{options.Seed:x16}_child{p}.jsonl");
            File.Delete(journalPath);
            var info = new ProcessStartInfo(Environment.ProcessPath!)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                // The stop is ASKED for, not forced: closing this pipe is the signal the child watches.
                RedirectStandardInput = true,
            };
            info.ArgumentList.Add("client");
            info.ArgumentList.Add("--db"); info.ArgumentList.Add(target.DbPath);
            info.ArgumentList.Add("--seed"); info.ArgumentList.Add(options.Seed.ToString());
            info.ArgumentList.Add("--client-base"); info.ArgumentList.Add((p * perChild).ToString());
            info.ArgumentList.Add("--clients"); info.ArgumentList.Add(perChild.ToString());
            info.ArgumentList.Add("--duration"); info.ArgumentList.Add(options.Duration.TotalSeconds.ToString("F0"));
            info.ArgumentList.Add("--journal"); info.ArgumentList.Add(journalPath);
            info.ArgumentList.Add("--max-attempts"); info.ArgumentList.Add(options.MaxAttempts.ToString());
            info.ArgumentList.Add("--started"); info.ArgumentList.Add(started.UtcTicks.ToString());
            var process = Process.Start(info)
                ?? throw new InvalidOperationException("Failed to start torture child process.");
            children.Add((process, journalPath, p * perChild));
        }
        Console.WriteLine($"torture: spawned {children.Count} child processes × {perChild} clients on {target.DbPath}");

        // A kill never reaches a finally, and the child writes its journal in one, so the parent ASKS
        // first and kills only what will not go. The ask is the close of the child's stdin, which lands
        // the moment the time box ends - at the end of the run, or early when the mid-run audit found
        // something. Each child then stops its clients, writes its journal file, and exits on its own.
        using var watchdog = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, timebox);
            }
            catch (OperationCanceledException)
            {
                // The time box ended, which is the only reason this wait ever returns.
            }
            foreach (var (process, _, _) in children)
            {
                AskChildToStop(process);
            }
            try
            {
                // The kill clock starts only once every child was asked, so the grace window is the
                // same for all of them whether the run ended on its time box or on a finding.
                watchdog.CancelAfter(ChildStopGrace);
            }
            catch (ObjectDisposedException)
            {
                // Every child already exited and the loop below returned. Nothing left to kill.
            }
        });

        foreach (var (process, journalPath, clientBase) in children)
        {
            var killed = false;
            try
            {
                await process.WaitForExitAsync(watchdog.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                killed = true;
                journal.Record(new JournalEntry
                {
                    Client = $"child-{clientBase}", Op = Ops.ClientCrash,
                    T0 = DateTimeOffset.UtcNow.UtcTicks, T1 = DateTimeOffset.UtcNow.UtcTicks,
                    Detail = $"child did not exit within {ChildStopGrace.TotalSeconds:F0}s of the stop request " +
                        "and was killed; its journal may be short",
                });
            }
            if (!killed && process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                journal.Record(new JournalEntry
                {
                    Client = $"child-{clientBase}", Op = Ops.ClientCrash,
                    T0 = DateTimeOffset.UtcNow.UtcTicks, T1 = DateTimeOffset.UtcNow.UtcTicks,
                    Detail = $"child exited {process.ExitCode}: {stderr[..Math.Min(stderr.Length, 2000)]}",
                });
            }
            // Merged whatever happened. A killed child still flushed part of its journal, and a run that
            // ended on a finding is exactly the run whose evidence matters most.
            if (File.Exists(journalPath))
            {
                foreach (var entry in await Journal.ReadAsync(journalPath))
                {
                    journal.Record(entry);
                }
                File.Delete(journalPath);
            }
        }
    }

    /// <summary>How long a child has to land its journal and exit after the parent asked it to stop.</summary>
    private static readonly TimeSpan ChildStopGrace = TimeSpan.FromSeconds(60);

    /// <summary>Closes the child's stdin, which is the stop signal it watches. A child already gone needs none.</summary>
    private static void AskChildToStop(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception)
        {
            // The child exited on its own time box and closed the pipe first. Nothing to ask.
        }
    }

    /// <summary>The child-process entry: run this process's slice of clients and write the journal file.</summary>
    public static async Task<int> RunChildAsync(
        string dbPath, ulong seed, int clientBase, int clients, TimeSpan duration, string journalPath,
        int maxAttempts, long startedTicks)
    {
        var options = new TortureOptions
        {
            Adapter = TortureAdapter.Sqlite,
            Seed = seed,
            Duration = duration,
            MaxAttempts = maxAttempts,
        };
        var keys = new KeySpace(seed);
        var started = new DateTimeOffset(startedTicks, TimeSpan.Zero);
        var journal = new Journal();
        // The counter is per-process, so the parent's watch cannot see this process's degraded triggers.
        // Journaling them here is what carries them home: this journal is merged into the parent's.
        using var degrades = new DegradeWatch(journal, $"child-{clientBase}");
        await using var target = new SqliteTarget(dbPath, migrate: false);

        try
        {
            using var timebox = new CancellationTokenSource(duration);
            WatchForParentStop(timebox);
            var workers = Enumerable.Range(clientBase, clients)
                .Select(i => new WorkloadClient(i, target.CreateStore(), keys, journal, options, started)
                {
                    IsTransient = target.IsTransientFault,
                })
                .ToList();
            await Task.WhenAll(workers.Select(w => Task.Run(() => w.RunAsync(timebox.Token))));
            return 0;
        }
        catch (Exception exception)
        {
            journal.Record(new JournalEntry
            {
                Client = $"child-{clientBase}", Op = Ops.ClientCrash,
                T0 = DateTimeOffset.UtcNow.UtcTicks, T1 = DateTimeOffset.UtcNow.UtcTicks,
                Detail = exception.ToString(),
            });
            return 1;
        }
        finally
        {
            await journal.WriteAsync(journalPath);
        }
    }

    /// <summary>
    /// Ends this child's time box when the parent closes its stdin. The parent asks that way when the run
    /// ends, and early when its mid-run audit found something, so the child lands the journal its finally
    /// writes instead of dying to a kill that no finally survives. The duration still ends a child whose
    /// parent asks for nothing.
    /// </summary>
    private static void WatchForParentStop(CancellationTokenSource timebox)
    {
        // Nothing waits on this: it blocks on a pipe the parent may never close, and the run must not.
        _ = Task.Run(async () =>
        {
            try
            {
                await using var stdin = Console.OpenStandardInput();
                var buffer = new byte[1];
                while (await stdin.ReadAsync(buffer) > 0)
                {
                    // The parent sends no data. Only the close of the pipe means anything.
                }
            }
            catch (Exception)
            {
                // No stdin at all, or a pipe torn down under us. Either reads as the same request.
            }
            try
            {
                await timebox.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // The duration ended the run first and the caller already disposed the source.
            }
        });
    }

    private static async Task ProgressAsync(Func<long> ops, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                Console.WriteLine($"torture: … {ops()} ops issued");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
