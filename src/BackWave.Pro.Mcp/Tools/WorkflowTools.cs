using System.ComponentModel;
using BackWave.Monitor;
using BackWave.Operations;
using BackWave.Pro;
using BackWave.Storage;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BackWave.Pro.Mcp.Tools;

// The three workflow tools (mcp-0003 inventory, issue 0228): list_workflows, get_workflow behind
// the view gate, cancel_workflow behind the SAME AuthorizeCancel gate as cancel_job (its
// McpToolGates entry; dashboard precedent — WorkflowDashboardExtension reuses AuthorizeCancel).
// They mirror the Pro dashboard's Workflows surface exactly: the same Pro monitor reads
// (ListWorkflowsAsync/GetWorkflowAsync) and the same Pro operator write (CancelWorkflowAsync).
// Like every Pro feature the tools are soft-fail: license state never changes whether they run,
// and no license text ever appears in a result. Internal: the tool surface is wire-level (MCP),
// never a C# API. Registered explicitly via WithTools<WorkflowTools>() in AddMcp.
[McpServerToolType]
internal sealed class WorkflowTools(
    BackWaveMonitor monitor,
    BackWaveOperator @operator,
    BackWaveProMcpOptions options,
    IHttpContextAccessor httpContextAccessor)
{
    // The default page size when max_results is omitted, matching search_jobs (mcp-0003).
    private const int DefaultMaxResults = 20;

    [McpServerTool(
        Name = ToolNames.ListWorkflows,
        Title = "List workflows",
        UseStructuredContent = true,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "List workflows and page through them: one row per workflow with its derived status " +
        "(Running, Failed, Cancelled, or Succeeded — computed from its member jobs' states) and " +
        "member count. Returns a paging cursor: when hasMore is true, pass nextCursor as " +
        "after_cursor to fetch the next page. Sorted newest-first by default. Drill into one " +
        "workflow's full graph with get_workflow.")]
    public async Task<ListWorkflowsResult> ListWorkflowsAsync(
        [Description("The paging cursor: the nextCursor value from the previous page. Omit to start at the first page. If the workflow it pointed at has since been removed, paging safely restarts from the first page (some already-seen rows may repeat) instead of failing.")]
        string? after_cursor = null,
        [Description("Sort order: \"newest_first\" (the default) or \"oldest_first\".")]
        string? sort = null,
        [Description("Maximum workflows per page. Defaults to 20; larger values are clamped to the store's page cap.")]
        int? max_results = null,
        CancellationToken cancellationToken = default)
    {
        var newestFirst = sort switch
        {
            null => true,
            _ when sort.Equals("newest_first", StringComparison.OrdinalIgnoreCase) => true,
            _ when sort.Equals("oldest_first", StringComparison.OrdinalIgnoreCase) => false,
            _ => throw new McpException($"Unknown sort '{sort}'. Use \"newest_first\" (the default) or \"oldest_first\"."),
        };

        var pageSize = max_results ?? DefaultMaxResults;
        if (pageSize < 1)
        {
            throw new McpException("max_results must be at least 1.");
        }

        // Clamp the in-memory slice so an enormous max_results can't serialize every workflow in one
        // response. search_jobs gets this ceiling for free from the store's own paged read; this list
        // is read whole and sliced here, so we apply the same bound explicitly. No sentinel over-fetch
        // (unlike search_jobs) because hasMore below is computed from the full ordered count, not from
        // an over-read, so we clamp straight to the cap rather than cap - 1. The cap is read from the
        // monitor, so a host that configures a non-default page cap is honored.
        // The Math.Max(1, ...) floor guards a degenerate host cap of 0: a plain Min would drive pageSize
        // to 0, and any existing workflow would then set hasMore=true below (start + 0 < ordered.Count)
        // while page is empty, so the NextCursor read (page[^1]) would throw and surface as an opaque
        // server fault. The floor yields a clean single-row page instead.
        pageSize = Math.Max(1, Math.Min(pageSize, monitor.MaxMonitorPageSize));

        // The store lists every workflow oldest-first with a stable id tiebreak, so reversing is
        // the deterministic newest-first order. The cursor is the last-returned workflow's id,
        // re-located in the fresh read: rows created after the previous page sort strictly newer
        // (before the cursor, newest-first), so resuming after the cursor id never skips or
        // repeats the rows that were still ahead — the same guarantee search_jobs gets from its
        // monotonic sequence cursor. If the cursor workflow itself is gone (workflow-aware retention
        // can purge a drained workflow between pages), see the absent-cursor branch below.
        var all = await monitor.ListWorkflowsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<WorkflowSnapshot> ordered = newestFirst ? [.. all.Reverse()] : all;

        var start = 0;
        if (after_cursor is not null)
        {
            if (!Guid.TryParse(after_cursor, out var cursorId))
            {
                throw MalformedCursor(after_cursor);
            }
            var index = -1;
            for (var i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].WorkflowId == cursorId)
                {
                    index = i;
                    break;
                }
            }
            // A well-formed cursor whose workflow is no longer present is NOT malformed input:
            // workflow-aware retention can purge a drained workflow between pages, so a valid
            // nextCursor can legitimately vanish. MalformedCursor is reserved strictly for the
            // non-GUID case (already thrown above). The cursor is only a WorkflowId, and the purged
            // row's sort key (creation time) is gone with it, so we cannot re-locate its exact sort
            // position; restart from the start of the fresh list. That can repeat rows the client
            // already saw but never skips one (no silent data loss), keeping paging usable instead
            // of dead-ending the client on a cursor it can never resume from.
            start = index < 0 ? 0 : index + 1;
        }

        var page = ordered.Skip(start).Take(pageSize).Select(WorkflowRow.From).ToList();
        var hasMore = start + page.Count < ordered.Count;
        return new ListWorkflowsResult
        {
            Workflows = page,
            NextCursor = hasMore ? page[^1].WorkflowId.ToString() : null,
            HasMore = hasMore,
        };
    }

    [McpServerTool(
        Name = ToolNames.GetWorkflow,
        Title = "Get workflow",
        UseStructuredContent = true,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "One workflow's full graph: its member jobs each with its current state, the fixed " +
        "dependency edges between them (child runs after parent), and the workflow's derived " +
        "status. Members are job snapshots — displayable facts only, never payload bytes. An " +
        "unknown or malformed id is an answer, not a fault: the result carries found=false and " +
        "no workflow.")]
    public async Task<GetWorkflowResult> GetWorkflowAsync(
        [Description("The workflow's id (a GUID). Workflow ids appear in list_workflows results and on jobs' workflowId field.")]
        string workflow_id,
        CancellationToken cancellationToken = default)
    {
        // Malformed and unknown ids are both {found:false} — the same contract as the dashboard's
        // workflow detail page, which renders Not Found for either (issue 0228).
        if (!Guid.TryParse(workflow_id, out var workflowId))
        {
            return new GetWorkflowResult { Found = false };
        }

        var view = await monitor.GetWorkflowAsync(workflowId, cancellationToken).ConfigureAwait(false);
        return new GetWorkflowResult
        {
            Found = view is not null,
            Workflow = view is null
                ? null
                : new WorkflowDetail
                {
                    WorkflowId = view.WorkflowId,
                    Name = view.Name,
                    CreatedAt = view.CreatedAt,
                    Status = view.Status.ToString(),
                    Members = [.. view.Members.Select(JobRow.From)],
                    Edges = [.. view.Edges.Select(e => new WorkflowEdgeRow(e.Parent, e.Child))],
                    RestartedFrom = view.RestartedFrom,
                },
        };
    }

    [McpServerTool(
        Name = ToolNames.CancelWorkflow,
        Title = "Cancel a workflow",
        UseStructuredContent = true,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Cancels a whole workflow by cancelling each member job that is still running at the " +
        "moment of the call: a member that has not started is cancelled immediately, a running " +
        "member is asked to stop cooperatively, and members that already finished are left " +
        "untouched. An unknown workflow id is an answer, not a fault: the result carries " +
        "found=false and nothing changes. Each member cancel is recorded in the operator audit " +
        "trail.")]
    public async Task<CancelWorkflowToolResult> CancelWorkflowAsync(
        [Description("The id of the workflow to cancel, as a GUID string. Workflow ids appear in list_workflows results.")]
        string workflow_id,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(workflow_id, out var workflowId))
        {
            throw new McpException(
                $"Invalid workflow_id '{workflow_id}': pass the workflow's id as a GUID string, "
                + "e.g. \"7f4d21f0-5b0a-4f3e-9c2d-8a6b1e0c9d42\". Workflow ids appear in "
                + "list_workflows results.");
        }

        var result = await @operator
            .CancelWorkflowAsync(workflowId, ResolveActor(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new CancelWorkflowToolResult(
            result.Found, result.CancelledImmediately, result.CancellationRequested);
    }

    // Every write stamps the audit record with the host-resolved actor, exactly like WriteTools:
    // the HTTP context is always present over the mounted endpoint, and its absence means the call
    // arrived some other way — fail loudly rather than stamp a made-up identity.
    private string ResolveActor()
    {
        var httpContext = httpContextAccessor.HttpContext
            ?? throw new McpException(
                "No HTTP request context is available, so the acting operator's identity cannot be "
                + "resolved; call this tool through the mounted BackWave MCP endpoint.");
        return options.ResolveActor(httpContext);
    }

    private static McpException MalformedCursor(string cursor)
        => new(
            $"Invalid after_cursor '{cursor}': pass the nextCursor value returned by a previous "
            + "list_workflows page, or omit it to start at the first page.");
}

