---
name: lease-fencing-model
description: TLA+ model of lease fencing in the Postgres, SQL Server, and Oracle job stores (Claim, Heartbeat, ExpireLeases, RelinquishLeases, ReportOutcome(s), Reschedule), checked with TLC at the level of SQL statements and row locks. Use when a change touches the SQL, locking, or transaction shape of those methods, to check a lease race, or to read a model result.
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

Bounds: 2 jobs (symmetric), 2 sessions for workers `w1` and `w2`, `MaxAttempts` 2, a lease of 2 ticks, a clock from 0 to 2, a call `@now` that can lag the clock by 1 tick, and both outcome paths. A pass covers these bounds and the abstractions in the spec header, not the whole system.

## Does the change touch the model

The model describes the code at the sync commit, the last commit that changed `LeaseFencing.tla`. Find it with `git log -1 --format=%h -- tests/formal/lease-fencing/LeaseFencing.tla`. Compare the change with this coverage record. Line numbers are at the sync commit. Each method is the untraced body unless the table names another.

| Operation | Postgres `PostgresJobStore.cs` | SQL Server `SqlServerJobStore.cs` | Oracle `OracleJobStore.cs` | Model actions |
|---|---|---|---|---|
| Claim | `ClaimUntracedAsync` 430, SQL 508-520 | `ClaimQueueAsync` 492, SQL 564-583 | `ClaimUntracedAsync` 498, SQL 581-592, due check 612-623, write 631-636 | `ClaimBegin`, `ClaimLock`, `ClaimCommit` |
| ReportOutcome | `ReportOutcomeUntracedAsync` 683, SQL 742-745 | `ReportOutcomeUntracedAsync` 782, SQL 842-845 | `ReportOutcomeUntracedAsync` 850, SQL 909-911 | `ReportUpdate` |
| ReportOutcomes | `ReportOutcomesUntracedAsync` 806, SQL 879-892 | `ReportOutcomesUntracedAsync` 908, SQL 995-1019 | `ReportOutcomesUntracedAsync` 972, SQL 1043-1047, 1066-1073, 1092-1117 | `ReportUpdate` (Postgres), `ReportLock`, `ReportCommit` |
| Heartbeat | `HeartbeatAsync` 1130, SQL 1139-1141 | `HeartbeatAsync` 1250, SQL 1266-1272 | `HeartbeatAsync` 1377, SQL 1402-1405, 1421 | `HeartbeatUpdate`, `HeartbeatLock`, `HeartbeatCommit` |
| ExpireLeases | `ExpireLeasesUntracedAsync` 1184, SQL 1202-1207, 1266-1268, 1281-1283 | `ExpireLeasesUntracedAsync` 1315, SQL 1335-1338, 1401, 1419 | `ExpireLeasesUntracedAsync` 1454, SQL 1475-1486, 1549-1558, 1567-1579 | `ExpireBegin`, `ExpireLock`, `ExpireCommit` |
| RelinquishLeases | `RelinquishLeasesUntracedAsync` 1342, SQL 1355-1359, 1421-1423, 1444-1446 | `RelinquishLeasesUntracedAsync` 1486, SQL 1501-1504, 1562-1563, 1578 | `RelinquishLeasesUntracedAsync` 1639, SQL 1652-1656, 1717-1724, 1739-1745 | `RelinquishBegin`, `RelinquishLock`, `RelinquishCommit` |
| Reschedule | `RescheduleCoreAsync` 1617, SQL 1632-1639 | `RescheduleCoreAsync` 1757, SQL 1767-1769 | `RescheduleCoreAsync` 1922, SQL 1938-1943 | `RescheduleLock`, `RescheduleCommit` |

The model also depends on these facts. A change to any of them touches the model too:

- `src/BackWave.SqlServer/SqlServerMigrator.cs:260` turns on READ_COMMITTED_SNAPSHOT.
- `src/BackWave/Core/RetryPolicy.cs:56-57` (`RetryPolicy.NextAttemptAt`) and `166-167` (`RetryDisposition.NextAttemptAt`) dead-letter when the failed attempt reaches `MaxAttempts`.
- `src/BackWave/Driver/NodeDriver.cs:153-155` picks the outcome from the run's attempt, through a per-job `[Retry]` override or the Worker Group policy.

To list the commits since the sync commit that touched covered code:

```sh
git log --oneline "$(git log -1 --format=%H -- tests/formal/lease-fencing/LeaseFencing.tla)"..HEAD -- src/BackWave.Postgres/PostgresJobStore.cs src/BackWave.SqlServer/SqlServerJobStore.cs src/BackWave.Oracle/OracleJobStore.cs src/BackWave.SqlServer/SqlServerMigrator.cs src/BackWave/Core/RetryPolicy.cs src/BackWave/Driver/NodeDriver.cs
```

