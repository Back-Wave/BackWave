using BackWave.Storage;

namespace BackWave.Pro.Mcp.Tests;

/// <summary>
/// The three workflow tools end-to-end through the mounted MCP endpoint (issue 0228):
/// <c>list_workflows</c> pages snapshot rows with the <c>search_jobs</c> envelope conventions,
/// <c>get_workflow</c> returns the full graph whose per-job states match the member jobs' actual
/// states (unknown or malformed id is a <c>{found:false}</c> result, never an error), and
/// <c>cancel_workflow</c> rides the same <c>AuthorizeCancel</c> gate as <c>cancel_job</c> —
/// hidden and denied without the grant, cancelling the non-terminal members with actor-stamped
/// audit records when granted.
/// </summary>
public sealed class WorkflowToolsTests
{
    private static readonly Action<BackWaveProMcpOptions> GrantCancel =
        mcp => mcp.AuthorizeCancel = _ => ValueTask.FromResult(true);

    [Fact]
    public async Task UnconfiguredHost_ListsTheWorkflowReads_ButHidesCancelWorkflow()
    {
        await using var server = await McpTestServer.StartAsync();

        var tools = (await server.Client.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.Contains("list_workflows", tools);
        Assert.Contains("get_workflow", tools);
        Assert.DoesNotContain("cancel_workflow", tools);
    }

    [Fact]
    public async Task GrantingAuthorizeCancel_SurfacesCancelWorkflowAndCancelJobTogether()
    {
        // One gate, two tools: cancel_workflow rides the SAME AuthorizeCancel as cancel_job.
        await using var server = await McpTestServer.StartAsync(GrantCancel);

        var tools = (await server.Client.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.Contains("cancel_workflow", tools);
        Assert.Contains("cancel_job", tools);
    }

    [Fact]
    public async Task ListWorkflows_ReturnsSnapshotRows_WithDerivedStatusAndMemberCount()
    {
        await using var server = await McpTestServer.StartAsync();
        var (workflowId, _, _) = await SeedFanOutWorkflowAsync(server.Store, "resize order-1001");

        var result = await server.Client.CallToolAsync("list_workflows");

        Assert.False(result.IsError);
        var structured = result.StructuredContent!.Value;
        var row = Assert.Single(structured.GetProperty("workflows").EnumerateArray());
        Assert.Equal(workflowId, Guid.Parse(row.GetProperty("workflowId").GetString()!));
        Assert.Equal("resize order-1001", row.GetProperty("name").GetString());
        Assert.Equal("Running", row.GetProperty("status").GetString());
        Assert.Equal(3, row.GetProperty("memberCount").GetInt32());
        Assert.False(structured.GetProperty("hasMore").GetBoolean());
        AssertJson.NullOrAbsent(structured, "nextCursor");
    }

    [Fact]
    public async Task ListWorkflows_PagesNewestFirst_WithTheCursorEnvelope()
    {
        await using var server = await McpTestServer.StartAsync();
        var baseTime = DateTimeOffset.UtcNow;
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var (id, _, _) = await SeedFanOutWorkflowAsync(server.Store, $"wf-{i}", baseTime.AddMinutes(i));
            ids.Add(id);
        }

        var first = await server.Client.CallToolAsync("list_workflows", new { max_results = 2 });
        Assert.False(first.IsError);
        var firstPage = first.StructuredContent!.Value;
        Assert.Equal(
            [ids[2], ids[1]], // newest-first by default
            firstPage.GetProperty("workflows").EnumerateArray()
                .Select(w => Guid.Parse(w.GetProperty("workflowId").GetString()!)));
        Assert.True(firstPage.GetProperty("hasMore").GetBoolean());
        var cursor = firstPage.GetProperty("nextCursor").GetString();
        Assert.Equal(ids[1].ToString(), cursor);

        var second = await server.Client.CallToolAsync(
            "list_workflows", new { max_results = 2, after_cursor = cursor });
        Assert.False(second.IsError);
        var secondPage = second.StructuredContent!.Value;
        var row = Assert.Single(secondPage.GetProperty("workflows").EnumerateArray());
        Assert.Equal(ids[0], Guid.Parse(row.GetProperty("workflowId").GetString()!));
        Assert.False(secondPage.GetProperty("hasMore").GetBoolean());
        AssertJson.NullOrAbsent(secondPage, "nextCursor");
    }

    [Fact]
    public async Task ListWorkflows_OldestFirst_ReversesTheOrder()
    {
        await using var server = await McpTestServer.StartAsync();
        var baseTime = DateTimeOffset.UtcNow;
        var (older, _, _) = await SeedFanOutWorkflowAsync(server.Store, "older", baseTime);
        var (newer, _, _) = await SeedFanOutWorkflowAsync(server.Store, "newer", baseTime.AddMinutes(1));

        var result = await server.Client.CallToolAsync("list_workflows", new { sort = "oldest_first" });

        Assert.False(result.IsError);
        Assert.Equal(
            [older, newer],
            result.StructuredContent!.Value.GetProperty("workflows").EnumerateArray()
                .Select(w => Guid.Parse(w.GetProperty("workflowId").GetString()!)));
    }

    [Fact]
    public async Task ListWorkflows_MaxResultsAboveTheStorePageCap_IsClampedToTheConfiguredCap()
    {
        // An in-memory slice must not exceed the store's configured monitor page cap however large
        // max_results is, so one call can never serialize every workflow at once. The clamp reads the
        // store's actual cap through the monitor, not a hardcoded default: configure a non-default cap
        // and the clamp must track it — the assertion that would have caught a hardcoded 200.
        const int cap = 10;
        await using var server = await McpTestServer.StartAsync(
            bounds: StoreBounds.Default with { MaxMonitorPageSize = cap });
        var baseTime = DateTimeOffset.UtcNow;
        for (var i = 0; i < cap + 5; i++)
        {
            await SeedFanOutWorkflowAsync(server.Store, $"wf-{i}", baseTime.AddSeconds(i));
        }

        var result = await server.Client.CallToolAsync("list_workflows", new { max_results = 100_000 });

        Assert.False(result.IsError);
        var structured = result.StructuredContent!.Value;
        Assert.Equal(cap, structured.GetProperty("workflows").EnumerateArray().Count());
        // The extra rows remain reachable behind the cursor, not silently dropped.
        Assert.True(structured.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task ListWorkflows_DegenerateStorePageCapOfZero_ReturnsACleanSingleRowPage()
    {
        // A misconfigured host can set the monitor page cap to 0. A plain Min clamp would drive the
        // page size to 0, and any existing workflow would then set hasMore=true over an empty page, so
        // the NextCursor read (page[^1]) throws and surfaces as an opaque server fault. The Math.Max(1)
        // floor must yield a clean single-row page instead — the assertion that catches a missing floor.
        await using var server = await McpTestServer.StartAsync(
            bounds: StoreBounds.Default with { MaxMonitorPageSize = 0 });
        await SeedFanOutWorkflowAsync(server.Store, "wf-degenerate-cap");

        var result = await server.Client.CallToolAsync("list_workflows");

        Assert.False(result.IsError);
        var row = Assert.Single(result.StructuredContent!.Value.GetProperty("workflows").EnumerateArray());
        Assert.Equal("wf-degenerate-cap", row.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ListWorkflows_CursorWorkflowPurgedBetweenPages_RecoversWithoutError()
    {
        await using var server = await McpTestServer.StartAsync();
        var baseTime = DateTimeOffset.UtcNow;
        var (wf0, _, _) = await SeedFanOutWorkflowAsync(server.Store, "wf-0", baseTime);
        var (wf1, _, _) = await SeedFanOutWorkflowAsync(server.Store, "wf-1", baseTime.AddMinutes(1));
        var (wf2, r2, c2) = await SeedFanOutWorkflowAsync(server.Store, "wf-2", baseTime.AddMinutes(2));

        // Page 1 (newest-first) returns wf2 and hands back its id as the resume cursor.
        var first = await server.Client.CallToolAsync("list_workflows", new { max_results = 1 });
        Assert.False(first.IsError);
        var cursor = first.StructuredContent!.Value.GetProperty("nextCursor").GetString();
        Assert.Equal(wf2.ToString(), cursor);

        // Workflow-aware retention drains and purges wf2 (the cursor's own workflow) before page 2.
        await PurgeWorkflowAsync(server.Store, r2, c2);

        // The now-absent-but-well-formed cursor does NOT error: paging recovers and the remaining
        // rows are all still reachable (restart-from-start semantics — no exception, no data loss).
        var second = await server.Client.CallToolAsync(
            "list_workflows", new { max_results = 10, after_cursor = cursor });
        Assert.False(second.IsError);
        Assert.Equal(
            [wf1, wf0], // newest-first of what survives the purge; nothing dropped
            second.StructuredContent!.Value.GetProperty("workflows").EnumerateArray()
                .Select(w => Guid.Parse(w.GetProperty("workflowId").GetString()!)));

        // A genuinely malformed (non-GUID) cursor still errors with actionable text.
        var malformed = await server.Client.CallToolAsync(
            "list_workflows", new { after_cursor = "not-a-guid" });
        Assert.True(malformed.IsError);
        Assert.Contains("after_cursor", malformed.Text);
    }

    [Fact]
    public async Task ListWorkflows_UnknownSortOrCursor_AreToolExecutionErrorsWithActionableText()
    {
        await using var server = await McpTestServer.StartAsync();

        var badSort = await server.Client.CallToolAsync("list_workflows", new { sort = "sideways" });
        Assert.True(badSort.IsError);
        Assert.Contains("newest_first", badSort.Text);

        var badCursor = await server.Client.CallToolAsync("list_workflows", new { after_cursor = "not-a-cursor" });
        Assert.True(badCursor.IsError);
        Assert.Contains("after_cursor", badCursor.Text);
        Assert.Contains("nextCursor", badCursor.Text);
    }

    [Fact]
    public async Task GetWorkflow_GraphCarriesPerJobStates_MatchingTheMembersActualStates()
    {
        await using var server = await McpTestServer.StartAsync();
        var (workflowId, rootId, childIds) = await SeedFanOutWorkflowAsync(server.Store, "resize order-1001");
        // Drive the root to Succeeded so the members span three distinct live states.
        await SucceedJobAsync(server, rootId, queue: "resize");

        var result = await server.Client.CallToolAsync(
            "get_workflow", new { workflow_id = workflowId.ToString() });

        Assert.False(result.IsError);
        var structured = result.StructuredContent!.Value;
        Assert.True(structured.GetProperty("found").GetBoolean());
        var workflow = structured.GetProperty("workflow");
        Assert.Equal(workflowId, Guid.Parse(workflow.GetProperty("workflowId").GetString()!));
        Assert.Equal("Running", workflow.GetProperty("status").GetString());

        // Every member node's state matches that member job's actual state in the store.
        var members = workflow.GetProperty("members").EnumerateArray().ToList();
        Assert.Equal(3, members.Count);
        foreach (var member in members)
        {
            var jobId = Guid.Parse(member.GetProperty("jobId").GetString()!);
            var actual = await server.Store.GetJobAsync(jobId);
            Assert.Equal(actual!.State.ToString(), member.GetProperty("state").GetString());
        }
        Assert.Equal(
            "Succeeded",
            members.Single(m => Guid.Parse(m.GetProperty("jobId").GetString()!) == rootId)
                .GetProperty("state").GetString());

        // The graph's arrows: one fixed edge from the root to each child.
        var edges = workflow.GetProperty("edges").EnumerateArray()
            .Select(e => (
                Parent: Guid.Parse(e.GetProperty("parent").GetString()!),
                Child: Guid.Parse(e.GetProperty("child").GetString()!)))
            .ToList();
        Assert.Equal(2, edges.Count);
        Assert.All(edges, e => Assert.Equal(rootId, e.Parent));
        Assert.Equal(childIds.Order(), edges.Select(e => e.Child).Order());
    }

    [Fact]
    public async Task GetWorkflow_UnknownId_IsAFoundFalseResult()
    {
        await using var server = await McpTestServer.StartAsync();

        var result = await server.Client.CallToolAsync(
            "get_workflow", new { workflow_id = Guid.NewGuid().ToString() });

        Assert.False(result.IsError);
        Assert.False(result.StructuredContent!.Value.GetProperty("found").GetBoolean());
        AssertJson.NullOrAbsent(result.StructuredContent!.Value, "workflow");
    }

    [Fact]
    public async Task GetWorkflow_MalformedId_IsAFoundFalseResult_NotAnError()
    {
        await using var server = await McpTestServer.StartAsync();

        var result = await server.Client.CallToolAsync(
            "get_workflow", new { workflow_id = "not-a-guid" });

        Assert.False(result.IsError);
        Assert.False(result.StructuredContent!.Value.GetProperty("found").GetBoolean());
        AssertJson.NullOrAbsent(result.StructuredContent!.Value, "workflow");
    }

    [Fact]
    public async Task CancelWorkflow_Granted_CancelsTheNonTerminalMembers_AndAuditsEachWithTheActor()
    {
        await using var server = await McpTestServer.StartAsync(GrantCancel);
        var (workflowId, rootId, childIds) = await SeedFanOutWorkflowAsync(server.Store, "resize order-1001");
        // The root has already finished; only the two released children are still cancellable.
        await SucceedJobAsync(server, rootId, queue: "resize");

        var result = await server.Client.CallToolAsync(
            "cancel_workflow", new { workflow_id = workflowId.ToString() });

        Assert.False(result.IsError);
        var structured = result.StructuredContent!.Value;
        Assert.True(structured.GetProperty("found").GetBoolean());
        Assert.Equal(2, structured.GetProperty("cancelledImmediately").GetInt32());
        Assert.Equal(0, structured.GetProperty("cancellationRequested").GetInt32());

        // The non-terminal members are now Cancelled; the finished root is untouched.
        foreach (var childId in childIds)
        {
            Assert.Equal(JobState.Cancelled, (await server.Store.GetJobAsync(childId))!.State);
            var audit = Assert.Single(await server.Store.ListAuditRecordsAsync(childId.ToString()));
            Assert.Equal(OperatorAction.Cancel, audit.Action);
            Assert.Equal("mcp", audit.Actor); // no principal here, so ResolveActor's default fallback
        }
        Assert.Equal(JobState.Succeeded, (await server.Store.GetJobAsync(rootId))!.State);
        Assert.Empty(await server.Store.ListAuditRecordsAsync(rootId.ToString()));

        // The derived status follows: all terminal, none failed, some cancelled.
        var view = await server.Client.CallToolAsync(
            "get_workflow", new { workflow_id = workflowId.ToString() });
        Assert.Equal(
            "Cancelled",
            view.StructuredContent!.Value.GetProperty("workflow").GetProperty("status").GetString());
    }

    [Fact]
    public async Task CancelWorkflow_StampsTheHostResolvedActor()
    {
        await using var server = await McpTestServer.StartAsync(mcp =>
        {
            GrantCancel(mcp);
            mcp.ResolveActor = ctx => ctx.Request.Headers.TryGetValue("X-Api-Key-Owner", out var owner)
                ? $"api:{owner}"
                : "mcp";
        });
        var (workflowId, rootId, _) = await SeedFanOutWorkflowAsync(server.Store, "resize order-1001");
        var ops = server.CreateClient(http => http.DefaultRequestHeaders.Add("X-Api-Key-Owner", "alice"));

        Assert.False((await ops.CallToolAsync(
            "cancel_workflow", new { workflow_id = workflowId.ToString() })).IsError);

        // The root is the member the operator cancel lands on (cancelling it fans parent-failure
        // out to the awaiting children, which therefore carry no operator audit of their own).
        var audit = Assert.Single(await server.Store.ListAuditRecordsAsync(rootId.ToString()));
        Assert.Equal("api:alice", audit.Actor);
        Assert.Equal(OperatorAction.Cancel, audit.Action);
    }

    [Fact]
    public async Task CancelWorkflow_Denied_IsHiddenFromTheList_AndTheCallTimeBackstopErrors()
    {
        await using var server = await McpTestServer.StartAsync(); // AuthorizeCancel stays default-deny
        var (workflowId, rootId, _) = await SeedFanOutWorkflowAsync(server.Store, "resize order-1001");

        Assert.DoesNotContain(
            "cancel_workflow", (await server.Client.ListToolsAsync()).Select(t => t.Name));

        var result = await server.Client.CallToolAsync(
            "cancel_workflow", new { workflow_id = workflowId.ToString() });

        Assert.True(result.IsError);
        Assert.Contains("Permission denied", result.Text);
        Assert.Contains("cancel_workflow", result.Text);
        Assert.Contains("BackWaveProMcpOptions.AuthorizeCancel", result.Text);
        // And nothing happened: the members are untouched and no audit record was written.
        Assert.Equal(JobState.Scheduled, (await server.Store.GetJobAsync(rootId))!.State);
        Assert.Empty(await server.Store.ListAuditRecordsAsync(rootId.ToString()));
    }

    [Fact]
    public async Task CancelWorkflow_UnknownId_IsAFoundFalseResult()
    {
        await using var server = await McpTestServer.StartAsync(GrantCancel);

        var result = await server.Client.CallToolAsync(
            "cancel_workflow", new { workflow_id = Guid.NewGuid().ToString() });

        Assert.False(result.IsError);
        var structured = result.StructuredContent!.Value;
        Assert.False(structured.GetProperty("found").GetBoolean());
        Assert.Equal(0, structured.GetProperty("cancelledImmediately").GetInt32());
        Assert.Equal(0, structured.GetProperty("cancellationRequested").GetInt32());
    }

    [Fact]
    public async Task CancelWorkflow_MalformedId_IsAToolExecutionErrorWithActionableText()
    {
        await using var server = await McpTestServer.StartAsync(GrantCancel);

        var result = await server.Client.CallToolAsync(
            "cancel_workflow", new { workflow_id = "not-a-guid" });

        Assert.True(result.IsError);
        Assert.Contains("workflow_id", result.Text);
        Assert.Contains("GUID", result.Text);
    }

    /// <summary>
    /// Enqueues a fan-out workflow straight through the store: one root member plus two children
    /// that each depend on it. The root is Scheduled and the children wait in AwaitingParent.
    /// </summary>
    internal static async Task<(Guid WorkflowId, Guid RootId, Guid[] ChildIds)> SeedFanOutWorkflowAsync(
        BackWave.Storage.InMemory.InMemoryJobStore store, string? name = null, DateTimeOffset? now = null)
    {
        var instant = now ?? DateTimeOffset.UtcNow;
        var rootId = Guid.NewGuid();
        var childIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var definition = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Name = name,
            Members =
            [
                new NewJob(rootId, "resize-image", "{}"u8.ToArray(), "resize", instant),
                new NewJob(childIds[0], "publish-image", "{}"u8.ToArray(), "publish", instant) { Parents = [rootId] },
                new NewJob(childIds[1], "notify-owner", "{}"u8.ToArray(), "notify", instant) { Parents = [rootId] },
            ],
        };
        var result = await store.EnqueueWorkflowAsync(definition, instant);
        Assert.Equal(WorkflowEnqueueResult.Ok, result);
        return (definition.WorkflowId, rootId, childIds);
    }

    /// <summary>
    /// Cancels every member of a workflow to terminal, then purges the drained unit — the store
    /// drops the now-orphaned workflow row, so it vanishes from list_workflows (as retention would).
    /// </summary>
    private static async Task PurgeWorkflowAsync(
        BackWave.Storage.InMemory.InMemoryJobStore store, Guid rootId, Guid[] childIds)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var memberId in new[] { rootId }.Concat(childIds))
        {
            await store.CancelJobAsync(memberId, "test", now);
        }
        // A far-future window makes the whole drained unit retention-eligible at once.
        await store.PurgeTerminalAsync(TerminalStateClass.SucceededOrCancelled, DateTimeOffset.MaxValue, 1000);
    }

    /// <summary>Drives one Scheduled job through claim + success so it lands terminal Succeeded.</summary>
    private static async Task SucceedJobAsync(McpTestServer server, Guid jobId, string queue)
    {
        var now = DateTimeOffset.UtcNow;
        var claimed = Assert.Single(await server.Store.ClaimAsync(
            new ClaimRequest("test-worker", [queue], MaxJobs: 1, LeaseDuration: TimeSpan.FromMinutes(1), Now: now)));
        Assert.Equal(jobId, claimed.JobId);
        var outcome = await server.Store.ReportOutcomeAsync(
            jobId, "test-worker", claimed.Attempt, new JobOutcome.Success(), now);
        Assert.Equal(OutcomeResult.Applied, outcome);
        Assert.Equal(JobState.Succeeded, (await server.Store.GetJobAsync(jobId))!.State);
    }
}
