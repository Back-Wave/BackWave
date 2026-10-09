using System.Text.Json.Serialization;
using BackWave.Jobs;

namespace BackWave.Sample.Api;

public enum ParcelSpeed
{
    Standard,
    Express,
}

/// <summary>One line of a parcel: a SKU, how many to pack, and the serial number of each unit.</summary>
public sealed record ParcelLine(string Sku, int Quantity, List<string> SerialNumbers);

/// <summary>Where a parcel goes.</summary>
public sealed record ParcelAddress(string Street, string City, string? Region);

/// <summary>
/// A <c>[Job]</c> payload with complex members: a list of nested records, a dictionary, a nullable
/// record, and an array. The generated codec writes the scalar members itself and hands each complex
/// member to <see cref="ParcelJsonContext"/>, which lists this payload type.
/// </summary>
[Job("dispatch-parcel", Queue = "low")]
public sealed record DispatchParcel(
    string ParcelRef,
    ParcelSpeed Speed,
    List<ParcelLine> Lines,
    Dictionary<string, int> BinCounts,
    ParcelAddress? Destination,
    string[] Notes);

public sealed class DispatchParcelHandler(ILogger<DispatchParcelHandler> logger) : IJobHandler<DispatchParcel>
{
    public Task HandleAsync(DispatchParcel job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "dispatch-parcel: {ParcelRef} ({Speed}) packed {Lines} lines, {Units} units, to {City}",
            job.ParcelRef, job.Speed, job.Lines.Count, job.Lines.Sum(line => line.Quantity), job.Destination?.City ?? "(none)");
        return Task.CompletedTask;
    }
}

[JsonSerializable(typeof(DispatchParcel))]
internal sealed partial class ParcelJsonContext : JsonSerializerContext;
