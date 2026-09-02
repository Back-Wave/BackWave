using System.Collections.Concurrent;
using System.Diagnostics;
using BackWave.Storage;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace BackWave.Sqlite.Tests;

/// <summary>
/// Source-name drift guard for the BackWave.OpenTelemetry package's SQLite registration. That package
/// holds no reference to this adapter - it subscribes to the adapter's ActivitySource BY NAME - so a
/// rename of the adapter's source would silently drop every SQLite store span with a fully green build.
///
/// This test refuses to encode the string. It drives the real public entry point
/// <c>AddBackWaveSqliteInstrumentation()</c>, emits a span from a genuine store round-trip, and asserts
/// the span actually lands in the exporter. It guards the property that matters - the signal arrives -
/// so it fails correctly the moment the registered name and the emitted name diverge.
/// <para>
/// An ActivitySource is process-wide, so this provider also receives the store spans of every other
/// test that runs beside it. Two consequences are designed for. The sink is a ConcurrentBag, because
/// the SDK appends on the emitting thread and a plain List faults the read with "Collection was
/// modified". And the assertion matches on this test's own TraceId, so a neighbour's enqueue span can
/// never stand in for the one this test emitted.
/// </para>
/// </summary>
public sealed class SqliteInstrumentationRegistrationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string RootSourceName = "BackWave.Sqlite.Tests.InstrumentationDriftGuard";
    private static readonly ActivitySource RootSource = new(RootSourceName);

    [Fact]
    public async Task The_package_registration_captures_a_real_sqlite_store_span()
    {
        var exported = new ConcurrentBag<Activity>();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddBackWaveSqliteInstrumentation()
            .AddSource(RootSourceName)
            .AddProcessor(new BagProcessor(exported))
            .Build()!;

        // Roots this test's own trace, so the span below inherits its TraceId and is separable
        // from the identical spans the rest of the suite emits into this same provider.
        using var root = RootSource.StartActivity("drift-guard");
        Assert.NotNull(root);

        await using var temp = TempSqliteStore.Create();
        await temp.Store.EnqueueAsync(new NewJob(Guid.NewGuid(), "drift-guard", default, "default", T0), T0);

        provider.ForceFlush();

        // If the registered name ever drifts from the adapter's own SourceName, nothing lands here.
        Assert.Contains(exported, span =>
            span.TraceId == root.TraceId
            && span.OperationName == "enqueue"
            && (string?)span.GetTagItem("db.system") == "sqlite");
    }

    // The SDK appends from whichever thread ended the span, so the sink must tolerate a concurrent
    // add during the read. A ConcurrentBag enumerates a snapshot; the in-memory exporter's ICollection
    // contract cannot take one.
    private sealed class BagProcessor(ConcurrentBag<Activity> bag) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => bag.Add(data);
    }
}
