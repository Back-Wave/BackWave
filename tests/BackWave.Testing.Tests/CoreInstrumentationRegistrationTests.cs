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
/// </summary>
public sealed class CoreInstrumentationRegistrationTests
{
    [Fact]
    public async Task The_package_registration_captures_a_real_core_job_span()
    {
        var exported = new List<Activity>();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddBackWaveInstrumentation()
            .AddInMemoryExporter(exported)
            .Build()!;

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
            span.OperationName == "send" && (string?)span.GetTagItem("messaging.system") == "backwave");
    }
}
