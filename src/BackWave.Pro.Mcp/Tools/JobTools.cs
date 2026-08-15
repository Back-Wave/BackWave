using System.ComponentModel;
using BackWave.Monitor;
using BackWave.Storage;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BackWave.Pro.Mcp.Tools;

// The job read tools (mcp-0003 inventory, issue 0225): search_jobs, get_job, get_job_history,
// get_job_dependencies — all behind the view gate, all wrapping BackWaveMonitor reads 1:1.
// Internal: the tool surface is wire-level (MCP), never a C# API. Registered explicitly via
// WithTools<JobTools>() in AddMcp; never assembly scanning. Input parameter names are snake_case
// (the wire contract), which is why the C# parameters carry underscores.
[McpServerToolType]
internal sealed class JobTools(
    BackWaveMonitor monitor,
    BackWaveProMcpOptions options,
    IHttpContextAccessor httpContextAccessor)
{
    // The default page size when max_results is omitted (mcp-0003); the store's own monitor page
    // cap still clamps larger requests.
    private const int DefaultMaxResults = 20;

    [McpServerTool(
        Name = ToolNames.SearchJobs,
        Title = "Search jobs",
        UseStructuredContent = true,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Find jobs by filter and page through the matches. Every filter is optional and omitted " +
        "filters match everything; supplied filters are AND-ed together. Returns full job " +
        "snapshots plus a paging cursor: when hasMore is true, pass nextCursor as after_cursor " +
        "to fetch the next page. Sorted newest-first by default.")]
    public async Task<SearchJobsResult> SearchJobsAsync(
        [Description("Only jobs in this state: Scheduled, AwaitingParent, Leased, Succeeded, Cancelled, DeadLettered, or Quarantined. Omit to match any state.")]
        string? state = null,
        [Description("Only jobs on this queue. Omit to match any queue.")]
        string? queue = null,
        [Description("Only jobs of this wire name (the job type's stable string identity; list_wire_names enumerates them). Omit to match any type.")]
        string? wire_name = null,
        [Description("Only jobs minted by this recurring schedule. Omit to match jobs from any source.")]
        string? schedule_id = null,
        [Description("Tag predicates, AND-ed together. Each is one of three forms: \"key=value\" (the job carries that keyed tag), \"key=*\" (the job carries any value under that key), or a bare \"value\" (the job carries that label). A label containing '=' cannot be expressed here.")]
        string[]? tags = null,
        [Description("The paging cursor: the nextCursor value from the previous page. Omit to start at the first page.")]
        long? after_cursor = null,
        [Description("Sort order: \"newest_first\" (the default) or \"oldest_first\".")]
        string? sort = null,
        [Description("Maximum jobs per page. Defaults to 20; the store's page cap clamps larger values.")]
        int? max_results = null,
        CancellationToken cancellationToken = default)
    {
        JobState? parsedState = null;
        if (state is not null)
        {
            if (!Enum.TryParse<JobState>(state, ignoreCase: true, out var s) || !Enum.IsDefined(s))
            {
                throw new McpException(
                    $"Unknown state '{state}'. Valid states: {string.Join(", ", Enum.GetNames<JobState>())}.");
            }
            parsedState = s;
        }

        var direction = sort switch
        {
            null => JobSortDirection.NewestFirst,
            _ when sort.Equals("newest_first", StringComparison.OrdinalIgnoreCase) => JobSortDirection.NewestFirst,
            _ when sort.Equals("oldest_first", StringComparison.OrdinalIgnoreCase) => JobSortDirection.OldestFirst,
            _ => throw new McpException($"Unknown sort '{sort}'. Use \"newest_first\" (the default) or \"oldest_first\"."),
        };

        var pageSize = max_results ?? DefaultMaxResults;
        if (pageSize < 1)
        {
            throw new McpException("max_results must be at least 1.");
        }
        // Keep room for the +1 next-page sentinel under the store's monitor page cap. If pageSize + 1
        // exceeded the cap, the store would clamp the sentinel away, so a full final page would report
        // hasMore=false and silently strand every later match. Clamping to cap - 1 first also stops
        // pageSize + 1 from overflowing when max_results is int.MaxValue. A caller asking for the cap
        // gets one fewer row but a cursor that keeps working — correct paging beats one extra row.
        // The cap is read from the monitor, so a host that configures a non-default page cap is honored.
        // The Math.Max(1, ...) floor guards a degenerate host cap of 1: cap - 1 would drive pageSize to
        // 0, and a MaxResults of 1 with any match would set hasMore=true while jobs is empty, so the
        // NextCursor read below (jobs[^1]) would throw and surface as an opaque server fault. At cap 1
        // there is no room for a sentinel, so paging cannot detect a next page (hasMore stays false),
        // but the tool returns a clean single-row page rather than faulting.
        pageSize = Math.Max(1, Math.Min(pageSize, monitor.MaxMonitorPageSize - 1));

        var query = new JobQuery
        {
            State = parsedState,
            Queue = queue,
            WireName = wire_name,
            ScheduleId = schedule_id,
            TagPredicates = ParseTagPredicates(tags),
            AfterSequence = after_cursor,
            SortDirection = direction,
            // One extra row answers "is there a next page?"; pageSize was clamped so the extra row
            // always survives the store's monitor page cap.
            MaxResults = pageSize + 1,
        };
        var page = await monitor.ListJobsAsync(query, cancellationToken).ConfigureAwait(false);

        var hasMore = page.Count > pageSize;
        var jobs = page.Take(pageSize).Select(JobRow.From).ToList();
        return new SearchJobsResult
        {
            Jobs = jobs,
            NextCursor = hasMore ? jobs[^1].Sequence : null,
            HasMore = hasMore,
        };
    }

    [McpServerTool(
        Name = ToolNames.GetJob,
        Title = "Get job",
        UseStructuredContent = true,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "One job's current snapshot by its id. An unknown id is an answer, not a fault: the " +
        "result carries found=false and no job. The snapshot never carries payload bytes.")]
    public async Task<GetJobResult> GetJobAsync(
        [Description("The job's id (a GUID).")]
        string job_id,
        CancellationToken cancellationToken = default)
    {
        var jobId = ParseJobId(job_id);
        var snapshot = await monitor.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        return new GetJobResult
        {
            Found = snapshot is not null,
            Job = snapshot is null ? null : JobRow.From(snapshot),
        };
    }

    [McpServerTool(
        Name = ToolNames.GetJobHistory,
        Title = "Get job history",
        UseStructuredContent = true,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "One job's history: the append-only timeline of state changes the store recorded, oldest " +
        "first. The response also states the store's history-recording policy — when recording " +
        "is off or limited, historyNote explains it, so an empty or detail-less list is not " +
        "mistaken for a broken store. The list is also empty for an unknown job id.")]
    public async Task<GetJobHistoryResult> GetJobHistoryAsync(
        [Description("The id (a GUID) of the job whose history to read.")]
        string job_id,
        CancellationToken cancellationToken = default)
    {
        var jobId = ParseJobId(job_id);
        var transitions = await monitor.GetJobHistoryAsync(jobId, cancellationToken).ConfigureAwait(false);
        var policy = monitor.JobHistoryPolicy;

        // FailureDetail carries the raw exception (type, message, stack), which routinely embeds
        // connection strings, file paths, and payload fragments — the same exfiltration class the
        // sensitive-data lock guards for get_job_payload/get_job_output. So it rides that same triple
        // lock: withheld (nulled) unless AuthorizeViewSensitiveData permits the request AND
        // ExposeSensitiveData is on AND the disable env kill-switch is unset. Fails closed with no
        // HttpContext, exactly like the gated tools. The rest of each transition (state, ordinal,
        // timestamp, attempt) is not sensitive and always flows through.
        var sensitiveAllowed = await McpToolGates
            .AllowsSensitiveDataAsync(httpContextAccessor.HttpContext, options)
            .ConfigureAwait(false);

        // A note is warranted only when detail was ACTUALLY stripped: the gate denied AND some
        // transition really carried detail. A job with no failing transition (and every transition
        // under the Off/Transitions policies, where the store records no detail at all) withholds
        // nothing, so it keeps its policy note (or none) — the denial never invents a warning.
        var detailWithheld = !sensitiveAllowed && transitions.Any(t => t.FailureDetail is not null);

        return new GetJobHistoryResult
        {
            Transitions = [.. transitions.Select(t => new JobTransitionRow(
                t.Ordinal, t.Timestamp, t.State.ToString(), t.Attempt,
                sensitiveAllowed ? t.FailureDetail : null))],
            HistoryPolicy = policy.ToString(),
            // When detail was actually withheld, the note tells the client it exists but is gated, so
            // a null failureDetail is not misread as "no failure" — it wins over the policy notes
            // (which describe Off/Transitions, where detail is never recorded and so never withheld).
            HistoryNote = detailWithheld
                ? "This store recorded failure detail, but it is withheld from this response because "
                    + "failure detail may carry secrets, connection strings, file paths, or personal "
                    + "data (exception messages and stacks), so failureDetail is null on every failing "
                    + "transition here. All three sensitive-data locks must allow it: the host's "
                    + "AuthorizeViewSensitiveData callback must permit this request, "
                    + "BackWaveProMcpOptions.ExposeSensitiveData must be true, and the "
                    + "BACKWAVE_MCP_DISABLE_SENSITIVE_DATA environment variable must not be set to a "
                    + "truthy value on the host."
                : policy switch
                {
                    JobHistoryPolicy.Off =>
                        "History recording is turned off on this store: no transitions are ever "
                        + "recorded, so an empty list does not mean the job never changed state.",
                    JobHistoryPolicy.Transitions =>
                        "This store records transitions without failure detail, so failureDetail is "
                        + "always null, even for failing transitions.",
                    _ => null,
                },
        };
    }

    [McpServerTool(
        Name = ToolNames.GetJobDependencies,
        Title = "Get job dependencies",
        UseStructuredContent = true,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "The dependency edges around one job: the parent jobs still gating it from running, and " +
        "the child jobs waiting on it. The parent side is the set still gating the job — an edge " +
        "resolves away as each parent completes, so it is not the full original parent list. Both " +
        "sides are empty for a job with no dependencies, and for an unknown job id.")]
    public async Task<GetJobDependenciesResult> GetJobDependenciesAsync(
        [Description("The id (a GUID) of the job whose dependency edges to read.")]
        string job_id,
        CancellationToken cancellationToken = default)
    {
        var jobId = ParseJobId(job_id);
        var edges = await monitor.GetDependencyEdgesAsync(jobId, cancellationToken).ConfigureAwait(false);
        return new GetJobDependenciesResult
        {
            GatingParents = edges.GatingParents,
            Children = edges.Children,
        };
    }

    // Invalid input = a tool-execution error with actionable text (mcp-0003 conventions), so a
    // malformed id never surfaces as an opaque server fault.
    private static Guid ParseJobId(string jobId)
        => Guid.TryParse(jobId, out var parsed)
            ? parsed
            : throw new McpException(
                $"Invalid job_id '{jobId}': expected a GUID, e.g. \"7f4df6f2-8c3a-4a0e-9d1a-2f6b8c1d5e3f\".");

    // The compact tag-predicate grammar (mcp-0003): "key=value" keyed, "key=*" any-value-under-key,
    // bare "value" a label. Split on the FIRST '=' only, so a value containing '=' stays intact.
    private static IReadOnlyList<JobTagPredicate> ParseTagPredicates(string[]? tags)
    {
        if (tags is null || tags.Length == 0)
        {
            return [];
        }

        var predicates = new List<JobTagPredicate>(tags.Length);
        foreach (var token in tags)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw MalformedTagPredicate(token);
            }

            var separator = token.IndexOf('=');
            if (separator < 0)
            {
                predicates.Add(JobTagPredicate.HasLabel(token));
                continue;
            }

            var key = token[..separator];
            var value = token[(separator + 1)..];
            if (key.Length == 0 || value.Length == 0)
            {
                throw MalformedTagPredicate(token);
            }
            predicates.Add(value == "*" ? JobTagPredicate.HasKey(key) : JobTagPredicate.HasKeyValue(key, value));
        }
        return predicates;
    }

    private static McpException MalformedTagPredicate(string? token)
        => new(
            $"Invalid tag predicate '{token}'. Each predicate must be one of three forms: "
            + "\"key=value\" (the job carries that keyed tag), \"key=*\" (the job carries any "
            + "value under that key), or a bare \"value\" (the job carries that label). "
            + "Predicates are AND-ed together.");
}

