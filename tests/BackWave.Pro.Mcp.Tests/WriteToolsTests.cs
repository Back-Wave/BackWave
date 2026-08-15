using System.Security.Claims;
using System.Security.Principal;
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
/// The six operator write tools end-to-end through the mounted MCP endpoint (issue 0227): each is
/// callable when its gate grants the request, its effect is visible through reads (the
/// <c>get_queue_depths</c> tool or the store), every write lands in the audit trail stamped with
/// the resolved actor, not-found stays a normal structured result, and invalid input is a
/// tool-execution error with actionable text.
/// </summary>
public sealed class WriteToolsTests
{
    private static readonly Action<BackWaveProMcpOptions> GrantAllWrites = mcp =>
    {
        mcp.AuthorizeCancel = _ => ValueTask.FromResult(true);
        mcp.AuthorizeRequeue = _ => ValueTask.FromResult(true);
        mcp.AuthorizePauseQueue = _ => ValueTask.FromResult(true);
        mcp.AuthorizeSetConcurrencyLimit = _ => ValueTask.FromResult(true);
        mcp.AuthorizeTriggerSchedule = _ => ValueTask.FromResult(true);
    };

    [Fact]
    public async Task CancelJob_Granted_CancelsAndTheReadToolSeesIt()
    {
        await using var server = await McpTestServer.StartAsync(GrantAllWrites);
        var jobId = await server.SeedJobAsync("critical");

        var result = await server.Client.CallToolAsync("cancel_job", new { job_id = jobId.ToString() });

        Assert.False(result.IsError);
        Assert.Equal("CancelledImmediately", result.StructuredContent!.Value.GetProperty("status").GetString());

        // The effect is visible through the read tool: the queue's one job is now Cancelled.
        var depths = await server.Client.CallToolAsync("get_queue_depths");
        var row = Assert.Single(depths.StructuredContent!.Value.GetProperty("queueDepths").EnumerateArray());
        Assert.Equal("Cancelled", row.GetProperty("state").GetString());
    }

