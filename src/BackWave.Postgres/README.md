# BackWave.Postgres

The Postgres storage adapter for [BackWave](https://backwave.app). Durable jobs backed by a versioned
SQL schema, with `FOR UPDATE SKIP LOCKED` claims so many workers pull work without contending, and
Transactional Enqueue so a job commits atomically with your business write.

```csharp
using BackWave;
using BackWave.Generated;
using BackWave.Hosting;
using BackWave.Postgres;

builder.Services.AddBackWave(backwave =>
{
    backwave
        .UseStore(new PostgresJobStore(new PostgresStoreOptions
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
- **`SchemaName`** places BackWave's tables in a schema of your choosing (defaults to `public`).
- **Transactional Enqueue** lets you enqueue on your own `NpgsqlTransaction` or `DbConnection`, so the
  job and the rows it depends on commit or roll back together. For EF Core unit-of-work integration,
  add **BackWave.EntityFrameworkCore**.

Requires a running BackWave host, so add **BackWave.Hosting**. Full documentation: https://backwave.app
