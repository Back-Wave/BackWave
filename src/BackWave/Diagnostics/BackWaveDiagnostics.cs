using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json;
using BackWave.Storage;

namespace BackWave.Diagnostics;

/// <summary>
/// BackWave's OpenTelemetry surface: one <see cref="System.Diagnostics.ActivitySource"/> and one
/// <see cref="System.Diagnostics.Metrics.Meter"/>, both named "BackWave". Built on the base class
/// library only, so spans and measurements cost nothing until you subscribe a tracer or meter
/// provider to the name. Subscribe your providers to <see cref="SourceName"/> to collect BackWave's
/// job-lifecycle telemetry.
/// </summary>
/// <remarks>
/// The job lifecycle is modelled on the OpenTelemetry <b>messaging</b> semantic conventions: a job
/// enqueue is a <c>send</c> (PRODUCER) span, a claim is a <c>receive</c> (CLIENT) span, and an
/// execution is a <c>process</c> (CONSUMER) span, all carrying <c>messaging.*</c> attributes with
/// <c>messaging.system</c> = <c>"backwave"</c>. Those <c>messaging.*</c> names are borrowed from an
/// upstream convention that is still evolving and may be renamed in a future release; only the
/// <c>backwave.*</c> names are owned by BackWave and carry its stability promise.
/// </remarks>
public static class BackWaveDiagnostics
{
    /// <summary>The single source name to subscribe your tracer provider and meter provider to.</summary>
    public const string SourceName = "BackWave";

    // The package version (MinVer's AssemblyInformationalVersion, minus any +build metadata) passed as
    // the telemetry scope version, so a collector records which BackWave build produced a signal. Same
    // technique the per-adapter sources use, kept in lockstep with them.
    private static readonly string Version = ResolveScopeVersion();

    /// <summary>The activity source BackWave emits its send, receive, and process spans on.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName, Version);

    /// <summary>The meter BackWave emits its job and observer-delivery counters and gauges on.</summary>
    public static readonly Meter Meter = new(SourceName, Version);

    private static string ResolveScopeVersion()
    {
        var informational = typeof(BackWaveDiagnostics).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return "0.0.0";
        }
        // .NET appends "+{commit-sha}" to the informational version; the scope version is the semver.
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }

    // ── Messaging semantic-convention attribute keys (borrowed, upstream-tracking) ──────────────
    // These names come from the OpenTelemetry messaging conventions; the paired VALUES BackWave picks
    // (system = "backwave", operation = send/receive/process) are ours. See the type remarks.
    private const string SystemKey = "messaging.system";
    private const string SystemValue = "backwave";
    private const string DestinationNameKey = "messaging.destination.name";        // the Queue
    private const string DestinationTemplateKey = "messaging.destination.template"; // the Wire Name (job type)
    private const string OperationNameKey = "messaging.operation.name";
    private const string OperationTypeKey = "messaging.operation.type";
    private const string MessageIdKey = "messaging.message.id";                     // the job id
    private const string ConsumerGroupKey = "messaging.consumer.group.name";        // the worker-group name
    private const string SendOperation = "send";
    private const string ReceiveOperation = "receive";
    private const string ProcessOperation = "process";

    // The opaque trace-context string a job carries from enqueue to execution is the W3C traceparent,
    // optionally followed by a newline and the tracestate. This cap keeps the encoded string inside the
    // narrowest store column (SqlServer trace_context is nvarchar(450)); a context that would overflow it
    // drops the tracestate and keeps the traceparent, so an enqueue never fails over a large tracestate.
    private const int MaxTraceContextLength = 450;

    // ── Metrics ──────────────────────────────────────────────────────────────────────────────
    // The lifecycle counters and histogram are named per the messaging conventions; instruments OTel
    // has no equivalent for (attempts, failures) stay BackWave-owned. All carry the same messaging
    // destination tags (Queue + Wire Name) so a consumer can slice throughput and latency by either.
    private static readonly Counter<long> SentMessages = Meter.CreateCounter<long>(
        "messaging.client.sent.messages", "{message}", "Jobs accepted by Enqueue.");
    private static readonly Counter<long> Attempts = Meter.CreateCounter<long>(
        "backwave.job.attempts", "{attempt}", "Attempts started - a claim is the start of an Attempt.");
    private static readonly Counter<long> ConsumedMessages = Meter.CreateCounter<long>(
        "messaging.client.consumed.messages", "{message}", "Executions that succeeded.");
    private static readonly Counter<long> JobsFailed = Meter.CreateCounter<long>(
        "backwave.jobs.failed", "{job}", "Failed Attempts — retried and Dead-Lettered alike.");

    // Handler execution latency, in SECONDS (the messaging convention's unit), with explicit buckets
    // tuned for job work (sub-millisecond handlers up to the half-minute range). Emitted at the process
    // edge for the SAME outcomes the consumed/failed counters count - success and failure - never for a
    // cooperative cancellation. net9 gained InstrumentAdvice, the API for pinning bucket boundaries;
    // net8 has no equivalent, so it falls back to the SDK's default buckets there.
    private const string ProcessDurationDescription =
        "Handler execution duration in seconds, recorded on success and failure but not on cooperative cancellation.";
