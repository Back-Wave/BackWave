# BackWave.Oracle

The Oracle storage adapter for [BackWave](https://backwave.app). Durable jobs backed by a versioned
SQL schema, with `FOR UPDATE SKIP LOCKED` claims so many workers pull work without contending, and
Transactional Enqueue so a job commits atomically with your business write. Requires Oracle 19c or
later, and depends on Oracle's ODP.NET driver (`Oracle.ManagedDataAccess.Core`).

```csharp
using BackWave;
using BackWave.Generated;
using BackWave.Hosting;
using BackWave.Oracle;

builder.Services.AddBackWave(backwave =>
{
    backwave
        .UseStore(new OracleJobStore(new OracleStoreOptions
        {
            ConnectionString = builder.Configuration.GetConnectionString("BackWave")!,
            AutoMigrate = true, // the embedded schema self-applies on startup
        }))
        .UseJobs(BackWaveJobs.Module);

    backwave.AddWorkerGroup(new WorkerGroupOptions { Name = "default" });
});
```

## Notes

- **`AutoMigrate = true`** applies the embedded, versioned schema on startup. Leave it off and run the
  migration yourself if you manage schema out of band.
- **`SchemaName`** places BackWave's tables in the owning schema of your choosing (defaults to
  `backwave`).
- **Transactional Enqueue** lets you enqueue on your own `OracleTransaction`, so the job and the rows it
  depends on commit or roll back together. For EF Core unit-of-work integration, add
  **BackWave.EntityFrameworkCore**.

Requires a running BackWave host, so add **BackWave.Hosting**. Full documentation: https://backwave.app
