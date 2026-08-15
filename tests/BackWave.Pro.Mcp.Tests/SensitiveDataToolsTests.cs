using System.Text.Json;
using BackWave.Storage;

namespace BackWave.Pro.Mcp.Tests;

/// <summary>
/// The sensitive-data tools (issue 0226): <c>get_job_payload</c> and <c>get_job_output</c> render
/// content behind the triple lock — AuthorizeViewSensitiveData AND ExposeSensitiveData AND no env
/// kill-switch. Rendering mirrors the dashboard (UTF-8 with hex fallback), inline text is capped at
/// 16 KiB with a truncation flag, and each lock independently hides the tools from tools/list and
/// errors direct calls. The env-var lock lives in <see cref="SensitiveDataEnvKillSwitchTests"/>
/// (its own collection, since the variable is process-wide).
/// </summary>
[Collection(SensitiveDataEnvCollection.Name)] // these tests need the process-wide kill-switch var unset
public sealed class SensitiveDataToolsTests
{
    private static void GrantSensitive(BackWaveProMcpOptions mcp)
        => mcp.AuthorizeViewSensitiveData = _ => ValueTask.FromResult(true);

    private static async Task<Guid> SeedJobWithPayloadAsync(McpTestServer server, byte[] payload)
    {
        var id = Guid.NewGuid();
        var result = await server.Store.EnqueueAsync(
            new NewJob(id, "test-job", payload, "critical", DateTimeOffset.UtcNow),
            now: DateTimeOffset.UtcNow);
        Assert.Equal(EnqueueResult.Ok, result);
        return id;
    }