/// <summary>The structured result of <c>search_jobs</c>: one page of matches plus the paging cursor.</summary>
internal sealed record SearchJobsResult
{
    /// <summary>The matching jobs, one full snapshot each, in the requested sort order.</summary>
    [Description("The matching jobs, one full snapshot each, in the requested sort order.")]
    public required IReadOnlyList<JobRow> Jobs { get; init; }

    /// <summary>The cursor for the next page, or null on the last page.</summary>
    [Description("Pass this as after_cursor to fetch the next page; null when this is the last page.")]
    public long? NextCursor { get; init; }

    /// <summary>Whether more matching jobs exist beyond this page.</summary>
    [Description("Whether more matching jobs exist beyond this page.")]
    public required bool HasMore { get; init; }
}

/// <summary>The structured result of <c>get_job</c>.</summary>
internal sealed record GetJobResult
{
    /// <summary>Whether a job with the requested id exists.</summary>
    [Description("Whether a job with the requested id exists. False is an answer, not an error.")]
    public required bool Found { get; init; }

    /// <summary>The job's snapshot; null when not found.</summary>
    [Description("The job's snapshot; absent when found is false.")]
    public JobRow? Job { get; init; }
}

/// <summary>The structured result of <c>get_job_history</c>.</summary>
internal sealed record GetJobHistoryResult
{
    /// <summary>The recorded transitions, oldest first.</summary>
    [Description("The recorded state transitions, oldest first. Empty for an unknown job id, and always empty when historyPolicy is Off.")]
    public required IReadOnlyList<JobTransitionRow> Transitions { get; init; }

