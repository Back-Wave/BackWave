-- BackWave schema v2 (SQL Server dialect). Idempotent: safe to run on every deploy.
-- v1 -> v2: give the clean-stop hand-back a lease_owner index, so it locks only its own rows.

-- The hand-back (RelinquishLeasesAsync) fences on the lease itself: the rows this worker still holds,
-- still Leased. Under v1 no index keys lease_owner, so the plan scans ix_backwave_jobs_leased_queue
-- and looks every row up on the clustered key. The UPDLOCK hint keeps a U lock on every row that scan
-- touches, which is every live Lease in the store rather than the batch this worker hands back. A peer
-- that claims at the same instant closes a lock cycle and SQL Server kills one of the two. Keying
-- lease_owner turns the scan into a seek, so the hand-back's lock footprint is its own batch.
--
-- attempt rides along as an INCLUDE column. The hand-back reads exactly (job_id, attempt), and job_id
-- is the clustered key, so the seek covers the whole read and never returns to the clustered index.
-- Filtered on state = 2 for the same reason ix_backwave_jobs_leased_queue is: only live Leases have a
-- lease_owner worth indexing, and every other row stays out of the index.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_backwave_jobs_lease_owner')
    CREATE INDEX ix_backwave_jobs_lease_owner
        ON backwave.jobs (lease_owner) INCLUDE (attempt) WHERE state = 2;

-- v1 seeds the row with an INSERT guarded by WHERE NOT EXISTS, so every later version stamps by UPDATE.
UPDATE backwave.schema_version SET version = 2;