/// <summary>The structured result of <c>list_workflows</c>: one page of rows plus the paging cursor.</summary>
internal sealed record ListWorkflowsResult
{
    /// <summary>The workflows on this page, in the requested sort order.</summary>
    [Description("The workflows on this page, one row each, in the requested sort order.")]
    public required IReadOnlyList<WorkflowRow> Workflows { get; init; }

    /// <summary>The cursor for the next page, or null on the last page.</summary>
    [Description("Pass this as after_cursor to fetch the next page; null when this is the last page.")]
    public string? NextCursor { get; init; }

    /// <summary>Whether more workflows exist beyond this page.</summary>
    [Description("Whether more workflows exist beyond this page.")]
    public required bool HasMore { get; init; }
}

/// <summary>One workflow as <c>list_workflows</c> shows it: identity, derived status, and size.</summary>
internal sealed record WorkflowRow
{
    /// <summary>The workflow's unique id.</summary>
    [Description("The workflow's unique id.")]
    public required Guid WorkflowId { get; init; }

    /// <summary>The workflow's human-readable label, or null if unnamed.</summary>
    [Description("The workflow's human-readable label; null for an unnamed workflow.")]
    public string? Name { get; init; }

    /// <summary>When the workflow was created.</summary>
    [Description("When the workflow was created.")]
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The workflow's derived status.</summary>
    [Description("The workflow's status, always derived from its member jobs' current states: Running (any member non-terminal), Failed (all terminal, any dead-lettered or quarantined), Cancelled (all terminal, none failed, any cancelled), or Succeeded (every member succeeded).")]
    public required string Status { get; init; }

