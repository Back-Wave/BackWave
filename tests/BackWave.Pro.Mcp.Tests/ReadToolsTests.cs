using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Pro.Mcp.Tests;

/// <summary>
/// The plain view-gated read tools (issue 0226): queue settings, tag facet, wire names, schedules,
/// and the operator audit trail — all listed on an unconfigured host, all returning structured
/// content that mirrors what the Monitor reads.
/// </summary>
public sealed class ReadToolsTests
{
    [Fact]
    public async Task UnconfiguredHost_ListsTheWholeNonSensitiveReadSurface_AndNothingElse()
    {
        await using var server = await McpTestServer.StartAsync();

        var tools = await server.Client.ListToolsAsync();

        // The complete 14-tool non-sensitive read surface (the 12 plain reads plus the two
        // workflow reads of 0228); this assertion is exact so it fails loudly if a sensitive or
        // write tool ever leaks into the unconfigured default.
        string[] expected =
        [
            "get_job",
            "get_job_dependencies",
            "get_job_history",
            "get_observer_lag",
            "get_queue_depths",
            "get_queue_settings",
            "get_tag_facet",
            "get_workflow",
            "list_audit_records",
            "list_observer_dead_letters",
            "list_schedules",
            "list_wire_names",
            "list_workflows",
            "search_jobs",
        ];
        Assert.Equal(expected, tools.Select(t => t.Name).Order().ToArray());
    }

