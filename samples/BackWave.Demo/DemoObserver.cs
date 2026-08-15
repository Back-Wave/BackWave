using System.Text.Json;
using BackWave.Observers;

namespace BackWave.Demo;

/// <summary>
/// A Transition Observer that keeps the dashboard's Observers surface alive: on every terminal
/// transition of an <c>order-notification</c> job it lazily reads the payload body and writes one
/// structured "pretend-posting to Slack" line. Real delivery attempts, real delivery lag — synthetic
/// destination. Registered through <c>backwave.AddObservers(...)</c> and resolved per delivery from a
/// DI scope, so it can take an injected <see cref="ILogger{T}"/>.
/// </summary>
public sealed class DemoObserver(ILogger<DemoObserver> logger) : ITransitionObserver
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async ValueTask OnTransitionAsync(ObserverContext context, CancellationToken cancellationToken)
    {
        // Reach for the body lazily; tolerate the purged case a lazy read can race the retention sweep into.
        var payload = await context.Payload.GetAsync(cancellationToken).ConfigureAwait(false);

        string orderRef = "<unavailable>";
        string customerEmail = "<unavailable>";
        if (payload.Available)
        {
            var order = JsonSerializer.Deserialize<OrderBody>(payload.Bytes.Span, JsonOptions);
            orderRef = order?.OrderRef ?? "<missing>";
            customerEmail = order?.CustomerEmail ?? "<missing>";
        }

        logger.LogInformation(
            "demo-observer: pretend-posting to Slack — order {OrderRef} for {CustomerEmail} reached {State} "
            + "(job {JobId}, wire {WireName}, queue {Queue}, attempt {Attempt}, at {Timestamp:O})",
            orderRef, customerEmail, context.State,
            context.JobId, context.WireName, context.Queue, context.Attempt, context.Timestamp);
    }

    private sealed record OrderBody(string? OrderRef, string? CustomerEmail);
}
