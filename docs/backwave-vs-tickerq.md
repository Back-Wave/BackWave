# BackWave vs TickerQ

*A side-by-side comparison of two modern .NET background job libraries. Written 2026-08-15.*

> **What this is.** BackWave publishes this report, so it is not a neutral document. It is written to
> be *accurate*, because an inaccurate comparison is worthless to the reader and embarrassing to the
> publisher. Every TickerQ claim below comes from the TickerQ documentation at
> [tickerq.net](https://tickerq.net) or from the TickerQ source on the `main` branch, read on
> 2026-08-15. Where TickerQ is better, this report says so in its own section. Where a claim is not
> measured, this report labels it as not measured.

---

## The short version

Both libraries reject the old .NET pattern of reflection and runtime type lookup. Both use a source
generator, both run on Native AOT, and both ship a free dashboard. If you have a Hangfire habit, both
feel like an upgrade.

The two products then diverge on what they optimize for.

**TickerQ optimizes for reach and immediacy.** It is MIT and Apache dual-licensed, it has 3.6k GitHub
stars and a Discord, it runs with zero configuration on an in-memory store, and it persists to EF
Core, Redis, MySQL, or MongoDB. It schedules across separate applications through TickerQ Hub. You can
create and edit a scheduled job from the dashboard.

**BackWave optimizes for what happens when things go wrong.** It is built as a pure Core plus a dumb
Shell, so the scheduling logic is a deterministic function of state, event, and time. That structure
gives BackWave a simulator, a seed minimizer, a forever-running bug search, a real-database torture
suite, and a virtual-time test harness for your own jobs. It also gives a crash-recovery model with a
time-bounded Lease and a `(workerId, attempt)` fence in every storage adapter.

| Question | BackWave | TickerQ |
|---|---|---|
| A node dies mid-job. What reclaims the work? | Lease expiry, in every adapter | The Redis package only (see [Crash recovery](#4-crash-recovery-the-largest-difference)) |
| A slow node writes an outcome after it lost the job | The `(workerId, attempt)` fence discards it | Not fenced |
| License | Source available, no-compete | MIT / Apache 2.0 |
| Persistence | Postgres, SQL Server, SQLite | EF Core (4 engines), Redis, MongoDB, in-memory |
| Named queues and fair dispatch | Yes, Strict and Weighted | No, priority per function |
| Cluster-wide concurrency cap | Yes, per queue | No, `MaxConcurrency` is per node |
| Deterministic simulation of the scheduler | Yes | No |
| Test your jobs over virtual time | Yes, `BackWave.Testing` | No |
| OpenTelemetry | Traces, metrics, logs on `messaging.*` / `db.*` | Traces and logs on custom `tickerq.*` tags |
| Create and edit a job from the dashboard | No, by design | Yes |
| Cross-application scheduling | No | Yes, TickerQ Hub |

---

## 1. What each product is

**TickerQ** is a scheduler-first job library. Its two units are the **TimeTicker**, a one-shot job at
a point in time, and the **CronTicker**, a cron expression that produces **CronTickerOccurrence**
rows. You mark a method with `[TickerFunction]`, and the source generator registers it. The scheduler
computes the next due time, sleeps until then, and runs the due functions on a custom
`TickerQTaskScheduler` with four priorities.

**BackWave** is a background job system built around a determinism boundary. The **Core** holds every
decision: due calculation, retries, lease timeout, state transitions. The Core performs no I/O and
never reads the wall clock. The **Shell** is the per-node loop that runs the Core's Commands through
the **Storage Contract**. Everything above the Storage Contract is simulable. Everything below it is
an adapter that the Conformance Suite verifies against a real database.

That split is the reason for most of the differences in this report.

---

## 2. Shared ground

These points are genuinely equal. Neither product wins here.

- **Compile-time job definition.** Both use a source generator. Neither scans assemblies at startup.
- **No runtime reflection.** Both emit the registry and the invocation path at compile time.
- **Native AOT.** Both support it.
- **A free dashboard.** Both ship one, and neither charges for it.
- **Cron scheduling with time zones.**
- **Retries with a backoff.** BackWave sets the policy per worker group. TickerQ sets `Retries` and
  `RetryIntervals` per scheduled job, which is more granular.
- **Cooperative cancellation** through a `CancellationToken`.
- **A dependency injection story** that resolves your handler's services.
- **EF Core integration.** TickerQ persists through EF Core. BackWave uses EF Core for transactional
  enqueue alongside your own `SaveChanges`.

If your requirement list stops here, either product serves you, and TickerQ costs less to adopt
because of its license and its zero-configuration default.

---

## 3. Defining a job

The two APIs are close in spirit.

TickerQ marks a method and names the function:

```csharp
public class InvoiceJobs
{
    [TickerFunction("send-invoice")]
    public Task SendInvoiceAsync(TickerFunctionContext<InvoicePayload> context, CancellationToken ct)
        => gateway.SendAsync(context.Request.OrderId, ct);
}
```

BackWave marks a method and names the wire format:

```csharp
public sealed class InvoiceJobs(IInvoiceGateway gateway)
{
    [Job("send-invoice", Queue = "billing")]
    public Task SendInvoiceAsync(string orderId, JobContext context, CancellationToken ct)
        => gateway.SendAsync(orderId, ct);
}
```

Two differences matter.

**The payload.** TickerQ carries a single `TRequest` that you serialize into the ticker. BackWave
generates a payload record from the method parameters, so a job with five arguments stays a typed
signature and needs no hand-written wrapper type.

**The stored identity.** BackWave calls `"send-invoice"` the **Wire Name**. It is mandatory, and it is
never derived from a CLR type name, so a class rename never breaks a stored job. BackWave also ships a
**Job Manifest**: a committed snapshot of every registered Wire Name that a test helper verifies. A
wire-format change then shows up in a pull-request diff. TickerQ's `functionName` is also an explicit
string and is stable in the same way, but TickerQ ships no manifest check.

**Unroutable jobs.** BackWave has a terminal state named **Quarantined** for a stored job whose Wire
Name has no registered handler, or whose payload no longer deserializes. It is loud and visible, and
it is deliberately distinct from **Dead-Lettered**, which means the job ran and kept failing. TickerQ
does not separate these two cases.

---

## 4. Crash recovery, the largest difference

This is the section that decides most production choices, so it is stated with source references.

### How BackWave reclaims work

A worker holds a **Lease**: a time-bounded, heartbeat-renewed claim on a job. When the lease expires,
the job is claimable again. This is the mechanism behind BackWave's at-least-once contract. A crash,
a hung process, a paused container, and a lost network route all produce the same outcome. The lease
lapses and another node picks the job up. Lease expiry counts as an **Attempt**, exactly like a thrown
exception. This behavior lives in every storage adapter, because the Storage Contract requires it, and
the Conformance Suite proves it against the real database.

BackWave then adds a second guarantee on top, called **Effect-Once**. The handler body can run more
than once, and idempotency is still your problem. What runs exactly once is the *recorded outcome* of
an attempt and every state change that flows from it: the terminal state, the dependency latch
decrement, the concurrency-limit slot release. The storage boundary enforces this with a
`(workerId, attempt)` fence. A node that was isolated past its lease expiry writes its stale outcome
into nothing. Because every downstream effect is a consequence of that one write, fencing the write
fences everything below it.

The simulator has a fault named **Node Isolation** for exactly this case. It cuts one node off from
storage for a bounded window while the node keeps running its handler and keeps believing that it
holds the lease. On heal, the node reports completion for a job that storage already re-leased. An
oracle asserts that the stale write changed nothing.

### How TickerQ reclaims work

TickerQ's EF Core provider claims a row with `ExecuteUpdateAsync`. It sets `LockHolder`, `LockedAt`,
and `Status = InProgress`, then re-reads the rows filtered by its own `LockHolder`. The predicate that
selects claimable rows is in `TickerQueryExtensions.cs`:

```csharp
Expression<Func<TTimeTicker, bool>> pred = e =>
    ((e.Status == TickerStatus.Idle || e.Status == TickerStatus.Queued) && e.LockHolder == lockHolder) ||
    ((e.Status == TickerStatus.Idle || e.Status == TickerStatus.Queued) && e.LockedAt == null);
```

Read the second clause. A row is claimable when `LockedAt` is null. There is no comparison against a
lease deadline, and there is no time term at all. A row whose `LockedAt` is set is not claimable, no
matter how old that timestamp is.

TickerQ does release its rows on the paths it controls. `TickerQSchedulerBackgroundService` calls
`ReleaseAcquiredResources` on a graceful shutdown, on a scheduler restart, and in its top-level
exception handler. A normal deploy is therefore safe.

The gap is the path that TickerQ does not control. A hard kill, an out-of-memory kill, a node that
loses power, or a hung process leaves rows with `Status = InProgress` and a non-null `LockedAt`.
Nothing in the EF Core provider reclaims them on a timer.

TickerQ does solve this, in one package. `NodeHeartBeatBackgroundService` writes an `hb:<node>` key to
Redis with a TTL of the heartbeat interval plus 20 seconds. When a key expires, a Lua script runs
`ReleaseDeadNodeResources` for that node. That service lives in
`TickerQ.Caching.StackExchangeRedis`. It is not part of the EF Core persistence package.

### The practical statement

| Deployment | TickerQ behavior after a hard node crash |
|---|---|
| EF Core, single node | The row stays `InProgress` until you intervene |
| EF Core, multiple nodes, no Redis | The row stays `InProgress` until you intervene |
| EF Core plus the Redis caching package | The dead node's rows are released after the heartbeat TTL |

BackWave's answer is the same in all three rows: the lease expires and the job runs again.

This is a real difference in operational risk, and it is not a difference in marketing. If you run
TickerQ in a cluster, add the Redis package. That is the fair recommendation, and TickerQ's own
architecture supports it well.

---

## 5. Queues, priority, and concurrency

**TickerQ.** Priority is a property of the *function*, declared once on `[TickerFunction]`, with four
levels: `LongRunning`, `High`, `Normal`, `Low`. The scheduler orders the due functions by priority
and queues them onto its own task scheduler. `MaxConcurrency` caps parallel jobs and defaults to
`Environment.ProcessorCount`. `LongRunning` jobs are excluded from that cap. Groups
(`MapTickerGroup()`) organize functions for the dashboard and for shared configuration.

Two limits follow from this design. Priority is fixed per function, so the same function cannot be
urgent for one caller and background for another. `MaxConcurrency` is a per-node semaphore, so ten
nodes with a cap of 16 run up to 160 jobs at once.

**BackWave.** A job belongs to exactly one named **Queue**, declared on the job type and overridable
at enqueue. Priority is never a property of a job. It lives in the consumer's **Dispatch Policy**:

- **Strict** takes an ordered list of queues and accepts starvation of the lower ones.
- **Weighted** uses smooth weighted round-robin, with no randomness, so a 6:1 weight gives the low
  queue a real share.

Both policies are work-conserving. A worker never idles while a served queue has due work.

BackWave then separates two caps that are easy to confuse:

- The **Concurrency Limit** is per queue and **cluster-wide**, enforced at claim time. A slot is
  released on a terminal state or on lease expiry, so a crash never leaks one. Ten nodes with a limit
  of 16 run 16 jobs at once, not 160.
- **Backpressure** is node-local. A node stops claiming when its worker pool has no free slot. The
  Node Driver enforces it, so a claim never exceeds free capacity.

The two gates are independent. A node can be backpressured while the cluster limit has free slots, or
limit-saturated while its own pool is idle.

If you have ever needed "never more than 4 of these running against the payment provider, across the
whole fleet," that requirement is a one-line BackWave configuration and has no TickerQ equivalent.

BackWave also separates **Worker** from **Pump**. A worker is one execution slot. A pump is one
independent claim, dispatch, and report loop with its own store round-trips. Adding pumps multiplies
store-I/O parallelism at the cost of a few more connections. This is the lever behind the fan-out
numbers in the benchmark section.

---

## 6. Recurring schedules

TickerQ's CronTicker produces `CronTickerOccurrence` rows in advance. It seeds occurrences at startup
and it supports `SkipIfAlreadyRunning()` to avoid an overlap. Since issue #776, TickerQ also offers an
opt-in `SkipStaleCronOccurrencesAsync` pass, gated on a `StaleCronOccurrenceThreshold`, that discards
occurrences that went stale while the system was down.

BackWave's **Recurring Schedule** is a template that mints jobs as time passes. The schedule and the
jobs it mints are distinct objects with distinct lifecycles. Two per-schedule policies exist:

- **Catch-Up.** `Skip` is the default, so a missed occurrence stays missed. `Coalesce` mints exactly
  one make-up job. Replaying every missed occurrence is deliberately unsupported, because a system
  that was down for a day must not wake up and run 1,440 copies of a minutely job.
- **No-Overlap.** Opt-in. A new instance is not minted while a previous one is non-terminal. The
  skipped tick is recorded visibly, never silently.

The features overlap heavily. The difference is in how each product proves the behavior. BackWave's
Core reads a virtual clock, so a test advances three years of schedule activity in milliseconds and
asserts the exact set of minted jobs. That test never flakes, because the same seed always produces
the same run.

---

## 7. Correctness engineering

This is BackWave's core investment, and TickerQ has no comparable layer. That is a design choice on
TickerQ's part, not a defect, and TickerQ's simpler architecture is what makes it easy to read.

BackWave ships five distinct instruments, each covering a different quadrant.

**The Simulator.** It drives N virtual node drivers plus the in-memory store through compressed
virtual time, with seeded fault injection: reorderings, crashes, clock skew, lost hints, node
isolation. One 64-bit seed fully determines a run. Any failure replays exactly from its seed. The
method is borrowed from TigerBeetle.

**The Seed Minimizer.** A failing run becomes a **Plan**: a scenario plus a fault map addressed by
stable identity, never by draw order. The minimizer removes faults one at a time and re-checks that
the same invariant still trips. Because removal only ever calms a run, minimization is exact. What
lands in the repository is a small, checked-in regression, not a 300-step trace.

**The VOPR Runner.** A forever-running search. It draws fresh seeds without end, and on a failure it
writes the plan and keeps going instead of halting. Bug-finding stops being a fixed battery on a pull
request and becomes a background process.

**The Conformance Suite.** The simulator stops at the Storage Contract, so it cannot catch a wrong
`SKIP LOCKED` query. The Conformance Suite is the sequential correctness test against the real
database. It also ships as a public package, `BackWave.Conformance`, so a custom adapter can prove
itself against the same contract.

**The Torture Suite.** The concurrent, non-deterministic correctness instrument. A randomized workload
hammers a real database, then invariants are audited over the final state, the Transition Log, and a
client-side journal of what each connection observed. A torture failure is always a bug.

Two more instruments sit alongside these. The **Benchmark Harness** measures performance only, and it
is deliberately outside the determinism boundary, so a noisy run is never a bug. The **upgrade
harness** boots an old schema, migrates it in place while jobs are in flight, and asserts that a
mixed-version fleet still works.

The point of the list is not the count. It is that each instrument states exactly what it proves, and
refuses the quadrant next to it. Deterministic correctness, sequential correctness against a real
database, concurrent correctness against a real database, and performance are four different
questions with four different tools.

---

## 8. Testing your own jobs

TickerQ's testing story is the ordinary one. Run the scheduler, wait for real time to pass, and assert
the result.

BackWave ships `BackWave.Testing`, a harness on virtual time:

```csharp
var harness = new BackWaveHarness(BackWaveJobs.CreateRegistry(), services);

var jobId = await harness.EnqueueAsync(new SendReminder("order-42"), delay: TimeSpan.FromDays(2));
await harness.AdvanceAsync(TimeSpan.FromDays(3));

var job = await harness.Monitor.GetJobAsync(jobId);
```

No database, no container, no `Thread.Sleep`, no flake. A two-day delay is one line and completes in
microseconds. The harness also exposes `harness.BeginTransaction()`, so a rolled-back transaction
means the job never existed, which is how you test transactional enqueue.

For anyone who has written `await Task.Delay(5000)` in a job test and then watched it fail on a loaded
CI machine, this is the difference that shows up every week.

---

## 9. Observability

**TickerQ** ships `TickerQ.Instrumentation.OpenTelemetry`. It registers an activity source named
`"TickerQ"` and emits a job execution span with child spans for the lifecycle events: enqueued,
completed, failed, cancelled, skipped, and the two seeding events. Every event is also written through
`ILogger` with structured properties. Tags are TickerQ-owned names, for example `tickerq.job.id`,
`tickerq.job.function`, and `tickerq.job.machine`. There is no metrics pillar.

**BackWave** ships `BackWave.OpenTelemetry` with all three pillars, on the OpenTelemetry semantic
conventions rather than on private names:

- **Traces** use `messaging.system=backwave`, `messaging.destination.name` for the queue,
  `messaging.destination.template` for the Wire Name, `messaging.message.id` for the job id, and
  `messaging.consumer.group.name` for the worker group. The span shape is `send` to `receive` to
  `process`, with links for fan-in.
- **Metrics** use `messaging.client.sent.messages`, `messaging.client.consumed.messages`, and the
  `messaging.process.duration` histogram. BackWave adds its own instruments where the conventions have
  no equivalent: `backwave.schedule.delay`, `backwave.job.queue.wait`, `backwave.jobs.dead_lettered`,
  `backwave.worker.slots.active` and `.capacity`, `backwave.queue.depth`, `backwave.store.faults`, and
  `backwave.observer.dispatch.duration`.
- **Store spans.** Each adapter emits `db.*` spans and classifies store faults, so a slow claim query
  is visible as a database span and not as an unexplained gap.
- **Exemplars** link a slow histogram bucket straight through to the trace that produced it.

The practical result is that BackWave's telemetry lands in a generic messaging dashboard with no
custom queries. TickerQ's telemetry needs a TickerQ-shaped dashboard.

BackWave also has two features with no TickerQ counterpart. The **Transition Log** is an append-only,
per-job history of state changes, governed by a **Job History Policy** with three levels. The
**Transition Observer** is host-supplied, egress-only code that BackWave invokes when a job reaches a
declared state, for example "Dead-Lettered, so post to Slack." It observes the Core's outputs and can
never alter a decision. It is delivered at-least-once and it is deliberately not Effect-Once, because
the reaction is a new side effect outside the fence. That limit is documented rather than hidden.

---

## 10. Schema, upgrades, and fleets

TickerQ persists through EF Core, so schema changes arrive as EF migrations. `SetSchema` moves the
tables, with a default of `"ticker"`. All TickerQ packages must be version-matched with each other.

BackWave treats the schema as a contract with three named guarantees:

- **In-Place Upgrade.** The schema upgrades on a live database while jobs stay in flight. No drain, no
  maintenance window. This is the supported path for every adapter, not a best case.
- **Mixed-Version Fleet.** During a rolling deploy the cluster runs two adjacent versions at once.
  BackWave supports exactly N-1 skew, and a harness proves that a node one release behind still works
  against the upgraded schema.
- **Coordinated Migration.** When a fleet cold-boots with auto-migrate on, every node attempts the
  migration and a database-level lock orders them. One applies it, the rest wake, re-check the version,
  and find nothing to do. No node is elected, so no node is special.

A schema-diff gate in CI fails the build when a migration is not additive.

BackWave's **Schema Name** is configurable per adapter, and the adapters author every query against
the literal `backwave` and rename it at one choke point, so the default emits byte-identical SQL.

---

## 11. Dashboard and operator actions

TickerQ's dashboard is a Vue application. It is genuinely capable: you can create, edit, and delete
tickers from the UI, watch executions live, and inspect a chain. Authentication is configurable as
basic auth, an API key, host authentication, or a custom callback.

One point deserves a plain statement: **TickerQ's dashboard has no authentication by default**. The
documentation says so directly, and it offers `WithNoAuth()` as the explicit form. That is a
reasonable default for a local development loop, and a serious risk in a deployment where somebody
forgets to configure it.

BackWave takes the opposite position on both counts.

**Authorization is delegated and explicit.** There is a fixed set of **Dashboard Permissions**: View,
ViewSensitiveData, Requeue, Cancel, TriggerSchedule, PauseQueue. Each maps to a policy name or a
predicate in your own application. BackWave never owns users or roles. **ViewSensitiveData** is a
separate gate over raw content that can carry secrets: payload bytes, Failure Detail, and Job Output.
A reader can therefore see the dashboard without seeing the contents.

**Every write is a defined state transition.** An **Operator Action** is requeue, cancel, trigger a
schedule now, pause or resume a queue, cancel a workflow, or restart or retry a workflow. Each one is
a state-machine transition with recorded identity, and never a raw row edit. Editing a job's payload
is deliberately not an operator action, because a payload edit is an unauditable change to work that
is already in flight.

That is a real trade. TickerQ lets an operator fix a bad job from the UI. BackWave makes you re-enqueue
it from your own application, and gives you an audit trail in exchange. Which one you want depends on
who has dashboard access.

BackWave also has an MCP server (`BackWave.Pro.Mcp`) that exposes the same reads and audited writes as
23 Model Context Protocol tools, under the same delegated authorization. Tools the caller is denied
are hidden from the tool list. It creates no new capability, only a new surface for an AI agent.

---

## 12. Chaining and workflows

**TickerQ** chains with a fluent builder. A parent TimeTicker declares children, and a `RunCondition`
decides when a child runs: `OnSuccess`, `OnFailure`, `OnCancelled`, `OnFailureOrCancelled`,
`OnAnyCompletedStatus`, or `InProgress` for a parallel child. The builder is capped at 5 children, and
5 grandchildren per child. Chaining applies to TimeTickers only, not to cron occurrences.

**BackWave** splits the same territory into two layers.

Below the determinism boundary sits the **Dependency**: a static edge from a job to a parent set whose
terminal states gate the job's due-ness, implemented as a countdown latch. It has exactly two reaction
modes, on-success and on-any-terminal. It is simulated and fenced like any Core state, with no fan-out
cap.

Above the boundary sits the **Workflow**: the user-facing identity, name, graph view, and lifecycle
operations over a set of jobs connected by those edges. Workflows v2 gives it a typed builder where a
step is referenced by its .NET type rather than by a string:

```csharp
public sealed class CheckoutWorkflow : IWorkflow<CheckoutSeed>
{
    public void Build(TypedWorkflowBuilder builder, CheckoutSeed seed)
        => builder.Then(new ValidateOrder(seed.OrderRef))
                  .Then(new AuthorizeCharge(seed.OrderRef))
                  .Then(new PackShipment(seed.OrderRef), after: [typeof(AuthorizeCharge)]);
}
```

The `after:` argument names the gating parents by type, so fan-out and fan-in are compile-checked.
A downstream step emits a typed result with `context.SetOutput<AuthorizeCharge, ChargeResult>(value)`
and an ancestor's result is read with `await context.Output<AuthorizeCharge, ChargeResult>(ct)`.
The output is written to the job row atomically with the
Succeeded transition, on the same `(workerId, attempt)` fence, so a fenced-out outcome discards it.
Over-limit output is rejected rather than truncated, because a clipped serialized blob is
undeserializable and silent truncation is data corruption.

Two recovery paths exist. **Workflow Restart** re-instantiates the definition as a brand-new workflow
with fresh identities and re-runs the whole graph. **Workflow Retry** moves terminal members back to a
non-terminal state in place, with three scopes: all members, failed members only, or failed members
plus their downstream dependents.

**An honest limit, stated by BackWave itself.** This is not durable execution. There are no signals,
no waits, no conditionals evaluated at runtime, and no replay. The graph is static per job. A workflow
grows by appending new jobs, never by rewriting an existing job's dependencies. If you need a step
that pauses for a human approval three days from now, use Temporal, not BackWave. BackWave documents
the escape hatches for the common cases: a completion-anchored delay is a step that self-schedules the
next step at a future due time, and a wait-for-condition is a step that re-enqueues itself on a
backoff.

TickerQ's chaining is not durable execution either, and its `RunCondition` set is slightly richer than
BackWave's two dependency modes. BackWave's advantage here is the typed builder, typed outputs, the
absence of a fan-out cap, and the fact that the gating logic is simulated.

---

## 13. Performance

**No head-to-head BackWave-vs-TickerQ benchmark exists.** This report does not claim one, and it does
not estimate one. The paragraph below is context on BackWave's architecture, not a comparison.

BackWave's benchmark methodology is published at
[backwave.app](https://backwave.app). The harness runs in two
modes and every result self-labels which: `local` on a developer machine, indicative only, and
`official` on a pinned native x86-64 instance, the only publishable source. The figures below are from
a **local, indicative, not publishable** run on 2026-06-29 against Hangfire, on Postgres, with 40
matched worker slots and history on for both engines.

| Handler delay | BackWave | Hangfire | BackWave connections | Hangfire connections |
|---|---:|---:|---:|---:|
| 5 ms | 522 j/s | 593 j/s | 5 | 44 |
| 25 ms | 563 j/s | 518 j/s | 4 | 45 |
| 50 ms | 542 j/s | 431 j/s | 4 | 45 |

At 4 pumps, BackWave reached 1,733 j/s on 11 connections and 11.5% CPU. Hangfire's default reached
581 j/s on 45 connections and 14.0% CPU.

The same report states a loss plainly. Under a sustained 300 j/s load, BackWave's p99 job latency was
82 ms against Hangfire's 44 ms, because Hangfire's 40 always-polling workers pick jobs up faster than
BackWave's 25 ms poll interval.

The architectural point that carries over to any comparison: BackWave claims in batches over a handful
of connections and runs the jobs concurrently off to the side. Connection count is the resource that
managed Postgres actually caps.

TickerQ's design differs again. It sleeps until the next due time rather than polling on a fixed
interval, with `MinPollingInterval` as the floor, which is a good fit for sparse scheduled work. How
that compares under a dense queue backlog is unmeasured by either project.

---

## 14. Where TickerQ wins today

This section is not a courtesy. Each item is a real reason to pick TickerQ.

1. **The license.** TickerQ is MIT and Apache 2.0. BackWave is source-available under PolyForm Shield
   1.0.0, which permits reading, running, modifying, and forking for your own use, but forbids using
   BackWave to build a competing product. Some organizations have a policy that rules that out.
   BackWave does not call itself open source, because a no-compete license and the OSI definition are
   mutually exclusive.
2. **Community and proof of adoption.** 3.6k GitHub stars, an active Discord, and an OpenCollective.
   BackWave is newer and its contributions are invitation-only under a CLA.
3. **More persistence choices.** Redis, MySQL, and MongoDB have no BackWave equivalent. BackWave ships
   Postgres, SQL Server, and SQLite only.
4. **Zero-configuration start.** TickerQ runs in memory with no store registered, and presents that as
   a deployment option. BackWave's in-memory store is not durable, so it cannot carry the execution
   guarantee, and the docs point a no-database deployment at SQLite instead.
5. **Cross-application scheduling.** TickerQ Hub and the RemoteExecutor let one application schedule
   work that another application runs. BackWave has no equivalent, because a BackWave node is a peer
   that coordinates only through the database.
6. **Dashboard job management.** Creating and editing a scheduled job from the UI is convenient, and
   BackWave refuses it on purpose.
7. **Per-job retry configuration.** TickerQ sets `Retries` and `RetryIntervals` on the individual
   ticker. BackWave's retry policy is per worker group, which is coarser.
8. **A smaller surface to learn.** TickerQ has two entities and eight statuses. BackWave has queues,
   dispatch policies, worker groups, pumps, leases, attempts, tags, dependencies, and workflows. If
   your need is "run this method every night at 2 a.m.," BackWave is more machinery than the job
   requires.

---

## 15. Which one to pick

**Pick TickerQ when** the license must be permissive, when you want to start in minutes with no
database, when you need Redis or MySQL or MongoDB persistence, when one application must schedule work
for another, or when your workload is a set of cron jobs and timers with modest correctness stakes.

**Pick BackWave when** a duplicated or lost job costs real money, when you run a cluster and need a
crash to heal itself without a Redis dependency, when you need a cluster-wide cap on a queue, when you
want your own job logic under test without a container, when you deploy often enough that in-place
upgrades and N-1 fleets matter, or when your telemetry has to land in a standard messaging dashboard.

A blunter version. TickerQ is an excellent scheduler. BackWave is a job system built by someone who
assumes the node will die at the worst possible moment, and who wrote a simulator to prove what
happens next.

---

## 16. Caveats, please read

1. **BackWave publishes this report.** Read section 14 with that in mind, and read the TickerQ
   documentation yourself.
2. **TickerQ moves fast.** Every TickerQ claim reflects the `main` branch and the published
   documentation as read on 2026-08-15. A later release can change any of them, and the crash-recovery
   gap in section 4 is exactly the kind of thing a maintainer fixes.
3. **No head-to-head benchmark was run.** The numbers in section 13 are BackWave against Hangfire, in
   `local` mode, on an Apple Silicon laptop, and BackWave's own harness refuses to label them
   publishable. They are not TickerQ numbers, and nothing here estimates one.
4. **Feature lists are not experience.** TickerQ has thousands of users running it in production.
   BackWave has a larger correctness apparatus and a shorter production history. Both facts are
   relevant.
5. **Workflows are a BackWave Pro feature.** Everything else described here is in the free packages.
   Pro is free for organizations under $1M annual revenue, enforcement is offline and soft-fail, and a
   missing license never disables a feature.

---

## Sources

**TickerQ**: [tickerq.net](https://tickerq.net) documentation, and the
[Arcenox-co/TickerQ](https://github.com/Arcenox-co/TickerQ) `main` branch. The files quoted are
`src/TickerQ.EntityFrameworkCore/Infrastructure/TickerQueryExtensions.cs`,
`src/TickerQ.EntityFrameworkCore/Infrastructure/TickerEFCorePersistenceProvider.cs`,
`src/TickerQ.Caching.StackExchangeRedis/NodeHeartBeatBackgroundService.cs`,
`src/TickerQ/Src/BackgroundServices/TickerQSchedulerBackgroundService.cs`, and
`src/TickerQ/Src/BackgroundServices/TickerQInitializerHostedService.cs`.

**BackWave**: this repository, plus the guides at [backwave.app](https://backwave.app). See
[`CONTEXT.md`](../CONTEXT.md) for the domain glossary, `src/BackWave.Testing/README.md` for the
virtual-time harness, and `src/BackWave.Conformance` for the Storage Contract test suite. The
benchmark methodology and the OpenTelemetry surface are documented at
[backwave.app](https://backwave.app).
