-- BackWave schema v2 (Oracle dialect). Idempotent: safe to run on every deploy.
-- v1 -> v2: record why a job went back to Scheduled, so a Retrying job is told apart from a new one.
--
-- retry_cause: why the job most recently went back to Scheduled after an attempt went wrong.
--   1 HandlerFailed (the handler failed and the retry policy scheduled another attempt),
--   2 LeaseExpired (the lease lapsed before the worker reported an outcome), NULL none.
-- A Scheduled job with a cause is Retrying. A requeue clears it; a clean-stop hand-back of the lease
-- leaves it alone, so a deploy never makes a healthy job look like a failing one.
--
-- Nullable with no default, so the ADD is a dictionary-only change that rewrites no rows, and an N-1
-- node that neither reads nor writes the column keeps working. Existing rows read NULL: a job already
-- retrying when the fleet upgrades shows as Retrying from its next failed attempt or expired lease.
--
-- One anonymous PL/SQL block. Unlike v1 it alters a table that live workers write to, so it differs in
-- two ways:
--   * It returns at once when the schema is already at v2 or later. A boot against a current schema
--     then takes no DDL lock on the jobs table, and an N-1 node that boots after the upgrade never
--     stamps the version back down.
--   * ddl() also retries ORA-00054 and ORA-14411 (another session runs DDL on the same table). The
--     migrator session already waits for the table lock, so the ALTER queues behind the in-flight claims
--     and outcome reports of a running fleet. When a fleet cold-boots, other nodes can still be creating
--     v1's indexes on the jobs table. After the winner commits, every other node gets "already exists"
--     and no-ops.

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
                    -- -955 name already used, -1430 column exists, -1408 index column list already indexed.
                    IF SQLCODE IN (-955, -1430, -1408) THEN
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
    IF deployed >= 2 THEN
        RETURN;
    END IF;

    ddl(q'{ALTER TABLE backwave.jobs ADD (retry_cause NUMBER(10) NULL)}');

    -- The Retrying listing. A single-column index omits NULL keys, so only a row with a cause enters it,
    -- and a claim (which changes state, not retry_cause) never touches it.
    ddl(q'{CREATE INDEX backwave.ix_bw_jobs_retrying ON backwave.jobs (retry_cause)}');

    -- v1 seeds the row with an INSERT guarded by WHERE NOT EXISTS, so every later version stamps by
    -- UPDATE. The guard keeps a later version's stamp in place.
    EXECUTE IMMEDIATE q'{UPDATE backwave.schema_version SET version = 2 WHERE version < 2}';
END;
