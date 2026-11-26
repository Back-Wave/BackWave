---------------------------- MODULE LeaseFencing ----------------------------
(***************************************************************************)
(* Lease fencing in the BackWave SQL adapters (Postgres, SQL Server,       *)
(* Oracle), at the grain of one SQL statement or one row lock.             *)
(*                                                                         *)
(* Each step is one atomic unit of the adapter code:                       *)
(*                                                                         *)
(*  - "Begin" takes the statement snapshot. It picks the candidate rows    *)
(*    with the predicate that the statement evaluates on that snapshot.    *)
(*  - "Lock" visits one candidate row. A row that another session holds is *)
(*    skipped (SKIP LOCKED, READPAST) or waited on (plain FOR UPDATE,      *)
(*    UPDLOCK, UPDATE). An unlocked row is re-checked on its latest        *)
(*    committed version with the predicate that the adapter re-checks      *)
(*    (see the isolation rules), then locked or dropped.                   *)
(*  - "Commit" writes every locked row and releases the locks. Rows stay   *)
(*    locked from Lock to Commit, so the later statements of a             *)
(*    transaction that write by id see the same row the lock saw, and the *)
(*    write can wait until the commit with no loss.                        *)
(*  - A single fenced UPDATE of one row is one step. It waits for the row  *)
(*    lock and evaluates its WHERE on the latest committed row.            *)
(*                                                                         *)
(* A multi-row statement locks its rows one at a time and commits once.    *)
(* The model visits the rows in any order and, for the single UPDATEs      *)
(* (heartbeat, outcome), one row per statement. That reaches every state   *)
(* the multi-row form reaches.                                             *)
(*                                                                         *)
(* Isolation rules (READ COMMITTED on all three):                          *)
(*  - Postgres: each statement has its own snapshot. FOR UPDATE on a row   *)
(*    that changed after the snapshot re-checks the whole WHERE of that    *)
(*    query level on the latest version (EvalPlanQual). An UPDATE joined   *)
(*    to a CTE re-checks only the join. Every CTE of one statement reads   *)
(*    that statement's snapshot. A command of several statements runs as   *)
(*    one implicit transaction, and each statement takes a new snapshot.   *)
(*    FOR NO KEY UPDATE conflicts with every row lock and row write in     *)
(*    the model.                                                           *)
(*  - SQL Server runs with READ_COMMITTED_SNAPSHOT ON                      *)
(*    (src/BackWave.SqlServer/SqlServerMigrator.cs:260). A plain SELECT    *)
(*    reads the row versions of its statement. An UPDLOCK read and the     *)
(*    target rows of an UPDATE read the latest committed row under a U     *)
(*    lock, so they have no snapshot to go stale. Begin picks the rows     *)
(*    that qualify when the read starts; a row that starts to qualify      *)
(*    later is left to the next read, which makes the same lock-time test. *)
(*  - Oracle: each statement has its own snapshot. FOR UPDATE SKIP LOCKED  *)
(*    skips a row that another session holds. It also skips a row when a   *)
(*    column that the locking query level reads (its select list, WHERE,   *)
(*    or ORDER BY) changed after the snapshot. When only other columns     *)
(*    changed, also columns that only a subquery reads, it locks the row   *)
(*    and returns its latest values. It does not restart. Measured on      *)
(*    Oracle XE 21.3 and Oracle Free 23.26 by oracle-skip-locked-probe.sh; *)
(*    inferred for 19c. The model compares values, so a write that leaves  *)
(*    those columns as they were counts as no change. That only lets the   *)
(*    model lock more rows than Oracle does. A plain FOR UPDATE            *)
(*    waits and then re-checks the predicates of its own query level on    *)
(*    the latest version. Every plain FOR UPDATE in the model has all its  *)
(*    predicates at that level.                                            *)
(*                                                                         *)
(* Sync commit: the last commit that changed this file. Line numbers       *)
(* cite the code at that commit.                                           *)
(***************************************************************************)
EXTENDS Integers, FiniteSets, TLC

CONSTANTS
    Adapter,
    Mutants,
    Jobs,
    SessionCount,
    MaxAttempts,
    LeaseTicks,
    MaxTime,
    MaxNowLag,
    ReportPaths

ASSUME Adapter \in {"Postgres", "SqlServer", "Oracle"}
ASSUME Mutants \subseteq {"OwnerOnlyFence", "ClaimNoDueRecheck", "ClaimNoOuterDue",
                          "RelinquishNoLeasedCheck", "OutcomeKeepsOwner", "ClaimNoLock",
                          "ExpireNoOuterWhere", "ExpireNoOuterOrder"}
ASSUME ReportPaths \subseteq {"Single", "Batch"} /\ ReportPaths # {}
ASSUME LeaseTicks >= 1

Scheduled == 0
Leased == 2
Succeeded == 3
DeadLettered == 5
Terminal == {Succeeded, DeadLettered}

NoOwner == "none"
NotTerminal == -1
NoExpiry == 0
NoSession == 0
Sessions == 1..SessionCount
Workers == {"w1", "w2"}
WorkerOf(s) == IF s % 2 = 1 THEN "w1" ELSE "w2"
Has(m) == m \in Mutants

Idle == [op |-> "Idle", at |-> 0, todo |-> {}, held |-> {}, job |-> "none", att |-> 0, kind |-> "none",
         lockLevelSnap |-> [j \in {} |-> NoExpiry]]

VARIABLES
    now,
    row,
    lockOf,
    sess,
    unreported,
    liveLeases,
    firstTerminal,
    brokenRules

vars == <<now, row, lockOf, sess, unreported, liveLeases, firstTerminal, brokenRules>>

-----------------------------------------------------------------------------

Max(a, b) == IF a > b THEN a ELSE b
CallTimes == Max(0, now - MaxNowLag)..now
Free(j, s) == lockOf[j] \in {NoSession, s}
Release(s) == [j \in Jobs |-> IF lockOf[j] = s THEN NoSession ELSE lockOf[j]]
Lapsed(r, at) == r.expiry # NoExpiry /\ r.expiry <= at
Unlapsed(r, at) == r.expiry # NoExpiry /\ r.expiry > at

EndLease(j) == IF row[j].state = Leased
               THEN liveLeases[j] \ {<<row[j].owner, row[j].attempt>>}
               ELSE liveLeases[j]
NoteTerminal(j, st) == IF firstTerminal[j] = NotTerminal /\ st \in Terminal THEN st ELSE firstTerminal[j]
Check(ok, rule) == IF ok THEN {} ELSE {rule}

LockRow(s, j, mode, recheckPasses) ==
    /\ j \in sess[s].todo
    /\ IF ~Free(j, s)
         THEN /\ mode = "skip"
              /\ sess' = [sess EXCEPT ![s].todo = @ \ {j}]
              /\ UNCHANGED lockOf
         ELSE IF recheckPasses
                THEN /\ lockOf' = [lockOf EXCEPT ![j] = s]
                     /\ sess' = [sess EXCEPT ![s].todo = @ \ {j}, ![s].held = @ \cup {j}]
                ELSE /\ sess' = [sess EXCEPT ![s].todo = @ \ {j}]
                     /\ UNCHANGED lockOf

\* The claim selects every column but output (JobColumns,
\* OracleJobStore.cs:4181-4184). output is written only with a Success state.
\* The sweep selects attempt, filters on state and lease_expiry in its outer
\* WHERE, and orders by lease_expiry (1475, 1476, 1483).
ExpireLockCols ==
    {"attempt"}
    \cup (IF Has("ExpireNoOuterWhere") THEN {} ELSE {"state", "expiry"})
    \cup (IF Has("ExpireNoOuterOrder") THEN {} ELSE {"expiry"})

LockLevelCols(op, r) ==
    CASE op = "Claim" -> r
      [] op = "Expire" -> [c \in ExpireLockCols |-> r[c]]

Begin(s, op, at, P) ==
    sess' = [sess EXCEPT ![s] = [Idle EXCEPT !.op = op, !.at = at, !.todo = P,
                                 !.lockLevelSnap = IF Adapter = "Oracle"
                                          THEN [j \in P |-> LockLevelCols(op, row[j])]
                                          ELSE @]]

