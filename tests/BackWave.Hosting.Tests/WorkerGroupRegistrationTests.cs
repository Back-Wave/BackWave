using BackWave.Core;
using BackWave.Jobs;
using BackWave.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BackWave.Hosting.Tests;

/// <summary>
/// Registration wiring for a Worker Group's Pumps dial (ADR 0037): a group fans out one
/// <see cref="WorkerGroupService"/> hosted pump per <see cref="WorkerGroupOptions.Pumps"/>, and
/// <see cref="BackWaveBuilder.AddWorkerGroup"/> rejects a sub-one pump count or a duplicate group name.
/// </summary>
public class WorkerGroupRegistrationTests
{
    private static WorkerGroupOptions Group(string name, int pumps = 1) => new()
    {
        Name = name,
        Policy = new DispatchPolicy.Strict(["default"]),
        Pumps = pumps,
    };

    private static int RegisteredPumpCount(WorkerGroupOptions group)
    {
        var services = new ServiceCollection();
        services.AddLogging(); // the pump factory resolves an ILogger<WorkerGroupService> on materialization
        services.AddBackWave(backwave => backwave
            .UseStore(new InMemoryJobStore())
            .UseRegistry(new JobRegistry([]))
            .AddWorkerGroup(group));
        using var provider = services.BuildServiceProvider();
        return provider.GetServices<IHostedService>().OfType<WorkerGroupService>().Count();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void AddWorkerGroup_RegistersOnePumpPerPumpsCount(int pumps)
    {
        // The Apply() fan-out registers exactly Pumps WorkerGroupService hosted services — the
        // throughput lever. A regression to "one pump regardless of Pumps" would still pass the
        // end-to-end tests (one pump processes the queue) but silently lose the dial.
        Assert.Equal(pumps, RegisteredPumpCount(Group("workers", pumps)));
    }

    [Fact]
    public void AddWorkerGroup_WithPumpsBelowOne_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddBackWave(backwave => backwave
                .UseStore(new InMemoryJobStore())
                .UseRegistry(new JobRegistry([]))
                .AddWorkerGroup(Group("workers", pumps: 0))));
        Assert.Contains("Pump", ex.Message);
    }

    [Fact]
    public void AddWorkerGroup_WithDuplicateName_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddBackWave(backwave => backwave
                .UseStore(new InMemoryJobStore())
                .UseRegistry(new JobRegistry([]))
                .AddWorkerGroup(Group("workers"))
                .AddWorkerGroup(Group("workers"))));
        Assert.Contains("twice", ex.Message);
    }
}
