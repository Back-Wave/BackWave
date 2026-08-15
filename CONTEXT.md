# BackWave

A background job system for .NET — a modern Hangfire alternative built around deterministic testing, a functional core, and storage that piggybacks on the application's existing database.

## Language

**Background Job System**:
The product species: enqueue work, it runs once on some worker, with retries. Job internals are opaque to the system.
_Avoid_: Durable execution, workflow engine — BackWave does not record or replay steps inside a job.

**Core**:
The pure decision-making logic: scheduling, retries, due calculation, lease/timeout handling, state transitions. Deterministic functions of (state, event, time) — performs no I/O.
_Avoid_: Engine, scheduler (overloaded)

**Shell**:
The per-node imperative loop and its edges: fetch state, call the Core, execute the resulting Commands through the Storage Contract. Owns all concurrency, I/O, and the real clock. Kept too dumb to have interesting bugs.
_Avoid_: Host, runtime (overloaded)

**Command**:
A value the Core returns describing what the Shell should do (mint a job, schedule a retry at t, expire a lease, claim from a queue). The Core decides; only the Shell acts.
_Avoid_: Event (events are inputs to the Core; Commands are its outputs)

**Storage Contract**:
The semantic specification of what any storage implementation must guarantee (e.g. behavior under concurrent lease acquisition, crash mid-write). The determinism boundary: everything inside it is simulable, everything beyond it is not.
_Avoid_: Repository interface, persistence layer

**Storage Adapter**:
A production implementation of the Storage Contract against a real database the user already operates (v1: Postgres, SQL Server). Must be durable; verified by the Conformance Suite, not the Simulator. Two topologies exist — see Networked Adapter and Embedded Adapter — and the distinction is which deployment shapes the Adapter supports, never which Contract clauses it honors: every Adapter honors all of them.
_Avoid_: Provider, driver

**Schema Name**:
The database schema (Postgres, SQL Server) or table-name prefix (SQLite, which has no schemas) that holds all of a Storage Adapter's objects. Defaults to `backwave`; configurable per store so a deployment can fit a naming convention or coexist with another application's objects in a shared database. A configuration value fixed for the life of the data — chosen before first migrate, never changed in place, and never a per-tenant or per-request dimension (multi-tenant scoping stays dropped). The adapters author every query and DDL script against the literal `backwave` and rename it at one choke point, so the default emits byte-identical SQL.
_Avoid_: Namespace, tenant (it is neither a runtime scope nor a tenancy mechanism)

**Networked Adapter**:
A Storage Adapter over a database server reachable across hosts (Postgres, SQL Server), so the cluster of Nodes may span many machines. The implicit shape of every concept that assumes peers on different hosts.

