-- BackWave schema v1 (Postgres dialect). Idempotent: safe to run on every deploy.
-- This file is the canonical schema artifact — a versioned, DBA-reviewable script;
-- auto-migrate executes exactly this script, nothing else.

CREATE SCHEMA IF NOT EXISTS backwave;

-- A global, monotonic Position over every recorded Transition — the order the Observer cursors walk
-- (the per-job `ordinal` only orders within one job). job_transitions.position carries its DEFAULT, so
-- recording a Transition needs no awareness of Observers. Declared first so that column default resolves.
CREATE SEQUENCE IF NOT EXISTS backwave.observer_log_position AS bigint;

CREATE TABLE IF NOT EXISTS backwave.schema_version (
    version int NOT NULL
);

-- States: 0 Scheduled, 1 AwaitingParent, 2 Leased, 3 Succeeded, 4 Cancelled,
--         5 DeadLettered, 6 Quarantined.
-- workflow_id: the membership grouping key, set once at workflow enqueue, <=1 per job, never rewritten;
--         NULL for a non-workflow job. Above the determinism boundary — the Core never reads it.
-- output: the opaque blob a handler emits via SetOutput on its Succeeded Attempt (functional data a
--         Dependency descendant pulls, NOT diagnostics); NULL when a job never set output. Kept off the
--         listing/claim path (never in JobColumns) so a large blob never rides a hot read.
CREATE TABLE IF NOT EXISTS backwave.jobs (
    job_id            uuid PRIMARY KEY,
    wire_name         text NOT NULL,
    payload           bytea NOT NULL,
    trace_context     text NULL,
    queue             text NOT NULL,
    state             int NOT NULL,
    due_time          timestamptz NOT NULL,
    attempt           int NOT NULL DEFAULT 0,
    lease_owner       text NULL,
    lease_expiry      timestamptz NULL,
    cancel_requested  boolean NOT NULL DEFAULT false,
    terminal_at       timestamptz NULL,
    terminal_cause    text NULL,
    schedule_id       text NULL,
    parents_remaining int NOT NULL DEFAULT 0,
    mode              int NOT NULL DEFAULT 0,
    sequence          bigint GENERATED ALWAYS AS IDENTITY,
    workflow_id       uuid NULL,
    output            bytea NULL
);

-- The claim path: due Scheduled jobs per queue, oldest due first.
CREATE INDEX IF NOT EXISTS ix_backwave_jobs_claim
    ON backwave.jobs (queue, due_time, sequence) WHERE state = 0;

-- The expiry sweep: live Leases per queue by expiry — also the I3 slot count per queue, so keying
-- (queue, lease_expiry) lets that count seek instead of scanning every live Lease cluster-wide.
CREATE INDEX IF NOT EXISTS ix_backwave_jobs_leased_queue
    ON backwave.jobs (queue, lease_expiry) WHERE state = 2;

CREATE INDEX IF NOT EXISTS ix_backwave_jobs_schedule
    ON backwave.jobs (schedule_id) WHERE schedule_id IS NOT NULL;

-- The retention sweep: terminal jobs by class and terminal instant. Swept every poll, so it must
-- never scan the live table.
CREATE INDEX IF NOT EXISTS ix_backwave_jobs_terminal
    ON backwave.jobs (state, terminal_at, sequence) WHERE terminal_at IS NOT NULL;

-- Monitor list pagination orders by the sequence key, so pages seek instead of sorting the whole table.
CREATE INDEX IF NOT EXISTS ix_backwave_jobs_sequence
    ON backwave.jobs (sequence);

-- The per-Workflow member scan (graph read, status projection, drain check).
CREATE INDEX IF NOT EXISTS ix_backwave_jobs_workflow
    ON backwave.jobs (workflow_id) WHERE workflow_id IS NOT NULL;

