using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using BackWave.Conformance;
using BackWave.Storage;
using Microsoft.Data.Sqlite;

namespace BackWave.Sqlite.Tests;

/// <summary>
/// The SQLite adapter's store-telemetry surface: every store round-trip opens a CLIENT span carrying
/// the db.* attributes, and a faulted round-trip increments <c>backwave.store.faults</c> tagged
/// transient (the worker degrades-and-retries) vs terminal (the worker fail-stops). This is the signal
/// an operator reaches for during an incident, so the contract is pinned here rather than left to the
/// reader of the emit site.
///
/// Isolation: the store's Meter and ActivitySource are process-wide statics, and sibling test classes
/// run in parallel against the same ones. Every test here therefore starts its own parent Activity and
/// keeps only the spans and measurements carrying that marker's TraceId - the store span nests under
/// the ambient Activity (it is deliberately never forced to a root), and the counter is recorded while
/// that span is current, so the TraceId cleanly separates this test's signal from everyone else's.
/// </summary>
public sealed class SqliteStoreTelemetryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // The adapter's own constant, not a copy of it - BackWave.Sqlite grants this project
    // [InternalsVisibleTo], so a rename of the source breaks this test at compile time rather than
    // silently listening to a name nothing emits on.
    private static readonly string SourceName = SqliteDiagnostics.SourceName;

    [Fact]
    public async Task A_successful_round_trip_emits_a_client_span_carrying_the_db_attributes()
    {
        var spans = new ConcurrentBag<Activity>();
        using var listener = ListenToStoreSpans(spans);
        using var marker = StartMarker();

        await using var temp = TempSqliteStore.Create();
        await temp.Store.EnqueueAsync(Job(), T0);

        var span = Assert.Single(Mine(spans, marker), s => s.OperationName == "enqueue");
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Equal("sqlite", span.GetTagItem("db.system"));
        Assert.Equal("enqueue", span.GetTagItem("db.operation.name"));
        Assert.Equal("backwave_jobs", span.GetTagItem("db.collection.name"));
        // The convention's low-cardinality "{operation} {target}" display name.
        Assert.Equal("enqueue backwave_jobs", span.DisplayName);
        // A clean round-trip is not an error and carries no error.type.
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Null(span.GetTagItem("error.type"));
    }

    [Fact]
    public async Task A_faulted_round_trip_counts_a_terminal_fault_and_marks_the_span_in_error()
    {
        var spans = new ConcurrentBag<Activity>();
        var faults = new ConcurrentBag<(long Value, string? Kind, ActivityTraceId TraceId)>();
        using var spanListener = ListenToStoreSpans(spans);
        using var meterListener = ListenToStoreFaults(faults);
        using var marker = StartMarker();

        // A fault with nothing transient about it: the worker's fail-stop signal.
        await using var temp = FaultArmedStore("enqueue", _ => new FaultInjectedException("enqueue"));

        await Assert.ThrowsAsync<FaultInjectedException>(async () => await temp.Store.EnqueueAsync(Job(), T0));

        var fault = Assert.Single(Mine(faults, marker));
        Assert.Equal(1, fault.Value);
        Assert.Equal("terminal", fault.Kind);

        var span = Assert.Single(Mine(spans, marker), s => s.OperationName == "enqueue");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(typeof(FaultInjectedException).FullName, span.GetTagItem("error.type"));
        // The db.* attributes still describe the round-trip that failed - that is what makes the span
        // usable in an incident.
        Assert.Equal("sqlite", span.GetTagItem("db.system"));
        Assert.Equal("backwave_jobs", span.GetTagItem("db.collection.name"));
    }

    /// <summary>
    /// SQLITE_BUSY (5) and SQLITE_LOCKED (6) are residual write-lock contention that survived the
    /// busy-timeout: transient, never an invariant violation. This adapter recognises them itself
    /// because the provider does not raise them through <c>DbException.IsTransient</c>, so a generic
    /// classifier would mis-tag them terminal and send an operator hunting a bug that is really load.
    /// </summary>
    [Theory]
    [InlineData(5, "transient")]  // SQLITE_BUSY
    [InlineData(6, "transient")]  // SQLITE_LOCKED
    [InlineData(19, "terminal")]  // SQLITE_CONSTRAINT - a real invariant violation, not contention
    [InlineData(1, "terminal")]   // SQLITE_ERROR
    public async Task The_fault_kind_tag_follows_the_adapters_busy_locked_recognition(
        int sqliteErrorCode, string expectedKind)
    {
        var faults = new ConcurrentBag<(long Value, string? Kind, ActivityTraceId TraceId)>();
        using var spanListener = ListenToStoreSpans(new ConcurrentBag<Activity>());
        using var meterListener = ListenToStoreFaults(faults);
        using var marker = StartMarker();

        await using var temp = FaultArmedStore(
            "enqueue", _ => new SqliteException("injected", sqliteErrorCode));

        await Assert.ThrowsAsync<SqliteException>(async () => await temp.Store.EnqueueAsync(Job(), T0));

        var fault = Assert.Single(Mine(faults, marker));
        Assert.Equal(expectedKind, fault.Kind);
    }

    [Fact]
    public async Task A_busy_fault_wrapped_at_depth_still_counts_as_transient()
    {
        var faults = new ConcurrentBag<(long Value, string? Kind, ActivityTraceId TraceId)>();
        using var spanListener = ListenToStoreSpans(new ConcurrentBag<Activity>());
        using var meterListener = ListenToStoreFaults(faults);
        using var marker = StartMarker();

        // Microsoft.Data.Sqlite sometimes nests the provider error inside another exception; the
        // recognition walks the inner chain, so the tag must not depend on the fault being top-level.
        await using var temp = FaultArmedStore(
            "enqueue", _ => new InvalidOperationException("wrapped", new SqliteException("busy", 5)));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await temp.Store.EnqueueAsync(Job(), T0));

        var fault = Assert.Single(Mine(faults, marker));
        Assert.Equal("transient", fault.Kind);
    }

    [Fact]
    public async Task A_custom_table_prefix_rides_on_the_collection_attribute()
    {
        var spans = new ConcurrentBag<Activity>();
        using var listener = ListenToStoreSpans(spans);
        using var marker = StartMarker();

        await using var temp = TempSqliteStore.Create(tablePrefix: "custom");
        await temp.Store.EnqueueAsync(Job(), T0);

        // db.collection.name must name the table the round-trip actually touched, not the default.
        var span = Assert.Single(Mine(spans, marker), s => s.OperationName == "enqueue");
        Assert.Equal("custom_jobs", span.GetTagItem("db.collection.name"));
        Assert.Equal("enqueue custom_jobs", span.DisplayName);
    }

    // ── harness ─────────────────────────────────────────────────────────────────

    private static NewJob Job() => new(Guid.NewGuid(), "telemetry-probe", default, "default", T0);

    // A parent span whose TraceId tags everything this test provokes. A raw Activity needs no listener
    // to start, and the store span inherits its TraceId by nesting under it.
    private static Activity StartMarker()
    {
        var marker = new Activity("store-telemetry-test");
        marker.Start();
        return marker;
    }

    private static IEnumerable<Activity> Mine(IEnumerable<Activity> spans, Activity marker)
        => spans.Where(s => s.TraceId == marker.TraceId);

    private static IEnumerable<(long Value, string? Kind, ActivityTraceId TraceId)> Mine(
        IEnumerable<(long Value, string? Kind, ActivityTraceId TraceId)> faults, Activity marker)
        => faults.Where(f => f.TraceId == marker.TraceId);

    private static ActivityListener ListenToStoreSpans(ConcurrentBag<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    // The counter is recorded while the store span is current, so Activity.Current inside the callback
    // carries the TraceId that attributes the measurement to the test that provoked it.
    private static MeterListener ListenToStoreFaults(
        ConcurrentBag<(long Value, string? Kind, ActivityTraceId TraceId)> faults)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == SourceName && instrument.Name == "backwave.store.faults")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var kind = tags.ToArray()
                .FirstOrDefault(t => t.Key == "backwave.store.fault_kind").Value as string;
            faults.Add((value, kind, Activity.Current?.TraceId ?? default));
        });
        listener.Start();
        return listener;
    }

    private static TempSqliteStore FaultArmedStore(string failpoint, Func<string, Exception> fault)
        => TempSqliteStore.CreateFaultArmed(failpoint, fault);
}
