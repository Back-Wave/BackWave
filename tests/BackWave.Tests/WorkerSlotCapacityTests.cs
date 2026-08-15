using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using BackWave.Diagnostics;
using BackWave.Storage;

namespace BackWave.Tests;

/// <summary>
/// The <c>backwave.worker.slots.capacity</c> gauge accumulates its pool size PER consumer group, so a
/// group backed by several pumps reports its whole concurrency rather than one pump's slice, and
/// disposing a pump's registration subtracts exactly that pump's contribution. The active up/down
/// counter carries the same consumer-group attribute so the two instruments join for headroom.
/// </summary>
public class WorkerSlotCapacityTests
{
    // A real SDK aggregates this observable gauge as LastValue: one measurement per distinct attribute
    // set. So the fix is correct only if ObserveSlotCapacities yields a SINGLE measurement per group,
    // carrying the accumulated capacity. Observing the callback directly - no SDK, no aggregation -
    // proves the accumulation happens at registration rather than being papered over by the collector.
    private static Dictionary<string, long> ObserveByGroup() =>
        BackWaveDiagnostics.ObserveSlotCapacities()
            .ToDictionary(
                m => (string)m.Tags.ToArray().Single(t => t.Key == "messaging.consumer.group.name").Value!,
                m => m.Value);

    [Fact]
    public void Capacity_AccumulatesPerGroup_AcrossPumps_AndReleasesOneContributionPerDispose()
    {
        // A distinct group name so a concurrently running test's registrations never bleed in.
        var group = $"orders-{Guid.NewGuid():N}";

        // Four pumps of the same group at PoolSize 20 - the Pumps=4 registration - is 80 concurrent
        // slots, not 20. A List keyed only by (group, 20) would yield four identical measurements a
        // LastValue gauge collapses to 20; the Dictionary accumulates to one measurement of 80.
        var registrations = Enumerable.Range(0, 4)
            .Select(_ => BackWaveDiagnostics.RegisterWorkerSlotCapacity(group, 20))
            .ToList();

        var byGroup = ObserveByGroup();
        Assert.Equal(80, byGroup[group]);

        // Disposing one pump's registration subtracts exactly its 20, not the whole group.
        registrations[0].Dispose();
        Assert.Equal(60, ObserveByGroup()[group]);

        // The group leaves the gauge entirely once its last pump stops - no stale capacity lingers.
        foreach (var registration in registrations.Skip(1))
        {
            registration.Dispose();
        }
        Assert.DoesNotContain(group, ObserveByGroup().Keys);
    }

    [Fact]
    public void ActiveCounter_CarriesTheConsumerGroup_SoItJoinsToCapacity()
    {
        var group = $"orders-{Guid.NewGuid():N}";
        var wireName = $"probe-{Guid.NewGuid():N}";
        var job = new JobRecord
        {
            JobId = Guid.NewGuid(),
            WireName = wireName,
            Payload = ReadOnlyMemory<byte>.Empty,
            Queue = "default",
            State = JobState.Leased,
            DueTime = DateTimeOffset.UnixEpoch,
        };

        var deltas = new ConcurrentBag<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BackWaveDiagnostics.SourceName
                    && instrument.Name == "backwave.worker.slots.active")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            deltas.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();

        BackWaveDiagnostics.RecordWorkerSlotOccupied(job, group);
        BackWaveDiagnostics.RecordWorkerSlotReleased(job, group);

        var mine = deltas.Where(d => Equals(d.Tags.GetValueOrDefault("messaging.destination.template"), wireName)).ToList();
        Assert.Equal(2, mine.Count);
        Assert.All(mine, d => Assert.Equal(group, d.Tags.GetValueOrDefault("messaging.consumer.group.name")));
        Assert.Contains(mine, d => d.Value == 1);
        Assert.Contains(mine, d => d.Value == -1);
    }
}
