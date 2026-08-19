namespace BackWave.Torture;

/// <summary>Which adapter shape a torture run drives (issue 0200's four-row matrix).</summary>
internal enum TortureAdapter
{
    Postgres,
    SqlServer,
    Oracle,
    Sqlite,
    /// <summary>3–4 real OS child processes hammering one SQLite WAL file — the Embedded Adapter's cross-process promise.</summary>
    SqliteMultiProcess,
}

/// <summary>Everything a torture run needs, parsed once from the command line.</summary>
internal sealed record TortureOptions
{
    public required TortureAdapter Adapter { get; init; }

    /// <summary>The workload seed. Every random decision in the run derives from it; logged on every run.</summary>
    public required ulong Seed { get; init; }

    /// <summary>Wall-clock time box for the workload phase (drain and audit run after it).</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Total synthetic clients. In multi-process mode they are spread over <see cref="Processes"/>.</summary>
    public int Clients { get; init; } = 8;

    /// <summary>Child process count for <see cref="TortureAdapter.SqliteMultiProcess"/>.</summary>
    public int Processes { get; init; } = 4;

    /// <summary>Attempt ceiling the synthetic clients enforce (kept small so dead-letter paths run hot).</summary>
    public int MaxAttempts { get; init; } = 4;

    /// <summary>Bounded wait for the post-workload drain to reach quiescence (the liveness oracle's teeth).</summary>
    public TimeSpan DrainBound { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Where failure artifact bundles land.</summary>
    public string ArtifactsDir { get; init; } = "torture-artifacts";

    /// <summary>The fixed concurrency limit on the governed queue (the statically-audited I3 queue).</summary>
    public int GovernedLimit { get; init; } = 2;
}