-- The Continuation latch edges: each row resolves exactly once (invariant I2).
CREATE TABLE IF NOT EXISTS backwave.job_parents (
    parent_id uuid NOT NULL REFERENCES backwave.jobs (job_id),
    child_id  uuid NOT NULL REFERENCES backwave.jobs (job_id),
    PRIMARY KEY (parent_id, child_id)
);

CREATE INDEX IF NOT EXISTS ix_backwave_job_parents_child
    ON backwave.job_parents (child_id);

CREATE TABLE IF NOT EXISTS backwave.schedules (
    schedule_id   text PRIMARY KEY,
    cron          text NOT NULL,
    wire_name     text NOT NULL,
    payload       bytea NOT NULL,
    queue         text NOT NULL,
    cursor        timestamptz NOT NULL,
    time_zone_id  text NULL,
    catch_up      int NOT NULL DEFAULT 0,
    no_overlap    boolean NOT NULL DEFAULT false,
    skipped_ticks jsonb NOT NULL DEFAULT '[]'
);

-- A Paused Queue yields nothing to Claim. The flag lives on queue_limits so the claim path reads
-- limit and pause state in the one row it already locks per queue.
CREATE TABLE IF NOT EXISTS backwave.queue_limits (
    queue          text PRIMARY KEY,
    max_concurrent int NULL,
    paused         boolean NOT NULL DEFAULT false
);

-- Every Operator Action appends one record atomically with its effect. Append-only.
-- action: 0 Cancel, 1 Requeue, 2 TriggerScheduleNow, 3 PauseQueue, 4 ResumeQueue.
CREATE TABLE IF NOT EXISTS backwave.operator_audit (
    sequence    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    actor       text NOT NULL,
    action      int NOT NULL,
    target      text NOT NULL,
    recorded_at timestamptz NOT NULL
);

-- Audit reads are by target, oldest first.
CREATE INDEX IF NOT EXISTS ix_backwave_operator_audit_target
    ON backwave.operator_audit (target, sequence);

-- The Transition Log: an append-only, per-job history of state changes the Monitor surfaces as a
-- timeline. Every state-changing op appends one row in the SAME transaction as the change. Bounded per
-- job life (oldest dropped past the cap). Deleted WITH the job via FK cascade.
-- ordinal: per-job sequence, 0-based, oldest first; climbs even as old rows age out past the cap.
-- failure_detail: opaque diagnostics of a failed Attempt.
-- position: the global walk order; auto-fills from the observer_log_position sequence, so History Policy
--         still gates whether a row is written at all (Off yields nothing to walk).
CREATE TABLE IF NOT EXISTS backwave.job_transitions (
    job_id         uuid NOT NULL REFERENCES backwave.jobs (job_id) ON DELETE CASCADE,
    ordinal        bigint NOT NULL,
    recorded_at    timestamptz NOT NULL,
    state          int NOT NULL,
    attempt        int NOT NULL,
    failure_detail text NULL,
    position       bigint NOT NULL DEFAULT nextval('backwave.observer_log_position'),
    PRIMARY KEY (job_id, ordinal)
);

-- The claim/report walk scans `position > cursor` across all jobs, in Position order.
CREATE INDEX IF NOT EXISTS ix_backwave_job_transitions_position
    ON backwave.job_transitions (position);

-- One row per registered Observer: its durable cursor, claim Lease, and the subscription filter.
-- cursor_pos: global Position up to and including which every matching row is delivered-or-dead-lettered;
--         -1 means nothing delivered yet (deliver from the first Position).
-- sub_states: comma-joined JobState ints the Observer watches; sub_wire_name / sub_queue: NULL = any.
CREATE TABLE IF NOT EXISTS backwave.observers (
    observer_id   text PRIMARY KEY,
    cursor_pos    bigint NOT NULL DEFAULT -1,
    lease_owner   text NULL,
    lease_expiry  timestamptz NULL,
    sub_states    text NOT NULL DEFAULT '',
    sub_wire_name text NULL,
    sub_queue     text NULL
);

