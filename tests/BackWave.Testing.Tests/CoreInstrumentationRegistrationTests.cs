using System.Collections.Concurrent;
using System.Diagnostics;
using BackWave.Core;
using BackWave.Jobs;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace BackWave.Testing.Tests;

/// <summary>
/// Source-name drift guard for the BackWave.OpenTelemetry package's Core registration. That package
/// holds no reference to Core - it subscribes to the Core job-lifecycle ActivitySource BY NAME - so a
/// rename of that source would silently drop every job-lifecycle span with a fully green build.
///
/// This test refuses to encode the string. It drives the real public entry point
/// <c>AddBackWaveInstrumentation()</c>, runs a real job through the harness so Core emits its
/// messaging spans, and asserts a span actually lands in the exporter. It guards the property that
/// matters - the signal arrives - so it fails correctly the moment the registered name and the emitted
/// name diverge.
/// <para>
/// An ActivitySource is process-wide, so this provider also receives the job spans of every other test
/// that runs beside it. Two consequences are designed for. The sink is a ConcurrentBag, because the SDK
/// appends on the emitting thread and a plain List faults the read with "Collection was modified". And
/// the assertion matches on this test's own TraceId, so a neighbour's send span can never stand in for
/// the one this test emitted.
/// </para>
/// </summary>
public sealed class CoreInstrumentationRegistrationTests
{
    private const string RootSourceName = "BackWave.Testing.Tests.InstrumentationDriftGuard";
    private static readonly ActivitySource RootSource = new(RootSourceName);

    [Fact]
    public async Task The_package_registration_captures_a_real_core_job_span()
    {
        var exported = new ConcurrentBag<Activity>();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddBackWaveInstrumentation()
            .AddSource(RootSourceName)
            .AddProcessor(new BagProcessor(exported))
            .Build()!;

        // Roots this test's own trace, so the spans below inherit its TraceId and are separable from
        // the identical spans the rest of the suite emits into this same provider.
        using var root = RootSource.StartActivity("drift-guard");
        Assert.NotNull(root);

        var services = new ServiceCollection()
            .AddSingleton<TraceLog>()
            .AddTransient<IJobHandler<TraceProbe>, TraceProbeHandler>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<TraceProbe, TraceProbeHandler>(
                "trace-probe", ObservabilityJsonContext.Default.TraceProbe),
        ]);
        var harness = new BackWaveHarness(registry, services, new BackWaveHarnessOptions());

        // A genuine job lifecycle: the enqueue emits the PRODUCER "send" span on the Core source, and
        // running it emits the CONSUMER "process" span.
        await harness.EnqueueAsync(new TraceProbe("drift-guard"));
        await harness.AdvanceAsync(TimeSpan.Zero);

        provider.ForceFlush();

        // If the registered name ever drifts from the Core source name, nothing lands here.
        Assert.Contains(exported, span =>
            span.TraceId == root.TraceId
            && span.OperationName == "send"
            && (string?)span.GetTagItem("messaging.system") == "backwave");
    }

    // The SDK appends from whichever thread ended the span, so the sink must tolerate a concurrent
    // add during the read. A ConcurrentBag enumerates a snapshot; the in-memory exporter's ICollection
    // contract cannot take one.
    private sealed class BagProcessor(ConcurrentBag<Activity> bag) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => bag.Add(data);
    }
}
