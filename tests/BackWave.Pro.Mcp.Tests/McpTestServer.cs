using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Pro.Mcp.Tests;

/// <summary>How the MCP surface is mounted on the test host's pipeline.</summary>
public enum McpMountShape
{
    /// <summary>Branch-style <c>app.UseBackWaveProMcp(prefix)</c> — the documented primary.</summary>
    Use,

    /// <summary>Endpoint-style <c>app.MapBackWaveProMcp(prefix)</c> — the composable alternative.</summary>
    Map,
}

/// <summary>
/// An in-process host with the full consumer wiring — <c>AddBackWave</c> + <c>bw.AddMcp()</c> over
/// an in-memory store, mounted in either shape — plus an <see cref="McpTestClient"/> against it.
/// The reusable fixture for every MCP test: start one, seed jobs through <see cref="Store"/> or
/// <see cref="SeedJobAsync"/>, then drive the protocol through <see cref="Client"/>.
/// </summary>
public sealed class McpTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _prefix;

    private McpTestServer(WebApplication app, InMemoryJobStore store, string prefix)
    {
        _app = app;
        _prefix = prefix;
        Store = store;
        Client = new McpTestClient(app.GetTestClient(), prefix);
    }

    /// <summary>The store behind the host, for seeding and direct assertions.</summary>
    public InMemoryJobStore Store { get; }

    /// <summary>An MCP client speaking streamable HTTP against the mounted endpoint.</summary>
    public McpTestClient Client { get; }

    /// <summary>Boots the host and returns the running fixture.</summary>
    public static async Task<McpTestServer> StartAsync(
        Action<BackWaveProMcpOptions>? configure = null,
        McpMountShape mountShape = McpMountShape.Use,
        string prefix = "/backwave-mcp",
        StoreBounds? bounds = null)
    {
        var store = new InMemoryJobStore(bounds);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // The consumer flow under test: one line inside the existing AddBackWave block. An empty
        // registry suffices — the MCP read tools go through the Monitor, which needs no handlers.
        builder.Services.AddBackWave(bw => bw
            .UseStore(store)
            .UseRegistry(new JobRegistry([]))
            .AddMcp(configure));

        var app = builder.Build();
        switch (mountShape)
        {
            case McpMountShape.Use:
                app.UseBackWaveProMcp(prefix);
                break;
            case McpMountShape.Map:
                app.MapBackWaveProMcp(prefix);
                break;
        }

        await app.StartAsync();
        return new McpTestServer(app, store, prefix);
    }

    /// <summary>
    /// A second client against the same host, with its underlying <see cref="HttpClient"/>
    /// customized — for example to send a default header the permission callbacks key off.
    /// </summary>
    public McpTestClient CreateClient(Action<HttpClient> configureHttp)
    {
        var http = _app.GetTestClient();
        configureHttp(http);
        return new McpTestClient(http, _prefix);
    }

    /// <summary>Enqueues one job straight through the store; it stays Scheduled (no workers run).</summary>
    public async Task<Guid> SeedJobAsync(string queue, string wireName = "test-job")
    {
        var id = Guid.NewGuid();
        var result = await Store.EnqueueAsync(
            new NewJob(id, wireName, "{}"u8.ToArray(), queue, DateTimeOffset.UtcNow),
            now: DateTimeOffset.UtcNow);
        Assert.Equal(EnqueueResult.Ok, result);
        return id;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
