---------------------------- MODULE CommitHorizon ----------------------------
(***************************************************************************)
(* Paths below are relative to the repo root. PG = src/BackWave.Postgres,  *)
(* MS = src/BackWave.SqlServer, ORA = src/BackWave.Oracle.                 *)
(***************************************************************************)
EXTENDS Integers, FiniteSets, TLC

CONSTANTS
    Writers,        \* writer sessions; each runs transactions one after another
    Observers,      \* Transition Observers, one claim/report worker each
    Reporters,      \* stand-ins for other Observers' reports: each runs the horizon
                    \* step whenever it likes and has no cursor of its own
    OddOnly,        \* Observers whose subscription matches only odd Positions
    MaxPos,         \* the sequence hands out Positions 1..MaxPos, then stops
    MaxRowsPerTxn,  \* Transition Log rows one transaction may insert
    MaxRows,        \* the claim's maxRows (LIMIT @take)
    ReporterRuns,   \* horizon steps each stand-in report runs in one behavior
    MaxRetries,     \* total Retry outcomes in one run; a Retry holds the cursor
    Cache,          \* sequence values a session reserves at once
    HorizonOrder,   \* "postgres" | "lockFirst" (SQL Server, Oracle) | mutant "pAfterProbes"
    ProbeMode,
    UseHorizon,
    RegisterFirst   \* FALSE in a mutant: a row can draw its Position before its statement registers

ASSUME /\ MaxPos \in Nat \ {0} /\ MaxRowsPerTxn \in Nat \ {0} /\ MaxRows \in Nat \ {0}
       /\ MaxRetries \in Nat /\ ReporterRuns \in Nat /\ Cache \in Nat \ {0} /\ OddOnly \subseteq Observers /\ Observers \cap Reporters = {}
       /\ HorizonOrder \in {"postgres", "lockFirst", "pAfterProbes"}
       /\ ProbeMode \in {"both", "previousOnly", "currentOnly"}
       /\ UseHorizon \in BOOLEAN
       /\ RegisterFirst \in BOOLEAN

None == -1
Pos == 1..MaxPos
Actors == Observers \cup Reporters
Epochs == {0, 1}
NoState == [epoch |-> None, candidate |-> None, horizon |-> None]

VARIABLES
    \* job_transitions rows and the observer_log_position sequence
    rows,
    nextSeq,
    cache,
    wstep,
    wlocks,     \* session -> epochs its open transaction holds a shared lock on
    wrows,
    \* observer_log_horizon: the committed row and who holds its row lock
    hEpoch, hCand, hPos,
    hLock,      \* {} or {o}: the report transaction holding the row lock (SKIP LOCKED)
    epochsSeen, \* ghost: every epoch the committed row has held so far
    \* observers and observer_deliveries
    cursor,     \* o -> cursor_pos
    resolved,   \* o -> Positions with resolution <> 0 (Delivered or DeadLettered)
    backoff,    \* o -> Positions with resolution 0 still in their Retry backoff window
    delivered,  \* o -> ghost: every Position ever reported Delivered or DeadLettered
    retries,
    runs,
    ob          \* actor -> its open claim or report transaction (local values)

vars == <<rows, nextSeq, cache, wstep, wlocks, wrows, hEpoch, hCand, hPos, hLock, epochsSeen,
          cursor, resolved, backoff, delivered, retries, runs, ob>>
writerVars == <<rows, nextSeq, cache, wstep, wlocks, wrows>>
horizonVars == <<hEpoch, hCand, hPos, hLock, epochsSeen>>
observerVars == <<cursor, resolved, backoff, delivered, retries, runs, ob>>

Min(S) == CHOOSE x \in S : \A y \in S : x <= y
Max(S) == CHOOSE x \in S : \A y \in S : x >= y
Max2(a, b) == IF a >= b THEN a ELSE b

\* The subscription filter. A non-matching row needs no delivery and never blocks.
Matches(o, p) == o \notin OddOnly \/ p % 2 = 1

