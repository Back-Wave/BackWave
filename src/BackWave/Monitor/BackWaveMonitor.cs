using System.Text;
using BackWave.Core;
using BackWave.Jobs;
using BackWave.Observers;
using BackWave.Storage;

namespace BackWave.Monitor;

/// <summary>How a job's opaque payload bytes were rendered for display.</summary>
public enum PayloadEncoding
{
    /// <summary>The bytes decoded cleanly as UTF-8 text.</summary>
    Utf8,

    /// <summary>The bytes were not valid UTF-8; rendered as a hex dump instead.</summary>
    Hex,
}

/// <summary>
/// A job's payload rendered for display. The payload is <b>opaque bytes</b> — serialized by the
/// host's own serializer; BackWave does not assume JSON and does not parse it. <see cref="Text"/>
/// is a best-effort UTF-8 decode, falling back to a hex dump when the bytes are not valid text.
/// <see cref="Encoding"/> says which path produced <see cref="Text"/>. <see cref="ByteCount"/> is
/// the raw payload length. Surfaced only behind the ViewSensitiveData Dashboard Permission.
/// </summary>
public sealed record JobPayloadView
{
    /// <summary>The raw payload length in bytes, before any rendering.</summary>
    public required int ByteCount { get; init; }

    /// <summary>Which rendering path produced <see cref="Text"/>: clean UTF-8, or a hex dump.</summary>
    public required PayloadEncoding Encoding { get; init; }

    /// <summary>
    /// The payload rendered for display: the decoded UTF-8 string when the bytes were valid text,
    /// otherwise an uppercase hex dump of the raw bytes.
    /// </summary>
    public required string Text { get; init; }
}

