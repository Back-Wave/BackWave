using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using BackWave.Core;
using BackWave.Jobs;
using BackWave.Monitor;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Hosting.Tests;

public sealed record AuditEntry(string Action);

public sealed record AuditPurge(string Reason);

public sealed record AuditSeed(string Actor);

public sealed class AuditRecorder
{
    public List<string> Handled { get; } = [];
}

public sealed class AuditHandler(AuditRecorder recorder) : IJobHandler<AuditEntry>
{
    public Task HandleAsync(AuditEntry job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Handled.Add(job.Action);
        return Task.CompletedTask;
    }
}

public sealed class AuditPurgeHandler : IJobHandler<AuditPurge>
{
    public Task HandleAsync(AuditPurge job, JobContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

[JsonSerializable(typeof(AuditEntry))]
[JsonSerializable(typeof(AuditPurge))]
[JsonSerializable(typeof(AuditSeed))]
internal sealed partial class AuditJsonContext : JsonSerializerContext;

/// <summary>
/// The additive registration seam: a host can contribute extra <see cref="JobRegistration"/> instances
/// through dependency injection, and <c>Apply</c> folds them into the registry that
/// <c>UseRegistry(...)</c> supplied. BackWave.Pro's <c>AddWorkflowGate</c> is the shipped consumer of
/// this seam, and a hand-registered job type that generated serialization cannot express is the other.
/// </summary>
public class ContributedJobRegistrationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private static JobRegistration AuditRegistration(string wireName = "audit", string queue = "default")
        => JobRegistration.Create<AuditEntry, AuditHandler>(
            wireName, AuditJsonContext.Default.AuditEntry, queue);

    // A second, distinct payload type so a base registry and a contributed registration never collide
    // on JobType - the registry rejects a repeated CLR type as well as a repeated Wire Name.
    private static JobRegistration BaseRegistration()
        => JobRegistration.Create<AuditPurge, AuditPurgeHandler>(
            "base-job", AuditJsonContext.Default.AuditPurge);

    [Fact]
    public void ContributedRegistration_IsFoldedIntoTheGeneratedRegistry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(AuditRegistration());
        services.AddBackWave(backwave => backwave
            .UseStore(new InMemoryJobStore())
            .UseRegistry(new JobRegistry([BaseRegistration()])));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<JobRegistry>();

        Assert.Equal(["audit", "base-job"], registry.Registrations.Select(r => r.WireName));
    }

    [Fact]
    public void NoContributedRegistration_ReturnsTheSuppliedRegistryUnchanged()
    {
        // The zero-contribution path must hand back the very instance UseRegistry supplied. A rebuild
        // would be observationally close but would drop reference identity that callers can hold.
        var supplied = new JobRegistry([BaseRegistration()]);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBackWave(backwave => backwave
            .UseStore(new InMemoryJobStore())
            .UseRegistry(supplied));

        using var provider = services.BuildServiceProvider();

        Assert.Same(supplied, provider.GetRequiredService<JobRegistry>());
    }

    [Fact]
    public void ContributedRegistration_PreservesTheSuppliedSeedCodecs()
    {
        // Seed codecs have no public getter, so folding must carry them across rather than rebuild from
        // Registrations alone. Lose them and a seed-aware workflow can no longer read its Workflow Input.
        var seedCodecs = new Dictionary<Type, JsonTypeInfo>
        {
            [typeof(AuditSeed)] = AuditJsonContext.Default.AuditSeed,
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(AuditRegistration());
        services.AddBackWave(backwave => backwave
            .UseStore(new InMemoryJobStore())
            .UseRegistry(new JobRegistry([BaseRegistration()], seedCodecs)));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<JobRegistry>();

        Assert.Same(AuditJsonContext.Default.AuditSeed, registry.FindSeedCodec(typeof(AuditSeed)));
    }

    [Fact]
    public void ContributedRegistration_ThatRepeatsAWireName_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(AuditRegistration());
        services.AddBackWave(backwave => backwave
            .UseStore(new InMemoryJobStore())
            .UseRegistry(new JobRegistry([AuditRegistration()])));

        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<JobRegistry>());

        Assert.Contains("Duplicate Wire Name 'audit'", exception.Message);
    }

    [Fact]
    public async Task ContributedRegistration_RunsEndToEndThroughTheHostedPump()
    {
        // The seam is only useful if a contributed registration dispatches like a generated one. This
        // enqueues and drains a job type the registry never knew about until DI folded it in.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<AuditRecorder>();
        builder.Services.AddTransient<IJobHandler<AuditEntry>, AuditHandler>();
        builder.Services.AddSingleton(AuditRegistration());
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(new InMemoryJobStore())
            .UseRegistry(new JobRegistry([]))
            .AddWorkerGroup(new WorkerGroupOptions
            {
                Name = "workers",
                Policy = new DispatchPolicy.Strict(["default"]),
                PollInterval = TimeSpan.FromMilliseconds(25),
                LeaseDuration = TimeSpan.FromSeconds(5),
            }));

        await using var app = builder.Build();
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new AuditEntry("deleted"), dueTime: DateTimeOffset.UtcNow);

        var deadline = DateTimeOffset.UtcNow + TestTimeout;
        while (DateTimeOffset.UtcNow < deadline
            && (await monitor.GetJobAsync(jobId))?.State != JobState.Succeeded)
        {
            await Task.Delay(25);
        }

        Assert.Equal(JobState.Succeeded, (await monitor.GetJobAsync(jobId))?.State);
        Assert.Equal(["deleted"], app.Services.GetRequiredService<AuditRecorder>().Handled);

        await app.StopAsync();
    }
}
