using System.Diagnostics.Metrics;
using BackWave.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace BackWave.Dashboard;

// The live-metrics spine (ADR 0032). A MeterListener over the already-emitted "BackWave" Meter
// accumulates the enqueued/processed/failed/attempts counters into a bounded ring buffer, entirely
// above the storage boundary and dashboard-process-local: no store surface, no Conformance work.
//
// Per-node and ephemeral by design — the buffer sees only the node hosting this dashboard and resets
// when the process restarts. Both facts are labelled in the panel UI. Memory is bounded regardless of
// uptime: a FIXED window of one-second buckets that self-recycle, and a FIXED per-bucket cap on
// distinct wire_names (extras fold into an "other" key), so an unbounded stream of dynamically-named
// job types can never grow the footprint.
internal sealed class DashboardMetricsCollector : IHostedService, IDisposable
{
    // Fixed window + resolution: 60 one-second buckets = a 60-second sliding window. Both are
    // compile-time constants, so the buffer's size is constant no matter how long the process runs.
    internal const int WindowSeconds = 60;

    // Per-bucket cap on distinct wire_names. Any wire_name beyond the cap folds into OtherKey, so
    // each bucket holds at most Cap+1 endpoint entries and total endpoint memory is bounded by
    // WindowSeconds * (Cap + 1). The panels additionally take top-N, so cardinality is doubly bounded.
    private const int EndpointCap = 64;

    // The counters we consume — all already emitted by the Core (READ ONLY; see BackWaveDiagnostics).
    // The sent/consumed names follow the OpenTelemetry messaging conventions the job lifecycle adopted.
    private const string EnqueuedName = "messaging.client.sent.messages";
    private const string ProcessedName = "messaging.client.consumed.messages";
    private const string FailedName = "backwave.jobs.failed";
    private const string AttemptsName = "backwave.job.attempts";

    // The one double instrument we consume: handler execution latency (ADR 0032, issue 0162). We keep
    // per-bucket COUNTS (not raw samples or a sketch), then interpolate percentiles from them — the
    // only in-process representation that merges correctly across nodes, so a future cluster-wide view
    // stays possible.
    private const string DurationName = "messaging.process.duration";

    // Explicit latency bucket boundaries in SECONDS, MIRRORING the messaging.process.duration
    // instrument's Advice (see BackWaveDiagnostics). A value falls in the first bucket it does not
    // exceed; anything above the last boundary lands in an implicit (+Inf) overflow bucket.
    private static readonly double[] DurationBoundariesSeconds =
        [0.001, 0.002, 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30];

    // Finite boundaries plus the one (+Inf) overflow slot.
    private static readonly int DurationBucketCount = DurationBoundariesSeconds.Length + 1;

    // The messaging convention's destination-template attribute carries the Wire Name (the job type).
    private const string WireNameTag = "messaging.destination.template";

    // The sentinel wire_name a bucket at its cap folds overflow into; surfaced as the "other" rollup.
    internal const string OtherKey = "(other)";

    private readonly object _gate = new();
    private readonly Bucket[] _buckets;
    private readonly TimeProvider _timeProvider;
    private readonly MeterListener _listener;

    public DashboardMetricsCollector() : this(TimeProvider.System) { }

    // TimeProvider is injectable so tests can drive the window deterministically; production uses the
    // system clock. The listener starts here so measurements are captured for the whole process
    // lifetime — even before the first dashboard request resolves the collector.
    internal DashboardMetricsCollector(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _buckets = new Bucket[WindowSeconds];
        for (var i = 0; i < _buckets.Length; i++)
        {
            _buckets[i] = new Bucket();
        }

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                // Subscribe only to the BackWave Meter — the four counters (long) plus the duration
                // histogram (double); each has its own typed callback below.
                if (instrument.Meter.Name == BackWaveDiagnostics.SourceName
                    && (IsTracked(instrument.Name) || instrument.Name == DurationName))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.SetMeasurementEventCallback<double>(OnDurationMeasurement);
        _listener.Start();
    }

    private static bool IsTracked(string name)
        => name is EnqueuedName or ProcessedName or FailedName or AttemptsName;

