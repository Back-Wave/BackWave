using System.Text.Json.Serialization;
using BackWave.Jobs;
using BackWave.Pro;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

// ── Zero-config Workflow Input seed codec, wired by the [Job] generator (0263) ──────────
//
// Twin of WorkflowsV2JobOutputTests for the SEED half of the finding: a consumer marks the seed
// IWorkflowInput and lists it in a JsonSerializerContext, and the generator emits the seed-codec map onto
// the registry. So Workflow(seed) / StartWorkflow(seed) bake it and ctx.Input<TInput>() reads it back with
// NO JsonTypeInfo passed anywhere. This runs the real STJ generator + [Job] generator + runtime end-to-end.

/// <summary>The immutable Workflow Input this suite seeds its graphs with - marked, never hand-serialized.</summary>
public sealed record GenOrderSeed(string OrderId, bool Premium) : IWorkflowInput;

/// <summary>Records what a seeded handler observed of its Workflow Input.</summary>
public sealed class SeedRecorder
{
    public string? SeenOrderId { get; set; }
    public bool SeenPremium { get; set; }
}

[Job("gen-echo-seed")]
public sealed record GenEchoStep(string Note) : IWorkflowStep;

public sealed class GenEchoStepHandler(SeedRecorder recorder) : IJobHandler<GenEchoStep>
{
    public Task HandleAsync(GenEchoStep job, JobContext context, CancellationToken ct)
    {
        // The whole point: the seed codec is resolved off the registry - no JsonTypeInfo at the call site.
        var seed = context.Input<GenOrderSeed>();
        recorder.SeenOrderId = seed.OrderId;
        recorder.SeenPremium = seed.Premium;
        return Task.CompletedTask;
    }
}

/// <summary>A reusable definition seeded with <see cref="GenOrderSeed"/> - one echo step.</summary>
public sealed class GenCheckoutWorkflow : IWorkflow<GenOrderSeed>
{
    public void Build(TypedWorkflowBuilder builder, GenOrderSeed seed)
        => builder.Then(new GenEchoStep(seed.OrderId));
}

// Only the seed needs listing for the seed-codec map; the step payload rides the generated wire class.
[JsonSerializable(typeof(GenOrderSeed))]
internal sealed partial class WorkflowsV2SeedJsonContext : JsonSerializerContext;

/// <summary>
/// Workflows v2 zero-config seed codec (issue 0263): the [Job] generator emits the Workflow Input
/// seed-codec map from the app's JsonSerializerContext, so <c>Workflow(seed)</c>,
/// <c>StartWorkflow(seed)</c>, and <c>ctx.Input&lt;TInput&gt;()</c> all pass no serializer.
/// </summary>
public class WorkflowsV2GeneratedSeedCodecTests
{
    private static BackWaveHarness NewHarness(out SeedRecorder recorder)
    {
        var services = new ServiceCollection()
            .AddSingleton<SeedRecorder>()
            .AddTransient<IJobHandler<GenEchoStep>, GenEchoStepHandler>()
            .BuildServiceProvider();
        // The generated registry carries the seed-codec map (BackWaveJobs.CreateSeedCodecs()) the generator
        // built from WorkflowsV2SeedJsonContext - nothing is hand-wired.
        var registry = Generated.BackWaveJobs.CreateRegistry();
        recorder = services.GetRequiredService<SeedRecorder>();
        return new BackWaveHarness(registry, services, new BackWaveHarnessOptions
        {
            RetryPolicy = new Core.RetryPolicy { MaxAttempts = 1 },
        });
    }

    [Fact]
    public async Task InlineWorkflow_WithNoSeedTypeInfo_BakesTheSeed_AndCtxInputReadsItBack()
    {
        var h = NewHarness(out var recorder);

        // Workflow(seed) with no JsonTypeInfo - the seed codec is resolved off the registry.
        await h.Client.Workflow(new GenOrderSeed("order-77", Premium: true))
            .Then(new GenEchoStep("x"))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Equal("order-77", recorder.SeenOrderId);
        Assert.True(recorder.SeenPremium);
    }

    [Fact]
    public async Task StartWorkflow_WithNoSeedTypeInfo_RunsTheDefinition_WithItsSeed()
    {
        var h = NewHarness(out var recorder);

        // StartWorkflow<TWorkflow, TInput>(seed) with no JsonTypeInfo.
        await h.Client.StartWorkflow<GenCheckoutWorkflow, GenOrderSeed>(
            new GenOrderSeed("order-91", Premium: false));
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Equal("order-91", recorder.SeenOrderId);
        Assert.False(recorder.SeenPremium);
    }
}
