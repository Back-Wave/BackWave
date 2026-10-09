using System.Text;
using System.Text.Json;
using BackWave.AotConsumer;

var mixed = new Mixed(
    "order-1",
    3,
    Shade.Deep,
    ["sku-1", "sku-2"],
    new Dictionary<string, int> { ["a"] = 1 },
    new Bin("bin-7", Shade.Pale),
    [new Bin("bin-1", Shade.Deep)],
    [4, 5],
    new Link { Next = new Link() },
    JsonDocument.Parse("""{"k":[1,true]}""").RootElement,
    JsonDocument.Parse("""["m",2]""").RootElement);
var mixedBytes = MixedWire.Serialize(mixed);
RequireJson(
    """{"Id":"order-1","Count":3,"Shade":"Deep","Skus":["sku-1","sku-2"],"Counts":{"a":1}""" +
    ""","Bin":{"BinCode":"bin-7","Shade":0},"History":[{"BinCode":"bin-1","Shade":1}],"Slots":[4,5]""" +
    ""","Head":{"Next":{"Next":null}},"Raw":{"k":[1,true]},"MaybeRaw":["m",2]}""",
    mixedBytes,
    "Mixed");
var mixedBack = MixedWire.Deserialize(mixedBytes);
Require(mixedBack.Head.Next is not null, "Mixed.Head lost its Next link");
Require(mixedBack.History.Single().BinCode == "bin-1", "Mixed.History did not round-trip");
Require(mixedBytes.AsSpan().SequenceEqual(MixedWire.Serialize(mixedBack)), "Mixed did not round-trip");

// The null path: Bin and MaybeRaw are null.
var sparse = mixed with { Bin = null, MaybeRaw = null };
var sparseBytes = MixedWire.Serialize(sparse);
RequireJson(
    """{"Id":"order-1","Count":3,"Shade":"Deep","Skus":["sku-1","sku-2"],"Counts":{"a":1},"Bin":null""" +
    ""","History":[{"BinCode":"bin-1","Shade":1}],"Slots":[4,5],"Head":{"Next":{"Next":null}}""" +
    ""","Raw":{"k":[1,true]},"MaybeRaw":null}""",
    sparseBytes,
    "Mixed with null members");
var sparseBack = MixedWire.Deserialize(sparseBytes);
Require(sparseBack.Bin is null && sparseBack.MaybeRaw is null, "Mixed did not read its null members back as null");
Require(sparseBytes.AsSpan().SequenceEqual(MixedWire.Serialize(sparseBack)), "Mixed with null members did not round-trip");

var bucket = new Bucket(
    new Dictionary<string, List<Bin>> { ["north"] = [new Bin("bin-2", Shade.Pale)] },
    "batch-1");
var bucketBytes = BucketWire.Serialize(bucket);
RequireJson(
    """{"Buckets":{"north":[{"BinCode":"bin-2","Shade":0}]},"Batch":"batch-1"}""",
    bucketBytes,
    "Bucket");
var bucketBack = BucketWire.Deserialize(bucketBytes);
Require(bucketBack.Buckets["north"].Single().Shade == Shade.Pale, "Bucket.Buckets did not round-trip");
Require(bucketBytes.AsSpan().SequenceEqual(BucketWire.Serialize(bucketBack)), "Bucket did not round-trip");

Console.WriteLine("Every job round-tripped through its generated codec.");
return 0;

static void Require(bool condition, string failure)
{
    if (!condition)
    {
        throw new InvalidOperationException(failure);
    }
}

// Compares the first write with a literal, because a member that is lost on write is lost from every write.
static void RequireJson(string expected, byte[] actual, string job)
{
    var json = Encoding.UTF8.GetString(actual);
    Require(json == expected, $"{job} wrote unexpected JSON.\nExpected: {expected}\nActual:   {json}");
}
