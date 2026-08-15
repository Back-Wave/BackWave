using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Observers;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Pro.Mcp.Tests;

/// <summary>
/// The observer read tools (issue 0226): <c>get_observer_lag</c> and
/// <c>list_observer_dead_letters</c> resolve the observer host-side from the canonical registered
/// list — like the dashboard — so a client supplies only the id, and an unregistered id is a
/// not-found answer rather than a fault.
/// </summary>
public sealed class ObserverToolsTests
{
    [Fact]
    public async Task GetObserverLag_ResolvesTheSubscriptionHostSide_AndReportsLag()
    {
        // "slack" subscribes to every transition: the two seeded jobs each logged a Scheduled
        // transition, so its pending count sees them; nothing was delivered, so the cursor is -1.
        await using var host = await ObserverHost.StartAsync(
            new ObserverRegistration("slack", ObserverSubscription.AllTransitions));
        await host.SeedJobAsync("critical");
        await host.SeedJobAsync("critical");

        var result = await host.Client.CallToolAsync(
            "get_observer_lag", new Dictionary<string, object?> { ["observer_id"] ="slack" });

        Assert.False(result.IsError);
        var content = result.StructuredContent!.Value;
        Assert.True(content.GetProperty("found").GetBoolean());
        Assert.Equal(-1, content.GetProperty("cursor").GetInt64());
        Assert.Equal(2, content.GetProperty("pending").GetInt32());
    }

    [Fact]
    public async Task GetObserverLag_CountsOnlyTransitionsMatchingTheSubscription()
    {
        // "pager" only watches DeadLettered: the seeded Scheduled transitions do not match, so it
        // is caught up even though the log is not empty.
        await using var host = await ObserverHost.StartAsync(
            new ObserverRegistration("pager", new ObserverSubscription([JobState.DeadLettered])));
        await host.SeedJobAsync("critical");

        var result = await host.Client.CallToolAsync(
            "get_observer_lag", new Dictionary<string, object?> { ["observer_id"] ="pager" });

        Assert.False(result.IsError);
        var content = result.StructuredContent!.Value;
        Assert.True(content.GetProperty("found").GetBoolean());
        Assert.Equal(0, content.GetProperty("pending").GetInt32());
        AssertJson.NullOrAbsent(content, "oldestPendingAt"); // caught up
    }

