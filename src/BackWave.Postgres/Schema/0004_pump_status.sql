-- BackWave schema v4 (Postgres dialect). Idempotent: safe to run on every deploy.
-- v3 -> v4: keep what each Pump's own process last wrote about it, so an operator can see every Pump.

-- One row per Pump, written only by the process that minted its worker id. Observational only: no
-- claim, report or sweep reads these rows.
-- state: the PumpState int (0 Running, 1 Degraded, 2 Halted, 3 Stopping, 4 Stopped). A reader maps a
--   value it does not define to Unknown and still lists the row.
-- fault_type: the full exception type name behind a halt or a degrade, never its message; NULL otherwise.
--   A Pump that halted and then stopped with its process keeps its fault type in the Stopped row.
-- invariant_trigger: the InvariantTrigger name for a halt a named check raised; NULL otherwise. Kept in
--   the Stopped row like fault_type.
-- report_interval_ms: how often the process writes the row, so a reader judges it stale by the
--   writer's own interval.
--
-- A new table, so an N-1 node that neither reads nor writes it keeps working.
CREATE TABLE IF NOT EXISTS backwave.pumps (
    worker_id          text PRIMARY KEY,
    worker_group       text NOT NULL,
    state              int NOT NULL,
    fault_type         text NULL,
    invariant_trigger  text NULL,
    reported_at        timestamptz NOT NULL,
    report_interval_ms int NOT NULL
);

-- Every write reaps the oldest rows past a cutoff, so the reap seeks instead of scanning every Pump.
CREATE INDEX IF NOT EXISTS ix_backwave_pumps_reported_at ON backwave.pumps (reported_at);

-- v1 seeds the row with an INSERT guarded by WHERE NOT EXISTS, so every later version stamps by UPDATE.
UPDATE backwave.schema_version SET version = 4;
