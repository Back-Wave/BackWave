using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

public enum Shade
{
    Pale,
    Deep,
}

public sealed record Bin(string BinCode, Shade Shade);

[Job("stable-scalars")]
public sealed record StableScalars(string Id, int Count, Shade Shade, Guid Key, DateTimeOffset At, bool Rush);

public sealed class StableScalarsHandler : IJobHandler<StableScalars>
{
    public Task HandleAsync(StableScalars job, JobContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

[Job("stable-wide-scalars")]
public sealed record StableWideScalars(
    decimal Price,
    double Ratio,
    long Big,
    DateTime UtcAt,
    DateTime PlainAt,
    int? Set,
    int? Unset,
    decimal? Fee,
    DateTime? NoTime,
    string? Note);

public sealed class StableWideScalarsHandler : IJobHandler<StableWideScalars>
{
    public Task HandleAsync(StableWideScalars job, JobContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

[Job("stable-mixed")]
public sealed record StableMixed(
    string Id,
    int Count,
    Shade Shade,
    Guid Key,
    DateTimeOffset At,
    bool Rush,
    List<string> Skus,
    Dictionary<string, int> Counts,
    Bin? Bin,
    IReadOnlyList<Bin> History,
    int[] Slots);

public sealed class StableMixedHandler(ComplexPayloadRecorder recorder) : IJobHandler<StableMixed>
{
    public Task HandleAsync(StableMixed job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Mixed.Add(job);
        return Task.CompletedTask;
    }
}

public sealed class LineBucketer(ComplexPayloadRecorder recorder)
{
    [Job("bucket-lines")]
    public Task BucketLinesAsync(Dictionary<string, List<Bin>> buckets, string batch)
    {
        recorder.Buckets.Add((buckets, batch));
        return Task.CompletedTask;
    }
}

[Job("raw-note")]
public sealed record RawNote(string Id)
{
    public JsonElement Extra { get; init; }
}

public sealed class RawNoteHandler(ComplexPayloadRecorder recorder) : IJobHandler<RawNote>
{
    public Task HandleAsync(RawNote job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Notes.Add(job);
        return Task.CompletedTask;
    }
}

public sealed class Link
{
    public Link? Next { get; set; }
}

[Job("walk-links")]
public sealed record WalkLinks(string Id, Link Head);

public sealed class WalkLinksHandler(ComplexPayloadRecorder recorder) : IJobHandler<WalkLinks>
{
    public Task HandleAsync(WalkLinks job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Walks.Add(job);
        return Task.CompletedTask;
    }
}

[Job("tag-batch")]
public sealed record TagBatch(string Id, ImmutableArray<string> Tags)
{
    public ImmutableArray<int>? Sizes { get; init; }
}

public sealed class TagBatchHandler : IJobHandler<TagBatch>
{
    public Task HandleAsync(TagBatch job, JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record Shelf(ImmutableArray<string> Codes);

public sealed class Crate
{
    public Crate(string code)
    {
        Code = code.Length > 0 ? code : throw new ArgumentException("A crate needs a code.", nameof(code));
    }

    public string Code { get; }
}

[JsonConverter(typeof(SealConverter))]
public sealed class Seal;

public sealed class SealConverter : JsonConverter<Seal>
{
    public override Seal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new OutOfMemoryException();

    public override void Write(Utf8JsonWriter writer, Seal value, JsonSerializerOptions options) => throw new JsonException();
}

[Job("stock-shelf")]
public sealed record StockShelf(string Id, Shelf Shelf, Crate? Crate, Seal? Seal = null);

public sealed class StockShelfHandler : IJobHandler<StockShelf>
{
    public Task HandleAsync(StockShelf job, JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

[JsonSerializable(typeof(RawNote))]
[JsonSerializable(typeof(WalkLinks))]
[JsonSerializable(typeof(TagBatch))]
[JsonSerializable(typeof(StockShelf))]
internal sealed partial class EdgePayloadJsonContext : JsonSerializerContext;

public sealed class ComplexPayloadRecorder
{
    public List<StableMixed> Mixed { get; } = [];

    public List<RawNote> Notes { get; } = [];

    public List<WalkLinks> Walks { get; } = [];

    public List<(Dictionary<string, List<Bin>> Buckets, string Batch)> Buckets { get; } = [];
}

[JsonPolymorphic]
[JsonDerivedType(typeof(Circle), "circle")]
public interface IShape;

public sealed record Circle(double Radius) : IShape;

[Job("draw")]
public sealed record Draw(string Id, IShape Shape);

public sealed class DrawHandler : IJobHandler<Draw>
{
    public Task HandleAsync(Draw job, JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

[JsonSerializable(typeof(Draw))]
internal sealed partial class DrawJsonContext : JsonSerializerContext;

// The snake-case policy reaches inside a delegated member only: the payload's own property names and
// its top-level enum stay in the generated codec's format.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(StableMixed))]
[JsonSerializable(typeof(Dictionary<string, List<Bin>>))]
internal sealed partial class ComplexPayloadJsonContext : JsonSerializerContext;

/// <summary>
/// Complex payload members at runtime, through the generator the build really runs: exact bytes,
/// scalar format stability, tolerant decode, and a job that enqueues and runs end to end.
/// </summary>
public class ComplexPayloadSerializationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Key = new("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private static readonly DateTimeOffset At = new(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(2));

    // The bytes the generated codec wrote for this payload before complex members existed. The scalar
    // members of StableMixed below must keep exactly this prefix.
    private const string ScalarJson =
        """{"Id":"o-1","Count":3,"Shade":"Deep","Key":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","At":""" +
        "\"2026-03-04T05:06:07+02:00\",\"Rush\":true";

    private static readonly StableWideScalars WideScalars = new(
        Price: 1.50m,
        Ratio: 0.1,
        Big: 9_007_199_254_740_993,
        UtcAt: new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1_234_567),
        PlainAt: new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Unspecified),
        Set: 7,
        Unset: null,
        Fee: 0.10m,
        NoTime: null,
        Note: null);

    private static readonly StableMixed Mixed = new(
        "o-1", 3, Shade.Deep, Key, At, true,
        Skus: ["a", "b"],
        Counts: new() { ["x"] = 1 },
        Bin: new Bin("B-7", Shade.Deep),
        History: [new Bin("H-1", Shade.Pale)],
        Slots: [4, 5]);

    private static JobRegistration Registration(string wireName)
    {
        Assert.True(Generated.BackWaveJobs.CreateRegistry().TryGetByWireName(wireName, out var registration));
        return registration;
    }

    [Fact]
    public void ScalarOnlyPayload_KeepsItsWireFormat()
    {
        var bytes = Registration("stable-scalars").Serialize(new StableScalars("o-1", 3, Shade.Deep, Key, At, true));

        Assert.Equal(ScalarJson + "}", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void WideScalarPayload_KeepsItsWireFormat()
    {
        var bytes = Registration("stable-wide-scalars").Serialize(WideScalars);

        // Decimal keeps its scale, double writes its shortest round-trip form, a long past 2^53 stays exact,
        // a UTC DateTime keeps its Z and every tick, an Unspecified one carries no offset at all.
        Assert.Equal(
            """{"Price":1.50,"Ratio":0.1,"Big":9007199254740993,"UtcAt":"2026-03-04T05:06:07.1234567Z","PlainAt":""" +
            "\"2026-03-04T05:06:07\",\"Set\":7,\"Unset\":null,\"Fee\":0.10,\"NoTime\":null,\"Note\":null}",
            Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void WideScalarPayload_RoundTrips_WithDecimalScaleAndDateTimeKind()
    {
        var registration = Registration("stable-wide-scalars");

        var decoded = (StableWideScalars)registration.Deserialize(registration.Serialize(WideScalars));

        Assert.Equal(WideScalars, decoded);
        Assert.Equal("1.50", decoded.Price.ToString(CultureInfo.InvariantCulture));
        Assert.Equal("0.10", decoded.Fee!.Value.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(DateTimeKind.Utc, decoded.UtcAt.Kind);
        Assert.Equal(DateTimeKind.Unspecified, decoded.PlainAt.Kind);
    }

    [Fact]
    public void ComplexMembers_AppendToTheUnchangedScalarFormat_AndUseTheContextInsideOnly()
    {
        var bytes = Registration("stable-mixed").Serialize(Mixed);

        // Scalars byte-for-byte as before, top-level enum as its name. Inside a delegated member the
        // context owns the format: snake-case names and enums as numbers.
        Assert.Equal(
            ScalarJson +
            ""","Skus":["a","b"],"Counts":{"x":1},"Bin":{"bin_code":"B-7","shade":1},"History":""" +
            """[{"bin_code":"H-1","shade":0}],"Slots":[4,5]}""",
            Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void ComplexMembers_RoundTrip()
    {
        var registration = Registration("stable-mixed");

        var decoded = (StableMixed)registration.Deserialize(registration.Serialize(Mixed));

        AssertEquivalent(Mixed, decoded);
    }

    [Fact]
    public void NullComplexMember_WritesJsonNull_AndReadsBackNull()
    {
        var registration = Registration("stable-mixed");
        var withoutBin = Mixed with { Bin = null };

        var bytes = registration.Serialize(withoutBin);

        Assert.Contains("\"Bin\":null,", Encoding.UTF8.GetString(bytes));
        Assert.Null(((StableMixed)registration.Deserialize(bytes)).Bin);
    }

    [Fact]
    public void Decode_SkipsUnknownProperties_AtTheTopAndInsideAComplexMember()
    {
        var decoded = (StableMixed)Registration("stable-mixed").Deserialize(Encoding.UTF8.GetBytes(
            """{"Legacy":{"deep":[1,{"x":2}]},"Id":"o-2","Bin":{"bin_code":"B-1","retired":{"a":[1]},"shade":1},"Extra":7}"""));

        Assert.Equal("o-2", decoded.Id);
        Assert.Equal(new Bin("B-1", Shade.Deep), decoded.Bin);
    }

    [Fact]
    public void ComplexMember_ReEncodesInTheFormatOfThePayloadContext()
    {
        var registration = Registration("stable-mixed");

        // The member decodes and writes again in the snake case of the payload context.
        var decoded = (StableMixed)registration.Deserialize(Encoding.UTF8.GetBytes(
            """{"Id":"o-4","Bin":{"bin_code":"B-4","shade":1}}"""));

        Assert.Equal(new Bin("B-4", Shade.Deep), decoded.Bin);
        Assert.Contains(
            "\"Bin\":{\"bin_code\":\"B-4\",\"shade\":1}", Encoding.UTF8.GetString(registration.Serialize(decoded)));
    }

    [Fact]
    public void Decode_MissingComplexMembers_ReadAsNull()
    {
        var decoded = (StableMixed)Registration("stable-mixed").Deserialize(Encoding.UTF8.GetBytes("""{"Id":"o-3"}"""));

        Assert.Equal("o-3", decoded.Id);
        Assert.Null(decoded.Skus);
        Assert.Null(decoded.Counts);
        Assert.Null(decoded.Bin);
        Assert.Null(decoded.History);
        Assert.Null(decoded.Slots);
    }

    [Theory]
    [InlineData("""{"Id":"o-4","Bin":{"bin_code":5}}""", "$.Bin.bin_code")]
    [InlineData("""{"Id":"o-4","History":[{"bin_code":"H-1"},{"bin_code":true}]}""", "$.History[1].bin_code")]
    [InlineData("""{"Id":"o-4","Skus":7}""", "$.Skus")]
    public void Decode_WrongTokenInsideAComplexMember_ReportsThePathFromThePayloadRoot(string json, string path)
    {
        var exception = Assert.Throws<JsonException>(
            () => Registration("stable-mixed").Deserialize(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(path, exception.Path);
        Assert.Contains($"Path: {path} |", exception.Message);
    }

    [Fact]
    public async Task ComplexPayloadJobs_EnqueueRunAndReachTheirHandlers()
    {
        var services = new ServiceCollection()
            .AddSingleton<ComplexPayloadRecorder>()
            .AddSingleton<LineBucketer>()
            .AddTransient<IJobHandler<StableMixed>, StableMixedHandler>()
            .AddTransient<IJobHandler<BucketLines>, BucketLinesHandler>()
            .BuildServiceProvider();
        var registry = Generated.BackWaveJobs.CreateRegistry();
        var store = new InMemoryJobStore();
        var driver = new NodeDriver(new NodeOptions { WorkerId = "node-1", Policy = new Core.DispatchPolicy.Strict(["default"]) });
        var pump = new DeterministicPump(driver, store, registry, services);
        var client = new BackWaveClient(store, registry);
        var buckets = new Dictionary<string, List<Bin>> { ["north"] = [new Bin("N-1", Shade.Pale), new Bin("N-2", Shade.Deep)] };

        var mixedId = await client.EnqueueAsync(Mixed, dueTime: T0);
        var bucketsId = await client.EnqueueAsync(new BucketLines(buckets, "b-9"), dueTime: T0);
        await pump.PumpAsync(T0);

        var recorder = services.GetRequiredService<ComplexPayloadRecorder>();
        AssertEquivalent(Mixed, Assert.Single(recorder.Mixed));
        var (receivedBuckets, batch) = Assert.Single(recorder.Buckets);
        Assert.Equal("b-9", batch);
        Assert.Equal(buckets["north"], Assert.Single(receivedBuckets, pair => pair.Key == "north").Value);
        Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(mixedId))!.State);
        Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(bucketsId))!.State);
    }

    [Fact]
    public async Task UnsetJsonElementMember_EnqueuesAndReachesTheHandlerAsAJsonNull()
    {
        var (client, pump, store, recorder) = Pump();

        var id = await client.EnqueueAsync(new RawNote("n-1"), dueTime: T0);
        await pump.PumpAsync(T0);

        Assert.Equal(JsonValueKind.Null, Assert.Single(recorder.Notes).Extra.ValueKind);
        Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(id))!.State);
    }

    [Fact]
    public void UnsetImmutableArrayMembers_WriteJsonNull_AndReadBackAsTheirDefault()
    {
        var registration = Registration("tag-batch");

        // A nullable member that holds a default array writes null too, so it reads back as null.
        var bytes = registration.Serialize(new TagBatch("t-1", default) { Sizes = default(ImmutableArray<int>) });
        var decoded = (TagBatch)registration.Deserialize(bytes);

        Assert.Equal("""{"Id":"t-1","Tags":null,"Sizes":null}""", Encoding.UTF8.GetString(bytes));
        Assert.True(decoded.Tags.IsDefault);
        Assert.Null(decoded.Sizes);
    }

    [Fact]
    public void ImmutableArrayMembers_RoundTrip()
    {
        var registration = Registration("tag-batch");

        var decoded = (TagBatch)registration.Deserialize(
            registration.Serialize(new TagBatch("t-2", ["a", "b"]) { Sizes = [3] }));

        Assert.Equal(["a", "b"], decoded.Tags.ToArray());
        Assert.Equal([3], decoded.Sizes!.Value.ToArray());
    }

    [Fact]
    public void MemberThatFailsToWrite_NamesTheMemberPath_AndKeepsTheCause()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Registration("stock-shelf").Serialize(new StockShelf("s-1", new Shelf(default), null)));

        Assert.StartsWith("Could not write member $.Shelf: ", exception.Message);
        Assert.IsType<NullReferenceException>(exception.InnerException);
    }

    [Fact]
    public void JsonExceptionWhileWritingAMember_ReportsThePathFromThePayloadRoot()
    {
        var exception = Assert.Throws<JsonException>(
            () => Registration("stock-shelf").Serialize(new StockShelf("s-1", new Shelf([]), null, new Seal())));

        Assert.Equal("$.Seal", exception.Path);
        Assert.EndsWith(" Path: $.Seal.", exception.Message);
    }

    [Fact]
    public void MemberThatFailsToRead_NamesTheMemberPath_AndKeepsTheCause()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Registration("stock-shelf").Deserialize(
            Encoding.UTF8.GetBytes("""{"Id":"s-1","Shelf":{"Codes":[]},"Crate":{"Code":""}}""")));

        Assert.Equal("Could not read member $.Crate: A crate needs a code. (Parameter 'code')", exception.Message);
        Assert.IsType<ArgumentException>(exception.InnerException);
    }

    [Fact]
    public void OutOfMemoryWhileReadingAMember_IsNotWrapped()
        => Assert.Throws<OutOfMemoryException>(() => Registration("stock-shelf").Deserialize(
            Encoding.UTF8.GetBytes("""{"Id":"s-1","Shelf":{"Codes":[]},"Seal":{}}""")));

    [Fact]
    public async Task RecursiveMember_AtTheDeepestDepthThatSerializes_EnqueuesAndRuns_AndOneLevelDeeperFailsAtEnqueue()
    {
        var (client, pump, store, recorder) = Pump();

        // The default MaxDepth is 64.
        var id = await client.EnqueueAsync(new WalkLinks("w-1", Chain(64)), dueTime: T0);
        var tooDeep = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.EnqueueAsync(new WalkLinks("w-2", Chain(65)), dueTime: T0));
        await pump.PumpAsync(T0);

        Assert.StartsWith("Could not write member $.Head: ", tooDeep.Message);
        Assert.Contains("maximum allowed depth of 65", tooDeep.Message);
        Assert.Equal("w-1", Assert.Single(recorder.Walks).Id);
        Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(id))!.State);
    }

    [Fact]
    public void StoredRowDeeperThanTheLimit_FailsToDecode_AtTheMemberPath()
    {
        // A row that this codec did not write: 65 links put the deepest object one level past the limit of 65.
        var json = """{"Id":"w-3","Head":""" + string.Concat(Enumerable.Repeat("""{"Next":""", 64)) + "{}" +
            new string('}', 65);

        var exception = Assert.Throws<JsonException>(
            () => Registration("walk-links").Deserialize(Encoding.UTF8.GetBytes(json)));

        Assert.StartsWith("$.Head", exception.Path);
        Assert.Contains("maximum configured depth of 65", exception.Message);
    }

    [Fact]
    public void PolymorphicInterfaceMember_KeepsItsDerivedType()
    {
        var registration = Registration("draw");

        var bytes = registration.Serialize(new Draw("d-1", new Circle(2)));
        var decoded = (Draw)registration.Deserialize(bytes);

        Assert.Equal("""{"Id":"d-1","Shape":{"$type":"circle","Radius":2}}""", Encoding.UTF8.GetString(bytes));
        Assert.Equal(new Circle(2), decoded.Shape);
    }

    private static (BackWaveClient Client, DeterministicPump Pump, InMemoryJobStore Store, ComplexPayloadRecorder Recorder) Pump()
    {
        var services = new ServiceCollection()
            .AddSingleton<ComplexPayloadRecorder>()
            .AddTransient<IJobHandler<RawNote>, RawNoteHandler>()
            .AddTransient<IJobHandler<WalkLinks>, WalkLinksHandler>()
            .BuildServiceProvider();
        var registry = Generated.BackWaveJobs.CreateRegistry();
        var store = new InMemoryJobStore();
        var driver = new NodeDriver(new NodeOptions { WorkerId = "node-1", Policy = new Core.DispatchPolicy.Strict(["default"]) });
        return (new BackWaveClient(store, registry), new DeterministicPump(driver, store, registry, services), store,
            services.GetRequiredService<ComplexPayloadRecorder>());
    }

    private static Link Chain(int links)
    {
        var head = new Link();
        for (var i = 1; i < links; i++)
        {
            head = new Link { Next = head };
        }

        return head;
    }

    private static void AssertEquivalent(StableMixed expected, StableMixed actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.Shade, actual.Shade);
        Assert.Equal(expected.Key, actual.Key);
        Assert.Equal(expected.At, actual.At);
        Assert.Equal(expected.At.Offset, actual.At.Offset);
        Assert.Equal(expected.Rush, actual.Rush);
        Assert.Equal(expected.Skus, actual.Skus);
        Assert.Equal(expected.Counts, actual.Counts);
        Assert.Equal(expected.Bin, actual.Bin);
        Assert.Equal(expected.History, actual.History);
        Assert.Equal(expected.Slots, actual.Slots);
    }
}