    [Fact]
    public async Task GetObserverLag_UnregisteredObserver_IsFoundFalse_NotAnError()
    {
        await using var host = await ObserverHost.StartAsync(
            new ObserverRegistration("slack", ObserverSubscription.AllTransitions));

        var result = await host.Client.CallToolAsync(
            "get_observer_lag", new Dictionary<string, object?> { ["observer_id"] ="unknown" });

        Assert.False(result.IsError);
        Assert.False(result.StructuredContent!.Value.GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task ListObserverDeadLetters_HealthyObserver_ReturnsAnEmptyList()
    {
        await using var host = await ObserverHost.StartAsync(
            new ObserverRegistration("slack", ObserverSubscription.AllTransitions));
        await host.SeedJobAsync("critical");

        var result = await host.Client.CallToolAsync(
            "list_observer_dead_letters", new Dictionary<string, object?> { ["observer_id"] ="slack" });

        Assert.False(result.IsError);
        var content = result.StructuredContent!.Value;
        Assert.True(content.GetProperty("found").GetBoolean());
        Assert.Empty(content.GetProperty("deadLetters").EnumerateArray());
    }

    [Fact]
    public async Task ListObserverDeadLetters_SurfacesDeadLetteredDeliveries()
    {
        await using var host = await ObserverHost.StartAsync(
            new ObserverRegistration("slack", ObserverSubscription.AllTransitions));
        var jobId = await host.SeedJobAsync("critical");

        // Drive one delivery to its dead-letter: claim the seeded Scheduled transition for the
        // observer, then report it dead-lettered — the poison-row path the pump takes after the
        // retry ceiling.
        var now = DateTimeOffset.UtcNow;
        var claim = await host.Store.ClaimObserverDeliveriesAsync(new ObserverClaimRequest(
            "slack", ObserverSubscription.AllTransitions.States, WireName: null, Queue: null,
            WorkerId: "w1", MaxRows: 10, TimeSpan.FromMinutes(5), now));
        var delivery = Assert.Single(claim.Deliveries);
        await host.Store.ReportObserverDeliveriesAsync(new ObserverDeliveryReport(
            "slack", "w1", [new ObserverDeliveryOutcome(delivery.Position, ObserverDeliveryDisposition.DeadLettered)], now));

        var result = await host.Client.CallToolAsync(
            "list_observer_dead_letters", new Dictionary<string, object?> { ["observer_id"] ="slack" });

        Assert.False(result.IsError);
        var content = result.StructuredContent!.Value;
        Assert.True(content.GetProperty("found").GetBoolean());
        var row = Assert.Single(content.GetProperty("deadLetters").EnumerateArray());
        Assert.Equal(jobId, row.GetProperty("jobId").GetGuid());
        Assert.Equal("Scheduled", row.GetProperty("state").GetString());
        Assert.Equal(delivery.Position, row.GetProperty("position").GetInt64());
    }

    [Fact]
    public async Task ListObserverDeadLetters_UnregisteredObserver_IsFoundFalse()
    {
        await using var host = await ObserverHost.StartAsync(
            new ObserverRegistration("slack", ObserverSubscription.AllTransitions));

        var result = await host.Client.CallToolAsync(
            "list_observer_dead_letters", new Dictionary<string, object?> { ["observer_id"] ="unknown" });

        Assert.False(result.IsError);
        var content = result.StructuredContent!.Value;
        Assert.False(content.GetProperty("found").GetBoolean());
        Assert.Empty(content.GetProperty("deadLetters").EnumerateArray());
    }

    [Fact]
    public async Task NoObserversRegistered_ToolsStillListed_AndAnswerFoundFalse()
    {
        // The harness host registers no observers at all (no registration list in DI): the tools
        // stay listed and answer not-found instead of failing to resolve.
        await using var server = await McpTestServer.StartAsync();

        var tools = await server.Client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "get_observer_lag");
        Assert.Contains(tools, t => t.Name == "list_observer_dead_letters");

        var result = await server.Client.CallToolAsync(
            "get_observer_lag", new Dictionary<string, object?> { ["observer_id"] ="slack" });
        Assert.False(result.IsError);
        Assert.False(result.StructuredContent!.Value.GetProperty("found").GetBoolean());
    }

    /// <summary>
    /// A host with observer registrations published to DI — the canonical list <c>AddObservers</c>
    /// registers — without running the dispatch pump, so lag and dead-letter reads stay
    /// deterministic under test.
    /// </summary>
    private sealed class ObserverHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ObserverHost(WebApplication app, InMemoryJobStore store)
        {
            _app = app;
            Store = store;
            Client = new McpTestClient(app.GetTestClient());
        }

        public InMemoryJobStore Store { get; }

        public McpTestClient Client { get; }

        public static async Task<ObserverHost> StartAsync(params ObserverRegistration[] observers)
        {
            var store = new InMemoryJobStore();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddBackWave(bw => bw
                .UseStore(store)
                .UseRegistry(new JobRegistry([]))
                .AddMcp());
            builder.Services.AddSingleton<IReadOnlyList<ObserverRegistration>>(observers);

            var app = builder.Build();
            app.UseBackWaveProMcp();
            await app.StartAsync();
            return new ObserverHost(app, store);
        }

        public async Task<Guid> SeedJobAsync(string queue)
        {
            var id = Guid.NewGuid();
            var result = await Store.EnqueueAsync(
                new NewJob(id, "test-job", "{}"u8.ToArray(), queue, DateTimeOffset.UtcNow),
                now: DateTimeOffset.UtcNow);
            Assert.Equal(EnqueueResult.Ok, result);
            return id;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
