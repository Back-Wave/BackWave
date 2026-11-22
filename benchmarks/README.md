# Benchmark harness

`BackWave.Benchmarks` is a console app that runs a job stream through a real scheduler against a real
database and writes one JSON result. It measures throughput, enqueue latency, end-to-end latency, peak
database connections, and CPU. It is not a test, and `dotnet test` never runs it.

Local runs on a laptop are indicative only. A publishable number comes only from `--mode official` on the
pinned native x86-64 instance in [cloud/aws-official-run.md](cloud/aws-official-run.md).

## Targets

Every Postgres target uses its own database on the `postgres` service of the repository's
`docker-compose.yml` (port 5499). The harness creates the database when it is missing. Set the environment
variable to use another server.

| `--target` | System | DSN variable | Default database |
|---|---|---|---|
| `postgres` | BackWave | `BACKWAVE_POSTGRES_DSN` | `backwave_test` |
| `sqlserver` | BackWave | `BACKWAVE_SQLSERVER_DSN` | |
| `oracle` | BackWave | `BACKWAVE_ORACLE_DSN` | |
| `hangfire-postgres` | Hangfire 1.8.25, Hangfire.PostgreSql 1.21.1 | `BACKWAVE_HANGFIRE_POSTGRES_DSN` | `hangfire_test` |
| `hangfire-sqlserver` | Hangfire 1.8.25 | `BACKWAVE_HANGFIRE_SQLSERVER_DSN` | |
| `quartz-postgres` | Quartz.NET 4.7.3, ADO.NET job store, clustered | `BACKWAVE_QUARTZ_POSTGRES_DSN` | `quartz_test` |
| `tickerq-postgres` | TickerQ 10.4.0 | `BACKWAVE_TICKERQ_POSTGRES_DSN` | `tickerq_test` |
| `jobmaster-postgres` | JobMaster 0.0.11-alpha | `BACKWAVE_JOBMASTER_POSTGRES_DSN` | `jobmaster_test` |

TickerQ and JobMaster run in their own host processes, `BackWave.Benchmarks.TickerQHost` and
`BackWave.Benchmarks.JobMasterHost`, so that their package graphs stay apart from the harness. A build of
the harness also builds both hosts and copies them next to it. The harness talks to each host over stdin and
stdout.

Each run starts a fresh host process, so the JIT and the EF Core query caches of TickerQ and JobMaster are
cold in every measured run. Warmup runs do not warm them.

TickerQ stays on 10.4.0. Version 10.5.0 and later refuse to run jobs without a license certificate.

## Run one cell

```sh
docker compose up -d --wait postgres
dotnet run -c Release --project benchmarks/BackWave.Benchmarks -- \
	--target quartz-postgres --mode local --arrival drain --delay-ms 0 --jobs 20000
```

The usage comment at the top of [BackWave.Benchmarks/Program.cs](BackWave.Benchmarks/Program.cs) lists
every option.

Every system gets BackWave's default pool of run slots: four per core, at least 8. That is 40 on a
10-core host. JobMaster rounds its slots up to a multiple of 5, so it gets more on some hosts, for example
50 against 48 on 12 cores. End-to-end latency starts at the enqueue call, for every system. It stops at the
completion instant the system records: the handler's return for TickerQ and Quartz.NET, the success state
for Hangfire and JobMaster, and the batched outcome write for BackWave, which comes later than the handler's
return. After each run, a work audit checks three things:

- The number of succeeded jobs equals the number of enqueued jobs.
- Each handler ran exactly once.
- The first and the last completion are inside the timed window.

The harness prints the audit and records it in the result JSON. A failed audit makes the result
unpublishable. The Quartz.NET and TickerQ targets stop the cell instead when they see lost or duplicated work.

## Run the five-way battery

[blog/run-blog.sh](blog/run-blog.sh) runs the local five-way comparison: BackWave in two shapes, Hangfire,
Quartz.NET, TickerQ, and JobMaster, all on one Postgres server. The script header describes the workloads
and the fairness controls. The script uses `top` and `sysctl` from macOS.

```sh
docker compose up -d --wait postgres
OUT_DIR=benchmarks/results/my-run ROUNDS=5 bash benchmarks/blog/run-blog.sh
```

The script skips cells that already have a result, so you can stop it and start it again. A cell that fails
leaves a `.failed` marker next to its log, and a restart skips it too. Set `RETRY_FAILED=1` to run the
marked cells again. At the end,
[blog/summarize.py](blog/summarize.py) writes `summary.md` into `OUT_DIR`, with the median and range of each
cell across rounds. It leaves out a round that failed, whose audit failed, or that ran with a
diagnostic variable engaged, and says so next to the system name. A paced round counts as carried when the
producers delivered 98% of the offered rate and the system completed jobs at 98% of it too, not counting one
median end-to-end latency at the end of the window. `benchmarks/results/` is in `.gitignore`.

## Tuning and diagnostic variables

Every variable is optional. The default is the setting that the battery uses. A diagnostic variable is
recorded in the result's `DiagnosticKnobs` and makes the result unpublishable.

| Variable | Default | Effect |
|---|---|---|
| `BACKWAVE_BENCH_HANGFIRE_POLL_MS` | `200` | Hangfire `QueuePollInterval` |
| `BACKWAVE_BENCH_HANGFIRE_LONG_POLLING` | off | `1` turns on Hangfire.PostgreSql long polling |
| `BACKWAVE_QUARTZ_CLUSTERED` | `true` | Quartz clustered job store. `false` is diagnostic, because the store is then not safe across nodes |
| `BACKWAVE_QUARTZ_MAX_BATCH_SIZE` | slot count | Quartz `MaxBatchSize` |
| `BACKWAVE_QUARTZ_IDLE_WAIT_MS` | `1000` | Quartz `IdleWaitTime` |
| `BACKWAVE_QUARTZ_FIRE_AHEAD_MS` | `0` | Quartz batch trigger acquisition fire-ahead window |
| `BACKWAVE_QUARTZ_ACQUIRE_WITHIN_LOCK` | `false` | Quartz `AcquireTriggersWithinLock` |
| `BACKWAVE_QUARTZ_LOCK_ON_INSERT` | `false` | Quartz `LockOnInsert` |
| `BACKWAVE_QUARTZ_LOCK_FREE_FIRING` | `true` | Quartz lock-free firing rounds |
| `BACKWAVE_QUARTZ_ENQUEUE_SHAPE` | `one-off` | `job-per-enqueue` stores one job and one trigger per enqueue |
| `BACKWAVE_TICKERQ_MAX_CONCURRENCY` | slot count | TickerQ concurrency per function |
| `BACKWAVE_TICKERQ_FALLBACK_MS` | `10` | TickerQ fallback scan period |
| `BACKWAVE_TICKERQ_MIN_POLL_MS` | `0` | TickerQ minimum scheduler wait |
| `BACKWAVE_JOBMASTER_BUCKETS` | `1` | JobMaster bucket count |
| `BACKWAVE_JOBMASTER_PARALLELISM_FACTOR` | slot count / 5, rounded up | JobMaster run slots per bucket, divided by 5 |
| `BACKWAVE_JOBMASTER_BUCKET_BUFFER` | `1000` | Jobs JobMaster takes into memory per bucket on each 3 s tick |
| `BACKWAVE_BENCH_DB_TRACE` | off | A file path. Writes one CSV row for each Npgsql and BackWave Postgres span. Diagnostic, because it slows every call |
