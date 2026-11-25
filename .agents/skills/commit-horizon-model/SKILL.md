---
name: commit-horizon-model
description: Model-check the Observer Commit Horizon with TLA+ and TLC. Use before you merge a change to observer_log_horizon, the writer epoch registration (the Postgres or Oracle observer_log_register trigger, the SQL Server RegisterTransitionWriter prefix), AdvanceCommitHorizonAsync, AdvanceObserverCursorAsync, the claim's catch-up sweep, CommitHorizon.Advance, or the observer_log_position sequence, and when a Transition Observer skips or stalls on a transition.
---

# Commit Horizon model

`tests/formal/commit-horizon/CommitHorizon.tla` is a TLA+ model of the Commit Horizon protocol as the Postgres, SQL Server, and Oracle adapters run it. TLC checks every order of its steps within small bounds.

- **CursorSafe** (storage contract 5.13): every committed matching Transition Log row at or below an Observer's cursor was delivered or dead-lettered. When it breaks, a transition commits below the cursor and no claim ever scans it, so the Observer never sees it. This is the late-commit skip that the Commit Horizon fixes.
- **HorizonSafe** (the lemma that localizes a break): every Position at or below `horizon_pos`, and at or below the horizon a report is using, is final. Its row committed or rolled back, and no session can still draw it.
- **Liveness**: after writers stop, `horizon_pos` reaches every committed row an Observer matches, and each Observer's cursor passes every committed row it matches.

The Simulator and the in-memory store give a row its Position at commit, so they cannot show a row that takes its Position at insert and commits late. This model can. Tests cannot replace it.

## Does your change touch the model?

The model was last confirmed against the code at commit `ad5b0ab` (the sync commit). Run this to list the covered files that differ from it, committed or not:

```bash
git diff --stat ad5b0ab -- src/BackWave/Storage/CommitHorizon.cs \
  src/BackWave.Postgres/PostgresJobStore.cs src/BackWave.SqlServer/SqlServerJobStore.cs src/BackWave.Oracle/OracleJobStore.cs \
  'src/BackWave.Postgres/Schema/*' 'src/BackWave.SqlServer/Schema/*' 'src/BackWave.Oracle/Schema/*'
```

The three store files are large, so a file in this list may not have changes in the covered code. Check the diff against the coverage record below. A change outside the record does not need the model.

| Code (paths from the repo root) | Model action |
|---|---|
| `src/BackWave/Storage/CommitHorizon.cs` `Advance` | `Advance`, transcribed line for line |
| Postgres `Schema/0005_observer_commit_horizon.sql` `observer_log_register` trigger, SQL Server `SqlServerJobStore.RegisterTransitionWriter` (the prefix of every `INSERT INTO backwave.job_transitions` in `SqlServerJobStore.cs`, today at :1966 and :2098), Oracle `Schema/0005_observer_commit_horizon.sql` trigger | `Register` |
| `observer_log_position` sequence and the `job_transitions.position` default in each `Schema/0001_initial.sql` | `NextVal` (`Cache` = 1) |
| The caller's transaction commit or rollback | `Commit`, `Rollback` |
| `ClaimObserverDeliveriesAsync` (lock statement with `resolvedAboveCursor`, scan with `LIMIT` and the backoff break, the catch-up branch) and `TryReportObserverDeliveriesAsync` (outcome writes) in each store | `Claim` |
| `AdvanceCommitHorizonAsync` in each store: the row lock with SKIP LOCKED or READPAST, the read of P, the two probes, the skipped-lock fallback, the `UPDATE` | `Snap`, `Lock`, `ReadH`, `ProbePrev`, `ProbeCur`, `Decided` |
| Postgres `observer_log_epoch_free`, SQL Server `APPLOCK_TEST`, Oracle `observer_log_epoch_free` | `EpochFree` |
| `AdvanceObserverCursorAsync` in each store: the block query, the target query, dead letters, the `observer_deliveries` delete, the cursor update, the commit | `Sweep` (and `Block`, `Target`, `Finish` for the no-horizon mutant) |

Each action's comment in the spec cites the file and line it models, and says why a fold of several statements into one action loses no order.

## Run the check

```bash
tests/formal/commit-horizon/check.sh
```

The first run needs network access, and Java 11 or later or mise. `tests/formal/tlc.sh` downloads the pinned tla2tools 1.7.4 and checks its hash, and with no Java it installs a JDK with mise. A run takes 2 to 5 minutes on a 10-core M-series Mac, depending on load. Add `--quick` to skip the two liveness runs, or `--deep` to add `Deep.cfg` (about 2.5 minutes more). Any other argument prints the usage and exits 2.

The script prints one line for each run and ends with `check passed` or `check FAILED`. Each line shows the result, the config, the wanted outcome, the exit code, the states, the property TLC named, and the length of its counterexample. A mutant's state count and trace length can change between runs, because TLC runs several workers and stops at the first violation it finds. A good run looks like this:

