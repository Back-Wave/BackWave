using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;

namespace BackWave.Pro.Mcp;

// The per-tool permission registry behind the generalized tools/list filtering and its call-time
// backstop (issue 0227; gate assignments from mcp-0003/mcp-0004). The view gate fronts the whole
// surface upstream in the filter pipeline; this map layers each tool's OWN gate on top of it. A
// tool with no entry here needs only the view gate — read tools registered by other issues require
// nothing from this file. A new gated tool is one dictionary entry: tool name → (the additional
// per-request check, the actionable denial text) — e.g. the sensitive-data locks (0226) slot in as
// entries whose check composes AuthorizeViewSensitiveData with the exposure switch. Fails closed
// when no HttpContext is visible, matching the view gate.
internal static class McpToolGates
{
    private sealed record Gate(
        Func<BackWaveProMcpOptions, HttpContext, ValueTask<bool>> Allows,
        string DeniedMessage);

    // The six operator writes, each behind its own default-deny options callback. Pause and resume
    // share AuthorizePauseQueue (one permission gates both directions).
    private static readonly Dictionary<string, Gate> Gates = new(StringComparer.Ordinal)
    {
        [ToolNames.CancelJob] = WriteGate(o => o.AuthorizeCancel, ToolNames.CancelJob, nameof(BackWaveProMcpOptions.AuthorizeCancel)),
        [ToolNames.RequeueJob] = WriteGate(o => o.AuthorizeRequeue, ToolNames.RequeueJob, nameof(BackWaveProMcpOptions.AuthorizeRequeue)),
        [ToolNames.PauseQueue] = WriteGate(o => o.AuthorizePauseQueue, ToolNames.PauseQueue, nameof(BackWaveProMcpOptions.AuthorizePauseQueue)),
        [ToolNames.ResumeQueue] = WriteGate(o => o.AuthorizePauseQueue, ToolNames.ResumeQueue, nameof(BackWaveProMcpOptions.AuthorizePauseQueue)),
        [ToolNames.SetConcurrencyLimit] = WriteGate(o => o.AuthorizeSetConcurrencyLimit, ToolNames.SetConcurrencyLimit, nameof(BackWaveProMcpOptions.AuthorizeSetConcurrencyLimit)),
        [ToolNames.TriggerSchedule] = WriteGate(o => o.AuthorizeTriggerSchedule, ToolNames.TriggerSchedule, nameof(BackWaveProMcpOptions.AuthorizeTriggerSchedule)),
        // cancel_workflow rides the SAME AuthorizeCancel as cancel_job (issue 0228; dashboard
        // precedent — the workflow cancel fans the per-job cancel out over the members).
        [ToolNames.CancelWorkflow] = WriteGate(o => o.AuthorizeCancel, ToolNames.CancelWorkflow, nameof(BackWaveProMcpOptions.AuthorizeCancel)),
        [ToolNames.GetJobPayload] = SensitiveDataGate(),
        [ToolNames.GetJobOutput] = SensitiveDataGate(),
    };

    // The sensitive-data triple lock (0226) fronting the two payload tools: the per-request
    // AuthorizeViewSensitiveData permission AND the host's ExposeSensitiveData flag AND the
    // absence of the BACKWAVE_MCP_DISABLE_SENSITIVE_DATA kill-switch must all agree
    // (SensitiveDataExposureEnabled composes the latter two).
    private static Gate SensitiveDataGate()
        => new(
            (options, context) => AllowsSensitiveDataAsync(context, options),
            "Permission denied: this request may not read sensitive job "
            + "content (job payloads and outputs may carry secrets or personal "
            + "data). All three locks must allow it: the host's "
            + "AuthorizeViewSensitiveData callback must permit this request, "
            + "BackWaveProMcpOptions.ExposeSensitiveData must be true, and the "
            + "BACKWAVE_MCP_DISABLE_SENSITIVE_DATA environment variable must not "
            + "be set to a truthy value on the host.");

    private static Gate WriteGate(
        Func<BackWaveProMcpOptions, Func<HttpContext, ValueTask<bool>>> callback, string toolName, string optionName)
        => new(
            (options, context) => callback(options)(context),
            $"Permission denied: this request may not call '{toolName}'. The host's {optionName} "
            + "callback denied it (write actions are denied by default); authenticate the request to "
            + $"satisfy the host's policy, or have the host grant it in BackWaveProMcpOptions.{optionName}.");

    /// <summary>
    /// Removes from <paramref name="result"/> every tool whose per-tool gate denies the current
    /// request; ungated tools always stay (the view gate already fronted the request upstream).
    /// </summary>
    public static async ValueTask<ListToolsResult> FilterListAsync(
        ListToolsResult result, HttpContext? httpContext, BackWaveProMcpOptions options)
    {
        var allowed = new List<Tool>(result.Tools.Count);
        foreach (var tool in result.Tools)
        {
            if (await AllowsAsync(tool.Name, httpContext, options).ConfigureAwait(false))
            {
                allowed.Add(tool);
            }
        }

        result.Tools = allowed;
        return result;
    }

    /// <summary>
    /// Whether the current request passes <paramref name="toolName"/>'s own gate. Ungated (or
    /// unknown) tool names pass — the view gate and the SDK's unknown-tool handling own those —
    /// while a gated tool with no visible <see cref="HttpContext"/> is denied (fail closed).
    /// </summary>
    public static ValueTask<bool> AllowsAsync(
        string? toolName, HttpContext? httpContext, BackWaveProMcpOptions options)
    {
        if (toolName is null || !Gates.TryGetValue(toolName, out var gate))
        {
            return ValueTask.FromResult(true);
        }

        return httpContext is null ? ValueTask.FromResult(false) : gate.Allows(options, httpContext);
    }

    /// <summary>
    /// Whether the given request may read sensitive job content — exception detail, job payloads,
    /// and job outputs, any of which can carry secrets, connection strings, file paths, or personal
    /// data. All three locks must agree: the host's per-request sensitive-data authorization callback
    /// must permit the request, the host's sensitive-data exposure flag must be on, and the host's
    /// sensitive-data disable environment variable must be unset. A request with no visible
    /// <paramref name="httpContext"/> is denied (fail closed), so a missing context never leaks.
    /// </summary>
    /// <param name="httpContext">The request being judged; null when no request context is available.</param>
    /// <param name="options">The host's MCP options carrying the three sensitive-data locks.</param>
    /// <returns>
    /// True only when all three locks allow the request; false otherwise, including whenever
    /// <paramref name="httpContext"/> is null.
    /// </returns>
    public static ValueTask<bool> AllowsSensitiveDataAsync(HttpContext? httpContext, BackWaveProMcpOptions options)
    {
        if (httpContext is null || !options.SensitiveDataExposureEnabled)
        {
            return ValueTask.FromResult(false);
        }

        return options.AuthorizeViewSensitiveData(httpContext);
    }

    /// <summary>The call-time backstop's tool-execution error for a request the tool's gate denied.</summary>
    public static CallToolResult DeniedResult(string? toolName) => new()
    {
        IsError = true,
        Content =
        [
            new TextContentBlock
            {
                Text = toolName is not null && Gates.TryGetValue(toolName, out var gate)
                    ? gate.DeniedMessage
                    : "Permission denied: this request may not call the tool.",
            },
        ],
    };
}
