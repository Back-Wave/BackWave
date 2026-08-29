using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using BackWave.Diagnostics;

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
    [Theory]
    [InlineData(InvariantTrigger.ClaimedJobTerminal, "Halt")]
    [InlineData(InvariantTrigger.ObserverCursorRegressed, "Degrade")]
    public void RecordInvariantViolation_CountsOne_TaggedByTriggerNameAndAction(
        InvariantTrigger trigger, string action)
    {
        var measurements = new ConcurrentBag<(long Value, string? Trigger, string? Action)>();
        using var listener = new MeterListener
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

        BackWaveDiagnostics.RecordInvariantViolation(
            trigger, Enum.Parse<InvariantAction>(action));

        // Both tags present on the one measurement: the id by name, the action by its enum member name.
        var measurement = Assert.Single(measurements, m => m.Trigger == trigger.ToString());
        Assert.Equal(1, measurement.Value);
        Assert.Equal(action, measurement.Action);
    }
}