SnapshotCurrent(s, j) == LockLevelCols(sess[s].op, row[j]) = sess[s].lockLevelSnap[j]

Finish(s) == /\ lockOf' = Release(s)
             /\ sess' = [sess EXCEPT ![s] = Idle]

-----------------------------------------------------------------------------
(* Postgres  PostgresJobStore.cs:625-651 (ClaimCandidates, ClaimUpdate)
     WITH candidates AS (SELECT ... WHERE state = 0 AND due_time <= @now
                         ... FOR UPDATE SKIP LOCKED)
     UPDATE ... SET state = 2, attempt = j.attempt + 1, ... FROM candidates
     ClaimUntracedAsync (from 482) runs it in one of two forms (498-503):
     an autocommit statement whose later CTEs also append and prune the
     Leased transitions (108-116, TryClaimInOneStatementAsync from 525), or
     a transaction with the transition insert as a second statement (609,
     ClaimInTransactionAsync from 545). The one-statement form rolls back
     whole on a transition ordinal collision, and the caller claims again
     in the transaction form: Rollback, then a new ClaimBegin.
     Begin = the statement snapshot; Lock = SKIP LOCKED with the
     EvalPlanQual re-check of state and due_time; Commit = the UPDATE and
     the commit.
   SQL Server  SqlServerJobStore.cs:564-583 (ClaimQueueAsync, from 492)
     INSERT INTO @batch SELECT ... WITH (UPDLOCK, READPAST, ROWLOCK)
       WHERE state = 0 AND due_time <= @now;
     UPDATE j ... FROM @batch c INNER LOOP JOIN backwave.jobs j
     The UPDLOCK read tests each row on its latest committed version when
     it reaches it; Lock makes that test.
   Oracle  OracleJobStore.cs:581-592, 610-621, 629-634 (ClaimUntracedAsync, from 498)
     SELECT ... WHERE state = 0 AND due_time <= :now AND job_id IN (
       SELECT ... WHERE state = 0 AND due_time <= :now ... ROWNUM <= :take)
     FOR UPDATE SKIP LOCKED;  then the C# row checks halt on a locked row
     that is not Scheduled or not due;  then UPDATE ... WHERE job_id IN (...)
     Begin = the subquery on the snapshot; Lock = SKIP LOCKED, which skips a
     row when a selected column changed after the snapshot, then the outer
     predicates and the C# checks on the row it locked. *)

ClaimPick(at) == {j \in Jobs : row[j].state = Scheduled /\ row[j].due <= at}

ClaimMode == IF Has("ClaimNoLock") THEN "wait" ELSE "skip"

ClaimRecheck(s, j, at) ==
    CASE Has("ClaimNoLock") -> TRUE
      [] ~Has("ClaimNoLock") /\ Adapter = "Oracle" ->
            /\ SnapshotCurrent(s, j)
            /\ row[j].state = Scheduled
            /\ (row[j].due <= at \/ Has("ClaimNoDueRecheck") \/ Has("ClaimNoOuterDue"))
      [] OTHER -> row[j].state = Scheduled /\ row[j].due <= at

\* The Oracle C# checks on the row the locking read returned
\* (OracleJobStore.cs:610-621).
ClaimHalts(j, at) ==
    /\ Adapter = "Oracle" /\ ~Has("ClaimNoLock")
    /\ \/ row[j].state # Scheduled
       \/ ~Has("ClaimNoDueRecheck") /\ row[j].due > at

ClaimBegin(s) ==
    /\ sess[s].op = "Idle"
    /\ \E at \in CallTimes :
         /\ ClaimPick(at) # {}
         /\ Begin(s, "Claim", at, ClaimPick(at))
    /\ UNCHANGED <<now, row, lockOf, unreported, liveLeases, firstTerminal, brokenRules>>

ClaimLock(s) ==
    /\ sess[s].op = "Claim"
    /\ \E j \in sess[s].todo :
         IF Free(j, s) /\ ClaimRecheck(s, j, sess[s].at) /\ ClaimHalts(j, sess[s].at)
         THEN /\ brokenRules' = brokenRules \cup {"NoHalt"}
              /\ Finish(s)
         ELSE /\ LockRow(s, j, ClaimMode, ClaimRecheck(s, j, sess[s].at))
              /\ UNCHANGED brokenRules
    /\ UNCHANGED <<now, row, unreported, liveLeases, firstTerminal>>

ClaimCommit(s) ==
    /\ sess[s].op = "Claim"
    /\ sess[s].todo = {}
    /\ LET me == sess[s]
           w == WorkerOf(s)
           H == me.held
       IN /\ row' = [j \in Jobs |->
                       IF j \in H
                       THEN [row[j] EXCEPT !.state = Leased, !.attempt = row[j].attempt + 1,
                                           !.owner = w, !.expiry = me.at + LeaseTicks]
                       ELSE row[j]]
          /\ unreported' = [unreported EXCEPT ![w] = @ \cup {<<j, row[j].attempt + 1>> : j \in H}]
          /\ liveLeases' = [j \in Jobs |->
                        IF j \in H THEN liveLeases[j] \cup {<<w, row[j].attempt + 1>>} ELSE liveLeases[j]]
          /\ brokenRules' = brokenRules \cup Check(\A j \in H : row[j].due <= me.at, "ClaimOnlyDue")
    /\ Finish(s)
    /\ UNCHANGED <<now, firstTerminal>>

