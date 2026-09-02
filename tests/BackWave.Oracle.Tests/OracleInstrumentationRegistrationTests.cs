using System.Collections.Concurrent;
using System.Diagnostics;
using BackWave.Storage;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace BackWave.Oracle.Tests;

/// <summary>
/// Source-name drift guard for the BackWave.OpenTelemetry package's Oracle registration. That package
/// holds no reference to this adapter - it subscribes to the adapter's ActivitySource BY NAME - so a
/// rename of the adapter's source would silently drop every Oracle store span with a fully green build.
///
/// This test refuses to encode the string. It drives the real public entry point
/// <c>AddBackWaveOracleInstrumentation()</c>, emits a span from a genuine store round-trip, and asserts
/// the span actually lands in the exporter. It guards the property that matters - the signal arrives - so
/// it fails correctly the moment the registered name and the emitted name diverge.
/// </summary>
[Collection("oracle")]
public sealed class OracleInstrumentationRegistrationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string RootSourceName = "BackWave.Oracle.Tests.InstrumentationDriftGuard";
    private static readonly ActivitySource RootSource = new(RootSourceName);

    [Fact]
    public async Task The_package_registration_captures_a_real_oracle_store_span()
    {
        var exported = new ConcurrentBag<Activity>();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddBackWaveOracleInstrumentation()
            .AddSource(RootSourceName)
            .AddProcessor(new BagProcessor(exported))
            .Build()!;

        // Roots this test's own trace, so the span below inherits its TraceId and is separable
        // from the identical spans the rest of the suite emits into this same provider.
        using var root = RootSource.StartActivity("drift-guard");
        Assert.NotNull(root);

        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "drift-guard", "{}"u8.ToArray(), "default", T0), T0);

        provider.ForceFlush();

        // If the registered name ever drifts from the adapter's own SourceName, nothing lands here.
        Assert.Contains(exported, span =>
            span.TraceId == root.TraceId
            && span.OperationName == "enqueue"
            && (string?)span.GetTagItem("db.system") == "oracle");
    }

    // The SDK appends from whichever thread ended the span, so the sink must tolerate a concurrent
    // add during the read. A ConcurrentBag enumerates a snapshot; the in-memory exporter's ICollection
    // contract cannot take one.
    private sealed class BagProcessor(ConcurrentBag<Activity> bag) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => bag.Add(data);
    }
}