-- Per-(Observer, Position) delivery bookkeeping: the at-least-once attempt counter and resolution.
-- A row appears when the Position is first claimed and is swept away once the cursor passes it.
-- resolution: 0 Pending, 1 Delivered, 2 DeadLettered. next_attempt_at holds a Retry's backoff instant:
--         a Pending row whose next_attempt_at is still in the future is the head-of-line block.
CREATE TABLE IF NOT EXISTS backwave.observer_deliveries (
    observer_id      text NOT NULL,
    position         bigint NOT NULL,
    delivery_attempt int NOT NULL,
    resolution       int NOT NULL DEFAULT 0,
    next_attempt_at  timestamptz NULL,
    PRIMARY KEY (observer_id, position)
);

-- Dead-lettered deliveries: poison rows that exhausted their ceiling — metadata only, never payload or
-- Failure Detail. Standalone (no FK to job_transitions): the record outlives the Transition and the job
-- it came from, so the Monitor can always explain a missed notification.
CREATE TABLE IF NOT EXISTS backwave.observer_dead_letters (
    observer_id      text NOT NULL,
    position         bigint NOT NULL,
    job_id           uuid NOT NULL,
    ordinal          bigint NOT NULL,
    state            int NOT NULL,
    attempt          int NOT NULL,
    delivery_attempts int NOT NULL,
    dead_lettered_at timestamptz NOT NULL,
    PRIMARY KEY (observer_id, position)
);

-- Job Tags: an observational string-set the Core never reads, attached at enqueue and unioned by the
-- fenced outcome write. A child table so filtering, grouping, and multi-value are plain portable SQL.
-- Deleted WITH the job via FK cascade. A Label's key is the empty string '', NEVER NULL, so
-- PRIMARY KEY (job_id, key, value) behaves identically across adapters; uniqueness is PER JOB.
CREATE TABLE IF NOT EXISTS backwave.job_tags (
    job_id uuid NOT NULL REFERENCES backwave.jobs (job_id) ON DELETE CASCADE,
    key    text NOT NULL,
    value  text NOT NULL,
    PRIMARY KEY (job_id, key, value)
);

-- The unscoped facet/filter read groups by dimension.
CREATE INDEX IF NOT EXISTS ix_backwave_job_tags_key_value
    ON backwave.job_tags (key, value);

-- Tag Suggest: case-insensitive prefix completion. An expression index on the ASCII-folded (key, value)
-- under the "C" collation serves the suggest's `lower(...) COLLATE "C" LIKE 'prefix%'` range scans in
-- byte-ordinal order.
CREATE INDEX IF NOT EXISTS ix_backwave_job_tags_lower_key_value
    ON backwave.job_tags (lower(key) COLLATE "C", lower(value) COLLATE "C");

-- The Workflows row: identity + config. A Workflow's status is always a PROJECTION of member states,
-- never stored. retention is the policy enum (0 = UnitUntilDrained). restarted_from is the lineage
-- pointer: the WorkflowId a Restart re-instantiated from, or NULL for a fresh creation.
CREATE TABLE IF NOT EXISTS backwave.workflows (
    workflow_id    uuid PRIMARY KEY,
    name           text NULL,
    created_at     timestamptz NOT NULL,
    retention      int NOT NULL DEFAULT 0,
    restarted_from uuid NULL
);

-- The IMMUTABLE structural edges: parent -> child, recorded once at enqueue and never deleted (unlike
-- job_parents, which the latch cascade resolves away). This keeps the graph view total for the
-- Workflow's whole life. Cascades away when the Workflow row is dropped as its last member is purged.
CREATE TABLE IF NOT EXISTS backwave.workflow_edges (
    workflow_id uuid NOT NULL REFERENCES backwave.workflows (workflow_id) ON DELETE CASCADE,
    parent_id   uuid NOT NULL,
    child_id    uuid NOT NULL,
    PRIMARY KEY (workflow_id, parent_id, child_id)
);

INSERT INTO backwave.schema_version (version)
SELECT 1
WHERE NOT EXISTS (SELECT 1 FROM backwave.schema_version);
