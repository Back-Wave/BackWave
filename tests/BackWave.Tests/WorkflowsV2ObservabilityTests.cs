using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;
using BackWave.Diagnostics;
using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

/// <summary>
/// Workflows v2 span-per-step observability, rebased onto the OpenTelemetry messaging conventions (issue
/// 0249): a workflow start opens one brief PRODUCER <c>send</c> root span, and each member gets its own
/// PRODUCER <c>send</c> span beneath it whose context is baked into the member's <c>TraceContext</c> at
/// atomic enqueue. Each member's <c>process</c> span (a CONSUMER root in its own trace) then carries an
/// <c>ActivityLink</c> back to its own send span, and a fan-in member links to every upstream step's send
/// span as well. Member process spans also carry a <c>backwave.workflow.after</c> tag listing their parent
/// step wire names so a reader can reconstruct the DAG from the linked traces.
/// </summary>
public class WorkflowsV2ObservabilityTests
{
    // Steps, handlers, and the JSON context are defined once in WorkflowsV2BuilderTests (same namespace).
    private static BackWaveHarness NewHarness()
    {
        var services = new ServiceCollection()
            .AddSingleton<V2Recorder>()
            .AddTransient<IJobHandler<ChargeStep>, ChargeStepHandler>()
            .AddTransient<IJobHandler<ReceiptStep>, ReceiptStepHandler>()
            .AddTransient<IJobHandler<NotifyStep>, NotifyStepHandler>()
            .AddTransient<IJobHandler<CloseStep>, CloseStepHandler>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<ChargeStep, ChargeStepHandler>("v2-charge", WorkflowsV2JsonContext.Default.ChargeStep),
            JobRegistration.Create<ReceiptStep, ReceiptStepHandler>("v2-receipt", WorkflowsV2JsonContext.Default.ReceiptStep),
            JobRegistration.Create<NotifyStep, NotifyStepHandler>("v2-notify", WorkflowsV2JsonContext.Default.NotifyStep),
            JobRegistration.Create<CloseStep, CloseStepHandler>("v2-close", WorkflowsV2JsonContext.Default.CloseStep),
        ]);
        return new BackWaveHarness(registry, services, new BackWaveHarnessOptions
        {
            RetryPolicy = new Core.RetryPolicy { MaxAttempts = 1 },
        });
    }

    private static ActivityListener ListenToBackWave(ConcurrentBag<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BackWaveDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    // ── AC1: the ambient trace context is baked into every member at build ──────────────

    [Fact]
    public void Build_BakesTheAmbientTraceContext_IntoEveryMember()
    {
        using var listener = ListenToBackWave([]);
        var h = NewHarness();

        using var root = BackWaveDiagnostics.ActivitySource.StartActivity("test-root");
        Assert.NotNull(root);
        var def = h.Client.Workflow("flow")
            .Then(new ChargeStep("o"))
            .Then(new ReceiptStep("o"))
            .Build();

        Assert.All(def.Members, m => Assert.Equal(root!.Id, m.TraceContext));
    }

    // ── AC2: StartWorkflow emits a brief PRODUCER "send" root span ──────────────────────

    [Fact]
    public async Task StartWorkflow_EmitsABriefProducerSendRootSpan()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = ListenToBackWave(stopped);
        var h = NewHarness();

        // A per-test ambient trace isolates this run's spans from other test classes that share the
        // process-global BackWave source and run in parallel.
        using var outer = BackWaveDiagnostics.ActivitySource.StartActivity("test-outer");
        await h.Client.Workflow("checkout")
            .Then(new ChargeStep("o"))
            .Then(new ReceiptStep("o"))
            .EnqueueAsync();

        var root = WorkflowRoot(stopped, outer!);
        Assert.Equal("send", root.OperationName);
        Assert.Equal(ActivityKind.Producer, root.Kind);
        Assert.Equal("checkout", root.GetTagItem("backwave.workflow.name"));
        Assert.Equal(2, root.GetTagItem("backwave.workflow.member_count"));

        // Each member also emitted its own send span beneath the root.
        var memberSends = MemberSends(stopped, outer!);
        Assert.Equal(2, memberSends.Count);
        Assert.All(memberSends, s => Assert.Equal(root.SpanId, s.ParentSpanId));
    }

    // ── AC4: each member's process span links back to its own send span ─────────────────

    [Fact]
    public async Task Workflow_LinksEachMemberProcessSpanToItsOwnSendSpan()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = ListenToBackWave(stopped);
        var h = NewHarness();

        using var outer = BackWaveDiagnostics.ActivitySource.StartActivity("test-outer");
        await h.Client.Workflow()
            .Then(new ChargeStep("o"))
            .Then(new ReceiptStep("o"), after: [typeof(ChargeStep)])
            .Then(new NotifyStep("o"), after: [typeof(ChargeStep)])
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        var root = WorkflowRoot(stopped, outer!);
        var memberSends = MemberSends(stopped, outer!).ToDictionary(MessageId);
        var processes = LinkedProcesses(stopped, outer!);
        Assert.Equal(3, processes.Count);

        // Each process span is a CONSUMER root in its own trace, linked to the member send that carries
        // its job id - the messaging model's producer→consumer correlation.
        Assert.All(processes, p =>
        {
            Assert.Equal(ActivityKind.Consumer, p.Kind);
            Assert.NotEqual(outer!.TraceId, p.TraceId);
            var ownSend = memberSends[MessageId(p)];
            Assert.Equal(root.SpanId, ownSend.ParentSpanId);
            Assert.Contains(p.Links, l =>
                l.Context.TraceId == ownSend.TraceId && l.Context.SpanId == ownSend.SpanId);
        });
    }

    // ── AC (fan-in): a fan-in member's process span links to every upstream step ─────────

    [Fact]
    public async Task FanInMemberProcessSpan_LinksToEachUpstreamStepSendSpan()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = ListenToBackWave(stopped);
        var h = NewHarness();

        using var outer = BackWaveDiagnostics.ActivitySource.StartActivity("test-outer");
        await h.Client.Workflow()
            .Then(new ChargeStep("o"))
            .Then(new ReceiptStep("o"))                                                    // after charge
            .Then(new CloseStep("o"), after: [typeof(ChargeStep), typeof(ReceiptStep)])    // fan-in
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        var sendsByWire = MemberSends(stopped, outer!).ToDictionary(DestinationTemplate);
        var closeProcess = Assert.Single(
            LinkedProcesses(stopped, outer!), p => DestinationTemplate(p) == "v2-close");

        // The fan-in step links to its OWN send span plus each parent step's send span - so a reader can
        // walk from the join back to every upstream branch, not just one.
        foreach (var wire in new[] { "v2-charge", "v2-receipt", "v2-close" })
        {
            var send = sendsByWire[wire];
            Assert.Contains(closeProcess.Links, l =>
                l.Context.TraceId == send.TraceId && l.Context.SpanId == send.SpanId);
        }
    }

    // ── AC3: member process spans carry backwave.workflow.after listing parent wire names ─

    [Fact]
    public async Task MemberSpans_CarryTheAfterTag_ListingParentWireNames()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = ListenToBackWave(stopped);
        var h = NewHarness();

        using var outer = BackWaveDiagnostics.ActivitySource.StartActivity("test-outer");
        await h.Client.Workflow()
            .Then(new ChargeStep("o"))
            .Then(new ReceiptStep("o"))                                                    // after charge
            .Then(new CloseStep("o"), after: [typeof(ChargeStep), typeof(ReceiptStep)])    // fan-in
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        var byWire = LinkedProcesses(stopped, outer!).ToDictionary(DestinationTemplate);

        // A root step has no parents, so no after tag at all.
        Assert.Null(byWire["v2-charge"].GetTagItem("backwave.workflow.after"));
        // A linear child names its single parent.
        Assert.Equal(new[] { "v2-charge" }, byWire["v2-receipt"].GetTagItem("backwave.workflow.after"));
        // A fan-in names every parent.
        Assert.Equal(
            new HashSet<string> { "v2-charge", "v2-receipt" },
            ((string[])byWire["v2-close"].GetTagItem("backwave.workflow.after")!).ToHashSet());
    }

    // ── AC1 end-to-end: the baked context survives the enqueue→execute gap ──────────────

    [Fact]
    public async Task StartWorkflow_MemberProcessSpansLinkIntoTheWorkflowRootTrace()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = ListenToBackWave(stopped);
        var h = NewHarness();

        using var outer = BackWaveDiagnostics.ActivitySource.StartActivity("test-outer");
        await h.Client.StartWorkflow<CheckoutWorkflow, V2Seed>(
            new V2Seed("order-7"), WorkflowsV2JsonContext.Default.V2Seed);
        await h.AdvanceAsync(TimeSpan.Zero);

        WorkflowRoot(stopped, outer!); // the one workflow-start send root exists
        var processes = LinkedProcesses(stopped, outer!);
        Assert.Equal(2, processes.Count);
        Assert.All(processes, p => Assert.Contains(p.Links, l => l.Context.TraceId == outer!.TraceId));
    }

    // ── Settlement events: the deferred process span records its true turning point ──────

    [Fact]
    public void CompleteProcess_RecordsRetryScheduled_WhenARetryIsDue()
    {
        using var listener = ListenToBackWave([]);
        using var activity = BackWaveDiagnostics.StartProcess(SampleJob(), "backwave");
        Assert.NotNull(activity);

        // A Failure carrying a next-due time is a scheduled retry.
        var retry = new JobOutcome.Failure(DateTimeOffset.UnixEpoch.AddMinutes(5), "boom");
        BackWaveDiagnostics.CompleteProcess(activity, retry, "v2-charge", "default");

        Assert.Contains(activity!.Events, e => e.Name == "retry-scheduled");
        Assert.DoesNotContain(activity.Events, e => e.Name == "dead-lettered");
    }

    [Fact]
    public void CompleteProcess_RecordsDeadLettered_WhenNoRetryIsDue()
    {
        using var listener = ListenToBackWave([]);
        using var activity = BackWaveDiagnostics.StartProcess(SampleJob(), "backwave");
        Assert.NotNull(activity);

        // A Failure with no next-due time means the attempt ceiling is spent - a dead-letter.
        var dead = new JobOutcome.Failure(NextDueTime: null, "boom");
        BackWaveDiagnostics.CompleteProcess(activity, dead, "v2-charge", "default");

        Assert.Contains(activity!.Events, e => e.Name == "dead-lettered");
        Assert.DoesNotContain(activity.Events, e => e.Name == "retry-scheduled");
    }

    [Fact]
    public void RecordLeaseLost_RecordsTheLeaseLostEvent_AndRestoresAmbientActivity()
    {
        using var listener = ListenToBackWave([]);
        using var ambient = BackWaveDiagnostics.ActivitySource.StartActivity("ambient");
        var process = BackWaveDiagnostics.StartProcess(SampleJob(), "backwave");
        Assert.NotNull(process);
        // The pump opens the deferred process span, then restores the ambient Activity so the span never
        // leaks as current into the drive loop; the abandon path settles it later from that ambient state.
        Activity.Current = ambient;

        BackWaveDiagnostics.RecordLeaseLost(process);

        Assert.Contains(process!.Events, e => e.Name == "lease-lost");
        // Settling the deferred span must not disturb the caller's ambient Activity.
        Assert.Equal(ambient, Activity.Current);
    }

    private static JobRecord SampleJob() => new()
    {
        JobId = Guid.NewGuid(),
        WireName = "v2-charge",
        Payload = "{}"u8.ToArray(),
        Queue = "default",
        State = JobState.Leased,
        DueTime = DateTimeOffset.UnixEpoch,
    };

    // ── Drift guards: the raw-byte fast-path scans must track their string constants ─────

    [Fact]
    public void WorkflowAfterKey_ScanBytes_MatchTheStringConst()
    {
        // The fast-path byte scan uses a u8 literal the compiler cannot derive from the string const,
        // so pin the two together: a rename of one that misses the other would silently stop the scan
        // from ever finding the key it looks up by string.
        Assert.True(
            BackWaveDiagnostics.WorkflowAfterPayloadKeyUtf8.SequenceEqual(
                System.Text.Encoding.UTF8.GetBytes(BackWaveDiagnostics.WorkflowAfterPayloadKey)));
    }

    [Fact]
    public void WorkflowAfterTraceKey_ScanBytes_MatchTheStringConst()
    {
        // Same lockstep guard for the sibling fan-in trace-context key.
        Assert.True(
            BackWaveDiagnostics.WorkflowAfterTracePayloadKeyUtf8.SequenceEqual(
                System.Text.Encoding.UTF8.GetBytes(BackWaveDiagnostics.WorkflowAfterTracePayloadKey)));
    }

    // ── Hostile payloads: a coincidental workflowAfter key must never break execution ────

    [Fact]
    public void StartProcess_ToleratesANonStringWorkflowAfterElement()
    {
        using var listener = ListenToBackWave([]);

        // A payload that carries the reserved key but with numbers where the workflow envelope
        // puts parent wire names. GetString() on a number element throws InvalidOperationException
        // (not JsonException), so this must be shape-checked, not just caught.
        var job = new JobRecord
        {
            JobId = Guid.NewGuid(),
            WireName = "colliding",
            Payload = """{"$backwave.workflowAfter":[1,2,3]}"""u8.ToArray(),
            Queue = "default",
            State = JobState.Leased,
            DueTime = DateTimeOffset.UnixEpoch,
        };

        using var activity = BackWaveDiagnostics.StartProcess(job, "backwave");

        Assert.NotNull(activity);
        Assert.Null(activity!.GetTagItem("backwave.workflow.after"));
    }

    [Fact]
    public async Task Execution_Succeeds_WhenAPayloadCoincidentallyCarriesANonStringWorkflowAfterArray()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = ListenToBackWave(stopped);
        var recorder = new V2Recorder();
        var services = new ServiceCollection()
            .AddSingleton(recorder)
            .AddTransient<IJobHandler<CollidingPayload>, CollidingPayloadHandler>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<CollidingPayload, CollidingPayloadHandler>(
                "colliding", CollidingJsonContext.Default.CollidingPayload),
        ]);
        var h = new BackWaveHarness(registry, services);

        using var outer = BackWaveDiagnostics.ActivitySource.StartActivity("test-outer");
        await h.EnqueueAsync(new CollidingPayload([1, 2]));
        await h.AdvanceAsync(TimeSpan.Zero);

        // The job runs to completion (telemetry never wedges execution) and the coincidental key
        // yields no after tag.
        Assert.Equal(["colliding"], recorder.Ran);
        var process = Assert.Single(LinkedProcesses(stopped, outer!));
        Assert.Equal(ActivityStatusCode.Ok, process.Status);
        Assert.Null(process.GetTagItem("backwave.workflow.after"));
    }

    // ── Span-filtering helpers ───────────────────────────────────────────────────────────

    // The workflow-root and per-member send spans share this test's own ambient trace; filter to them so
    // a process-global listener never mixes in spans from other test classes running in parallel.
    private static List<Activity> Sends(ConcurrentBag<Activity> stopped, Activity outer)
        => stopped.Where(a => a.TraceId == outer.TraceId && a.OperationName == "send").ToList();

    // The one workflow-start send root - the only send carrying the workflow member-count tag.
    private static Activity WorkflowRoot(ConcurrentBag<Activity> stopped, Activity outer)
        => Assert.Single(Sends(stopped, outer), a => a.GetTagItem("backwave.workflow.member_count") is not null);

    // The per-member send spans (every send under the root except the root marker itself).
    private static List<Activity> MemberSends(ConcurrentBag<Activity> stopped, Activity outer)
        => Sends(stopped, outer).Where(a => a.GetTagItem("backwave.workflow.member_count") is null).ToList();

    // A member's process span is a root in its OWN trace, so it cannot be filtered by trace id; it belongs
    // to this test when one of its ActivityLinks points back into this test's ambient send trace.
    private static List<Activity> LinkedProcesses(ConcurrentBag<Activity> stopped, Activity outer)
        => stopped
            .Where(a => a.OperationName == "process" && a.Links.Any(l => l.Context.TraceId == outer.TraceId))
            .ToList();

    private static string MessageId(Activity a) => (string)a.GetTagItem("messaging.message.id")!;

    private static string DestinationTemplate(Activity a) => (string)a.GetTagItem("messaging.destination.template")!;
}

// A user payload whose own JSON property collides with the reserved workflow-envelope key, carrying
// numbers where the envelope puts parent wire-name strings.
public sealed record CollidingPayload(
    [property: JsonPropertyName("$backwave.workflowAfter")] int[] Numbers);

public sealed class CollidingPayloadHandler(V2Recorder recorder) : IJobHandler<CollidingPayload>
{
    public Task HandleAsync(CollidingPayload job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("colliding");
        return Task.CompletedTask;
    }
}

[JsonSerializable(typeof(CollidingPayload))]
internal sealed partial class CollidingJsonContext : JsonSerializerContext;
