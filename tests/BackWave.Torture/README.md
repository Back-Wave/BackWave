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
docker compose up -d postgres sqlserver   # not needed for the sqlite shapes

dotnet run -- --adapter postgres              --duration 5m
dotnet run -- --adapter sqlserver             --duration 5m
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
finding), ClientCrash.

## Artifact bundle

On violation, `torture-artifacts/torture-<adapter>-<seed>-<utc>/` holds `run.json` (options, seed,
coverage stats), `journal.jsonl` (merged, time-ordered), `violations.json`, `store-dump.json`
(every job + its history + tags), and raw table dumps (`table-*.json`, or the SQLite file itself).
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
