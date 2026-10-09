using System.Text;
using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Monitor;
using BackWave.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BackWave.Sqlite.Tests;

public sealed record GeoPoint(double Lat, double Lon);

public sealed record ShipAddress(string City, GeoPoint Point);

public sealed record Parcel(string Sku, int Quantity);

[Job("pack-shipment")]
public sealed record PackShipment(
    string OrderId,
    int Priority,
    bool Rush,
    DateTimeOffset PackedAt,
    ShipAddress Destination,
    List<Parcel> Parcels,
    Dictionary<string, int> Stock,
    ShipAddress? ReturnTo);

public sealed class PackShipmentRecorder
{
    public List<PackShipment> Received { get; } = [];
}

public sealed class PackShipmentHandler(PackShipmentRecorder recorder) : IJobHandler<PackShipment>
{
    public Task HandleAsync(PackShipment job, JobContext context, CancellationToken cancellationToken)
    {
        lock (recorder.Received)
        {
            recorder.Received.Add(job);
        }

        return Task.CompletedTask;
    }
}

[JsonSerializable(typeof(PackShipment))]
internal sealed partial class PackShipmentJsonContext : JsonSerializerContext;

/// <summary>
/// A payload with complex members, enqueued through the public client into a real SQLite file, claimed
/// and run by a hosted worker group, and read back from the row the store wrote.
/// </summary>
public sealed class SqliteComplexPayloadTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Complex_payload_is_stored_claimed_and_handed_to_the_handler_unchanged()
    {
        await using var temp = TempSqliteStore.Create();
        var recorder = new PackShipmentRecorder();
        // The empty builder reads no appsettings file, so no config file watcher starts. On macOS that
        // watcher's start can block forever.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();
        builder.Services.AddSingleton(recorder);
        builder.Services.AddTransient<IJobHandler<PackShipment>, PackShipmentHandler>();
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(temp.Store)
            .UseRegistry(Generated.BackWaveJobs.CreateRegistry())
            .AddWorkerGroup(new WorkerGroupOptions
            {
                Name = "workers",
                Policy = new DispatchPolicy.Strict(["default"]),
                PollInterval = TimeSpan.FromMilliseconds(25),
                LeaseDuration = TimeSpan.FromSeconds(5),
            }));
        using var host = builder.Build();
        var shipment = new PackShipment(
            "o-17",
            2,
            true,
            new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(2)),
            new ShipAddress("Leeds", new GeoPoint(53.8, -1.55)),
            [new Parcel("A-1", 2), new Parcel("B-2", 1)],
            new() { ["A-1"] = 8, ["B-2"] = 0 },
            ReturnTo: null);

        await host.StartAsync();
        var jobId = await host.Services.GetRequiredService<BackWaveClient>()
            .EnqueueAsync(shipment, dueTime: DateTimeOffset.UtcNow);
        var monitor = host.Services.GetRequiredService<BackWaveMonitor>();
        var deadline = DateTimeOffset.UtcNow + TestTimeout;
        while (DateTimeOffset.UtcNow < deadline && (await monitor.GetJobAsync(jobId))?.State != JobState.Succeeded)
        {
            await Task.Delay(25);
        }
        await host.StopAsync();

        Assert.Equal(JobState.Succeeded, (await monitor.GetJobAsync(jobId))?.State);
        var received = Assert.Single(recorder.Received);
        Assert.Equal(shipment.OrderId, received.OrderId);
        Assert.Equal(shipment.Priority, received.Priority);
        Assert.Equal(shipment.Rush, received.Rush);
        Assert.Equal(shipment.PackedAt, received.PackedAt);
        Assert.Equal(shipment.PackedAt.Offset, received.PackedAt.Offset);
        Assert.Equal(shipment.Destination, received.Destination);
        Assert.Equal(shipment.Parcels, received.Parcels);
        Assert.Equal(shipment.Stock, received.Stock);
        Assert.Null(received.ReturnTo);
        Assert.Equal(
            """{"OrderId":"o-17","Priority":2,"Rush":true,"PackedAt":"2026-03-04T05:06:07+02:00","Destination":""" +
            """{"City":"Leeds","Point":{"Lat":53.8,"Lon":-1.55}},"Parcels":""" +
            """[{"Sku":"A-1","Quantity":2},{"Sku":"B-2","Quantity":1}],"Stock":{"A-1":8,"B-2":0},"ReturnTo":null}""",
            await StoredPayloadAsync(temp.Path, jobId));
    }

    private static async Task<string> StoredPayloadAsync(string path, Guid jobId)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM backwave_jobs WHERE job_id = $id";
        command.Parameters.AddWithValue("$id", jobId.ToString("D"));
        return Encoding.UTF8.GetString((byte[])(await command.ExecuteScalarAsync())!);
    }
}