    // Measurement callbacks fire on arbitrary threads (whichever thread ran the job), so every buffer
    // mutation is under the lock. The work here is O(1) plus a small dictionary touch.
    private void OnMeasurement(
        Instrument instrument, long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        string? wire = null;
        foreach (var tag in tags)
        {
            if (tag.Key == WireNameTag)
            {
                wire = tag.Value as string;
                break;
            }
        }

        var second = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        lock (_gate)
        {
            var bucket = Roll(second);
            switch (instrument.Name)
            {
                case EnqueuedName:
                    bucket.Enqueued += measurement;
                    break;
                case ProcessedName:
                    bucket.Processed += measurement;
                    Endpoint(bucket, wire).Processed += measurement;
                    break;
                case FailedName:
                    bucket.Failed += measurement;
                    Endpoint(bucket, wire).Failed += measurement;
                    break;
                case AttemptsName:
                    Endpoint(bucket, wire).Attempts += measurement;
                    break;
            }
        }
    }

    // The duration histogram (double) fires here. We do not keep the raw value — we bump the count of
    // the latency bucket it falls in, per wire_name, so percentiles interpolate from bucket counts.
    private void OnDurationMeasurement(
        Instrument instrument, double measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (instrument.Name != DurationName)
        {
            return; // the only double instrument we track, but stay defensive
        }

        string? wire = null;
        foreach (var tag in tags)
        {
            if (tag.Key == WireNameTag)
            {
                wire = tag.Value as string;
                break;
            }
        }

        var index = DurationBucketIndex(measurement);
        var second = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        lock (_gate)
        {
            var bucket = Roll(second);
            Endpoint(bucket, wire).Duration[index]++;
        }
    }

    // The bucket a latency value falls in: the first boundary it does not exceed, or the (+Inf)
    // overflow bucket above the last finite boundary.
    private static int DurationBucketIndex(double valueSeconds)
    {
        for (var i = 0; i < DurationBoundariesSeconds.Length; i++)
        {
            if (valueSeconds <= DurationBoundariesSeconds[i])
            {
                return i;
            }
        }
        return DurationBoundariesSeconds.Length; // (+Inf) overflow slot
    }

    // Resolves the bucket owning this second, recycling it in place when its stamp is stale (i.e. it
    // last held a second that has now aged out of the window). This is what keeps memory bounded: the
    // same 60 buckets are reused forever.
    private Bucket Roll(long second)
    {
        var bucket = _buckets[Mod(second)];
        if (bucket.Stamp != second)
        {
            bucket.Reset(second);
        }
        return bucket;
    }

    // Per-bucket endpoint slot for a wire_name, folding overflow (and missing names) into OtherKey so
    // the bucket never exceeds EndpointCap+1 distinct keys.
    private static EndpointCounts Endpoint(Bucket bucket, string? wire)
    {
        var key = wire ?? OtherKey;
        if (!bucket.Endpoints.TryGetValue(key, out var counts))
        {
            if (bucket.Endpoints.Count >= EndpointCap)
            {
                key = OtherKey;
            }
            if (!bucket.Endpoints.TryGetValue(key, out counts))
            {
                counts = new EndpointCounts();
                bucket.Endpoints[key] = counts;
            }
        }
        return counts;
    }

    private static int Mod(long second)
    {
        var m = (int)(second % WindowSeconds);
        return m < 0 ? m + WindowSeconds : m;
    }

