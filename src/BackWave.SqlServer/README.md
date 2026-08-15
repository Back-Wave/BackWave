# BackWave.SqlServer

The SQL Server storage adapter for [BackWave](https://backwave.app). Durable jobs backed by a versioned
SQL schema, with `UPDLOCK, READPAST` claims so many workers pull work without blocking each other, and
Transactional Enqueue so a job commits atomically with your business write.

```csharp
using BackWave;
using BackWave.Generated;
using BackWave.Hosting;
using BackWave.SqlServer;

builder.Services.AddBackWave(backwave =>
{
    backwave
        .UseStore(new SqlServerJobStore(new SqlServerStoreOptions
        {
            ConnectionString = builder.Configuration.GetConnectionString("BackWave")!,
            AutoMigrate = true, // the embedded schema self-applies on startup
        }))
        .UseJobs(BackWaveJobs.Module);

    backwave.AddWorkerGroup(new WorkerGroupOptions { Name = "default" });
});
```

## Notes

- **`AutoMigrate = true`** applies the embedded, versioned schema on startup. Leave it off to run the
  migration yourself.
- **`SchemaName`** places BackWave's tables in a schema of your choosing (defaults to `dbo`).
- **Transactional Enqueue** lets you enqueue on your own `SqlTransaction` or `DbTransaction`, so the job
  and the rows it depends on commit or roll back together. For EF Core unit-of-work integration, add
  **BackWave.EntityFrameworkCore**.

Requires a running BackWave host, so add **BackWave.Hosting**. Full documentation: https://backwave.app
