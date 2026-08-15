# BackWave.Hosting

The Hosting Shell for [BackWave](https://backwave.app). It handles DI registration, in-process Worker
Groups, and fail-stop health, so this is how you actually run jobs in an ASP.NET Core or Generic Host
app.

```csharp
using BackWave;
using BackWave.Generated; // BackWaveJobs.Module, emitted from your [Job] methods
using BackWave.Hosting;

builder.Services.AddBackWave(backwave =>
{
    backwave
        .UseStore(new InMemoryJobStore())   // swap for a durable adapter in production
        .UseJobs(BackWaveJobs.Module);      // registers the registry + a scoped handler per [Job]

    backwave.AddWorkerGroup(new WorkerGroupOptions
    {
        Name = "default",
        Policy = new DispatchPolicy.Strict(["critical", "default"]),
        PollInterval = TimeSpan.FromMilliseconds(250),
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

## What it adds

- **`AddBackWave(...)`** registers the engine, the store, and your jobs in one call.
- **Worker Groups** are hosted services with per-group dispatch (Strict priority or smooth Weighted),
  poll interval, concurrency, and retry policy. Run several in one process, or scale them out.
- **Per-Attempt DI scope** gives each execution its own scope, so your handlers inject normally.
- **Fail-stop health** reports unhealthy when the engine can't make progress.

Pair with a storage adapter (**BackWave.Postgres**, **BackWave.SqlServer**, **BackWave.Sqlite**) for
durability, and **BackWave.Dashboard** to watch jobs, queues, failures, and schedules.

Full documentation: https://backwave.app