    [Fact]
    public async Task CancelJob_UnknownJob_IsANormalNotCancellableResult()
    {
        await using var server = await McpTestServer.StartAsync(GrantAllWrites);

        var result = await server.Client.CallToolAsync("cancel_job", new { job_id = Guid.NewGuid().ToString() });

        // Not-found is an answer, not a fault (mcp-0003 error conventions).
        Assert.False(result.IsError);
        Assert.Equal("NotCancellable", result.StructuredContent!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task RequeueJob_Granted_ReturnsADeadLetteredJobToScheduled()
    {
        await using var server = await McpTestServer.StartAsync(GrantAllWrites);
        var jobId = await DeadLetterAJobAsync(server, "critical");

        var result = await server.Client.CallToolAsync("requeue_job", new { job_id = jobId.ToString() });

        Assert.False(result.IsError);
        Assert.Equal("Requeued", result.StructuredContent!.Value.GetProperty("status").GetString());
        var job = await server.Store.GetJobAsync(jobId);
        Assert.Equal(JobState.Scheduled, job!.State);
        Assert.Equal(0, job.Attempt);
    }

    [Fact]
    public async Task RequeueJob_JobNotInARequeueableState_IsANormalResult()
    {
        await using var server = await McpTestServer.StartAsync(GrantAllWrites);
        var jobId = await server.SeedJobAsync("critical"); // Scheduled, not requeueable

        var result = await server.Client.CallToolAsync("requeue_job", new { job_id = jobId.ToString() });

        Assert.False(result.IsError);
        Assert.Equal("NotRequeueable", result.StructuredContent!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PauseQueue_And_ResumeQueue_ToggleTheStoredPauseState()
    {
        await using var server = await McpTestServer.StartAsync(GrantAllWrites);

        var paused = await server.Client.CallToolAsync("pause_queue", new { queue = "critical" });
        Assert.False(paused.IsError);
        Assert.True(paused.StructuredContent!.Value.GetProperty("paused").GetBoolean());
        var settings = Assert.Single(await server.Store.ListQueueSettingsAsync(), s => s.Queue == "critical");
        Assert.True(settings.Paused);

        var resumed = await server.Client.CallToolAsync("resume_queue", new { queue = "critical" });
        Assert.False(resumed.IsError);
        Assert.False(resumed.StructuredContent!.Value.GetProperty("paused").GetBoolean());
        Assert.DoesNotContain(
            await server.Store.ListQueueSettingsAsync(), s => s.Queue == "critical" && s.Paused);
    }

    [Fact]
    public async Task SetConcurrencyLimit_SetsAndThenClearsTheCap()
    {
        await using var server = await McpTestServer.StartAsync(GrantAllWrites);

        var set = await server.Client.CallToolAsync("set_concurrency_limit", new { queue = "bulk", limit = 4 });
        Assert.False(set.IsError);
        Assert.Equal(4, set.StructuredContent!.Value.GetProperty("limit").GetInt32());
        var settings = Assert.Single(await server.Store.ListQueueSettingsAsync(), s => s.Queue == "bulk");
        Assert.Equal(4, settings.ConcurrencyLimit);

        // Omitting limit clears the cap. The serializer omits null properties, so a cleared limit
        // is either absent from the structured content or explicitly null.
        var cleared = await server.Client.CallToolAsync("set_concurrency_limit", new { queue = "bulk" });
        Assert.False(cleared.IsError);
        Assert.True(
            !cleared.StructuredContent!.Value.TryGetProperty("limit", out var clearedLimit)
            || clearedLimit.ValueKind == System.Text.Json.JsonValueKind.Null);
        Assert.DoesNotContain(
            await server.Store.ListQueueSettingsAsync(),
            s => s.Queue == "bulk" && s.ConcurrencyLimit is not null);
    }

    [Fact]
    public async Task TriggerSchedule_MintsOneInstance_VisibleThroughTheReadTool()
    {
        await using var server = await McpTestServer.StartAsync(GrantAllWrites);
        await UpsertScheduleAsync(server, "nightly-report", queue: "reports");

        var result = await server.Client.CallToolAsync("trigger_schedule", new { schedule_id = "nightly-report" });

        Assert.False(result.IsError);
        Assert.Equal("Triggered", result.StructuredContent!.Value.GetProperty("status").GetString());
        var depths = await server.Client.CallToolAsync("get_queue_depths");
        var row = Assert.Single(depths.StructuredContent!.Value.GetProperty("queueDepths").EnumerateArray());
        Assert.Equal("reports", row.GetProperty("queue").GetString());
        Assert.Equal("Scheduled", row.GetProperty("state").GetString());
        Assert.Equal(1, row.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task TriggerSchedule_UnknownSchedule_IsANormalNotFoundResult()
    {
        await using var server = await McpTestServer.StartAsync(GrantAllWrites);

        var result = await server.Client.CallToolAsync("trigger_schedule", new { schedule_id = "no-such-schedule" });

        Assert.False(result.IsError);
        Assert.Equal("ScheduleNotFound", result.StructuredContent!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task EveryWrite_LandsInTheAuditTrail_StampedWithTheDefaultActor()
    {
        await using var server = await McpTestServer.StartAsync(GrantAllWrites);
        var jobId = await server.SeedJobAsync("critical");
        await UpsertScheduleAsync(server, "nightly-report", queue: "reports");

        Assert.False((await server.Client.CallToolAsync("cancel_job", new { job_id = jobId.ToString() })).IsError);
        Assert.False((await server.Client.CallToolAsync("pause_queue", new { queue = "critical" })).IsError);
        Assert.False((await server.Client.CallToolAsync("resume_queue", new { queue = "critical" })).IsError);
        Assert.False((await server.Client.CallToolAsync("set_concurrency_limit", new { queue = "critical", limit = 2 })).IsError);
        Assert.False((await server.Client.CallToolAsync("trigger_schedule", new { schedule_id = "nightly-report" })).IsError);

        // No principal is authenticated in this host, so ResolveActor's default falls back to "mcp".
        var jobAudit = Assert.Single(await server.Store.ListAuditRecordsAsync(jobId.ToString()));
        Assert.Equal("mcp", jobAudit.Actor);
        Assert.Equal(OperatorAction.Cancel, jobAudit.Action);

        var queueAudit = await server.Store.ListAuditRecordsAsync("critical");
        Assert.Equal(
            [OperatorAction.PauseQueue, OperatorAction.ResumeQueue, OperatorAction.SetConcurrencyLimit],
            queueAudit.Select(a => a.Action));
        Assert.All(queueAudit, a => Assert.Equal("mcp", a.Actor));

        var scheduleAudit = Assert.Single(await server.Store.ListAuditRecordsAsync("nightly-report"));
        Assert.Equal("mcp", scheduleAudit.Actor);
        Assert.Equal(OperatorAction.TriggerScheduleNow, scheduleAudit.Action);
    }

    [Fact]
    public async Task RequeueAudit_CarriesTheResolvedActor()
    {
        await using var server = await McpTestServer.StartAsync(GrantAllWrites);
        var jobId = await DeadLetterAJobAsync(server, "critical");

        Assert.False((await server.Client.CallToolAsync("requeue_job", new { job_id = jobId.ToString() })).IsError);

        var record = Assert.Single(
            await server.Store.ListAuditRecordsAsync(jobId.ToString()),
            a => a.Action == OperatorAction.Requeue);
        Assert.Equal("mcp", record.Actor);
    }

    [Fact]
    public async Task CustomResolveActor_StampsWhatTheHostResolves()
    {
        // The host shapes the actor from whatever it authenticated — here a header stands in for
        // an API key the host's own middleware validated.
        await using var server = await McpTestServer.StartAsync(mcp =>
        {
            GrantAllWrites(mcp);
            mcp.ResolveActor = ctx => ctx.Request.Headers.TryGetValue("X-Api-Key-Owner", out var owner)
                ? $"api:{owner}"
                : "mcp";
        });
        var ops = server.CreateClient(http => http.DefaultRequestHeaders.Add("X-Api-Key-Owner", "alice"));

        Assert.False((await ops.CallToolAsync("pause_queue", new { queue = "critical" })).IsError);

        var record = Assert.Single(await server.Store.ListAuditRecordsAsync("critical"));
        Assert.Equal("api:alice", record.Actor);
    }

    [Fact]
    public async Task DefaultResolveActor_AnAuthenticatedPrincipalsNameWins()
    {
        // The shared harness runs no authentication, so this test builds its own host with a
        // stand-in auth middleware that sets the request's principal before the MCP mount — the
        // documented production shape. The default ResolveActor then stamps Identity.Name, not
        // the "mcp" fallback.
        var store = new InMemoryJobStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBackWave(bw => bw
            .UseStore(store)
            .UseRegistry(new JobRegistry([]))
            .AddMcp(mcp => mcp.AuthorizePauseQueue = _ => ValueTask.FromResult(true)));
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new GenericIdentity("alice@example.com"));
            await next(context);
        });
        app.UseBackWaveProMcp();
        await app.StartAsync();
        var client = new McpTestClient(app.GetTestClient());

        Assert.False((await client.CallToolAsync("pause_queue", new { queue = "critical" })).IsError);

        var record = Assert.Single(await store.ListAuditRecordsAsync("critical"));
        Assert.Equal("alice@example.com", record.Actor);
    }

    [Fact]
    public async Task InvalidJobId_IsAToolExecutionErrorWithActionableText()
    {
        await using var server = await McpTestServer.StartAsync(GrantAllWrites);

        var result = await server.Client.CallToolAsync("cancel_job", new { job_id = "not-a-guid" });

        Assert.True(result.IsError);
        Assert.Contains("job_id", result.Text);
        Assert.Contains("GUID", result.Text);
    }

    [Fact]
    public async Task InvalidConcurrencyLimit_IsAToolExecutionErrorWithActionableText()
    {
        await using var server = await McpTestServer.StartAsync(GrantAllWrites);

        var result = await server.Client.CallToolAsync("set_concurrency_limit", new { queue = "bulk", limit = 0 });

        Assert.True(result.IsError);
        Assert.Contains("at least 1", result.Text);
        Assert.Contains("pause_queue", result.Text);
        // Nothing was written: no settings row, no audit record.
        Assert.Empty(await server.Store.ListAuditRecordsAsync("bulk"));
    }

    [Fact]
    public async Task EmptyQueueName_IsAToolExecutionErrorWithActionableText()
    {
        await using var server = await McpTestServer.StartAsync(GrantAllWrites);

        var result = await server.Client.CallToolAsync("pause_queue", new { queue = " " });

        Assert.True(result.IsError);
        Assert.Contains("queue", result.Text);
        Assert.Contains("non-empty", result.Text);
    }

    /// <summary>Drives one seeded job through claim + final failure so it lands DeadLettered.</summary>
    private static async Task<Guid> DeadLetterAJobAsync(McpTestServer server, string queue)
    {
        var jobId = await server.SeedJobAsync(queue);
        var now = DateTimeOffset.UtcNow;
        var claimed = Assert.Single(await server.Store.ClaimAsync(
            new ClaimRequest("test-worker", [queue], MaxJobs: 1, LeaseDuration: TimeSpan.FromMinutes(1), Now: now)));
        Assert.Equal(jobId, claimed.JobId);
        var outcome = await server.Store.ReportOutcomeAsync(
            jobId, "test-worker", claimed.Attempt,
            new JobOutcome.Failure(NextDueTime: null, Error: "boom"), now); // null next-due = dead-letter
        Assert.Equal(OutcomeResult.Applied, outcome);
        Assert.Equal(JobState.DeadLettered, (await server.Store.GetJobAsync(jobId))!.State);
        return jobId;
    }

    /// <summary>Registers one recurring schedule directly in the store.</summary>
    private static async Task UpsertScheduleAsync(McpTestServer server, string scheduleId, string queue)
        => await server.Store.UpsertScheduleAsync(new ScheduleRecord
        {
            ScheduleId = scheduleId,
            Cron = "0 0 3 * * *",
            WireName = "test-job",
            Payload = "{}"u8.ToArray(),
            Queue = queue,
            Cursor = DateTimeOffset.UtcNow,
        });
}
