-- BackWave schema v5 (SQL Server dialect). Idempotent: safe to run on every deploy.
-- v4 -> v5: keep the last status each Pump's own process wrote, so any node can list every Pump.

-- One row per Pump, keyed by its worker id and written only by the process that minted that id.
-- Observational only: no Core decision reads this table.
-- worker_id: the same worker id a leased job's lease_owner carries, so the same width.
-- state: 0 Running, 1 Degraded, 2 Halted, 3 Stopping, 4 Stopped. A reader that does not define a stored
--   value reads it as Unknown.
-- fault_type: the full exception type name behind a halt or a degrade, never its message; NULL otherwise.
--   A Pump that halted and then stopped with its process keeps its fault type in the Stopped row.
-- invariant_trigger: the name of the invariant a named check found broken; NULL otherwise. Kept in the
--   Stopped row like fault_type.
-- reported_at: the instant the Pump's process wrote the row, on that process's clock.
-- report_interval_ms: how often that process writes the row; a reader judges staleness against it.
--
-- A new table, so an N-1 node that never reads or writes it keeps working.
IF OBJECT_ID('backwave.pumps', 'U') IS NULL
    CREATE TABLE backwave.pumps (
        worker_id          nvarchar(450) NOT NULL PRIMARY KEY,
        worker_group       nvarchar(450) NOT NULL,
        state              int NOT NULL,
        fault_type         nvarchar(450) NULL,
        invariant_trigger  nvarchar(128) NULL,
        reported_at        datetimeoffset(7) NOT NULL,
        report_interval_ms int NOT NULL
    );

-- Every write reaps the oldest rows past a cutoff, a bounded number at a time. Keying reported_at
-- finds them with a seek instead of a scan of every row.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'ix_backwave_pumps_reported_at'
                 AND object_id = OBJECT_ID('backwave.pumps'))
    CREATE INDEX ix_backwave_pumps_reported_at ON backwave.pumps (reported_at);

-- v1 seeds the row with an INSERT guarded by WHERE NOT EXISTS, so every later version stamps by UPDATE.
UPDATE backwave.schema_version SET version = 5;
