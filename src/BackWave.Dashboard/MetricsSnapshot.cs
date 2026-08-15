namespace BackWave.Dashboard;

/// <summary>
/// An immutable, point-in-time view of the dashboard's live in-process metrics: per-second
/// throughput over a fixed recent window plus the busiest and most-faulting job types. It is
/// <b>per-node and ephemeral</b> — it reflects only throughput observed on the node hosting the
/// dashboard, accumulated from the BackWave meter into a bounded in-memory ring buffer, and it
/// resets when that process restarts. Retained history and cross-node aggregation are the job of an
/// external metrics stack, not this snapshot.
/// </summary>
/// <param name="Enqueued">
/// The per-second count of jobs accepted by Enqueue over the window, one entry per second, oldest
/// first. Every series in this snapshot has the same length — the window size in seconds.
/// </param>
/// <param name="Processed">The per-second count of executions that succeeded over the window, oldest first.</param>
/// <param name="Failed">The per-second count of failed attempts over the window, oldest first.</param>
/// <param name="EnqueuedPerSecond">The enqueued rate, averaged across the window, in jobs per second.</param>
/// <param name="ProcessedPerSecond">The processed rate, averaged across the window, in jobs per second.</param>
/// <param name="FailedPerSecond">The failed-attempt rate, averaged across the window, in attempts per second.</param>
/// <param name="TopEndpoints">
/// The busiest job types by throughput, highest first, capped at a fixed number of rows with the
/// remainder folded into a single "other" row so the ranking never grows without bound.
/// </param>
/// <param name="FaultingEndpoints">
/// The job types with the highest fault rate (failed divided by attempts), highest first, capped at
/// a fixed number of rows with the remainder folded into a single "other" row.
/// </param>
public sealed record MetricsSnapshot(
    IReadOnlyList<long> Enqueued,
    IReadOnlyList<long> Processed,
    IReadOnlyList<long> Failed,
    double EnqueuedPerSecond,
    double ProcessedPerSecond,
    double FailedPerSecond,
    IReadOnlyList<EndpointThroughput> TopEndpoints,
    IReadOnlyList<EndpointFaultRate> FaultingEndpoints);

/// <summary>
/// One row of the Top Endpoints ranking: a job type and how fast it is being processed, averaged
/// across the metrics window. Everything past the capped top rows is aggregated under a single
/// "other" entry, so a workload with unbounded distinct job types cannot grow the ranking.
/// </summary>
/// <param name="WireName">
/// The job type (its wire name), or <c>"(other)"</c> for the row aggregating every job type past the
/// capped top rows.
/// </param>
/// <param name="ProcessedPerSecond">The processed rate for this job type, averaged across the window, in jobs per second.</param>
/// <param name="ApproxP95Ms">
/// The approximate 95th-percentile handler execution time for this job type over the window, in
/// milliseconds, or <see langword="null"/> when no execution latency was recorded for it. It is
/// interpolated from fixed latency buckets, so it is accurate only to the width of the bucket it falls
/// in — never an exact quantile. For exact distributions, use an external metrics stack.
/// </param>
/// <param name="ApproxP99Ms">
/// The approximate 99th-percentile handler execution time for this job type over the window, in
/// milliseconds, or <see langword="null"/> when no execution latency was recorded for it. Approximate
/// for the same reason as <paramref name="ApproxP95Ms"/>.
/// </param>
public sealed record EndpointThroughput(
    string WireName, double ProcessedPerSecond, double? ApproxP95Ms, double? ApproxP99Ms);

/// <summary>
/// One row of the Faulting Endpoints ranking: a job type with its failed and attempt counts over the
/// metrics window, from which its fault rate is derived. Everything past the capped top rows is
/// aggregated under a single "other" entry.
/// </summary>
/// <param name="WireName">
/// The job type (its wire name), or <c>"(other)"</c> for the row aggregating every faulting job type
/// past the capped top rows.
/// </param>
/// <param name="Failed">The number of failed attempts for this job type over the window.</param>
/// <param name="Attempts">The total number of attempts started for this job type over the window.</param>
/// <param name="ApproxP95Ms">
/// The approximate 95th-percentile handler execution time for this job type over the window, in
/// milliseconds (across all of its attempts, not only the failed ones), or <see langword="null"/> when
/// no execution latency was recorded for it. Interpolated from fixed latency buckets, so it is accurate
/// only to the width of the bucket it falls in — never an exact quantile.
/// </param>
/// <param name="ApproxP99Ms">
/// The approximate 99th-percentile handler execution time for this job type over the window, in
/// milliseconds, or <see langword="null"/> when no execution latency was recorded for it. Approximate
/// for the same reason as <paramref name="ApproxP95Ms"/>.
/// </param>
public sealed record EndpointFaultRate(
    string WireName, long Failed, long Attempts, double? ApproxP95Ms, double? ApproxP99Ms)
{
    /// <summary>
    /// The fault rate — <see cref="Failed"/> divided by <see cref="Attempts"/> — as a fraction from
    /// <c>0.0</c> to <c>1.0</c>. Zero when no attempts were recorded.
    /// </summary>
    public double Rate => Attempts == 0 ? 0 : (double)Failed / Attempts;
}
