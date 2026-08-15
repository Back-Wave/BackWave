using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Monitor;
using BackWave.Storage;
using BackWave.Testing;
using BackWave.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

public sealed record WelcomeEmail(string UserId);

public sealed class WelcomeEmailHandler(WelcomeRecorder recorder) : IJobHandler<WelcomeEmail>
{
    public Task HandleAsync(WelcomeEmail job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Sent.Add(job.UserId);
        return Task.CompletedTask;
    }
}

public sealed class WelcomeRecorder
{
    public List<string> Sent { get; } = [];
}

[JsonSerializable(typeof(WelcomeEmail))]
internal sealed partial class HostJsonContext : JsonSerializerContext;

/// <summary>
/// The Client and Monitor APIs on a real host: registered in an ASP.NET Core app's DI,
/// served by TestServer, and exercised against the In-Memory Store.
/// </summary>
public class HostIntegrationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static WebApplication BuildHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton<WelcomeRecorder>();
        builder.Services.AddTransient<IJobHandler<WelcomeEmail>, WelcomeEmailHandler>();
        builder.Services.AddSingleton(new JobRegistry(
        [
            JobRegistration.Create<WelcomeEmail, WelcomeEmailHandler>(
                "welcome-email", HostJsonContext.Default.WelcomeEmail),
        ]));
        builder.Services.AddSingleton<IJobStore, InMemoryJobStore>();
        builder.Services.AddSingleton<BackWaveClient>();
        builder.Services.AddSingleton<BackWaveMonitor>();

        return builder.Build();
    }

    [Fact]
    public async Task ClientAndMonitor_ResolveFromTheHost_AndDriveAJobToSucceeded()
    {
        await using var app = BuildHost();
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new WelcomeEmail("user-42"), dueTime: T0);

        // Asserted through the Monitor API only — the same surface the dashboard uses.
        var pending = await monitor.GetJobAsync(jobId);
        Assert.Equal(JobState.Scheduled, pending!.State);

        var pump = new DeterministicPump(
            new NodeDriver(new NodeOptions
            {
                WorkerId = "host-node",
                Policy = new DispatchPolicy.Strict(["default"]),
                RetryPolicy = RetryPolicy.Default,
            }),
            app.Services.GetRequiredService<IJobStore>(),
            app.Services.GetRequiredService<JobRegistry>(),
            app.Services);
        await pump.PumpAsync(T0);

        var done = await monitor.GetJobAsync(jobId);
        Assert.Equal(JobState.Succeeded, done!.State);
        Assert.Equal(["user-42"], app.Services.GetRequiredService<WelcomeRecorder>().Sent);

        var depth = Assert.Single(await monitor.GetQueueDepthsAsync());
        Assert.Equal(new QueueStateCount("default", JobState.Succeeded, 1), depth);

        await app.StopAsync();
    }
}