A commit touches the model when it changes a cited statement, its `WHERE`, its lock hints, its transaction, or the order of statements inside one transaction. A change elsewhere in these files needs only new line numbers.

## Run the check

```sh
tests/formal/lease-fencing/check.sh
```

It runs TLC (pinned by `tests/formal/tlc.sh`) on every config in `tests/formal/lease-fencing/` and prints one row per config: the expected result, the result, the distinct states, and the seconds. Each config's first line, `\* expect: pass` or `\* expect: <Rule>`, says what it must end with. The script exits 0 only when every row matches. It takes about 2 minutes. Add `--mutants` for the planted breaks in `mutants/` (about 3.5 minutes) or `--all` for both (about 5.5 minutes on 2026-11-25; times vary about 2x with machine load). Run `--all` after you change the model.

The baseline at the sync commit: every row matches, and `postgres.cfg`, `sqlserver.cfg`, and `oracle.cfg` pass.

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
3. A new guard gets a mutant. Add its name to the `Mutants` `ASSUME`, gate the guard on `Has("<Name>")`, and add a config in `mutants/` that expects the rule the guard protects. When a change removes a guard, find the mutant config that planted the same removal: the real model now ends as that config does.
4. Run `check.sh --all`. Every row must match.
5. Update every file and line citation: in the spec comments, in the probe, and in this skill, including the coverage record. Then move the sync commit, here and in the spec header, to the commit you checked. Move it only after you have confirmed that the model describes the code at that commit.

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

Some mutant configs leave out a rule that would break first:

- `claim-no-lock-*` leave out `ClaimOnlyDue`. With every rule on, it breaks first, when a reschedule commits between the read and the UPDATE. Without it, TLC shows the double lease.
- `outcome-keeps-owner-postgres` and `relinquish-no-leased-check-and-outcome-keeps-owner-postgres` leave out `OwnerOnlyWhileLeased`, because a set `lease_owner` on a `Succeeded` row is the planted change itself.

A mutant that passes removes a second guard. `relinquish-no-leased-check-postgres` passes because `OwnerOnlyWhileLeased` holds. The two Oracle claim mutants pass because the claim selects every column, so SKIP LOCKED skips any row that changed after the snapshot, and the outer predicates and the C# checks never see a row that is not Scheduled and due. The two single Oracle sweep mutants pass because the outer `WHERE` and the outer `ORDER BY` each read `lease_expiry`, so either one alone makes SKIP LOCKED skip a row that a lease write changed. They are second guards. Keep them: they stop being second guards if the select list or the lock rule changes.

## Oracle SKIP LOCKED rule

The Oracle claim (`OracleJobStore.cs:581-592`) and the Oracle expiry sweep (1475-1486) pick their rows in a `ROWNUM` subquery and lock them in an outer `SELECT ... FOR UPDATE SKIP LOCKED`. The model uses this rule for that lock (`LockLevelCols` and `SnapshotCurrent` in the spec):

- A row that another session holds is skipped.
- A row is skipped when a column that the locking query level reads changed after the statement snapshot. The locking level is the outer select: its select list, its `WHERE`, and its `ORDER BY`.
- A row where only other columns changed is locked, and it returns its latest values. This includes a column that only the `ROWNUM` subquery reads.
- The statement does not restart.

The claim selects every column, so any change makes it skip the row. The sweep selects `job_id, attempt`, repeats `state = 2 AND lease_expiry <= :now` in its outer `WHERE`, and orders by `lease_expiry`. Every write that ends or renews a lease changes `lease_expiry`, so the outer predicates keep the sweep off a row that an outcome, a heartbeat, or another sweep committed after the snapshot. The outer `ORDER BY lease_expiry` does the same on its own. Without both, the sweep locks a row that an outcome wrote after the snapshot and writes it back to `Scheduled`, so a `Succeeded` job can run again. The mutant `expire-no-outer-where-and-order-oracle` shows this, and the probe case `noorder` measures the lock. The probe case `outerwhere` measures that a column read only in the outer `WHERE` also makes SKIP LOCKED skip the row.

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

The Core driver loop, Concurrency Limits and the queue locks of a limited queue, slot accounting, AwaitingParent and the fan-in latches, Cancel, Requeue, the transition log, the Commit Horizon, SQLite, and the in-memory store. Each worker has one database session, so the calls of one worker never overlap, as they can on separate connections. Retry backoff is zero ticks, and a Reschedule moves the due time by one tick. Liveness is not checked.

When the code changed without a rerun of this check, use the pstack `/maintain-formal-verification-skill` skill to bring the model back in line.
