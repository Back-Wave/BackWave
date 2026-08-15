namespace BackWave.Torture;

/// <summary>Which relational adapter the in-place upgrade harness drives. SQLite is out of scope (see UpgradePopulator).</summary>
internal enum UpgradeAdapter
{
    Postgres,
    SqlServer,
}

/// <summary>Everything one in-place upgrade harness run needs (issue 0202, ADR 0038).</summary>
internal sealed record UpgradeOptions
{
    public required UpgradeAdapter Adapter { get; init; }

    /// <summary>Deterministic seed for the live-workload KeySpace and the populated-fixture identifiers.</summary>
    public ulong Seed { get; init; } = 0x0202_0038_C0FFEEUL;

    /// <summary>How long the post-upgrade live workload hammers the freshly-migrated store.</summary>
    public TimeSpan WorkloadDuration { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>Synthetic live-workload clients (kept modest so the battery stays fast).</summary>
    public int Clients { get; init; } = 4;

    /// <summary>Attempt ceiling the live-workload clients and the drainer enforce.</summary>
    public int MaxAttempts { get; init; } = 4;

    /// <summary>Bounded wait for the post-workload drain to reach quiescence.</summary>
    public TimeSpan DrainBound { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>The fixed concurrency limit on the governed queue (the statically-audited I3 queue).</summary>
    public int GovernedLimit { get; init; } = 2;

    /// <summary>When set, runs only this single prior version instead of the whole v1..current-1 matrix (fast self-tests).</summary>
    public int? OnlyPriorVersion { get; init; }

    /// <summary>
    /// When true, hand-breaks the upgrade: after the real migration the harness deletes one populated
    /// fixture job — simulating a migration step that loses rows — so the conservation oracle must go
    /// RED. Off for normal runs; armed by <c>--sabotage</c> or <c>BACKWAVE_UPGRADE_SABOTAGE=1</c>.
    /// </summary>
    public bool Sabotage { get; init; }
}
