# BackWave.Sample.Api

A **development-only** ASP.NET Core minimal API that wires up BackWave the way a real consumer
would — `AddBackWave(...)`, two in-process Worker Groups, the Dashboard, and an endpoint per
scenario so you can exercise setup and runtime behavior by hand in a realistic host.

> This is a playground, never a shipped artifact. It sets `<IsPackable>false</IsPackable>`, lives
> under `samples/` (not `src/`), and is never referenced by any library project. CI builds it on
> every PR to keep it honest as the library evolves, but `dotnet pack` produces no package for it.

## Run it

```bash
# From this folder. Zero infra — uses the In-Memory store.
dotnet run
```

Then open:

- **Swagger UI** — <http://localhost:5xxx/swagger> — every scenario endpoint, clickable.
- **Dashboard** — <http://localhost:5xxx/backwave> — read-only job/queue/schedule visibility.
- **MCP endpoint** — <http://localhost:5xxx/backwave-mcp> — the BackWave Pro MCP server for AI
  agents; see [MCP server (BackWave Pro)](#mcp-server-backwave-pro) below.

(The port is printed at startup. `GET /` redirects to Swagger.)

## Switching stores

Storage is chosen by the `BackWave:Store` config key: `InMemory` (default), `Sqlite`,
`SqliteDedicated`, `Postgres`, or `SqlServer`.

| Store             | Infra needed           | Transactional Enqueue (`POST /tx`)         |
| ----------------- | ---------------------- | ------------------------------------------ |
| `InMemory`        | none — F5-and-go       | returns **409** with guidance              |
| `Sqlite`          | none — a local file    | ✅ commits/rolls back atomically (co-resident) |
| `SqliteDedicated` | none — a local file    | returns **409** (BackWave on its own file) |
| `Postgres`        | `docker compose up -d` | ✅ commits/rolls back atomically           |
| `SqlServer`       | `docker compose up -d` | ✅ commits/rolls back atomically           |

The **SQLite Embedded Adapter** needs no server — just a local `.db` file (written under the content
root). Two deployments demonstrate the spectrum:

- **`Sqlite` (co-resident)** — BackWave's `backwave_*` tables live in the application's *own* database
  file alongside `sample_business`, so `POST /tx` commits a job atomically with a business write with
  no outbox. A startup same-file guard verifies the EF connection and BackWave point at the same file.
- **`SqliteDedicated`** — BackWave on its own file, business data in a separate file. The two files
  cannot share a transaction, so this deployment deliberately forgoes Transactional Enqueue (`POST /tx`
  answers 409) — jobs still run normally.

This sample ships its **own** `docker-compose.yml` on distinct ports + DB name
(`5398` / `backwave_sample` for Postgres, `14331` for SQL Server) so it never collides with the
repo-root compose (`5499` / `backwave_test`). Every adapter runs with `AutoMigrate = true`, so the
embedded schema self-applies on startup — no manual migration step.

```bash
# SQLite — no infra, headline Transactional Enqueue on a local file
BackWave__Store=Sqlite dotnet run
# SQLite, dedicated file (forgoes POST /tx)
BackWave__Store=SqliteDedicated dotnet run

# Postgres
docker compose up -d postgres
BackWave__Store=Postgres dotnet run

# SQL Server (amd64 image; slow under Rosetta on Apple Silicon)
docker compose up -d sqlserver
BackWave__Store=SqlServer dotnet run
```

Connection strings default to the compose values and can be overridden via
`BackWave:Postgres:ConnectionString` / `BackWave:SqlServer:ConnectionString`; the SQLite file paths
via `BackWave:Sqlite:DataSource` (and `BackWave:Sqlite:BusinessDataSource` for the dedicated business
file) — see `appsettings.json`.

## Scenario endpoints

**Enqueue** (`/jobs`)
- `POST /jobs/enqueue?name=` — immediate enqueue (`greet`, queue `critical`).
- `POST /jobs/delayed?name=&seconds=` — delayed / scheduled job.
- `POST /jobs/strict-burst?count=` — Strict dispatch: `critical` preempts `bulk`.
- `POST /jobs/weighted-burst?count=` — Weighted dispatch: 6:1 across `high`/`low`.
- `POST /jobs/concurrency-burst?count=` — Concurrency Limit: `limited` caps at 1 cluster-wide.
- `POST /jobs/flaky?label=` — always fails → retries → **Dead-Lettered**.
- `POST /jobs/order-notification?fail=` — posts a structured JSON document (`order-notification`,
  queue `low`) with a pre-filled Swagger example. View `/backwave/jobs/{id}` for the payload card and
  transition timeline; `?fail=true` drives it to **Dead-Lettered** with captured Failure Detail.
  Doubles as the **Transition Observer** proof-out: the dummy `SlackObserver` (registered via
  `backwave.AddObservers(...)`) pretends to post to Slack but really logs **one structured console
  line** per terminal delivery, carrying the transition metadata *and* fields read from the payload
  **body** (`orderRef`, `customerEmail`). Success → a Slack line on the **Succeeded** transition;
  `?fail=true` → a Slack line on the **Dead-Lettered** terminal transition.
- `POST /jobs/quarantine` — enqueues an unregistered Wire Name → **Quarantined**.
- `POST /jobs/tagged-report?tenant=&amount=&priority=` — **Job Tags** showcase (`tagged-report`,
  queue `bulk`). One job collects Tags from all three sources, both kinds (bare **Label** vs **Keyed**
  Tag): **type-default Labels** `billing`+`report` (declared on `[Job(Labels = ...)]`),
  **enqueue-time** `tenant=<tenant>` (Keyed) plus an optional `priority` Label, and **runtime** Tags
  the handler adds via `context.AddLabel(...)` / `context.AddTag(...)` (`processed` + a computed
  `amount-band`). They union (set semantics) onto the one job — see them as pills at
  `/backwave/jobs/{id}`, then filter and facet via the Monitor endpoints below.

**Dependencies** (`/dependencies`)
- `POST /dependencies/on-success?name=` — child released only when the parent Succeeds.
- `POST /dependencies/on-any-terminal?name=` — child released whatever the parent's terminal state.

**Workflows** (`/workflows`) — a named group of jobs wired into a DAG by Dependency edges and
enqueued atomically (ADR 0023). Each renders as a graph at `/backwave/workflows/{id}`.
- `POST /workflows/order-fulfillment?orderRef=&amount=&itemCount=&fail=` — a realistic **diamond +
  tail** pipeline: `validate ─┬─> charge ─┐` / `└─> reserve ─┴─> pack ──> notify`. `validate` fans
  out to `charge` + `reserve` (parallel), which fan back in to `pack`, then `notify` (which also
  trips the Slack Observer). `?fail=true` makes `charge` Dead-Letter, so its on-success dependents
  `pack` and `notify` Cancel and the Workflow projects **Failed** (failure dominates).
- `POST /workflows/fan-out-fan-in?label=` — the canonical `a → {b1, b2} → c` shape, for direct
  comparison with River's workflow example: `a` runs, then `b1`/`b2` in parallel, then `c` after both.
- `POST /workflows/job-output?datasetRef=` — **Job Output** (ADR 0026, River's `LoadDeps`): a diamond
  `ingest ─┬─> enrich ─┐` / `└─> score ─┴─> publish` where each stage emits an opaque blob via
  `JobContext.SetOutput` and `publish` **pulls** its direct parents `enrich` + `score` **and**,
  transitively, their shared grandparent `ingest` — via `JobContext.GetDependencyOutputAsync<T>`.
  Pull, never push: BackWave never injects a parent's output into a child's args. Each stage's output
  shows on its Job detail page at `/backwave/jobs/{id}` behind the **ViewSensitiveData** permission.

**Recurring** (`/schedules`)
- `POST /schedules/recurring` · `POST /schedules/no-overlap` · `POST /schedules/catch-up`
- `DELETE /schedules/{id}` · `GET /schedules`

**Operator Actions** (`/ops`)
- `POST /ops/jobs/{id}/requeue` · `POST /ops/jobs/{id}/cancel`
- `POST /ops/queues/{queue}/pause` · `POST /ops/queues/{queue}/resume`
- `POST /ops/schedules/{id}/trigger`
- `POST /ops/workflows/{id}/cancel` — cancel a whole Workflow (fans the per-job Cancel out over its
  non-terminal members; reuses the Cancel permission).

**Monitor** (`/monitor`)
- `GET /monitor/jobs/{id}` · `GET /monitor/jobs?state=&queue=` · `GET /monitor/queues`
- `GET /monitor/workflows` — every Workflow with its derived status + member count.
- `GET /monitor/workflows/{id}` — one Workflow's graph: members, structural Dependency edges, status.
- `GET /monitor/tagged?tenant=&label=` — filter jobs by Tags; predicates **AND** (a Keyed
  `tenant=` and/or a `label=` Label). OR is out of scope — run two queries.
- `GET /monitor/facet?key=` — group jobs by one tag dimension: `?key=tenant` (or `amount-band`)
  for per-value counts, or `?key=` (empty) to facet **Labels**.

**Transactional Enqueue**
- `POST /tx?fail=false` commits a business row and a job atomically; `?fail=true` rolls both back.
  Relational stores only — `409` on In-Memory.

## MCP server (BackWave Pro)

The sample registers **BackWave.Pro.Mcp** (`backwave.AddMcp(...)` inside the `AddBackWave` block)
and mounts it at **<http://localhost:5283/backwave-mcp>** (`app.UseBackWaveProMcp()`). Out of the
box only the read tools would appear — writes and sensitive data are default-deny — but, like the
dashboard block, this dev sample opts into **all** the permissions so the full 23-tool surface is
exercisable by hand. MCP writes are stamped into the audit trail as actor `sample-mcp`
(the dashboard's are `sample-operator`).

**Connect a real MCP client** — e.g. Claude Code — and just ask:

```bash
claude mcp add --transport http backwave-sample http://localhost:5283/backwave-mcp
```

Then prompts like *"Which BackWave queues have jobs right now, and is anything dead-lettered?"* or
*"Pause the bulk queue"* drive `get_queue_depths` / `search_jobs` / `pause_queue` for you.

**Or drive it raw with curl.** The endpoint speaks MCP streamable HTTP, stateless: each JSON-RPC
request is its own POST (send `Accept: application/json, text/event-stream`), and every response
is **SSE-framed** (`Content-Type: text/event-stream`, the JSON-RPC response on a `data:` line) —
even for single-shot POSTs. With `dotnet run` going in another terminal:

```bash
MCP=http://localhost:5283/backwave-mcp
H=(-H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream')

# 0. The tool surface (23 tools here, because the sample grants every permission).
curl -sN "${H[@]}" -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}' $MCP

# 1. A read — seed a job, then read queue depths over MCP.
curl -s -X POST 'http://localhost:5283/jobs/enqueue?name=Ada'
curl -sN "${H[@]}" -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_queue_depths","arguments":{}}}' $MCP

# 2. A granted write — pause the bulk queue, then resume it.
curl -sN "${H[@]}" -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"pause_queue","arguments":{"queue":"bulk"}}}' $MCP
curl -sN "${H[@]}" -d '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"resume_queue","arguments":{"queue":"bulk"}}}' $MCP
# ...both writes land in the queue's audit trail as actor "sample-mcp":
curl -sN "${H[@]}" -d '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"list_audit_records","arguments":{"target":"bulk"}}}' $MCP

# 3. A workflow tool — enqueue the fan-out/fan-in workflow, then read its graph over MCP.
curl -s -X POST 'http://localhost:5283/workflows/fan-out-fan-in?label=mcp-demo'
curl -sN "${H[@]}" -d '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"list_workflows","arguments":{}}}' $MCP
# ...take a workflowId from that result to drill into members + dependency edges:
curl -sN "${H[@]}" -d '{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"get_workflow","arguments":{"workflow_id":"<workflowId>"}}}' $MCP
```

A response looks like this — one SSE `message` event carrying the JSON-RPC response (step 2's
pause, abbreviated):

```
event: message
data: {"jsonrpc":"2.0","id":3,"result":{"content":[...],"structuredContent":{"queue":"bulk","paused":true},"isError":false}}
```

`cancel_job` / `cancel_workflow` / `trigger_schedule` / `set_concurrency_limit` and the sensitive
`get_job_payload` / `get_job_output` are all granted here too — same `tools/call` shape. To see the
**environment kill-switch** in action, restart with `BACKWAVE_MCP_DISABLE_SENSITIVE_DATA=1
dotnet run`: the two sensitive tools vanish from `tools/list` (and direct calls error) even though
the sample's code grants the permission.

## How it's wired

- **Jobs** are declared with `[Job("wire-name")]` on methods in [`SampleJobs.cs`](./SampleJobs.cs);
  the source generator emits the payload records, handlers, wire formats, and `BackWaveJobs.Module`.
  A single `.UseJobs(BackWaveJobs.Module)` registers the Job Registry, a scoped handler per `[Job]`,
  and the scoped `SampleJobs` class that declares them — no hand-written registration to keep in sync
  with the registry.
- **Lifetime model** (ADR 0021): the class that declares `[Job]` methods is a *per-Attempt unit of
  work* — registered **scoped**, so it can hold a scoped `DbContext` for an idempotent dedup write,
  disposed when the Attempt ends. Anything genuinely process-wide does **not** live on that class; it
  lives in a **singleton it injects** — here `ConcurrencyTracker`, whose cluster-wide high-water mark
  must outlive any one Attempt (the one line still registered by hand in `Program.cs`).
- **Two Worker Groups** run in-process as hosted services (one Strict, one Weighted) — see
  [`Program.cs`](./Program.cs). No separate worker host.
- **A Transition Observer** ([`SlackObserver.cs`](./SlackObserver.cs)) is registered through
  `backwave.AddObservers(...)` in [`Program.cs`](./Program.cs) and runs on its own in-process pump
  alongside the Worker Groups. It watches the `order-notification` job's terminal transitions and
  logs the dummy Slack line described above.
- **Transactional Enqueue** uses an EF Core `DbContext` ([`SampleDbContext.cs`](./SampleDbContext.cs))
  over the same database, riding the application's own transaction.

## Observability

`Program.cs` includes the **canonical OpenTelemetry wiring** a consumer copies: a full
`AddOpenTelemetry()` block with `WithTracing` / `WithMetrics` / `WithLogging`, subscribing BackWave's
signals via `AddBackWaveInstrumentation()` (from the `BackWave.OpenTelemetry` package) plus the
per-adapter opt-in matching the configured store (see [`StoreKind.cs`](./StoreKind.cs)). It exports over
**both** the console exporter (always on) and **OTLP** (lit up when `OTEL_EXPORTER_OTLP_ENDPOINT` is set),
with **trace-id exemplars** enabled on the histograms. The full guide - conventions, exporters, exemplars,
and the .NET Aspire dashboard - is in [`docs/observability.md`](../../docs/observability.md), backed by
[ADR 0049](../../docs/adr/0049-telemetry-adopts-otel-messaging-and-db-conventions.md).

**See it on the console** (zero infra):

```bash
# Short metric export interval just so the histograms flush quickly for a demo.
OTEL_METRIC_EXPORT_INTERVAL=2000 dotnet run
# in another terminal:
curl -s -X POST 'http://localhost:5283/jobs/enqueue?name=Ada'
```

The terminal prints the `process` CONSUMER span (tagged `messaging.system=backwave`,
`messaging.destination.template=greet`, `messaging.message.id=...`), the `messaging.process.duration`
histogram carrying an `Exemplars` line with a `TraceId`/`SpanId`, and the catalogued lifecycle logs
(e.g. `RetryScheduled` from `POST /jobs/flaky`) with their `job_id` / `wire_name` / `attempt` / `queue`
scopes.

**The schema-migration log is opt-in**, unlike every other log here. A store emits it only when its
options carry a `LoggerFactory` *and* `AutoMigrate` is on; the option defaults to null, so a host that
does not set it never sees the event. `StoreKind.CreateStore` takes the factory from the `UseStore`
provider and sets it, so any migrating store here demonstrates it. The In-Memory default has no schema
to migrate, so pick one that does:

```bash
dotnet run -- --BackWave:Store=Sqlite   # a local file; no Docker
```

The terminal then prints, once per run, as the store readies:

```
info: BackWave.Sqlite[1302]
      BackWave schema migration applied for sqlite.
```

The store readies once per process, so this is one event per start whenever `AutoMigrate` is on - not
one per schema change. An already-migrated database still logs it; the migration itself is idempotent.

**See it in the .NET Aspire dashboard** (traces/metrics/logs UI, still no infra beyond one container):

```bash
docker run --rm -it -p 18888:18888 -p 4317:18889 \
  -e DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true \
  mcr.microsoft.com/dotnet/aspire-dashboard:latest
# then, pointed at it:
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 dotnet run
```

Open <http://localhost:18888>, drive a job, and BackWave's `messaging.*` spans, metrics, and logs render
with no bespoke config. See [`docs/observability.md`](../../docs/observability.md) for the walkthrough.
