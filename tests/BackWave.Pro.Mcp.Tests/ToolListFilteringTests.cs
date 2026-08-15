namespace BackWave.Pro.Mcp.Tests;

/// <summary>
/// Per-request <c>tools/list</c> filtering generalized across the surface (issue 0227): a tool
/// whose gate denies the current request is absent from the list, an unconfigured host presents
/// the clean read-only list, granting a single write callback surfaces exactly that tool
/// (pause/resume together), and call-time denial stays as the backstop.
/// </summary>
// Joins the sensitive-data env collection so the all-granted snapshot, which exposes sensitive data,
// never races a test that sets the process-wide BACKWAVE_MCP_DISABLE_SENSITIVE_DATA kill-switch.
[Collection(SensitiveDataEnvCollection.Name)]
public sealed class ToolListFilteringTests
{
    private static readonly string[] AllWriteTools =
    [
        "cancel_job", "requeue_job", "pause_queue", "resume_queue", "set_concurrency_limit", "trigger_schedule",
    ];

    // The exact-set snapshot tripwire (independent witness): these wire names are hardcoded string
    // literals, deliberately NOT the ToolNames constants, so a rename of a tool's wire value breaks
    // this test instead of silently sliding through. It catches the rename-ships-ungated trap two
    // ways: a write tool whose attribute Name drifts from its gate key would surface in the default
    // (ungranted) list and fail DefaultServer_PresentsExactlyTheFourteenReadTools; a read tool
    // dropped or renamed would fail the same set-equality. The all-granted set pins the full surface.
    private static readonly string[] DefaultReadTools =
    [
        "search_jobs", "get_job", "get_job_history", "get_job_dependencies",
        "get_observer_lag", "list_observer_dead_letters",
        "list_workflows", "get_workflow",
        "get_queue_settings", "get_tag_facet", "list_wire_names", "list_schedules", "list_audit_records",
        "get_queue_depths",
    ];

    private static readonly string[] SensitiveDataTools = ["get_job_payload", "get_job_output"];

    private static readonly string[] AllTwentyThreeTools =
        [.. DefaultReadTools, .. AllWriteTools, "cancel_workflow", .. SensitiveDataTools];

    [Fact]
    public async Task DefaultServer_PresentsExactlyTheFourteenReadTools()
    {
        // No write grants and sensitive data not authorized: tools/list is exactly the read surface.
        // Set-equality (not Contains) is the point — an accidentally-ungated write tool would appear,
        // and a missing read tool would vanish; either breaks this.
        await using var server = await McpTestServer.StartAsync();

        var tools = (await server.Client.ListToolsAsync()).Select(t => t.Name).ToHashSet();

        Assert.Equal(DefaultReadTools.ToHashSet(), tools);
    }

    [Fact]
    public async Task EveryGateGranted_PresentsExactlyTheFullTwentyThreeTools()
    {
        // Every write callback granted AND sensitive data exposed: tools/list is the entire surface,
        // no more and no less. This pins the full membership so an added or removed tool is caught.
        await using var server = await McpTestServer.StartAsync(mcp =>
        {
            mcp.AuthorizeCancel = _ => ValueTask.FromResult(true);
            mcp.AuthorizeRequeue = _ => ValueTask.FromResult(true);
            mcp.AuthorizePauseQueue = _ => ValueTask.FromResult(true);
            mcp.AuthorizeSetConcurrencyLimit = _ => ValueTask.FromResult(true);
            mcp.AuthorizeTriggerSchedule = _ => ValueTask.FromResult(true);
            // Sensitive-data triple lock: the permission callback plus ExposeSensitiveData (default
            // true) plus the absence of the env kill-switch.
            mcp.AuthorizeViewSensitiveData = _ => ValueTask.FromResult(true);
            mcp.ExposeSensitiveData = true;
        });

        var tools = (await server.Client.ListToolsAsync()).Select(t => t.Name).ToHashSet();

        Assert.Equal(AllTwentyThreeTools.ToHashSet(), tools);
    }

