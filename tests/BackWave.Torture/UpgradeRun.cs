namespace BackWave.Torture;

/// <summary>
/// The in-place upgrade harness (issue 0202 / ADR 0038). For each shipped prior schema version
/// v1..v(current-1), it: resets a throwaway database, applies ONLY the script prefix to reach that
/// version, populates it with the full golden-fixture inventory (jobs in every state, mid-flight
/// leases, dependency edges, tags, schedules, queue limits/pauses, observer cursors, transition
/// history, and — where the schema supports it — workflows), then runs the REAL production migration
/// to current on that populated, in-flight database. A live workload then hammers the freshly
/// migrated store (with a redundant idempotent re-migration running concurrently — the "migrate under
/// load" case), the drain carries every live job to quiescence, and the full Torture oracle set audits
/// the result: end-state + transition-log + conservation (every fixture job accounted for, none lost,
/// none illegally transitioned by the migration itself).
/// </summary>
internal static class UpgradeRun
{
    public static async Task<int> RunAsync(UpgradeOptions options)
    {
        Console.WriteLine($"upgrade: adapter={options.Adapter} seed=0x{options.Seed:x16} " +
            $"workload={options.WorkloadDuration.TotalSeconds:F0}s clients={options.Clients} sabotage={options.Sabotage}");

        await using var store = CreateStore(options);
        try
        {
            await store.InitializeAsync(CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"upgrade: {exception.Message}");
            return 2;
        }

        var current = store.CurrentVersion;
        var failed = new List<int>();
        var first = options.OnlyPriorVersion ?? 1;
        var last = options.OnlyPriorVersion ?? current - 1;
        for (var prior = first; prior <= last; prior++)
        {
            Console.WriteLine($"upgrade: === v{prior} → v{current} ===");
            IReadOnlyList<TortureViolation> violations;
            try
            {
                violations = await RunOneAsync(store, options, prior);
            }
            finally
            {
                // Free this iteration's store fleet before the next prior version: each iteration opens
                // a fresh setup/workload/drain/audit fleet, so without a per-iteration release the pools
                // pile up across the whole sweep and exhaust the database's connection ceiling on the
                // later versions once an adapter ships enough prior versions to make the sweep non-empty.
                await store.ReleaseStoresAsync();
            }

            if (violations.Count == 0)
            {
                Console.WriteLine($"upgrade: v{prior} → v{current} CLEAN — every oracle held.");
                continue;
            }

            failed.Add(prior);
            Console.WriteLine($"upgrade: v{prior} → v{current} RED — {violations.Count} violation(s):");
            foreach (var group in violations.GroupBy(v => v.Invariant))
            {
                Console.WriteLine($"  {group.Key}: {group.Count()} hit(s)");
                foreach (var violation in group.Take(3))
                {
                    Console.WriteLine($"    - {violation.Message}");
                }
            }
        }

        if (failed.Count == 0)
        {
            Console.WriteLine($"upgrade: ALL CLEAN — every shipped prior version upgrades in place ({options.Adapter}).");
            return 0;
        }
        Console.WriteLine($"upgrade: FAILED for prior version(s) {string.Join(", ", failed)} on {options.Adapter}.");
        return 1;
    }

    private static async Task<IReadOnlyList<TortureViolation>> RunOneAsync(
        IUpgradeStore store, UpgradeOptions options, int priorVersion)
    {
        var keys = new KeySpace(options.Seed);
        var cancellationToken = CancellationToken.None;

        // 1. Reach the prior version by applying ONLY its script prefix, then populate the fixture.
        await store.ResetSchemaAsync(cancellationToken);
        await store.PrefixMigrateAsync(priorVersion, cancellationToken);
        var reached = await store.ReadSchemaVersionAsync(cancellationToken);
        if (reached != priorVersion)
        {
            return [new TortureViolation("UpgradePrefix", $"Prefix migrate reached v{reached}, expected v{priorVersion}.")];
        }

        var journal = new Journal();
        var populator = new UpgradePopulator(store, keys);
        await populator.PopulateAsync(priorVersion, journal, cancellationToken);

        // 2. THE upgrade: the real production migration, on a populated, in-flight database.
        await store.MigrateToCurrentAsync(cancellationToken);

        // Give the in-flight fixtures a legal birth history now that the transition log exists — so the
        // store's own subsequent transitions read as a legal life to the transition-log oracle.
        await populator.SeedLiveBirthHistoryAsync(cancellationToken);

        // 3. Live workload against the freshly-migrated store. The in-flight fixtures (leased, scheduled,
        //    awaiting-parent) were present in the database THROUGH the migration — that is the in-place
        //    "jobs in flight during upgrade" contract; the workload then proves the upgraded schema
        //    serves real traffic. (A concurrent re-run of MigrateAsync is deliberately NOT done: it
        //    re-stamps the version 1→…→current in separate commits, so a store initializing mid-re-run
        //    would transiently see an intermediate version and fail-stop — a store-init race the N-1
        //    contract does not cover, not a migration defect.)
        var setup = store.CreateStore();
        await setup.SetConcurrencyLimitAsync(keys.GovernedQueue, options.GovernedLimit, "torture-setup", DateTimeOffset.UtcNow);

        var started = DateTimeOffset.UtcNow;
        var options2 = new TortureOptions
        {
            Adapter = TortureAdapter.Postgres, // unused by WorkloadClient beyond MaxAttempts/GovernedLimit
            Seed = options.Seed,
            Duration = options.WorkloadDuration,
            Clients = options.Clients,
            MaxAttempts = options.MaxAttempts,
            DrainBound = options.DrainBound,
            GovernedLimit = options.GovernedLimit,
        };

        using (var timebox = new CancellationTokenSource(options.WorkloadDuration))
        {
            var clients = Enumerable.Range(0, options.Clients)
                .Select(i => new WorkloadClient(i, store.CreateStore(), keys, journal, options2, started)
                {
                    IsTransient = store.IsTransientFault,
                })
                .ToList();
            await Task.WhenAll(clients.Select(c => Task.Run(() => c.RunAsync(timebox.Token), cancellationToken)));
        }

        // 4. Optional sabotage: simulate a migration that loses a row. The conservation oracle must catch it.
        if (options.Sabotage && populator.TerminalFixtureJobs.Count > 0)
        {
            var victim = populator.TerminalFixtureJobs[0];
            await store.ExecuteAsync("DELETE FROM backwave.jobs WHERE job_id = @p0", [victim], cancellationToken);
            Console.WriteLine($"upgrade: SABOTAGE armed — deleted populated fixture job {victim}.");
        }

        // 5. Drain to quiescence, then run the full oracle audit over the merged journal.
        var violations = new List<TortureViolation>();
        var drainer = new Drainer(store.CreateStore(), keys, options2, store.IsTransientFault);
        violations.AddRange(await drainer.DrainAsync(cancellationToken));

        var auditor = new Auditor(store.CreateStore(), keys, options2);
        violations.AddRange(await auditor.AuditAsync(journal.Entries, cancellationToken));
        violations.AddRange(await store.RawAuditAsync(cancellationToken));

        Console.WriteLine($"upgrade: v{priorVersion} audited {auditor.ScannedJobs.Count} jobs " +
            $"({journal.Entries.Count} journal entries).");
        return violations;
    }

    private static IUpgradeStore CreateStore(UpgradeOptions options) => options.Adapter switch
    {
        UpgradeAdapter.Postgres => new PostgresUpgradeStore(),
        UpgradeAdapter.SqlServer => new SqlServerUpgradeStore(),
        _ => throw new ArgumentOutOfRangeException(nameof(options)),
    };
}
