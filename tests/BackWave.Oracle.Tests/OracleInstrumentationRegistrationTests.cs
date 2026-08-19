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

    [Fact]
    public async Task The_package_registration_captures_a_real_oracle_store_span()
    {
        var exported = new List<Activity>();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddBackWaveOracleInstrumentation()
            .AddInMemoryExporter(exported)
            .Build()!;

        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "drift-guard", "{}"u8.ToArray(), "default", T0), T0);

        provider.ForceFlush();

        // If the registered name ever drifts from the adapter's own SourceName, nothing lands here.
        Assert.Contains(exported, span =>
            span.OperationName == "enqueue" && (string?)span.GetTagItem("db.system") == "oracle");
    }
}