-----------------------------------------------------------------------------
(* Single path, all adapters: one fenced UPDATE of one row.
     Postgres  PostgresJobStore.cs:875-878 (ReportOutcomeUntracedAsync, from 816)
     SQL Server  SqlServerJobStore.cs:842-845 (ReportOutcomeUntracedAsync, from 782)
     Oracle  OracleJobStore.cs:907-909 (ReportOutcomeUntracedAsync, from 848)
     WHERE job_id = @id AND state = 2 AND lease_owner = @worker
       AND attempt = @attempt AND lease_expiry > @now
   Batch path (ReportOutcomesAsync):
     Postgres  PostgresJobStore.cs:1095-1103, 1115-1119, 1129-1137, 1144-1160
       (OutcomeCommandSql; ReportOutcomesUntracedAsync from 939 runs it as
       one implicit transaction, 1054, or inside a transaction, 1162):
       SELECT ... FROM jobs j, unnest(...) d WHERE <fence> ORDER BY j.job_id
         FOR NO KEY UPDATE OF j;
       WITH d AS (unnest(...)), gate AS (...),
         written AS (UPDATE jobs j ... FROM d, gate
                     WHERE NOT gate.gated AND <fence>), <transitions> ...
       Lock = the waiting lock with the EvalPlanQual re-check of the fence;
       a row that fails it stays unlocked and the command goes on. The
       model leaves such a row unlocked, which only allows more orders.
       Commit = the UPDATE on a snapshot taken after the locks, its fence
       on the latest row, and the commit. The transaction form commits
       after the output, tag, and latch statements, which the model does
       not have. A write the gate holds back is a Rollback; the caller
       then reports the batch again in the transaction form.
     SQL Server  SqlServerJobStore.cs:995-1019 (from 908):
       INSERT INTO @batch ... INNER LOOP JOIN jobs j WITH (UPDLOCK, ROWLOCK)
         WHERE <fence>;   UPDATE j ... FROM @batch d INNER LOOP JOIN jobs j
       Lock = the U-locked fence read; Commit = the unfenced UPDATE.
     Oracle  OracleJobStore.cs:1041-1045, 1064-1071, 1090-1115 (from 970):
       SELECT ... WHERE job_id IN (...) AND state = 2 AND lease_expiry > :now
       FOR UPDATE;  the C# verdict on owner and attempt;  MERGE ... WHEN
       MATCHED ... WHERE <fence>.  Lock = the FOR UPDATE read; Commit = the
       MERGE with its fence, on the locked row.
   A retry goes back to Scheduled with due = @now (zero backoff), or to
   DeadLettered at the ceiling (NodeDriver.cs:153-155 decides from the run's
   attempt). *)