    /// <summary>How much history this store records.</summary>
    [Description("How much history the store records: Off (nothing), Transitions (state changes without failure detail), or TransitionsAndFailureDetail (the full log).")]
    public required string HistoryPolicy { get; init; }

    /// <summary>
    /// Explains an off or limited recording policy, or that failure detail is withheld because this
    /// request lacks sensitive-data permission; null when the full log is recorded and readable.
    /// </summary>
    [Description("Present when recording is off or limited, or when failure detail is withheld because this request lacks sensitive-data permission, explaining what the timeline can and cannot show; null when the full log is recorded and readable.")]
    public string? HistoryNote { get; init; }
}

/// <summary>One entry of a job's transition timeline.</summary>
/// <param name="Ordinal">The 0-based per-job sequence number, oldest first.</param>
/// <param name="Timestamp">When the transition occurred.</param>
/// <param name="State">The job state the transition produced.</param>
/// <param name="Attempt">The job's attempt number at the transition.</param>
/// <param name="FailureDetail">
/// Captured diagnostics for a failing transition; null on non-failing transitions, whenever the
/// history policy omits detail, and whenever this request lacks sensitive-data permission (the
/// detail is gated because it can carry secrets or personal data).
/// </param>
internal sealed record JobTransitionRow(
    [property: Description("The 0-based per-job sequence number of this entry, oldest first; preserved even when older entries age out.")]
    long Ordinal,
    [property: Description("When the transition occurred.")]
    DateTimeOffset Timestamp,
    [property: Description("The job state this transition produced, e.g. Scheduled, Leased, Succeeded, DeadLettered.")]
    string State,
    [property: Description("The job's attempt number at this transition.")]
    int Attempt,
    [property: Description("For a failing transition, the captured diagnostics (exception type, message, stack), bounded for storage; null on non-failing transitions, whenever the history policy omits detail, and whenever this request lacks sensitive-data permission (the detail is gated behind the same sensitive-data lock as job payloads and outputs because it can carry secrets or personal data; when it is withheld, historyNote says so).")]
    string? FailureDetail);

