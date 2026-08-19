-- BackWave schema v1 (Oracle dialect). Idempotent: safe to run on every deploy.
-- This file is the canonical schema artifact - a versioned, DBA-reviewable script;
-- auto-migrate executes exactly this script, nothing else.
--
-- The whole script is ONE anonymous PL/SQL block, so the driver runs it in a single round-trip and the
-- objects are created in order (the sequence a column default reads must exist before the table). Oracle
-- has no CREATE ... IF NOT EXISTS below 23ai and the 19c floor forbids it, so each object is created
-- through the local ddl() procedure, which swallows the "already there" errors (ORA-00955 name in use,
-- ORA-01430 column exists, ORA-00001 unique row, and the constraint-exists codes). This makes the script
-- idempotent AND concurrency-safe: when a fleet cold-boots, Oracle serializes DDL on each object inside the
-- database, so the loser of every race gets ORA-00955 and no-ops. No application lock is needed.
--
-- Oracle notes that shape the mapping:
--   * READ COMMITTED already gives non-blocking MVCC reads (readers never block writers), so no database
--     setting like SQL Server RCSI is needed.
--   * uniqueidentifier -> RAW(16); varbinary(max) -> BLOB; nvarchar(n) -> VARCHAR2(n CHAR);
--     nvarchar(max) -> CLOB; datetimeoffset(7) -> TIMESTAMP(7) WITH TIME ZONE; bit -> NUMBER(1);
--     int -> NUMBER(10); bigint -> NUMBER(19); bigint IDENTITY -> GENERATED ALWAYS AS IDENTITY.
--   * The column named `mode` in the other adapters is `job_mode` here (MODE is an Oracle reserved word).
--   * Oracle stores '' as NULL, so a NOT NULL "" column is impossible. observers.sub_states is therefore
--     nullable and the store maps NULL <-> "". job_tags key/value are NOT NULL in the primary key; the
--     store encodes the empty Label key as CHR(1) so the column is never actually empty (see job_tags).
--   * Oracle has no filtered (partial) indexes. The indexes below are plain B-tree. A single-column index
--     already omits NULL keys, so the schedule_id and workflow_id indexes are partial for free; the others
--     also index rows outside the old filter. Function-based partial equivalents are a future optimization.

DECLARE
    -- Runs one DDL/DML statement and ignores the "object/row already exists" family, so re-running the
    -- script - or two fleet nodes running it at once - converges instead of failing.
    PROCEDURE ddl(statement IN VARCHAR2) IS
    BEGIN
        EXECUTE IMMEDIATE statement;
    EXCEPTION
        WHEN OTHERS THEN
            -- -955 name already used, -1430 column exists, -957 duplicate column name,
            -- -2260/-2261/-2264/-2275 constraint/PK already there, -1442/-1451 nullability already set,
            -- -1408 index column list already indexed, -1 unique row already present (version seed).
            IF SQLCODE NOT IN (-955, -1430, -957, -2260, -2261, -2264, -2275, -1442, -1451, -1408, -1) THEN
                RAISE;
            END IF;
    END;