Committed == {p \in Pos : rows[p] = "committed"}
\* SELECT COALESCE(MAX(position), 0) FROM job_transitions: the highest visible Position.
MaxCommitted == IF Committed = {} THEN 0 ELSE Max(Committed)

\* An exclusive lock on epoch e could be granted now: no open transaction holds it shared.
EpochFree(e) == \A w \in Writers : e \notin wlocks[w]

\* src/BackWave/Storage/CommitHorizon.cs:7-13, the pure decision, transcribed.
Advance(st, P, previousFree, currentFree) ==
    IF previousFree /\ currentFree THEN [st EXCEPT !.horizon = Max2(st.horizon, P)]
    ELSE IF previousFree THEN [epoch |-> 1 - st.epoch, candidate |-> P,
                               horizon |-> Max2(st.horizon, IF st.candidate = None THEN st.horizon ELSE st.candidate)]
    ELSE st

Idle == [pc |-> "idle", P |-> 0, H |-> 0, lk |-> NoState,
         pf |-> FALSE, cf |-> FALSE, probed |-> {}, nxt |-> NoState, hz |-> None, blk |-> None, tgt |-> None]

Init ==
    /\ rows = [p \in Pos |-> "free"]
    /\ nextSeq = 1
    /\ cache = [w \in Writers |-> {}]
    /\ wstep = [w \in Writers |-> "idle"]
    /\ wlocks = [w \in Writers |-> {}]
    /\ wrows = [w \in Writers |-> {}]
    /\ hEpoch = 0 /\ hCand = None /\ hPos = 0   \* PG 0005_observer_commit_horizon.sql:12-14
    /\ hLock = {}
    /\ epochsSeen = {0}
    /\ cursor = [o \in Observers |-> 0]
    /\ resolved = [o \in Observers |-> {}]
    /\ backoff = [o \in Observers |-> {}]
    /\ delivered = [o \in Observers |-> {}]
    /\ retries = 0
    /\ runs = [r \in Reporters |-> 0]
    /\ ob = [a \in Actors |-> Idle]

-----------------------------------------------------------------------------
Avail(w) == cache[w] # {} \/ nextSeq + Cache - 1 <= MaxPos
Room(w) == Cardinality(wrows[w]) < MaxRowsPerTxn

(* An insert statement registers on an epoch, before any of its rows draws a
   Position. PG: the BEFORE INSERT FOR EACH STATEMENT trigger
   (PG Schema/0005_observer_commit_horizon.sql:19-39), which reads h.epoch and
   calls pg_advisory_xact_lock_shared. MS: the RegisterTransitionWriter prefix
   (MS SqlServerJobStore.cs:2072-2086), a READCOMMITTED read then sp_getapplock
   Shared. ORA: the statement trigger (ORA Schema/0005_observer_commit_horizon.sql:71-87),
   SELECT epoch then LOCK TABLE ... ROW SHARE.
   The code reads the epoch and takes the lock in two steps, and the read can
   be stale (a REPEATABLE READ, SERIALIZABLE, or SNAPSHOT caller transaction
   reads its own snapshot). The model lets the writer pick any epoch the row
   has held so far, which covers every value the read can return at any
   earlier moment, so it folds the read into the lock step without losing an
   order. A probe holds its
   exclusive lock only inside one atomic model step, so a shared lock that
   waits for a probe is the same as one taken just after it. *)
Register(w) ==
    /\ \/ wstep[w] = "idle" /\ Avail(w)       \* first statement of a new transaction
       \/ wstep[w] = "rows" /\ Room(w)        \* a later insert statement registers again
    /\ \E e \in epochsSeen : wlocks' = [wlocks EXCEPT ![w] = @ \cup {e}]
    /\ wstep' = [wstep EXCEPT ![w] = "registered"]
    /\ UNCHANGED <<rows, nextSeq, cache, wrows, horizonVars, observerVars>>