/// <summary>The structured result of <c>get_job_dependencies</c>.</summary>
internal sealed record GetJobDependenciesResult
{
    /// <summary>The still-non-terminal parents currently blocking the job.</summary>
    [Description("The ids of the parent jobs still gating this job from running. An edge resolves away as each parent completes, so this is not the full original parent list.")]
    public required IReadOnlyList<Guid> GatingParents { get; init; }

    /// <summary>The jobs waiting on this one as a parent.</summary>
    [Description("The ids of the child jobs waiting on this job as a parent.")]
    public required IReadOnlyList<Guid> Children { get; init; }
}

/// <summary>One full job snapshot as the job tools return it — displayable facts only, never payload bytes.</summary>
internal sealed record JobRow
{
    /// <summary>The job's unique id.</summary>
    [Description("The job's unique id.")]
    public required Guid JobId { get; init; }

    /// <summary>The wire name of the job's type.</summary>
    [Description("The wire name of the job's type: the stable string identity it was registered under.")]
    public required string WireName { get; init; }

    /// <summary>The queue the job runs on.</summary>
    [Description("The queue this job runs on.")]
    public required string Queue { get; init; }

    /// <summary>The job's current lifecycle state.</summary>
    [Description("The job's current lifecycle state: Scheduled, AwaitingParent, Leased, Succeeded, Cancelled, DeadLettered, or Quarantined.")]
    public required string State { get; init; }