    /// <summary>How many member jobs the workflow has.</summary>
    [Description("How many member jobs the workflow has.")]
    public required int MemberCount { get; init; }

    /// <summary>The workflow this one was restarted from, or null if created fresh.</summary>
    [Description("The id of the workflow this one was restarted from; null when it was created fresh.")]
    public Guid? RestartedFrom { get; init; }

    internal static WorkflowRow From(WorkflowSnapshot snapshot) => new()
    {
        WorkflowId = snapshot.WorkflowId,
        Name = snapshot.Name,
        CreatedAt = snapshot.CreatedAt,
        Status = snapshot.Status.ToString(),
        MemberCount = snapshot.MemberCount,
        RestartedFrom = snapshot.RestartedFrom,
    };
}

/// <summary>The structured result of <c>get_workflow</c>.</summary>
internal sealed record GetWorkflowResult
{
    /// <summary>Whether a workflow with the requested id exists.</summary>
    [Description("Whether a workflow with the requested id exists. False (also returned for a malformed id) is an answer, not an error.")]
    public required bool Found { get; init; }

    /// <summary>The workflow's full graph; null when not found.</summary>
    [Description("The workflow's full graph; absent when found is false.")]
    public WorkflowDetail? Workflow { get; init; }
}

/// <summary>One workflow's full graph as <c>get_workflow</c> returns it.</summary>
internal sealed record WorkflowDetail
{
    /// <summary>The workflow's unique id.</summary>
    [Description("The workflow's unique id.")]
    public required Guid WorkflowId { get; init; }

    /// <summary>The workflow's human-readable label, or null if unnamed.</summary>
    [Description("The workflow's human-readable label; null for an unnamed workflow.")]
    public string? Name { get; init; }

    /// <summary>When the workflow was created.</summary>
    [Description("When the workflow was created.")]
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The workflow's derived status.</summary>
    [Description("The workflow's status, always derived from its member jobs' current states: Running, Failed, Cancelled, or Succeeded.")]
    public required string Status { get; init; }

    /// <summary>The member jobs, each as a full snapshot carrying its current state.</summary>
    [Description("The workflow's member jobs, one full snapshot each, carrying each member's current state — the graph's nodes. Never payload bytes.")]
    public required IReadOnlyList<JobRow> Members { get; init; }

    /// <summary>The fixed dependency edges between members.</summary>
    [Description("The fixed dependency edges between members — the graph's arrows. Recorded when the workflow is enqueued and complete for its whole life; the child runs only after the parent completes.")]
    public required IReadOnlyList<WorkflowEdgeRow> Edges { get; init; }

    /// <summary>The workflow this one was restarted from, or null if created fresh.</summary>
    [Description("The id of the workflow this one was restarted from; null when it was created fresh.")]
    public Guid? RestartedFrom { get; init; }
}

/// <summary>One dependency edge of a workflow's graph: <paramref name="Child"/> runs after <paramref name="Parent"/>.</summary>
/// <param name="Parent">The depended-upon member job.</param>
/// <param name="Child">The member job that waits for the parent.</param>
internal sealed record WorkflowEdgeRow(
    [property: Description("The id of the depended-upon member job.")]
    Guid Parent,
    [property: Description("The id of the member job that waits for the parent to complete.")]
    Guid Child);

/// <summary>The structured result of <c>cancel_workflow</c>.</summary>
/// <param name="Found">Whether the workflow existed.</param>
/// <param name="CancelledImmediately">How many members were cancelled outright.</param>
/// <param name="CancellationRequested">How many running members were asked to stop cooperatively.</param>
internal sealed record CancelWorkflowToolResult(
    [property: Description("Whether a workflow with the requested id existed. False means nothing was cancelled — an answer, not an error.")]
    bool Found,
    [property: Description("How many still-pending members were cancelled outright and are now terminal Cancelled.")]
    int CancelledImmediately,
    [property: Description("How many running members were asked to stop cooperatively; each cancels when its handler next checks. Members that had already finished are counted in neither field.")]
    int CancellationRequested);
