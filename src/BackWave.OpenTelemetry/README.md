# BackWave.OpenTelemetry

One-call OpenTelemetry registration for [BackWave](https://backwave.app). BackWave's Core and Hosting
packages stay BCL-only and emit their traces and metrics on plain `ActivitySource`/`Meter` names, so you
pay nothing until you subscribe. This package is that subscription: `AddBackWaveInstrumentation()` wires
the Core job-lifecycle telemetry onto a `TracerProviderBuilder`/`MeterProviderBuilder`, and per-adapter
methods opt in to the storage-adapter store spans.

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddBackWaveInstrumentation()          // Core job spans: send, receive, process
        .AddBackWavePostgresInstrumentation()  // opt in to the Postgres store spans
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddBackWaveInstrumentation()          // Core job metrics: throughput, latency, depth, saturation
        .AddBackWavePostgresInstrumentation()  // opt in to the Postgres store-fault meter
        .AddOtlpExporter());
```

## Notes

- **One call for the Core surface.** `AddBackWaveInstrumentation()` on both the tracer and meter builder
  subscribes every Core job-lifecycle signal - the enqueue/claim/execute spans, and the throughput,
  latency, queue-depth, worker-saturation, and observer-delivery instruments.
- **Store spans are opt-in, per adapter.** `AddBackWavePostgresInstrumentation()`,
  `AddBackWaveSqlServerInstrumentation()`, and `AddBackWaveSqliteInstrumentation()` each add exactly one
  adapter's store round-trip spans and store-fault meter. A consumer who does not opt in gets no store
  spans, so the (chattier) database signals never crowd the job-lifecycle view unasked.
- **By-name subscription, minimal dependencies.** This package references only the OpenTelemetry API, not
  the BackWave Core or adapter assemblies. Opting into an adapter subscribes its source by name; it does
  not drag that adapter's assembly into your build.
- **Conventions.** Job spans follow the OpenTelemetry *messaging* semantic conventions; adapter store
  spans follow the *database* conventions. Every source carries its BackWave package version as the
  instrumentation-scope version.

Full documentation: https://backwave.app
