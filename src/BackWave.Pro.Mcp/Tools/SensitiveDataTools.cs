using System.ComponentModel;
using BackWave.Monitor;
using ModelContextProtocol.Server;

namespace BackWave.Pro.Mcp.Tools;

// The two sensitive-data read tools (mcp-0003 inventory): rendered payload/output views, never raw
// bytes (base64 is token-hostile). Both sit behind the sensitive-data triple lock (mcp-0004) —
// enforced by the filter pipeline in AddMcp, so the tools themselves stay plain reads. Internal:
// the tool surface is wire-level (MCP), never a C# API — consumers see tool names and schemas, not
// these types. Registered explicitly via WithTools<SensitiveDataTools>() in AddMcp; never assembly
// scanning.
[McpServerToolType]
internal sealed class SensitiveDataTools(BackWaveMonitor monitor)
{
    // 16 KiB of UTF-8-encoded rendered text; larger content is cut at the cap with Truncated set,
    // and the tool descriptions point readers at the dashboard for the full content. No offset
    // paging in v1 (mcp-0003).
    internal const int InlineTextCapBytes = 16 * 1024;

    [McpServerTool(
        Name = ToolNames.GetJobPayload,
        Title = "Get job payload",
        UseStructuredContent = true,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "One job's payload rendered for reading. The payload is opaque bytes serialized by the " +
        "host application: valid UTF-8 decodes to text (encoding \"utf8\"); anything else renders " +
        "as an uppercase hex dump (encoding \"hex\"). The inline text is capped at 16 KiB - when " +
        "cut, truncated is true while byteCount still reports the full raw length; read content " +
        "larger than the cap on the BackWave dashboard's job detail page instead. Returns " +
        "found=false when no job with the id exists. Payloads may carry secrets or personal data, " +
        "so this tool is available only when the host grants sensitive-data access.")]
    public async Task<RenderedContentResult> GetJobPayloadAsync(
        [Description("The id of the job whose payload to read.")] Guid job_id,
        CancellationToken cancellationToken)
        => RenderedContentResult.From(
            await monitor.GetJobPayloadAsync(job_id, cancellationToken).ConfigureAwait(false));

    [McpServerTool(
        Name = ToolNames.GetJobOutput,
        Title = "Get job output",
        UseStructuredContent = true,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "One job's output - the opaque result blob its handler emitted on success - rendered for " +
        "reading: valid UTF-8 decodes to text (encoding \"utf8\"); anything else renders as an " +
        "uppercase hex dump (encoding \"hex\"). The inline text is capped at 16 KiB - when cut, " +
        "truncated is true while byteCount still reports the full raw length; read content larger " +
        "than the cap on the BackWave dashboard's job detail page instead. Returns found=false " +
        "when the job recorded no output or no job with the id exists. Output may carry secrets " +
        "or personal data, so this tool is available only when the host grants sensitive-data " +
        "access.")]
    public async Task<RenderedContentResult> GetJobOutputAsync(
        [Description("The id of the job whose output to read.")] Guid job_id,
        CancellationToken cancellationToken)
        => RenderedContentResult.From(
            await monitor.GetJobOutputViewAsync(job_id, cancellationToken).ConfigureAwait(false));
}

/// <summary>The structured result of <c>get_job_payload</c> and <c>get_job_output</c>.</summary>
internal sealed record RenderedContentResult
{
    /// <summary>Whether the requested content exists.</summary>
    [Description("Whether the requested content exists: false when no job with the id exists (or, " +
        "for get_job_output, when the job recorded no output). All other fields are meaningful " +
        "only when true.")]
    public required bool Found { get; init; }

    /// <summary>The rendered content, cut at the inline cap when over it.</summary>
    [Description("The content rendered for reading: decoded UTF-8 text when the raw bytes are " +
        "valid text, otherwise an uppercase hex dump. Cut at the 16 KiB inline cap (see " +
        "truncated). Null when found is false.")]
    public string? Text { get; init; }

    /// <summary>The full raw content length in bytes, before rendering or truncation.</summary>
    [Description("The full raw content length in bytes, before rendering or truncation. Null when " +
        "found is false.")]
    public int? ByteCount { get; init; }

    /// <summary>Which rendering produced <see cref="Text"/>: <c>"utf8"</c> or <c>"hex"</c>.</summary>
    [Description("Which rendering produced text: \"utf8\" (the bytes decoded cleanly as UTF-8) or " +
        "\"hex\" (an uppercase hex dump of non-text bytes). Null when found is false.")]
    public string? Encoding { get; init; }

    /// <summary>Whether <see cref="Text"/> was cut at the 16 KiB inline cap.</summary>
    [Description("True when text was cut at the 16 KiB inline cap; the full content is available " +
        "on the BackWave dashboard's job detail page.")]
    public bool Truncated { get; init; }

    /// <summary>Projects a Monitor-rendered view into the tool result, applying the inline cap.</summary>
    internal static RenderedContentResult From(JobPayloadView? view)
    {
        if (view is null)
        {
            return new RenderedContentResult { Found = false };
        }

        var (text, truncated) = CapInline(view.Text);
        return new RenderedContentResult
        {
            Found = true,
            Text = text,
            ByteCount = view.ByteCount,
            Encoding = view.Encoding == PayloadEncoding.Utf8 ? "utf8" : "hex",
            Truncated = truncated,
        };
    }

    // Cap the rendered text at 16 KiB of UTF-8. Encoder.Convert with flush:false consumes only
    // whole chars that fit; the extra guard keeps a surrogate pair from being split at the cut.
    private static (string Text, bool Truncated) CapInline(string text)
    {
        var utf8 = System.Text.Encoding.UTF8;
        if (utf8.GetByteCount(text) <= SensitiveDataTools.InlineTextCapBytes)
        {
            return (text, false);
        }

        var buffer = new byte[SensitiveDataTools.InlineTextCapBytes];
        utf8.GetEncoder().Convert(text.AsSpan(), buffer, flush: false, out var charsUsed, out _, out _);
        if (charsUsed > 0 && char.IsHighSurrogate(text[charsUsed - 1]))
        {
            charsUsed--;
        }
        return (text[..charsUsed], true);
    }
}
