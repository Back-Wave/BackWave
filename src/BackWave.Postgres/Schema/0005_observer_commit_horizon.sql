-- BackWave schema v5 (Postgres dialect). Idempotent: safe to run on every deploy.
-- v4 -> v5: stop Observer cursors at the Commit Horizon, so a cursor never passes a transition whose
-- transaction commits late.

CREATE TABLE IF NOT EXISTS backwave.observer_log_horizon (
    id            smallint PRIMARY KEY CHECK (id = 1),
    epoch         smallint NOT NULL,
    candidate_pos bigint NULL,
    horizon_pos   bigint NOT NULL
);

INSERT INTO backwave.observer_log_horizon (id, epoch, candidate_pos, horizon_pos)
SELECT 1, 0, NULL, 0
WHERE NOT EXISTS (SELECT 1 FROM backwave.observer_log_horizon);

-- A statement trigger fires before the Position default of every row in its statement. The function runs
-- as its owner, so a role that writes transitions, an N-1 node's included, needs no grant on the new table.
-- A SECURITY DEFINER function pins its search_path, so a caller's schema cannot shadow what it calls.
CREATE OR REPLACE FUNCTION backwave.observer_log_register() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
BEGIN
    PERFORM pg_advisory_xact_lock_shared(x'42574F30'::int + h.epoch, hashtext('backwave'))
    FROM backwave.observer_log_horizon h
    WHERE h.id = 1;
    RETURN NULL;
END
$$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger
                   WHERE tgname = 'observer_log_register'
                     AND tgrelid = 'backwave.job_transitions'::regclass) THEN
        CREATE TRIGGER observer_log_register
            BEFORE INSERT ON backwave.job_transitions
            FOR EACH STATEMENT EXECUTE FUNCTION backwave.observer_log_register();
    END IF;
END
$$;

-- It asks for the exclusive lock without waiting, inside a block whose rollback gives the lock straight
-- back.
CREATE OR REPLACE FUNCTION backwave.observer_log_epoch_free(epoch int) RETURNS boolean
LANGUAGE plpgsql VOLATILE AS $$
BEGIN
    BEGIN
        IF NOT pg_try_advisory_xact_lock(x'42574F30'::int + epoch, hashtext('backwave')) THEN
            RETURN false;
        END IF;
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'observer_log_epoch_free releases its probe lock';
    EXCEPTION WHEN raise_exception THEN
        RETURN true;
    END;
END
$$;

-- v1 seeds the row with an INSERT guarded by WHERE NOT EXISTS, so every later version stamps by UPDATE.
UPDATE backwave.schema_version SET version = 5;