Kinds == {"Succeeded", "Retry"}

OutcomeState(kind, a) ==
    IF kind = "Succeeded" THEN Succeeded
    ELSE IF a >= MaxAttempts THEN DeadLettered ELSE Scheduled

Fence(r, w, a, at) ==
    /\ r.state = Leased
    /\ r.owner = w
    /\ (r.attempt = a \/ Has("OwnerOnlyFence"))
    /\ Unlapsed(r, at)

ContractFence(r, w, a, at) ==
    r.state = Leased /\ r.owner = w /\ r.attempt = a /\ Unlapsed(r, at)

ApplyOutcome(j, w, a, kind, at) ==
    LET st == OutcomeState(kind, a)
        keep == Has("OutcomeKeepsOwner") /\ st = Succeeded
    IN /\ row' = [row EXCEPT ![j] = [@ EXCEPT !.state = st,
                                             !.owner = IF keep THEN @ ELSE NoOwner,
                                             !.expiry = NoExpiry,
                                             !.due = IF st = Scheduled THEN at ELSE @]]
       /\ liveLeases' = [liveLeases EXCEPT ![j] = EndLease(j)]
       /\ firstTerminal' = [firstTerminal EXCEPT ![j] = NoteTerminal(j, st)]
       /\ brokenRules' = brokenRules \cup Check(ContractFence(row[j], w, a, at), "OutcomeFenced")

