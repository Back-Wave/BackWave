# BackWave.Sqlite

The SQLite storage adapter for [BackWave](https://backwave.app), the first Embedded Adapter. It runs
against a single local file, with no server or Docker required. Durable jobs for a single host, with
WAL journaling and `BEGIN IMMEDIATE` whole-writer serialization, and co-resident Transactional Enqueue.

```csharp
using BackWave;
using BackWave.Generated;
using BackWave.Hosting;
using BackWave.Sqlite;

builder.Services.AddBackWave(backwave =>
{
    backwave
        .UseStore(new SqliteJobStore(new SqliteStoreOptions
        {
            ConnectionString = "Data Source=app.db",
            AutoMigrate = true, // the embedded schema self-applies on startup
        }))
        .UseJobs(BackWaveJobs.Module);

    backwave.AddWorkerGroup(new WorkerGroupOptions { Name = "default" });
});
```

## Notes

- **Single host.** SQLite serializes writers, so this adapter targets one process: a desktop app, a
  worker box, a small service. For multi-node throughput, use **BackWave.Postgres** or **BackWave.SqlServer**.
- **Co-resident Transactional Enqueue.** Point BackWave at your *application's own* `.db` file and a job
  commits in the same transaction as your business write, atomically, with no distributed coordination.
  Give BackWave a separate file and it runs dedicated, without that guarantee.
- **`TablePrefix`** namespaces BackWave's tables when they share a file with yours.
- **`AutoMigrate = true`** applies the embedded schema on startup.

Requires a running BackWave host, so add **BackWave.Hosting**. Full documentation: https://backwave.app