    /// <summary>Takes an immutable, point-in-time view of the ring buffer: the per-second series for
    /// each counter over the window, the current per-second rates, and the top/faulting endpoints.</summary>
    public MetricsSnapshot Snapshot(int topN)
    {
        var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var enqueued = new long[WindowSeconds];
        var processed = new long[WindowSeconds];
        var failed = new long[WindowSeconds];
        var endpoints = new Dictionary<string, EndpointCounts>(StringComparer.Ordinal);

        lock (_gate)
        {
            for (var i = 0; i < WindowSeconds; i++)
            {
                // Oldest slot first so the series reads left-to-right in time.
                var second = now - (WindowSeconds - 1) + i;
                var bucket = _buckets[Mod(second)];
                if (bucket.Stamp != second)
                {
                    continue; // no activity in that second (or already recycled) → zeros
                }
                enqueued[i] = bucket.Enqueued;
                processed[i] = bucket.Processed;
                failed[i] = bucket.Failed;
                foreach (var (key, counts) in bucket.Endpoints)
                {
                    if (!endpoints.TryGetValue(key, out var acc))
                    {
                        acc = new EndpointCounts();
                        endpoints[key] = acc;
                    }
                    acc.Processed += counts.Processed;
                    acc.Failed += counts.Failed;
                    acc.Attempts += counts.Attempts;
                    for (var j = 0; j < DurationBucketCount; j++)
                    {
                        acc.Duration[j] += counts.Duration[j];
                    }
                }
            }
        }

        return Build(enqueued, processed, failed, endpoints, topN);
    }

    // Shapes the raw window aggregates into the immutable snapshot the panels render, applying the
    // top-N + "other" rollup that bounds each panel's row count.
    private static MetricsSnapshot Build(
        long[] enqueued, long[] processed, long[] failed,
        Dictionary<string, EndpointCounts> endpoints, int topN)
    {
        var top = RankTop(endpoints, topN);
        var faulting = RankFaulting(endpoints, topN);
        return new MetricsSnapshot(
            enqueued, processed, failed,
            PerSecond(enqueued), PerSecond(processed), PerSecond(failed),
            top, faulting);
    }

    // Busiest job types by throughput (processed per second), top-N with the remainder summed into
    // an "other" row so the panel never grows past N+1 rows.
    private static IReadOnlyList<EndpointThroughput> RankTop(
        Dictionary<string, EndpointCounts> endpoints, int topN)
    {
        var ranked = endpoints
            .Where(e => e.Value.Processed > 0)
            .OrderByDescending(e => e.Value.Processed)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .ToList();

        var rows = new List<EndpointThroughput>(topN + 1);
        foreach (var entry in ranked.Take(topN))
        {
            rows.Add(new EndpointThroughput(
                entry.Key, PerSecond(entry.Value.Processed),
                Percentile(entry.Value.Duration, 0.95), Percentile(entry.Value.Duration, 0.99)));
        }
        var rest = ranked.Skip(topN).ToList();
        var otherProcessed = rest.Sum(e => e.Value.Processed);
        if (otherProcessed > 0)
        {
            // Percentiles for the rollup interpolate from the summed bucket counts — the correct merge,
            // since percentiles themselves cannot be summed or averaged.
            var otherDuration = SumDuration(rest);
            rows.Add(new EndpointThroughput(
                OtherKey, PerSecond(otherProcessed),
                Percentile(otherDuration, 0.95), Percentile(otherDuration, 0.99)));
        }
        return rows;
    }

    // Highest fault rate (failed ÷ attempts) among job types that actually failed, top-N with an
    // "other" row aggregating the remaining faulting types' failed and attempt counts.
    private static IReadOnlyList<EndpointFaultRate> RankFaulting(
        Dictionary<string, EndpointCounts> endpoints, int topN)
    {
        var ranked = endpoints
            .Where(e => e.Value.Failed > 0 && e.Value.Attempts > 0)
            .OrderByDescending(e => (double)e.Value.Failed / e.Value.Attempts)
            .ThenByDescending(e => e.Value.Failed)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .ToList();

        var rows = new List<EndpointFaultRate>(topN + 1);
        foreach (var entry in ranked.Take(topN))
        {
            rows.Add(new EndpointFaultRate(
                entry.Key, entry.Value.Failed, entry.Value.Attempts,
                Percentile(entry.Value.Duration, 0.95), Percentile(entry.Value.Duration, 0.99)));
        }
        var rest = ranked.Skip(topN).ToList();
        if (rest.Count > 0)
        {
            var otherDuration = SumDuration(rest);
            rows.Add(new EndpointFaultRate(
                OtherKey, rest.Sum(e => e.Value.Failed), rest.Sum(e => e.Value.Attempts),
                Percentile(otherDuration, 0.95), Percentile(otherDuration, 0.99)));
        }
        return rows;
    }

