using System.ComponentModel;
using BackWave.Operations;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BackWave.Pro.Mcp.Tools;

// The six operator write tools (mcp-0003 inventory), wrapping BackWaveOperator 1:1 — each behind
// its own default-deny gate in McpToolGates (0227), enforced by the filter pipeline before the
// tool runs. Internal: the tool surface is wire-level (MCP), never a C# API. Every write resolves
// the acting identity through options.ResolveActor (default: authenticated user name, else "mcp")
// and hands it to the Operator, which records it in the append-only audit log. Error conventions
// (mcp-0003): not-found is a normal structured result (an answer, not a fault); invalid input is a
// tool-execution error via McpException, whose message the SDK surfaces to the client.
[McpServerToolType]
internal sealed class WriteTools(
    BackWaveOperator @operator,
    BackWaveProMcpOptions options,
    IHttpContextAccessor httpContextAccessor)
{
    [McpServerTool(
        Name = ToolNames.CancelJob,
        Title = "Cancel a job",
        UseStructuredContent = true,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Cancels a job. A job that has not started yet is cancelled immediately; a running job is " +
        "asked to stop cooperatively and cancels when its handler next checks. A job that is absent " +
        "or already terminal reports NotCancellable and nothing changes. The action is recorded in " +
        "the operator audit trail.")]
    public async Task<CancelJobResult> CancelJobAsync(
        [Description("The id of the job to cancel, as a GUID string.")] string job_id,
        CancellationToken cancellationToken)
    {
        var result = await @operator
            .CancelJobAsync(ParseJobId(job_id), ResolveActor(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new CancelJobResult(result.ToString());
    }

    [McpServerTool(
        Name = ToolNames.RequeueJob,
        Title = "Requeue a failed job",
        UseStructuredContent = true,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Requeues a dead-lettered or quarantined job: it returns to Scheduled with its attempt count " +
        "reset to zero and runs again. A job that is absent or in any other state reports " +
        "NotRequeueable and nothing changes. The action is recorded in the operator audit trail.")]
    public async Task<RequeueJobResult> RequeueJobAsync(
        [Description("The id of the job to requeue, as a GUID string.")] string job_id,
        CancellationToken cancellationToken)
    {
        var result = await @operator
            .RequeueAsync(ParseJobId(job_id), ResolveActor(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new RequeueJobResult(result.ToString());
    }

    [McpServerTool(
        Name = ToolNames.PauseQueue,
        Title = "Pause a queue",
        UseStructuredContent = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Pauses a queue across the whole cluster: no worker claims work from it until it is resumed " +
        "with resume_queue. Jobs already running are unaffected. Pausing an already-paused queue " +
        "changes nothing. The action is recorded in the operator audit trail.")]
    public async Task<QueuePauseStateResult> PauseQueueAsync(
        [Description("The name of the queue to pause.")] string queue,
        CancellationToken cancellationToken)
    {
        RequireQueue(queue);
        await @operator.PauseQueueAsync(queue, ResolveActor(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new QueuePauseStateResult(queue, Paused: true);
    }

    [McpServerTool(
        Name = ToolNames.ResumeQueue,
        Title = "Resume a paused queue",
        UseStructuredContent = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Resumes a paused queue, so workers may claim work from it again. Resuming a queue that is " +
        "not paused changes nothing. The action is recorded in the operator audit trail.")]
    public async Task<QueuePauseStateResult> ResumeQueueAsync(
        [Description("The name of the queue to resume.")] string queue,
        CancellationToken cancellationToken)
    {
        RequireQueue(queue);
        await @operator.ResumeQueueAsync(queue, ResolveActor(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new QueuePauseStateResult(queue, Paused: false);
    }

    [McpServerTool(
        Name = ToolNames.SetConcurrencyLimit,
        Title = "Set a queue's concurrency limit",
        UseStructuredContent = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Sets or clears a queue's cluster-wide concurrency limit: at most `limit` of the queue's " +
        "jobs run at once across every worker node. Omit `limit` to remove the cap. Takes effect on " +
        "the next claim; jobs already running are unaffected. To stop a queue entirely, use " +
        "pause_queue instead. The action is recorded in the operator audit trail.")]
    public async Task<SetConcurrencyLimitResult> SetConcurrencyLimitAsync(
        [Description("The name of the queue whose limit to set.")] string queue,
        CancellationToken cancellationToken,
        [Description("The maximum number of concurrently-running jobs allowed in the queue (at least 1). Omit to remove the limit.")]
        int? limit = null)
    {
        RequireQueue(queue);
        if (limit is < 1)
        {
            throw new McpException(
                $"Invalid limit {limit}: the concurrency ceiling must be at least 1. Omit limit to "
                + "remove the cap; to stop a queue entirely, use pause_queue.");
        }

        await @operator
            .SetConcurrencyLimitAsync(queue, limit, ResolveActor(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new SetConcurrencyLimitResult(queue, limit);
    }

    [McpServerTool(
        Name = ToolNames.TriggerSchedule,
        Title = "Trigger a recurring schedule now",
        UseStructuredContent = true,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Mints one instance of a recurring schedule to run right now, without moving the schedule's " +
        "cursor or disturbing its future ticks — a one-off run on demand. An unknown schedule id " +
        "reports ScheduleNotFound and nothing changes. The action is recorded in the operator audit " +
        "trail.")]
    public async Task<TriggerScheduleToolResult> TriggerScheduleAsync(
        [Description("The id of the recurring schedule to trigger.")] string schedule_id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(schedule_id))
        {
            throw new McpException(
                "Invalid schedule_id: pass a non-empty schedule id. Schedule ids appear in "
                + "list_schedules results.");
        }

        var result = await @operator
            .TriggerScheduleNowAsync(schedule_id, ResolveActor(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new TriggerScheduleToolResult(result.ToString());
    }

    // Every write stamps the audit record with the host-resolved actor. The HTTP context is always
    // present over the mounted endpoint (each stateless call is its own POST); its absence means the
    // call arrived some other way, and we fail loudly rather than stamp a made-up identity.
    private string ResolveActor()
    {
        var httpContext = httpContextAccessor.HttpContext
            ?? throw new McpException(
                "No HTTP request context is available, so the acting operator's identity cannot be "
                + "resolved; call this tool through the mounted BackWave MCP endpoint.");
        return options.ResolveActor(httpContext);
    }

    private static Guid ParseJobId(string jobId)
        => Guid.TryParse(jobId, out var parsed)
            ? parsed
            : throw new McpException(
                $"Invalid job_id '{jobId}': pass the job's id as a GUID string, e.g. "
                + "\"7f4d21f0-5b0a-4f3e-9c2d-8a6b1e0c9d42\". Job ids appear in search_jobs and "
                + "get_job results.");

    private static void RequireQueue(string queue)
    {
        if (string.IsNullOrWhiteSpace(queue))
        {
            throw new McpException(
                "Invalid queue: pass a non-empty queue name, e.g. \"default\". Queue names appear "
                + "in get_queue_depths results.");
        }
    }
}

/// <summary>The structured result of <c>cancel_job</c>.</summary>
/// <param name="Status">How the cancel landed.</param>
internal sealed record CancelJobResult(
    [property: Description(
        "How the cancel landed: CancelledImmediately (the job had not started and is now terminal " +
        "Cancelled), CancellationRequested (the running job was asked to stop cooperatively), or " +
        "NotCancellable (the job was absent or already terminal; nothing changed).")]
    string Status);

/// <summary>The structured result of <c>requeue_job</c>.</summary>
/// <param name="Status">How the requeue landed.</param>
internal sealed record RequeueJobResult(
    [property: Description(
        "How the requeue landed: Requeued (a dead-lettered or quarantined job returned to Scheduled " +
        "with its attempt count reset), or NotRequeueable (the job was absent or in a state that " +
        "cannot be requeued; nothing changed).")]
    string Status);

/// <summary>The structured result of <c>pause_queue</c> and <c>resume_queue</c>.</summary>
/// <param name="Queue">The queue acted on.</param>
/// <param name="Paused">The queue's paused state after the action.</param>
internal sealed record QueuePauseStateResult(
    [property: Description("The queue acted on.")]
    string Queue,
    [property: Description("The queue's cluster-wide paused state after the action: true after pause_queue, false after resume_queue.")]
    bool Paused);

/// <summary>The structured result of <c>set_concurrency_limit</c>.</summary>
/// <param name="Queue">The queue acted on.</param>
/// <param name="Limit">The limit now in effect, or null when the cap was removed.</param>
internal sealed record SetConcurrencyLimitResult(
    [property: Description("The queue acted on.")]
    string Queue,
    [property: Description("The cluster-wide concurrency limit now in effect for the queue, or null when the cap was removed.")]
    int? Limit);

// Named ...ToolResult (unlike its siblings) so it can never shadow the storage layer's
// TriggerScheduleResult enum, which the tool body consumes.
/// <summary>The structured result of <c>trigger_schedule</c>.</summary>
/// <param name="Status">How the trigger landed.</param>
internal sealed record TriggerScheduleToolResult(
    [property: Description(
        "How the trigger landed: Triggered (one instance was minted to run now; the schedule's " +
        "cursor and future ticks are untouched), or ScheduleNotFound (no schedule with that id " +
        "exists; nothing changed).")]
    string Status);