DropRun(w, j, a) == unreported' = [unreported EXCEPT ![w] = @ \ {<<j, a>>}]

ReportUpdate(s) ==
    /\ sess[s].op = "Idle"
    /\ "Single" \in ReportPaths
    /\ LET w == WorkerOf(s) IN
       \E r \in unreported[w], kind \in Kinds, at \in CallTimes :
         LET j == r[1]
             a == r[2]
         IN /\ lockOf[j] = NoSession
            /\ DropRun(w, j, a)
            /\ IF Fence(row[j], w, a, at)
                 THEN ApplyOutcome(j, w, a, kind, at)
                 ELSE UNCHANGED <<row, liveLeases, firstTerminal, brokenRules>>
    /\ UNCHANGED <<now, lockOf, sess>>

ReportLock(s) ==
    /\ sess[s].op = "Idle"
    /\ "Batch" \in ReportPaths
    /\ LET w == WorkerOf(s) IN
       \E r \in unreported[w], kind \in Kinds, at \in CallTimes :
         LET j == r[1]
             a == r[2]
             locks == IF Adapter = "Oracle"
                      THEN row[j].state = Leased /\ Unlapsed(row[j], at)
                      ELSE Fence(row[j], w, a, at)
         IN /\ lockOf[j] = NoSession
            /\ IF locks \/ Adapter = "Postgres"
                 THEN /\ lockOf' = IF locks THEN [lockOf EXCEPT ![j] = s] ELSE lockOf
                      /\ sess' = [sess EXCEPT ![s] = [Idle EXCEPT !.op = "Report", !.at = at,
                                     !.held = IF locks THEN {j} ELSE {}, !.job = j, !.att = a,
                                     !.kind = kind]]
                      /\ UNCHANGED unreported
                 ELSE /\ DropRun(w, j, a)
                      /\ UNCHANGED <<lockOf, sess>>
    /\ UNCHANGED <<now, row, liveLeases, firstTerminal, brokenRules>>

ReportCommit(s) ==
    /\ sess[s].op = "Report"
    /\ Free(sess[s].job, s)
    /\ LET me == sess[s]
           w == WorkerOf(s)
           j == me.job
           writes == IF Adapter = "SqlServer"
                     THEN TRUE
                     ELSE Fence(row[j], w, me.att, me.at)
       IN /\ DropRun(w, j, me.att)
          /\ IF writes
               THEN ApplyOutcome(j, w, me.att, me.kind, me.at)
               ELSE UNCHANGED <<row, liveLeases, firstTerminal, brokenRules>>
    /\ Finish(s)
    /\ UNCHANGED now

-----------------------------------------------------------------------------
(* Postgres  PostgresJobStore.cs:1374-1376 (HeartbeatAsync, from 1365)
   SQL Server  SqlServerJobStore.cs:1266-1272 (HeartbeatAsync, from 1250)
     One UPDATE: SET lease_expiry = @expiry
       WHERE state = 2 AND lease_owner = @worker AND lease_expiry > @now
   Oracle  OracleJobStore.cs:1400-1403, 1419 (HeartbeatAsync, from 1375)
     SELECT ... WHERE job_id IN (...) AND state = 2 AND lease_owner = :worker
       AND lease_expiry > :now FOR UPDATE;  UPDATE ... SET lease_expiry *)

HeldJobs(w) == {r[1] : r \in unreported[w]}
Renews(r, w, at) == r.state = Leased /\ r.owner = w /\ Unlapsed(r, at)