#if NET9_0_OR_GREATER
    private static readonly Histogram<double> ProcessDuration = Meter.CreateHistogram<double>(
        "messaging.process.duration", "s", ProcessDurationDescription, tags: null,
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries =
                [0.001, 0.002, 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30],
        });
#else
    private static readonly Histogram<double> ProcessDuration = Meter.CreateHistogram<double>(
        "messaging.process.duration", "s", ProcessDurationDescription);
#endif

    // ── New lifecycle instruments ────────────────────────────────────────────────────────────────
    // The scheduler-signature latencies OTel's messaging conventions do not cover, a distinct dead-letter
    // count, and worker-pool saturation. Each carries the same messaging destination tags as the lifecycle
    // counters (Queue + Wire Name), computed at the Shell emit edge from the pump's injected TimeProvider -
    // so under simulation they read VIRTUAL time and never re-enter the Core.

    // Builds a seconds-valued latency histogram. net9 gained InstrumentAdvice, the API for pinning bucket
    // boundaries; net8 has no equivalent, so it falls back to the SDK's default buckets there.
    private static Histogram<double> CreateSecondsHistogram(string name, string description, double[] buckets) =>
#if NET9_0_OR_GREATER
        Meter.CreateHistogram<double>(name, "s", description, tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = buckets });
#else
        Meter.CreateHistogram<double>(name, "s", description);
#endif

    // Scheduling drift, in SECONDS: from sub-millisecond up to the hour range, since a job scheduled far
    // out and then claimed late can drift by minutes.
    private static readonly Histogram<double> ScheduleDelay = CreateSecondsHistogram(
        "backwave.schedule.delay",
        "Drift in seconds between a job's scheduled (due) time and when it actually started executing.",
        [0.001, 0.01, 0.1, 0.5, 1, 2.5, 5, 10, 30, 60, 300, 3600]);

    // Backlog pressure, in SECONDS: how long a due job waited to be claimed by a worker.
    private static readonly Histogram<double> QueueWait = CreateSecondsHistogram(
        "backwave.job.queue.wait",
        "Time in seconds a due job waited to be claimed - the backlog-pressure signal.",
        [0.001, 0.01, 0.1, 0.5, 1, 2.5, 5, 10, 30, 60, 300, 3600]);

    // Observer egress latency, in SECONDS: measured around each delivery invocation. Same job-work bucket
    // shape as the process-duration histogram.
    private static readonly Histogram<double> ObserverDispatchDuration = CreateSecondsHistogram(
        "backwave.observer.dispatch.duration",
        "Observer callback dispatch latency in seconds, measured around each delivery invocation.",
        [0.001, 0.002, 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30]);

    // Terminal-failure count, distinct from the retryable backwave.jobs.failed: a Dead-Letter is the
    // subset of failures whose retry ceiling is spent.
    private static readonly Counter<long> JobsDeadLettered = Meter.CreateCounter<long>(
        "backwave.jobs.dead_lettered", "{job}",
        "Attempts that exhausted the retry ceiling and were Dead-Lettered - a terminal subset of the failed count.");

    // Pool saturation: +1 when an execution starts, -1 when it ends, so the live value is the pump's
    // in-flight count and it returns to zero at drain. Stamped with the consumer group (like the
    // capacity gauge), so summing active over a group joins to backwave.worker.slots.capacity for headroom.
    private static readonly UpDownCounter<long> WorkerSlotsActive = Meter.CreateUpDownCounter<long>(
        "backwave.worker.slots.active", "{slot}",
        "Handler executions currently in flight in a worker group's pool; returns to zero when the pool drains.");

    private static readonly Counter<long> ObserverDeliveriesAttempted = Meter.CreateCounter<long>(
        "backwave.observer.deliveries.attempted", "{delivery}",
        "Observer callback invocations started at the delivery edge (§5.13, ADR 0017).");
    private static readonly Counter<long> ObserverDeliveriesSucceeded = Meter.CreateCounter<long>(
        "backwave.observer.deliveries.succeeded", "{delivery}",
        "Observer callbacks that returned without throwing.");
    private static readonly Counter<long> ObserverDeliveriesDeadLettered = Meter.CreateCounter<long>(
        "backwave.observer.deliveries.dead_lettered", "{delivery}",
        "Observer deliveries that exhausted their ceiling and were dead-lettered (§0077).");

