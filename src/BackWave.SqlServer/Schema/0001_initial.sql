-- BackWave schema v1 (SQL Server dialect). Idempotent: safe to run on every deploy.
-- This file is the canonical schema artifact — a versioned, DBA-reviewable script;
-- auto-migrate executes exactly this script, nothing else.

-- The contract's read semantics (reads see committed state, never in-flight transactions, and never
-- block on them) need MVCC-style reads. RCSI is how SQL Server does that; without it a pending
-- Transactional Enqueue would block Monitor reads.
IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_read_committed_snapshot_on = 0)
    ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'backwave')
    EXEC('CREATE SCHEMA backwave');

-- A global, monotonic Position over every recorded Transition — the order the Observer cursors walk
-- (the per-job `ordinal` only orders within one job). job_transitions.position carries its DEFAULT, so
-- recording a Transition needs no awareness of Observers. Declared first so that column default resolves.
IF NOT EXISTS (SELECT 1 FROM sys.sequences
               WHERE name = 'observer_log_position' AND SCHEMA_NAME(schema_id) = 'backwave')
    EXEC('CREATE SEQUENCE backwave.observer_log_position AS bigint START WITH 1 INCREMENT BY 1');

IF OBJECT_ID('backwave.schema_version', 'U') IS NULL
    CREATE TABLE backwave.schema_version (
        version int NOT NULL
    );

-- States: 0 Scheduled, 1 AwaitingParent, 2 Leased, 3 Succeeded, 4 Cancelled,
--         5 DeadLettered, 6 Quarantined.
-- workflow_id: the membership grouping key, set once at workflow enqueue, <=1 per job, never rewritten;
--         NULL for a non-workflow job. Above the determinism boundary — the Core never reads it.
-- output: the opaque blob a handler emits via SetOutput on its Succeeded Attempt (functional data a
--         Dependency descendant pulls, NOT diagnostics); NULL when a job never set output. Kept off the
--         listing/claim path (never in JobColumns) so a large blob never rides a hot read.
IF OBJECT_ID('backwave.jobs', 'U') IS NULL
    CREATE TABLE backwave.jobs (
        job_id            uniqueidentifier NOT NULL PRIMARY KEY,
        wire_name         nvarchar(450) NOT NULL,
        payload           varbinary(max) NOT NULL,
        trace_context     nvarchar(450) NULL,
        queue             nvarchar(450) NOT NULL,
        state             int NOT NULL,
        due_time          datetimeoffset(7) NOT NULL,
        attempt           int NOT NULL DEFAULT 0,
        lease_owner       nvarchar(450) NULL,
        lease_expiry      datetimeoffset(7) NULL,
        cancel_requested  bit NOT NULL DEFAULT 0,
        terminal_at       datetimeoffset(7) NULL,
        terminal_cause    nvarchar(max) NULL,
        schedule_id       nvarchar(450) NULL,
        parents_remaining int NOT NULL DEFAULT 0,
        mode              int NOT NULL DEFAULT 0,
        [sequence]        bigint IDENTITY(1,1) NOT NULL,
        workflow_id       uniqueidentifier NULL,
        output            varbinary(max) NULL
    );

-- The claim path: due Scheduled jobs per queue, oldest due first.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_backwave_jobs_claim')
    CREATE INDEX ix_backwave_jobs_claim
        ON backwave.jobs (queue, due_time, [sequence]) WHERE state = 0;

-- The expiry sweep: live Leases per queue by expiry — also the I3 slot count per queue, so keying
-- (queue, lease_expiry) lets that count seek instead of scanning every live Lease cluster-wide.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_backwave_jobs_leased_queue')
    CREATE INDEX ix_backwave_jobs_leased_queue
        ON backwave.jobs (queue, lease_expiry) WHERE state = 2;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_backwave_jobs_schedule')
    CREATE INDEX ix_backwave_jobs_schedule
        ON backwave.jobs (schedule_id) WHERE schedule_id IS NOT NULL;

-- The retention sweep: terminal jobs by class and terminal instant. Swept every poll, so it must
-- never scan the live table.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_backwave_jobs_terminal')
    CREATE INDEX ix_backwave_jobs_terminal
        ON backwave.jobs (state, terminal_at, [sequence]) WHERE terminal_at IS NOT NULL;

-- Monitor list pagination orders by the sequence key, so pages seek instead of sorting the whole table.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_backwave_jobs_sequence')
    CREATE INDEX ix_backwave_jobs_sequence
        ON backwave.jobs ([sequence]);

