-- BackWave SQLite schema v5. Runs once: the migrator skips a step the file already carries.
-- v4 -> v5: keep the status each Pump's own process last wrote about it, one row per worker id.
CREATE TABLE IF NOT EXISTS backwave_pumps (
    worker_id          TEXT    NOT NULL PRIMARY KEY,
    worker_group       TEXT    NOT NULL,
    state              INTEGER NOT NULL,
    fault_type         TEXT    NULL,
    invariant_trigger  TEXT    NULL,
    reported_at        INTEGER NOT NULL,
    report_interval_ms INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_backwave_pumps_reported_at
    ON backwave_pumps (reported_at);

-- v1 seeds the row with an INSERT guarded by WHERE NOT EXISTS, so every later version stamps by UPDATE.
UPDATE backwave_schema_version SET version = 5;
