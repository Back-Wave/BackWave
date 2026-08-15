using System.ComponentModel;
using BackWave.Monitor;
using BackWave.Operations;
using BackWave.Storage;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BackWave.Pro.Mcp.Tools;

// The remaining plain view-gated read tools (mcp-0003 inventory): queue settings, tag facet, wire
// names, schedules, and the operator audit trail. Internal: the tool surface is wire-level (MCP),
// never a C# API. Registered explicitly via WithTools<ReadTools>() in AddMcp; never assembly
// scanning.
[McpServerToolType]
internal sealed class ReadTools(BackWaveMonitor monitor, BackWaveOperator jobOperator)
{
    [McpServerTool(
        Name = ToolNames.GetQueueSettings,
        Title = "Get queue settings",
        UseStructuredContent = true,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Each queue's operational settings: whether it is paused (a paused queue accepts new jobs " +
        "but hands out no work) and its concurrency limit (the cluster-wide ceiling on how many " +
        "of its jobs run at once; null means unlimited). Combine with get_queue_depths to see how " +
        "many limit slots are in use.")]
    public async Task<QueueSettingsResult> GetQueueSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await monitor.GetQueueSettingsAsync(cancellationToken).ConfigureAwait(false);
        return new QueueSettingsResult
        {
            QueueSettings = [.. settings.Select(s => new QueueSettingsRow(s.Queue, s.Paused, s.ConcurrencyLimit))],
        };
    }

    [McpServerTool(
        Name = ToolNames.GetTagFacet,
        Title = "Get tag facet",
        UseStructuredContent = true,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Groups jobs by one tag dimension and counts the distinct jobs per value, ordered by " +
        "count descending - for example a jobs-per-tenant breakdown. Pass a non-empty key to " +
        "facet a keyed tag (key \"tenant\" yields per-tenant counts); pass the empty string to " +
        "facet plain labels. The optional filters scope which jobs are counted, for example " +
        "\"within dead-lettered jobs on the orders queue, break down by tenant\".")]
    public async Task<TagFacetResult> GetTagFacetAsync(
        [Description("The tag dimension to break down by: a tag key such as \"tenant\", or the " +
            "empty string to facet plain labels.")]
        string key,
        [Description("Only count jobs in this state. One of: Scheduled, AwaitingParent, Leased, " +
            "Succeeded, Cancelled, DeadLettered, Quarantined. Omit to count jobs in any state.")]
        string? state = null,
        [Description("Only count jobs on this queue; omit for any queue.")]
        string? queue = null,
        [Description("Only count jobs with this wire name (job type identifier); omit for any type.")]
        string? wire_name = null,
        [Description("Only count jobs minted by this recurring schedule; omit for jobs from any source.")]
        string? schedule_id = null,
        [Description("The maximum number of buckets to return, keeping the highest-count buckets. " +
            "Defaults to 20.")]
        int max_results = 20,
        CancellationToken cancellationToken = default)
    {
        if (max_results <= 0)
        {
            throw new McpException("max_results must be at least 1.");
        }

        var query = new JobQuery
        {
            State = ParseState(state),
            Queue = queue,
            WireName = wire_name,
            ScheduleId = schedule_id,
        };
        var buckets = await monitor.GetTagFacetAsync(key, query, max_results, cancellationToken).ConfigureAwait(false);
        return new TagFacetResult
        {
            Buckets = [.. buckets.Select(b => new TagFacetBucket(b.Value, b.Count))],
        };
    }

    [McpServerTool(
        Name = ToolNames.ListWireNames,
        Title = "List wire names",
        UseStructuredContent = true,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "The wire name of every registered job type, ordered alphabetically. A wire name is the " +
        "stable string identity a job type is registered under - the value to use for wire-name " +
        "filters on other tools.")]
    public WireNamesResult ListWireNames()
        => new() { WireNames = monitor.GetKnownWireNames() };

    [McpServerTool(
        Name = ToolNames.ListSchedules,
        Title = "List schedules",
        UseStructuredContent = true,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Every recurring schedule with its cron, time zone, policies, current cursor, next due " +
        "tick, whether a minted instance is still running, and recently skipped ticks. A schedule " +
        "that cannot be resolved on this host (bad cron or unknown time zone) is returned with " +
        "its error field set rather than dropped.")]
    public async Task<SchedulesResult> ListSchedulesAsync(CancellationToken cancellationToken)
    {
        var schedules = await monitor.ListSchedulesAsync(cancellationToken).ConfigureAwait(false);
        return new SchedulesResult
        {
            Schedules = [.. schedules.Select(s => new ScheduleRow(
                s.ScheduleId,
                s.Cron,
                s.WireName,
                s.Queue,
                s.TimeZoneId,
                s.CatchUp.ToString(),
                s.NoOverlap,
                s.Cursor,
                s.NextDue,
                s.HasLiveInstance,
                s.SkippedTicks,
                s.Error))],
        };
    }

    [McpServerTool(
        Name = ToolNames.ListAuditRecords,
        Title = "List audit records",
        UseStructuredContent = true,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "The operator audit trail for one target - who did what to it, and when - oldest first. " +
        "Every operator write action (cancel, requeue, pause/resume queue, trigger schedule, set " +
        "concurrency limit) contributes exactly one record. Empty when no action ever touched the " +
        "target.")]
    public async Task<AuditRecordsResult> ListAuditRecordsAsync(
        [Description("The audit target: a job id, a queue name, or a schedule id.")]
        string target,
        CancellationToken cancellationToken)
    {
        var records = await jobOperator.ListAuditRecordsAsync(target, cancellationToken).ConfigureAwait(false);
        return new AuditRecordsResult
        {
            AuditRecords = [.. records.Select(r => new AuditRecordRow(
                r.Actor, r.Action.ToString(), r.Target, r.RecordedAt))],
        };
    }

    // An invalid state is a caller mistake, not an empty result: throwing McpException surfaces the
    // message (with the valid states named) to the client as an isError tool result.
    private static JobState? ParseState(string? state)
    {
        if (string.IsNullOrEmpty(state))
        {
            return null;
        }
        if (!Enum.TryParse<JobState>(state, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw new McpException(
                $"Unknown job state '{state}'. Valid states: {string.Join(", ", Enum.GetNames<JobState>())}.");
        }
        return parsed;
    }
}

