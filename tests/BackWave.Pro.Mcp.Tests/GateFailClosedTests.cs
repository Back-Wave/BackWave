using ModelContextProtocol.Protocol;

namespace BackWave.Pro.Mcp.Tests;

/// <summary>
/// Direct unit tests for the fail-closed-on-null-HttpContext branch of every gate entry point.
/// This is the security-critical path: a null context means the request did not arrive over the
/// authenticated HTTP transport, so the host's callback cannot judge it and the gate must deny —
/// even when every host callback would grant. The mounted-endpoint tests can never reach it (the
/// HTTP transport always populates the accessor), so flipping any <c>context is null ? deny</c> to
/// <c>allow</c> would be a fail-open leak that passes the whole endpoint suite. These tests prove
/// each null branch directly, so that regression is caught.
/// </summary>
// Joins the sensitive-data env collection so the all-granted options here never race a test that
// sets the process-wide BACKWAVE_MCP_DISABLE_SENSITIVE_DATA kill-switch — the null-context assertion
// holds regardless, but this keeps "even with everything granted" honest.
[Collection(SensitiveDataEnvCollection.Name)]
public sealed class GateFailClosedTests
{
    // A permissive options object: every write, the view gate, and the sensitive-data lock all
    // granting. Any denial below is therefore attributable to the null context, nothing else.
    private static BackWaveProMcpOptions AllGranted() => new()
    {
        AuthorizeView = _ => ValueTask.FromResult(true),
        AuthorizeCancel = _ => ValueTask.FromResult(true),
        AuthorizeRequeue = _ => ValueTask.FromResult(true),
        AuthorizePauseQueue = _ => ValueTask.FromResult(true),
        AuthorizeTriggerSchedule = _ => ValueTask.FromResult(true),
        AuthorizeSetConcurrencyLimit = _ => ValueTask.FromResult(true),
        AuthorizeViewSensitiveData = _ => ValueTask.FromResult(true),
        ExposeSensitiveData = true,
    };

    [Fact]
    public async Task AllowsAsync_GatedTool_NullContext_DeniesEvenWhenTheCallbackGrants()
    {
        // cancel_job is gated by AuthorizeCancel, which grants here — the only thing denying is the
        // null context. A regression flipping this branch open would leak the write tool.
        Assert.False(await McpToolGates.AllowsAsync(ToolNames.CancelJob, httpContext: null, AllGranted()));
    }

    [Fact]
    public async Task AllowsAsync_SensitiveDataTool_NullContext_Denies()
    {
        // The payload tools route through the sensitive-data gate; null context must still deny.
        Assert.False(await McpToolGates.AllowsAsync(ToolNames.GetJobPayload, httpContext: null, AllGranted()));
    }

    [Fact]
    public async Task AllowsAsync_UngatedTool_NullContext_StillPasses()
    {
        // The contrast case that proves the deny above is the gate, not a blanket null rejection: an
        // ungated read tool has no per-tool gate, so a null context passes it (the view gate upstream
        // owns whether the surface is visible at all). Without this, a test that made every null
        // context deny would look correct while actually hiding a broken gate lookup.
        Assert.True(await McpToolGates.AllowsAsync(ToolNames.SearchJobs, httpContext: null, AllGranted()));
    }

    [Fact]
    public async Task AllowsSensitiveDataAsync_NullContext_DeniesEvenWhenAllThreeLocksGrant()
    {
        // The sensitive-data triple lock's own entry point: the permission grants and exposure is on,
        // so only the null context denies. This is the branch guarding get_job_payload, get_job_output,
        // and the withheld failure detail in get_job_history.
        Assert.False(await McpToolGates.AllowsSensitiveDataAsync(httpContext: null, AllGranted()));
    }

    [Fact]
    public async Task FilterListAsync_NullContext_DropsGatedToolsButKeepsUngatedOnes()
    {
        // The tools/list path shares the AllowsAsync gate: with no context, gated tools must vanish
        // from the list while ungated reads remain. A fail-open regression would surface cancel_job.
        var result = new ListToolsResult
        {
            Tools =
            [
                new Tool { Name = ToolNames.SearchJobs },
                new Tool { Name = ToolNames.CancelJob },
                new Tool { Name = ToolNames.GetJobPayload },
            ],
        };

        var filtered = await McpToolGates.FilterListAsync(result, httpContext: null, AllGranted());

        var names = filtered.Tools.Select(t => t.Name).ToHashSet();
        Assert.Contains(ToolNames.SearchJobs, names);
        Assert.DoesNotContain(ToolNames.CancelJob, names);
        Assert.DoesNotContain(ToolNames.GetJobPayload, names);
    }

    [Fact]
    public async Task ViewAllowedAsync_NullServices_DeniesEvenWhenAuthorizeViewGrants()
    {
        // The outermost gate fronting the whole surface. Null services means no IHttpContextAccessor,
        // hence no context — it must deny even though AuthorizeView grants. Over the HTTP transport
        // services and the context are always present, so only a direct test reaches this branch.
        Assert.False(await BackWaveProMcpExtensions.ViewAllowedAsync(services: null, AllGranted()));
    }
}
