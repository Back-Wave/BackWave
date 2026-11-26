---
name: lease-fencing-model
description: TLA+ model of lease fencing in the Postgres, SQL Server, and Oracle job stores (Claim, Heartbeat, ExpireLeases, RelinquishLeases, ReportOutcome(s), Reschedule) and the Transition Log ordinals and cap they write, checked with TLC at the level of SQL statements and row locks. Use when a change touches the SQL, locking, or transaction shape of those methods, to check a lease race, or to read a model result.
---

# Lease fencing model

`tests/formal/lease-fencing/LeaseFencing.tla` models the jobs rows, the row locks, and two database sessions working for two workers. Each model step is one statement, or one row lock inside a statement, of the adapter code. Every action carries a comment that cites the file and lines it models. TLC checks every order of those steps within the bounds below.

## Rules

Each rule is a TLA+ invariant. The harm is what a user sees when the rule breaks.

| Rule | Property | Harm |
|---|---|---|
| `SingleLease` | A job has at most one live lease. | Two workers run the job at the same time. |
| `OutcomeFenced` | An outcome write lands only on the reporter's live lease: `Leased`, same owner, same attempt, not expired at the call's `@now`. | A stale or late outcome completes or fails another run of the job. |
| `ClaimOnlyDue` | A claim leases only rows that are due at the claim's `@now`. A claim of a row that is not `Scheduled` breaks `SingleLease` or `TerminalStays` instead. | A job runs before its due time, for example after a Reschedule moved it. |
| `ExpireOnce` | An expiry sweep writes only rows that are `Leased` and lapsed at the sweep's `@now`. | A finished run is reset, or a renewed lease is taken from a running worker. |
| `RelinquishOnlyHeld` | A relinquish writes only leases the calling worker holds. | A finished or foreign job goes back to `Scheduled`. |
| `TerminalStays` | A job that reached `Succeeded` or `DeadLettered` stays there. | A finished job runs again. |
| `NoHalt` | The Oracle claim's C# due check (`Invariant.Halt`) never fires. | The node fail-stops. |
| `OwnerOnlyWhileLeased` | `lease_owner` and `lease_expiry` are set exactly when the state is `Leased`. | None by itself. It makes `lease_owner = @owner` imply `Leased`, so the `state = 2` guard in RelinquishLeases is a second guard. |
| `LogOrdered` | A job's Transition Log ordinals rise in commit order. | Job history and the Transition Observer show a job's transitions out of order. |
| `LogCapped` | A job keeps at most `MaxTransitionsPerJob` log rows. | The log grows past its storage bound. |
| `NoOrdinalCollision` | The Postgres one-statement claim never inserts an ordinal that the log already holds. | The claim fails with a primary-key error, because it no longer catches one. |

Bounds: 2 jobs (symmetric), 2 sessions for workers `w1` and `w2`, `MaxAttempts` 2, a lease of 2 ticks, a clock from 0 to 2, a call `@now` that can lag the clock by 1 tick, both outcome paths, and a `MaxTransitionsPerJob` (`TransitionCap`) of 1. A pass covers these bounds and the abstractions in the spec header, not the whole system.

## Does the change touch the model

The model describes the code at the sync commit, the last commit that changed `LeaseFencing.tla`. Find it with `git log -1 --format=%h -- tests/formal/lease-fencing/LeaseFencing.tla`. Compare the change with this coverage record. Line numbers are at the sync commit. Each method is the untraced body unless the table names another.

