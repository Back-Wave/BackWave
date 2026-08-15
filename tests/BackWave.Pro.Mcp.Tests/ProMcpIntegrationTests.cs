using System.Text.Json;

namespace BackWave.Pro.Mcp.Tests;

/// <summary>
/// End-to-end through the mounted MCP endpoint (issue 0224): the fresh consumer flow —
/// <c>bw.AddMcp()</c> + <c>UseBackWaveProMcp()</c> — lets an MCP client list and call
/// <c>get_queue_depths</c>, the view gate hides/denies, and both mounting shapes serve the same
/// requests. All protocol traffic goes through the SSE-parsing <see cref="McpTestClient"/>.
/// </summary>
public sealed class ProMcpIntegrationTests
{
    [Fact]
    public async Task ToolsList_ShowsGetQueueDepths_WithAnOutputSchema()
    {
        await using var server = await McpTestServer.StartAsync();

        var tools = await server.Client.ListToolsAsync();

        var tool = Assert.Single(tools, t => t.Name == "get_queue_depths");
        Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        // Structured content advertises its shape: the output schema names the queueDepths rows.
        Assert.NotNull(tool.OutputSchema);
        Assert.True(tool.OutputSchema.Value.GetProperty("properties").TryGetProperty("queueDepths", out _));
    }

    [Fact]
    public async Task CallTool_GetQueueDepths_RoundTripsTypedStructuredContent()
    {
        await using var server = await McpTestServer.StartAsync();
        await server.SeedJobAsync("critical");
        await server.SeedJobAsync("critical");
        await server.SeedJobAsync("bulk");

        var result = await server.Client.CallToolAsync("get_queue_depths");

        Assert.False(result.IsError);
        Assert.NotNull(result.StructuredContent);
        var rows = result.StructuredContent.Value.GetProperty("queueDepths")
            .EnumerateArray()
            .Select(row => (
                Queue: row.GetProperty("queue").GetString(),
                State: row.GetProperty("state").GetString(),
                Count: row.GetProperty("count").GetInt32()))
            .ToList();

        // No workers run in this host, so the seeded jobs sit Scheduled — one row per queue.
        Assert.Contains(("critical", "Scheduled", 2), rows);
        Assert.Contains(("bulk", "Scheduled", 1), rows);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task DeniedViewGate_HidesEveryToolFromToolsList()
    {
        await using var server = await McpTestServer.StartAsync(
            mcp => mcp.AuthorizeView = _ => ValueTask.FromResult(false));

        var tools = await server.Client.ListToolsAsync();

        Assert.Empty(tools);
    }

    [Fact]
    public async Task DeniedViewGate_ErrorsADirectCall()
    {
        // The call-time check is the backstop: a client that ignores the filtered list (or cached
        // an allowed one) still cannot execute the tool.
        await using var server = await McpTestServer.StartAsync(
            mcp => mcp.AuthorizeView = _ => ValueTask.FromResult(false));
        await server.SeedJobAsync("critical");

        var result = await server.Client.CallToolAsync("get_queue_depths");

        Assert.True(result.IsError);
        Assert.Contains("Permission denied", result.Text);
        Assert.Null(result.StructuredContent);
    }

    [Theory]
    [InlineData(McpMountShape.Use)]
    [InlineData(McpMountShape.Map)]
    public async Task BothMountingShapes_ServeTheSameListAndCall(McpMountShape shape)
    {
        await using var server = await McpTestServer.StartAsync(mountShape: shape);
        await server.SeedJobAsync("critical");

        var tools = await server.Client.ListToolsAsync();
        Assert.Single(tools, t => t.Name == "get_queue_depths");

        var result = await server.Client.CallToolAsync("get_queue_depths");
        Assert.False(result.IsError);
        var row = Assert.Single(result.StructuredContent!.Value.GetProperty("queueDepths").EnumerateArray());
        Assert.Equal("critical", row.GetProperty("queue").GetString());
        Assert.Equal(1, row.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task CustomPrefix_ServesAtThatPrefix()
    {
        await using var server = await McpTestServer.StartAsync(prefix: "/agents/backwave");

        var tools = await server.Client.ListToolsAsync();

        Assert.Single(tools, t => t.Name == "get_queue_depths");
    }

    [Fact]
    public async Task PermissionCallback_SeesTheLiveHttpRequest_PerCall()
    {
        // Delegation contract: the gate receives the real HttpContext per request, so a host can
        // key its policy off anything on the request (here: a header). One host, two callers.
        await using var server = await McpTestServer.StartAsync(
            mcp => mcp.AuthorizeView = ctx => ValueTask.FromResult(ctx.Request.Headers.ContainsKey("X-Ops")));

        Assert.Empty(await server.Client.ListToolsAsync());

        var opsClient = server.CreateClient(http => http.DefaultRequestHeaders.Add("X-Ops", "1"));
        Assert.Single(await opsClient.ListToolsAsync(), t => t.Name == "get_queue_depths");
    }
}
