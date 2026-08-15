using System.Text.Json;
using BackWave.Observers;

namespace BackWave.Sample.Api;

/// <summary>
/// A dummy Transition Observer that <b>pretends</b> to send a Slack message but really just writes
/// one structured console log line per delivery — the observer proof-out for the
/// <c>order-notification</c> scenario. The line carries the transition metadata (job id, wire name,
/// queue, state, attempt, timestamp) <b>and</b> two fields read from the job's payload <b>body</b>
/// (<c>orderRef</c>, <c>customerEmail</c>) via the lazy <see cref="ObserverContext.Payload"/>
/// accessor, proving the end-to-end lazy payload read works in a real host.
///
/// Registered through <c>backwave.AddObservers(...)</c> in <see cref="Program"/> and resolved per
/// delivery from a DI scope, so it can take an injected <see cref="ILogger{T}"/> exactly like
/// <see cref="SampleJobs"/> does.
/// </summary>
public sealed class SlackObserver(ILogger<SlackObserver> logger) : ITransitionObserver
{
    // The payload bytes are the JSON-serialized OrderNotification document; match the generated
    // PascalCase property names, but stay case-insensitive to be tolerant.
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async ValueTask OnTransitionAsync(ObserverContext context, CancellationToken cancellationToken)
    {
        // Reach for the body lazily — this is the point of the demo. Tolerate the purged case
        // (Available == false) the lazy read can race the retention sweep into.
        var payload = await context.Payload.GetAsync(cancellationToken).ConfigureAwait(false);

        string orderRef = "<unavailable>";
        string customerEmail = "<unavailable>";
        if (payload.Available)
        {
            var order = JsonSerializer.Deserialize<OrderBody>(payload.Bytes.Span, JsonOptions);
            orderRef = order?.OrderRef ?? "<missing>";
            customerEmail = order?.CustomerEmail ?? "<missing>";
        }

        // One structured line "pretending" to post to Slack: transition metadata + payload body.
        logger.LogInformation(
            "slack-observer: pretend-posting to Slack — order {OrderRef} for {CustomerEmail} reached {State} "
            + "(job {JobId}, wire {WireName}, queue {Queue}, attempt {Attempt}, at {Timestamp:O})",
            orderRef, customerEmail, context.State,
            context.JobId, context.WireName, context.Queue, context.Attempt, context.Timestamp);
    }

    /// <summary>The slice of the order-notification payload body this observer reads.</summary>
    private sealed record OrderBody(string? OrderRef, string? CustomerEmail);
}