/// <summary>
/// The read-only observability surface over a BackWave store. Use it to inspect jobs, queues,
/// schedules, dependencies, observers, and workflows — for a dashboard, an admin endpoint, or a
/// test assertion. Every read returns a stable public shape; storage internals never leak through.
/// All methods are read-only and do not mutate the store.
/// </summary>
/// <param name="store">The job store to read from.</param>
/// <param name="registry">
/// Optional. The registered job types, used to answer <see cref="GetKnownWireNames"/>. When omitted,
/// that method returns an empty list and all other reads still work.
/// </param>
public sealed class BackWaveMonitor(
    IJobStore store,
    JobRegistry? registry = null)
{
    /// <summary>
    /// How much job history the store records: the configured policy for the append-only timeline of
    /// state changes (and whether failure detail is kept). Read directly from the store, so it always
    /// reflects what the store actually records. Read it to tell an empty timeline that is genuinely
    /// empty apart from one that is empty because recording is turned off.
    /// </summary>
    public JobHistoryPolicy JobHistoryPolicy => store.HistoryPolicy;

    /// <summary>
    /// The largest page a listing read will return: the store's configured monitor page cap. A listing
    /// that requests more rows than this is clamped down to it rather than rejected. Read it to size
    /// your own paging under the store's limit — for example, to leave room for an extra "is there a
    /// next page?" row so a full final page still reports the cursor correctly.
    /// </summary>
    public int MaxMonitorPageSize => store.Bounds.MaxMonitorPageSize;

    /// <summary>
    /// Whether history recording is turned off entirely — no timeline rows are ever written.
    /// True when the policy is <see cref="JobHistoryPolicy.Off"/>. Use it to render an explicit
    /// "history disabled" state instead of a blank timeline that would look broken.
    /// </summary>
    /// <returns><c>true</c> when recording is off; otherwise <c>false</c>.</returns>
    public bool IsHistoryRecordingDisabled => JobHistoryPolicy == JobHistoryPolicy.Off;

    /// <summary>
    /// The wire name of every registered job type, ordered alphabetically. A wire name is the stable
    /// string identity a job type is registered under. Use it to populate a job-type filter.
    /// </summary>
    /// <returns>
    /// The ordered wire names, or an empty list when no registry was supplied at construction.
    /// </returns>
    public IReadOnlyList<string> GetKnownWireNames()
        => registry is null ? [] : [.. registry.Registrations.Select(r => r.WireName)];

    /// <summary>The current snapshot of one job by its id.</summary>
    /// <param name="jobId">The id of the job to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The job's snapshot, or <c>null</c> when no job with that id exists.</returns>
    public async ValueTask<JobSnapshot?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        => await store.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false) is { } record
            ? ToSnapshot(record)
            : null;

    /// <summary>
    /// One job's history: the append-only timeline of state changes the store recorded as it applied
    /// them, oldest first. Each entry carries a timestamp, the resulting state, the attempt number,
    /// and optional failure detail. Use it to render a per-job activity timeline.
    /// </summary>
    /// <param name="jobId">The id of the job whose history to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The transitions oldest first. Empty when the job is unknown, or when history recording is off.
    /// </returns>
    public ValueTask<IReadOnlyList<JobTransition>> GetJobHistoryAsync(
        Guid jobId, CancellationToken cancellationToken = default)
        => store.GetJobHistoryAsync(jobId, cancellationToken);

    /// <summary>
    /// Jobs matching a filter, oldest first. Each field of the query narrows the result; a field left
    /// unset matches everything. The returned page is capped by the store's maximum page size — page
    /// further by passing the last row's <see cref="JobSnapshot.Sequence"/> as the query's after-cursor.
    /// </summary>
    /// <param name="query">
    /// The filter and paging cursor. When <c>null</c>, an empty query is used, which matches all jobs.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The matching jobs, oldest first, capped at one page; empty when nothing matches.</returns>
    public async ValueTask<IReadOnlyList<JobSnapshot>> ListJobsAsync(
        JobQuery? query = null, CancellationToken cancellationToken = default)
    {
        var records = await store.ListJobsAsync(query ?? new JobQuery(), cancellationToken).ConfigureAwait(false);
        return [.. records.Select(ToSnapshot)];
    }

    /// <summary>
    /// One job's payload bytes, rendered best-effort for display. The payload is opaque to BackWave —
    /// serialized by your own serializer and never parsed here — so it is decoded as UTF-8 with a hex
    /// dump fallback for non-text bytes. A <see cref="JobSnapshot"/> never carries payload bytes; this
    /// is the one method that surfaces them, on demand. Gate it behind the ViewSensitiveData Dashboard
    /// Permission, since a payload may carry secrets or personal data.
    /// </summary>
    /// <param name="jobId">The id of the job whose payload to render.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rendered payload, or <c>null</c> when no job with that id exists.</returns>
    public async ValueTask<JobPayloadView?> GetJobPayloadAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (await store.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false) is not { } record)
        {
            return null;
        }
        return RenderPayload(record.Payload);
    }

    /// <summary>Best-effort render of opaque payload bytes: strict UTF-8, hex dump on failure.</summary>
    private static JobPayloadView RenderPayload(ReadOnlyMemory<byte> payload)
    {
        var bytes = payload.Span;
        try
        {
            // Strict decode: throwOnInvalidBytes turns non-text bytes into a fallback, not mojibake.
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            return new JobPayloadView { ByteCount = bytes.Length, Encoding = PayloadEncoding.Utf8, Text = text };
        }
        catch (DecoderFallbackException)
        {
            return new JobPayloadView { ByteCount = bytes.Length, Encoding = PayloadEncoding.Hex, Text = Convert.ToHexString(bytes) };
        }
    }

    /// <summary>
    /// One job's output bytes: the value its handler emitted on success. Returned as raw bytes so an
    /// in-process dependent job can deserialize them to the producer's own shape. For a display-ready
    /// rendering, use <see cref="GetJobOutputViewAsync"/> instead.
    /// </summary>
    /// <param name="jobId">The id of the job whose output to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The raw output bytes, or <c>null</c> when the job set no output (or no job with that id exists).
    /// </returns>
    public ValueTask<ReadOnlyMemory<byte>?> GetJobOutputAsync(Guid jobId, CancellationToken cancellationToken = default)
        => store.GetJobOutputAsync(jobId, cancellationToken);

    /// <summary>
    /// One job's output bytes, rendered best-effort for display: decoded as UTF-8 with a hex dump
    /// fallback for non-text bytes, exactly like the payload. This is the human-viewing surface; for
    /// the raw bytes a dependent job consumes, use <see cref="GetJobOutputAsync"/>. Gate it behind the
    /// ViewSensitiveData Dashboard Permission, since output may carry secrets or personal data.
    /// </summary>
    /// <param name="jobId">The id of the job whose output to render.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The rendered output, or <c>null</c> when the job set no output (or no job with that id exists).
    /// </returns>
    public async ValueTask<JobPayloadView?> GetJobOutputViewAsync(Guid jobId, CancellationToken cancellationToken = default)
        => await store.GetJobOutputAsync(jobId, cancellationToken).ConfigureAwait(false) is { } output
            ? RenderPayload(output)
            : null;

    /// <summary>
    /// Job counts grouped by queue and state — backlog depths, in-flight counts, and failure counts in
    /// one read. Use it for at-a-glance queue health.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One count per (queue, state) pair that currently has jobs.</returns>
    public ValueTask<IReadOnlyList<QueueStateCount>> GetQueueDepthsAsync(CancellationToken cancellationToken = default)
        => store.CountJobsAsync(cancellationToken);

    /// <summary>
    /// Groups jobs by one tag dimension and counts the distinct jobs per value, ordered by count
    /// descending. Use it to build a breakdown such as jobs-per-tenant. One dimension per call.
    /// </summary>
    /// <param name="key">
    /// The tag dimension to break down by. A non-empty key facets a keyed tag (for example
    /// <c>"tenant"</c> yields per-tenant counts); the empty string (<c>""</c>) facets plain labels.
    /// </param>
    /// <param name="baseQuery">
    /// Optional. Scopes which jobs are counted, using the same filters as <see cref="ListJobsAsync"/>
    /// (for example, "within quarantined jobs on the <c>lab</c> queue, break down by tenant"). When
    /// <c>null</c>, all jobs are considered.
    /// </param>
    /// <param name="maxResults">
    /// Optional. The maximum number of buckets to return, keeping the highest-count buckets; defaults
    /// to returning every bucket. Pass a small cap (for example 20) to show only the dominant values.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Up to <paramref name="maxResults"/> entries, each a distinct tag value with its job count, ordered by count descending.</returns>
    public ValueTask<IReadOnlyList<TagFacet>> GetTagFacetAsync(
        string key, JobQuery? baseQuery = null, int maxResults = int.MaxValue, CancellationToken cancellationToken = default)
        => store.FacetAsync(key, baseQuery, maxResults, cancellationToken);

    /// <summary>
    /// Suggests Tags by case-insensitive prefix — a completion aid for composing an exact Tag filter by
    /// typing rather than by clicking a Tag on a job already in view. With no key it suggests Labels and
    /// keys together; with a key it suggests the values under that key (the empty string selects Labels).
    /// Matching is prefix and ASCII case-insensitive; results carry the canonical stored casing, are
    /// ordered lexicographically, and are paged by a keyset cursor. The suggest never filters jobs and
    /// only promises a suggested Tag exists, not that it has matches under any current filter.
    /// </summary>
    /// <param name="query">The prefix, optional key (stage selector), keyset cursor, and window size.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Up to the clamped window size of suggestions, in lexicographic order; empty when nothing matches beyond the cursor.</returns>
    public ValueTask<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(
        TagSuggestQuery query, CancellationToken cancellationToken = default)
        => store.SuggestTagsAsync(query, cancellationToken);

    /// <summary>
    /// Each queue's operational settings: whether it is paused and its configured concurrency-limit cap.
    /// To show how many of those slots are in use, combine this with the leased count from
    /// <see cref="GetQueueDepthsAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One settings entry per known queue.</returns>
    public ValueTask<IReadOnlyList<QueueSettings>> GetQueueSettingsAsync(CancellationToken cancellationToken = default)
        => store.ListQueueSettingsAsync(cancellationToken);

    /// <summary>
    /// The dependency edges around one job: the parent jobs still gating it from running, and the
    /// child jobs waiting on it. Use it to render why a job is held or what it will release.
    /// </summary>
    /// <param name="jobId">The id of the job whose dependency edges to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The edges. The parent side is the set <i>still</i> gating the job, not its full original parent
    /// list — a parent drops off once it has completed. Both sides are empty for a job with no
    /// dependencies, and for an unknown job.
    /// </returns>
    public ValueTask<DependencyEdges> GetDependencyEdgesAsync(Guid jobId, CancellationToken cancellationToken = default)
        => store.GetDependencyEdgesAsync(jobId, cancellationToken);

    /// <summary>
    /// Every recurring schedule with its current cursor, next due tick, and recently skipped ticks.
    /// A schedule that cannot be resolved on this host (bad cron or an unknown time zone) is returned
    /// with its <see cref="ScheduleStatus.Error"/> set rather than being dropped or throwing.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The schedules; empty when none are configured.</returns>
    public async ValueTask<IReadOnlyList<ScheduleStatus>> ListSchedulesAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = await store.ListSchedulesAsync(cancellationToken).ConfigureAwait(false);
        return [.. snapshots.Select(s =>
        {
            // A poisoned row (unresolvable zone on this host, corrupted cron) is shown errored
            // rather than throwing the whole read — the same row the mint planner skips.
            var resolvable = ScheduleValidation.TryResolve(
                s.Schedule.Cron, s.Schedule.TimeZoneId, out var cron, out var zone, out var error);
            return new ScheduleStatus
            {
                ScheduleId = s.Schedule.ScheduleId,
                Cron = s.Schedule.Cron,
                WireName = s.Schedule.WireName,
                Queue = s.Schedule.Queue,
                TimeZoneId = s.Schedule.TimeZoneId,
                CatchUp = s.Schedule.CatchUp,
                NoOverlap = s.Schedule.NoOverlap,
                Cursor = s.Schedule.Cursor,
                // Everything resolved through the Cursor is settled; the next tick after it is due next.
                NextDue = resolvable ? ZonedCron.NextAfter(cron!, s.Schedule.Cursor, zone) : null,
                HasLiveInstance = s.HasLiveInstance,
                SkippedTicks = s.Schedule.SkippedTicks,
                Error = error,
            };
        })];
    }

    /// <summary>
    /// One observer's durable delivery cursor: the global timeline position up to and including which
    /// every matching transition has been delivered or dead-lettered. Use it to gauge how far behind
    /// an observer is. Metadata only — a position, never payload or failure detail.
    /// </summary>
    /// <param name="observerId">The id of the observer whose cursor to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The delivered-through position, or <c>-1</c> when the observer has no cursor yet (nothing
    /// delivered, including when the observer id is unknown).
    /// </returns>
    public ValueTask<long> GetObserverCursorAsync(string observerId, CancellationToken cancellationToken = default)
        => store.GetObserverCursorAsync(observerId, cancellationToken);

    /// <summary>
    /// One observer's delivery lag — how far its durable cursor trails the transitions it subscribes to.
    /// The returned snapshot carries the cursor, the number of matching transitions the cursor has not
    /// yet passed (0 when caught up), and when the oldest of those occurred, so a dashboard can show
    /// whether the observer is keeping up and, if not, how stale its progress is. Subscription-aware:
    /// only transitions this observer would actually deliver are counted, never the whole log.
    /// </summary>
    /// <param name="observerId">The id of the observer whose lag to read.</param>
    /// <param name="subscription">The observer's subscription; its states and optional wire-name/queue filters scope the count.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The observer's lag snapshot: cursor position (−1 when nothing delivered), count of pending
    /// matching transitions, and the age of the oldest pending transition (null when caught up).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="observerId"/> or <paramref name="subscription"/> is null.</exception>
    /// <example>
    /// <code>
    /// var lag = await monitor.GetObserverLagAsync("slack", registration.Subscription);
    /// Console.WriteLine(lag.Pending == 0 ? "caught up" : $"{lag.Pending} behind");
    /// </code>
    /// </example>
    public ValueTask<ObserverLag> GetObserverLagAsync(
        string observerId, ObserverSubscription subscription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observerId);
        ArgumentNullException.ThrowIfNull(subscription);
        return store.GetObserverLagAsync(
            new ObserverLagRequest(observerId, subscription.States, subscription.WireName, subscription.Queue),
            cancellationToken);
    }

    /// <summary>
    /// One observer's dead-lettered deliveries, oldest first — deliveries that exhausted their retry
    /// ceiling, surfaced like dead-lettered jobs. Each record carries delivery metadata only (the
    /// timeline position, the job it points at, ordinal, state, attempt counts, and timestamps) —
    /// never payload or failure detail. Use it for an observer-delivery health view.
    /// </summary>
    /// <param name="observerId">The id of the observer whose dead-lettered deliveries to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The dead-lettered deliveries oldest first; empty for a healthy observer (and for an unknown
    /// observer id).
    /// </returns>
    public ValueTask<IReadOnlyList<ObserverDeadLetterRecord>> ListObserverDeadLettersAsync(
        string observerId, CancellationToken cancellationToken = default)
        => store.ListObserverDeadLettersAsync(observerId, cancellationToken);

    // The workflow read surface (ListWorkflowsAsync/GetWorkflowAsync + the WorkflowView projection)
    // is a Pro feature and lives in the BackWave.Pro package as extension methods on this type; the
    // free Monitor exposes no workflow surface. Those extensions read through Store below and project
    // members with ToSnapshot.

    /// <summary>The underlying store, exposed to the Pro package (via InternalsVisibleTo) so its
    /// workflow read extensions can read through the same store this Monitor wraps.</summary>
    internal IJobStore Store => store;

    internal static JobSnapshot ToSnapshot(JobRecord record) => new()
    {
        JobId = record.JobId,
        WireName = record.WireName,
        Queue = record.Queue,
        State = record.State,
        Attempt = record.Attempt,
        DueTime = record.DueTime,
        // Lease bookkeeping is meaningful only while Leased; the record clears it on every
        // other transition, so this passes through whatever the store committed (ADR 0009).
        LeaseOwner = record.LeaseOwner,
        LeaseExpiry = record.LeaseExpiry,
        CancelRequested = record.CancelRequested,
        TerminalAt = record.TerminalAt,
        TerminalCause = record.TerminalCause,
        ScheduleId = record.ScheduleId,
        Sequence = record.Sequence,
        Tags = record.Tags,
        WorkflowId = record.WorkflowId,
    };
}
