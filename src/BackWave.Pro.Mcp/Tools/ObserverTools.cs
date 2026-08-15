using System.ComponentModel;
using BackWave.Monitor;
using BackWave.Observers;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace BackWave.Pro.Mcp.Tools;

// The observer-delivery-health read tools (mcp-0003 inventory). The observer's subscription is
// resolved host-side from the canonical registration list AddObservers publishes to DI — exactly
// how the dashboard does it — so a client only ever supplies the observer id, never a subscription.
// The list is resolved lazily through IServiceProvider because a host with no observers registers
// no list at all. Internal: the tool surface is wire-level (MCP), never a C# API. Registered
// explicitly via WithTools<ObserverTools>() in AddMcp; never assembly scanning.
[McpServerToolType]
internal sealed class ObserverTools(BackWaveMonitor monitor, IServiceProvider services)
{
    private IReadOnlyList<ObserverRegistration> Registrations
        => services.GetService<IReadOnlyList<ObserverRegistration>>() ?? [];

    [McpServerTool(
        Name = ToolNames.GetObserverLag,
        Title = "Get observer lag",
        UseStructuredContent = true,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "One transition observer's delivery lag: how far its durable cursor trails the job " +
        "transitions it subscribes to. pending is 0 when the observer is caught up; a growing " +
        "oldestPendingAt age signals an observer falling behind. Observer ids are the stable ids " +
        "the host registered its observers under; only transitions matching that observer's own " +
        "subscription are counted. Returns found=false when no observer with the id is registered " +
        "on this host. Metadata only - never payloads or failure detail.")]
    public async Task<ObserverLagResult> GetObserverLagAsync(
        [Description("The id of the registered observer whose lag to read.")] string observer_id,
        CancellationToken cancellationToken)
    {
        if (Registrations.FirstOrDefault(r => r.Id == observer_id) is not { } registration)
        {
            return new ObserverLagResult { Found = false };
        }

        var lag = await monitor.GetObserverLagAsync(observer_id, registration.Subscription, cancellationToken)
            .ConfigureAwait(false);
        return new ObserverLagResult
        {
            Found = true,
            Cursor = lag.Cursor,
            Pending = lag.Pending,
            OldestPendingAt = lag.OldestPendingAt,
        };
    }

    [McpServerTool(
        Name = ToolNames.ListObserverDeadLetters,
        Title = "List observer dead letters",
        UseStructuredContent = true,
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "One transition observer's dead-lettered deliveries, oldest first - deliveries that " +
        "exhausted their retry ceiling, surfaced like dead-lettered jobs. Each record carries " +
        "delivery metadata only (log position, the job it points at, state, attempt counts, " +
        "timestamps) - never payloads or failure detail. Empty for a healthy observer. Returns " +
        "found=false when no observer with the id is registered on this host.")]
    public async Task<ObserverDeadLettersResult> ListObserverDeadLettersAsync(
        [Description("The id of the registered observer whose dead-lettered deliveries to read.")]
        string observer_id,
        CancellationToken cancellationToken)
    {
        if (Registrations.All(r => r.Id != observer_id))
        {
            return new ObserverDeadLettersResult { Found = false, DeadLetters = [] };
        }

        var records = await monitor.ListObserverDeadLettersAsync(observer_id, cancellationToken)
            .ConfigureAwait(false);
        return new ObserverDeadLettersResult
        {
            Found = true,
            DeadLetters = [.. records.Select(r => new ObserverDeadLetterRow(
                r.Position, r.JobId, r.Ordinal, r.State.ToString(), r.Attempt, r.DeliveryAttempts, r.DeadLetteredAt))],
        };
    }
}

/// <summary>The structured result of <c>get_observer_lag</c>.</summary>
internal sealed record ObserverLagResult
{
    /// <summary>Whether an observer with the given id is registered on this host.</summary>
    [Description("Whether an observer with the given id is registered on this host. All other " +
        "fields are meaningful only when true.")]
    public required bool Found { get; init; }

    /// <summary>The durable delivered-through log position, or −1 when nothing was delivered yet.</summary>
    [Description("The durable delivered-through log position; -1 when nothing has been delivered yet.")]
    public long? Cursor { get; init; }

    /// <summary>How many matching transitions the cursor has not yet passed; 0 when caught up.</summary>
    [Description("How many transitions matching the observer's subscription its cursor has not " +
        "yet passed; 0 means the observer is caught up.")]
    public int? Pending { get; init; }

    /// <summary>When the oldest pending matching transition occurred; null when caught up.</summary>
    [Description("When the oldest pending matching transition occurred; null when the observer is caught up.")]
    public DateTimeOffset? OldestPendingAt { get; init; }
}

/// <summary>The structured result of <c>list_observer_dead_letters</c>.</summary>
internal sealed record ObserverDeadLettersResult
{
    /// <summary>Whether an observer with the given id is registered on this host.</summary>
    [Description("Whether an observer with the given id is registered on this host.")]
    public required bool Found { get; init; }

    /// <summary>The dead-lettered deliveries, oldest first.</summary>
    [Description("The observer's dead-lettered deliveries, oldest first; empty for a healthy " +
        "observer (and when found is false).")]
    public required IReadOnlyList<ObserverDeadLetterRow> DeadLetters { get; init; }
}

/// <summary>One dead-lettered observer delivery (metadata only).</summary>
/// <param name="Position">The delivery-log position of the dead-lettered row.</param>
/// <param name="JobId">The job the dead-lettered transition belongs to.</param>
/// <param name="Ordinal">The transition's per-job ordinal.</param>
/// <param name="State">The state the job had transitioned into.</param>
/// <param name="Attempt">The job's attempt number at the transition.</param>
/// <param name="DeliveryAttempts">How many delivery attempts were made before giving up.</param>
/// <param name="DeadLetteredAt">When the delivery was dead-lettered.</param>
internal sealed record ObserverDeadLetterRow(
    [property: Description("The delivery-log position of the dead-lettered row.")]
    long Position,
    [property: Description("The job the dead-lettered transition belongs to.")]
    Guid JobId,
    [property: Description("The transition's per-job ordinal within that job's history.")]
    long Ordinal,
    [property: Description("The job state the transition produced, e.g. Succeeded, DeadLettered.")]
    string State,
    [property: Description("The job's attempt number at the transition.")]
    int Attempt,
    [property: Description("How many delivery attempts were made before giving up.")]
    int DeliveryAttempts,
    [property: Description("When the delivery was dead-lettered.")]
    DateTimeOffset DeadLetteredAt);
