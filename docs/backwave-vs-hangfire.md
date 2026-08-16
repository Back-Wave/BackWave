# BackWave vs Hangfire

*A side-by-side comparison of a new .NET background job system and the one that defined the category.
Written 2026-08-15.*

> **What this is.** BackWave publishes this report, so it is not a neutral document. It is written to
> be *accurate*, because an inaccurate comparison is worthless to the reader and embarrassing to the
> publisher. Every Hangfire claim below comes from the Hangfire documentation at
> [docs.hangfire.io](https://docs.hangfire.io) or from the `HangfireIO/Hangfire` `master` branch, read
> on 2026-08-15. Where Hangfire is better, this report says so in its own section. Where a claim is
> not measured, this report labels it as not measured.

---

## The short version

Hangfire is the reason most .NET teams stopped writing their own job table. It is twelve years old, it
runs in production at enormous scale, and its design decisions are the ones every later library
reacts to. This report treats it with that respect.

BackWave is not a Hangfire clone with extra features. It is a different answer to one question: *what
happens when a node dies at the worst possible moment, and how do you prove the answer?*

**Hangfire optimizes for reach and for getting out of your way.** One line enqueues a job. The core is
LGPL. It runs on .NET Framework 4.5.1 and on .NET 10. The storage list is long, the extension
ecosystem is large, and a decade of polish shows in the dashboard. Hangfire recovers crashed work
correctly, through a sliding invisibility timeout, and its documentation is unusually honest about
where its guarantees stop.

**BackWave optimizes for what the failure mode costs you.** It is built as a pure Core plus a dumb
Shell, so scheduling logic is a deterministic function of state, event, and time. That structure gives
BackWave a simulator, a seed minimizer, a forever-running bug search, a real-database torture suite,
and a virtual-time test harness for your own jobs. It also puts a `(workerId, attempt)` fence at the
storage boundary, so a displaced node cannot write an outcome for a job it no longer holds.

| Question | BackWave | Hangfire |
|---|---|---|
| A node dies mid-job. What reclaims the work? | Lease expiry, in every adapter | Sliding invisibility timeout, 5 min default |
| A displaced node writes an outcome after losing the job | The `(workerId, attempt)` fence discards it | The state write checks the state *name* only |
| Cluster-wide cap on concurrent jobs per queue | Yes, free, enforced at claim time | Hangfire Ace (paid), documented as "best-effort" |
| Queue priority | Yes, Strict or Weighted, storage-independent | Storage-dependent. Ignored by `Hangfire.SqlServer` |
| Job identity in storage | Wire Name, an explicit string | CLR type name, method name, parameter types |
| Test your jobs over virtual time | Yes, `BackWave.Testing` | No first-party harness |
| Deterministic simulation of the scheduler | Yes | No |
| OpenTelemetry | Traces, metrics, logs on `messaging.*` / `db.*` | No first-party package |
| Native AOT | Core and Postgres adapter | No |
| Target frameworks | net8.0, net9.0, net10.0 | net451, net46, netstandard1.3, netstandard2.0 |
| License | Source available, no-compete | LGPL v3 or a commercial subscription |
| Fan-out and fan-in workflows | Free dependency edges, Pro workflow layer | Batches, in Hangfire Pro (paid) |
| Production history | Short | Twelve years, very large |

---

## 1. What each product is

**Hangfire** is a job invocation system. You hand it a lambda, it turns the expression tree into a
stored method reference, and a pool of worker threads pulls that reference back out and invokes it.
The design goal is minimum friction: `BackgroundJob.Enqueue(() => SendEmail(id))` and you are done.
Everything else - retries, the dashboard, recurring jobs, continuations - grows from that one idea.

**BackWave** is a background job system built around a determinism boundary. The **Core** holds every
decision: due calculation, retries, lease timeout, state transitions. The Core performs no I/O and
never reads the wall clock. The **Shell** is the per-node loop that runs the Core's Commands through
the **Storage Contract**. Everything above the Storage Contract is simulable. Everything below it is
an adapter that the Conformance Suite verifies against a real database.

That split is the reason for most of the differences in this report. It is also a cost. BackWave has
more concepts than Hangfire because the boundary forces them to be named.

---

## 2. Shared ground

These points are genuinely equal. Neither product wins here.

- **Durable jobs in your existing database.** Both persist to SQL. Both survive a process restart.
- **At-least-once execution.** Both state it plainly. Both require your handler to be idempotent.
- **Automatic retry with backoff.** Hangfire defaults to 10 attempts through `AutomaticRetryAttribute`.
  BackWave sets the policy per worker group.
- **A free dashboard.** Both ship one, and neither charges for it.
- **Recurring jobs with cron expressions and time zones.**
- **Cooperative cancellation** through a `CancellationToken`.
- **Dependency injection** that resolves your handler's services.
- **A configurable schema name.** Hangfire uses `SchemaName` on the storage options. BackWave uses
  **Schema Name** per adapter.
- **Transactional enqueue.** Both let you create a job inside your own transaction.
- **Graceful shutdown** that returns in-flight work to the queue.

If your requirement list stops here, Hangfire serves you and costs far less to adopt. It is proven, it
is free, and every .NET developer you hire has already used it.

---

## 3. Defining a job

This is the largest API difference, and it has consequences that reach into operations.

Hangfire captures a method call as an expression tree:

```csharp
BackgroundJob.Enqueue(() => _invoices.SendAsync(orderId));
RecurringJob.AddOrUpdate("nightly-close", () => _ledger.CloseAsync(), Cron.Daily);
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

Hangfire's version is shorter, and that brevity is a real feature. The trade is what lands in the
database.

**What Hangfire stores.** `InvocationData` persists three strings: `Type`, `Method`, and
`ParameterTypes`. Arguments become JSON through Newtonsoft.Json. To run the job, Hangfire resolves
those strings back to a `Type` and a `MethodInfo` and invokes it by reflection.

The consequence is direct. Rename the class, rename the method, move it to another assembly, or change
the signature, and every stored job that references it fails to deserialize. Hangfire gives you an
escape hatch, `InvocationData.SetTypeResolver`, and teams that deploy often learn to use it. It is a
mitigation you have to remember, not a property of the design.

**What BackWave stores.** BackWave calls `"send-invoice"` the **Wire Name**. It is mandatory, and it
is never derived from a CLR type name. A rename or a move never breaks a stored job, because the CLR
name was never the identity.

BackWave also ships a **Job Manifest**: a committed snapshot of every registered Wire Name that a test
helper verifies. A wire-format change then shows up in a pull-request diff, before it reaches
production.

**Unroutable jobs.** BackWave has a terminal state named **Quarantined** for a stored job whose Wire
Name has no registered handler, or whose payload no longer deserializes. It is deliberately distinct
from **Dead-Lettered**, which means the job ran and kept failing. Hangfire folds both cases into a
failed job.

**Reflection versus code generation.** BackWave uses a source generator, so the registry and the
invocation path exist at compile time. `BackWave` and `BackWave.Postgres` set `IsAotCompatible`, so
they run on Native AOT. The SQL Server and SQLite adapters do not yet, because their JSON path is not
trim-safe. Hangfire's expression trees, reflection, and Newtonsoft.Json dependency rule out Native AOT
entirely. Hangfire.Core targets `net451`, `net46`, `netstandard1.3`, and `netstandard2.0`, which is
also why it still runs on .NET Framework and BackWave does not.

---

## 4. Crash recovery and the fence

This is the section that decides most production choices, so it is stated with source references. It
is also the section where Hangfire is much stronger than its younger competitors, and that gets said
first.

### Hangfire does reclaim crashed work

`Hangfire.SqlServer` fetches with a sliding invisibility timeout. The SQL is in `SqlServerJobQueue.cs`:

```sql
update top (1) JQ
set FetchedAt = GETUTCDATE()
output INSERTED.Id, INSERTED.JobId, INSERTED.Queue, INSERTED.FetchedAt
from [HangFire].JobQueue JQ with (forceseek, readpast, updlock, rowlock)
where Queue in @queues and
(FetchedAt is null or FetchedAt < DATEADD(second, @timeoutSs, GETUTCDATE()));
```

A row becomes fetchable again once `FetchedAt` falls outside the window.
`SlidingInvisibilityTimeout` defaults to 5 minutes, and the live worker renews it on a timer. A hard
kill, an out-of-memory kill, and a lost power supply therefore all heal on their own. Hangfire's own
options file makes the point in an obsolete-attribute message:

> "Does not make sense anymore. Background jobs re-queued instantly even after ungraceful shutdown
> now. Will be removed in 2.0.0."

The queue row itself is also fenced, optimistically. `SqlServerTimeoutJob.RemoveFromQueue` and the
keep-alive query both carry `and FetchedAt = @fetchedAt`, so a displaced worker's delete affects zero
rows and the worker learns that it lost the job. This is careful engineering, and it deserves credit.

### Where the fence stops

The fence protects the *queue table*. It does not protect the *job state*.

`Worker.cs` writes the outcome with an expected-state array that holds one entry, the string
`"Processing"`:

```csharp
private static readonly string[] ProcessingStateArray = new[] { ProcessingState.StateName };
```

`BackgroundJobStateChanger.cs` then applies that guard by name:

```csharp
if (context.ExpectedStates != null && !context.ExpectedStates.Contains(jobData.State, StringComparer.OrdinalIgnoreCase))
```

Read what the guard compares. It compares the state *name*. It does not compare the server id, the
worker id, or an attempt number. After a displacement, the new worker has already set the job to
`Processing`. The old worker's write therefore passes the guard, sets `Succeeded`, expires the job,
and fires any continuations.

Hangfire mitigates this cooperatively. `ServerJobCancellationWatcher` polls on a
`DefaultCheckInterval` of 5 seconds. `ServerJobCancellationToken` reads the job's state data and
aborts the handler's `CancellationToken` when the state name is no longer `Processing`, or when
`state.Data["ServerId"]` or `state.Data["WorkerId"]` no longer matches. That is a good design, and it
closes the common case.

It is cooperative, though. A handler that does not observe its token runs to completion and reports
its result. The check runs every 5 seconds, so a window exists. Hangfire's own documentation states
the limit without softening it, on the concurrency page:

> "Throttlers apply only to different background jobs, and there's no reliable way to prevent multiple
> executions of the same background job other than by using transactions in background job method
> itself. DisableConcurrentExecution may help a bit by narrowing the safety violation surface, but it
> heavily relies on an active connection, which may be broken (and lock is released) without any
> notification for our background job."

And in `Worker.cs`, as a code comment:

```
// Checkpoint #3. Job is in the Processing state. However, there are
// no guarantees that it was performed. We need to re-queue it even
// it was performed to guarantee that it was performed AT LEAST once.
```

There is one option that closes the gap, `UseTransactionalAcknowledge`, which commits the state change
and the queue removal in one transaction. It defaults to `false` and its own XML documentation calls
it an "experimental feature".

### How BackWave answers the same question

A worker holds a **Lease**: a time-bounded, heartbeat-renewed claim on a job. When the lease expires,
the job is claimable again. Lease expiry counts as an **Attempt**, exactly like a thrown exception.
This behavior lives in every storage adapter, because the Storage Contract requires it, and the
Conformance Suite proves it against the real database. So far this matches Hangfire.

BackWave then adds a second guarantee, called **Effect-Once**. The handler body still runs more than
once, and idempotency is still your problem, exactly as with Hangfire. What runs exactly once is the
*recorded outcome* of an attempt and every state change that flows from it: the terminal state, the
dependency latch decrement, the concurrency-limit slot release, the job output write.

The storage boundary enforces this with a `(workerId, attempt)` fence. A node that was isolated past
its lease expiry writes its stale outcome into nothing. Because every downstream effect is a
consequence of that one write, fencing the write fences everything below it. No cooperation from the
handler is required, and no polling interval bounds it.

The simulator has a fault named **Node Isolation** for exactly this case. It cuts one node off from
storage for a bounded window while the node keeps running its handler and keeps believing that it
holds the lease. On heal, the node reports completion for a job that storage already re-leased. An
oracle asserts that the stale write changed nothing. This runs on every seed, forever.

### The practical statement

| Failure | Hangfire | BackWave |
|---|---|---|
| Graceful shutdown | Job re-queued | Job re-queued |
| Hard kill, node never returns | Re-fetched after the invisibility timeout | Re-claimed after lease expiry |
| Node pauses, then resumes past the timeout, handler observes its token | Handler stops. Correct. | Handler stops. Correct. |
| Same, but the handler ignores the token | Stale `Succeeded` write is accepted | Stale write is fenced out and discarded |
| Same, and the job has downstream steps | Continuations fire from the stale write | Latch decrement is fenced with the write |

The honest summary: Hangfire's recovery is sound, and the remaining gap needs an uncooperative handler
plus a displacement. That combination is rare. It is also exactly the combination that produces the
double-charge incident nobody can reproduce afterwards. BackWave's position is that a rare,
unreproducible correctness failure is the expensive kind, so the guarantee belongs at the storage
boundary rather than in handler discipline.

---

## 5. Queues, priority, and concurrency

**Hangfire.** A job goes to a queue through `[Queue("alpha")]`. A server consumes an array of queue
names, `Queues = new[] { "alpha", "beta", "default" }`, which defaults to `["default"]`. Queue names
accept lowercase letters, digits, underscore, and dash only.

Priority is where the documentation is worth reading twice:

> "Queues are run in the order that depends on the concrete storage implementation. For example, when
> we are using Hangfire.SqlServer the order is defined by alphanumeric order and array index is
> ignored. When using Hangfire.Pro.Redis package, array index is important and queues with a lower
> index will be processed first."

So with the first-party SQL Server storage, you do not control queue priority through configuration.
You control it by naming your queues so that alphabetical order matches the priority you want. Teams
do exactly that, and it works. It is a workaround rather than a feature.

Parallelism is a fixed pool of dedicated threads. `WorkerCount` defaults to
`Math.Min(Environment.ProcessorCount * 5, 20)`. It is per process, so ten servers with 20 workers run
up to 200 jobs at once. Hangfire has no free cluster-wide cap. `Hangfire.Throttling` supplies mutexes,
semaphores, and rate limiters, and it lives in Hangfire Ace on a private NuGet feed. Its own
documentation heads a section "Everything works on a best-effort basis".

**BackWave.** A job belongs to exactly one named **Queue**, declared on the job type and overridable
at enqueue. Priority is never a property of a job. It lives in the consumer's **Dispatch Policy**, and
it behaves identically on every adapter:

- **Strict** takes an ordered list of queues and accepts starvation of the lower ones.
- **Weighted** uses smooth weighted round-robin, with no randomness, so a 6:1 weight gives the low
  queue a real share.

Both policies are work-conserving. A worker never idles while a served queue has due work.

BackWave then separates two caps that are easy to confuse:

- The **Concurrency Limit** is per queue and **cluster-wide**, enforced at claim time, in the free
  packages. A slot is released on a terminal state or on lease expiry, so a crash never leaks one. Ten
  nodes with a limit of 16 run 16 jobs at once, not 160.
- **Backpressure** is node-local. A node stops claiming when its worker pool has no free slot. The
  Node Driver enforces it, so a claim never exceeds free capacity.

If you have ever needed "never more than 4 of these running against the payment provider, across the
whole fleet," BackWave makes it a one-line configuration that the simulator verifies. In Hangfire that
requirement means a dedicated queue with a dedicated single-worker server, or a paid Ace subscription
with a best-effort guarantee.

BackWave also separates **Worker** from **Pump**. A worker is one execution slot. A pump is one
independent claim, dispatch, and report loop with its own store round-trips. Hangfire's model fuses
the two: each of its 20 worker threads runs its own fetch loop, which is why its connection count
tracks its worker count. Section 13 shows what that costs.

---

## 6. Recurring schedules

Hangfire's recurring jobs are mature and, on misfire handling, more flexible than BackWave's.
`RecurringJob.AddOrUpdate` writes an entry, and a Hangfire Server component checks recurring jobs on a
minute-based interval and enqueues the due ones as ordinary fire-and-forget jobs.

Hangfire 1.8 added `MisfireHandlingMode` with three values, quoted from the source:

- `Relaxed` (default): "only a single background job will be created, no matter how many occurrences
  were missed".
- `Strict`: "a new background job will be created for every missed occurrence".
- `Ignorable`: "no background jobs should be created on missed schedule".

BackWave's **Recurring Schedule** is a template that mints jobs as time passes. The schedule and the
jobs it mints are distinct objects with distinct lifecycles. Two per-schedule policies exist:

- **Catch-Up.** `Skip` is the default, so a missed occurrence stays missed. `Coalesce` mints exactly
  one make-up job. These match Hangfire's `Ignorable` and `Relaxed`. BackWave has no equivalent of
  `Strict`, and refuses it on purpose, because a system that was down for a day must not wake up and
  run 1,440 copies of a minutely job. If you want that replay, Hangfire offers it and BackWave does
  not.
- **No-Overlap.** Opt-in. A new instance is not minted while a previous one is non-terminal, and the
  skipped tick is recorded visibly rather than silently. Hangfire's nearest equivalent is
  `DisableConcurrentExecution`, which its own documentation describes as best-effort because the lock
  depends on a live connection.

The features overlap heavily. The difference is in how each product proves the behavior. BackWave's
Core reads a virtual clock, so a test advances three years of schedule activity in milliseconds and
asserts the exact set of minted jobs. That test never flakes, because the same seed always produces
the same run.

---

## 7. Correctness engineering

This is BackWave's core investment. Hangfire has a large unit and integration test suite, plus twelve
years of production exposure, which is a form of evidence that no simulator replaces. The two kinds of
evidence answer different questions, and this section is about the kind BackWave adds.

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

The honest counterweight: Hangfire has processed more real jobs than BackWave's simulator has
simulated, and real traffic finds bugs that no fault model contains. Section 16 restates this.

---

## 8. Testing your own jobs

Hangfire's testing story is the ordinary one. You extract the job body into a service and unit-test
that service, which is the advice its documentation gives and it is good advice. To test the
*scheduling* - the delay, the retry, the recurring cadence - you run a real server against a storage
and wait for real time to pass. `Hangfire.InMemory` makes the storage part easy, and it is an official
package. It deliberately uses a monotonic clock through `Stopwatch.GetTimestamp`, so the clock is real
and you cannot move it.

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

**Hangfire** has no first-party OpenTelemetry package. It writes through its own `ILog` abstraction
(LibLog), it exposes rich state history in the dashboard, and it offers `IJobFilter` hooks that make a
third-party tracing filter straightforward to write. Several community packages do exactly that.
Hangfire also ships performance counters for classic Windows deployments. The information is
available. Wiring it into a modern observability stack is your work.

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
custom queries.

BackWave also has two features whose Hangfire equivalents are partial. The **Transition Log** is an
append-only, per-job history of state changes, governed by a **Job History Policy** with three levels.
Hangfire keeps state history too, and expires it after a configurable retention.

The **Transition Observer** is host-supplied, egress-only code that BackWave invokes when a job
reaches a declared state, for example "Dead-Lettered, so post to Slack." It observes the Core's
outputs and can never alter a decision. Hangfire's nearest equivalent is a job filter, which is
strictly more powerful because a filter *can* change the outcome. That extra power is the reason
BackWave declines it: an observer that cannot alter a decision cannot corrupt one.

One BackWave limit, stated plainly: the Transition Observer is delivered at-least-once and is
deliberately not Effect-Once, because the reaction is a new side effect outside the fence.

---

## 10. Schema, upgrades, and fleets

Hangfire versions its schema and upgrades it on startup when `PrepareSchemaIfNecessary` is true, which
is the default. It publishes per-release upgrade guides, and the 1.7 and 1.8 guides give explicit
ordering rules for rolling deploys. This works, and a very large number of teams have done it.

BackWave treats the schema as a contract with three named guarantees:

- **In-Place Upgrade.** The schema upgrades on a live database while jobs stay in flight. No drain, no
  maintenance window. This is the supported path for every adapter, not a best case.
- **Mixed-Version Fleet.** During a rolling deploy the cluster runs two adjacent versions at once.
  BackWave supports exactly N-1 skew, and a harness proves that a node one release behind still works
  against the upgraded schema.
- **Coordinated Migration.** When a fleet cold-boots with auto-migrate on, every node attempts the
  migration and a database-level lock orders them. One applies it, the rest wake, re-check the
  version, and find nothing to do. No node is elected, so no node is special.

A schema-diff gate in CI fails the build when a migration is not additive.

The difference is not that Hangfire upgrades badly. It is that BackWave's upgrade properties are
tested by a harness that runs on every build, rather than described in a guide.

BackWave's **Schema Name** is configurable per adapter, and the adapters author every query against
the literal `backwave` and rename it at one choke point, so the default emits byte-identical SQL.
Hangfire's `SchemaName` option does the same job.

---

## 11. Dashboard and operator actions

Hangfire's dashboard is the most mature in .NET. Twelve years of iteration show. It lists jobs by
state, renders the full retry history with stack traces, exposes recurring jobs with a trigger button,
shows real-time graphs, and lets an operator requeue or delete.

Its default is also worth stating, because it is a good one:

> "By default Hangfire allows access to Dashboard pages only for local requests."

To open it up, you implement `IDashboardAuthorizationFilter`, or you install
`Hangfire.Dashboard.Authorization` for ready-made user, role, claims, and basic-auth filters. A
fail-closed default is the correct choice, and Hangfire made it.

BackWave takes a similar position and formalizes it.

**Authorization is delegated and explicit.** There is a fixed set of **Dashboard Permissions**: View,
ViewSensitiveData, Requeue, Cancel, TriggerSchedule, PauseQueue. Each maps to a policy name or a
predicate in your own application. BackWave never owns users or roles. **ViewSensitiveData** is a
separate gate over raw content that can carry secrets: payload bytes, Failure Detail, and Job Output.
A reader can therefore see the dashboard without seeing the contents. Hangfire's filter model is
all-or-nothing per page, so a viewer who can open the job page can read its arguments.

**Every write is a defined state transition.** An **Operator Action** is requeue, cancel, trigger a
schedule now, pause or resume a queue, cancel a workflow, or restart or retry a workflow. Each one is
a state-machine transition with recorded identity, and never a raw row edit.

**Pause a queue** has no Hangfire equivalent, and it is the action an operator reaches for during an
incident.

BackWave also has an MCP server (`BackWave.Pro.Mcp`) that exposes the same reads and audited writes as
23 Model Context Protocol tools, under the same delegated authorization. Tools the caller is denied
are hidden from the tool list. It creates no new capability, only a new surface for an AI agent.

Hangfire's counterweight here is real: its dashboard has extension points, a decade of third-party
pages, and behavior that thousands of operators already know without reading anything.

---

## 12. Chaining, batches, and workflows

**Hangfire** covers this ground in two layers. `BackgroundJob.ContinueJobWith` is free and chains one
job after another. **Batches** are the real answer for fan-out and fan-in, and they are excellent:
`BatchJob.StartNew` creates many jobs atomically, and `BatchJob.ContinueBatchWith` runs a follow-up
once every job in the parent batch finishes.

Two facts belong with that praise. Batches are in **Hangfire Pro**, which is a paid subscription. And
the batch documentation states its own limit:

> "Only official Hangfire.InMemory, Hangfire.SqlServer and Hangfire.Pro.Redis job storage
> implementations are currently supported."

If you run Hangfire on the community PostgreSQL storage, batches are not available to you.

**BackWave** splits the same territory into two layers.

Below the determinism boundary sits the **Dependency**: a static edge from a job to a parent set whose
terminal states gate the job's due-ness, implemented as a countdown latch. It has exactly two reaction
modes, on-success and on-any-terminal. It is free, it works on all three adapters, and it is simulated
and fenced like any Core state.

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
and an ancestor's result is read with `await context.Output<AuthorizeCharge, ChargeResult>(ct)`. The
output is written to the job row atomically with the Succeeded transition, on the same
`(workerId, attempt)` fence, so a fenced-out outcome discards it. Over-limit output is rejected rather
than truncated, because a clipped serialized blob is undeserializable and silent truncation is data
corruption.

Passing a result between Hangfire jobs has no first-party typed equivalent. The documented pattern is
to write the result to your own table and pass an identifier, which is also sound advice for BackWave
when the payload is large.

Two recovery paths exist in BackWave. **Workflow Restart** re-instantiates the definition as a
brand-new workflow with fresh identities and re-runs the whole graph. **Workflow Retry** moves
terminal members back to a non-terminal state in place, with three scopes: all members, failed members
only, or failed members plus their downstream dependents.

**An honest limit, stated by BackWave itself.** This is not durable execution. There are no signals,
no waits, no conditionals evaluated at runtime, and no replay. The graph is static per job. If you
need a step that pauses for a human approval three days from now, use Temporal, not BackWave.
Hangfire's batches share this limit, so neither product competes with a durable-execution engine.

**The commercial comparison is symmetric and worth naming.** Fan-out with fan-in costs money in both
products. In Hangfire it is Hangfire Pro. In BackWave the dependency edges are free and the workflow
layer is Pro, which is free for organizations under $1M annual revenue.

---

## 13. Performance

Unlike the TickerQ comparison, a direct measurement exists here. It still comes with a warning.

BackWave's benchmark harness runs in two modes and every result self-labels which: `local` on a
developer machine, indicative only, and `official` on a pinned native x86-64 instance, the only
publishable source. **Every number below is from a `local`, indicative, not publishable run on
2026-06-29.** BackWave's own harness refuses to publish them. They are reproduced here because they
are the best available data, not because they settle anything.

Environment: macOS 15.7.4, Arm64, 10 cores, .NET 10.0.0, `postgres:17-alpine` running natively,
Hangfire 1.8.23 with the community `Hangfire.PostgreSql` 1.21.1, 40 matched worker slots on both
sides, job history enabled on both sides.

**Drain a fixed backlog, Postgres:**

| Handler delay | BackWave | Hangfire | BackWave connections | Hangfire connections |
|---|---:|---:|---:|---:|
| 5 ms | 522 j/s | 593 j/s | 5 | 44 |
| 10 ms | 511 j/s | 581 j/s | 4 | 45 |
| 25 ms | 563 j/s | 518 j/s | 4 | 45 |
| 50 ms | 542 j/s | 431 j/s | 4 | 45 |

Two patterns. Hangfire is faster on very short jobs. BackWave holds its throughput as the handler gets
slower, while Hangfire's falls, because a Hangfire worker thread holds its connection across the
handler body. Across every row BackWave used about 4 to 5 connections against Hangfire's 44 to 45, at
roughly 4% CPU against 13%.

**Scaling by adding pumps, 10 ms handler:**

| Configuration | Throughput | Connections | CPU |
|---|---:|---:|---:|
| BackWave, 1 pump | 511 j/s | 4 | 3.9% |
| BackWave, 2 pumps | 862 j/s | 6 | 7.0% |
| BackWave, 4 pumps | 1,733 j/s | 11 | 11.5% |
| Hangfire, default | 581 j/s | 45 | 14.0% |

This is the architectural point. BackWave claims in batches over a handful of connections and runs the
jobs concurrently off to the side, so adding a pump multiplies store-I/O parallelism for a few more
connections. Hangfire's throughput is bounded by its worker count, and its connection count tracks it.
Connection count is the resource that managed Postgres actually caps.

**Where Hangfire wins, stated plainly.** Under a sustained 300 j/s arrival rate, BackWave's p99 job
latency was 82 ms against Hangfire's 44 ms. Hangfire's 40 always-polling workers pick a job up faster
than BackWave's 25 ms poll interval. If your jobs are user-visible and latency-sensitive, that is a
point for Hangfire. In the same run, BackWave's submit p99 was 7.7 ms against Hangfire's 18.5 ms, on
12 connections against 53, at 5.9% CPU against 12.2%.

On an empty-handler ceiling test, Hangfire reached 638 j/s on 46 connections and 13.6% CPU. BackWave
reached 559 j/s on 5 connections and 3.8% CPU.

**SQL Server, and why these numbers are not usable.** The SQL Server run used
`mcr.microsoft.com/mssql/server:2022` as an x86 image under Rosetta emulation on Apple Silicon. That
penalizes the engine that issues more round-trips per job, which is BackWave. On a 5 ms handler
BackWave reached 230 j/s against Hangfire's 605 j/s. With 4 pumps and a 10 ms handler BackWave reached
790 j/s on 5 connections against Hangfire's 561 j/s on 45. Draw no conclusion from these. They exist
here so that the report does not quietly omit the run where BackWave looked worst.

---

## 14. Where Hangfire wins today

This section is not a courtesy. Each item is a real reason to pick Hangfire.

1. **Twelve years of production evidence.** Hangfire has processed an enormous volume of real work
   under conditions no fault model enumerates. BackWave has a larger correctness apparatus and a much
   shorter production history. Both facts matter, and the second one is not a small caveat.
2. **The license.** The Hangfire core is LGPL v3, with a commercial subscription available for teams
   that need to distribute private forks. BackWave is source-available under PolyForm Shield 1.0.0,
   which permits reading, running, modifying, and forking for your own use, but forbids using BackWave
   to build a competing product. Some organizations have a policy that rules that out. BackWave does
   not call itself open source, because a no-compete license and the OSI definition are mutually
   exclusive.
3. **Framework reach.** Hangfire runs on .NET Framework 4.5.1. BackWave requires net8.0 or later. If
   you have a legacy application, this decides the question by itself.
4. **The ecosystem.** Storage providers for SQL Server, PostgreSQL, MySQL, Redis, MongoDB, SQLite,
   Oracle, and more. Dozens of extension packages. Thousands of Stack Overflow answers. A support
   subscription you can buy. BackWave has three adapters and a short history.
5. **Lower pickup latency by default.** Hangfire's always-polling worker threads got a p99 job latency
   of 44 ms against BackWave's 82 ms in the run above.
6. **Faster on very short jobs.** At a 5 ms handler, Hangfire drained the backlog faster.
7. **Job filters.** `IServerFilter` and `IElectStateFilter` let you intercept and change job execution
   globally. BackWave's Transition Observer deliberately cannot, so certain cross-cutting behaviors
   are easier in Hangfire.
8. **`Strict` misfire handling.** Hangfire replays every missed recurring occurrence when you ask it
   to. BackWave refuses that mode on purpose.
9. **A smaller surface to learn.** `BackgroundJob.Enqueue(() => Method())` is one line and needs no
   attribute, no registry, and no Wire Name. BackWave has queues, dispatch policies, worker groups,
   pumps, leases, attempts, tags, dependencies, and workflows. If your need is "run this method every
   night at 2 a.m.," BackWave is more machinery than the job requires.
10. **Hiring.** Every .NET developer has used Hangfire. Nobody has used BackWave.

---

## 15. Which one to pick

**Pick Hangfire when** you are on .NET Framework, when the license must not be no-compete, when you
need a storage BackWave does not have, when your jobs are short and latency-sensitive, when you want a
tool your whole team already knows, or when your correctness stakes are ordinary and a rare duplicate
outcome costs a support ticket rather than money.

**Pick BackWave when** a duplicated or lost outcome costs real money, when you need a cluster-wide cap
on a queue without a paid add-on, when a class rename must not break jobs already in the database,
when you want your own job logic under test without a container or a real clock, when connection count
is your scaling constraint, when you deploy often enough that in-place upgrades and N-1 fleets matter,
or when your telemetry has to land in a standard messaging dashboard with no custom queries.

A blunter version. Hangfire is the safe, proven, unglamorous choice, and it is the right one far more
often than a competitor's report likes to admit. BackWave is for the case where you have already been
burned by a job that ran twice, and you want the guarantee enforced by the database rather than by
your handler's discipline.

---

## 16. Caveats, please read

1. **BackWave publishes this report.** Read section 14 with that in mind, and read the Hangfire
   documentation yourself.
2. **Hangfire is a moving target.** Every Hangfire claim reflects the `master` branch and the
   published documentation as read on 2026-08-15. Hangfire 2.0 is in development, and the source
   already carries obsolete markers pointing at it.
3. **The benchmark is `local` mode.** The numbers in section 13 come from an Apple Silicon laptop, and
   BackWave's own harness refuses to label them publishable. The Postgres run used the community
   `Hangfire.PostgreSql` provider, not a first-party one. The SQL Server run used Rosetta emulation
   and is not usable in either direction. Reproduce them on your own hardware before you weigh them.
4. **The crash-recovery gap in section 4 is narrow, and this report says so.** It requires a handler
   that ignores its `CancellationToken` plus a displacement past the invisibility timeout. Hangfire's
   `ServerJobCancellationWatcher` closes the common case, and `UseTransactionalAcknowledge` closes the
   rest at the cost of an experimental flag. The disagreement is about where the guarantee belongs,
   not about whether Hangfire tried.
5. **Feature lists are not experience.** Hangfire entered production use in 2013. BackWave
   has a simulator, a torture suite, and a short track record. A reader who weighs track record above
   apparatus is reasoning correctly.
6. **Workflows are a BackWave Pro feature**, as batches are a Hangfire Pro feature. Everything else
   described here is in the free BackWave packages. BackWave Pro is free for organizations under $1M
   annual revenue, enforcement is offline and soft-fail, and a missing license never disables a
   feature.

---

## Sources

**Hangfire**: [docs.hangfire.io](https://docs.hangfire.io) and the
[HangfireIO/Hangfire](https://github.com/HangfireIO/Hangfire) `master` branch. The files quoted are
`src/Hangfire.Core/Server/Worker.cs`,
`src/Hangfire.Core/States/BackgroundJobStateChanger.cs`,
`src/Hangfire.Core/Server/ServerJobCancellationToken.cs`,
`src/Hangfire.Core/Server/ServerJobCancellationWatcher.cs`,
`src/Hangfire.Core/Storage/InvocationData.cs`,
`src/Hangfire.Core/AutomaticRetryAttribute.cs`,
`src/Hangfire.Core/MisfireHandlingMode.cs`,
`src/Hangfire.Core/BackgroundJobServerOptions.cs`,
`src/Hangfire.SqlServer/SqlServerJobQueue.cs`,
`src/Hangfire.SqlServer/SqlServerTimeoutJob.cs`,
`src/Hangfire.SqlServer/SqlServerStorage.cs`, and
`src/Hangfire.SqlServer/SqlServerStorageOptions.cs`. The documentation pages quoted are Best
Practices, Configuring Job Queues, Concurrency Rate Limiting, Using Batches, Performing Recurrent
Tasks, and Using Dashboard. Hangfire documentation is licensed under CC BY 4.0.

**BackWave**: this repository, plus the guides at [backwave.app](https://backwave.app). See
[`CONTEXT.md`](../CONTEXT.md) for the domain glossary, `src/BackWave.Testing/README.md` for the
virtual-time harness, and `src/BackWave.Conformance` for the Storage Contract test suite. The
benchmark methodology and the OpenTelemetry surface are documented at
[backwave.app](https://backwave.app).

**See also**: [`backwave-vs-tickerq.md`](./backwave-vs-tickerq.md).
