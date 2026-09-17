-- BackWave SQLite schema v2. Idempotent: safe to run on every deploy.
-- v1 -> v2: carry the Transition Log position on a high-water mark of its own, so it never regresses.

-- `position` is the single global, monotonic order the Observer walk cursors over. Under v1 the
-- adapter derived it as MAX(position)+1 over the Transition Log itself, and that only held while the
-- log kept every row it had ever written. Retention purges terminal jobs, the purge cascades their
-- transitions, and the moment the newest rows go the MAX drops with them: the next write hands out a
-- position an Observer cursor has already passed, and that transition is never delivered. This table
-- is the sequence SQLite does not have: one row, read and bumped inside the same write transaction as
-- the transition it numbers, and never lowered by anything the log does.
CREATE TABLE IF NOT EXISTS backwave_transition_position (
    position INTEGER NOT NULL
);

-- Seeded at the log's current high-water mark, so the first v2 write lands strictly after every row a
-- v1 node wrote. Guarded by WHERE NOT EXISTS: a re-run must never wind the mark back to the MAX.
INSERT INTO backwave_transition_position (position)
SELECT COALESCE(MAX(position), 0) FROM backwave_job_transitions
WHERE NOT EXISTS (SELECT 1 FROM backwave_transition_position);

-- v1 seeds the row with an INSERT guarded by WHERE NOT EXISTS, so every later version stamps by UPDATE.
UPDATE backwave_schema_version SET version = 2;