    [Fact]
    public async Task TripleLockOpen_ListsBothSensitiveTools_WithOutputSchemas()
    {
        await using var server = await McpTestServer.StartAsync(GrantSensitive);

        var tools = await server.Client.ListToolsAsync();

        foreach (var name in new[] { "get_job_payload", "get_job_output" })
        {
            var tool = Assert.Single(tools, t => t.Name == name);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            // The descriptions point large content at the dashboard (the 16 KiB inline cap).
            Assert.Contains("dashboard", tool.Description, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(tool.OutputSchema);
            var properties = tool.OutputSchema.Value.GetProperty("properties");
            foreach (var field in new[] { "found", "text", "byteCount", "encoding", "truncated" })
            {
                Assert.True(properties.TryGetProperty(field, out _), $"{name} schema lacks '{field}'");
            }
        }
    }

    [Fact]
    public async Task GetJobPayload_RendersUtf8TextInline()
    {
        await using var server = await McpTestServer.StartAsync(GrantSensitive);
        var jobId = await SeedJobWithPayloadAsync(server, """{"orderId":42}"""u8.ToArray());

        var result = await server.Client.CallToolAsync(
            "get_job_payload", new Dictionary<string, object?> { ["job_id"] =jobId });

        Assert.False(result.IsError, result.Text);
        var content = result.StructuredContent!.Value;
        Assert.True(content.GetProperty("found").GetBoolean());
        Assert.Equal("""{"orderId":42}""", content.GetProperty("text").GetString());
        Assert.Equal(14, content.GetProperty("byteCount").GetInt32());
        Assert.Equal("utf8", content.GetProperty("encoding").GetString());
        Assert.False(content.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task GetJobPayload_NonUtf8Bytes_FallBackToHexDump()
    {
        await using var server = await McpTestServer.StartAsync(GrantSensitive);
        var jobId = await SeedJobWithPayloadAsync(server, [0xC3, 0x28, 0xFF]);

        var result = await server.Client.CallToolAsync(
            "get_job_payload", new Dictionary<string, object?> { ["job_id"] =jobId });

        Assert.False(result.IsError);
        var content = result.StructuredContent!.Value;
        Assert.True(content.GetProperty("found").GetBoolean());
        Assert.Equal("C328FF", content.GetProperty("text").GetString());
        Assert.Equal(3, content.GetProperty("byteCount").GetInt32());
        Assert.Equal("hex", content.GetProperty("encoding").GetString());
        Assert.False(content.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task GetJobPayload_Over16KiB_IsCutAtTheCap_WithTruncatedFlag()
    {
        await using var server = await McpTestServer.StartAsync(GrantSensitive);
        // 20 000 ASCII bytes: 1 byte per char, so the cut lands exactly at the 16 KiB cap.
        var payload = new byte[20_000];
        Array.Fill(payload, (byte)'a');
        var jobId = await SeedJobWithPayloadAsync(server, payload);

        var result = await server.Client.CallToolAsync(
            "get_job_payload", new Dictionary<string, object?> { ["job_id"] =jobId });

        Assert.False(result.IsError);
        var content = result.StructuredContent!.Value;
        Assert.True(content.GetProperty("found").GetBoolean());
        Assert.Equal(16 * 1024, content.GetProperty("text").GetString()!.Length);
        Assert.True(content.GetProperty("truncated").GetBoolean());
        // byteCount still reports the full raw length, not the truncated rendering.
        Assert.Equal(20_000, content.GetProperty("byteCount").GetInt32());
    }

    [Fact]
    public async Task GetJobPayload_UnknownJob_IsFoundFalse_NotAnError()
    {
        await using var server = await McpTestServer.StartAsync(GrantSensitive);

        var result = await server.Client.CallToolAsync(
            "get_job_payload", new Dictionary<string, object?> { ["job_id"] =Guid.NewGuid() });

        // Not-found is an answer, not a fault (mcp-0003 conventions).
        Assert.False(result.IsError);
        Assert.False(result.StructuredContent!.Value.GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task GetJobOutput_RendersTheRecordedOutput()
    {
        await using var server = await McpTestServer.StartAsync(GrantSensitive);
        var jobId = await server.SeedJobAsync("critical");
        // Run the job the way a worker would: claim, then report success with an output blob.
        var claimed = Assert.Single(await server.Store.ClaimAsync(
            new ClaimRequest("w1", ["critical"], MaxJobs: 10, TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow)));
        Assert.Equal(OutcomeResult.Applied, await server.Store.ReportOutcomeAsync(
            jobId, "w1", claimed.Attempt, new JobOutcome.Success(), DateTimeOffset.UtcNow,
            output: "report-ready"u8.ToArray()));

        var result = await server.Client.CallToolAsync(
            "get_job_output", new Dictionary<string, object?> { ["job_id"] =jobId });

        Assert.False(result.IsError);
        var content = result.StructuredContent!.Value;
        Assert.True(content.GetProperty("found").GetBoolean());
        Assert.Equal("report-ready", content.GetProperty("text").GetString());
        Assert.Equal("utf8", content.GetProperty("encoding").GetString());
        Assert.False(content.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task GetJobOutput_JobWithoutOutput_IsFoundFalse()
    {
        await using var server = await McpTestServer.StartAsync(GrantSensitive);
        var jobId = await server.SeedJobAsync("critical");

        var result = await server.Client.CallToolAsync(
            "get_job_output", new Dictionary<string, object?> { ["job_id"] =jobId });

        Assert.False(result.IsError);
        Assert.False(result.StructuredContent!.Value.GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task PermissionLock_DefaultDeny_HidesBothTools_AndErrorsDirectCalls()
    {
        // Lock 1: AuthorizeViewSensitiveData defaults to deny; the other two locks are open.
        await using var server = await McpTestServer.StartAsync();
        var jobId = await server.SeedJobAsync("critical");

        var tools = await server.Client.ListToolsAsync();
        Assert.DoesNotContain(tools, t => t.Name == "get_job_payload");
        Assert.DoesNotContain(tools, t => t.Name == "get_job_output");
        // Only the sensitive pair is hidden — the plain view-gated surface stays visible.
        Assert.Contains(tools, t => t.Name == "get_queue_depths");

        foreach (var name in new[] { "get_job_payload", "get_job_output" })
        {
            var call = await server.Client.CallToolAsync(
                name, new Dictionary<string, object?> { ["job_id"] =jobId });
            Assert.True(call.IsError);
            Assert.Contains("Permission denied", call.Text);
            Assert.Contains("AuthorizeViewSensitiveData", call.Text);
        }
    }

    [Fact]
    public async Task ExposureLock_ExposeSensitiveDataFalse_Blocks_EvenWhenPermissionGranted()
    {
        // Lock 2: the host flag alone forces the tools off, whoever holds the permission.
        await using var server = await McpTestServer.StartAsync(mcp =>
        {
            GrantSensitive(mcp);
            mcp.ExposeSensitiveData = false;
        });
        var jobId = await server.SeedJobAsync("critical");

        var tools = await server.Client.ListToolsAsync();
        Assert.DoesNotContain(tools, t => t.Name == "get_job_payload");
        Assert.DoesNotContain(tools, t => t.Name == "get_job_output");

        var call = await server.Client.CallToolAsync(
            "get_job_payload", new Dictionary<string, object?> { ["job_id"] =jobId });
        Assert.True(call.IsError);
        Assert.Contains("Permission denied", call.Text);
    }

    // Exception detail routinely embeds this exfiltration class — a connection string with a
    // password. get_job_history's failureDetail must ride the SAME triple lock as the payload tools.
    private const string SecretFailureDetail =
        "System.Data.SqlException: Login failed. "
        + "ConnectionString=Server=db.internal;User=svc;Password=hunter2; at Orders.Charge()";

    // Enqueue → claim → report a dead-lettering Failure carrying SecretFailureDetail, so the store
    // holds one transition with real captured detail (under the default TransitionsAndFailureDetail
    // policy). Transitions: Scheduled(0), Leased(1), DeadLettered(2, detail-bearing).
    private static async Task<Guid> SeedFailedJobWithDetailAsync(McpTestServer server)
    {
        var jobId = await server.SeedJobAsync("critical");
        var claimed = Assert.Single(await server.Store.ClaimAsync(
            new ClaimRequest("w1", ["critical"], MaxJobs: 10, TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow)));
        Assert.Equal(OutcomeResult.Applied, await server.Store.ReportOutcomeAsync(
            jobId, "w1", claimed.Attempt, new JobOutcome.Failure(NextDueTime: null, Error: "dead-letter"),
            DateTimeOffset.UtcNow, failureDetail: SecretFailureDetail));
        return jobId;
    }

    [Fact]
    public async Task GetJobHistory_SensitiveAllowed_ReturnsRealFailureDetail_NoWithholdNote()
    {
        // Triple lock open: the failing transition surfaces its captured detail verbatim, and the
        // full-log policy leaves historyNote null (nothing withheld).
        await using var server = await McpTestServer.StartAsync(GrantSensitive);
        var jobId = await SeedFailedJobWithDetailAsync(server);

        var result = await server.Client.CallToolAsync(
            "get_job_history", new Dictionary<string, object?> { ["job_id"] = jobId });

        Assert.False(result.IsError, result.Text);
        var structured = result.StructuredContent!.Value;
        Assert.Equal("TransitionsAndFailureDetail", structured.GetProperty("historyPolicy").GetString());
        Assert.True(!structured.TryGetProperty("historyNote", out var note) || note.ValueKind == JsonValueKind.Null);

        var failing = Assert.Single(
            structured.GetProperty("transitions").EnumerateArray(),
            t => t.TryGetProperty("failureDetail", out var d) && d.ValueKind == JsonValueKind.String);
        Assert.Equal("DeadLettered", failing.GetProperty("state").GetString());
        Assert.Contains("hunter2", failing.GetProperty("failureDetail").GetString());
    }

    [Fact]
    public async Task GetJobHistory_PermissionDenied_WithholdsFailureDetail_ButKeepsTheTransition()
    {
        // Lock 1 default-deny: the detail is nulled and no secret leaks, yet the failing transition
        // itself (state, ordinal, timestamp, attempt) is intact, and historyNote explains the gate.
        await using var server = await McpTestServer.StartAsync(); // AuthorizeViewSensitiveData denies
        var jobId = await SeedFailedJobWithDetailAsync(server);

        var result = await server.Client.CallToolAsync(
            "get_job_history", new Dictionary<string, object?> { ["job_id"] = jobId });

        Assert.False(result.IsError, result.Text);
        var structured = result.StructuredContent!.Value;

        var failing = Assert.Single(
            structured.GetProperty("transitions").EnumerateArray(),
            t => t.GetProperty("state").GetString() == "DeadLettered");
        // The non-sensitive facts of the failing transition survive the strip.
        Assert.Equal(2, failing.GetProperty("ordinal").GetInt64());
        Assert.Equal(1, failing.GetProperty("attempt").GetInt32());
        Assert.True(failing.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String);
        // Only the detail is withheld — and no secret leaks anywhere in the response.
        Assert.True(!failing.TryGetProperty("failureDetail", out var d) || d.ValueKind == JsonValueKind.Null);
        Assert.DoesNotContain("hunter2", result.Text);

        // The note tells the client the detail exists but is gated (not "no failure").
        var note = structured.GetProperty("historyNote").GetString();
        Assert.Contains("withheld", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AuthorizeViewSensitiveData", note);
    }

    [Fact]
    public async Task GetJobHistory_ExposeSensitiveDataFalse_WithholdsFailureDetail_EvenWhenPermitted()
    {
        // Lock 2: the host flag forces the detail off whoever holds the permission.
        await using var server = await McpTestServer.StartAsync(mcp =>
        {
            GrantSensitive(mcp);
            mcp.ExposeSensitiveData = false;
        });
        var jobId = await SeedFailedJobWithDetailAsync(server);

        var result = await server.Client.CallToolAsync(
            "get_job_history", new Dictionary<string, object?> { ["job_id"] = jobId });

        Assert.False(result.IsError, result.Text);
        var structured = result.StructuredContent!.Value;
        var failing = Assert.Single(
            structured.GetProperty("transitions").EnumerateArray(),
            t => t.GetProperty("state").GetString() == "DeadLettered");
        Assert.True(!failing.TryGetProperty("failureDetail", out var d) || d.ValueKind == JsonValueKind.Null);
        Assert.DoesNotContain("hunter2", result.Text);
        Assert.Contains("withheld", structured.GetProperty("historyNote").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PermissionLock_IsPerRequest_OneHostServesBothAnswers()
    {
        // The permission is judged per request off the live HttpContext: the same host hides the
        // sensitive pair from one caller and serves it to another.
        await using var server = await McpTestServer.StartAsync(mcp =>
            mcp.AuthorizeViewSensitiveData = ctx => ValueTask.FromResult(ctx.Request.Headers.ContainsKey("X-Sensitive")));
        var jobId = await server.SeedJobAsync("critical");

        Assert.DoesNotContain(await server.Client.ListToolsAsync(), t => t.Name == "get_job_payload");

        var trusted = server.CreateClient(http => http.DefaultRequestHeaders.Add("X-Sensitive", "1"));
        Assert.Contains(await trusted.ListToolsAsync(), t => t.Name == "get_job_payload");
        var call = await trusted.CallToolAsync(
            "get_job_payload", new Dictionary<string, object?> { ["job_id"] =jobId });
        Assert.False(call.IsError);
        Assert.True(call.StructuredContent!.Value.GetProperty("found").GetBoolean());
    }
}
