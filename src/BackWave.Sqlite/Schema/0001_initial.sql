-- BackWave SQLite schema v1 — the first Embedded Adapter. CONSOLIDATED: this single script
-- folds the Networked Adapters' incremental migrations (their v1–v8) into one canonical
-- artifact pinned at schema version 1. Idempotent: safe to run on every deploy. There is no
-- CREATE SCHEMA — SQLite has one object namespace per file, so every object is prefixed
-- backwave_ instead of living under a `backwave` schema.
--
-- Dialect mapping vs Postgres (ADR 0019):
--   uuid        -> TEXT     (Guid in canonical "D" form, lowercase)
--   bytea       -> BLOB
--   timestamptz -> INTEGER  (UTC ticks; a monotonic instant, so range/order predicates work
--                            directly and chronological order is preserved — see SqliteValueCodec)
--   boolean     -> INTEGER  (0/1)
--   identity    -> INTEGER PRIMARY KEY AUTOINCREMENT
-- WAL (journal_mode) and foreign_keys are connection pragmas, set by the migrator / connection
-- string rather than DDL, so they are not part of this script.

CREATE TABLE IF NOT EXISTS backwave_schema_version (
    version INTEGER NOT NULL
);

-- States: 0 Scheduled, 1 AwaitingParent, 2 Leased, 3 Succeeded, 4 Cancelled,
--         5 DeadLettered, 6 Quarantined (Storage Contract §3).
-- `sequence` is the AUTOINCREMENT surrogate — the within-Queue claim tiebreak and the §5.9
-- pagination cursor. `job_id` is the logical key, UNIQUE. This INVERTS Postgres (where job_id is
-- the PK and sequence a plain identity column): SQLite's AUTOINCREMENT only works on the single
-- INTEGER PRIMARY KEY, so the monotonic surrogate must BE that primary key.
CREATE TABLE IF NOT EXISTS backwave_jobs (
    sequence          INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id            TEXT NOT NULL UNIQUE,
    wire_name         TEXT NOT NULL,
    payload           BLOB NOT NULL,
    trace_context     TEXT NULL,
    queue             TEXT NOT NULL,
    state             INTEGER NOT NULL,
    due_time          INTEGER NOT NULL,
    attempt           INTEGER NOT NULL DEFAULT 0,
    lease_owner       TEXT NULL,
    lease_expiry      INTEGER NULL,
    cancel_requested  INTEGER NOT NULL DEFAULT 0,
    terminal_at       INTEGER NULL,
    terminal_cause    TEXT NULL,
    schedule_id       TEXT NULL,
    parents_remaining INTEGER NOT NULL DEFAULT 0,
    mode              INTEGER NOT NULL DEFAULT 0,
    -- ADR 0023: the Workflow this job is a member of, or NULL. Set once at workflow enqueue.
    workflow_id       TEXT NULL,
    -- ADR 0026: the opaque Job Output blob, written on the Succeeded transition. NULL otherwise.
    -- Kept off every hot read (never in JobColumns); surfaced only by GetJobOutput.
    output            BLOB NULL
);

-- The claim path (§5.2): due Scheduled jobs per queue, oldest due first. Partial index — only
-- Scheduled rows, mirroring Postgres's WHERE state = 0.
CREATE INDEX IF NOT EXISTS ix_backwave_jobs_claim
    ON backwave_jobs (queue, due_time, sequence) WHERE state = 0;

-- The expiry sweep (§5.5) and the per-claim I3 slot count: live Leases keyed by queue then expiry.
CREATE INDEX IF NOT EXISTS ix_backwave_jobs_leased_queue
    ON backwave_jobs (queue, lease_expiry) WHERE state = 2;

CREATE INDEX IF NOT EXISTS ix_backwave_jobs_schedule
    ON backwave_jobs (schedule_id) WHERE schedule_id IS NOT NULL;

-- The retention sweep (§5.11): terminal jobs by class and terminal instant; must never scan live.
CREATE INDEX IF NOT EXISTS ix_backwave_jobs_terminal
    ON backwave_jobs (state, terminal_at, sequence) WHERE terminal_at IS NOT NULL;

-- The per-Workflow member scan (ADR 0023): graph read, status projection, drain check.
CREATE INDEX IF NOT EXISTS ix_backwave_jobs_workflow
    ON backwave_jobs (workflow_id) WHERE workflow_id IS NOT NULL;

-- Continuation latch edges (§5.6, invariant I2): each row resolves exactly once.
CREATE TABLE IF NOT EXISTS backwave_job_parents (
    parent_id TEXT NOT NULL REFERENCES backwave_jobs (job_id),
    child_id  TEXT NOT NULL REFERENCES backwave_jobs (job_id),
    PRIMARY KEY (parent_id, child_id)
);

CREATE INDEX IF NOT EXISTS ix_backwave_job_parents_child
    ON backwave_job_parents (child_id);

CREATE TABLE IF NOT EXISTS backwave_schedules (
    schedule_id   TEXT PRIMARY KEY,
    cron          TEXT NOT NULL,
    wire_name     TEXT NOT NULL,
    payload       BLOB NOT NULL,
    queue         TEXT NOT NULL,
    cursor        INTEGER NOT NULL,
    time_zone_id  TEXT NULL,
    catch_up      INTEGER NOT NULL DEFAULT 0,
    no_overlap    INTEGER NOT NULL DEFAULT 0,
    skipped_ticks TEXT NOT NULL DEFAULT '[]'
);