| Operation | Postgres `PostgresJobStore.cs` | SQL Server `SqlServerJobStore.cs` | Oracle `OracleJobStore.cs` | Model actions |
|---|---|---|---|---|
| Claim | `ClaimUntracedAsync` 499, path choice 515-522; one-statement claim `ClaimInOneStatementAsync` 547, SQL 114-120; transaction claim `ClaimInTransactionAsync` 569, SQL 628; `ClaimCandidates` 644-653, `ClaimUpdate` 660-672 | `ClaimQueueAsync` 492, SQL 564-583 | `ClaimUntracedAsync` 498, SQL 581-592, due check 610-621, write 629-634 | `ClaimBegin`, `ClaimLock`, `ClaimCommit` |
| ReportOutcome | `ReportOutcomeUntracedAsync` 843, SQL 902-905 | `ReportOutcomeUntracedAsync` 782, SQL 842-845 | `ReportOutcomeUntracedAsync` 848, SQL 907-909 | `ReportUpdate` |
| ReportOutcomes | `ReportOutcomesUntracedAsync` 966, path choice 993-996; `TryReportOutcomesInOneRoundTripAsync` 1081; `ReportOutcomesInTransactionAsync` 1193; SQL `OutcomeCommandSql` 1124-1132, `OutcomeFence` 1146-1150, `LockOutcomeRows` 1160-1168, `OutcomeWrite` 1175-1191 | `ReportOutcomesUntracedAsync` 908, SQL 995-1019 | `ReportOutcomesUntracedAsync` 970, SQL 1041-1045, 1064-1071, 1090-1115 | `ReportLock`, `ReportCommit` |
| Heartbeat | `HeartbeatAsync` 1389, SQL 1398-1400 | `HeartbeatAsync` 1250, SQL 1266-1272 | `HeartbeatAsync` 1375, SQL 1400-1403, 1419 | `HeartbeatUpdate`, `HeartbeatLock`, `HeartbeatCommit` |
| ExpireLeases | `ExpireLeasesUntracedAsync` 1443, SQL 1461-1466, 1525-1527, 1540-1542 | `ExpireLeasesUntracedAsync` 1315, SQL 1335-1338, 1401, 1419 | `ExpireLeasesUntracedAsync` 1452, SQL 1473-1484, 1547-1556, 1565-1577 | `ExpireBegin`, `ExpireLock`, `ExpireCommit` |
| RelinquishLeases | `RelinquishLeasesUntracedAsync` 1601, SQL 1614-1618, 1680-1682, 1703-1705 | `RelinquishLeasesUntracedAsync` 1486, SQL 1501-1504, 1562-1563, 1578 | `RelinquishLeasesUntracedAsync` 1637, SQL 1650-1654, 1715-1722, 1737-1743 | `RelinquishBegin`, `RelinquishLock`, `RelinquishCommit` |
| Reschedule | `RescheduleCoreAsync` 1876, SQL 1891-1898 | `RescheduleCoreAsync` 1757, SQL 1767-1769 | `RescheduleCoreAsync` 1920, SQL 1936-1941 | `RescheduleLock`, `RescheduleCommit` |
| Transition Log | `TransitionLogCtes` 683-703, `RecordTransitionsBatchAsync` 2089 | `RecordTransitionsBatchAsync` 2015 | `RecordTransitionsBatchAsync` 2177 | `LogAppend`, `Record`, in `ClaimCommit`, `ApplyOutcome`, `ExpireCommit`, `RelinquishCommit` |

The model also depends on these facts. A change to any of them touches the model too:

- `src/BackWave.SqlServer/SqlServerMigrator.cs:260` turns on READ_COMMITTED_SNAPSHOT.
- `src/BackWave/Core/RetryPolicy.cs:56-57` (`RetryPolicy.NextAttemptAt`) and `166-167` (`RetryDisposition.NextAttemptAt`) dead-letter when the failed attempt reaches `MaxAttempts`.
- `src/BackWave/Driver/NodeDriver.cs:153-155` picks the outcome from the run's attempt, through a per-job `[Retry]` override or the Worker Group policy.

To list the commits since the sync commit that touched covered code:

```sh
git log --oneline "$(git log -1 --format=%H -- tests/formal/lease-fencing/LeaseFencing.tla)"..HEAD -- src/BackWave.Postgres/PostgresJobStore.cs src/BackWave.SqlServer/SqlServerJobStore.cs src/BackWave.Oracle/OracleJobStore.cs src/BackWave.SqlServer/SqlServerMigrator.cs src/BackWave/Core/RetryPolicy.cs src/BackWave/Driver/NodeDriver.cs
```

A commit touches the model when it changes a cited statement, its `WHERE`, its lock hints, its transaction, the statements sent as one command, or the order of statements inside one transaction. A change elsewhere in these files needs only new line numbers.

## Run the check

```sh
tests/formal/lease-fencing/check.sh
```

The first run needs network access, and Java 11 or later or mise. `tests/formal/tlc.sh` downloads the pinned tla2tools.jar and checks its hash, and with no Java it installs a JDK with mise.

