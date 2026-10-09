using System.Text.Json;
using System.Text.Json.Serialization;
using BackWave.Jobs;

namespace BackWave.AotConsumer;

public enum Shade
{
    Pale,
    Deep,
}

public sealed record Bin(string BinCode, Shade Shade);

public sealed class Link
{
    public Link? Next { get; set; }
}

[Job("aot-mixed")]
public sealed record Mixed(
    string Id,
    int Count,
    Shade Shade,
    List<string> Skus,
    Dictionary<string, int> Counts,
    Bin? Bin,
    IReadOnlyList<Bin> History,
    int[] Slots,
    Link Head,
    JsonElement Raw,
    JsonElement? MaybeRaw);

public sealed class MixedHandler : IJobHandler<Mixed>
{
    public Task HandleAsync(Mixed job, JobContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class Bucketer
{
    [Job("aot-buckets")]
    public Task BucketAsync(Dictionary<string, List<Bin>> buckets, string batch) => Task.CompletedTask;
}

[JsonSerializable(typeof(Mixed))]
[JsonSerializable(typeof(Dictionary<string, List<Bin>>))]
internal sealed partial class AotJsonContext : JsonSerializerContext;