-- The per-Workflow member scan (graph read, status projection, drain check). EXEC defers compilation
-- so the filtered index over workflow_id resolves regardless of batch-compile ordering.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_backwave_jobs_workflow')
    EXEC('CREATE INDEX ix_backwave_jobs_workflow
        ON backwave.jobs (workflow_id) WHERE workflow_id IS NOT NULL');

-- The Continuation latch edges: each row resolves exactly once (invariant I2).
IF OBJECT_ID('backwave.job_parents', 'U') IS NULL
    CREATE TABLE backwave.job_parents (
        parent_id uniqueidentifier NOT NULL REFERENCES backwave.jobs (job_id),
        child_id  uniqueidentifier NOT NULL REFERENCES backwave.jobs (job_id),
        PRIMARY KEY (parent_id, child_id)
    );

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_backwave_job_parents_child')
    CREATE INDEX ix_backwave_job_parents_child
        ON backwave.job_parents (child_id);

IF OBJECT_ID('backwave.schedules', 'U') IS NULL
    CREATE TABLE backwave.schedules (
        schedule_id   nvarchar(450) NOT NULL PRIMARY KEY,
        cron          nvarchar(450) NOT NULL,
        wire_name     nvarchar(450) NOT NULL,
        payload       varbinary(max) NOT NULL,
        queue         nvarchar(450) NOT NULL,
        [cursor]      datetimeoffset(7) NOT NULL,
        time_zone_id  nvarchar(450) NULL,
        catch_up      int NOT NULL DEFAULT 0,
        no_overlap    bit NOT NULL DEFAULT 0,
        skipped_ticks nvarchar(max) NOT NULL DEFAULT '[]'
    );

-- A Paused Queue yields nothing to Claim. The flag lives on queue_limits so the claim path reads
-- limit and pause state in the one row it already locks per queue.
IF OBJECT_ID('backwave.queue_limits', 'U') IS NULL
    CREATE TABLE backwave.queue_limits (
        queue          nvarchar(450) NOT NULL PRIMARY KEY,
        max_concurrent int NULL,
        paused         bit NOT NULL CONSTRAINT df_backwave_queue_limits_paused DEFAULT 0
    );

-- Every Operator Action appends one record atomically with its effect. Append-only.
-- action: 0 Cancel, 1 Requeue, 2 TriggerScheduleNow, 3 PauseQueue, 4 ResumeQueue.
IF OBJECT_ID('backwave.operator_audit', 'U') IS NULL
    CREATE TABLE backwave.operator_audit (
        [sequence]  bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        actor       nvarchar(450) NOT NULL,
        action      int NOT NULL,
        target      nvarchar(450) NOT NULL,
        recorded_at datetimeoffset(7) NOT NULL
    );

-- Audit reads are by target, oldest first.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_backwave_operator_audit_target')
    CREATE INDEX ix_backwave_operator_audit_target
        ON backwave.operator_audit (target, [sequence]);

-- The Transition Log: an append-only, per-job history of state changes the Monitor surfaces as a
-- timeline. Every state-changing op appends one row in the SAME transaction as the change. Bounded per
-- job life (oldest dropped past the cap). Deleted WITH the job via FK cascade.
-- ordinal: per-job sequence, 0-based, oldest first; climbs even as old rows age out past the cap.
-- failure_detail: opaque diagnostics of a failed Attempt.
-- position: the global walk order; auto-fills from the observer_log_position sequence, so History Policy
--         still gates whether a row is written at all (Off yields nothing to walk).
IF OBJECT_ID('backwave.job_transitions', 'U') IS NULL
    CREATE TABLE backwave.job_transitions (
        job_id         uniqueidentifier NOT NULL
                       REFERENCES backwave.jobs (job_id) ON DELETE CASCADE,
        ordinal        bigint NOT NULL,
        recorded_at    datetimeoffset(7) NOT NULL,
        state          int NOT NULL,
        attempt        int NOT NULL,
        failure_detail nvarchar(max) NULL,
        position       bigint NOT NULL
                       CONSTRAINT df_backwave_job_transitions_position DEFAULT (NEXT VALUE FOR backwave.observer_log_position),
        PRIMARY KEY (job_id, ordinal)
    );

-- The claim/report walk scans `position > cursor` across all jobs, in Position order.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'ix_backwave_job_transitions_position'
                 AND object_id = OBJECT_ID('backwave.job_transitions'))
    CREATE INDEX ix_backwave_job_transitions_position ON backwave.job_transitions (position);

-- One row per registered Observer: its durable cursor, claim Lease, and the subscription filter.
-- cursor_pos: global Position up to and including which every matching row is delivered-or-dead-lettered;
--         -1 means nothing delivered yet (deliver from the first Position).
-- sub_states: comma-joined JobState ints the Observer watches; sub_wire_name / sub_queue: NULL = any.
IF OBJECT_ID('backwave.observers', 'U') IS NULL
    CREATE TABLE backwave.observers (
        observer_id   nvarchar(256) NOT NULL PRIMARY KEY,
        cursor_pos    bigint NOT NULL CONSTRAINT df_backwave_observers_cursor DEFAULT (-1),
        lease_owner   nvarchar(256) NULL,
        lease_expiry  datetimeoffset(7) NULL,
        sub_states    nvarchar(256) NOT NULL CONSTRAINT df_backwave_observers_states DEFAULT (N''),
        sub_wire_name nvarchar(max) NULL,
        sub_queue     nvarchar(max) NULL
    );

