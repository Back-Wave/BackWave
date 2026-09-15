# BackWave.Torture — the Torture Suite, store mode

Issue 0200 / ADR 0039. A deliberately **non-deterministic** discovery instrument: N concurrent
synthetic clients hammer a real Storage Adapter with a randomized, collision-engineered workload,
then the run drains to quiescence and a full oracle audit runs over the end state, every job's
Transition Log, and the merged client observation journals.

It fills the quadrant the other instruments refuse: the Simulator/VOPR are deterministic and never
touch a real adapter; the Conformance Suite drives real databases but sequentially (plus targeted
deterministic interleavings). Nothing else *discovers* unknown concurrency anomalies on live
Postgres/SQL Server/SQLite — the 0178/0193/0194/0195 class was caught by code review, not tests.

**Never a PR gate.** Discovery runs are nightly/local only (nightly CI wiring is issue 0199).
A torture failure is always a bug, never noise — every oracle is sound under wall-clock
nondeterminism. Regression teeth live in the deterministic Conformance Suite: distill every
confirmed finding into a per-site clause (0196 pattern,
`docs/adapter-concurrency-review-checklist.md`) and file it as `torture-NNNN`.

## Running

```bash
docker compose up -d postgres sqlserver oracle   # not needed for the sqlite shapes

dotnet run -- --adapter postgres              --duration 5m
dotnet run -- --adapter sqlserver             --duration 5m
dotnet run -- --adapter oracle                --duration 5m
dotnet run -- --adapter sqlite                --duration 5m
dotnet run -- --adapter sqlite-multiprocess   --duration 5m   # 4 real OS processes, one WAL file
```

Options: `--seed <n|0xHEX>` (default random, always logged — every random decision derives from
it), `--clients` (8), `--processes` (4, multi-process only), `--max-attempts` (4),
`--drain-bound` (90s), `--governed-limit` (2), `--artifacts <dir>`.

Exit codes: `0` clean (coverage stats printed), `1` violations (artifact bundle written),
`2` infrastructure error, `3` usage.

Postgres/SQL Server run in a dedicated `backwave_torture` database (created on demand, wiped per
run) so they never collide with the conformance suite's `backwave_test`. SQLite gets a fresh temp
file per run, deleted afterwards.

## Coverage ledger

The store-mode twin of the VOPR coverage ledger: when `BACKWAVE_TORTURE_LEDGER` points at a JSONL
file, every run appends one line — clean or red — capturing that run's real-load contribution
(hours hammered, jobs audited, store ops, duplicate-key races provoked, and any tripped invariants).
It is append-only; nothing is ever rewritten, so cumulative coverage only grows. Optionally set
`BACKWAVE_TORTURE_SHA` to stamp each entry with the commit under test.

```bash
export BACKWAVE_TORTURE_LEDGER=.vopr-overnight/torture-ledger.jsonl
dotnet run -- --adapter postgres --duration 5m      # …runs append to the ledger…
dotnet run -- --stats                               # roll the ledger up into docs/torture-coverage.md
```

`--stats` folds the ledger into the headline (total real load, per-adapter breakdown, distinct
invariants ever surfaced) and stamps the citable `docs/torture-coverage.md`
(override with `BACKWAVE_TORTURE_COVERAGE_DOC`). Unlike VOPR's virtual cluster-time, the unit here
is real wall-clock concurrent load on a live adapter. The cycled driver
(`.vopr-overnight/run-torture-cycled.sh`) sets these and rolls up automatically at the end.

## Workload

Each client is a seeded-PRNG loop over the store surface: enqueue (with duplicate-JobId pressure
from a seed-derived collision pool whose window slides with wall time), claim/execute/report
(single and batched outcomes, added tags, output blobs, occasional lease abandonment), heartbeats
(including stray ones for fence pressure), `ExpireLeases`, operator cancel/requeue, pause/resume
and limit-set on config queues (first-config races), and workflow create/append with shared
WorkflowIds. Collision-pool writes use **barrier alignment** (a shared 250 ms wall-clock boundary)
so the same key's *first* inserts genuinely race across connections — natural timing essentially
never hits the sub-millisecond check-then-insert window.

Workload discipline the oracles rely on: designated-unroutable wires are always reported
`Unroutable` and never executed; `Cancelled` outcomes only follow an observed cancel request;
failure retries respect the attempt ceiling; the governed queue's concurrency limit is set once,
before the workload, and never touched again.

## Oracles

Store-side (end state + Transition Log): LegalInitialState, LegalTransition, AttemptMonotonic
(requeue resets allowed), AttemptCeiling, TerminalStable (log tail vs row), TerminalTimestamp,
LeaseOwnerPresent/LeaseOwnerCleared, QuarantineNotExecuted, NoAwaitingParentOrphan,
CancelProvenance, DrainLiveness (bounded-wait drain), plus raw-row audits the set-typed reads
can't see (DuplicateTagRows, DuplicateEdgeRows).