HeartbeatUpdate(s) ==
    /\ sess[s].op = "Idle"
    /\ Adapter \in {"Postgres", "SqlServer"}
    /\ \E j \in HeldJobs(WorkerOf(s)), at \in CallTimes :
         /\ lockOf[j] = NoSession
         /\ Renews(row[j], WorkerOf(s), at)
         /\ row' = [row EXCEPT ![j].expiry = at + LeaseTicks]
    /\ UNCHANGED <<now, lockOf, sess, unreported, liveLeases, firstTerminal, brokenRules>>

HeartbeatLock(s) ==
    /\ sess[s].op = "Idle"
    /\ Adapter = "Oracle"
    /\ \E j \in HeldJobs(WorkerOf(s)), at \in CallTimes :
         /\ lockOf[j] = NoSession
         /\ Renews(row[j], WorkerOf(s), at)
         /\ lockOf' = [lockOf EXCEPT ![j] = s]
         /\ sess' = [sess EXCEPT ![s] = [Idle EXCEPT !.op = "Heartbeat", !.at = at,
                                            !.held = {j}, !.job = j]]
    /\ UNCHANGED <<now, row, unreported, liveLeases, firstTerminal, brokenRules>>

HeartbeatCommit(s) ==
    /\ sess[s].op = "Heartbeat"
    /\ row' = [row EXCEPT ![sess[s].job].expiry = sess[s].at + LeaseTicks]
    /\ Finish(s)
    /\ UNCHANGED <<now, unreported, liveLeases, firstTerminal, brokenRules>>

-----------------------------------------------------------------------------
(* Below the ceiling the job goes back to Scheduled at @now (zero
   backoff); at the ceiling it dead-letters.
   The attempt the read returns decides (RetryPolicy.cs:166-167).

   Postgres  PostgresJobStore.cs:1437-1442, 1501-1503, 1516-1518 (from 1419)
     SELECT job_id, attempt ... WHERE state = 2 AND lease_expiry <= @now
       ... FOR UPDATE SKIP LOCKED;  UPDATE ... by id (retry, dead-letter)
   SQL Server  SqlServerJobStore.cs:1335-1338, 1401, 1419 (from 1315)
     SELECT TOP (@max) ... WITH (UPDLOCK, READPAST, ROWLOCK)
       WHERE state = 2 AND lease_expiry <= @now;  UPDATE j ... by id
   Oracle  OracleJobStore.cs:1473-1484, 1547-1556, 1565-1577 (from 1452)
     SELECT job_id, attempt FROM jobs
       WHERE state = 2 AND lease_expiry <= :now AND job_id IN (
       SELECT ... WHERE state = 2 AND lease_expiry <= :now ... ROWNUM <= :max)
     ORDER BY lease_expiry FOR UPDATE SKIP LOCKED;  MERGE ... by id, no fence *)

ExpirePick(at) == {j \in Jobs : row[j].state = Leased /\ Lapsed(row[j], at)}

ExpireRecheck(s, j, at) ==
    IF Adapter = "Oracle"
    THEN SnapshotCurrent(s, j)
    ELSE row[j].state = Leased /\ Lapsed(row[j], at)

ExpireBegin(s) ==
    /\ sess[s].op = "Idle"
    /\ \E at \in CallTimes :
         /\ ExpirePick(at) # {}
         /\ Begin(s, "Expire", at, ExpirePick(at))
    /\ UNCHANGED <<now, row, lockOf, unreported, liveLeases, firstTerminal, brokenRules>>

ExpireLock(s) ==
    /\ sess[s].op = "Expire"
    /\ \E j \in sess[s].todo : LockRow(s, j, "skip", ExpireRecheck(s, j, sess[s].at))
    /\ UNCHANGED <<now, row, unreported, liveLeases, firstTerminal, brokenRules>>

Disposed(j, at) ==
    LET st == IF row[j].attempt >= MaxAttempts THEN DeadLettered ELSE Scheduled
    IN [row[j] EXCEPT !.state = st, !.owner = NoOwner, !.expiry = NoExpiry,
                      !.due = IF st = Scheduled THEN at ELSE row[j].due]

