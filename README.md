# BackWave

Deterministic background jobs for .NET. Define a job as a plain method, and BackWave generates its
payload, handler, wire format, and registry at compile time - no reflection at runtime.

Full documentation, guides, and pricing: **[backwave.app](https://backwave.app)**

## Quickstart

```bash
dotnet add package BackWave.Hosting
```

Define a job as a method. The source generator turns this one signature into a typed payload, handler,
wire format, and registry entry:

```csharp
using BackWave.Jobs;

public sealed class InvoiceJobs(IInvoiceGateway gateway)
{
    [Job("send-invoice", Queue = "billing")]
    public Task SendInvoiceAsync(string orderId, JobContext context, CancellationToken ct)
        => gateway.SendAsync(orderId, ct);
}
```

Register the engine, a store, and your jobs in one call, then run worker groups in-process:

```csharp
using BackWave;
using BackWave.Generated; // BackWaveJobs.Module, emitted from your [Job] methods
using BackWave.Hosting;

builder.Services.AddBackWave(backwave =>
{
    backwave
        .UseStore(new InMemoryJobStore())   // swap for a durable adapter in production
        .UseJobs(BackWaveJobs.Module);

    backwave.AddWorkerGroup(new WorkerGroupOptions
    {
        Name = "default",
        Policy = new DispatchPolicy.Strict(["critical", "default"]),
        RetryPolicy = new RetryPolicy { MaxAttempts = 5, Backoff = _ => TimeSpan.FromSeconds(1) },
    });
});
```

Enqueue from anywhere via the injected `BackWaveClient`:

```csharp
app.MapPost("/greet", async (BackWaveClient client, string name) =>
{
    var jobId = await client.EnqueueAsync(new Greet(name), dueTime: DateTimeOffset.UtcNow);
    return Results.Ok(new { jobId });
});
```

## Packages

Everything is on NuGet. The base packages are free; the `BackWave.Pro.*` packages are commercial (free
under $1M revenue - see [License](#license)).

| Package | What it's for |
| --- | --- |
| `BackWave` | The engine: job model, Storage Contract, in-memory store, `[Job]` source generator. |
| `BackWave.Hosting` | The Shell: DI registration, in-process worker groups, fail-stop health. |
| `BackWave.Postgres` · `BackWave.SqlServer` · `BackWave.Sqlite` | Durable storage adapters. |
| `BackWave.EntityFrameworkCore` | Enqueue transactionally alongside your EF Core `SaveChanges`. |
| `BackWave.Dashboard` | Watch jobs, queues, failures, and schedules. |
| `BackWave.Testing` | Drive job behavior over virtual time with no infrastructure. |
| `BackWave.OpenTelemetry` | Traces, metrics, and logs over OpenTelemetry `messaging.*` / `db.*` conventions. |
| `BackWave.Conformance` | The Storage Contract test suite, for validating a custom adapter. |
| `BackWave.Pro` | Pro features (Workflows). Commercial. |
| `BackWave.Pro.Dashboard` · `BackWave.Pro.Mcp` | Pro dashboard surfaces and the Model Context Protocol server. Commercial. |

## License

BackWave is **source available**. Two licenses apply, by package:

- **Free packages** (`BackWave`, `BackWave.Hosting`, the storage adapters, `BackWave.EntityFrameworkCore`,
  `BackWave.Dashboard`, `BackWave.Testing`, `BackWave.OpenTelemetry`, `BackWave.Conformance`) are licensed
  under [**PolyForm Shield 1.0.0**](LICENSE.md). You may run them in production, modify them, and fork them
  **for your own use**, at any scale, for free - forever. The one thing you may not do is use BackWave to
  build a product that competes with it.

- **Pro packages** (`BackWave.Pro.*`) are licensed under the [**BackWave Pro Commercial License**](LICENSE-PRO.md).
  Development, testing, and evaluation are always free. Production use is free for organizations under **$1M**
  annual revenue (self-assessed, honor system, no key required); at or above that, a paid subscription is
  required. Every feature is identical whether you pay or not - a license affects price only. Enforcement is
  offline and soft-fail: a missing or expired key only shows an unlicensed notice, and never disables a
  feature, degrades behavior, or phones home.

## Contributing

Contributions are **invitation-only** and require a signed [Contributor License Agreement](CLA.md), which
keeps the dual-license model workable. Please **do not send unsolicited pull requests**. 
Ideas, bug reports, and feature requests are very welcome: open a [Discussion or issue](https://github.com/Back-Wave/BackWave/issues). 
See [CONTRIBUTING.md](CONTRIBUTING.md) for details.

## Security

Please report vulnerabilities privately - see [SECURITY.md](SECURITY.md). Do not open a public issue for a
security report.

---

Copyright © DeVito Digital Solutions LLC · [backwave.app](https://backwave.app) · team@backwave.app