-- Per-(Observer, Position) delivery bookkeeping: the at-least-once attempt counter and resolution.
-- A row appears when the Position is first claimed and is swept away once the cursor passes it.
-- resolution: 0 Pending, 1 Delivered, 2 DeadLettered. next_attempt_at holds a Retry's backoff instant:
--         a Pending row whose next_attempt_at is still in the future is the head-of-line block.
IF OBJECT_ID('backwave.observer_deliveries', 'U') IS NULL
    CREATE TABLE backwave.observer_deliveries (
        observer_id      nvarchar(256) NOT NULL,
        position         bigint NOT NULL,
        delivery_attempt int NOT NULL,
        resolution       int NOT NULL CONSTRAINT df_backwave_observer_deliveries_resolution DEFAULT (0),
        next_attempt_at  datetimeoffset(7) NULL,
        PRIMARY KEY (observer_id, position)
    );

-- Dead-lettered deliveries: poison rows that exhausted their ceiling — metadata only, never payload or
-- Failure Detail. Standalone (no FK to job_transitions): the record outlives the Transition and the job
-- it came from, so the Monitor can always explain a missed notification.
IF OBJECT_ID('backwave.observer_dead_letters', 'U') IS NULL
    CREATE TABLE backwave.observer_dead_letters (
        observer_id       nvarchar(256) NOT NULL,
        position          bigint NOT NULL,
        job_id            uniqueidentifier NOT NULL,
        ordinal           bigint NOT NULL,
        state             int NOT NULL,
        attempt           int NOT NULL,
        delivery_attempts int NOT NULL,
        dead_lettered_at  datetimeoffset(7) NOT NULL,
        PRIMARY KEY (observer_id, position)
    );

-- Job Tags: an observational string-set the Core never reads, attached at enqueue and unioned by the
-- fenced outcome write. A child table so filtering, grouping, and multi-value are plain portable SQL.
-- Deleted WITH the job via FK cascade. A Label's key is the empty string '', NEVER NULL, so
-- PRIMARY KEY (job_id, [key], [value]) behaves identically across adapters; uniqueness is PER JOB.
-- [key]/[value] are bracketed (reserved words) and bounded nvarchar(200): the composite PK key
-- (16-byte job_id + two nvarchar(200) = 816 bytes) stays under SQL Server's 900-byte clustered
-- index-key limit. key_lower/value_lower are PERSISTED folds under Latin1_General_BIN2 so the Tag
-- Suggest range walk seeks in byte-ordinal order and case-insensitivity holds on a case-sensitive DB.
IF OBJECT_ID('backwave.job_tags', 'U') IS NULL
    CREATE TABLE backwave.job_tags (
        job_id      uniqueidentifier NOT NULL
                    REFERENCES backwave.jobs (job_id) ON DELETE CASCADE,
        [key]       nvarchar(200) NOT NULL,
        [value]     nvarchar(200) NOT NULL,
        key_lower   AS LOWER([key]) COLLATE Latin1_General_BIN2 PERSISTED,
        value_lower AS LOWER([value]) COLLATE Latin1_General_BIN2 PERSISTED,
        PRIMARY KEY (job_id, [key], [value])
    );

-- The unscoped facet/filter read groups by dimension.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_backwave_job_tags_key_value')
    CREATE INDEX ix_backwave_job_tags_key_value
        ON backwave.job_tags ([key], [value]);

-- Tag Suggest: the typeahead range walk over the folded tokens, seekable because the index sorts in
-- BIN2. EXEC defers compilation so the index over the computed columns resolves within the batch.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_backwave_job_tags_lower_key_value')
    EXEC('CREATE INDEX ix_backwave_job_tags_lower_key_value
        ON backwave.job_tags (key_lower, value_lower)');

-- The Workflows row: identity + config. A Workflow's status is always a PROJECTION of member states,
-- never stored. retention is the policy enum (0 = UnitUntilDrained). restarted_from is the lineage
-- pointer: the WorkflowId a Restart re-instantiated from, or NULL for a fresh creation.
IF OBJECT_ID('backwave.workflows', 'U') IS NULL
    CREATE TABLE backwave.workflows (
        workflow_id    uniqueidentifier NOT NULL PRIMARY KEY,
        name           nvarchar(max) NULL,
        created_at     datetimeoffset(7) NOT NULL,
        retention      int NOT NULL DEFAULT 0,
        restarted_from uniqueidentifier NULL
    );

-- The IMMUTABLE structural edges: parent -> child, recorded once at enqueue and never deleted (unlike
-- job_parents, which the latch cascade resolves away). This keeps the graph view total for the
-- Workflow's whole life. Cascades away when the Workflow row is dropped as its last member is purged.
IF OBJECT_ID('backwave.workflow_edges', 'U') IS NULL
    CREATE TABLE backwave.workflow_edges (
        workflow_id uniqueidentifier NOT NULL
                    REFERENCES backwave.workflows (workflow_id) ON DELETE CASCADE,
        parent_id   uniqueidentifier NOT NULL,
        child_id    uniqueidentifier NOT NULL,
        PRIMARY KEY (workflow_id, parent_id, child_id)
    );

INSERT INTO backwave.schema_version (version)
SELECT 1
WHERE NOT EXISTS (SELECT 1 FROM backwave.schema_version);