ExpireCommit(s) ==
    /\ sess[s].op = "Expire"
    /\ sess[s].todo = {}
    /\ LET me == sess[s]
           H == me.held
       IN /\ row' = [j \in Jobs |-> IF j \in H THEN Disposed(j, me.at) ELSE row[j]]
          /\ liveLeases' = [j \in Jobs |-> IF j \in H THEN EndLease(j) ELSE liveLeases[j]]
          /\ firstTerminal' = [j \in Jobs |->
                            IF j \in H THEN NoteTerminal(j, Disposed(j, me.at).state) ELSE firstTerminal[j]]
          /\ brokenRules' = brokenRules \cup
                Check(\A j \in H : row[j].state = Leased /\ Lapsed(row[j], me.at), "ExpireOnce")
    /\ Finish(s)
    /\ UNCHANGED <<now, unreported>>

-----------------------------------------------------------------------------
(* Postgres  PostgresJobStore.cs:1590-1594, 1656-1658, 1679-1681 (from 1577)
     SELECT ... WHERE state = 2 AND lease_owner = @owner ... FOR UPDATE
   SQL Server  SqlServerJobStore.cs:1501-1504, 1562-1563, 1578 (from 1486)
     SELECT ... WITH (UPDLOCK, ROWLOCK) WHERE state = 2 AND lease_owner = @owner
   Oracle  OracleJobStore.cs:1650-1654, 1715-1722, 1737-1743 (from 1637)
     SELECT ... WHERE state = 2 AND lease_owner = :owner ... FOR UPDATE
   Each then writes the locked rows by id. The lock waits. *)

RelinquishHolds(j, w) ==
    IF Has("RelinquishNoLeasedCheck")
    THEN row[j].owner = w
    ELSE row[j].state = Leased /\ row[j].owner = w

RelinquishPick(w) == {j \in Jobs : RelinquishHolds(j, w)}

RelinquishBegin(s) ==
    /\ sess[s].op = "Idle"
    /\ \E at \in CallTimes :
         /\ RelinquishPick(WorkerOf(s)) # {}
         /\ sess' = [sess EXCEPT ![s] = [Idle EXCEPT !.op = "Relinquish", !.at = at,
                                            !.todo = RelinquishPick(WorkerOf(s))]]
    /\ UNCHANGED <<now, row, lockOf, unreported, liveLeases, firstTerminal, brokenRules>>

RelinquishLock(s) ==
    /\ sess[s].op = "Relinquish"
    /\ \E j \in sess[s].todo : LockRow(s, j, "wait", RelinquishHolds(j, WorkerOf(s)))
    /\ UNCHANGED <<now, row, unreported, liveLeases, firstTerminal, brokenRules>>

RelinquishCommit(s) ==
    /\ sess[s].op = "Relinquish"
    /\ sess[s].todo = {}
    /\ LET me == sess[s]
           w == WorkerOf(s)
           H == me.held
       IN /\ row' = [j \in Jobs |-> IF j \in H THEN Disposed(j, me.at) ELSE row[j]]
          /\ liveLeases' = [j \in Jobs |-> IF j \in H THEN EndLease(j) ELSE liveLeases[j]]
          /\ firstTerminal' = [j \in Jobs |->
                            IF j \in H THEN NoteTerminal(j, Disposed(j, me.at).state) ELSE firstTerminal[j]]
          /\ brokenRules' = brokenRules \cup
                Check(\A j \in H : row[j].state = Leased /\ row[j].owner = w, "RelinquishOnlyHeld")
    /\ Finish(s)
    /\ UNCHANGED <<now, unreported>>

-----------------------------------------------------------------------------
(* The lock and the guard are one step; the commit can come later, because
   a caller transaction holds the row lock until it commits.

   Postgres  PostgresJobStore.cs:1867-1874 (RescheduleCoreAsync, from 1852)
     WITH old AS (SELECT ... WHERE job_id = @id AND state IN (0, 1) FOR UPDATE)
     UPDATE ... SET due_time = @due FROM old
   SQL Server  SqlServerJobStore.cs:1767-1769 (RescheduleCoreAsync, from 1757)
     UPDATE ... SET due_time = @due WHERE job_id = @id AND state IN (0, 1)
   Oracle  OracleJobStore.cs:1936-1941 (RescheduleCoreAsync, from 1920)
     SELECT due_time ... WHERE job_id = :id AND state IN (0, 1) FOR UPDATE;
     UPDATE ... SET due_time = :due WHERE job_id = :id *)