/// <summary>The structured result of <c>get_queue_settings</c>.</summary>
internal sealed record QueueSettingsResult
{
    /// <summary>One settings entry per known queue.</summary>
    [Description("One settings entry per known queue.")]
    public required IReadOnlyList<QueueSettingsRow> QueueSettings { get; init; }
}

/// <summary>One queue's operational settings.</summary>
/// <param name="Queue">The queue name.</param>
/// <param name="Paused">Whether the queue is paused.</param>
/// <param name="ConcurrencyLimit">The queue's concurrency ceiling, or null when unlimited.</param>
internal sealed record QueueSettingsRow(
    [property: Description("The queue name.")]
    string Queue,
    [property: Description("Whether the queue is paused: a paused queue accepts new jobs but hands out no work.")]
    bool Paused,
    [property: Description("The cluster-wide ceiling on how many of this queue's jobs run at once; null means unlimited.")]
    int? ConcurrencyLimit);

/// <summary>The structured result of <c>get_tag_facet</c>.</summary>
internal sealed record TagFacetResult
{
    /// <summary>The facet buckets, ordered by count descending.</summary>
    [Description("One bucket per distinct value under the faceted dimension, ordered by count " +
        "descending (value ascending as the tiebreak).")]
    public required IReadOnlyList<TagFacetBucket> Buckets { get; init; }
}

/// <summary>One facet bucket: a tag value and its distinct-job count.</summary>
/// <param name="Value">The tag value (or label text) this bucket counts.</param>
/// <param name="Count">The number of distinct jobs carrying the faceted dimension with this value.</param>
internal sealed record TagFacetBucket(
    [property: Description("The tag value (or label text) this bucket counts.")]
    string Value,
    [property: Description("The number of distinct jobs carrying the faceted dimension with this value.")]
    int Count);

