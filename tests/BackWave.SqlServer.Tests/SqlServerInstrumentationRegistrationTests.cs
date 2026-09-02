using System.Collections.Concurrent;
using System.Diagnostics;
using BackWave.Storage;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace BackWave.SqlServer.Tests;

/// <summary>
/// Source-name drift guard for the BackWave.OpenTelemetry package's SQL Server registration. That
/// package holds no reference to this adapter - it subscribes to the adapter's ActivitySource BY NAME -
/// so a rename of the adapter's source would silently drop every SQL Server store span with a fully
/// green build.
///
/// This test refuses to encode the string. It drives the real public entry point
/// <c>AddBackWaveSqlServerInstrumentation()</c>, emits a span from a genuine store round-trip, and
/// asserts the span actually lands in the exporter. It guards the property that matters - the signal
/// arrives - so it fails correctly the moment the registered name and the emitted name diverge.
/// </summary>
[Collection("sqlserver")]
public sealed class SqlServerInstrumentationRegistrationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string RootSourceName = "BackWave.SqlServer.Tests.InstrumentationDriftGuard";
    private static readonly ActivitySource RootSource = new(RootSourceName);

    [Fact]
    public async Task The_package_registration_captures_a_real_sqlserver_store_span()
    {
        var exported = new ConcurrentBag<Activity>();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddBackWaveSqlServerInstrumentation()
            .AddSource(RootSourceName)
            .AddProcessor(new BagProcessor(exported))
            .Build()!;

        // Roots this test's own trace, so the span below inherits its TraceId and is separable
        // from the identical spans the rest of the suite emits into this same provider.
        using var root = RootSource.StartActivity("drift-guard");
        Assert.NotNull(root);

        var store = await SqlServerTestDatabase.CreateFreshStoreAsync();
        await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "drift-guard", default, "default", T0), T0);

        provider.ForceFlush();

        // If the registered name ever drifts from the adapter's own SourceName, nothing lands here.
        Assert.Contains(exported, span =>
            span.TraceId == root.TraceId
            && span.OperationName == "enqueue"
            && (string?)span.GetTagItem("db.system") == "mssql");
    }

    // The SDK appends from whichever thread ended the span, so the sink must tolerate a concurrent
    // add during the read. A ConcurrentBag enumerates a snapshot; the in-memory exporter's ICollection
    // contract cannot take one.
    private sealed class BagProcessor(ConcurrentBag<Activity> bag) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => bag.Add(data);
    }
}