    [Fact]
    public async Task UnconfiguredHost_PresentsTheCleanReadOnlyList()
    {
        await using var server = await McpTestServer.StartAsync();

        var tools = (await server.Client.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.Contains("get_queue_depths", tools);
        Assert.All(AllWriteTools, write => Assert.DoesNotContain(write, tools));
    }

    [Fact]
    public async Task DeniedWrite_CallTimeBackstop_IsAnActionableToolExecutionError()
    {
        // A client that ignores (or cached) the filtered list still cannot execute the tool, and
        // the error names the exact callback the host must grant.
        await using var server = await McpTestServer.StartAsync();
        var jobId = await server.SeedJobAsync("critical");

        var result = await server.Client.CallToolAsync("cancel_job", new { job_id = jobId.ToString() });

        Assert.True(result.IsError);
        Assert.Contains("Permission denied", result.Text);
        Assert.Contains("cancel_job", result.Text);
        Assert.Contains("BackWaveProMcpOptions.AuthorizeCancel", result.Text);
        // And nothing happened: the job is untouched and no audit record was written.
        Assert.NotEqual(
            BackWave.Storage.JobState.Cancelled, (await server.Store.GetJobAsync(jobId))!.State);
        Assert.Empty(await server.Store.ListAuditRecordsAsync(jobId.ToString()));
    }

    [Theory]
    [InlineData("cancel", new[] { "cancel_job" })]
    [InlineData("requeue", new[] { "requeue_job" })]
    [InlineData("pause", new[] { "pause_queue", "resume_queue" })] // one permission, both directions
    [InlineData("limit", new[] { "set_concurrency_limit" })]
    [InlineData("trigger", new[] { "trigger_schedule" })]
    public async Task GrantingOneGate_SurfacesExactlyItsTools(string grant, string[] expected)
    {
        await using var server = await McpTestServer.StartAsync(mcp =>
        {
            Func<Microsoft.AspNetCore.Http.HttpContext, ValueTask<bool>> allow = _ => ValueTask.FromResult(true);
            switch (grant)
            {
                case "cancel": mcp.AuthorizeCancel = allow; break;
                case "requeue": mcp.AuthorizeRequeue = allow; break;
                case "pause": mcp.AuthorizePauseQueue = allow; break;
                case "limit": mcp.AuthorizeSetConcurrencyLimit = allow; break;
                case "trigger": mcp.AuthorizeTriggerSchedule = allow; break;
            }
        });

        var tools = (await server.Client.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.Equal(expected, tools.Where(AllWriteTools.Contains).Order());
        Assert.Contains("get_queue_depths", tools); // the reads are unaffected by write grants
    }

    [Fact]
    public async Task GrantingEveryGate_SurfacesAllSixWriteTools()
    {
        await using var server = await McpTestServer.StartAsync(mcp =>
        {
            mcp.AuthorizeCancel = _ => ValueTask.FromResult(true);
            mcp.AuthorizeRequeue = _ => ValueTask.FromResult(true);
            mcp.AuthorizePauseQueue = _ => ValueTask.FromResult(true);
            mcp.AuthorizeSetConcurrencyLimit = _ => ValueTask.FromResult(true);
            mcp.AuthorizeTriggerSchedule = _ => ValueTask.FromResult(true);
        });

        var tools = (await server.Client.ListToolsAsync()).Select(t => t.Name).ToList();

        foreach (var write in AllWriteTools)
        {
            Assert.Contains(write, tools);
        }
    }

    [Fact]
    public async Task Filtering_IsPerRequest_OneHostTwoCallers()
    {
        // The gate sees the live request, so the same host shows different lists to different
        // callers — and the backstop tracks the same decision at call time.
        await using var server = await McpTestServer.StartAsync(
            mcp => mcp.AuthorizeCancel = ctx => ValueTask.FromResult(ctx.Request.Headers.ContainsKey("X-Ops")));
        var jobId = await server.SeedJobAsync("critical");
        var ops = server.CreateClient(http => http.DefaultRequestHeaders.Add("X-Ops", "1"));

        Assert.DoesNotContain("cancel_job", (await server.Client.ListToolsAsync()).Select(t => t.Name));
        Assert.Contains("cancel_job", (await ops.ListToolsAsync()).Select(t => t.Name));

        Assert.True((await server.Client.CallToolAsync("cancel_job", new { job_id = jobId.ToString() })).IsError);
        Assert.False((await ops.CallToolAsync("cancel_job", new { job_id = jobId.ToString() })).IsError);
    }
}
