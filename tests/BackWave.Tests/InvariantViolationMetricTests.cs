using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using BackWave.Diagnostics;
using BackWave.Storage;
using BackWave.Storage.InMemory;

namespace BackWave.Tests;

/// <summary>
/// The <c>backwave.invariant.violations</c> counter is public API down to its tag VALUES: an operator's
/// alert rule matches a fail-stop on the trigger id and the action, so the instrument name, both tag keys,
/// and the strings those tags carry are all a compatibility surface. These pin them - the trigger tag
/// carries the <see cref="InvariantTrigger"/> member NAME (never its ordinal, which is deliberately
/// unpinned), and the action tag carries Halt or Degrade.
/// </summary>
public class InvariantViolationMetricTests
{
    /// <summary>
    /// One row per action, each pinned to a trigger a real site raises THAT way: the Worker Group pump
    /// halts on <see cref="InvariantTrigger.ClaimedJobTerminal"/>, and every adapter's observer-report
    /// fence degrades on <see cref="InvariantTrigger.ObserverReportFenceRejected"/>. A hand-picked pairing
    /// no site produces would pin only the tag encoding while reading like a claim about the product.
    /// </summary>
    [Theory]
    [InlineData(InvariantTrigger.ClaimedJobTerminal, "Halt")]
    [InlineData(InvariantTrigger.ObserverReportFenceRejected, "Degrade")]
    public void RecordInvariantViolation_CountsOne_TaggedByTriggerNameAndAction(
        InvariantTrigger trigger, string action)
    {
        var measurements = new ConcurrentBag<(long Value, string? Trigger, string? Action)>();
        using var listener = Listen(measurements);

        BackWaveDiagnostics.RecordInvariantViolation(
            trigger, Enum.Parse<InvariantAction>(action));

        // Both tags present on the one measurement: the id by name, the action by its enum member name.
        var measurement = Assert.Single(measurements, m => m.Trigger == trigger.ToString());
        Assert.Equal(1, measurement.Value);
        Assert.Equal(action, measurement.Action);
    }

    /// <summary>
    /// A claim Lease that simply ran out is the ordinary end of a delivery attempt: the store refuses the
    /// late report (at-least-once redelivers it), but nothing about the fleet is broken, so the violation
    /// counter - the surface a promotion rule reads FOR ZEROS - must stay untouched.
    /// </summary>
    [Fact]
    public async Task ObserverReportFence_RefusesALapsedLease_WithoutCountingAViolation()
    {
        var store = new InMemoryJobStore();
        var claim = await ClaimOneDeliveryAsync(store, "node-a");

        var measurements = new ConcurrentBag<(long Value, string? Trigger, string? Action)>();
        using var listener = Listen(measurements);

        // The same worker reports after its own Lease lapsed - the everyday late-report race.
        var outcome = await store.TryReportObserverDeliveriesAsync(new ObserverDeliveryReport(
            "obs", "node-a", [Delivered(claim)], T0 + Lease + TimeSpan.FromSeconds(1)));

        Assert.Equal(ObserverReportOutcome.FenceRejected, outcome); // still refused, still no-op
        Assert.DoesNotContain(
            measurements, m => m.Trigger == nameof(InvariantTrigger.ObserverReportFenceRejected));
    }

    /// <summary>
    /// A report from a worker that is not the owner while the Lease is still LIVE is the contradiction the
    /// trigger exists for: two workers believe they hold the same observer claim at once. That one counts.
    /// </summary>
    [Fact]
    public async Task ObserverReportFence_CountsAViolation_WhenANonOwnerReportsUnderALiveLease()
    {
        var store = new InMemoryJobStore();
        var claim = await ClaimOneDeliveryAsync(store, "node-a");

        var measurements = new ConcurrentBag<(long Value, string? Trigger, string? Action)>();
        using var listener = Listen(measurements);

        // node-b never held this claim, and node-a's Lease has not lapsed.
        var outcome = await store.TryReportObserverDeliveriesAsync(new ObserverDeliveryReport(
            "obs", "node-b", [Delivered(claim)], T0 + TimeSpan.FromSeconds(1)));

        Assert.Equal(ObserverReportOutcome.FenceRejected, outcome);
        var measurement = Assert.Single(
            measurements, m => m.Trigger == nameof(InvariantTrigger.ObserverReportFenceRejected));
        Assert.Equal(1, measurement.Value);
        Assert.Equal(nameof(InvariantAction.Degrade), measurement.Action);
    }

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    /// <summary>Enqueue one job and claim its Scheduled transition for observer 'obs' under a live Lease.</summary>
    private static async Task<ObserverClaimedDelivery> ClaimOneDeliveryAsync(InMemoryJobStore store, string worker)
    {
        await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "pay-job", ReadOnlyMemory<byte>.Empty, "default", T0), T0);
        var claim = await store.ClaimObserverDeliveriesAsync(new ObserverClaimRequest(
            "obs", [JobState.Scheduled], WireName: null, Queue: null, worker, MaxRows: 32, Lease, T0));
        return Assert.Single(claim.Deliveries);
    }

    private static ObserverDeliveryOutcome Delivered(ObserverClaimedDelivery delivery)
        => new(delivery.Position, ObserverDeliveryDisposition.Delivered);

    /// <summary>
    /// Subscribes to the violation counter. The instrument is process-global, so every test that reads it
    /// lives in THIS class: xUnit runs a class's tests one at a time, which is what keeps one test's
    /// deliberate violation out of another's measurements.
    /// </summary>
    private static MeterListener Listen(ConcurrentBag<(long Value, string? Trigger, string? Action)> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BackWaveDiagnostics.SourceName
                    && instrument.Name == "backwave.invariant.violations")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var pairs = tags.ToArray();
            measurements.Add((
                value,
                pairs.FirstOrDefault(t => t.Key == "backwave.invariant.trigger").Value as string,
                pairs.FirstOrDefault(t => t.Key == "backwave.invariant.action").Value as string));
        });
        listener.Start();
        return listener;
    }
}
