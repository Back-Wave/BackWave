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
[Collection(InvariantViolationCounterCollection.Name)]
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

        // The same worker reports after its own Lease lapsed - the everyday late-report race. It says
        // what it believed, and what it believed is already behind it, so there is nothing to contradict.
        var outcome = await store.TryReportObserverDeliveriesAsync(new ObserverDeliveryReport(
            "obs", "node-a", [Delivered(claim)], T0 + Lease + TimeSpan.FromSeconds(1))
        {
            BelievedLeaseExpiry = T0 + Lease,
        });

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

        // node-b never held this claim, and node-b still believes its own claim Lease runs a full minute
        // out - well past the skew a fleet can carry - while node-a holds the row.
        var outcome = await store.TryReportObserverDeliveriesAsync(new ObserverDeliveryReport(
            "obs", "node-b", [Delivered(claim)], T0 + TimeSpan.FromSeconds(1))
        {
            BelievedLeaseExpiry = T0 + Lease,
        });

        Assert.Equal(ObserverReportOutcome.FenceRejected, outcome);
        var measurement = Assert.Single(
            measurements, m => m.Trigger == nameof(InvariantTrigger.ObserverReportFenceRejected));
        Assert.Equal(1, measurement.Value);
        Assert.Equal(nameof(InvariantAction.Degrade), measurement.Action);
    }

    /// <summary>
    /// The case the row's own expiry gets wrong. After node-a's Lease lapses, node-b reclaims the observer
    /// and stamps a fresh Lease, and only then does node-a's late report land. The row now carries a
    /// FUTURE expiry, so a fence that reads the row counts a violation - but no two workers ever held this
    /// claim at once, and the report itself says node-a believed its Lease was long gone.
    /// </summary>
    [Fact]
    public async Task ObserverReportFence_StaysSilent_WhenAPeerAlreadyReclaimedTheObserver()
    {
        var store = new InMemoryJobStore();
        var claim = await ClaimOneDeliveryAsync(store, "node-a");

        // node-a's Lease lapses, and node-b takes the claim: the row's expiry moves into the future.
        var afterLapse = T0 + Lease + TimeSpan.FromSeconds(1);
        var reclaim = await store.ClaimObserverDeliveriesAsync(new ObserverClaimRequest(
            "obs", [JobState.Scheduled], WireName: null, Queue: null, "node-b", MaxRows: 32, Lease, afterLapse));
        Assert.True(reclaim.Acquired);

        var measurements = new ConcurrentBag<(long Value, string? Trigger, string? Action)>();
        using var listener = Listen(measurements);

        var outcome = await store.TryReportObserverDeliveriesAsync(new ObserverDeliveryReport(
            "obs", "node-a", [Delivered(claim)], afterLapse + TimeSpan.FromSeconds(1))
        {
            BelievedLeaseExpiry = T0 + Lease,
        });

        Assert.Equal(ObserverReportOutcome.FenceRejected, outcome);
        Assert.DoesNotContain(
            measurements, m => m.Trigger == nameof(InvariantTrigger.ObserverReportFenceRejected));
    }

    /// <summary>
    /// The whole trigger set, by name. An operator's alert rule matches the
    /// <c>backwave.invariant.trigger</c> tag against a literal string, so renaming a member silently
    /// breaks that rule in the field - nothing in the compiler or the suite would say a word. This test
    /// says it. Adding a member is a normal change: put the new name in the list. Renaming or removing
    /// one is a breaking change to a shipped surface, and the release notes have to carry it.
    /// <para>
    /// Only the NAMES are pinned. The ordinals are deliberately unpinned - the tag carries the name, and
    /// nothing durable stores the number - so the list is compared as a set, in sorted order.
    /// </para>
    /// </summary>
    [Fact]
    public void InvariantTrigger_CarriesExactlyThesePublishedNames()
    {
        string[] published =
        [
            "OutcomeBatchCountMismatch", "HeartbeatBatchCountMismatch",
            "ClaimedJobTerminal", "OutcomeFenceRejected",
            "ClaimBatchOverrun", "WorkflowMemberWithoutWorkflow",
            "ClaimedRowNotLeasedToWorker", "ClaimedRowNotEligible",
            "WorkflowMemberEnqueueRejected", "DanglingGatingEdge",
            "UnexpectedAffectedRowCount", "GuaranteedRowAbsent",
            "ParentJobMissingFromBatch", "UndefinedEnumValueStored",
            "ObserverCursorRegressed", "ApplicationLockNotAcquired",
            "QueueConfigLockNotAcquired", "LeasedCountAggregateNull",
            "ObserverReportFenceRejected", "WorkflowMemberCycle",
        ];

        var actual = Enum.GetNames<InvariantTrigger>();

        Assert.Equal(published.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
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
    /// Subscribes to the violation counter. The instrument is process-global, so a reader sees every
    /// violation the process raises, whoever raised it. Every test that reads it lives in THIS class,
    /// and the classes that deliberately raise one share this class's xUnit collection, so no writer
    /// ever runs while a listener is attached.
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

/// <summary>
/// The xUnit collection serializing the tests that READ the process-global
/// <c>backwave.invariant.violations</c> counter against the ones that WRITE it. A MeterListener sees
/// every measurement the process records, so a class that deliberately raises a violation - the
/// in-memory run of the Conformance Suite drives the observer-report fence contradiction - would
/// otherwise land its trigger in a listener attached by a class running beside it, and fail an
/// assertion for a reason that is not a product bug. Any new test that raises a violation joins here.
/// </summary>
[CollectionDefinition(Name)]
public sealed class InvariantViolationCounterCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "BackWave invariant-violation counter";
}
