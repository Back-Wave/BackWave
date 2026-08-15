using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BackWave.Pro.Mcp.Tests;

/// <summary>
/// A minimal in-process MCP client for tests: speaks JSON-RPC over MCP streamable HTTP against a
/// mounted BackWave MCP endpoint and parses the SSE-framed responses the stateless server returns
/// (responses are <c>text/event-stream</c> even for single-shot POSTs). Reused by every MCP test —
/// list tools with <see cref="ListToolsAsync"/>, call one with <see cref="CallToolAsync"/>, or hit
/// any other method raw with <see cref="SendAsync"/>.
/// </summary>
public sealed class McpTestClient(HttpClient http, string prefix = "/backwave-mcp")
{
    private int _nextId;

    /// <summary>
    /// Sends one JSON-RPC request to the MCP endpoint and returns the parsed <c>result</c>.
    /// Throws <see cref="McpProtocolException"/> on a JSON-RPC <c>error</c> response and
    /// <see cref="HttpRequestException"/> on a non-success HTTP status.
    /// </summary>
    public async Task<JsonElement> SendAsync(string method, object? @params = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = @params ?? new Dictionary<string, object?>(),
        };

        using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        using var message = new HttpRequestMessage(HttpMethod.Post, prefix) { Content = content };
        // Streamable HTTP requires the client to accept both shapes; the stateless server answers
        // with an SSE-framed single message.
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await http.SendAsync(message);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        var json = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? ExtractFirstSseData(body)
            : body;

        var envelope = JsonSerializer.Deserialize<JsonElement>(json);
        if (envelope.TryGetProperty("error", out var error))
        {
            throw new McpProtocolException(
                error.TryGetProperty("code", out var code) ? code.GetInt32() : 0,
                error.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "");
        }

        Assert.Equal(id, envelope.GetProperty("id").GetInt32());
        return envelope.GetProperty("result");
    }

    /// <summary>Calls <c>tools/list</c> and returns the advertised tools.</summary>
    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync()
    {
        var result = await SendAsync("tools/list");
        return [.. result.GetProperty("tools").EnumerateArray().Select(tool => new McpToolInfo(
            Name: tool.GetProperty("name").GetString()!,
            Description: tool.TryGetProperty("description", out var d) ? d.GetString() : null,
            InputSchema: tool.TryGetProperty("inputSchema", out var i) ? i : null,
            OutputSchema: tool.TryGetProperty("outputSchema", out var o) ? o : null))];
    }

    /// <summary>Calls <c>tools/call</c> for <paramref name="name"/> and returns the parsed result.</summary>
    public async Task<McpToolCallResult> CallToolAsync(string name, object? arguments = null)
    {
        var result = await SendAsync("tools/call", new Dictionary<string, object?>
        {
            ["name"] = name,
            ["arguments"] = arguments ?? new Dictionary<string, object?>(),
        });

        var text = result.TryGetProperty("content", out var content)
            ? content.EnumerateArray()
                .Where(block => block.GetProperty("type").GetString() == "text")
                .Select(block => block.GetProperty("text").GetString())
                .FirstOrDefault()
            : null;

        return new McpToolCallResult(
            IsError: result.TryGetProperty("isError", out var isError) && isError.GetBoolean(),
            Text: text,
            StructuredContent: result.TryGetProperty("structuredContent", out var structured) ? structured : null,
            Raw: result);
    }

    // Minimal SSE parse: the stateless server frames the single JSON-RPC response as one
    // "event: message" whose data line(s) carry the JSON. Multiple data lines in one event are
    // joined with '\n' per the SSE spec; the first complete event wins.
    private static string ExtractFirstSseData(string body)
    {
        var data = new StringBuilder();
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }
                data.Append(line["data:".Length..].TrimStart(' '));
            }
            else if (line.Length == 0 && data.Length > 0)
            {
                break; // Blank line ends the event; we have the first one.
            }
        }

        Assert.True(data.Length > 0, $"No SSE data event found in response body: {body}");
        return data.ToString();
    }
}

/// <summary>One advertised tool from <c>tools/list</c>.</summary>
public sealed record McpToolInfo(string Name, string? Description, JsonElement? InputSchema, JsonElement? OutputSchema);

/// <summary>A parsed <c>tools/call</c> result.</summary>
/// <param name="IsError">Whether the server flagged the call as a tool-execution error.</param>
/// <param name="Text">The first text content block, when present (error text lives here).</param>
/// <param name="StructuredContent">The typed structured content, when the tool returns one.</param>
/// <param name="Raw">The whole result element, for assertions the shortcuts don't cover.</param>
public sealed record McpToolCallResult(bool IsError, string? Text, JsonElement? StructuredContent, JsonElement Raw);

/// <summary>A JSON-RPC protocol-level error returned by the MCP endpoint.</summary>
public sealed class McpProtocolException(int code, string message)
    : Exception($"JSON-RPC error {code}: {message}")
{
    /// <summary>The JSON-RPC error code.</summary>
    public int Code { get; } = code;
}