It runs TLC on every config in `tests/formal/lease-fencing/` and prints one row per config: the expected result, the result, the distinct states, and the seconds. Each config's first line, `\* expect: pass` or `\* expect: <Rule>`, says what it must end with. The script exits 0 only when every row matches. Add `--mutants` for the planted breaks in `mutants/` or `--all` for both. Run `--all` after you change the model. Each config that passes on Oracle takes about 2 to 5 minutes, and every other config takes under a minute. With the default 5 workers on a 10-core Mac, `--all` took 17 minutes 26 seconds at a load average that fell from about 510 to 145 (2026-11-26). Run `uptime` first, because load slows every config. Add `--config <name>` to run one config, for example `--config oracle.cfg` or `--config mutants/claim-no-lock-oracle.cfg`.

`tlc.sh` runs one TLC process at a time on the machine, from any checkout. A second run prints `Waiting for the TLC run of PID <pid> to finish` and starts when the first ends. The seconds the script prints include that wait. Each run uses half the CPU cores and a 4 GiB heap. Set `TLC_WORKERS` (a number, or `auto` for every core) or `TLC_HEAP` (an `-Xmx` value such as `8g`) to change that.

When TLC exits with a code other than 0 or 12, the row reads `error:<code>` and the script prints the last 20 lines of TLC's output under it, which name the reason, such as a Java error.

The baseline at the sync commit: every row matches, and the three adapter configs pass with these distinct-state counts. A count that changes without a model change means the bounds or the TLC version changed.

| Config | Distinct states |
|---|---|
| `oracle.cfg` | 14,110,656 |
| `postgres.cfg` | 1,947,808 |
| `sqlserver.cfg` | 2,698,212 |

Read every result with the pstack `/statechart` skill. A violation comes back as its shortest path in plain steps, and a pass comes back as a chart of the states. The raw TLC output of each config is in the folder that `check.sh` prints last.

## Tell a real bug from a modeling error

Take the shortest path and replay it step by step against the code:

1. For each step, open the lines that its action cites. The step must be one statement, or one row lock inside a statement, of that method.
2. Check the values that the step reads against the isolation rule of that adapter in the spec header. A snapshot read sees only rows committed before the statement started. A locked row is checked again on its latest committed version, with only the predicates that the rule says are checked again. If the path depends on what a lock does with a row that changed after the snapshot, measure that on the database before you trust the rule. For Oracle `FOR UPDATE SKIP LOCKED`, the probe below does it.
3. Check the session rules. One session runs one operation at a time. Locks last until the commit. A rollback can come at any point before the commit.

If any step does what the code cannot do, the model is wrong. Fix the action and its comment, then run `check.sh --all` to confirm that every mutant still fails. If every step is possible, the code is wrong. Confirm it with a two-session run on a real database; documentation and published reproductions are not enough. Label the finding measured or inferred, and report it with the path in code terms. Keep the rule and the model as they are. Until the code is fixed, change the adapter config to expect the rule that breaks.

## Update the model after an intended change

1. Find the actions of the changed statements in the coverage record.
2. Change each action so that each step is again one atomic step of the new code. Update the citation in its comment.
3. Definitions such as `Fence` serve every adapter, and the config's `Adapter` constant picks the adapter. When a change alters a guard in one adapter only, gate the guard on that adapter, so the other adapters' configs keep it. For example, an Oracle outcome that drops `attempt = :attempt` becomes `(r.attempt = a \/ Has("OwnerOnlyFence") \/ Adapter = "Oracle")` in `Fence`.
4. A new guard gets a mutant. Add its name to the `Mutants` `ASSUME`, gate the guard on `Has("<Name>")`, and add a config in `mutants/` that expects the rule the guard protects. When a change removes a guard, find the mutant config that planted the same removal: the real model now ends as that config does.
5. Run `check.sh --all`. Every row must match.
6. Update every file and line citation: in the spec comments, in the probe, and in this skill, including the coverage record. Then move the sync commit, here and in the spec header, to the commit you checked. Move it only after you have confirmed that the model describes the code at that commit.

## Mutants

Each config in `mutants/` plants one break and must end on the rule named in its first line.

