using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using BackWave.Diagnostics;
using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Storage.InMemory;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

public class DeterministicPumpTelemetryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UnwrittenOutcomes_CountTheOutcomeUntilItsWriteAnswers()
    {
        var group = $"harness-{Guid.NewGuid():N}";
        var deltas = new ConcurrentQueue<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BackWaveDiagnostics.SourceName
                    && instrument.Name == "backwave.worker.outcomes.unwritten")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "messaging.consumer.group.name" && tag.Value as string == group)
                {
                    deltas.Enqueue(value);
                }
            }
        });
        listener.Start();

        var services = new ServiceCollection()
            .AddSingleton<ExecutionRecorder>()
            .AddTransient<IJobHandler<SendWelcomeEmail>, SendWelcomeEmailHandler>()
            .BuildServiceProvider();
        var registry = Generated.BackWaveJobs.CreateRegistry();
        var store = new InMemoryJobStore();
        var driver = new NodeDriver(new NodeOptions { WorkerId = "node-1", Policy = new Core.DispatchPolicy.Strict(["default"]) });
        var pump = new DeterministicPump(driver, store, registry, services, consumerGroup: group);
        await new BackWaveClient(store, registry).EnqueueAsync(new SendWelcomeEmail("ada@example.test"), dueTime: T0);

        await pump.PumpAsync(T0);

        Assert.Equal([1L, -1L], deltas);
    }
}
