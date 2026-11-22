-- BackWave schema v6 (SQL Server dialect). Idempotent: safe to run on every deploy.
-- v5 -> v6: stop Observer cursors at the Commit Horizon, so a cursor never passes a transition whose
-- transaction commits late.

-- No trigger registers a writer: a statement with OUTPUT and no INTO refuses a table with triggers.
IF OBJECT_ID('backwave.observer_log_horizon', 'U') IS NULL
    CREATE TABLE backwave.observer_log_horizon (
        id            smallint NOT NULL PRIMARY KEY CHECK (id = 1),
        epoch         smallint NOT NULL,
        candidate_pos bigint NULL,
        horizon_pos   bigint NOT NULL
    );

-- UPDLOCK, HOLDLOCK makes the guard and the insert one step, so two uncoordinated migrations cannot
-- both find the table empty and collide on the key.
INSERT INTO backwave.observer_log_horizon (id, epoch, candidate_pos, horizon_pos)
SELECT 1, 0, NULL, 0
WHERE NOT EXISTS (SELECT 1 FROM backwave.observer_log_horizon WITH (UPDLOCK, HOLDLOCK));

-- v1 seeds the row with an INSERT guarded by WHERE NOT EXISTS, so every later version stamps by UPDATE.
UPDATE backwave.schema_version SET version = 6;
