using BackWave.Core;
using BackWave.Storage;
using Npgsql;

namespace BackWave.Postgres.Tests;

/// <summary>
/// The one-statement claim reads the Transition Log from the snapshot its statement starts with, and
/// locks each job later, in the same scan. Another node can lease a job and hand it back between the two.
/// This test stops claimer A inside that window: a row-level security policy on <c>backwave.jobs</c>
/// for A's role calls a function that waits on an advisory lock the test holds. Postgres evaluates the
/// policy after the snapshot and before the row lock. Superusers and the table owner skip row-level
/// security, so A logs in as its own non-superuser role and store B is not affected.
/// </summary>
[Collection("postgres")]
public sealed class PostgresClaimWindowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly StoreBounds CapOne = StoreBounds.Default with { MaxTransitionsPerJob = 1 };
    private const string Queue = "claim-window";
    private const string ClaimerRole = "backwave_claim_window";
    private const string ClaimerApplication = "claim-window-a";
    private const int GateClass = 4296;
    private const int GateObject = 1;

    [Fact]
    public async Task A_job_handed_back_after_the_claim_snapshot_keeps_its_log_ordered_and_capped()
    {
        await using var storeB = await PostgresTestDatabase.CreateFreshStoreAsync(bounds: CapOne);
        await using var admin = new NpgsqlConnection(PostgresTestDatabase.ConnectionString);
        await admin.OpenAsync();
        await InstallGateAsync(admin);
        try
        {
            await using var storeA = new PostgresJobStore(new PostgresStoreOptions
            {
                ConnectionString = ClaimerConnectionString(),
                Bounds = CapOne,
            });
            Assert.Empty(await storeA.ClaimAsync(Claim("a"))); // stamps the queue unlimited

            var job = new NewJob(Guid.NewGuid(), "claim-window", "{}"u8.ToArray(), Queue, T0);
            await storeB.EnqueueAsync(job, T0);

            await Execute(admin, $"SELECT pg_advisory_lock({GateClass}, {GateObject})");
            PostgresRoundTripScope? roundTrips = null;
            var claimA = Task.Run(async () =>
            {
                using var scope = PostgresRoundTrips.Observe();
                roundTrips = scope;
                return await storeA.ClaimAsync(Claim("a"));
            });
            await PostgresTestDatabase.WaitForLockWaitAsync(ClaimerApplication, "advisory");

            Assert.Equal(job.JobId, Assert.Single(await storeB.ClaimAsync(Claim("b"))).JobId);
            Assert.Equal(1, await storeB.RelinquishLeasesAsync(
                "b", T0, new RetryPolicy { MaxAttempts = 5 }.ToDisposition()));

            await Execute(admin, $"SELECT pg_advisory_unlock({GateClass}, {GateObject})");
            var claimed = Assert.Single(await claimA);

            Assert.Equal(job.JobId, claimed.JobId);
            Assert.Equal(new[] { (3L, JobState.Leased) }, await LogInCommitOrderAsync(admin, job.JobId));
            Assert.Equal(1, roundTrips!.Commits); // the one-statement claim stood down for the transaction claim
        }
        finally
        {
            await RemoveGateAsync(admin);
        }
    }

    private static ClaimRequest Claim(string worker) => new(worker, [Queue], 1, Lease, T0);

    private static string ClaimerConnectionString() => new NpgsqlConnectionStringBuilder(PostgresTestDatabase.ConnectionString)
    {
        Username = ClaimerRole,
        Password = ClaimerRole,
        ApplicationName = ClaimerApplication,
        MaxPoolSize = 2,
    }.ConnectionString;

    private static async Task InstallGateAsync(NpgsqlConnection admin)
    {
        await RemoveGateAsync(admin);
        await Execute(admin, $"""
            DO $$ BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{ClaimerRole}') THEN
                    CREATE ROLE {ClaimerRole} LOGIN NOSUPERUSER PASSWORD '{ClaimerRole}';
                END IF;
            END $$;
            GRANT USAGE ON SCHEMA backwave TO {ClaimerRole};
            GRANT ALL ON ALL TABLES IN SCHEMA backwave TO {ClaimerRole};
            GRANT ALL ON ALL SEQUENCES IN SCHEMA backwave TO {ClaimerRole};
            CREATE FUNCTION backwave.claim_window_gate() RETURNS boolean LANGUAGE plpgsql VOLATILE AS $f$
            BEGIN
                PERFORM pg_advisory_lock_shared({GateClass}, {GateObject});
                PERFORM pg_advisory_unlock_shared({GateClass}, {GateObject});
                RETURN true;
            END $f$;
            CREATE POLICY claim_window ON backwave.jobs TO {ClaimerRole} USING (backwave.claim_window_gate());
            ALTER TABLE backwave.jobs ENABLE ROW LEVEL SECURITY;
            """);
    }

    private static Task RemoveGateAsync(NpgsqlConnection admin) => Execute(admin, """
        SELECT pg_advisory_unlock_all();
        ALTER TABLE backwave.jobs DISABLE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS claim_window ON backwave.jobs;
        DROP FUNCTION IF EXISTS backwave.claim_window_gate();
        """);

    private static async Task<List<(long Ordinal, JobState State)>> LogInCommitOrderAsync(
        NpgsqlConnection connection, Guid jobId)
    {
        await using var read = new NpgsqlCommand(
            "SELECT ordinal, state FROM backwave.job_transitions WHERE job_id = @id ORDER BY position",
            connection);
        read.Parameters.AddWithValue("id", jobId);
        await using var reader = await read.ExecuteReaderAsync();
        var log = new List<(long, JobState)>();
        while (await reader.ReadAsync())
        {
            log.Add((reader.GetInt64(0), (JobState)reader.GetInt32(1)));
        }
        return log;
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
