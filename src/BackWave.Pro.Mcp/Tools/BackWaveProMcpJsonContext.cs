using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace BackWave.Pro.Mcp.Tools;

// Source-generated JSON metadata for every tool result DTO, so tool-schema generation and
// (de)serialization resolve their JsonTypeInfo without the reflection-based System.Text.Json
// fallback that Native AOT strips out. Without this, a trimmed/AOT host throws
// NotSupportedException ("JsonTypeInfo metadata for type ... was not provided") the first time it
// builds a tool. The result records are internal to this assembly, so the context lives here to
// see them; tool arguments are all primitives and need no registration. Web defaults (camelCase)
// match the MCP SDK's own default options, so the wire format is identical to the reflection path.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AuditRecordsResult))]
[JsonSerializable(typeof(CancelJobResult))]
[JsonSerializable(typeof(CancelWorkflowToolResult))]
[JsonSerializable(typeof(GetJobDependenciesResult))]
[JsonSerializable(typeof(GetJobHistoryResult))]
[JsonSerializable(typeof(GetJobResult))]
[JsonSerializable(typeof(GetWorkflowResult))]
[JsonSerializable(typeof(ListWorkflowsResult))]
[JsonSerializable(typeof(ObserverDeadLettersResult))]
[JsonSerializable(typeof(ObserverLagResult))]
[JsonSerializable(typeof(QueueDepthsResult))]
[JsonSerializable(typeof(QueuePauseStateResult))]
[JsonSerializable(typeof(QueueSettingsResult))]
[JsonSerializable(typeof(RenderedContentResult))]
[JsonSerializable(typeof(RequeueJobResult))]
[JsonSerializable(typeof(SchedulesResult))]
[JsonSerializable(typeof(SearchJobsResult))]
[JsonSerializable(typeof(SetConcurrencyLimitResult))]
[JsonSerializable(typeof(TagFacetResult))]
[JsonSerializable(typeof(TriggerScheduleToolResult))]
[JsonSerializable(typeof(WireNamesResult))]
internal sealed partial class BackWaveProMcpJsonContext : JsonSerializerContext;

internal static class BackWaveProMcpJson
{
    // The MCP SDK defaults (protocol types, primitives, AIContent - all already source-gen backed)
    // with our tool-DTO resolver inserted first, so tool results resolve via source generation and
    // everything else falls through to the SDK's own resolvers.
    internal static JsonSerializerOptions ToolOptions { get; } = CreateToolOptions();

    private static JsonSerializerOptions CreateToolOptions()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        options.TypeInfoResolverChain.Insert(0, BackWaveProMcpJsonContext.Default);
        options.MakeReadOnly();
        return options;
    }
}