#if NET9_0_OR_GREATER
    private static readonly System.Threading.Lock DepthGate = new();
#else
    // System.Threading.Lock is net9+; on net8 a plain object gives the same lock-statement semantics.
    private static readonly object DepthGate = new();
#endif
    private static readonly List<Func<IReadOnlyList<QueueStateCount>>> DepthSources = [];

    // The configured pool capacity behind the backwave.worker.slots.capacity gauge, ACCUMULATED per
    // consumer group and guarded by the same registration lock as the depth sources above. One worker
    // group runs one pump PER configured Pump off the same options, so a group with Pumps=4 / PoolSize=20
    // registers four times and its true concurrency is the sum (80). Summing here - rather than keeping
    // one entry per pump - is what a real LastValue gauge needs: four same-attribute measurements would
    // otherwise collapse to a single 20 and read as saturated at a quarter of the real capacity.
    private static readonly Dictionary<string, int> SlotCapacities = [];

    static BackWaveDiagnostics()
    {
        Meter.CreateObservableGauge(
            "backwave.queue.depth", ObserveQueueDepths, "{job}",
            "Job counts by Queue and state — the spec §5.9 depths.");
        Meter.CreateObservableGauge(
            "backwave.worker.slots.capacity", ObserveSlotCapacities, "{slot}",
            "Configured pool size - the max concurrent executions - of each worker group in this process.");
    }

    /// <summary>
    /// Registers a callback that supplies current job counts by queue and state, surfaced behind the
    /// <c>backwave.queue.depth</c> observable gauge. Dispose the returned handle to stop reporting.
    /// Register one source per store — registering two worker groups that share a store would
    /// double-count its depths.
    /// </summary>
    /// <param name="source">A callback returning the current per-queue, per-state job counts each time the gauge is observed.</param>
    /// <returns>A handle that, when disposed, removes the source so it no longer contributes to the gauge.</returns>
    public static IDisposable RegisterQueueDepthSource(Func<IReadOnlyList<QueueStateCount>> source)
    {
        lock (DepthGate)
        {
            DepthSources.Add(source);
        }
        return new DepthRegistration(source);
    }

    private static IEnumerable<Measurement<long>> ObserveQueueDepths()
    {
        Func<IReadOnlyList<QueueStateCount>>[] sources;
        lock (DepthGate)
        {
            sources = [.. DepthSources];
        }
        foreach (var source in sources)
        {
            foreach (var depth in source())
            {
                yield return new Measurement<long>(
                    depth.Count,
                    new KeyValuePair<string, object?>("backwave.queue", depth.Queue),
                    new KeyValuePair<string, object?>("backwave.state", depth.State.ToString()));
            }
        }
    }

    private sealed class DepthRegistration(Func<IReadOnlyList<QueueStateCount>> source) : IDisposable
    {
        public void Dispose()
        {
            lock (DepthGate)
            {
                DepthSources.Remove(source);
            }
        }
    }

    // The pool capacity behind the backwave.worker.slots.capacity gauge, registered by each production
    // pump with its configured PoolSize and attributed by consumer group, so a reader can compare it to
    // backwave.worker.slots.active for headroom. Each pump of a group ADDS its PoolSize to the group's
    // running total, so the gauge reports the group's whole concurrency however many pumps back it.
    // Dispose the handle to subtract that pump's contribution again (pump shutdown), dropping the group
    // once its last pump stops. The deterministic harness has no fixed pool cap, so it registers none.
    internal static IDisposable RegisterWorkerSlotCapacity(string consumerGroup, int capacity)
    {
        lock (DepthGate)
        {
            SlotCapacities[consumerGroup] = SlotCapacities.GetValueOrDefault(consumerGroup) + capacity;
        }
        return new CapacityRegistration(consumerGroup, capacity);
    }

    // Internal, not private, so a unit test can assert the accumulated per-group capacity directly -
    // without an SDK, whose LastValue aggregation would mask whether registration double-counts.
    internal static IEnumerable<Measurement<long>> ObserveSlotCapacities()
    {
        KeyValuePair<string, int>[] entries;
        lock (DepthGate)
        {
            entries = [.. SlotCapacities];
        }
        foreach (var entry in entries)
        {
            yield return new Measurement<long>(
                entry.Value, new KeyValuePair<string, object?>(ConsumerGroupKey, entry.Key));
        }
    }

    private sealed class CapacityRegistration(string consumerGroup, int capacity) : IDisposable
    {
        public void Dispose()
        {
            lock (DepthGate)
            {
                // Subtract this pump's contribution; the group leaves the gauge entirely once its last
                // pump stops, rather than lingering at a stale capacity.
                var remaining = SlotCapacities.GetValueOrDefault(consumerGroup) - capacity;
                if (remaining > 0)
                {
                    SlotCapacities[consumerGroup] = remaining;
                }
                else
                {
                    SlotCapacities.Remove(consumerGroup);
                }
            }
        }
    }

    // A workflow member's payload may carry this reserved JSON property: an array of the member's
    // parent steps' wire names, spliced in above the storage boundary by the Pro workflow builder.
    // StartProcess surfaces it as the backwave.workflow.after span tag so a reader can reconstruct the
    // DAG edges from the otherwise-flat workflow trace. Core never writes it; it only reads it back for
    // telemetry, and treats the payload as opaque bytes otherwise. The Pro envelope writes the same key.
    internal const string WorkflowAfterPayloadKey = "$backwave.workflowAfter";

    // The same reserved key as UTF-8 bytes, for the vectorized fast-path scan below. A u8 literal is a
    // compile-time constant span (no allocation), but the compiler cannot derive it from the const
    // string, so the two are kept in lockstep by a drift test rather than by construction.
    internal static ReadOnlySpan<byte> WorkflowAfterPayloadKeyUtf8 => "$backwave.workflowAfter"u8;

    // A sibling reserved property carrying the W3C trace contexts of a fan-in member's parent steps -
    // one traceparent per parent send span - so the member's process span can carry a LINK to each
    // upstream step, not just the one workflow-root context its own TraceContext holds. Written by the
    // Pro workflow envelope alongside WorkflowAfterPayloadKey; read back here at StartProcess. Opaque
    // above-boundary transport the Core never reads for scheduling.
    internal const string WorkflowAfterTracePayloadKey = "$backwave.workflowAfterTrace";

    internal static ReadOnlySpan<byte> WorkflowAfterTracePayloadKeyUtf8 => "$backwave.workflowAfterTrace"u8;

    // ── Spans ──────────────────────────────────────────────────────────────────────────────────

    // Stamps the shared messaging destination attributes onto a lifecycle span: the system, the
    // operation (send/receive/process, as both .name and .type), the Queue, the Wire Name, and the job id.
    private static void SetMessagingTags(Activity activity, string operation, string queue, string wireName, Guid jobId)
    {
        activity.SetTag(SystemKey, SystemValue);
        activity.SetTag(OperationNameKey, operation);
        activity.SetTag(OperationTypeKey, operation);
        activity.SetTag(DestinationNameKey, queue);
        activity.SetTag(DestinationTemplateKey, wireName);
        activity.SetTag(MessageIdKey, jobId.ToString());
    }

    // The same destination tags as measurement attributes, for the lifecycle counters and the duration
    // histogram, so a consumer can slice throughput and latency by Queue and by Wire Name. Returned as a
    // stack-allocated TagList (never a heap array) so the per-job recording path stays allocation-free
    // whether or not a MeterListener is subscribed - all values are strings, so there is no boxing either.
    private static TagList MessagingMeasurementTags(string wireName, string queue) =>
        new()
        {
            { SystemKey, SystemValue },
            { DestinationNameKey, queue },
            { DestinationTemplateKey, wireName },
        };

    // The destination tags plus an error.type dimension (the failing exception's type name), so failures
    // break down by exception type on the failed counter and the failed-execution duration. error.type is
    // null on the success path (no tag). Cardinality is bounded by the handler's exception surface.
    private static TagList MessagingMeasurementTags(string wireName, string queue, string? errorType)
    {
        var tags = MessagingMeasurementTags(wireName, queue);
        if (errorType is not null)
        {
            tags.Add("error.type", errorType);
        }
        return tags;
    }

    // The brief workflow-start span: a PRODUCER "send" root that marks the whole workflow enqueue. It is
    // the parent of the per-member send spans emitted alongside it, and closes as soon as the enqueue
    // returns - a start marker, not a long-lived parent that stays open while the steps run.
    internal static Activity? StartWorkflow(string? name, int memberCount, bool isAppend)
    {
        var activity = ActivitySource.StartActivity(SendOperation, ActivityKind.Producer);
        if (activity is not null)
        {
            activity.DisplayName = name is null ? "send workflow" : $"send {name}";
            activity.SetTag(SystemKey, SystemValue);
            activity.SetTag(OperationNameKey, SendOperation);
            activity.SetTag(OperationTypeKey, SendOperation);
            if (name is not null)
            {
                activity.SetTag("backwave.workflow.name", name);
            }
            activity.SetTag("backwave.workflow.member_count", memberCount);
            activity.SetTag("backwave.workflow.append", isAppend);
        }
        return activity;
    }

    // Emits one per-member "send" span, a child of the workflow-root span, and returns its trace context
    // string. That context is baked as the member's TraceContext (so the member's process span links to
    // its own creation) and handed to fan-in descendants (so their process spans link to this step). When
    // no listener is attached the span is null; fall back to the root context so correlation still holds.
    internal static string? EmitMemberSend(Activity? workflowRoot, string wireName, string queue, Guid jobId)
    {
        var parentContext = workflowRoot?.Context ?? Activity.Current?.Context ?? default;
        using var activity = ActivitySource.StartActivity(SendOperation, ActivityKind.Producer, parentContext);
        if (activity is null)
        {
            return CaptureTraceContext(workflowRoot);
        }
        activity.DisplayName = $"send {queue}";
        SetMessagingTags(activity, SendOperation, queue, wireName, jobId);
        return EncodeTraceContext(activity);
    }

    // The Enqueue span; a PRODUCER "send" whose Id becomes the job's trace-correlation context, later
    // surfaced as a LINK on the job's process span.
    internal static Activity? StartSend(string wireName, string queue, Guid jobId)
    {
        var activity = ActivitySource.StartActivity(SendOperation, ActivityKind.Producer);
        if (activity is not null)
        {
            activity.DisplayName = $"send {queue}";
            SetMessagingTags(activity, SendOperation, queue, wireName, jobId);
        }
        return activity;
    }

    internal static void RecordEnqueued(string wireName, string queue)
        => SentMessages.Add(1, MessagingMeasurementTags(wireName, queue));

    // The Claim span: a CLIENT "receive" wrapping one claim round-trip to the store, attributed to the
    // worker group that pulled the work.
    internal static Activity? StartReceive(string workerId, string consumerGroup)
    {
        var activity = ActivitySource.StartActivity(ReceiveOperation, ActivityKind.Client);
        if (activity is not null)
        {
            activity.DisplayName = ReceiveOperation;
            activity.SetTag(SystemKey, SystemValue);
            activity.SetTag(OperationNameKey, ReceiveOperation);
            activity.SetTag(OperationTypeKey, ReceiveOperation);
            activity.SetTag(ConsumerGroupKey, consumerGroup);
            activity.SetTag("backwave.worker_id", workerId);
        }
        return activity;
    }

    // Each claimed job is the start of an Attempt - count it as one - and records how long the due job
    // waited to be claimed (queue.wait, the backlog signal), measured off the pump's injected clock at the
    // claim instant. Clamped at zero: a job claimed a hair before its due instant under clock skew is on
    // time, not negatively early.
    internal static void RecordClaimed(Activity? receive, IReadOnlyList<JobRecord> jobs, DateTimeOffset claimedAt)
    {
        receive?.SetTag("backwave.claimed_count", jobs.Count);
        foreach (var job in jobs)
        {
            var tags = MessagingMeasurementTags(job.WireName, job.Queue);
            Attempts.Add(1, tags);
            var wait = claimedAt - job.DueTime;
            QueueWait.Record(wait > TimeSpan.Zero ? wait.TotalSeconds : 0d, tags);
        }
    }

    // schedule.delay: drift between a job's scheduled (due) instant and its actual execution start, read
    // off the pump's injected clock (virtual under simulation). Measured from DueTime - the scheduled time
    // - NOT from enqueue, so a job deliberately scheduled far in the future records near-zero drift when it
    // fires on time. Clamped at zero for the same skew reason as queue.wait.
    internal static void RecordScheduleDelay(JobRecord job, DateTimeOffset executionStart)
    {
        var drift = executionStart - job.DueTime;
        ScheduleDelay.Record(
            drift > TimeSpan.Zero ? drift.TotalSeconds : 0d, MessagingMeasurementTags(job.WireName, job.Queue));
    }

    // Worker-pool saturation: a slot is occupied when a handler execution starts and released when it ends
    // (success, failure, cancel, or a lost lease alike), so backwave.worker.slots.active tracks the pump's
    // in-flight count and returns to zero at drain. Carries the destination tags (Queue + Wire Name) so a
    // reader can slice saturation by job type, AND the consumer group - the SAME attribute the
    // backwave.worker.slots.capacity gauge carries - so active can be joined to capacity for headroom.
    // The deterministic harness has no group concept and registers no capacity, so it passes none and the
    // group tag is simply omitted.
    internal static void RecordWorkerSlotOccupied(JobRecord job, string? consumerGroup = null) =>
        WorkerSlotsActive.Add(1, WorkerSlotTags(job, consumerGroup));

    internal static void RecordWorkerSlotReleased(JobRecord job, string? consumerGroup = null) =>
        WorkerSlotsActive.Add(-1, WorkerSlotTags(job, consumerGroup));

    // The destination tags for a worker-slot delta, plus the consumer group when one is in scope so the
    // active up/down counter joins to the per-group capacity gauge. A null group (the deterministic
    // harness) omits the tag rather than stamping a null one.
    private static TagList WorkerSlotTags(JobRecord job, string? consumerGroup)
    {
        var tags = MessagingMeasurementTags(job.WireName, job.Queue);
        if (consumerGroup is not null)
        {
            tags.Add(ConsumerGroupKey, consumerGroup);
        }
        return tags;
    }

    // The execution span: a CONSUMER "process". The stored trace context becomes a LINK, not a hard
    // parent - the messaging model correlates a consumer to its producer(s) by link because one process
    // span may reference many creators (a batch enqueue, or a workflow fan-in). A fan-in member also
    // links to each upstream step's send context, carried in its payload envelope. No trace context and
    // no ancestors means a plain root span: never accidentally parented to whatever Activity happens to
    // be current in the pump.
    internal static Activity? StartProcess(JobRecord job, string consumerGroup)
    {
        // No ActivityListener sampling this source means StartActivity below returns null regardless, so
        // everything here is wasted on the no-tracer hot path: BuildProcessLinks does an O(payload) byte
        // scan for the fan-in trace key (plus a context parse and a list alloc), and the root-forcing
        // dance writes Activity.Current twice. Gate the whole method on the cheap listener check.
        if (!ActivitySource.HasListeners())
        {
            return null;
        }
        var links = BuildProcessLinks(job);
        // Force a root. StartActivity treats an empty parentContext as "no explicit parent" and falls
        // back to Activity.Current, so a process span opened while the pump has an ambient Activity would
        // otherwise inherit its trace. The consumer is correlated to its producer(s) by link, never by
        // parent, so clear Current across the creation, then hand Current back to the new span (matching
        // the old execute-span behaviour the callers scope around) or restore the ambient if unsampled.
        var previous = Activity.Current;
        Activity.Current = null;
        var activity = ActivitySource.StartActivity(
            ProcessOperation, ActivityKind.Consumer, parentContext: default, tags: null, links: links);
        Activity.Current = activity ?? previous;
        if (activity is not null)
        {
            activity.DisplayName = $"process {job.Queue}";
            SetMessagingTags(activity, ProcessOperation, job.Queue, job.WireName, job.JobId);
            activity.SetTag(ConsumerGroupKey, consumerGroup);
            activity.SetTag("backwave.attempt", job.Attempt);
            TrySetWorkflowAfter(activity, job.Payload);
        }
        return activity;
    }

    // Encodes an activity's W3C trace context into the opaque string stored on a job: the traceparent
    // (its Id), and the tracestate after a newline when present. Activity.Id carries the traceparent
    // alone, so without this the vendor tracestate is dropped at the enqueue hop. Returns null when there
    // is no activity to capture. A newline never appears inside a traceparent or a tracestate, so it is a
    // safe separator and the split on read is unambiguous.
    internal static string? EncodeTraceContext(Activity? activity)
    {
        if (activity?.Id is not { } traceParent)
        {
            return null;
        }
        var traceState = activity.TraceStateString;
        if (string.IsNullOrEmpty(traceState))
        {
            return traceParent;
        }
        var encoded = $"{traceParent}\n{traceState}";
        // Keep the traceparent rather than overflow the store column; tracestate is best-effort vendor data.
        return encoded.Length <= MaxTraceContextLength ? encoded : traceParent;
    }

    // The trace context to bake onto a job at enqueue: the given span when one was sampled, otherwise the
    // ambient span. Mirrors the old "activity?.Id ?? Activity.Current?.Id" fallback, now tracestate-aware.
    internal static string? CaptureTraceContext(Activity? preferred)
        => EncodeTraceContext(preferred ?? Activity.Current);

    // Parses a stored trace-context string back into an ActivityContext, restoring the tracestate that
    // EncodeTraceContext wrote after the newline. Accepts a bare traceparent too, so a context written
    // before this encoding (or by a caller that stores only a traceparent) still parses.
    internal static bool TryParseTraceContext(string? stored, out ActivityContext context)
    {
        context = default;
        if (string.IsNullOrEmpty(stored))
        {
            return false;
        }
        var newline = stored.IndexOf('\n');
        var traceParent = newline < 0 ? stored : stored[..newline];
        var traceState = newline < 0 ? null : stored[(newline + 1)..];
        return ActivityContext.TryParse(traceParent, traceState, out context);
    }

    // Builds the process span's link set: one link to the job's own creation (send) context stored in
    // TraceContext, plus one per fan-in ancestor send context baked into the payload envelope. A missing
    // or unparseable context is simply skipped - telemetry never fails an execution over a bad link.
    private static List<ActivityLink>? BuildProcessLinks(JobRecord job)
    {
        List<ActivityLink>? links = null;
        if (TryParseTraceContext(job.TraceContext, out var creation))
        {
            links = [new ActivityLink(creation)];
        }
        foreach (var ancestor in ReadAncestorTraceContexts(job.Payload))
        {
            if (TryParseTraceContext(ancestor, out var ancestorContext))
            {
                (links ??= []).Add(new ActivityLink(ancestorContext));
            }
        }
        return links;
    }

    // Reads a fan-in member's parent send contexts from the reserved payload key. Gated on a cheap byte
    // scan so a non-member job never parses. Telemetry must never break execution, so a malformed payload
    // yields no ancestors rather than throwing.
    private static IReadOnlyList<string> ReadAncestorTraceContexts(ReadOnlyMemory<byte> payload)
    {
        if (payload.Span.IndexOf(WorkflowAfterTracePayloadKeyUtf8) < 0)
        {
            return [];
        }
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(WorkflowAfterTracePayloadKey, out var traces)
                || traces.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            var contexts = new List<string>(traces.GetArrayLength());
            foreach (var element in traces.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String && element.GetString() is { } traceparent)
                {
                    contexts.Add(traceparent);
                }
            }
            return contexts;
        }
        catch (Exception)
        {
            // A payload that happens to carry the key bytes but is not the expected shape is not a fan-in
            // envelope; yield no links rather than fail the process span.
            return [];
        }
    }

    // Surfaces a workflow member's parent-step wire names (baked into its payload above the storage
    // boundary) as the backwave.workflow.after span tag, so a reader can reconstruct DAG edges from the
    // flat workflow trace. Gated on a cheap, vectorized byte scan for the reserved key: a non-member job
    // pays only that scan, never a parse. Telemetry must never break execution, so a malformed payload is
    // swallowed rather than thrown.
    private static void TrySetWorkflowAfter(Activity activity, ReadOnlyMemory<byte> payload)
    {
        if (payload.Span.IndexOf(WorkflowAfterPayloadKeyUtf8) < 0)
        {
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(WorkflowAfterPayloadKey, out var after)
                || after.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            var wireNames = new string[after.GetArrayLength()];
            var index = 0;
            foreach (var element in after.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    // A non-string element means this is not the workflow envelope's array of parent
                    // wire names; treat the whole key as a coincidental collision and skip the tag.
                    return;
                }
                wireNames[index++] = element.GetString() ?? string.Empty;
            }
            activity.SetTag("backwave.workflow.after", wireNames);
        }
        catch (Exception)
        {
            // A payload that happens to contain the key bytes but is not the expected shape is not a
            // workflow member envelope; drop the tag silently rather than fail the execution span.
            // Catch broadly: telemetry must never throw into the execution path, whatever the parse
            // failure mode.
        }
    }

    // Records an execution's outcome onto its OPEN process span and the consumed/failed counters. The
    // Shell hands in the verdict it already computed - this edge never re-derives one from the raw
    // exception, because only the Shell can tell a cancel it asked for from an OperationCanceledException
    // the handler raised on its own. A Cancelled verdict is deliberate non-execution (neither consumed
    // nor failed); every other throw, that handler-raised OCE included, is a Failure and counts as one.
    // The span is NOT stopped here: it stays open until the outcome settles (see CompleteProcess /
    // CloseProcess), so a retry-scheduled / dead-lettered / lease-lost event can still land on it.
    internal static void RecordExecuted(Activity? activity, JobRecord job, ExecutionOutcome outcome)
    {
        if (outcome.Failure is { } exception)
        {
            // Break the failure down by exception type via an error.type dimension on the counter, so
            // a thrown InvalidOperationException is queryable as such alongside the process span's tag.
            JobsFailed.Add(1, MessagingMeasurementTags(job.WireName, job.Queue, ErrorType(exception)));
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            RecordProcessException(activity, exception);
        }
        else if (outcome.IsCancelled)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
        }
        else
        {
            ConsumedMessages.Add(1, MessagingMeasurementTags(job.WireName, job.Queue));
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
    }

    // The error.type dimension's value: the exception's full type name, falling back to the short name
    // for a type that reports none. Shared by the failed counter, the duration histogram, and the span.
    private static string ErrorType(Exception exception) =>
        exception.GetType().FullName ?? exception.GetType().Name;

    // Records the failing exception onto the process span per the OTel exception convention: an
    // error.type tag plus an "exception" event carrying the type, message, and full stack. Hand-rolled
    // rather than Activity.AddException so it works identically on net8 (which lacks that API).
    private static void RecordProcessException(Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }
        var errorType = ErrorType(exception);
        activity.SetTag("error.type", errorType);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", errorType },
            { "exception.message", exception.Message },
            { "exception.stacktrace", exception.ToString() },
        }));
    }

    // Records one handler execution's latency onto the messaging.process.duration histogram, in SECONDS.
    // The Shell measures the span with the injected TimeProvider (virtual time under simulation, system
    // time in production) and passes the elapsed time here, along with the same verdict it handed
    // RecordExecuted. Sampled for the SAME outcomes that edge counts as real execution - success and
    // failure - so only a Shell-requested cancellation contributes none.
    internal static void RecordJobDuration(JobRecord job, TimeSpan duration, ExecutionOutcome outcome)
    {
        if (outcome.IsCancelled)
        {
            return;
        }
        // A failed execution's duration carries the same error.type dimension as the failed counter, so
        // latency slices by exception type; a successful execution carries no error.type tag.
        var errorType = outcome.Failure is { } exception ? ErrorType(exception) : null;
        ProcessDuration.Record(duration.TotalSeconds, MessagingMeasurementTags(job.WireName, job.Queue, errorType));
    }

    // ── Deferred process-span settlement ─────────────────────────────────────────────────────────
    // The process span stays open past handler return until the outcome settles, so its true turning
    // point - retried, or dead-lettered - reads on the span's own timeline. These helpers add that
    // event and stop the span. Stopping a span resets Activity.Current to its parent, so each helper
    // saves and restores the caller's ambient Activity to avoid disturbing an unrelated event-loop span.

    // Settles a reported outcome: on a Failure, adds retry-scheduled (a retry is due) or dead-lettered
    // (the attempt ceiling is spent, signalled by a null next-due time), then stops the span.
    internal static void CompleteProcess(Activity? activity, JobOutcome outcome, string wireName, string queue)
    {
        if (outcome is JobOutcome.Failure failure)
        {
            if (failure.NextDueTime is null)
            {
                // The attempt ceiling is spent: count the Dead-Letter (the terminal subset of the failed
                // count) at the metric, then mark the span. The counter emits even when no ActivityListener
                // is attached - metrics and traces are independent subscriptions - so it must not be gated
                // on a non-null span.
                JobsDeadLettered.Add(1, MessagingMeasurementTags(wireName, queue));
                activity?.AddEvent(new ActivityEvent("dead-lettered"));
            }
            else
            {
                activity?.AddEvent(new ActivityEvent("retry-scheduled"));
            }
        }
        CloseProcess(activity);
    }

    // Settles a lost Lease: the Attempt was abandoned (its Lease lapsed and was reclaimed), so no outcome
    // will ever report for it. Marks the span and stops it so the abandoned execution is not left open.
    internal static void RecordLeaseLost(Activity? activity)
    {
        if (activity is null)
        {
            return;
        }
        activity.AddEvent(new ActivityEvent("lease-lost"));
        CloseProcess(activity);
    }

    // Stops (and thus exports) a deferred process span without disturbing the caller's ambient Activity.
    internal static void CloseProcess(Activity? activity)
    {
        if (activity is null)
        {
            return;
        }
        var previous = Activity.Current;
        activity.Dispose();
        Activity.Current = previous;
    }

    // Observer delivery health, attributed per Observer. The Shell calls these at the edge that
    // invokes the host callback and reports outcomes; the sans-IO dispatch core stays pure and never
    // instruments. backwave.observer_id is the required attribution so per-Observer delivery health
    // is monitorable; wire_name/queue ride along from the delivery when cheaply available.
    internal static void RecordObserverDeliveryAttempted(string observerId, string? wireName = null, string? queue = null)
        => ObserverDeliveriesAttempted.Add(1, ObserverTags(observerId, wireName, queue));

    internal static void RecordObserverDeliverySucceeded(string observerId, string? wireName = null, string? queue = null)
        => ObserverDeliveriesSucceeded.Add(1, ObserverTags(observerId, wireName, queue));

    internal static void RecordObserverDeliveryDeadLettered(string observerId, string? wireName = null, string? queue = null)
        => ObserverDeliveriesDeadLettered.Add(1, ObserverTags(observerId, wireName, queue));

    // Observer egress latency: measured around each delivery invocation at the Shell edge, off the observer
    // pump's injected clock. Attributed per Observer like the delivery counters, with wire_name/queue
    // riding along from the delivery when cheaply available.
    internal static void RecordObserverDispatchDuration(
        string observerId, TimeSpan duration, string? wireName = null, string? queue = null)
        => ObserverDispatchDuration.Record(duration.TotalSeconds, ObserverTags(observerId, wireName, queue));

    private static KeyValuePair<string, object?>[] ObserverTags(string observerId, string? wireName, string? queue)
        => wireName is null && queue is null
            ? [new("backwave.observer_id", observerId)]
            : [
                new("backwave.observer_id", observerId),
                new("backwave.wire_name", wireName),
                new("backwave.queue", queue),
            ];
}
