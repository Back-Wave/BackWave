-- BackWave schema v5 (Oracle dialect). Idempotent: safe to run on every deploy.
-- v4 -> v5: stop Observer cursors at the Commit Horizon, so a cursor never passes a transition whose
-- transaction commits late.

DECLARE
    deployed NUMBER;
    compiled NUMBER;

    PROCEDURE ddl(statement IN VARCHAR2) IS
    BEGIN
        FOR attempt IN 1 .. 600 LOOP
            BEGIN
                EXECUTE IMMEDIATE statement;
                RETURN;
            EXCEPTION
                WHEN OTHERS THEN
                    -- -955 name already used, -4081 trigger already exists, -1 the horizon row is
                    -- already seeded.
                    IF SQLCODE IN (-955, -4081, -1) THEN
                        RETURN;
                    -- -54 resource busy, -4021 timeout waiting to lock the table, -14411 concurrent DDL
                    -- on the same object: wait, then try again.
                    ELSIF SQLCODE IN (-54, -4021, -14411) AND attempt < 600 THEN
                        DBMS_SESSION.SLEEP(0.1);
                    ELSE
                        RAISE;
                    END IF;
            END;
        END LOOP;
    END;
BEGIN
    SELECT MAX(version) INTO deployed FROM backwave.schema_version;
    IF deployed >= 5 THEN
        RETURN;
    END IF;

    ddl(q'{CREATE TABLE backwave.observer_log_horizon (
        id            NUMBER(1) NOT NULL PRIMARY KEY CHECK (id = 1),
        epoch         NUMBER(1) NOT NULL,
        candidate_pos NUMBER(19) NULL,
        horizon_pos   NUMBER(19) NOT NULL
    )}');

    ddl(q'{INSERT INTO backwave.observer_log_horizon (id, epoch, candidate_pos, horizon_pos)
        SELECT 1, 0, NULL, 0 FROM dual WHERE NOT EXISTS (SELECT 1 FROM backwave.observer_log_horizon)}');

    ddl(q'{CREATE TABLE backwave.observer_log_epoch_0 (unused NUMBER(1) NULL)}');
    ddl(q'{CREATE TABLE backwave.observer_log_epoch_1 (unused NUMBER(1) NULL)}');

    -- The probe takes an EXCLUSIVE NOWAIT lock in a savepoint it rolls straight back, so it never waits
    -- and never keeps the lock. It runs SAVEPOINT and ROLLBACK TO, so only PL/SQL can call it, not a query.
    ddl(q'{CREATE FUNCTION backwave.observer_log_epoch_free(epoch IN NUMBER) RETURN NUMBER IS
        busy EXCEPTION;
        PRAGMA EXCEPTION_INIT(busy, -54);
    BEGIN
        SAVEPOINT epoch_probe;
        IF epoch = 0 THEN
            LOCK TABLE backwave.observer_log_epoch_0 IN EXCLUSIVE MODE NOWAIT;
        ELSE
            LOCK TABLE backwave.observer_log_epoch_1 IN EXCLUSIVE MODE NOWAIT;
        END IF;
        ROLLBACK TO epoch_probe;
        RETURN 1;
    EXCEPTION
        WHEN busy THEN
            RETURN 0;
    END;}');

    -- A writer registers with a ROW SHARE lock on its epoch table, which a statement trigger takes before
    -- any row of its statement draws a Position, and holds it until its transaction ends.
    ddl(q'{CREATE TRIGGER backwave.observer_log_register
    BEFORE INSERT ON backwave.job_transitions
    DECLARE
        current_epoch NUMBER;
    BEGIN
        BEGIN
            SELECT epoch INTO current_epoch FROM backwave.observer_log_horizon WHERE id = 1;
        EXCEPTION
            WHEN NO_DATA_FOUND THEN
                RETURN;
        END;
        IF current_epoch = 0 THEN
            LOCK TABLE backwave.observer_log_epoch_0 IN ROW SHARE MODE;
        ELSE
            LOCK TABLE backwave.observer_log_epoch_1 IN ROW SHARE MODE;
        END IF;
    END;}');

    -- An invalid trigger would fail every transition insert, so the version must not move past one.
    SELECT COUNT(*) INTO compiled
    FROM all_objects
    WHERE owner = UPPER('backwave')
      AND object_name IN ('OBSERVER_LOG_EPOCH_FREE', 'OBSERVER_LOG_REGISTER')
      AND status = 'VALID';
    IF compiled < 2 THEN
        RAISE_APPLICATION_ERROR(-20290,
            'The Commit Horizon trigger on job_transitions or its probe function is missing or did not compile.');
    END IF;

    -- v1 seeds the row with an INSERT guarded by WHERE NOT EXISTS, so every later version stamps by
    -- UPDATE. The guard keeps a later version's stamp in place.
    EXECUTE IMMEDIATE q'{UPDATE backwave.schema_version SET version = 5 WHERE version < 5}';
END;