    [Fact]
    public async Task EveryReadTool_AdvertisesADescriptionAndAnOutputSchema()
    {
        await using var server = await McpTestServer.StartAsync();

        var tools = await server.Client.ListToolsAsync();

        Assert.All(tools, tool =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.NotNull(tool.OutputSchema);
        });
    }

    [Fact]
    public async Task GetQueueSettings_ReflectsPauseStateAndConcurrencyLimit()
    {
        await using var server = await McpTestServer.StartAsync();
        // A queue appears once either write path touched it: pause-only and limit-only both count.
        await server.Store.PauseQueueAsync("critical", "alice", DateTimeOffset.UtcNow);
        await server.Store.SetConcurrencyLimitAsync("bulk", 5, "alice", DateTimeOffset.UtcNow);

        var result = await server.Client.CallToolAsync("get_queue_settings");

        Assert.False(result.IsError);
        var rows = result.StructuredContent!.Value.GetProperty("queueSettings")
            .EnumerateArray()
            .ToDictionary(row => row.GetProperty("queue").GetString()!);
        Assert.Equal(2, rows.Count);
        Assert.True(rows["critical"].GetProperty("paused").GetBoolean());
        AssertJson.NullOrAbsent(rows["critical"], "concurrencyLimit"); // no limit configured
        Assert.False(rows["bulk"].GetProperty("paused").GetBoolean());
        Assert.Equal(5, rows["bulk"].GetProperty("concurrencyLimit").GetInt32());
    }

    [Fact]
    public async Task GetTagFacet_CountsDistinctJobsPerValue_CountDescending()
    {
        await using var server = await McpTestServer.StartAsync();
        await SeedTaggedJobAsync(server, "critical", JobTags.Empty.WithTag("tenant", "acme"));
        await SeedTaggedJobAsync(server, "critical", JobTags.Empty.WithTag("tenant", "acme"));
        await SeedTaggedJobAsync(server, "bulk", JobTags.Empty.WithTag("tenant", "globex"));

        var result = await server.Client.CallToolAsync(
            "get_tag_facet", new Dictionary<string, object?> { ["key"] = "tenant" });

        Assert.False(result.IsError);
        var buckets = result.StructuredContent!.Value.GetProperty("buckets")
            .EnumerateArray()
            .Select(b => (Value: b.GetProperty("value").GetString(), Count: b.GetProperty("count").GetInt32()))
            .ToList();
        Assert.Equal([("acme", 2), ("globex", 1)], buckets);
    }

    [Fact]
    public async Task GetTagFacet_ScopesByTheOptionalFilters()
    {
        await using var server = await McpTestServer.StartAsync();
        await SeedTaggedJobAsync(server, "critical", JobTags.Empty.WithTag("tenant", "acme"));
        await SeedTaggedJobAsync(server, "bulk", JobTags.Empty.WithTag("tenant", "globex"));

        var result = await server.Client.CallToolAsync("get_tag_facet", new Dictionary<string, object?>
        {
            ["key"] = "tenant",
            ["queue"] = "bulk",
            ["state"] = "Scheduled",
        });

        Assert.False(result.IsError);
        var bucket = Assert.Single(result.StructuredContent!.Value.GetProperty("buckets").EnumerateArray());
        Assert.Equal("globex", bucket.GetProperty("value").GetString());
    }

    [Fact]
    public async Task GetTagFacet_UnknownState_SurfacesTheValidStatesToTheClient()
    {
        await using var server = await McpTestServer.StartAsync();

        var result = await server.Client.CallToolAsync("get_tag_facet", new Dictionary<string, object?>
        {
            ["key"] = "tenant",
            ["state"] = "NotAState",
        });

        // McpException carries the actionable message all the way to the client as isError text;
        // an ArgumentException would have been swallowed into a generic "an error occurred" string.
        Assert.True(result.IsError);
        Assert.NotNull(result.Text);
        Assert.Contains("NotAState", result.Text);
        Assert.Contains("Valid states", result.Text);
        foreach (var name in Enum.GetNames<JobState>())
        {
            Assert.Contains(name, result.Text);
        }
    }

    [Fact]
    public async Task GetTagFacet_NonPositiveMaxResults_SurfacesTheBoundToTheClient()
    {
        await using var server = await McpTestServer.StartAsync();

        var result = await server.Client.CallToolAsync("get_tag_facet", new Dictionary<string, object?>
        {
            ["key"] = "tenant",
            ["max_results"] = 0,
        });

        Assert.True(result.IsError);
        Assert.NotNull(result.Text);
        Assert.Contains("max_results must be at least 1", result.Text);
    }

    [Fact]
    public async Task ListWireNames_ReturnsTheRegisteredWireNames_Ordered()
    {
        // The harness pins an empty registry, so a registry with entries needs its own host.
        var store = new InMemoryJobStore();
        var registry = new JobRegistry(
            [StubRegistration("send-email", typeof(EmailStub)), StubRegistration("bill-tenant", typeof(BillStub))]);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBackWave(bw => bw.UseStore(store).UseRegistry(registry).AddMcp());
        await using var app = builder.Build();
        app.UseBackWaveProMcp();
        await app.StartAsync();
        var client = new McpTestClient(app.GetTestClient());

        var result = await client.CallToolAsync("list_wire_names");

        Assert.False(result.IsError);
        var names = result.StructuredContent!.Value.GetProperty("wireNames")
            .EnumerateArray().Select(n => n.GetString()!).ToArray();
        Assert.Equal(["bill-tenant", "send-email"], names);
    }

    [Fact]
    public async Task ListWireNames_EmptyRegistry_ReturnsAnEmptyList()
    {
        await using var server = await McpTestServer.StartAsync();

        var result = await server.Client.CallToolAsync("list_wire_names");

        Assert.False(result.IsError);
        Assert.Empty(result.StructuredContent!.Value.GetProperty("wireNames").EnumerateArray());
    }

    [Fact]
    public async Task ListSchedules_ReturnsScheduleStatus_WithNextDue()
    {
        await using var server = await McpTestServer.StartAsync();
        await server.Store.UpsertScheduleAsync(new ScheduleRecord
        {
            ScheduleId = "hourly-report",
            Cron = "0 0 * * * *",
            WireName = "report",
            Payload = "{}"u8.ToArray(),
            Queue = "reports",
            Cursor = new DateTimeOffset(2026, 7, 10, 9, 30, 0, TimeSpan.Zero),
        });

        var result = await server.Client.CallToolAsync("list_schedules");

        Assert.False(result.IsError);
        var row = Assert.Single(result.StructuredContent!.Value.GetProperty("schedules").EnumerateArray());
        Assert.Equal("hourly-report", row.GetProperty("scheduleId").GetString());
        Assert.Equal("0 0 * * * *", row.GetProperty("cron").GetString());
        Assert.Equal("report", row.GetProperty("wireName").GetString());
        Assert.Equal("reports", row.GetProperty("queue").GetString());
        Assert.Equal("Skip", row.GetProperty("catchUp").GetString());
        Assert.False(row.GetProperty("noOverlap").GetBoolean());
        Assert.False(row.GetProperty("hasLiveInstance").GetBoolean());
        // The next top-of-hour after the cursor.
        Assert.Equal(
            new DateTimeOffset(2026, 7, 10, 10, 0, 0, TimeSpan.Zero),
            row.GetProperty("nextDue").GetDateTimeOffset());
        AssertJson.NullOrAbsent(row, "error"); // healthy schedule
    }

    [Fact]
    public async Task ListAuditRecords_ReturnsTheTrailForOneTarget_OldestFirst()
    {
        await using var server = await McpTestServer.StartAsync();
        var t0 = DateTimeOffset.UtcNow;
        await server.Store.PauseQueueAsync("critical", "alice", t0);
        await server.Store.ResumeQueueAsync("critical", "bob", t0.AddMinutes(1));
        await server.Store.PauseQueueAsync("unrelated", "carol", t0);

        var result = await server.Client.CallToolAsync(
            "list_audit_records", new Dictionary<string, object?> { ["target"] = "critical" });

        Assert.False(result.IsError);
        var records = result.StructuredContent!.Value.GetProperty("auditRecords")
            .EnumerateArray()
            .Select(r => (
                Actor: r.GetProperty("actor").GetString(),
                Action: r.GetProperty("action").GetString(),
                Target: r.GetProperty("target").GetString()))
            .ToList();
        Assert.Equal([("alice", "PauseQueue", "critical"), ("bob", "ResumeQueue", "critical")], records);
    }

    [Fact]
    public async Task ListAuditRecords_UntouchedTarget_ReturnsAnEmptyList()
    {
        await using var server = await McpTestServer.StartAsync();

        var result = await server.Client.CallToolAsync(
            "list_audit_records", new Dictionary<string, object?> { ["target"] = "never-touched" });

        Assert.False(result.IsError);
        Assert.Empty(result.StructuredContent!.Value.GetProperty("auditRecords").EnumerateArray());
    }

    private static async Task SeedTaggedJobAsync(McpTestServer server, string queue, JobTags tags)
    {
        var result = await server.Store.EnqueueAsync(
            new NewJob(Guid.NewGuid(), "test-job", "{}"u8.ToArray(), queue, DateTimeOffset.UtcNow) { Tags = tags },
            now: DateTimeOffset.UtcNow);
        Assert.Equal(EnqueueResult.Ok, result);
    }

    // A minimal registration: list_wire_names only reads names, so the delegates never run.
    private static JobRegistration StubRegistration(string wireName, Type jobType) => new()
    {
        WireName = wireName,
        JobType = jobType,
        Queue = "default",
        Deserialize = _ => new object(),
        Serialize = _ => [],
        Execute = (_, _, _, _) => Task.CompletedTask,
    };

    private sealed record EmailStub;

    private sealed record BillStub;
}