Journal cross-checks (client observations): NoDoubleExecution and SlotDoubleRelease (Effect-Once
per (job, attempt), allowing one extra life per successful requeue), NoOverlap and ConcurrencyLimit
via **conservative lease intervals** — a lease was *definitely* live from the claim's return until
the earliest of its first outcome report's start, its claim-set expiry, or any renewed-heartbeat
expiry (renewals can shorten a lease) — OutcomeProvenance (fence supersession),
DuplicateEnqueueAccepted / DuplicateWorkflowAccepted (at most one `Ok` per shared key),
EnqueueDurability (accepted ⇒ present; present ⇒ accepted), TagDurability (accepted tag writes
survive), RawStoreException (a raw provider exception escaping the store surface is itself a
finding), HaltTriggerFired (an adapter raised the production `InvariantViolationException`, which in
production takes the worker group out of service; the finding names the tripped `InvariantTrigger`),
DegradeTriggerFired, ClientCrash.

The Degrade half of the fail-stop vocabulary throws nothing - the site counts the impossible state and
carries on down its benign branch - and a torture process registers no log provider, so the
`backwave.invariant.violations` counter is the only surface it reaches here. `DegradeWatch` subscribes to that
counter (in the parent and in each SQLite child process, whose journal is merged home) and journals every
Degrade measurement by trigger id; the run sweeps the whole journal for them once, at the end, and goes RED.
Degrading keeps a production node in service; it does not make the state legal, and the suite has no benign
branch. Halt measurements are ignored there on purpose: they are emitted by the Hosting pump, which no
torture process runs, and the throw that provoked them is already journaled by the client that saw it.

## Mid-run audit pass

The post-drain audit finds a violation long after its cause. A background loop in `TortureRun` also
audits **while the workload runs**, so a finding lands near the state that produced it.

There is no barrier and no quiescent point: the clients never stop. The pass therefore runs only
the checks that stay sound against a live store - a per-row check reads one atomic row, and a
journal-only check is monotone under append. It walks `job_transitions.position` forward with one
joined query per pass (the transition AND its job row from a single snapshot), through raw adapter
SQL on all four dialects. The interval is seed-derived, 5 to 20 seconds; a pass that overruns its
slot skips the next one and counts the skip.

Mid-run: LegalInitialState, LegalTransition, AttemptMonotonic, AttemptCeiling, TerminalTimestamp,
LeaseOwnerPresent/LeaseOwnerCleared, the store half of QuarantineNotExecuted, RawStoreException,
HaltTriggerFired, ClientCrash, DuplicateEnqueueAccepted, DuplicateWorkflowAccepted, NoDoubleExecution,
SlotDoubleRelease, OutcomeProvenance.

The journal-only half of that list - RawStoreException, HaltTriggerFired, ClientCrash,
DuplicateEnqueueAccepted, DuplicateWorkflowAccepted, NoDoubleExecution, SlotDoubleRelease,
OutcomeProvenance - reaches mid-run on every adapter except `SqliteMultiProcess`. There the clients are
child processes that hand their journal to the parent only as they exit, which is after the time box has
already ended the loop, so the parent's journal is empty for the whole workload. The pass says so on its
console line and in `run.json` (`JournalHalfLive`) rather than counting an empty read as a clean one, and
those checks still run in full in the post-drain audit over the merged journal.

Held back to post-drain (each compares two sources read at different instants, and `WorkloadClient`
journals *after* the store call returns): DrainLiveness, TerminalStable, NoAwaitingParentOrphan,
EnqueueDurability, NoOverlap, ConcurrencyLimit. DegradeTriggerFired is held back for a different reason: it
is swept once over the whole journal at the end, so it cannot be double-reported by two passes over
overlapping prefixes, and so the drain and the audit are themselves covered.

The pass is a net, never a replacement: the post-drain audit still runs in full and remains the
complete one. On the first mid-run violation the run **fails fast** - the time box is cut short, the
drain is skipped so the store stays close to the cause, and the bundle marks its post-drain section
absent with the reason. `UpgradeRun` does not get the pass. Budget: under 5 percent of wall time,
measured 0.34 percent over a 540 s SQLite run at the shortest interval the seed can pick (107 passes,
50,701 transitions walked, 0 skips, 1.84 s).

## Artifact bundle

On violation, `torture-artifacts/torture-<adapter>-<seed>-<utc>/` holds `run.json` (options, seed,
coverage stats, mid-run pass counters, and whether the post-drain section is present),
`journal.jsonl` (merged, time-ordered), `violations.json` (each finding stamped with `DetectedAt`
and its `Phase`), `store-dump.json` (every job + its history + tags), and raw table dumps (`table-*.json`, or the SQLite file itself).
Repro is best-effort by design — the bundle is what makes hand-diagnosis possible.

## Sabotage self-test

Proof the instrument catches the class it was built for: re-introduce a known anomaly and watch it
go RED inside the time box. Verified 2026-07-02 by disabling the 0194 duplicate-key catch in
`SqlServerJobStore` enqueue (raw 2627 escapes on collision-pool birth races): RED in a 15 s box on
two different seeds (7 escapes / 4 distinct ids on seed 42), bundle sufficient to hand-diagnose.
To repeat: guard that catch with a temporary local edit, run `--adapter sqlserver --duration 15s`,
confirm RED + RawStoreException, revert the edit.

Note for sabotage-target selection: the 0195 *edge/tag* sites cannot be raced naturally even under
torture — a primary-key insert always serializes ahead of them in the same transaction — which is
exactly why their regression teeth are forced-interleaving Conformance clauses instead
(`Clause_5_6_ConcurrentDuplicateTagInsert…`, `Clause_5_1_ConcurrentDuplicateWorkflowEdge…`).
