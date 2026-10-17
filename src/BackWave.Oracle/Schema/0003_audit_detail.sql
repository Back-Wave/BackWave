-- BackWave schema v3 (Oracle dialect). Idempotent: safe to run on every deploy.
-- v2 -> v3: keep what an operator action changed on its audit record.
--
-- detail: what the action changed, as JSON, or NULL when the action keeps no detail. A Reschedule
--   stores the due time before and after the move: {"from":"<instant>","to":"<instant>"}, each instant
--   in UTC with seven fraction digits and a Z, for example 2026-01-01T01:00:00.0000000Z. Every other
--   action stores NULL. A VARCHAR2 and not a CLOB, so an audit read costs no LOB round trip.
--
-- Nullable with no default, so the ADD is a dictionary-only change that rewrites no rows, and an N-1
-- node that neither reads nor writes the column keeps working. Existing rows read NULL: no detail.
--
-- One anonymous PL/SQL block, shaped like v2's:
--   * It returns at once when the schema is already at v3 or later. A boot against a current schema
--     then takes no DDL lock on the audit table, and an N-1 node that boots after the upgrade never
--     stamps the version back down.
--   * ddl() also retries ORA-00054 and ORA-14411 (another session runs DDL on the same table). The
--     migrator session already waits for the table lock, so the ALTER queues behind the operator actions
--     of a running fleet. After the winner commits, every other node gets "column exists" and no-ops.

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
                    -- -1430 column exists.
                    IF SQLCODE = -1430 THEN
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
    IF deployed >= 3 THEN
        RETURN;
    END IF;

    ddl(q'{ALTER TABLE backwave.operator_audit ADD (detail VARCHAR2(450 CHAR) NULL)}');

    -- v1 seeds the row with an INSERT guarded by WHERE NOT EXISTS, so every later version stamps by
    -- UPDATE. The guard keeps a later version's stamp in place.
    EXECUTE IMMEDIATE q'{UPDATE backwave.schema_version SET version = 3 WHERE version < 3}';
END;