    /// <summary>Execution tries so far.</summary>
    [Description("Execution tries so far; claiming a job to run starts an attempt.")]
    public required int Attempt { get; init; }

    /// <summary>When the job becomes (or became) eligible to run.</summary>
    [Description("When the job becomes (or became) eligible to run.")]
    public required DateTimeOffset DueTime { get; init; }

    /// <summary>The worker currently holding the job, while leased.</summary>
    [Description("The worker currently holding this job; set while it is leased to a worker, null otherwise.")]
    public string? LeaseOwner { get; init; }

    /// <summary>When the current lease expires, while leased.</summary>
    [Description("When the current lease expires if not renewed by a heartbeat; set while leased, null otherwise.")]
    public DateTimeOffset? LeaseExpiry { get; init; }

    /// <summary>Whether cancellation has been requested.</summary>
    [Description("Whether cancellation has been requested for this job. A running attempt observes it cooperatively.")]
    public required bool CancelRequested { get; init; }

    /// <summary>When the job reached a terminal state; null while active.</summary>
    [Description("When the job reached a terminal state (succeeded, cancelled, dead-lettered, or quarantined); null while still active.")]
    public DateTimeOffset? TerminalAt { get; init; }

    /// <summary>A short reason for the terminal outcome; null while active.</summary>
    [Description("A short reason for the terminal outcome (for example why it was dead-lettered); null while still active.")]
    public string? TerminalCause { get; init; }

    /// <summary>The recurring schedule that minted this instance, when any.</summary>
    [Description("The recurring schedule that minted this instance; null for a directly enqueued job.")]
    public string? ScheduleId { get; init; }

    /// <summary>The job's monotonic ordering value, used for paging.</summary>
    [Description("A monotonic ordering value used for paging; search_jobs cursors are these values.")]
    public required long Sequence { get; init; }

    /// <summary>The workflow the job belongs to, when any.</summary>
    [Description("The workflow this job belongs to; null for a job that is not part of a workflow.")]
    public Guid? WorkflowId { get; init; }

    /// <summary>The job's tags.</summary>
    [Description("The job's tags. To filter search_jobs by one, compose the predicate \"key=value\" for a keyed tag or the bare value for a label (a tag whose key is the empty string).")]
    public required IReadOnlyList<JobTagRow> Tags { get; init; }

    internal static JobRow From(JobSnapshot snapshot) => new()
    {
        JobId = snapshot.JobId,
        WireName = snapshot.WireName,
        Queue = snapshot.Queue,
        State = snapshot.State.ToString(),
        Attempt = snapshot.Attempt,
        DueTime = snapshot.DueTime,
        LeaseOwner = snapshot.LeaseOwner,
        LeaseExpiry = snapshot.LeaseExpiry,
        CancelRequested = snapshot.CancelRequested,
        TerminalAt = snapshot.TerminalAt,
        TerminalCause = snapshot.TerminalCause,
        ScheduleId = snapshot.ScheduleId,
        Sequence = snapshot.Sequence,
        WorkflowId = snapshot.WorkflowId,
        Tags = [.. snapshot.Tags.Select(t => new JobTagRow(t.Key, t.Value))],
    };
}

/// <summary>One job tag.</summary>
/// <param name="Key">The tag's key; the empty string for a bare label.</param>
/// <param name="Value">The label text or the keyed tag's value.</param>
internal sealed record JobTagRow(
    [property: Description("The tag's key; the empty string means the tag is a bare label.")]
    string Key,
    [property: Description("The label text (when key is empty) or the value under the key.")]
    string Value);
