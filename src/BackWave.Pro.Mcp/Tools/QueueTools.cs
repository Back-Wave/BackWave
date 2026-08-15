using System.ComponentModel;
using BackWave.Monitor;
using ModelContextProtocol.Server;

namespace BackWave.Pro.Mcp.Tools;

// The queue-scoped read tools (mcp-0003 inventory). Internal: the tool surface is wire-level (MCP),
// never a C# API — consumers see tool names and schemas, not these types. Registered explicitly via
// WithTools<QueueTools>() in AddMcp; never assembly scanning.
[McpServerToolType]
internal sealed class QueueTools(BackWaveMonitor monitor)
{
    [McpServerTool(
        Name = ToolNames.GetQueueDepths,
        Title = "Get queue depths",
        UseStructuredContent = true,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Job counts grouped by queue and state - backlog depths, in-flight counts, and failure " +
        "counts in one read. Use it for at-a-glance queue health. Returns one row per " +
        "(queue, state) pair that currently has jobs; a queue/state pair with no jobs has no row.")]
    public async Task<QueueDepthsResult> GetQueueDepthsAsync(CancellationToken cancellationToken)
    {
        var depths = await monitor.GetQueueDepthsAsync(cancellationToken).ConfigureAwait(false);
        return new QueueDepthsResult
        {
            QueueDepths = [.. depths.Select(d => new QueueDepthRow(d.Queue, d.State.ToString(), d.Count))],
        };
    }
}

/// <summary>The structured result of <c>get_queue_depths</c>.</summary>
internal sealed record QueueDepthsResult
{
    /// <summary>One row per (queue, state) pair that currently has jobs.</summary>
    [Description("One row per (queue, state) pair that currently has jobs.")]
    public required IReadOnlyList<QueueDepthRow> QueueDepths { get; init; }
}

/// <summary>One (queue, state) count.</summary>
/// <param name="Queue">The queue name.</param>
/// <param name="State">The job state counted.</param>
/// <param name="Count">How many jobs on the queue are in the state.</param>
internal sealed record QueueDepthRow(
    [property: Description("The queue name.")]
    string Queue,
    [property: Description("The job state counted, e.g. Scheduled, Executing, Succeeded, DeadLettered, Quarantined.")]
    string State,
    [property: Description("How many jobs on this queue are currently in this state.")]
    int Count);
