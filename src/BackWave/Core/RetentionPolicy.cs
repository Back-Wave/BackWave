namespace BackWave.Core;

/// <summary>
/// Keep-then-purge retention: how long terminal jobs stay queryable before background sweeps
/// delete them. The retention clock starts at the instant a job reached its terminal state, not
/// when it was enqueued. A workflow is kept as a unit: its clock starts when every member is
/// terminal, and the whole workflow follows the window of its worst member, so one dead-lettered
/// or quarantined member keeps all of its siblings for <see cref="KeepDeadLettered"/>.
/// </summary>
public sealed record RetentionPolicy
{
    /// <summary>How long to keep Succeeded and Cancelled jobs queryable before they are swept. Defaults to 24 hours.</summary>
    public TimeSpan KeepSucceeded { get; init; } = TimeSpan.FromHours(24);

    /// <summary>How long to keep Dead-Lettered and Quarantined jobs queryable before they are swept — long enough that someone can still inspect a failure. Defaults to 14 days.</summary>
    public TimeSpan KeepDeadLettered { get; init; } = TimeSpan.FromDays(14);

    /// <summary>The default retention policy: 24 hours for succeeded or cancelled jobs, 14 days for dead-lettered or quarantined jobs.</summary>
    public static RetentionPolicy Default { get; } = new();
}