    // Sums the per-bucket latency counts of several endpoints into one histogram — how the "other"
    // rollup gets a percentile: bucket counts merge by addition, unlike the percentiles themselves.
    private static long[] SumDuration(IEnumerable<KeyValuePair<string, EndpointCounts>> entries)
    {
        var sum = new long[DurationBucketCount];
        foreach (var entry in entries)
        {
            var duration = entry.Value.Duration;
            for (var i = 0; i < sum.Length; i++)
            {
                sum[i] += duration[i];
            }
        }
        return sum;
    }

    // Interpolates a quantile (Prometheus-style) from per-bucket latency counts and returns it in
    // MILLISECONDS. The instrument records seconds (the messaging convention's unit) and the bucket
    // boundaries are in seconds, so the interpolated seconds value is scaled to milliseconds on the way
    // out - the dashboard surfaces latency in ms. Returns null when the window holds no duration samples
    // for the endpoint (so the panel shows a dash, not a fabricated 0). Within the chosen bucket the
    // value is linearly interpolated between its lower and upper boundary; a target landing in the (+Inf)
    // overflow bucket has no finite upper bound, so it reports the last finite boundary as a floor. The
    // result is therefore APPROXIMATE - error is bounded by the bucket width - and the UI labels it so.
    private const double SecondsToMillis = 1000d;

    private static double? Percentile(long[] buckets, double quantile)
    {
        long total = 0;
        for (var i = 0; i < buckets.Length; i++)
        {
            total += buckets[i];
        }
        if (total == 0)
        {
            return null;
        }

        var rank = quantile * total;
        long cumulative = 0;
        for (var i = 0; i < buckets.Length; i++)
        {
            if (buckets[i] == 0)
            {
                continue;
            }
            if (cumulative + buckets[i] >= rank)
            {
                if (i == DurationBoundariesSeconds.Length)
                {
                    // (+Inf) overflow: report the last finite boundary (seconds → ms).
                    return DurationBoundariesSeconds[^1] * SecondsToMillis;
                }
                var low = i == 0 ? 0d : DurationBoundariesSeconds[i - 1];
                var high = DurationBoundariesSeconds[i];
                return (low + (high - low) * ((rank - cumulative) / buckets[i])) * SecondsToMillis;
            }
            cumulative += buckets[i];
        }
        return DurationBoundariesSeconds[^1] * SecondsToMillis;
    }

    // A window total becomes a per-second rate by dividing across the fixed window — an honest
    // 60-second average, not an instantaneous spike.
    private static double PerSecond(IReadOnlyList<long> series)
    {
        long sum = 0;
        for (var i = 0; i < series.Count; i++)
        {
            sum += series[i];
        }
        return (double)sum / WindowSeconds;
    }

    private static double PerSecond(long total) => (double)total / WindowSeconds;

    /// <summary>Starts capturing measurements. The listener is already live from construction, so this
    /// is a no-op that exists so the host instantiates the singleton eagerly at startup.</summary>
    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Stops capturing by disposing the underlying meter listener.</summary>
    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _listener.Dispose();

    // A mutable per-second slot. Stamp is the absolute unix second it currently represents; a
    // mismatch on access means the slot has aged out and is recycled.
    private sealed class Bucket
    {
        public long Stamp = long.MinValue;
        public long Enqueued;
        public long Processed;
        public long Failed;
        public Dictionary<string, EndpointCounts> Endpoints { get; } = new(StringComparer.Ordinal);

        public void Reset(long stamp)
        {
            Stamp = stamp;
            Enqueued = 0;
            Processed = 0;
            Failed = 0;
            Endpoints.Clear();
        }
    }

    // Mutable counts per wire_name; a reference type so a dictionary lookup hands back a slot to
    // increment in place without a re-insert.
    private sealed class EndpointCounts
    {
        public long Processed;
        public long Failed;
        public long Attempts;

        // Per-bucket latency counts (one slot per boundary plus the (+Inf) overflow), from which p95/p99
        // are interpolated. Never raw samples — bucket counts are what merge correctly across nodes.
        public long[] Duration { get; } = new long[DurationBucketCount];
    }
}
