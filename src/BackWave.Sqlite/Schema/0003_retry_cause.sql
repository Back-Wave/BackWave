-- BackWave SQLite schema v3. Runs once: the migrator skips a step the file already carries.
-- v2 -> v3: record why a job went back to Scheduled, so a Retrying job is told apart from a new one.

-- retry_cause: why the job most recently went back to Scheduled after an attempt went wrong.
--   1 HandlerFailed (the handler failed and the retry policy scheduled another attempt),
--   2 LeaseExpired (the lease lapsed before the worker reported an outcome), NULL none.
-- A Scheduled job with a cause is Retrying. A requeue clears it; a clean-stop hand-back of the lease
-- leaves it alone, so a deploy never makes a healthy job look like a failing one.
--
-- SQLite has no ADD COLUMN IF NOT EXISTS, so this step is not safe to run twice; the migrator runs only
-- the steps above the version the file is stamped at. Nullable with no default, so the ADD rewrites no
-- rows, and an N-1 node that neither reads nor writes the column keeps working. Existing rows read NULL:
-- a job already retrying when the file upgrades shows as Retrying from its next failed attempt or
-- expired lease.
ALTER TABLE backwave_jobs ADD COLUMN retry_cause INTEGER NULL;

-- The Retrying listing, in sequence order. Only a Scheduled row with a cause enters the index, so a
-- claim (which moves the row out of Scheduled) and every healthy job leave it untouched.
CREATE INDEX IF NOT EXISTS ix_backwave_jobs_retrying
    ON backwave_jobs (sequence) WHERE state = 0 AND retry_cause IS NOT NULL;

-- v1 seeds the row with an INSERT guarded by WHERE NOT EXISTS, so every later version stamps by UPDATE.
UPDATE backwave_schema_version SET version = 3;
