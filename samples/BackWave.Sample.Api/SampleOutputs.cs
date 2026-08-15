using System.Text.Json.Serialization;

namespace BackWave.Sample.Api;

// ── Job Output DTOs (ADR 0026) ─────────────────────────────────────────────────
// The opaque blobs the /workflows/job-output stages emit via JobContext.SetOutput and a
// descendant pulls via JobContext.GetDependencyOutputAsync — River's LoadDeps. Output rides the
// same JSON serializer as the payload, but the [Job] source generator hand-writes payload
// serialization and never mints a JsonTypeInfo, so the codec needs one supplied here. That is the
// idiomatic System.Text.Json source-gen context every real consumer writes for its output shapes —
// reflection-free and NativeAOT-safe. Producer shape == reader shape: the producer SetOutputs a
// DatasetSummary with SampleOutputJsonContext.Default.DatasetSummary, and the reader decodes with
// the very same JsonTypeInfo.

/// <summary>What <c>ingest</c> produced — the root's output, pulled transitively by <c>publish</c>.</summary>
public sealed record DatasetSummary(string DatasetRef, int RowCount);

/// <summary>What <c>enrich</c> produced after reading <c>ingest</c>'s output.</summary>
public sealed record EnrichedResult(int EnrichedRows, string Note);

/// <summary>What <c>score</c> produced after reading <c>ingest</c>'s output.</summary>
public sealed record ScoreResult(double Value);

/// <summary>
/// What <c>price-order</c> produced (the all-features /workflows/checkout scenario) - the order total the
/// conditional gate reads with the new typed <c>ctx.Output&lt;PriceOrder, OrderPrice&gt;()</c> accessor.
/// </summary>
public sealed record OrderPrice(int Cents);

/// <summary>
/// What <c>authorize-charge</c> produced (the /workflows/checkout scenario) - the charge id the
/// <c>refund-charge</c> compensation would reverse if the charge had not settled.
/// </summary>
public sealed record ChargeResult(string ChargeId, int Cents);

/// <summary>
/// The source-generated <see cref="JsonTypeInfo"/> source for every Job Output shape above. Listing an
/// output type here is what lets the <c>[Job]</c> generator wire its step's output codec, so a handler
/// reads and writes the output with no hand-passed serializer (reflection-free, NativeAOT-safe).
/// </summary>
[JsonSerializable(typeof(DatasetSummary))]
[JsonSerializable(typeof(EnrichedResult))]
[JsonSerializable(typeof(ScoreResult))]
[JsonSerializable(typeof(OrderPrice))]
[JsonSerializable(typeof(ChargeResult))]
internal sealed partial class SampleOutputJsonContext : JsonSerializerContext;
