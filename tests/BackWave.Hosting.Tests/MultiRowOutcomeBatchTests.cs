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

public sealed record OutputJob(string Key);

/// <summary>One job's distinct output: the Key it was enqueued with, echoed back from the handler.</summary>
public sealed record OutputPayload(string Key, string Body);

/// <summary>
/// A barrier across all in-flight jobs so they reach their outcome write together — maximizing the chance
/// the real Shell drains them in one multi-row outcome batch — then each emits a Job Output keyed to its
/// own job, so a batch/stash seam bug would surface as a mismatched output.
/// </summary>
public sealed class DistinctOutputHandler(Barrier barrier) : IJobHandler<OutputJob>
{
    public Task HandleAsync(OutputJob job, JobContext context, CancellationToken cancellationToken)
    {
        context.SetOutput(
            new OutputPayload(job.Key, $"body-for-{job.Key}"), OutputJsonContext.Default.OutputPayload);
        // Hold every handler here until all of them have buffered their output, so their outcome
        // writes hit the Shell's batch drain in one window rather than one at a time.
        barrier.SignalAndWait(TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }
}

[JsonSerializable(typeof(OutputJob))]
[JsonSerializable(typeof(OutputPayload))]
internal sealed partial class OutputJsonContext : JsonSerializerContext;

/// <summary>
/// Multi-row outcome batch drain (the batch/stash seam): existing hosting tests drove a single job to
/// terminal, so every real <c>ReportOutcomeBatch</c> carried exactly one row. Here three jobs land in one
/// drain window, each with a distinct Job Output, and each job's persisted output must match its own
/// handler — proving the per-(JobId,Attempt) Output stash does not cross-contaminate rows in a batch.
/// </summary>
public class MultiRowOutcomeBatchTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(25);

    [Fact]
    public async Task ThreeJobs_DrainInOneBatch_EachPersistsItsOwnOutput()
    {
        var store = new InMemoryJobStore();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<OutputJob, DistinctOutputHandler>(
                "output", OutputJsonContext.Default.OutputJob, "default"),
        ]);
        var keys = new[] { "alpha", "beta", "gamma" };
        var barrier = new Barrier(keys.Length);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(barrier);
        builder.Services.AddTransient<IJobHandler<OutputJob>, DistinctOutputHandler>();
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(store)
            .UseRegistry(registry)
            .AddWorkerGroup(new WorkerGroupOptions
            {
                Name = "workers",
                Policy = new DispatchPolicy.Strict(["default"]),
                PollInterval = FastPoll,
                // PoolSize ≥ the job count so all three run concurrently and the Barrier can release them
                // together into one outcome batch drain.
                PoolSize = keys.Length,
                LeaseDuration = TimeSpan.FromSeconds(5),
            }));

        await using var app = builder.Build();
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();

        var jobIds = new Dictionary<string, Guid>();
        foreach (var key in keys)
        {
            jobIds[key] = await client.EnqueueAsync(new OutputJob(key), dueTime: DateTimeOffset.UtcNow);
        }

        var deadline = DateTimeOffset.UtcNow + TestTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var states = await Task.WhenAll(jobIds.Values.Select(async id => (await monitor.GetJobAsync(id))?.State));
            if (states.All(s => s == JobState.Succeeded))
            {
                break;
            }
            await Task.Delay(25);
        }

        // Each job succeeded AND its persisted output is exactly the one its own handler emitted —
        // no row in the batch picked up a sibling's stashed Output.
        foreach (var (key, jobId) in jobIds)
        {
            Assert.Equal(JobState.Succeeded, (await monitor.GetJobAsync(jobId))?.State);
            var output = await monitor.GetJobOutputAsync(jobId);
            Assert.NotNull(output);
            var decoded = JobOutputCodec.Decode(output.Value, OutputJsonContext.Default.OutputPayload);
            Assert.Equal(new OutputPayload(key, $"body-for-{key}"), decoded);
        }

        await app.StopAsync();
    }
}
