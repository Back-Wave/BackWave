-- BackWave schema v3 (Postgres dialect). Idempotent: safe to run on every deploy.
-- v2 -> v3: keep what an operator action changed on its audit record.

-- detail: what the action changed, as JSON, or NULL when the action keeps no detail. A Reschedule
--   stores the due time before and after the move: {"from":"<instant>","to":"<instant>"}, each instant
--   in UTC with seven fraction digits and a Z, for example 2026-01-01T01:00:00.0000000Z. Every other
--   action stores NULL.
--
-- Nullable with no default, so the ADD is a catalog-only change that rewrites no rows, and an N-1 node
-- that neither reads nor writes the column keeps working. Existing rows read NULL: no detail.
ALTER TABLE backwave.operator_audit ADD COLUMN IF NOT EXISTS detail text NULL;

-- v1 seeds the row with an INSERT guarded by WHERE NOT EXISTS, so every later version stamps by UPDATE.
UPDATE backwave.schema_version SET version = 3;