RescheduleLock(s) ==
    /\ sess[s].op = "Idle"
    /\ \E j \in Jobs, at \in CallTimes :
         /\ lockOf[j] = NoSession
         /\ row[j].state = Scheduled
         /\ lockOf' = [lockOf EXCEPT ![j] = s]
         /\ sess' = [sess EXCEPT ![s] = [Idle EXCEPT !.op = "Reschedule", !.at = at,
                                            !.held = {j}, !.job = j]]
    /\ UNCHANGED <<now, row, unreported, liveLeases, firstTerminal, brokenRules>>

RescheduleCommit(s) ==
    /\ sess[s].op = "Reschedule"
    /\ row' = [row EXCEPT ![sess[s].job].due = sess[s].at + 1]
    /\ Finish(s)
    /\ UNCHANGED <<now, unreported, liveLeases, firstTerminal, brokenRules>>

-----------------------------------------------------------------------------

Rollback(s) ==
    /\ sess[s].op # "Idle"
    /\ Finish(s)
    /\ UNCHANGED <<now, row, unreported, liveLeases, firstTerminal, brokenRules>>

Tick ==
    /\ now < MaxTime
    /\ now' = now + 1
    /\ UNCHANGED <<row, lockOf, sess, unreported, liveLeases, firstTerminal, brokenRules>>

\* Without this step, TLC reports a state where nothing is left to do as a
\* deadlock.
Done ==
    /\ now = MaxTime
    /\ \A s \in Sessions : sess[s].op = "Idle"
    /\ UNCHANGED vars

Init ==
    /\ now = 0
    /\ row = [j \in Jobs |-> [state |-> Scheduled, attempt |-> 0, owner |-> NoOwner,
                              expiry |-> NoExpiry, due |-> 0]]
    /\ lockOf = [j \in Jobs |-> NoSession]
    /\ sess = [s \in Sessions |-> Idle]
    /\ unreported = [w \in Workers |-> {}]
    /\ liveLeases = [j \in Jobs |-> {}]
    /\ firstTerminal = [j \in Jobs |-> NotTerminal]
    /\ brokenRules = {}

Next ==
    \/ Tick
    \/ Done
    \/ \E s \in Sessions :
         \/ ClaimBegin(s) \/ ClaimLock(s) \/ ClaimCommit(s)
         \/ ReportUpdate(s) \/ ReportLock(s) \/ ReportCommit(s)
         \/ HeartbeatUpdate(s) \/ HeartbeatLock(s) \/ HeartbeatCommit(s)
         \/ ExpireBegin(s) \/ ExpireLock(s) \/ ExpireCommit(s)
         \/ RelinquishBegin(s) \/ RelinquishLock(s) \/ RelinquishCommit(s)
         \/ RescheduleLock(s) \/ RescheduleCommit(s)
         \/ Rollback(s)

Spec == Init /\ [][Next]_vars

JobSymmetry == Permutations(Jobs)

-----------------------------------------------------------------------------

TypeOK ==
    /\ now \in 0..MaxTime
    /\ \A j \in Jobs :
         /\ row[j].state \in {Scheduled, Leased, Succeeded, DeadLettered}
         /\ row[j].owner \in Workers \cup {NoOwner}
         /\ lockOf[j] \in Sessions \cup {NoSession}
    /\ \A s \in Sessions :
         sess[s].op \in {"Idle", "Claim", "Report", "Heartbeat", "Expire", "Relinquish", "Reschedule"}

SingleLease == \A j \in Jobs : Cardinality(liveLeases[j]) <= 1

OutcomeFenced == "OutcomeFenced" \notin brokenRules

ClaimOnlyDue == "ClaimOnlyDue" \notin brokenRules

ExpireOnce == "ExpireOnce" \notin brokenRules

RelinquishOnlyHeld == "RelinquishOnlyHeld" \notin brokenRules

TerminalStays == \A j \in Jobs : firstTerminal[j] # NotTerminal => row[j].state = firstTerminal[j]

NoHalt == "NoHalt" \notin brokenRules

OwnerOnlyWhileLeased ==
    \A j \in Jobs : (row[j].owner # NoOwner \/ row[j].expiry # NoExpiry) <=> row[j].state = Leased

=============================================================================