(* One row draws its Position from the column default and is inserted, still
   invisible. PG Schema/0001_initial.sql:10,129 (sequence, CACHE 1 by default),
   MS Schema/0001_initial.sql:19,156 (one shared in-memory cache, so in order),
   ORA Schema/0001_initial.sql:47,157 (NOCACHE ORDER). Cache = 1 models all
   three. Cache > 1 models a per-session cache (the Postgres CACHE n setting):
   a session reserves a block and uses it in later transactions. A statement
   can insert several rows; other sessions can draw between them.
   With RegisterFirst = FALSE (mutant), a row can also draw before any
   statement of its transaction registers. *)
NextVal(w) ==
    /\ \/ wstep[w] \in {"registered", "rows"}
       \/ ~RegisterFirst /\ wstep[w] = "idle"
    /\ Room(w) /\ Avail(w)
    /\ LET fill == IF cache[w] = {} THEN nextSeq .. (nextSeq + Cache - 1) ELSE cache[w]
           p == Min(fill)
       IN /\ rows' = [rows EXCEPT ![p] = "open"]
          /\ cache' = [cache EXCEPT ![w] = fill \ {p}]
          /\ nextSeq' = IF cache[w] = {} THEN nextSeq + Cache ELSE nextSeq
          /\ wrows' = [wrows EXCEPT ![w] = @ \cup {p}]
    /\ wstep' = [wstep EXCEPT ![w] = "rows"]
    /\ UNCHANGED <<wlocks, horizonVars, observerVars>>

