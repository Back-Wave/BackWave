namespace BackWave.Storage;

/// <summary>
/// A recurring schedule as stored: a cron-defined template that mints scheduled job instances as
/// time passes. The schedule and the jobs it mints are distinct things with distinct lifecycles.
/// </summary>
public sealed record ScheduleRecord
{
    /// <summary>The schedule's stable, unique identifier. Upserting with the same id redefines the schedule in place.</summary>
    public required string ScheduleId { get; init; }

    /// <summary>The canonical six-field cron expression — the single stored representation of when the schedule fires.</summary>
    public required string Cron { get; init; }

    /// <summary>The serialization-stable name of the payload type each minted instance carries.</summary>
    public required string WireName { get; init; }

    /// <summary>
    /// The template payload each minted instance carries. This is not populated by the schedule
    /// listing read (that hot path returns it empty); the store re-reads it from the row when minting,
    /// so the per-poll schedule load never fetches blobs.
    /// </summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>The queue the minted instances are enqueued into.</summary>
    public required string Queue { get; init; }

    /// <summary>The instant up to which due ticks have been resolved (inclusive). Advances as occurrences are minted.</summary>
    public required DateTimeOffset Cursor { get; init; }

    /// <summary>The IANA time-zone the cron is evaluated in; null means UTC (the default).</summary>
    public string? TimeZoneId { get; init; }

    /// <summary>What the schedule does about occurrences missed while the system was down. Defaults to skipping them.</summary>
    public CatchUpPolicy CatchUp { get; init; } = CatchUpPolicy.Skip;

    /// <summary>When true, do not mint a new instance while a previous one is still non-terminal; the skipped tick is recorded.</summary>
    public bool NoOverlap { get; init; }

    /// <summary>The most recently skipped ticks (from no-overlap or catch-up), newest last, bounded so the list cannot grow without limit.</summary>
    public IReadOnlyList<DateTimeOffset> SkippedTicks { get; init; } = [];
}

/// <summary>What a recurring schedule does about occurrences missed while the system was down.</summary>
public enum CatchUpPolicy
{
    /// <summary>Missed means missed: mint nothing for the missed ticks (the default).</summary>
    Skip,

    /// <summary>Mint exactly one make-up run to stand in for the whole missed set.</summary>
    Coalesce,
}

/// <summary>A schedule paired with the one claim-relevant fact the mint planner needs.</summary>
/// <param name="Schedule">The schedule row.</param>
/// <param name="HasLiveInstance">Whether the schedule currently has a non-terminal instance (relevant to no-overlap minting).</param>
public sealed record ScheduleSnapshot(ScheduleRecord Schedule, bool HasLiveInstance);