| Config | Planted break | Ends on |
|---|---|---|
| `owner-only-fence-{postgres,sqlserver,oracle}` | Outcome fences without `attempt = @attempt` | `OutcomeFenced` |
| `claim-no-lock-{postgres,sqlserver,oracle}` | Claim read without its row lock | `SingleLease` |
| `claim-no-due-recheck-oracle` | Oracle claim without the outer `due_time <= :now` and the C# due check, as before `6e1db1b` | pass |
| `claim-no-outer-due-oracle` | Oracle claim without the outer `due_time <= :now` | pass |
| `expire-no-outer-where-oracle` | Oracle ExpireLeases without the outer `state = 2 AND lease_expiry <= :now`, as the code was before the outer `WHERE` | pass |
| `expire-no-outer-order-oracle` | Oracle ExpireLeases without its outer `ORDER BY lease_expiry` | pass |
| `expire-no-outer-where-and-order-oracle` | Both of the two above | `ExpireOnce` |
| `relinquish-no-leased-check-postgres` | RelinquishLeases without `state = 2` | pass |
| `outcome-keeps-owner-postgres` | A `Succeeded` write that keeps `lease_owner` | pass |
| `relinquish-no-leased-check-and-outcome-keeps-owner-postgres` | Both of the two above | `RelinquishOnlyHeld` |
| `ordinal-from-snapshot-postgres` | One-statement claim without the row-version filter; a collision rolls back, as the catch before `c0f2e3c` did | `LogOrdered` |
| `no-window-check-postgres` | One-statement claim without the filter, with the ordinal read from the locked row (a per-job counter) and the prune from the snapshot | `LogCapped` |
| `legacy-writer-no-bridge-postgres` | Filter and ordinal on a per-job counter that a release N-1 node (session 2) does not move, with no bridge trigger | `LogOrdered` |

Some mutant configs leave out a rule that would break first:

- `claim-no-lock-*` leave out `ClaimOnlyDue`. With every rule on, it breaks first, when a reschedule commits between the read and the UPDATE. Without it, TLC shows the double lease.
- `outcome-keeps-owner-postgres` and `relinquish-no-leased-check-and-outcome-keeps-owner-postgres` leave out `OwnerOnlyWhileLeased`, because a set `lease_owner` on a `Succeeded` row is the planted change itself.

A mutant that passes removes a second guard. `relinquish-no-leased-check-postgres` passes because `OwnerOnlyWhileLeased` holds. The two Oracle claim mutants pass because the claim selects every column but `output`, which is written only with a Success state, so SKIP LOCKED skips any row that changed after the snapshot, and the outer predicates and the C# checks never see a row that is not Scheduled and due. The two single Oracle sweep mutants pass because the outer `WHERE` and the outer `ORDER BY` each read `lease_expiry`, so either one alone makes SKIP LOCKED skip a row that a lease write changed. They are second guards. Keep them: they stop being second guards if the select list or the lock rule changes.

## Oracle SKIP LOCKED rule

The Oracle claim (`OracleJobStore.cs:581-592`) and the Oracle expiry sweep (1473-1484) pick their rows in a `ROWNUM` subquery and lock them in an outer `SELECT ... FOR UPDATE SKIP LOCKED`. The model uses this rule for that lock (`LockLevelCols` and `SnapshotCurrent` in the spec):

- A row that another session holds is skipped.
- A row is skipped when a column that the locking query level reads changed after the statement snapshot. The locking level is the outer select: its select list, its `WHERE`, and its `ORDER BY`.
- A row where only other columns changed is locked, and it returns its latest values. This includes a column that only the `ROWNUM` subquery reads.
- The statement does not restart.

The claim selects every column but `output`, and `output` is written only together with a Success state, so any change makes it skip the row. The sweep selects `job_id, attempt`, repeats `state = 2 AND lease_expiry <= :now` in its outer `WHERE`, and orders by `lease_expiry`. Every write that ends or renews a lease changes `lease_expiry`, so the outer predicates keep the sweep off a row that an outcome, a heartbeat, or another sweep committed after the snapshot. The outer `ORDER BY lease_expiry` does the same on its own. Without both, the sweep locks a row that an outcome wrote after the snapshot and writes it back to `Scheduled`, so a `Succeeded` job can run again. The mutant `expire-no-outer-where-and-order-oracle` shows this, and the probe case `noorder` measures the lock. The probe case `outerwhere` measures that a column read only in the outer `WHERE` also makes SKIP LOCKED skip the row.

Evidence: measured on Oracle XE 21.3 (`gvenzl/oracle-xe:21-slim`) and Oracle Free 23.26 (`gvenzl/oracle-free:23-slim`) on 2026-11-25. Inferred for 19c, the oldest version the adapter supports. Each probe container needs about 20 GiB of free disk, so check `df -h /` first. Measure it again with:

```sh
tests/formal/lease-fencing/oracle-skip-locked-probe.sh gvenzl/oracle-xe:21-slim
tests/formal/lease-fencing/oracle-skip-locked-probe.sh gvenzl/oracle-free:23-slim
```

