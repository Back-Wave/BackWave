using BackWave.Core;
using BackWave.Storage;
using Npgsql;

namespace BackWave.Postgres.Tests;

[Collection("postgres")]
public sealed class PostgresSchemaNameCaseTests
{
    private static readonly string LongestMixedCase = "Bw_" + new string('X', 60);

    public static TheoryData<string, bool> HintCases => new()
    {
        { "MySchema", false },
        { "MySchema", true },
        { LongestMixedCase, false },
        { LongestMixedCase, true },
    };

    private static async Task<PostgresJobStore> FreshStoreAsync(string schema)
    {
        await using var dataSource = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString);
        await using (var drop = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"))
        {
            await drop.ExecuteNonQueryAsync();
        }
        return new PostgresJobStore(new PostgresStoreOptions
        {
            ConnectionString = new NpgsqlConnectionStringBuilder(PostgresTestDatabase.ConnectionString)
            {
                Pooling = false,
            }.ConnectionString,
            SchemaName = schema,
            AutoMigrate = true,
        });
    }

    [Theory]
    [MemberData(nameof(HintCases))]
    public async Task EnqueueToStart_UnderAnUpperCaseSchemaName_IsMilliseconds_NotThePollInterval(
        string schema, bool transactional)
    {
        await using var store = await FreshStoreAsync(schema);
        var (service, client, recorder) = WakeUpHintTests.Build(store, pollInterval: TimeSpan.FromSeconds(10));
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WakeUpHintTests.WaitForListenerAsync(new SchemaRewriter(schema).HintChannel);

            var enqueuedAt = DateTimeOffset.UtcNow;
            if (transactional)
            {
                await using var caller = new NpgsqlConnection(PostgresTestDatabase.ConnectionString);
                await caller.OpenAsync();
                await using var transaction = await caller.BeginTransactionAsync();
                await client.EnqueueAsync(new HintProbe("transactional"), enqueuedAt, transaction: transaction);
                await transaction.CommitAsync();
            }
            else
            {
                await client.EnqueueAsync(new HintProbe("plain"), enqueuedAt);
            }

            var startedAt = await recorder.Handled.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var latency = startedAt - enqueuedAt;
            Assert.True(latency < TimeSpan.FromSeconds(3), $"enqueue-to-start took {latency}");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task MintDue_UnderTheLongestSchemaName_MintsTheTick()
    {
        await using var store = await FreshStoreAsync(LongestMixedCase);
        var cursor = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await store.UpsertScheduleAsync(new ScheduleRecord
        {
            ScheduleId = "long-name",
            Cron = CronExpression.Parse("0 3 * * *").Canonical,
            WireName = "long-name-probe",
            Payload = "{}"u8.ToArray(),
            Queue = "default",
            Cursor = cursor,
        });
        var tick = cursor.AddDays(1).AddHours(3);

        var minted = await store.MintDueAsync(
            [new MintDecision("long-name", ExpectedCursor: cursor, NewCursor: tick, Ticks: [tick], SkippedTicks: [])]);

        Assert.Equal(1, minted);
    }

    [Fact]
    public async Task ConcurrentFirstMigration_WithSchemaNamesThatDifferOnlyInCase_AllSucceed()
    {
        const int rounds = 10;
        const int fleet = 16;
        string[] names = ["Bw_MigCase", "bw_migcase"];

        for (var round = 0; round < rounds; round++)
        {
            await using (var dataSource = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString))
            await using (var drop = dataSource.CreateCommand("DROP SCHEMA IF EXISTS bw_migcase CASCADE"))
            {
                await drop.ExecuteNonQueryAsync();
            }

            using var barrier = new Barrier(fleet);
            var outcomes = await Task.WhenAll(Enumerable.Range(0, fleet).Select(node => Task.Run(async () =>
            {
                await using var dataSource = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString);
                barrier.SignalAndWait();
                try
                {
                    await PostgresMigrator.MigrateAsync(dataSource, names[node % names.Length]);
                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            })));

            Assert.All(outcomes, outcome => Assert.Null(outcome));
        }
    }
}