\* A registered statement inserts a row when it can; it ends with none only when
\* the sequence has nothing left for it, so writers stop when Positions run out.
CanEnd(w) == wstep[w] \in {"registered", "rows"} /\ (wrows[w] # {} \/ ~(Room(w) /\ Avail(w)))

(* The transaction commits or rolls back. Its rows become visible (or never
   will), and its registration locks go, in one step. Each engine makes a
   commit visible before it releases the transaction's locks, so a probe never
   finds an epoch free while a row of that epoch is still invisible. *)
EndTxn(w, outcome) ==
    /\ CanEnd(w)
    /\ rows' = [p \in Pos |-> IF p \in wrows[w] THEN outcome ELSE rows[p]]
    /\ wlocks' = [wlocks EXCEPT ![w] = {}]
    /\ wrows' = [wrows EXCEPT ![w] = {}]
    /\ wstep' = [wstep EXCEPT ![w] = "idle"]
    /\ UNCHANGED <<nextSeq, cache, horizonVars, observerVars>>

Commit(w) == EndTxn(w, "committed")
Rollback(w) == EndTxn(w, "rolledback")

-----------------------------------------------------------------------------
HorizonEntry == IF ~UseHorizon THEN "block"
                ELSE IF HorizonOrder = "postgres" THEN "snap" ELSE "lock"

SmallestK(S, k) == {p \in S : Cardinality({q \in S : q < p}) < k}

(* The claim's scan: matching rows after the cursor, not resolved, in Position
   order, LIMIT maxRows, cut at the first row still in its backoff window.
   PG PostgresJobStore.cs:3314-3353, MS SqlServerJobStore.cs:3404-3441,
   ORA OracleJobStore.cs:3582-3620. *)
ClaimScan(o) ==
    LET elig == {p \in Pos : p > cursor[o] /\ rows[p] = "committed" /\ Matches(o, p)
                             /\ p \notin resolved[o]}
        page == SmallestK(elig, MaxRows)
    IN {p \in page : \A q \in page \cap backoff[o] : p < q}

(* ClaimObserverDeliveriesAsync (PG PostgresJobStore.cs:3239-3396), then the
   report when the claim found rows. The claim locks the Observer row and
   reads resolvedAboveCursor (PG :3261-3265), then scans. Only this Observer's
   own transactions touch its row and its observer_deliveries rows, so the
   lock statement commutes with every other actor's step and the claim is
   atomic up to the scan, its one read of job_transitions.
   With rows to deliver, it claims them (PG :3369-3396), the subscriber
   handles them, and TryReportObserverDeliveriesAsync (PG :3405-3499) locks
   the Observer row, passes the fence, and writes each outcome (PG :3473-3492):
   Delivered or DeadLettered sets resolution <> 0, Retry keeps resolution 0
   with a next_attempt_at. Those writes read and touch only this Observer's
   rows, so they commute with every other actor's step and fold into the
   claim. The report then runs the horizon step.
   With no rows and a resolved row above the cursor, the claim's own
   transaction runs the horizon step and sweep (PG :3355-3366).
   With neither, it commits and changes nothing, which the model leaves out. *)
Claim(o) ==
    /\ ob[o].pc = "idle"
    /\ LET c == ClaimScan(o) IN
          \/ /\ c # {}
             /\ \E retry \in SUBSET c :
                   /\ retries + Cardinality(retry) <= MaxRetries
                   /\ resolved' = [resolved EXCEPT ![o] = @ \cup (c \ retry)]
                   /\ delivered' = [delivered EXCEPT ![o] = @ \cup (c \ retry)]
                   /\ backoff' = [backoff EXCEPT ![o] = @ \cup retry]
                   /\ retries' = retries + Cardinality(retry)
          \/ /\ c = {} /\ resolved[o] # {}
             /\ UNCHANGED <<resolved, delivered, backoff, retries>>
    /\ ob' = [ob EXCEPT ![o].pc = HorizonEntry]
    /\ UNCHANGED <<writerVars, horizonVars, cursor, runs>>

\* After the probes: run CommitHorizon.Advance and stage the UPDATE of the row.
\* The UPDATE (PG :3667-3677) writes a row this transaction has locked, and no
\* other actor sees it before commit, so it folds into the step that decides.
Decided(r, P, pf, cf) ==
    LET n == Advance(r.lk, P, pf, cf)
    IN [r EXCEPT !.pc = "sweep", !.nxt = n, !.hz = n.horizon, !.P = 0, !.H = 0,
                 !.lk = NoState, !.pf = FALSE, !.cf = FALSE, !.probed = {}]

(* Read P, the highest visible Position.
   postgres: the statement snapshot of the one horizon statement
   (PG PostgresJobStore.cs:3631-3645). The snapshot is taken when the
   statement starts, before the row lock and the probes, and it also fixes
   h.horizon_pos, the committed horizon the statement returns when the lock is
   skipped.
   lockFirst: its own statement after the lock (MS SqlServerJobStore.cs:3731,
   ORA OracleJobStore.cs:3920).
   pAfterProbes (mutant): P read after both probes, then the decision. *)
Snap(o) ==
    /\ ob[o].pc = "snap"
    /\ ob' = CASE HorizonOrder = "postgres" ->
                    [ob EXCEPT ![o].P = MaxCommitted, ![o].H = IF o \in Observers THEN hPos ELSE 0,
                               ![o].pc = "lock"]
               [] HorizonOrder = "lockFirst" ->
                    [ob EXCEPT ![o].P = MaxCommitted, ![o].pc = "probe"]
               [] OTHER ->
                    [ob EXCEPT ![o] = Decided(ob[o], MaxCommitted, ob[o].pf, ob[o].cf)]
    /\ UNCHANGED <<writerVars, horizonVars, cursor, resolved, backoff, delivered, retries, runs>>

(* Lock the observer_log_horizon row with SKIP LOCKED. A lock reads the newest
   committed version of the row. PG :3633-3636 (FOR UPDATE SKIP LOCKED in the
   CTE), MS :3724-3726 (UPDLOCK, READPAST), ORA :3906-3909 (FOR UPDATE SKIP
   LOCKED). If another report holds it: postgres returns the snapshot's
   h.horizon_pos (PG :3655-3658); lockFirst reads the committed horizon in a
   next statement (ReadH). *)
Lock(o) ==
    /\ ob[o].pc = "lock"
    /\ IF hLock = {}
       THEN /\ hLock' = {o}
            /\ ob' = [ob EXCEPT ![o].lk = [epoch |-> hEpoch, candidate |-> hCand, horizon |-> hPos],
                                ![o].H = 0,
                                ![o].pc = IF HorizonOrder = "lockFirst" THEN "snap" ELSE "probe"]
       ELSE /\ UNCHANGED hLock
            /\ ob' = CASE o \in Reporters -> [ob EXCEPT ![o] = Idle]  \* its sweep touches nothing shared
                    [] HorizonOrder = "postgres" ->
                         [ob EXCEPT ![o].hz = ob[o].H, ![o].P = 0, ![o].H = 0, ![o].pc = "sweep"]
                    [] OTHER -> [ob EXCEPT ![o].pc = "readH"]
    /\ UNCHANGED <<writerVars, hEpoch, hCand, hPos, epochsSeen, cursor, resolved, backoff, delivered, retries, runs>>