/// <summary>The structured result of <c>list_wire_names</c>.</summary>
internal sealed record WireNamesResult
{
    /// <summary>The registered wire names, ordered alphabetically.</summary>
    [Description("The wire name of every registered job type, ordered alphabetically.")]
    public required IReadOnlyList<string> WireNames { get; init; }
}

/// <summary>The structured result of <c>list_schedules</c>.</summary>
internal sealed record SchedulesResult
{
    /// <summary>One row per recurring schedule.</summary>
    [Description("One row per recurring schedule; empty when none are configured.")]
    public required IReadOnlyList<ScheduleRow> Schedules { get; init; }
}

/// <summary>One recurring schedule's status.</summary>
/// <param name="ScheduleId">The schedule's unique id.</param>
/// <param name="Cron">The schedule's six-field cron expression.</param>
/// <param name="WireName">The wire name of the job type the schedule mints.</param>
/// <param name="Queue">The queue minted jobs run on.</param>
/// <param name="TimeZoneId">The IANA time zone the cron is evaluated in; null means UTC.</param>
/// <param name="CatchUp">What the schedule does about ticks missed while the host was down.</param>
/// <param name="NoOverlap">Whether a new tick is skipped while a previous instance still runs.</param>
/// <param name="Cursor">The instant up to which due ticks have been resolved.</param>
/// <param name="NextDue">The next tick that will mint, or null when none is coming.</param>
/// <param name="HasLiveInstance">Whether a minted instance is currently non-terminal.</param>
/// <param name="SkippedTicks">Recently skipped ticks, newest last, bounded.</param>
/// <param name="Error">Why the schedule cannot run on this host, or null when healthy.</param>
internal sealed record ScheduleRow(
    [property: Description("The schedule's unique id.")]
    string ScheduleId,
    [property: Description("The schedule's cron expression, in six-field form (seconds first).")]
    string Cron,
    [property: Description("The wire name of the job type this schedule mints.")]
    string WireName,
    [property: Description("The queue the minted jobs run on.")]
    string Queue,
    [property: Description("The IANA time-zone id the cron is evaluated in; null means UTC.")]
    string? TimeZoneId,
    [property: Description("What the schedule does about ticks missed while the host was down: " +
        "\"Skip\" (missed means missed) or \"Coalesce\" (one make-up run).")]
    string CatchUp,
    [property: Description("Whether a new tick is skipped while a previous instance of this schedule is still running.")]
    bool NoOverlap,
    [property: Description("The instant up to which due ticks have been resolved (inclusive).")]
    DateTimeOffset Cursor,
    [property: Description("The next tick that will mint a job, or null when the cron has no future occurrence or the schedule cannot be resolved.")]
    DateTimeOffset? NextDue,
    [property: Description("Whether a minted instance is currently non-terminal (what no-overlap watches).")]
    bool HasLiveInstance,
    [property: Description("Recently skipped ticks (from no-overlap or catch-up), newest last, bounded.")]
    IReadOnlyList<DateTimeOffset> SkippedTicks,
    [property: Description("Why the schedule cannot run on this host (unparseable cron, or a time " +
        "zone unknown here); null for a healthy schedule.")]
    string? Error);

/// <summary>The structured result of <c>list_audit_records</c>.</summary>
internal sealed record AuditRecordsResult
{
    /// <summary>The audit records for the target, oldest first.</summary>
    [Description("The audit records for the target, oldest first; empty when none exist.")]
    public required IReadOnlyList<AuditRecordRow> AuditRecords { get; init; }
}

/// <summary>One operator audit record.</summary>
/// <param name="Actor">Who performed the action.</param>
/// <param name="Action">Which operator action was performed.</param>
/// <param name="Target">The job id, queue name, or schedule id acted on.</param>
/// <param name="RecordedAt">When the action was recorded.</param>
internal sealed record AuditRecordRow(
    [property: Description("Who performed the action.")]
    string Actor,
    [property: Description("Which operator action was performed, e.g. Cancel, Requeue, " +
        "TriggerScheduleNow, PauseQueue, ResumeQueue, SetConcurrencyLimit.")]
    string Action,
    [property: Description("The job id, queue name, or schedule id the action was performed on.")]
    string Target,
    [property: Description("When the action was recorded.")]
    DateTimeOffset RecordedAt);
