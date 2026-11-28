using System.ComponentModel;
using System.Text.Json;
using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BackWave.Pro.Mcp.Tests;

public sealed class HostMcpServerIsolationTests
{
    [Theory]
    [InlineData(McpMountShape.Use)]
    [InlineData(McpMountShape.Map)]
    public async Task EachPath_ServesOnlyItsOwnServer(McpMountShape shape)
    {
        await using var app = await StartHostAsync(shape, configure: null);
        var host = new McpTestClient(app.GetTestClient(), "/mcp");
        var backWave = new McpTestClient(app.GetTestClient(), "/backwave-mcp");

        Assert.Equal("Host", await ServerNameAsync(host));
        Assert.Equal("BackWave", await ServerNameAsync(backWave));

        Assert.Equal(["host_ping"], (await host.ListToolsAsync()).Select(t => t.Name));
        var backWaveTools = (await backWave.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("get_queue_depths", backWaveTools);
        Assert.DoesNotContain("host_ping", backWaveTools);
    }

    [Theory]
    [InlineData(McpMountShape.Use)]
    [InlineData(McpMountShape.Map)]
    public async Task HostCallFilter_DoesNotReachBackWavePath(McpMountShape shape)
    {
        await using var app = await StartHostAsync(shape, configure: null);
        var host = new McpTestClient(app.GetTestClient(), "/mcp");
        var backWave = new McpTestClient(app.GetTestClient(), "/backwave-mcp");

        Assert.False((await backWave.CallToolAsync("get_queue_depths")).IsError);
        Assert.False((await host.CallToolAsync("host_ping")).IsError);
    }

    [Theory]
    [InlineData(McpMountShape.Use)]
    [InlineData(McpMountShape.Map)]
    public async Task BackWaveViewGate_DoesNotFilterTheHostServer(McpMountShape shape)
    {
        await using var app = await StartHostAsync(
            shape, mcp => mcp.AuthorizeView = _ => ValueTask.FromResult(false));
        var host = new McpTestClient(app.GetTestClient(), "/mcp");

        Assert.Equal(["host_ping"], (await host.ListToolsAsync()).Select(t => t.Name));
        Assert.False((await host.CallToolAsync("host_ping")).IsError);
    }

    private static async Task<WebApplication> StartHostAsync(
        McpMountShape shape, Action<BackWaveProMcpOptions>? configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddMcpServer(server => server.ServerInfo = new Implementation { Name = "Host", Version = "1.0" })
            .WithHttpTransport(transport => transport.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<HostTools>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
                context.Params?.Name == "host_ping"
                    ? await next(context, cancellationToken)
                    : new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "host filter" }] }));

        builder.Services.AddBackWave(bw => bw
            .UseStore(new InMemoryJobStore())
            .UseRegistry(new JobRegistry([]))
            .AddMcp(configure));

        var app = builder.Build();
        app.MapMcp("/mcp");
        switch (shape)
        {
            case McpMountShape.Use:
                app.UseBackWaveProMcp();
                break;
            case McpMountShape.Map:
                app.MapBackWaveProMcp();
                break;
        }

        await app.StartAsync();
        return app;
    }

    private static async Task<string?> ServerNameAsync(McpTestClient client)
    {
        var result = await client.SendAsync("initialize", JsonSerializer.Deserialize<JsonElement>("""
            {
              "protocolVersion": "2025-06-18",
              "capabilities": {},
              "clientInfo": { "name": "test", "version": "1" }
            }
            """));
        return result.GetProperty("serverInfo").GetProperty("name").GetString();
    }

    [McpServerToolType]
    public sealed class HostTools
    {
        [McpServerTool(Name = "host_ping"), Description("A tool of the host's own MCP server.")]
        public static string Ping() => "pong";
    }
}