\* lockFirst, lock skipped: MS SqlServerJobStore.cs:3728, ORA OracleJobStore.cs:3916.
ReadH(o) ==
    /\ ob[o].pc = "readH"
    /\ ob' = [ob EXCEPT ![o].hz = hPos, ![o].pc = "sweep"]
    /\ UNCHANGED <<writerVars, horizonVars, cursor, resolved, backoff, delivered, retries, runs>>

(* The two probes, each in one step: a probe takes and gives back its
   exclusive lock, or takes none. The previous epoch is 1 - epoch of the locked
   row. PG :3639-3640 with observer_log_epoch_free
   (PG Schema/0005_observer_commit_horizon.sql:43-55), MS :3733-3736
   (APPLOCK_TEST), ORA :3921-3922 (observer_log_epoch_free,
   ORA Schema/0005_observer_commit_horizon.sql:52-67).
   The postgres order probes the previous epoch first. MS computes both
   APPLOCK_TEST calls in one SELECT list, and SQL Server does not promise the
   order in which it computes a SELECT list, so in the lockFirst order either
   probe can run first. ORA probes the previous epoch first, one of the two
   orders, so the free order covers it. *)
EitherProbeFirst == HorizonOrder = "lockFirst"

\* After both probes: decide (PG :3666, MS :3760, ORA :3945), unless the mutant
\* still has to read P.
Probed(r) ==
    IF r.probed # {"prev", "cur"} THEN r
    ELSE IF HorizonOrder = "pAfterProbes" THEN [r EXCEPT !.pc = "snap"]
    ELSE Decided(r, r.P, r.pf, r.cf)

ProbePrev(o) ==
    /\ ob[o].pc = "probe" /\ "prev" \notin ob[o].probed
    /\ ob' = [ob EXCEPT ![o] = Probed([ob[o] EXCEPT
                 !.pf = ProbeMode = "currentOnly" \/ EpochFree(1 - ob[o].lk.epoch),
                 !.probed = @ \cup {"prev"}])]
    /\ UNCHANGED <<writerVars, horizonVars, cursor, resolved, backoff, delivered, retries, runs>>

ProbeCur(o) ==
    /\ ob[o].pc = "probe" /\ "cur" \notin ob[o].probed
    /\ "prev" \in ob[o].probed \/ EitherProbeFirst
    /\ ob' = [ob EXCEPT ![o] = Probed([ob[o] EXCEPT
                 !.cf = ProbeMode = "previousOnly" \/ EpochFree(ob[o].lk.epoch),
                 !.probed = @ \cup {"cur"}])]
    /\ UNCHANGED <<writerVars, horizonVars, cursor, resolved, backoff, delivered, retries, runs>>

(* The block query: the first matching row after the cursor, at or below the
   bound, that is not resolved. PG PostgresJobStore.cs:3519-3543,
   MS SqlServerJobStore.cs:3610-3632, ORA OracleJobStore.cs:3786-3808. *)
