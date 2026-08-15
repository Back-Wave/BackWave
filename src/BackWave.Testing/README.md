# BackWave.Testing

Deterministic tests for your real jobs: the In-Memory Store plus **Virtual Time** behind one
harness. Enqueue real jobs, advance the clock, and assert through the same Monitor API the
dashboard uses. Every due instant — mints, retries, Lease expiries, Continuations — is
processed synchronously and in order. A year of cron behavior tests in milliseconds and
never flakes in CI.

## The full pattern: define, enqueue, advance, assert

```csharp
// 1. Define the job — the same record and handler your app ships.
[Job("send-invoice")]
public sealed record SendInvoice(string OrderId);

public sealed class SendInvoiceHandler(IInvoiceGateway gateway) : IJobHandler<SendInvoice>
{
    public Task HandleAsync(SendInvoice job, JobContext context, CancellationToken cancellationToken)
        => gateway.SendAsync(job.OrderId, cancellationToken);
}

[Fact]
public async Task InvoiceGoesOut_TwoDaysAfterTheOrder()
{
    // 2. Build the harness from your registry and DI services.
    var services = new ServiceCollection()
        .AddSingleton<IInvoiceGateway, FakeInvoiceGateway>()
        .AddTransient<IJobHandler<SendInvoice>, SendInvoiceHandler>()
        .BuildServiceProvider();
    var harness = new BackWaveHarness(BackWaveJobs.CreateRegistry(), services);

    // 3. Enqueue and advance Virtual Time — "advance 3 days" runs everything due in between.
    var jobId = await harness.EnqueueAsync(new SendInvoice("order-42"), delay: TimeSpan.FromDays(2));
    await harness.AdvanceAsync(TimeSpan.FromDays(3));

    // 4. Assert through the Monitor API — the same surface production observability uses.
    var job = await harness.Monitor.GetJobAsync(jobId);
    Assert.Equal(JobState.Succeeded, job!.State);
}
```

## What the harness covers

- **Retries**: a failing Attempt reschedules at its backoff instant; `AdvanceAsync` stops
  there and runs it. Assert `job.Attempt` afterwards.
- **Recurring Schedules**: `UpsertRecurringAsync("nightly", Cron.Daily(3), template)` then
  `AdvanceAsync(TimeSpan.FromDays(365))` mints and runs a year of ticks in milliseconds.
- **Continuations**: `EnqueueContinuationAsync(receipt, parentId)` releases when the parent
  goes terminal during an advance.
- **Transactional Enqueue**: `harness.BeginTransaction()` honors the rollback-means-it-never-
  existed guarantee, so outbox-replacement code paths are testable:

```csharp
using (var transaction = harness.BeginTransaction())
{
    await harness.EnqueueAsync(new SendInvoice("order-43"), transaction: transaction);
    transaction.Rollback(); // the job never existed — not claimable, not visible
}
```

- **Job Manifest**: commit `JobManifest.Verify(registry, "jobs.manifest")` in a test and a
  removed or renamed Wire Name fails in PR review instead of quarantining in production.

## Determinism rules

Virtual Time starts at a fixed instant (configurable via `BackWaveHarnessOptions.StartTime`)
and only moves when you advance it. No wall clock, no sleeps, no polling — the same test
input always produces the same execution order.
