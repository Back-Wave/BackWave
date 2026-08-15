using System.Text.Json.Serialization;
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

public sealed record TaggingJob(string Variant);

/// <summary>A handler that tags the running job from inside <see cref="JobContext"/> (ADR 0022).</summary>
public sealed class TaggingHandler : IJobHandler<TaggingJob>
{
    public Task HandleAsync(TaggingJob job, JobContext context, CancellationToken cancellationToken)
    {
        context.AddTag("variant", job.Variant);
        context.AddLabel("observed");
        return Task.CompletedTask;
    }
}

[JsonSerializable(typeof(TaggingJob))]
internal sealed partial class TaggingJsonContext : JsonSerializerContext;

/// <summary>
/// End-to-end (issue 0110): a handler calling <c>context.AddLabel/AddTag</c> through the real hosted
/// pump lands those Tags on the job once the Attempt's outcome write completes.
/// </summary>
public class JobTagRuntimeHostingTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(25);

    private static WorkerGroupOptions Group(string queue) => new()
    {
        Name = "workers",
        Policy = new DispatchPolicy.Strict([queue]),
        PollInterval = FastPoll,
        LeaseDuration = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task HandlerTags_LandOnTheJob_AfterExecution()
    {
        var store = new InMemoryJobStore();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<TaggingJob, TaggingHandler>("tagging", TaggingJsonContext.Default.TaggingJob, "default"),
        ]);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddTransient<IJobHandler<TaggingJob>, TaggingHandler>();
        builder.Services.AddBackWave(backwave =>
            backwave.UseStore(store).UseRegistry(registry).AddWorkerGroup(Group("default")));

        await using var app = builder.Build();
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new TaggingJob("BRCA1"), dueTime: DateTimeOffset.UtcNow);

        var deadline = DateTimeOffset.UtcNow + TestTimeout;
        JobSnapshot? snapshot = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            snapshot = await monitor.GetJobAsync(jobId);
            if (snapshot?.State == JobState.Succeeded)
            {
                break;
            }
            await Task.Delay(25);
        }

        Assert.Equal(JobState.Succeeded, snapshot?.State);
        Assert.Contains(JobTag.Keyed("variant", "BRCA1"), snapshot!.Tags);
        Assert.Contains(JobTag.Label("observed"), snapshot.Tags);

        await app.StopAsync();
    }
}