BlockOf(o, bound) ==
    LET B == {p \in Pos : p > cursor[o] /\ p <= bound /\ rows[p] = "committed"
                         /\ Matches(o, p) /\ p \notin resolved[o]}
    IN IF B = {} THEN None ELSE Min(B)

(* The target query T: the last visible row after the cursor, at or below the
   bound, before the block, matching or not. PG :3545-3556, MS :3634-3645,
   ORA :3810-3821. *)
TargetOf(o, bound, blk) ==
    LET T == {p \in Pos : p > cursor[o] /\ p <= bound /\ rows[p] = "committed"
                         /\ (blk = None \/ p < blk)}
    IN IF T = {} THEN None ELSE Max(T)

(* Record dead letters, delete observer_deliveries rows at or below the
   target, move the cursor (PG :3573-3618), then commit (PG :3365 for a claim,
   :3498 for a report). The commit publishes the staged horizon row and frees
   its lock. The writes before the commit read only rows at or below the
   target and no other actor sees them before the commit, so they fold into
   the commit step. *)
FinishWith(o, t) ==
    /\ IF o \in Observers /\ t # None /\ t > cursor[o]
       THEN /\ cursor' = [cursor EXCEPT ![o] = t]
            /\ resolved' = [resolved EXCEPT ![o] = {p \in @ : p > t}]
            /\ backoff' = [backoff EXCEPT ![o] = {p \in @ : p > t}]
       ELSE UNCHANGED <<cursor, resolved, backoff>>
    /\ IF hLock = {o}
       THEN /\ hEpoch' = ob[o].nxt.epoch /\ hCand' = ob[o].nxt.candidate /\ hPos' = ob[o].nxt.horizon
            /\ hLock' = {}
            /\ epochsSeen' = epochsSeen \cup {ob[o].nxt.epoch}
       ELSE UNCHANGED horizonVars
    /\ ob' = [ob EXCEPT ![o] = Idle]
    /\ UNCHANGED <<writerVars, delivered, retries, runs>>

(* With the horizon, the block query, T, and the commit run as one step. They
   are three statements in the code, but both queries read only rows at or
   below the horizon, and HorizonSafe (checked in the same run) says each of
   those rows is final, so no other actor's step can change what they return.
   Each other step commutes with them, so running them at the commit loses no
   outcome. The first state where that premise fails already breaks
   HorizonSafe, so a mutant that breaks it still fails. *)
Sweep(o) ==
    /\ ob[o].pc = "sweep"
    /\ IF o \in Observers
       THEN LET b == BlockOf(o, ob[o].hz) IN FinishWith(o, TargetOf(o, ob[o].hz, b))
       ELSE FinishWith(o, None)

(* The rule before the Commit Horizon has no horizon, so the commutation above
   does not hold: a row can commit between the block query and T. Without the
   horizon the three steps stay apart, and the queries read every Position. *)
Block(o) ==
    /\ ob[o].pc = "block"
    /\ ob' = [ob EXCEPT ![o].blk = BlockOf(o, MaxPos), ![o].pc = "target"]
    /\ UNCHANGED <<writerVars, horizonVars, cursor, resolved, backoff, delivered, retries, runs>>

Target(o) ==
    /\ ob[o].pc = "target"
    /\ ob' = [ob EXCEPT ![o].tgt = TargetOf(o, MaxPos, ob[o].blk), ![o].pc = "commit"]
    /\ UNCHANGED <<writerVars, horizonVars, cursor, resolved, backoff, delivered, retries, runs>>

Finish(o) ==
    /\ ob[o].pc = "commit"
    /\ FinishWith(o, ob[o].tgt)

\* A Retry row's next_attempt_at passes (PG :3343 stops holding the claim there).
Elapse(o) ==
    /\ \E p \in backoff[o] : backoff' = [backoff EXCEPT ![o] = @ \ {p}]
    /\ UNCHANGED <<writerVars, horizonVars, cursor, resolved, delivered, retries, runs, ob>>

