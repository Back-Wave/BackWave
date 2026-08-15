# BackWave

Deterministic background jobs for .NET. This package is the engine: the job model, the Storage
Contract, the In-Memory Store, and the source generator that turns a `[Job]` method into a typed
payload, handler, wire format, and registry. It writes the serialization for you, with no reflection
at runtime.

It's deterministic by design. The same inputs always produce the same schedule, so a year of cron
behavior tests in milliseconds and never flakes.

```csharp
using BackWave.Jobs;

public sealed class InvoiceJobs(IInvoiceGateway gateway)
{
    // The generator emits the SendInvoice payload, its IJobHandler, the wire format, and the
    // registry from this one signature. The declaring class is resolved from DI, so anything you
    // inject here is available to the job body.
    [Job("send-invoice", Queue = "billing")]
    public Task SendInvoiceAsync(string orderId, JobContext context, CancellationToken ct)
        => gateway.SendAsync(orderId, ct);
}
```

## What's in the box

- **Core engine**: the sans-IO Driver, routing, retries, leases, and scheduling.
- **Storage Contract** (`IJobStore`): the seam every durable adapter implements.
- **In-Memory Store**: zero-infra and in-process, the default for tests and getting started.
- **`[Job]` source generator**: bundled in this package, feeding the compiler only.

## Running jobs

This package defines and stores jobs; to run them in an app, add a Shell:

```bash
dotnet add package BackWave.Hosting
```

Then wire up worker groups and enqueue through `BackWaveClient`. For durability beyond a single
process, add a storage adapter: **BackWave.Postgres**, **BackWave.SqlServer**, or **BackWave.Sqlite**.
To test job behavior over time without any infrastructure, add **BackWave.Testing**.

Full documentation and guides: https://backwave.app