```
ok   CommitHorizon.cfg            want pass, exit 0, 1657076 distinct states found, 74s
ok   LockFirst.cfg                want pass, exit 0, 1357962 distinct states found, 47s
ok   RetryFiltered.cfg            want pass, exit 0, 1108972 distinct states found, 17s
ok   Liveness.cfg                 want pass, exit 0, 544924 distinct states found, 55s
ok   LivenessLockFirst.cfg        want pass, exit 0, 480406 distinct states found, 56s
ok   MutantProbePreviousOnly.cfg  want HorizonSafe, exit 12, 3890 distinct states found, got "Invariant HorizonSafe is violated", trace 11 states, 2s
ok   MutantProbePreviousOnly.cfg  want CursorSafe, exit 12, 12696 distinct states found, got "Invariant CursorSafe is violated", trace 14 states, 2s
...
ok   MutantNoHorizon.cfg          want CursorSafe, exit 12, 1474 distinct states found, got "Invariant CursorSafe is violated", trace 12 states, 1s
check passed
```

What each config checks:

| Config | Expected | What it is |
|---|---|---|
| `CommitHorizon.cfg` | pass | The real protocol, Postgres form: one statement reads P and the committed horizon from its snapshot, then locks the row, then probes. |
| `LockFirst.cfg` | pass | The real protocol, SQL Server and Oracle form: lock the row, read P in the next statement, then probe the two epochs in either order. |
| `RetryFiltered.cfg` | pass | The real protocol, Postgres form, with a Retry outcome and an Observer that matches only odd Positions. Two writers. |
| `Liveness.cfg` | pass | The real protocol, Postgres form, with fairness. Smaller bounds, because TLC checks liveness without symmetry. |
| `LivenessLockFirst.cfg` | pass | `Liveness.cfg` in the SQL Server and Oracle form. |
| `Deep.cfg` | pass | `CommitHorizon.cfg` with Positions up to 5. Only with `--deep`. |
| `MutantProbePreviousOnly.cfg` | HorizonSafe, then CursorSafe | Probe only the previous epoch. |
| `MutantProbeCurrentOnly.cfg` | HorizonSafe, then CursorSafe | Probe only the current epoch. |
| `MutantPAfterProbes.cfg` | HorizonSafe, then CursorSafe | Read P after the probes. |
| `MutantSequenceCache.cfg` | HorizonSafe, then CursorSafe | A sequence with a per-session cache of 2, so Positions are drawn out of order. |
| `MutantDrawBeforeRegister.cfg` | HorizonSafe, then CursorSafe | A row can draw its Position before its statement registers, as a SQL Server insert without the `RegisterTransitionWriter` prefix does. |
| `MutantNoHorizon.cfg` | CursorSafe | The rule before the Commit Horizon: the cursor ignores the horizon. |

For each horizon mutant the script runs twice. The first run reports the lemma, which breaks first. The second run checks CursorSafe alone and shows the lost delivery itself.

The counterexamples the mutants produce, in the code's words. A, B are writer sessions, and the row of Position n is "row n":

- **Probe previous only:** A registers on epoch 0 and inserts row 1, still open. B inserts row 2 and commits. The report reads P = 2, finds epoch 1 free, takes epoch 0 as free, and sets `horizon_pos` = 2 and the cursor to 2. A commits row 1 below the cursor.
- **Probe current only:** A holds row 1 open on epoch 0, and B commits row 2. A report finds epoch 0 busy and flips to epoch 1 with candidate 2. The next report takes epoch 0 as free, finds epoch 1 free, and sets `horizon_pos` = 2 and the cursor to 2. A commits row 1.
- **P after the probes:** a report locks the row and finds both epochs free. Then A inserts row 1, and B inserts and commits row 2. The report reads P = 2 and publishes `horizon_pos` = 2. The Observer's report skips the lock, reads `horizon_pos` = 2, and moves the cursor to 2. A commits row 1.
- **Sequence cache 2:** A's session reserves Positions 1-2 and commits row 1. B's session reserves 3-4 and commits row 3. The report reads P = 3, finds both epochs free, and decides `horizon_pos` = 3. A's next transaction registers and draws 2 from its cache. The cursor moves to 3, and A commits row 2 below it.
- **Draw before register:** B's statement draws row 1 without registering. A registers on epoch 0, inserts row 2, and commits. The report reads P = 2, finds both epochs free, and sets `horizon_pos` = 2 and the cursor to 2. B commits row 1 below the cursor.
- **No horizon:** row 1 is committed and A holds row 2 open. The claim delivers row 1. The block query finds no undelivered row, A commits row 2, and the target query returns 2. The cursor moves to 2, and row 2 is never delivered.

## Read a result

When a run fails, `check.sh` keeps its full TLC output in `tests/formal/commit-horizon/.failures/<config>.log`. TLC prints the counterexample there as a numbered list of states, each headed by the action that reached it.

To read a result as a path through states, use the `/statechart` skill. It finds the shortest path to each broken rule, step by step. The configs here have more than a million states, so give it a copy of the config with smaller bounds and no `SYMMETRY` line, for example two writers. With two writers and Positions up to 4, every mutant above still breaks.

## A real bug or a modeling error