CREATE TABLE IF NOT EXISTS backwave_queue_limits (
    queue          TEXT PRIMARY KEY,
    max_concurrent INTEGER NULL,
    paused         INTEGER NOT NULL DEFAULT 0
);

-- Operator audit (§5.8): one row per Operator Action, appended atomically with its effect.
-- action: 0 Cancel, 1 Requeue, 2 TriggerScheduleNow, 3 PauseQueue, 4 ResumeQueue.
CREATE TABLE IF NOT EXISTS backwave_operator_audit (
    sequence    INTEGER PRIMARY KEY AUTOINCREMENT,
    actor       TEXT NOT NULL,
    action      INTEGER NOT NULL,
    target      TEXT NOT NULL,
    recorded_at INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_backwave_operator_audit_target
    ON backwave_operator_audit (target, sequence);

-- The Transition Log (§5.12): append-only per-job history. `position` is the single global,
-- monotonic order the Observer walk (§5.13) cursors over — Postgres carries it on a SEQUENCE
-- DEFAULT, but SQLite has no sequences, so the adapter assigns position = MAX(position)+1 inside
-- the write. Whole-writer serialization (ADR 0019) makes that read-then-write race-free: there is
-- only ever one writer holding the database write lock.
CREATE TABLE IF NOT EXISTS backwave_job_transitions (
    job_id         TEXT NOT NULL REFERENCES backwave_jobs (job_id) ON DELETE CASCADE,
    ordinal        INTEGER NOT NULL,
    recorded_at    INTEGER NOT NULL,
    state          INTEGER NOT NULL,
    attempt        INTEGER NOT NULL,
    failure_detail TEXT NULL,
    position       INTEGER NOT NULL,
    PRIMARY KEY (job_id, ordinal)
);

CREATE INDEX IF NOT EXISTS ix_backwave_job_transitions_position
    ON backwave_job_transitions (position);

-- Observer registrations, per-(observer, position) delivery bookkeeping, and dead letters
-- (§5.13, ADR 0017). cursor_pos -1 means nothing delivered yet.
CREATE TABLE IF NOT EXISTS backwave_observers (
    observer_id   TEXT PRIMARY KEY,
    cursor_pos    INTEGER NOT NULL DEFAULT -1,
    lease_owner   TEXT NULL,
    lease_expiry  INTEGER NULL,
    sub_states    TEXT NOT NULL DEFAULT '',
    sub_wire_name TEXT NULL,
    sub_queue     TEXT NULL
);

CREATE TABLE IF NOT EXISTS backwave_observer_deliveries (
    observer_id      TEXT NOT NULL,
    position         INTEGER NOT NULL,
    delivery_attempt INTEGER NOT NULL,
    resolution       INTEGER NOT NULL DEFAULT 0,
    next_attempt_at  INTEGER NULL,
    PRIMARY KEY (observer_id, position)
);

CREATE TABLE IF NOT EXISTS backwave_observer_dead_letters (
    observer_id       TEXT NOT NULL,
    position          INTEGER NOT NULL,
    job_id            TEXT NOT NULL,
    ordinal           INTEGER NOT NULL,
    state             INTEGER NOT NULL,
    attempt           INTEGER NOT NULL,
    delivery_attempts INTEGER NOT NULL,
    dead_lettered_at  INTEGER NOT NULL,
    PRIMARY KEY (observer_id, position)
);

-- Job Tags (ADR 0022): observational string-set, child table (never JSON/array). The empty-string
-- sentinel key '' marks a Label; uniqueness is PER JOB.
CREATE TABLE IF NOT EXISTS backwave_job_tags (
    job_id TEXT NOT NULL REFERENCES backwave_jobs (job_id) ON DELETE CASCADE,
    key    TEXT NOT NULL,
    value  TEXT NOT NULL,
    PRIMARY KEY (job_id, key, value)
);

CREATE INDEX IF NOT EXISTS ix_backwave_job_tags_key_value
    ON backwave_job_tags (key, value);

-- Tag Suggest (ADR 0042): the prefix range scan folds ASCII case with the built-in lower(). SQLite
-- needs no persisted fold column — an EXPRESSION index over (lower(key), lower(value)) serves the
-- prefix scan directly. The default BINARY collation is already byte-ordinal, so the ORDER BY
-- tiebreak matches the reference store with no COLLATE clause.
CREATE INDEX IF NOT EXISTS ix_backwave_job_tags_lower_key_value
    ON backwave_job_tags (lower(key), lower(value));

-- Workflows (ADR 0023): identity + config only; status is always a PROJECTION of member states.
-- workflow_edges are the IMMUTABLE structural edges that keep the graph view total for life.
CREATE TABLE IF NOT EXISTS backwave_workflows (
    workflow_id    TEXT PRIMARY KEY,
    name           TEXT NULL,
    created_at     INTEGER NOT NULL,
    retention      INTEGER NOT NULL DEFAULT 0,
    restarted_from TEXT NULL
);

CREATE TABLE IF NOT EXISTS backwave_workflow_edges (
    workflow_id TEXT NOT NULL REFERENCES backwave_workflows (workflow_id) ON DELETE CASCADE,
    parent_id   TEXT NOT NULL,
    child_id    TEXT NOT NULL,
    PRIMARY KEY (workflow_id, parent_id, child_id)
);

INSERT INTO backwave_schema_version (version)
SELECT 1
WHERE NOT EXISTS (SELECT 1 FROM backwave_schema_version);
