-- BackWave schema v4 (Oracle dialect). Idempotent: safe to run on every deploy.
-- v3 -> v4: keep the status each Pump's own process last wrote about it.
--
-- One row per Pump, keyed by its worker id. Only the process that minted the id writes the row, and
-- every writer deletes rows not written for an hour. Observational: no store decision reads it.
--   state:              the PumpState value, kept as written, so a value a newer version defines still reads.
--   fault_type:         the full exception type name behind a halt or degrade, or NULL. A Pump that
--                       halted and then stopped with its process keeps it in the Stopped row.
--   invariant_trigger:  the InvariantTrigger name behind a halt a named check raised, or NULL. Kept in
--                       the Stopped row like fault_type.
--   reported_at:        the instant the process wrote the row.
--   report_interval_ms: how often the process writes the row while it runs.
--
-- A new table only, so an N-1 node that never reads or writes it keeps working.
--
-- One anonymous PL/SQL block, shaped like v3's:
--   * It returns at once when the schema is already at v4 or later, so an N-1 node that boots after the
--     upgrade never stamps the version back down.
--   * ddl() swallows ORA-00955, so a node that lost the race to create the table or the index no-ops,
--     and retries ORA-00054 and ORA-14411 (another session runs DDL on the same object).

DECLARE
    deployed NUMBER;

    -- Runs one DDL statement and ignores the "already exists" family, so a re-run converges.
    PROCEDURE ddl(statement IN VARCHAR2) IS
    BEGIN
        FOR attempt IN 1 .. 600 LOOP
            BEGIN
                EXECUTE IMMEDIATE statement;
                RETURN;
            EXCEPTION
                WHEN OTHERS THEN
                    -- -955 name already used.
                    IF SQLCODE = -955 THEN
                        RETURN;
                    -- -54 resource busy, -14411 concurrent DDL on the same object: wait, then try again.
                    ELSIF SQLCODE IN (-54, -14411) AND attempt < 600 THEN
                        DBMS_SESSION.SLEEP(0.1);
                    ELSE
                        RAISE;
                    END IF;
            END;
        END LOOP;
    END;
BEGIN
    SELECT MAX(version) INTO deployed FROM backwave.schema_version;
    IF deployed >= 4 THEN
        RETURN;
    END IF;

    ddl(q'{CREATE TABLE backwave.pumps (
        worker_id          VARCHAR2(450 CHAR) NOT NULL PRIMARY KEY,
        worker_group       VARCHAR2(450 CHAR) NOT NULL,
        state              NUMBER(10) NOT NULL,
        fault_type         VARCHAR2(450 CHAR) NULL,
        invariant_trigger  VARCHAR2(128 CHAR) NULL,
        reported_at        TIMESTAMP(7) WITH TIME ZONE NOT NULL,
        report_interval_ms NUMBER(10) NOT NULL
    )}');

    -- Serves the reap: rows written before the horizon, oldest first.
    ddl(q'{CREATE INDEX backwave.ix_bw_pumps_reported_at ON backwave.pumps (reported_at)}');

    -- v1 seeds the row with an INSERT guarded by WHERE NOT EXISTS, so every later version stamps by
    -- UPDATE. The guard keeps a later version's stamp in place.
    EXECUTE IMMEDIATE q'{UPDATE backwave.schema_version SET version = 4 WHERE version < 4}';
END;