Done ==
    /\ \A w \in Writers : wstep[w] = "idle" /\ ~Avail(w)
    /\ \A a \in Actors : ob[a].pc = "idle"
    /\ UNCHANGED vars

(* Another Observer's report or catch-up claim: it starts the horizon step at
   any moment. This over-approximates when a second Observer reaches the
   horizon row, and that row is all the two share. *)
ReporterStart(r) ==
    /\ UseHorizon /\ ob[r].pc = "idle" /\ runs[r] < ReporterRuns
    /\ ob' = [ob EXCEPT ![r].pc = HorizonEntry]
    /\ runs' = [runs EXCEPT ![r] = @ + 1]
    /\ UNCHANGED <<writerVars, horizonVars, cursor, resolved, backoff, delivered, retries>>

HorizonStep(a) ==
    Snap(a) \/ Lock(a) \/ ReadH(a) \/ ProbePrev(a) \/ ProbeCur(a) \/ Sweep(a)

ObserverStep(o) == Claim(o) \/ HorizonStep(o) \/ Block(o) \/ Target(o) \/ Finish(o)

Next ==
    \/ \E w \in Writers : Register(w) \/ NextVal(w) \/ Commit(w) \/ Rollback(w)
    \/ \E o \in Observers : ObserverStep(o) \/ Elapse(o)
    \/ \E r \in Reporters : ReporterStart(r) \/ HorizonStep(r)
    \/ Done

(* Fairness, as the code justifies it. A registered statement inserts its
   rows and its transaction ends (the host ends every Transactional Enqueue).
   No writer has to start a new transaction. Each Observer pump polls forever,
   so an enabled claim or report step runs. A backoff window passes. A
   stand-in report that has started finishes, but need not start. *)
Fairness ==
    /\ \A w \in Writers : WF_vars(NextVal(w)) /\ WF_vars(Commit(w) \/ Rollback(w))
    /\ \A o \in Observers : WF_vars(ObserverStep(o)) /\ WF_vars(Elapse(o))
    /\ \A r \in Reporters : WF_vars(HorizonStep(r))

Spec == Init /\ [][Next]_vars /\ Fairness

-----------------------------------------------------------------------------
TypeOK ==
    /\ rows \in [Pos -> {"free", "open", "committed", "rolledback"}]
    /\ nextSeq \in 1..(MaxPos + Cache)
    /\ hEpoch \in Epochs /\ hCand \in {None} \cup (0..MaxPos) /\ hPos \in 0..MaxPos
    /\ hLock \subseteq Actors /\ Cardinality(hLock) <= 1
    /\ epochsSeen \subseteq Epochs /\ hEpoch \in epochsSeen
    /\ hPos <= MaxCommitted
    /\ cursor \in [Observers -> 0..MaxPos]

(* The storage contract, section 5.13: the cursor is a Position up to and
   including which every matching row has been delivered or dead-lettered. *)
CursorSafe ==
    \A o \in Observers : \A p \in Pos :
        (p <= cursor[o] /\ rows[p] = "committed" /\ Matches(o, p)) => p \in delivered[o]

FinalUpTo(h) == \A p \in Pos : p <= h => rows[p] \in {"committed", "rolledback"}
HorizonSafe ==
    /\ FinalUpTo(hPos)
    /\ \A a \in Actors : ob[a].hz # None => FinalUpTo(ob[a].hz)

CaughtUp ==
    /\ \A p \in Committed : (\E o \in Observers : Matches(o, p)) => p <= hPos
    /\ \A o \in Observers \ OddOnly : cursor[o] = MaxCommitted
    /\ \A o \in Observers : \A p \in Committed : Matches(o, p) => p <= cursor[o]
Liveness == <>[]CaughtUp

WriterSymmetry == Permutations(Writers)
=============================================================================
