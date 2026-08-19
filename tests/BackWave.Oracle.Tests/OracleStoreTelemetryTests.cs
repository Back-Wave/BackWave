using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using BackWave.Conformance;
using BackWave.Storage;

namespace BackWave.Oracle.Tests;

/// <summary>
/// The Oracle adapter's store-telemetry surface: every store round-trip opens a CLIENT span carrying the
/// db.* attributes, and a faulted round-trip increments <c>backwave.store.faults</c> tagged transient (the
/// worker degrades-and-retries) vs terminal (the worker fail-stops). This is the signal an operator
/// reaches for during an incident, so the contract is pinned here rather than left to the reader of the
/// emit site.
///
/// Isolation: the store's Meter and ActivitySource are process-wide statics shared with every other test
/// in the process. Every test here therefore starts its own parent Activity and keeps only the spans and
/// measurements carrying that marker's TraceId - the store span nests under the ambient Activity (it is
/// deliberately never forced to a root), and the counter is recorded while that span is current, so the
/// TraceId cleanly separates this test's signal from everyone else's.
/// </summary>
[Collection("oracle")]
public sealed class OracleStoreTelemetryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // The adapter's own constant, not a copy of it - BackWave.Oracle grants this project
    // [InternalsVisibleTo], so a rename of the source breaks this test at compile time rather than
    // silently listening to a name nothing emits on.
    private static readonly string SourceName = OracleDiagnostics.SourceName;

    [Fact]
    public async Task A_successful_round_trip_emits_a_client_span_carrying_the_db_attributes()
    {
        var spans = new ConcurrentBag<Activity>();
        using var listener = ListenToStoreSpans(spans);
        using var marker = StartMarker();

        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        await store.EnqueueAsync(Job(), T0);

        var span = Assert.Single(Mine(spans, marker), s => s.OperationName == "enqueue");
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Equal("oracle", span.GetTagItem("db.system"));
        Assert.Equal("enqueue", span.GetTagItem("db.operation.name"));
        Assert.Equal("backwave.jobs", span.GetTagItem("db.collection.name"));
        // The convention's low-cardinality "{operation} {target}" display name.
        Assert.Equal("enqueue backwave.jobs", span.DisplayName);
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

        await OracleTestDatabase.CreateFreshStoreAsync();
        // A fault with nothing transient about it: the worker's fail-stop signal.
        var armed = FaultArmedStore("enqueue", _ => new FaultInjectedException("enqueue"));

        await Assert.ThrowsAsync<FaultInjectedException>(async () => await armed.EnqueueAsync(Job(), T0));

        var fault = Assert.Single(Mine(faults, marker));
        Assert.Equal(1, fault.Value);
        Assert.Equal("terminal", fault.Kind);

        var span = Assert.Single(Mine(spans, marker), s => s.OperationName == "enqueue");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(typeof(FaultInjectedException).FullName, span.GetTagItem("error.type"));
        // The db.* attributes still describe the round-trip that failed - that is what makes the span
        // usable in an incident.
        Assert.Equal("oracle", span.GetTagItem("db.system"));
        Assert.Equal("backwave.jobs", span.GetTagItem("db.collection.name"));
    }

    [Fact]
    public async Task A_timeout_counts_as_a_transient_fault()
    {
        var faults = new ConcurrentBag<(long Value, string? Kind, ActivityTraceId TraceId)>();
        using var spanListener = ListenToStoreSpans(new ConcurrentBag<Activity>());
        using var meterListener = ListenToStoreFaults(faults);
        using var marker = StartMarker();

        // A bare TimeoutException is squarely in this adapter's transient set: the worker retries rather
        // than fail-stopping, so the counter must not report it as terminal.
        await OracleTestDatabase.CreateFreshStoreAsync();
        var armed = FaultArmedStore("enqueue", _ => new TimeoutException("injected"));

        await Assert.ThrowsAsync<TimeoutException>(async () => await armed.EnqueueAsync(Job(), T0));

        var fault = Assert.Single(Mine(faults, marker));
        Assert.Equal("transient", fault.Kind);
    }

    [Fact]
    public async Task A_claim_round_trip_names_its_own_operation()
    {
        var spans = new ConcurrentBag<Activity>();
        using var listener = ListenToStoreSpans(spans);
        using var marker = StartMarker();

        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        await store.ClaimAsync(new ClaimRequest("w1", ["default"], 10, TimeSpan.FromMinutes(1), T0));

        // db.operation.name distinguishes the round-trips, so an operator can break the store's latency
        // and fault rate down per operation rather than seeing one undifferentiated blob.
        var span = Assert.Single(Mine(spans, marker), s => s.OperationName == "claim");
        Assert.Equal("claim", span.GetTagItem("db.operation.name"));
        Assert.Equal("backwave.jobs", span.GetTagItem("db.collection.name"));
        Assert.Equal("oracle", span.GetTagItem("db.system"));
    }

    // ── harness ─────────────────────────────────────────────────────────────────

    private static NewJob Job() => new(Guid.NewGuid(), "telemetry-probe", "{}"u8.ToArray(), "default", T0);

    // A second store on the same test database whose failpoint hook throws the exception the test picks -
    // the exception TYPE is the input to the adapter's transient/terminal classification.
    private static OracleJobStore FaultArmedStore(string failpoint, Func<string, Exception> fault)
        => new(new OracleStoreOptions
        {
            ConnectionString = OracleTestDatabase.ConnectionString,
            FaultHook = (name, _) => name == failpoint
                ? throw fault(name)
                : Task.CompletedTask,
        });

    // A parent span whose TraceId tags everything this test provokes. A raw Activity needs no listener to
    // start, and the store span inherits its TraceId by nesting under it.
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
}