**Embedded Adapter**:
A Storage Adapter whose database is an in-process, single-host engine (v1: SQLite) rather than a network server. Durable and Conformance-verified like any Adapter, but bounded to **one host**: any number of processes on that host may share the database — each a Node coordinating through the shared file, so concurrent Claim, cluster-wide Concurrency Limits, and lease expiry all stay live across those processes — but the database is never shared **across** hosts (the engine's file locking is unsafe over network filesystems). Trades cluster scale-out for zero operational overhead. Two deployments: **co-resident** (BackWave's tables live in the user's own application database file, so jobs and business writes commit in one file, one transaction — the tightest Transactional Enqueue there is) or **dedicated** (BackWave gets its own file while business data lives in some other engine — a fully supported deployment that simply forgoes Transactional Enqueue, since no transaction can span two engines). The Transactional-Enqueue *capability* is present either way; only the co-resident deployment can exploit it.
_Avoid_: Local store, dev store (it is a supported production target, unlike the In-Memory Store)

**Transactional Enqueue**:
Enqueueing a job inside the application's own database transaction, so the business write and the job commit or roll back atomically. BackWave rides along on a transaction the user owns — it never opens the business transaction itself. A Storage Contract capability, not a universal guarantee.
_Avoid_: Outbox (the pattern this makes unnecessary)

**In-Memory Store**:
A first-class, publicly shipped implementation of the Storage Contract that is deterministic and runs on Virtual Time. For tests and local dev only — never a supported production target.
_Avoid_: Mock storage, fake (it is a real implementation of the contract)

**Node Driver**:
The sans-I/O state machine containing all of a node's logic (claiming, heartbeats, lease renewal, hint reactions): Step(event) → Commands. It asks for effects and is told outcomes; it never awaits, times, or threads. The production pump and the Simulator are its two callers.
_Avoid_: Worker loop (the loop is the pump; the logic is the Driver)

**Simulator**:
The test harness that drives N virtual Node Drivers plus the In-Memory Store through compressed Virtual Time with seeded fault injection (orderings, crashes, clock skew, hint loss), TigerBeetle-style. One 64-bit seed fully determines a run; any failure replays exactly from its seed.

**Plan**:
A serializable, replayable description of one Simulator run, distinct from the Seed that discovers it: a *Scenario* (the deterministic world — node and job counts, topology, durations, the fault parameters in force) plus a *Fault Map* (every injected fault decision, addressed by what it acts on — an Attempt, a node, an isolation episode — never by draw order). The Seed is the compact discovery unit; the Plan is the unit that is minimized, replayed, and checked in as a regression. Because faults are addressed by stable identity, removing one from the Fault Map leaves the rest meaningful — the property the Seed Minimizer relies on. Replaying a Plan consults its Fault Map at each fault site (no-fault default on a miss) while regenerating the unchanged Scenario world from its Seed.
_Avoid_: Trace, decision log (imply draw-order addressing — exactly what a Plan refuses), replay file

**VOPR Runner**:
The continuous discovery engine built on the Simulator: it draws fresh Seeds without end, and on any failure replays it to a persisted Plan and keeps going rather than halting. Named for TigerBeetle's VOPR — the testing method is borrowed, not the architecture. Turns bug-finding from a fixed PR battery into a forever-running search. Internal infrastructure, never a shipped package.

**Swarm**:
The per-run randomization of the fault parameters themselves: each run independently decides which fault kinds are active and at what intensity, biased so calm, single-fault, and full-chaos runs all occur. Derived as a pure function of the Seed, so a swarm-discovered failure still replays from its seed alone. Only ever produces configurations inside the harness's supported envelope — never the oracle self-tests — so a swarm failure is always a real bug.
_Avoid_: Fuzzing (swarm randomizes the fault configuration, not a workload payload — and even the coverage-guided variant mutates Plans, never payloads; see Coverage-Guided Swarm)

**Fault Level**:
A prescriptive contract chosen before a run and recorded in its Scenario, pairing the *fault envelope the Swarm may draw from* with the *set of oracles enforced* — borrowed from TigerBeetle's graduated fault model. Three levels: **Pristine** (no faults; full safety plus strict liveness with tight bounds — the reproducible floor that the engine works), **Recoverable** (only faults guaranteed to heal; full safety plus liveness, the latter demanded only once faults have ceased), and **Adversarial** (faults that may never heal, e.g. permanent node loss; **safety only** — liveness is not an error). Safety oracles ("if X happened, Y holds") run at every level; the three liveness oracles (Drain-Liveness, Migration-Liveness, Observer-Delivery-Liveness) are the only level-gated checks — they are demanded under Pristine and Recoverable and switched off under Adversarial. The level both caps what the Swarm may generate and selects the oracle set, so a green run states exactly what it proved, and a liveness failure under a level that guaranteed progress is unambiguously a real bug rather than a fault-induced artifact.
_Avoid_: Chaos level / intensity (the level selects oracles, not merely how hard the box is shaken — intensity is the Swarm's job within a level), Mode

**Seed Minimizer**:
The tool that shrinks a failing Plan toward its minimal reproducing interleaving by removing faults from its Fault Map and re-checking that the same invariant still trips — matched by stable invariant identity, never by message text, Seed, or virtual time. Removal only ever calms a run, so minimization is exact and bug-preserving, and the original failing Plan is always retained as the floor. Shrinking the Scenario itself (fewer jobs or nodes) is a separate, opt-in, coarse pass that may surface a sibling bug and never replaces the exact repro.

**Coverage**:
The signal for which regions of the state space the corpus actually exercises: the state-transition edges traversed (against the legal-edge set) plus a curated set of named *Situations* (stale-outcome-fenced, migration-fired, limit-saturated, backpressure-idle, quarantine-reached, …). Unioned across runs and surfaced as a report of never-reached regions — a report, not a gate. The targets a later coverage-guided Swarm aims at.

**Coverage-Guided Swarm**:
The Swarm with a corpus and a coverage gradient (Phase 3b): it retains coverage-advancing Plans and mutates them across two surfaces — the swarm-config space (climbing the denominator-based edge/Situation Coverage report) and the Fault Map directly (trace-level, climbing a denominator-free novelty signal of co-occurring-Situation interaction tuples). Still not Fuzzing: it mutates fault configuration and fault decisions, never a workload payload. A failure it finds is a bug; a Plan it keeps is a replayable artifact — the search *trajectory* is not replayable (a shared mutable corpus across workers), but every *artifact* replays from itself.
_Avoid_: Fuzzer (the glossary reserves "fuzzing" for payload mutation), GuidedFuzzer (the rejected name)

**Node Isolation**:
A Simulator fault that cuts one node off from the Storage Contract for a bounded window: its store operations are rejected or withheld until the isolation heals, yet it keeps executing its handler and keeps believing it holds the Lease. Distinct from a crash, which forgets in-flight work — an isolated node instead acts on a *stale lease belief*, so on heal it may report completion or renewal for a job the store has already re-leased to another node (the heal-into-stale-write race). Because peers are database-authoritative and stateless there is no split-brain: the isolated node cannot fork authoritative state, only act on a stale read of it.
_Avoid_: Partition, Network partition (imply a peer split-brain that cannot occur here), Disconnect

**Wake-Up Hint**:
An optional storage notification ("something was enqueued — poll now") that exists only to cut latency. Never correctness-bearing: the system must behave identically (minus latency) if every hint is dropped, duplicated, or delayed. Polling is the sole source of truth.
_Avoid_: Push delivery, notification channel (implies reliability it doesn't have)

**Conformance Suite**:
The test suite that verifies a Storage Adapter honors the Storage Contract semantics against the real database. The adapters' substitute for simulation coverage.

**In-Place Upgrade**:
Upgrading BackWave's schema on a live database while jobs remain in flight — no drain, no maintenance window. The supported upgrade path for every Storage Adapter.
_Avoid_: Drain-first upgrade, offline migration (both imply a maintenance window BackWave refuses to require)

**Mixed-Version Fleet**:
A cluster running two adjacent BackWave versions at once — the normal state during a rolling deploy. Supported at N-1 skew only: nodes one release behind must keep working against the upgraded schema; wider skew is out of contract.
_Avoid_: Version skew (unbounded; the contract is exactly N-1)

**Coordinated Migration**:
Whenever more than one Node may apply the schema at once — a fleet cold-booting with auto-migrate on, or the first upgraded Node of a rolling deploy migrating a live database — only one Node applies it at a time; the others block on a database-level lock, then wake, re-check the schema version, and find nothing to do. On by default, with an opt-out for deployments that serialize migration themselves (a single-runner deploy step). Not leader election: every Node still attempts migration and the lock merely orders them, so no Node is special and none is elected. Networked Adapters coordinate through a lock held only for the migration; the Embedded Adapter leans on its engine's single-writer lock, which already permits just one writer across the host's processes.
_Avoid_: Leader election / migration leader (no Node is elected — all attempt, the lock orders them), Distributed lock (overclaims — for the Embedded Adapter it is just the file's write lock)

**Schema Re-baseline**:
Discarding a Storage Adapter's incremental migration history and replacing it with a single consolidated script that stamps the current schema as a fresh v1. Legitimate only while no consumer holds a populated database — in practice, once before the first published release — because it is the maximally destructive schema operation and cannot coexist with the additive-first obligation that protects live data. After the first release, additive-first binds; a further re-baseline is an explicit, recorded exception, expected only at a major version.
_Avoid_: Squash (git-flavored; this is about shipped schema, not commits), schema migration (the opposite — a re-baseline destroys migration history rather than extending it)

**Torture Suite**:
The non-deterministic correctness instrument for Storage Adapters: a randomized concurrent workload hammers a real database, then invariants are audited over the final state, the Transition Log, and a client-side journal of what each connection observed. Fills the quadrant the other instruments refuse — the Simulator and VOPR Runner are deterministic correctness, the Conformance Suite is sequential correctness, the Benchmark Harness is non-deterministic *performance*. A torture failure is always a bug, never noise; discovery-only, never a PR gate.
_Avoid_: Stress test (implies performance), chaos test (implies production), load test

**Benchmark Harness**:
The macro, end-to-end throughput measurement tool: it drives the real Shell pump against a real Storage Adapter under wall-clock time to produce throughput (jobs/sec), latency (p50/p99), and resource numbers. Deliberately the one body of testing **outside the determinism boundary** — real clock, real I/O, real threads, non-deterministic *by design* — so a noisy run is never a bug. It measures *performance*, never *correctness*; that line is what separates it from the Simulator (deterministic, virtual-time, Core bugs), the Conformance Suite (correctness against a real database), and the VOPR Runner (a forever-search for correctness bugs). The In-Memory Store is never one of its targets — running on Virtual Time, it has no wall-clock throughput to measure. Its credibility rests on a published methodology: a noop workload as the clearly-labeled framework-overhead ceiling, every competitor tuned to *its* best, and a pinned environment manifest on every number. Runs in two modes — **local** (a developer's machine, indicative only, never published) and **official** (a pinned native-x86-64 instance, the only publishable source) — and every result self-labels which.
_Avoid_: Load test / Stress test (imply stress-to-failure), Perf test (vague), Benchmark suite (it is a harness producing numbers, not a pass/fail battery)

**Scheduled Job**:
A job with a due time. The only shape of work the Core knows. An "enqueued" job is simply one whose due time is now — same mechanism, same code path.
_Avoid_: Fire-and-forget, delayed (both are just due times)

**Recurring Schedule**:
A cron-defined template that mints Scheduled Job instances as time passes (UTC by default, opt-in IANA zone). The schedule and the jobs it mints are distinct things with distinct lifecycles. Per-schedule policies: Catch-Up and No-Overlap.
_Avoid_: Recurring job (the schedule is not a job; it creates jobs)

**Catch-Up Policy**:
What a Recurring Schedule does about occurrences missed while the system was down: Skip (default — missed means missed) or Coalesce (mint exactly one make-up job). Replaying every missed occurrence is deliberately unsupported.

**No-Overlap**:
Opt-in Recurring Schedule policy: don't mint a new instance while a previous one is non-terminal. A skipped tick is recorded visibly, never silent. Enforced at mint time — the mint decision skips the tick when a previously minted instance is still live — so behaviorally equivalent to a per-schedule Concurrency Limit of 1 without coupling the claim path to schedules.
_Avoid_: Singleton job, disable concurrent execution

**Queue**:
A named stream of jobs, claimed in due-time order. A job belongs to exactly one Queue (declared on the job type, overridable at enqueue). Priority is never a property of a job — it lives in the consumer's Dispatch Policy.
_Avoid_: Priority lane, channel

**Tag**:
An observational annotation attached to a job for search, filtering, and grouping in the Monitor and Dashboard. Purely descriptive: the Core never reads a Tag, so Tags never cross the determinism boundary — the Simulator records none, exactly like Failure Detail. A Tag is one of two kinds, distinguished structurally rather than by parsing: a **Label** (a bare string, e.g. `urgent`) or a **Keyed Tag** (a key with a string value, e.g. `tenant`→`acme`); a colon in a Label is ordinary data, never a separator. One key may carry several values (`variant`→`BRCA1`, `variant`→`TP53`). A job's Tags form a set — re-adding an identical Tag is a no-op — which keeps tag authorship idempotent under At-Least-Once Execution. Values are strings only; a date is stored as a string the caller canonicalizes, never a stored type (no numeric/date range semantics).
_Avoid_: Priority (rejected), Label-as-synonym-for-Tag (a Label is one *kind* of Tag), Metadata bag (implies the Core consults it), Annotation that alters behavior.

**Tag Facet**:
A count-ranked breakdown of the Tags present within the *currently filtered* slice of jobs, capped to the top entries — it answers "what are the dominant buckets in this slice," never "what Tags exist." Deliberately non-exhaustive; reaching an arbitrary Tag is the Tag Suggest's job.
_Avoid_: Tag list, tag cloud, treating the facet as a complete inventory of Tags.

**Tag Suggest**:
A case-insensitive prefix-completion aid over *all* Tags in the store, ordered lexicographically, that helps an operator compose an exact Tag filter. A suggestion carries the canonical stored casing, so the filter it produces matches exactly; the suggest itself never filters jobs and never promises the suggested Tag has matches under the operator's current filter — only that it exists. Complements the Tag Facet: the Facet is scoped and count-ordered (discover what's big), the Suggest is global and lex-ordered (reach anything).
_Avoid_: Tag search (implies the job listing matches fragments — it doesn't; job filtering stays exact-match), fuzzy/substring search (rejected: prefix only), typeahead-as-filter.

**Worker**:
One execution slot in a Worker Group's pool. The pool's size is its Worker count (the group's `PoolSize`). The unit that both caps bound — the Concurrency Limit (cluster-wide) and Backpressure (node-local) — at different scopes. A Worker is *execution* concurrency (how many jobs run at once); distinct from a Pump, which is *fetch-loop* parallelism (how many independent claim→report loops a group runs).
_Avoid_: Thread (a Worker is a logical slot, not necessarily a thread), consumer, Pump (a Pump is the loop; a Worker is one slot the loop fills)

**Pump**:
The Shell-side event loop that runs one Worker Group's claim → dispatch → report cycle, feeding NodeEvents to a single Driver instance and owning that loop's timers, claim batch, in-flight execution set, and outcome writes. A group's store I/O is serial *within* one Pump — one round-trip in flight at a time — so a single Pump's throughput is bounded by round-trip latency, not by Workers or CPU. A Worker Group runs one or more Pumps (its `Pumps` count, default 1); each is an independent loop with its own Driver, `PoolSize` pool, and claim stream, so adding Pumps multiplies a group's store-I/O parallelism at the cost of more connections. Raising the single-Pump ceiling without more Pumps (overlapping a Pump's own round-trips) is a separate, deferred lever.
_Avoid_: Worker loop (the loop is the Pump; the logic is the Driver), Worker (execution slot, not the loop)

**Worker Group**:
One registered set of Workers in a process, declaring which Queues it serves and its Dispatch Policy. Runs one or more Pumps (its `Pumps` count); a process may host several groups.
_Avoid_: Server (Hangfire's term; overloaded)

**Dispatch Policy**:
A Worker Group's rule for choosing which Queue to claim from next: Strict (ordered list, starvation accepted) or Weighted (smooth weighted round-robin — deterministic, no randomness). Both are work-conserving: a worker never idles while any served Queue has due work.

**Concurrency Limit**:
A per-Queue, cluster-wide cap on simultaneously executing jobs, enforced at claim time. A slot is released on terminal state or Lease expiry — never leaked by a crash. Distinct from Backpressure: the Concurrency Limit is one shared counter across the whole cluster for a Queue; Backpressure is each node's own pool capacity. The two caps are independent gates — a node may be Backpressured while the Limit has free slots, or Limit-saturated while its pool is idle — and neither is ever conflated with the other.
_Avoid_: Rate limit (N-per-minute is a different, unbuilt thing), semaphore

**Backpressure**:
A node stops claiming when its Worker pool has no free Worker — node-local flow control enforced in the Node Driver (claims never exceed free capacity), not the Shell. Distinct from the Concurrency Limit, which is cluster-wide and per-Queue.
_Avoid_: Rate limit, throttle (both imply a time-based cap; Backpressure is a capacity cap)

**Dependency**:
A static edge from a job to a parent set whose terminal states gate the job's due-ness (a countdown latch). The orchestration *mechanism*, living **below** the determinism boundary — simulated and fenced like any Core state. Two reaction modes only: on-success (default) and on-any-terminal. Edges are static, declared at the dependent's enqueue time. This is the Core's *only* dependency vocabulary; the Core never knows what Workflow, if any, a set of dependencies composes.
_Avoid_: Continuation (retired — was both mechanism and user concept; now split into Dependency + Workflow), child job, callback, Workflow (the user-facing grouping built *from* dependencies, never the edge itself)

**Workflow**:
The user-facing grouping and identity over a set of jobs connected by Dependency edges: a name, a sortable ID, a graph view, and lifecycle operations (e.g. cancel-all-non-terminal). Lives entirely **above** the determinism boundary — the Core never reads it and the Simulator records none, exactly like a Tag or Failure Detail. Its status (Running / Succeeded / Failed / Cancelled) is always a *projection* derived from member-job states, never authoritative stored state (the stored Workflow row holds only identity + config). A job belongs to **at most one** Workflow; every gating parent of a member must be a member of the *same* Workflow (store-enforced at enqueue). Because edges stay static per job, a Workflow may grow by **appending new jobs** but never by rewriting an existing job's dependencies — as dynamic as River and Hangfire, never the durable-execution, result-driven graph of Temporal — a dependent may *read* an ancestor's Job Output, but output never reshapes the graph. Appending live work to a drained Workflow legitimately reopens its derived status.
_Avoid_: Durable execution / workflow engine (no signals, waits, conditionals, step results, or replay), Continuation (retired term), Dynamic/result-driven graph, Batch (Hangfire's term)

**Workflow Input** *(Workflows v2 design term — the immutable seed of the typed builder)*:
The optional, immutable, build-time seed a Workflow is constructed with (`Workflow<TInput>(seed)`) — the constant, workflow-wide data every member can read, the args a developer would otherwise copy into every job's payload. Carried by **baking it into each member job's payload at enqueue** (no new storage mechanism, no `Workflows`-row read), read by a handler via `ctx.Input<TInput>()` and kept **distinct** from the step's own payload. Set once, never rewritten — so it stays entirely **above** the determinism boundary and is *not* durable-execution shared state: it is the opposite of a threaded, step-mutated context. A Workflow with no seed is valid. It is **not** how a step reads an upstream result — that is **Job Output**, pulled per typed step reference (`ctx.Output<TStep, TOut>()`); Workflow Input is only the constant given at the start.
_Avoid_: Context / `TContext` (renamed away — collides with the per-Attempt `JobContext` and evokes shared mutable state), State / Workflow State (the refused durable-execution term), Payload (the job-level term), Accumulator / shared context bag (there is none — a step contributes only its own Job Output)

**Step** *(Workflows v2 design term — the typed builder's unit)*:
A member of a Workflow, referenced in the typed builder by its **.NET type** rather than a string name. A Step is an ordinary `[Job]` **payload record** that additionally implements the marker `IWorkflowStep` (output-less) or `IWorkflowStep<TOut>` (declares its Job Output type) — the generic extends the non-generic, and `.Then(...)` is constrained to the marker, so composing a job into a Workflow is an explicit opt-in. Its **handler stays an ordinary `IJobHandler<TStep>`**: there is one handler model, workflow or not. Its builder identity is its **type** (plus an optional disambiguation name when the same type is used twice); its dashboard/span label is its **Wire Name** (what the graph already renders). A downstream Step reads an ancestor's typed output via `ctx.Output<TStep, TOut>()` and emits its own via `ctx.SetOutput<TStep, TOut>(value)` — both compile-checked against `IWorkflowStep<TOut>`, neither passing a string handle or `JsonTypeInfo` (the `[Job]` generator registers the output codec by step type). Whether `TStep` is actually an ancestor stays a **runtime** absence, not a compile-time proof. Carries no new below-boundary surface: it lowers to the same member job the v1 name-based builder emitted.
_Avoid_: Node (the v1 name-based builder term — retired), Activity (Temporal's durable-execution unit), Task (overloaded with `System.Threading.Tasks`)

**Workflow Definition** *(Workflows v2 design term — the reusable shape)*:
The reusable *shape* of a Workflow, declared once as a type implementing `IWorkflow<TInput>` with a `Build(builder, input)` method that wires its Steps. Distinct from a **Workflow Instance** — a single run, created by `client.StartWorkflow<TWorkflow>(seed)`, which executes the definition's `Build` with that run's Workflow Input seed to emit a **fresh** graph (new `WorkflowId`, new job identities). Per-Step data derives from the seed at instantiation (`.Then(new ValidateOrder(input.OrderId))`), and a definition may shape itself on the seed at **build time** (`if (input.IsPremium) …`) — never a runtime, result-driven reshaping. An **ad-hoc** inline form (`client.Workflow(seed).Then(…).EnqueueAsync()`) builds an unnamed one-off graph directly; both forms lower to the identical prepared-graph. The named definition is the stable anchor for the version *naming convention* (v2 ships no `DefinitionVersion` field - build-once instances finish as-built) and the type a child workflow splices in inline via `.ThenWorkflow<TChild>()` (one flat graph, not a nested identity).
_Avoid_: Workflow (the runtime grouping/identity — the Instance, not the shape), Template (evokes stringly-typed substitution), Durable program (no stored resumable program exists)

**Workflow Restart**:
Recovery by re-instantiating a Workflow's definition (each member's Wire Name, payload, Queue, Dependency edges, and mode) as a **brand-new Workflow** with fresh job identities, optionally linked to the original by lineage. Always re-runs the *whole* graph from the start — it is not resume-from-failure — so it re-executes already-Succeeded steps (idempotency is the handler's problem, as ever). Stays entirely above the determinism boundary because it only creates new jobs. The zero-extra-machinery recovery story that comes with the grouping layer — and therefore ships in BackWave Pro, since the grouping layer (Workflow) is a Pro feature.
_Avoid_: Retry (Restart makes a new Workflow; Retry reanimates the same one), Resume (Restart never resumes — it redoes)

**Workflow Retry**:
Recovery by **reanimation** — moving a Workflow's *terminal* members back to a non-terminal state **in place**, under the same identities, by explicit operator action. Three scopes: all members, only failed members, or failed members + their downstream dependents (the resume-from-failure scope that re-runs the failed node and everything below it, even previously-Succeeded dependents). The one recovery path that touches the determinism boundary — it introduces a terminal→active Core transition — so it carries new VOPR oracle surface and is sequenced as its own effort *after* VOPR Phase 3 lands. Not durable execution: every reanimated job still runs its whole handler from scratch.
_Avoid_: Restart (Retry is in-place, same identities), Resume (Retry's "failed + dependents" scope resumes; its other scopes do not), Un-cancel (auto-cascade still never reverses a Cancelled member — only explicit Retry does)

**Awaiting Parent**:
The state of a job whose Dependency parent set is not yet fully terminal. Invariant: every job in this state is reachable from a live or terminal parent — orphans are a Core bug under any crash interleaving.

**Cancelled**:
A job's terminal state of deliberate non-execution, with a recorded cause: parent failure (on-success Dependencies) or operator action. One state, several causes; stays Cancelled under every *automatic* path — a failed parent later requeued and succeeding never un-cancels it — and is reversible *only* by an explicit Workflow Retry (reanimation). Cancelling an executing job is cooperative — the handler's CancellationToken fires via heartbeat; threads are never killed.
_Avoid_: Skipped, aborted, deleted

**Operator Action**:
A dashboard- or API-initiated Core state transition: requeue (Dead-Lettered or Quarantined), cancel, trigger a Recurring Schedule now, pause/resume a Queue, cancel a whole Workflow, and Restart or Retry a Workflow. Always a defined state-machine transition with recorded identity — never a raw row edit. Editing a job's payload is deliberately not one.
_Avoid_: Admin override, manual fix

**Dashboard Permission**:
One of a small fixed set of capabilities (View, ViewSensitiveData, Requeue, Cancel, TriggerSchedule, PauseQueue), each delegated to the host app's authorization (policy name or predicate). BackWave never owns users or roles. **ViewSensitiveData** is the one gate over raw content that may carry secrets or PII — job payload bytes, Failure Detail, and Job Output — held separate from View so a reader can be granted the dashboard without being granted that content. It gates operator *viewing* only; the in-process accessor a handler uses to read its ancestors' Job Output is not gated by it.

**Dashboard Extension**:
A surface a separately-installed package (in practice a `BackWave.Pro.*` Dashboard package) contributes to the free Dashboard without the free Dashboard knowing about it — navigation entries, a banner, **page routes** (GET pages rendered through the same renderer, permission gate, and live-refresh path as the built-ins), and **action routes** (POST Operator Actions, each gated by an existing Dashboard Permission and antiforgery). The boundary is the **package reference**, never the license: a surface appears exactly when its extension is registered, and the only license-driven element is the soft unlicensed banner. The free Dashboard owns every security-critical concern (the View gate, permission + antiforgery, redirects, SSE); a Dashboard Extension supplies only the route shape, the component, the data loader, and the action handler. The Workflow surface is the first one — it lives in `BackWave.Pro.Dashboard`, leaving the free Dashboard with no Workflow UI.
_Avoid_: Plugin, widget, module

**Lease**:
A worker's time-bounded, heartbeat-renewed claim on a job. Expiry makes the job claimable again — the mechanism behind at-least-once delivery. Every handler may therefore run more than once; idempotency is the handler author's responsibility.
_Avoid_: Lock (a lease expires on its own; a lock implies indefinite ownership)

**Attempt**:
One execution try of a job, numbered and visible to the handler. A lease expiry counts as an attempt, the same as a thrown exception.
_Avoid_: Retry (retry is attempts after the first; counting "retries" invites off-by-one ambiguity)

**At-Least-Once Execution**:
BackWave's delivery contract: a job's handler body may run more than once, and idempotency is the handler author's responsibility. The field standard — Hangfire, Sidekiq, River, Celery (acks-late), RabbitMQ-with-acks, and Temporal *activities* all land here. Exactly-once *body* execution is not offered, because the only two roads to it are both rejected: at-most-once (accept job loss on crash) or durable execution. What BackWave still guarantees exactly once is the Effect-Once property.
_Avoid_: Exactly-once execution, at-most-once, deliver-once

**Effect-Once**:
What BackWave guarantees happens exactly once despite At-Least-Once Execution: the recorded outcome of an Attempt and every state transition that flows from it — terminal state, Continuation latch decrement, Concurrency-Limit slot release — apply once, caused by the node holding the live Lease for that exact Attempt. Enforced at the Storage Contract boundary by the (workerId, attempt) fence (§5.6): a stale outcome from a node that was isolated past its lease expiry mutates nothing. The fence is the single chokepoint — because every downstream effect is a store-applied consequence of the outcome write, fencing the write gives downstream-once for free. Not a stronger promise than peers, but a mechanically simulated one: the Node Isolation oracle proves it.
_Avoid_: Exactly-once delivery, idempotent storage, deduplication

**Transition Log**:
The append-only, per-job history of state changes the Monitor surfaces as a timeline — each entry a (timestamp, resulting state, Attempt number, optional Failure Detail). A Storage Contract capability the store records as it applies a state change; deterministic under Virtual Time. Bounded: capped per job life and deleted with the job under retention (§5.11). Records that a job *moved between states* — never the steps inside a handler; that line stays drawn. Configurable, not universal (like Transactional Enqueue): a global **Job History Policy** (Off / Transitions / Transitions + Failure Detail) governs how much is recorded; on with detail by default. Config is an input to a run, so it never affects determinism.
_Avoid_: Event log, execution trace, audit (Operator Actions have their own audit trail)

**Transition Observer**:
Host-supplied, egress-only code BackWave invokes when a job reaches a declared state — the sanctioned way to react to the lifecycle (e.g. "Dead-Lettered → post to Slack"). It observes Transitions (the Core's recorded outputs), never Events (the Core's inputs), and can never alter a Core decision: the push twin of the Monitor's pull. Driven by a Lease-claimed walk of the Transition Log, so it is delivered **at-least-once** (one node delivers each transition in the happy path; a crash mid-delivery redelivers) and **not Effect-Once** — the fire is a new side effect outside the (workerId, attempt) fence, so making the reaction idempotent is the subscriber's responsibility, the same contract as a handler. Requires Job History Policy ≥ Transitions (no log, nothing to observe); costs nothing when no Observer is registered. Cannot intercept, veto, or rewrite a transition — that is the permanently-rejected interceptor.
_Avoid_: Hook, Interceptor, Filter (imply in-path power it lacks), Event observer (it watches Transitions, not Events), Notifier (the subscriber's code notifies; BackWave dispatches)

**Failure Detail**:
The opaque diagnostic text — exception type, message, stack trace — the Shell captures at the edge when an Attempt throws and attaches to the failing Transition Log entry. The Core never reads it (it only learns the Attempt failed), so it never crosses the determinism boundary; the Simulator records none. Capture is operator-toggleable (off via config/env for PII-sensitive hosts) — the inner rung of the Job History Policy, since you cannot capture detail without recording the Transition it hangs on; viewing is gated behind the ViewSensitiveData Dashboard Permission.
_Avoid_: Error, terminal cause (TerminalCause is *why a terminal state was reached*; Failure Detail is the diagnostics of one failed attempt)

**Job Output**:
The opaque blob a handler optionally emits on success — the success-side twin of Failure Detail. A handler buffers it through `JobContext` during an Attempt; the store writes it to a column on the **job row** (not the Transition Log) atomically with the Succeeded transition, riding the same `(workerId, attempt)` Effect-Once fence as Failure Detail and the runtime Tag delta, so a fenced-out outcome discards it and no isolated node persists split-brain output. It is independent of Job History Policy — unlike Failure Detail, output is functional data a dependent must read, so recording history Off must not erase it. The Core never reads it, so it never crosses the determinism boundary; the Simulator records none. A job reads the output of its transitive **Dependency** ancestors on demand (lazy, by node name within a Workflow or by JobId for a raw dependency) — never a non-ancestor sibling's, because only an ancestor has the happens-before guarantee (its latch fired before this job was released) that the output is already written. Absence is a normal read result: a parent may have failed, been Cancelled, or simply emitted nothing (on-any-terminal dependents routinely run past non-success parents). Bounded by `MaxOutputBytes`, and over-limit output is **rejected, not truncated** (unlike Failure Detail) — a clipped serialized blob is undeserializable, so silent truncation would be data corruption. Viewing in the Dashboard is gated behind ViewSensitiveData; the in-process accessor a handler calls is ungated (it is the pipeline consuming its own data). Reading an ancestor's terminal state alongside its output is allowed — that is a handler branching on already-decided state, not BackWave altering the graph. Not durable execution: nothing is replayed, the graph stays static, and BackWave never reads output to make a scheduling decision.
_Avoid_: Step result / message passing (no channel, signal, or delivery — it is persisted data a dependent pulls), Result-driven graph (the graph never changes in response to output), Return value (buffered on the context, not returned from the handler signature)

**Dead-Lettered**:
The terminal state of a job that exhausted its attempt ceiling. Distinct from **Quarantined** (couldn't be routed or decoded); dead-lettered jobs ran and kept failing.
_Avoid_: Failed (non-terminal attempts also "fail"), poison queue

**Wire Name**:
A job type's mandatory, explicitly declared identity in storage. Stable across refactors by construction — never derived from CLR type names. Renaming a class never changes it; changing it is a reviewed, deliberate act (enforced via the checked-in Job Manifest).
_Avoid_: Job type name, class name

**Job Manifest**:
A committed snapshot of every registered Wire Name, verified by a shipped test helper. Makes wire-format changes visible in PR diffs instead of discovered in production.

**Quarantined**:
The explicit state of a stored job that cannot be routed — its Wire Name has no registered handler or its payload no longer deserializes. Loud and visible in monitoring; never a silent retry loop.
_Avoid_: Orphaned, poison (poison implies execution failures; quarantine is routing/decoding failure)

**Virtual Time**:
A controllable clock that tests advance explicitly, letting years of schedule activity run in milliseconds. The Core never reads the wall clock.

**BackWave Pro**:
The commercial add-on feature set, shipped as separate, publicly-available `BackWave.Pro.*` packages layered on top of the free base. Free to *use* for organizations under $1M annual revenue (honor system, self-reported at purchase); a paid, revenue-banded license is required above that threshold (a larger org pays more — the band changes price only). The library features are **identical** regardless of tier: a license never gates which features exist, only the *permission* to use Pro in a commercial-scale org plus access to email support (free use carries no commercial support). Enforced softly — an unlicensed production process runs normally but emits a startup log warning and a Dashboard banner; there is no hard-fail, and the software cannot and does not detect revenue. The free base (everything not in `BackWave.Pro.*`) is complete and production-grade on its own, free for everyone forever.
_Avoid_: Core (the functional decision logic — never a pricing tier), Community Edition / Free Edition (implies a crippled variant; the free base is complete), Enterprise tier (no feature-differentiated tiers — bands change price only), Trial/Freemium-crippling.

**Encryption at Rest**:
A BackWave Pro capability that stores the opaque user-data channels — job payloads, Job Output, Recurring Schedule templates, and Failure Detail — as ciphertext in the database, so a database-level compromise (a stolen dump, a replica, raw table access) yields no plaintext. Lives entirely **above the determinism boundary**: the Core and Simulator only ever move opaque bytes, so it adds no oracle surface and leaves the determinism battery unchanged — exactly like a Tag or Job Output. Protects data *at rest in the database*, never *from the application's own operators*: a holder of the ViewSensitiveData Dashboard Permission still sees decrypted content — it is not operator-blind encryption. Does **not** cover Tags or routing/observability metadata (Wire Name, Queue, state, due time), which stay plaintext so they remain searchable and claim ordering stays intact — so regulated data must never be placed in a Tag. Like every Pro feature it is honor-system and **soft on license lapse** (a lapsed license keeps encrypting and decrypting, never reverting to plaintext), but unlike them it fails **closed** on misconfiguration: enabling it without a configured key provider stops startup rather than silently storing plaintext. A job whose payload cannot be decrypted (e.g. its key was purged) becomes Quarantined, the same loud terminal state as one whose payload no longer deserializes.
_Avoid_: End-to-end encryption / operator-blind encryption (operators with ViewSensitiveData see plaintext), Column / field encryption (the database is unaware; BackWave encrypts above the storage boundary), Encryption in transit (a separate concern — transport security to the database).

**MCP Server**:
The BackWave Pro surface (`BackWave.Pro.Mcp`) that lets AI agents operate on jobs over the Model Context Protocol: a streamable-HTTP endpoint mounted in the consumer's host app under a path prefix (default `/backwave-mcp`, like the Dashboard) serving 23 tools that wrap the public Monitor reads and audited Operator writes 1:1 — plus the Workflow tools — never a new capability, only a new surface. Same delegation authorization as the Dashboard: host-supplied per-action callbacks, view defaults allow, writes and sensitive data default deny, every write audit-stamped via `ResolveActor`; tools the caller is denied are hidden from the tool list. Entirely Pro by discussed exception to the capabilities-Pro/surfaces-free rule; soft-fail like all Pro — fully functional unlicensed, with no license nag in tool results. No job creation: consumers enqueue through their own app/API.
_Avoid_: Agent API (it speaks the standard MCP protocol, not a bespoke API), Free surface (the one discussed tier exception), Copilot/assistant (BackWave ships tools; the model and the agent are the consumer's).

## Example dialogue

> **Dev:** Can the Simulator catch a bug in our Postgres `SKIP LOCKED` query?
> **Expert:** No — the Simulator stops at the Storage Contract. It runs the In-Memory Store, so it catches bugs in the Core and in how the Core handles storage faults. A wrong Postgres query is the Conformance Suite's job.
> **Dev:** A user wants to run the In-Memory Store in production for a single-node app.
> **Expert:** Unsupported, full stop. It's not durable. Production means a Storage Adapter over a database they already run.
> **Dev:** So what makes a test deterministic here?
> **Expert:** The Core does no I/O and never reads the wall clock — give it the same events and Virtual Time, you get the same decisions, every run.