BEGIN
    -- A global, monotonic Position over every recorded Transition - the order the Observer cursors walk
    -- (the per-job `ordinal` only orders within one job). job_transitions.position reads its DEFAULT from
    -- this sequence, so recording a Transition needs no awareness of Observers. Created first so that
    -- default resolves.
    ddl(q'{CREATE SEQUENCE backwave.observer_log_position START WITH 1 INCREMENT BY 1 NOCACHE ORDER}');

    ddl(q'{CREATE TABLE backwave.schema_version (
        version NUMBER(10) NOT NULL PRIMARY KEY
    )}');

    -- States: 0 Scheduled, 1 AwaitingParent, 2 Leased, 3 Succeeded, 4 Cancelled,
    --         5 DeadLettered, 6 Quarantined.
    -- workflow_id: the membership grouping key, set once at workflow enqueue, <=1 per job, never rewritten;
    --         NULL for a non-workflow job. Above the determinism boundary - the Core never reads it.
    -- output: the opaque blob a handler emits via SetOutput on its Succeeded Attempt (functional data a
    --         Dependency descendant pulls, NOT diagnostics); NULL when a job never set output.
    ddl(q'{CREATE TABLE backwave.jobs (
        job_id            RAW(16) NOT NULL PRIMARY KEY,
        wire_name         VARCHAR2(450 CHAR) NOT NULL,
        payload           BLOB NOT NULL,
        trace_context     VARCHAR2(450 CHAR) NULL,
        queue             VARCHAR2(450 CHAR) NOT NULL,
        state             NUMBER(10) NOT NULL,
        due_time          TIMESTAMP(7) WITH TIME ZONE NOT NULL,
        attempt           NUMBER(10) DEFAULT 0 NOT NULL,
        lease_owner       VARCHAR2(450 CHAR) NULL,
        lease_expiry      TIMESTAMP(7) WITH TIME ZONE NULL,
        cancel_requested  NUMBER(1) DEFAULT 0 NOT NULL,
        terminal_at       TIMESTAMP(7) WITH TIME ZONE NULL,
        terminal_cause    CLOB NULL,
        schedule_id       VARCHAR2(450 CHAR) NULL,
        parents_remaining NUMBER(10) DEFAULT 0 NOT NULL,
        job_mode          NUMBER(10) DEFAULT 0 NOT NULL,
        sequence          NUMBER(19) GENERATED ALWAYS AS IDENTITY NOT NULL,
        workflow_id       RAW(16) NULL,
        output            BLOB NULL
    )}');

    -- The claim path: due Scheduled jobs per queue, oldest due first.
    ddl(q'{CREATE INDEX backwave.ix_bw_jobs_claim ON backwave.jobs (queue, due_time, sequence)}');
    -- The expiry sweep and the I3 slot count: live Leases per queue by expiry.
    ddl(q'{CREATE INDEX backwave.ix_bw_jobs_leased_queue ON backwave.jobs (queue, lease_expiry)}');
    -- Single-column index over a nullable column: Oracle omits NULL keys, so this is partial for free.
    ddl(q'{CREATE INDEX backwave.ix_bw_jobs_schedule ON backwave.jobs (schedule_id)}');
    -- The retention sweep: terminal jobs by class and terminal instant.
    ddl(q'{CREATE INDEX backwave.ix_bw_jobs_terminal ON backwave.jobs (state, terminal_at, sequence)}');
    -- Monitor list pagination orders by the sequence key.
    ddl(q'{CREATE INDEX backwave.ix_bw_jobs_sequence ON backwave.jobs (sequence)}');
    -- The per-Workflow member scan. Nullable single column -> partial for free.
    ddl(q'{CREATE INDEX backwave.ix_bw_jobs_workflow ON backwave.jobs (workflow_id)}');

    -- The Continuation latch edges: each row resolves exactly once (invariant I2).
    ddl(q'{CREATE TABLE backwave.job_parents (
        parent_id RAW(16) NOT NULL REFERENCES backwave.jobs (job_id),
        child_id  RAW(16) NOT NULL REFERENCES backwave.jobs (job_id),
        PRIMARY KEY (parent_id, child_id)
    )}');
    ddl(q'{CREATE INDEX backwave.ix_bw_job_parents_child ON backwave.job_parents (child_id)}');

    ddl(q'{CREATE TABLE backwave.schedules (
        schedule_id   VARCHAR2(450 CHAR) NOT NULL PRIMARY KEY,
        cron          VARCHAR2(450 CHAR) NOT NULL,
        wire_name     VARCHAR2(450 CHAR) NOT NULL,
        payload       BLOB NOT NULL,
        queue         VARCHAR2(450 CHAR) NOT NULL,
        cursor        TIMESTAMP(7) WITH TIME ZONE NOT NULL,
        time_zone_id  VARCHAR2(450 CHAR) NULL,
        catch_up      NUMBER(10) DEFAULT 0 NOT NULL,
        no_overlap    NUMBER(1) DEFAULT 0 NOT NULL,
        skipped_ticks CLOB DEFAULT '[]' NOT NULL
    )}');

    -- A Paused Queue yields nothing to Claim. The flag lives on queue_limits so the claim path reads
    -- limit and pause state in the one row it already locks per queue.
    ddl(q'{CREATE TABLE backwave.queue_limits (
        queue          VARCHAR2(450 CHAR) NOT NULL PRIMARY KEY,
        max_concurrent NUMBER(10) NULL,
        paused         NUMBER(1) DEFAULT 0 NOT NULL
    )}');

    -- Per-queue serialization anchor. FOR UPDATE can only lock a row that exists, so a claim and a
    -- first-ever pause/limit on the same queue would otherwise lock nothing and race. Both paths first
    -- materialize and lock the queue's row here (a concurrent insert loses the primary key and no-ops),
    -- which serializes them WITHOUT putting a phantom row into the operator-owned queue_limits listing.
    ddl(q'{CREATE TABLE backwave.queue_locks (
        queue VARCHAR2(450 CHAR) NOT NULL PRIMARY KEY
    )}');

    -- Every Operator Action appends one record atomically with its effect. Append-only.
    -- action: 0 Cancel, 1 Requeue, 2 TriggerScheduleNow, 3 PauseQueue, 4 ResumeQueue.
    ddl(q'{CREATE TABLE backwave.operator_audit (
        sequence    NUMBER(19) GENERATED ALWAYS AS IDENTITY NOT NULL PRIMARY KEY,
        actor       VARCHAR2(450 CHAR) NOT NULL,
        action      NUMBER(10) NOT NULL,
        target      VARCHAR2(450 CHAR) NOT NULL,
        recorded_at TIMESTAMP(7) WITH TIME ZONE NOT NULL
    )}');
    -- Audit reads are by target, oldest first.
    ddl(q'{CREATE INDEX backwave.ix_bw_operator_audit_target ON backwave.operator_audit (target, sequence)}');

    -- The Transition Log: an append-only, per-job history of state changes the Monitor surfaces as a
    -- timeline. Every state-changing op appends one row in the SAME transaction as the change. Bounded per
    -- job life (oldest dropped past the cap). Deleted WITH the job via FK cascade.
    -- ordinal: per-job sequence, 0-based, oldest first; climbs even as old rows age out past the cap.
    -- failure_detail: opaque diagnostics of a failed Attempt.
    -- position: the global walk order; auto-fills from the observer_log_position sequence, so History Policy
    --         still gates whether a row is written at all (Off yields nothing to walk).
    ddl(q'{CREATE TABLE backwave.job_transitions (
        job_id         RAW(16) NOT NULL REFERENCES backwave.jobs (job_id) ON DELETE CASCADE,
        ordinal        NUMBER(19) NOT NULL,
        recorded_at    TIMESTAMP(7) WITH TIME ZONE NOT NULL,
        state          NUMBER(10) NOT NULL,
        attempt        NUMBER(10) NOT NULL,
        failure_detail CLOB NULL,
        position       NUMBER(19) DEFAULT backwave.observer_log_position.NEXTVAL NOT NULL,
        PRIMARY KEY (job_id, ordinal)
    )}');
    -- The claim/report walk scans `position > cursor` across all jobs, in Position order.
    ddl(q'{CREATE INDEX backwave.ix_bw_job_transitions_position ON backwave.job_transitions (position)}');

    -- One row per registered Observer: its durable cursor, claim Lease, and the subscription filter.
    -- cursor_pos: global Position up to and including which every matching row is delivered-or-dead-lettered;
    --         -1 means nothing delivered yet (deliver from the first Position).
    -- sub_states: comma-joined JobState ints the Observer watches. Oracle stores '' as NULL, so this column
    --         is nullable and the store maps NULL <-> "" (watch-any). sub_wire_name / sub_queue: NULL = any.
    ddl(q'{CREATE TABLE backwave.observers (
        observer_id   VARCHAR2(256 CHAR) NOT NULL PRIMARY KEY,
        cursor_pos    NUMBER(19) DEFAULT -1 NOT NULL,
        lease_owner   VARCHAR2(256 CHAR) NULL,
        lease_expiry  TIMESTAMP(7) WITH TIME ZONE NULL,
        sub_states    VARCHAR2(256 CHAR) NULL,
        sub_wire_name VARCHAR2(450 CHAR) NULL,
        sub_queue     VARCHAR2(450 CHAR) NULL
    )}');

    -- Per-(Observer, Position) delivery bookkeeping: the at-least-once attempt counter and resolution.
    -- resolution: 0 Pending, 1 Delivered, 2 DeadLettered. next_attempt_at holds a Retry's backoff instant.
    ddl(q'{CREATE TABLE backwave.observer_deliveries (
        observer_id      VARCHAR2(256 CHAR) NOT NULL,
        position         NUMBER(19) NOT NULL,
        delivery_attempt NUMBER(10) NOT NULL,
        resolution       NUMBER(10) DEFAULT 0 NOT NULL,
        next_attempt_at  TIMESTAMP(7) WITH TIME ZONE NULL,
        PRIMARY KEY (observer_id, position)
    )}');

    -- Dead-lettered deliveries: poison rows that exhausted their ceiling - metadata only, never payload or
    -- Failure Detail. Standalone (no FK): the record outlives the Transition and the job it came from.
    ddl(q'{CREATE TABLE backwave.observer_dead_letters (
        observer_id       VARCHAR2(256 CHAR) NOT NULL,
        position          NUMBER(19) NOT NULL,
        job_id            RAW(16) NOT NULL,
        ordinal           NUMBER(19) NOT NULL,
        state             NUMBER(10) NOT NULL,
        attempt           NUMBER(10) NOT NULL,
        delivery_attempts NUMBER(10) NOT NULL,
        dead_lettered_at  TIMESTAMP(7) WITH TIME ZONE NOT NULL,
        PRIMARY KEY (observer_id, position)
    )}');

    -- Job Tags: an observational string-set the Core never reads, attached at enqueue and unioned by the
    -- fenced outcome write. A child table so filtering, grouping, and multi-value are plain portable SQL.
    -- Deleted WITH the job via FK cascade. A Label's key is the empty string, and Oracle cannot store ''
    -- (it becomes NULL, which a primary-key column forbids). The store therefore encodes the empty string
    -- as CHR(1) on write and decodes it on read, so `key` and `value` are never actually empty and the PK
    -- (job_id, key, value) enforces per-job uniqueness. key_lower/value_lower are virtual LOWER folds; the
    -- default BINARY sort makes the Tag Suggest range walk seek in byte-ordinal order, and CHR(1) sorts
    -- before every printable character, so an empty-key Label sorts first as it does on the other adapters.
    ddl(q'{CREATE TABLE backwave.job_tags (
        job_id      RAW(16) NOT NULL REFERENCES backwave.jobs (job_id) ON DELETE CASCADE,
        key         VARCHAR2(256 CHAR) NOT NULL,
        value       VARCHAR2(256 CHAR) NOT NULL,
        key_lower   VARCHAR2(256 CHAR) GENERATED ALWAYS AS (LOWER(key)) VIRTUAL,
        value_lower VARCHAR2(256 CHAR) GENERATED ALWAYS AS (LOWER(value)) VIRTUAL,
        PRIMARY KEY (job_id, key, value)
    )}');
    -- The unscoped facet/filter read groups by dimension.
    ddl(q'{CREATE INDEX backwave.ix_bw_job_tags_key_value ON backwave.job_tags (key, value)}');
    -- Tag Suggest: the typeahead range walk over the folded tokens, seekable because BINARY sort orders it.
    ddl(q'{CREATE INDEX backwave.ix_bw_job_tags_lower ON backwave.job_tags (key_lower, value_lower)}');

    -- The Workflows row: identity + config. A Workflow's status is always a PROJECTION of member states,
    -- never stored. retention is the policy enum (0 = UnitUntilDrained). restarted_from is the lineage
    -- pointer: the WorkflowId a Restart re-instantiated from, or NULL for a fresh creation.
    ddl(q'{CREATE TABLE backwave.workflows (
        workflow_id    RAW(16) NOT NULL PRIMARY KEY,
        name           CLOB NULL,
        created_at     TIMESTAMP(7) WITH TIME ZONE NOT NULL,
        retention      NUMBER(10) DEFAULT 0 NOT NULL,
        restarted_from RAW(16) NULL
    )}');

    -- The IMMUTABLE structural edges: parent -> child, recorded once at enqueue and never deleted (unlike
    -- job_parents, which the latch cascade resolves away). Cascades away when the Workflow row is dropped.
    ddl(q'{CREATE TABLE backwave.workflow_edges (
        workflow_id RAW(16) NOT NULL REFERENCES backwave.workflows (workflow_id) ON DELETE CASCADE,
        parent_id   RAW(16) NOT NULL,
        child_id    RAW(16) NOT NULL,
        PRIMARY KEY (workflow_id, parent_id, child_id)
    )}');

    -- Seed the schema version. The PK on `version` makes a concurrent second insert an ORA-00001 that
    -- ddl() swallows, so a fleet cold-boot lands exactly one row.
    ddl(q'{INSERT INTO backwave.schema_version (version)
        SELECT 1 FROM dual WHERE NOT EXISTS (SELECT 1 FROM backwave.schema_version)}');
END;
