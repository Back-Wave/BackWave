using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using BackWave.Diagnostics;
using BackWave.Storage;

namespace BackWave.Tests;

/// <summary>
/// The <c>messaging.process.duration</c> histogram records handler latency in SECONDS, not milliseconds.
/// The deterministic harness runs every handler inline at a single virtual instant, so ITS process-duration
/// measurements are always a structural zero - and 0 ms == 0 s, so a seconds-to-milliseconds unit slip
/// passes unnoticed there. Driving the recorder with a known non-zero elapsed is what pins the unit: five
/// seconds must record 5.0, never 5000, and the instrument must declare its unit as "s".
/// </summary>
public class ProcessDurationUnitTests
{
    [Fact]
    public void RecordJobDuration_RecordsSeconds_NotMilliseconds()
    {
        // A unique wire name so a concurrently running test's measurements never bleed into this one.
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

        var measurements = new ConcurrentBag<(double Value, string? Unit)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BackWaveDiagnostics.SourceName
                    && instrument.Name == "messaging.process.duration")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            var mine = Equals(
                tags.ToArray().FirstOrDefault(t => t.Key == "messaging.destination.template").Value, wireName);
            if (mine)
            {
                measurements.Add((value, instrument.Unit));
            }
        });
        listener.Start();

        // A five-SECOND handler latency. duration.TotalSeconds records 5.0; a regression to TotalMilliseconds
        // would record 5000. Assert both the recorded value and the instrument's declared unit pin "seconds".
        BackWaveDiagnostics.RecordJobDuration(job, TimeSpan.FromSeconds(5), ExecutionOutcome.Success);

        var measurement = Assert.Single(measurements);
        Assert.Equal(5d, measurement.Value);
        Assert.Equal("s", measurement.Unit);
    }
}
