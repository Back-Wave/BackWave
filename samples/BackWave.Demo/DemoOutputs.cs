using System.Text.Json.Serialization;

namespace BackWave.Demo;

// ── Job Output DTOs (River's LoadDeps) ─────────────────────────────────────────
// The opaque blobs the job-output workflow stages emit via JobContext.SetOutput and a descendant
// pulls via JobContext.GetDependencyOutputAsync. Output rides the same JSON serializer as the
// payload, but the [Job] source generator hand-writes payload serialization and never mints a
// JsonTypeInfo, so the codec needs one supplied here — the idiomatic, reflection-free,
// NativeAOT-safe System.Text.Json source-gen context every real consumer writes for its outputs.

/// <summary>What <c>ingest</c> produced — the root's output, pulled transitively by <c>publish</c>.</summary>
public sealed record DatasetSummary(string DatasetRef, int RowCount);

/// <summary>What <c>enrich</c> produced after reading <c>ingest</c>'s output.</summary>
public sealed record EnrichedResult(int EnrichedRows, string Note);

/// <summary>What <c>score</c> produced after reading <c>ingest</c>'s output.</summary>
public sealed record ScoreResult(double Value);

/// <summary>
/// What <c>price-order</c> produced (the all-features checkout workflow) — the order total the conditional
/// large-order gate reads with the typed <c>ctx.Output&lt;PriceOrder, OrderPrice&gt;()</c> accessor.
/// </summary>
public sealed record OrderPrice(int Cents);

/// <summary>
/// What <c>authorize-charge</c> produced (the checkout workflow) — the charge id the <c>refund-charge</c>
/// compensation would reverse if the charge had not settled.
/// </summary>
public sealed record ChargeResult(string ChargeId, int Cents);

/// <summary>
/// The source-generated <see cref="JsonTypeInfo"/> source for every Job Output shape above. Passed
/// to <c>SetOutput</c> / <c>GetDependencyOutputAsync</c> so the path stays reflection-free.
/// </summary>
[JsonSerializable(typeof(DatasetSummary))]
[JsonSerializable(typeof(EnrichedResult))]
[JsonSerializable(typeof(ScoreResult))]
[JsonSerializable(typeof(OrderPrice))]
[JsonSerializable(typeof(ChargeResult))]
internal sealed partial class DemoOutputJsonContext : JsonSerializerContext;
