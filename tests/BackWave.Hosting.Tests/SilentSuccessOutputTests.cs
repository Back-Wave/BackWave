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

/// <summary>A handler that succeeds without ever calling SetOutput - the "silent success" shape.</summary>
public sealed class SilentSuccessHandler : IJobHandler<OutputJob>
{
    public Task HandleAsync(OutputJob job, JobContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

/// <summary>
/// Silent success through the REAL production pump (<c>WorkerGroupService</c>), not the deterministic
/// test twin: a handler that returns without SetOutput must persist <b>no</b> Output on the job row.
/// Shipped v1.2.0 had an un-cast <c>: null</c> on the pump's outcome-batch drain, so the null arm
/// converted through <c>byte[]</c> and every silent success persisted a non-null EMPTY (0-byte) Output
/// blob instead of none. The only prior coverage exercised the DeterministicPump twin; this pins the
/// production pump itself.
/// </summary>
public class SilentSuccessOutputTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(25);

    [Fact]
    public async Task SilentSuccess_ThroughTheProductionPump_PersistsNullOutput_NotAnEmptyBlob()
    {
        var store = new InMemoryJobStore();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<OutputJob, SilentSuccessHandler>(
                "silent-output", OutputJsonContext.Default.OutputJob, "default"),
        ]);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddTransient<IJobHandler<OutputJob>, SilentSuccessHandler>();
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(store)
            .UseRegistry(registry)
            .AddWorkerGroup(new WorkerGroupOptions
            {
                Name = "workers",
                Policy = new DispatchPolicy.Strict(["default"]),
                PollInterval = FastPoll,
                LeaseDuration = TimeSpan.FromSeconds(5),
            }));

        await using var app = builder.Build();
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new OutputJob("silent"), dueTime: DateTimeOffset.UtcNow);

        var deadline = DateTimeOffset.UtcNow + TestTimeout;
        while (DateTimeOffset.UtcNow < deadline
               && (await monitor.GetJobAsync(jobId))?.State != JobState.Succeeded)
        {
            await Task.Delay(25);
        }
        Assert.Equal(JobState.Succeeded, (await monitor.GetJobAsync(jobId))?.State);

        // The row must carry NO Output at all: null, never a non-null zero-length blob. Asserting on
        // the raw record distinguishes the two (a 0-byte blob would satisfy a mere "decodes to nothing").
        var record = await store.GetJobAsync(jobId);
        Assert.Null(record!.Output);

        await app.StopAsync();
    }
}