When the real model fails, decide which one it is before you change anything.

1. Translate the trace into code steps. Each TLC step names an action, and the action's comment cites the code. Write the trace as a short list such as "writer 1 registers on epoch 0 and draws Position 1; report reads P = 2; ...".
2. For each step, open the cited code and confirm that the code can take that step in that state. Check the order of statements, what each statement's snapshot sees, and which lock it holds.
3. If every step is possible in the code, it is a real bug. Report it with the trace in code terms. Do not weaken a property or the model to make the run pass, and do not change product code unless the user asks.
4. If a step is impossible in the code, it is a modeling error. Fix the action so that it matches the code, and cite the line that shows it. Then rerun every config: each mutant must still fail.

Signs of a modeling error: the trace uses a value the code never reads, such as a writer's epoch that the code can never return; two code statements happen in the model in an order the code does not allow; or an action folds statements whose fold comment no longer holds.

A deadlock (exit 11) means some actor has no step out of a state that is not the end state. That is a modeling error, unless the code really can hang there.

## Update the model after an intended change

1. Read the changed code and find each model action it touches with the coverage record.
2. Change the action so that it does what the code does now. One atomic code step is one action, or a fold with a comment that says why no order is lost. Update the line citations.
3. If the change adds a step that a property depends on, add a mutant config that removes or reorders it, and add it to `check.sh`.
4. Every other config is a copy of `CommitHorizon.cfg` with a few constants changed. If you change a bound or an invariant list in `CommitHorizon.cfg`, make the same change in each of them.
5. Run `tests/formal/commit-horizon/check.sh`. The real configs must pass, and each mutant must fail with the property the script expects.
6. Move the sync commit in this file forward only after you confirm that the model describes the code at the new commit. Update the coverage record in the same edit.

To bring the model back in line after changes that skipped this skill, use `/maintain-formal-verification-skill`.

## Scope

Bounds of `CommitHorizon.cfg` and `LockFirst.cfg`: 3 writer sessions (symmetric), up to 2 rows a transaction, Positions 1-4, one Observer with `maxRows` 2, and one stand-in report that runs once. `RetryFiltered.cfg` has 2 writers, 1 Retry, and an odd-only Observer. `Liveness.cfg` and `LivenessLockFirst.cfg` have 2 writers, Positions 1-3, and 1 Retry. `Deep.cfg` has Positions 1-5.

Distinct states (time on a 10-core Mac, from idle to a load average near 350): `CommitHorizon.cfg` 1,657,076 (16-74 s), `LockFirst.cfg` 1,357,962 (47 s loaded), `RetryFiltered.cfg` 1,108,972 (9-17 s), `Liveness.cfg` 544,924 (24-55 s), `LivenessLockFirst.cfg` 480,406 (56 s loaded), `Deep.cfg` 12,933,881 (144 s). Each mutant fails in 1-5 s. The example above is a full run at that load, 4 minutes 37 seconds.

What the model assumes:

- A writer can register on either epoch. That covers a stale read of `epoch`, for example by a REPEATABLE READ, SERIALIZABLE, or SNAPSHOT caller transaction, so the read folds into the lock step.
- A commit becomes visible no later than it releases the transaction's locks, as in each engine.
- Every SQL Server Transition Log insert registers. SQL Server has no trigger (`src/BackWave.SqlServer/Schema/0006_observer_commit_horizon.sql:5`), so each `INSERT INTO backwave.job_transitions` in `SqlServerJobStore.cs` must start with the `{RegisterTransitionWriter}` prefix, today at :1966 and :2098. `MutantDrawBeforeRegister.cfg` shows what one insert without it breaks.
- Claim and report transactions run at READ COMMITTED, so each statement takes a fresh snapshot. No adapter passes an isolation level when it begins them, so each takes the default. Postgres: `BeginTransactionAsync` at `PostgresJobStore.cs:3129` (claim) and `:3295` (report) uses the Npgsql default, which sends `BEGIN TRANSACTION ISOLATION LEVEL READ COMMITTED`. SQL Server: `BeginTransactionAsync` at `SqlServerJobStore.cs:3332-3333` and `:3500-3501` uses the SqlClient default READ COMMITTED, and the migrator turns on `READ_COMMITTED_SNAPSHOT` (`SqlServerMigrator.cs:260`), so each statement reads its own snapshot. Oracle: `BeginAsync` (`OracleJobStore.cs:211-213`) at `:3503` and `:3681` uses the Oracle default READ COMMITTED.
- Each Observer has one claim and report worker, so the lease fence is not modeled. A second Observer's report is a stand-in (`Reporters`) that runs the horizon step at any time, up to `ReporterRuns` times.
- Dead-lettered is the same as delivered for the cursor. A Retry holds the cursor until its backoff passes.

What it leaves out: the lease and fence, crashes in the middle of a transaction, report transactions that roll back, transition pruning and retention deletes, the N-1 rolling-upgrade nodes (a SQL Server node on the previous version inserts without the `RegisterTransitionWriter` prefix, so it does not register), SQLite and the in-memory store, the Simulator, and the lag reading.