The probe starts a private container, or reuses one that an earlier `--keep` run left. It removes a container it started. It runs the ExpireLeases statement shape on one lapsed row, and a second session commits a change to the row between the snapshot and the lock. Each case states what the rule predicts, lock or skip, and the probe prints the rows locked. A cold run takes about 1 minute on Free 23 and about 2 minutes on XE 21, which runs under amd64 emulation on an arm64 Mac. Exit codes:

- 0: Oracle never locked a changed row where the model skips it. A `NOTE` line marks a case where Oracle did less than the model allows: it skipped a row that the model locks, or it restarted the statement. The Oracle configs still hold then. If `noorder` is a `NOTE`, `expire-no-outer-where-and-order-oracle` no longer matches Oracle.
- 1: Oracle locked a changed row that the model skips. Do not trust an Oracle pass until `LockLevelCols` is fixed and `check.sh --all` matches again.
- 2: no valid measurement. The setup failed, the control row was not locked, the change missed the window, or the subquery did not sleep.

Output of cold runs on both images at the last run:

```text
oracle: Oracle Database 21c Express Edition  21.3.0.0.0
none       locked=1 model=lock elapsed= 4.32s change_at=+1.37s  ok
outcome    locked=0 model=skip elapsed= 4.05s change_at=+1.67s  ok
heartbeat  locked=0 model=skip elapsed= 4.05s change_at=+1.69s  ok
selected   locked=0 model=skip elapsed= 4.04s change_at=+1.75s  ok
outerwhere locked=0 model=skip elapsed= 4.34s change_at=+1.54s  ok
inner      locked=1 model=lock elapsed= 4.05s change_at=+1.63s  ok
unread     locked=1 model=lock elapsed= 4.08s change_at=+1.71s  ok
noorder    locked=1 model=lock elapsed= 4.37s change_at=+1.20s  ok
Every case matches the model's Oracle SKIP LOCKED rule on gvenzl/oracle-xe:21-slim.

oracle: Oracle AI Database 26ai Free 23.26.4.0.0
none       locked=1 model=lock elapsed= 4.02s change_at=+1.51s  ok
outcome    locked=0 model=skip elapsed= 4.01s change_at=+1.50s  ok
heartbeat  locked=0 model=skip elapsed= 4.00s change_at=+1.52s  ok
selected   locked=0 model=skip elapsed= 4.00s change_at=+1.50s  ok
outerwhere locked=0 model=skip elapsed= 4.01s change_at=+1.51s  ok
inner      locked=1 model=lock elapsed= 4.00s change_at=+1.50s  ok
unread     locked=1 model=lock elapsed= 4.01s change_at=+1.50s  ok
noorder    locked=1 model=lock elapsed= 4.01s change_at=+1.50s  ok
Every case matches the model's Oracle SKIP LOCKED rule on gvenzl/oracle-free:23-slim.
```

Run the probe again before you trust the rule in these cases:

- The adapter supports a new Oracle version. Run the probe on an image of that version.
- A change edits the outer select list, `WHERE`, or `ORDER BY` of the claim or the sweep. Update `LockLevelCols`, and add a probe case for the new shape if the rule does not already predict it.
- A change replaces `SKIP LOCKED` with a waiting lock (plain `FOR UPDATE`) in a statement that picks its rows in a subquery. The probe does not measure waiting locks. A published reproduction (dbi-services, "Oracle write consistency bug and multi-thread de-queuing") shows a waiting lock on 18c and 21c that overwrites rows that another session committed. A two-session run on 2026-11-24 reproduced it on XE 21 and not on Free 23. Write a two-session run for that statement.

## Not covered

The Core driver loop, Enqueue, Concurrency Limits and the queue locks of a limited queue, slot accounting, AwaitingParent and the fan-in latches, Cancel, Requeue, the transition writes of the child latches, Mint and the trigger schedule, the Commit Horizon, SQLite, and the in-memory store. The model has no child edges, so the child-edge gate of the Postgres outcome command never holds a write back. A write it holds back ends like a rollback, and the caller runs the batch again in a transaction, which the model reaches as a rollback and a new report. Each worker has one database session, so the calls of one worker never overlap, as they can on separate connections. Retry backoff is zero ticks, and a Reschedule moves the due time by one tick. Liveness is not checked.

When the code changed without a rerun of this check, use the pstack `/maintain-formal-verification-skill` skill to bring the model back in line.
