using BackWave.Storage;
using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle.Tests;

/// <summary>
/// A pump that waits for a pooled connection and does not get one in time must see a transient
/// fault, not a fail-stop. A busy node can drain its pool without any fault in the database. This is an
/// integration test: it needs "docker compose up -d oracle".
/// </summary>
[Collection("oracle")]
public sealed class OraclePoolExhaustionFaultTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task A_pool_with_no_free_connection_raises_a_fault_the_adapter_classifies_transient()
    {
        await OracleTestDatabase.CreateFreshStoreAsync();

        // A pool of one connection, keyed by this exact string, so no other test shares it.
        var connectionString = new OracleConnectionStringBuilder(OracleTestDatabase.ConnectionString)
        {
            MaxPoolSize = 1,
            ConnectionTimeout = 2,
        }.ConnectionString;
        var store = new OracleJobStore(new OracleStoreOptions { ConnectionString = connectionString });

        // Hold the one pooled connection, so the store's next open must wait for it.
        await using var held = new OracleConnection(connectionString);
        await held.OpenAsync();

        var fault = await Assert.ThrowsAsync<OracleException>(async () =>
            await store.ClaimAsync(new ClaimRequest("w1", ["default"], 10, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow)));
        output.WriteLine($"ORA-{fault.Number}: {fault.Message} (IsTransient={fault.IsTransient})");

        Assert.Equal(50012, fault.Number);
        Assert.True(store.IsTransientFault(fault), $"ORA-{fault.Number} must classify transient");
    }
}
