# BackWave.Pro

Revenue-gated add-on features for [BackWave](https://backwave.app). v1 adds **Workflows**: durable DAGs
of jobs with fan-out, fan-in, dependencies, and failure propagation, orchestrated on the same engine and
storage as your regular jobs.

> **Licensing.** BackWave Pro is free to use for organizations under **$1M USD in annual revenue**. Above
> that, a license is required. Pro always soft-fails: without a valid license it still runs in full and
> logs a one-line notice, and it never blocks your app. See the included EULA for terms.

```csharp
using BackWave.Pro;

// Read the license from config (null is the fine, free-tier default).
builder.Services.AddBackWavePro(builder.Configuration["BackWave:ProLicense"]);
```

Build and enqueue a workflow through the same `BackWaveClient` you already use. Each step is an ordinary
`[Job]` payload record wearing the `IWorkflowStep` marker, referenced by its .NET type, so a mistyped or
renamed step is a compile error:

```csharp
var workflowId = await client.Workflow("order-fulfillment")
    .Then(new ValidateOrder(orderId))
    .Then(new ChargePayment(orderId))                                        // runs after validate
    .Then(new ReserveInventory(orderId), after: [typeof(ValidateOrder)])     // also after validate
    .Then(new PackShipment(orderId), after: [typeof(ChargePayment), typeof(ReserveInventory)]) // fan-in
    .Then(new NotifyBuyer(orderId))                                          // runs after pack
    .EnqueueAsync();
```

`validate` runs, then `charge` and `reserve` in parallel, then `pack` once both succeed, then `notify`.
If a node dead-letters, its on-success dependents cancel and the whole workflow projects Failed. Failure
dominates.

To see workflows in the dashboard (a Workflows tab with a live graph), add **BackWave.Pro.Dashboard**.
Full documentation and licensing details: https://backwave.app
