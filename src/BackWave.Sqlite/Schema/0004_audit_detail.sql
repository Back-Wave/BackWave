-- BackWave SQLite schema v4. Runs once: the migrator skips a step the file already carries.
-- v3 -> v4: keep what an operator action changed on its audit record.

-- detail: what the action changed, as JSON, or NULL when the action keeps no detail. A Reschedule
--   stores the due time before and after the move: {"from":"<instant>","to":"<instant>"}, each instant
--   in UTC with seven fraction digits and a Z, for example 2026-01-01T01:00:00.0000000Z. Every other
--   action stores NULL.
--
-- SQLite has no ADD COLUMN IF NOT EXISTS, so this step is not safe to run twice; the migrator runs only
-- the steps above the version the file is stamped at. Nullable with no default, so the ADD rewrites no
-- rows, and an N-1 node that neither reads nor writes the column keeps working. Existing rows read NULL:
-- no detail.
ALTER TABLE backwave_operator_audit ADD COLUMN detail TEXT NULL;

-- v1 seeds the row with an INSERT guarded by WHERE NOT EXISTS, so every later version stamps by UPDATE.
UPDATE backwave_schema_version SET version = 4;
