using System.Text.Json.Serialization;
using BackWave.Jobs;
using BackWave.Pro;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

// ── Typed Job Output steps, output type, handlers, recorder (0264) ──────────────────────

/// <summary>The output an invoice step produces - the value a downstream step reads back.</summary>
public sealed record InvoiceResult(string OrderId, int Cents);

/// <summary>Records the order steps ran, the last typed output a reader step observed, and any
/// exception a handler caught from a typed accessor.</summary>
public sealed class OutputRecorder
{
    public List<string> Ran { get; } = [];
    public DependencyOutput<InvoiceResult>? SeenInvoice { get; set; }
    public Exception? Caught { get; set; }
}

[Job("v2-kickoff")]
public sealed record KickOff(string Note) : IWorkflowStep;

[Job("v2-make-invoice")]
public sealed record MakeInvoice(string OrderId) : IWorkflowStep<InvoiceResult>;

[Job("v2-silent-invoice")]
public sealed record SilentInvoice(string OrderId) : IWorkflowStep<InvoiceResult>;

[Job("v2-read-invoice")]
public sealed record ReadInvoice(string Note) : IWorkflowStep;

[Job("v2-read-silent")]
public sealed record ReadSilent(string Note) : IWorkflowStep;

[Job("v2-sibling-read")]
public sealed record SiblingRead(string Note) : IWorkflowStep;

public sealed class KickOffHandler(OutputRecorder recorder) : IJobHandler<KickOff>
{
    public Task HandleAsync(KickOff job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("kick");
        return Task.CompletedTask;
    }
}

public sealed class MakeInvoiceHandler(OutputRecorder recorder) : IJobHandler<MakeInvoice>
{
    public Task HandleAsync(MakeInvoice job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("make");
        // Compile-checked against MakeInvoice's declared output type; no serializer passed at the call site.
        context.SetOutput<MakeInvoice, InvoiceResult>(new InvoiceResult(job.OrderId, 4200));
        return Task.CompletedTask;
    }
}

// A producer that succeeds but emits nothing - the "absent output" case.
public sealed class SilentInvoiceHandler(OutputRecorder recorder) : IJobHandler<SilentInvoice>
{
    public Task HandleAsync(SilentInvoice job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("silent");
        return Task.CompletedTask;
    }
}

public sealed class ReadInvoiceHandler(OutputRecorder recorder) : IJobHandler<ReadInvoice>
{
    public async Task HandleAsync(ReadInvoice job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("read");
        recorder.SeenInvoice = await context.Output<MakeInvoice, InvoiceResult>(ct);
    }
}

public sealed class ReadSilentHandler(OutputRecorder recorder) : IJobHandler<ReadSilent>
{
    public async Task HandleAsync(ReadSilent job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("read");
        recorder.SeenInvoice = await context.Output<SilentInvoice, InvoiceResult>(ct);
    }
}

// Reads MakeInvoice from a parallel branch (both are children of KickOff). MakeInvoice is not this
// job's ancestor, so the typed read is physically unresolvable and must come back as a clean absence.
public sealed class SiblingReadHandler(OutputRecorder recorder) : IJobHandler<SiblingRead>
{
    public async Task HandleAsync(SiblingRead job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("sibling");
        recorder.SeenInvoice = await context.Output<MakeInvoice, InvoiceResult>(ct);
    }
}

// ── Finding 2 (running-step guard) + Finding 4 (repeated-type ambiguity) fixtures ───────────

// Two producers of the same output type but distinct steps. A handler running Alpha must NOT be able
// to emit output as Beta: the running-step guard rejects it so the buffered bytes always match the
// codec a reader will decode with.
[Job("output-alpha")]
public sealed record OutputAlpha(string OrderId) : IWorkflowStep<InvoiceResult>;

[Job("output-beta")]
public sealed record OutputBeta(string OrderId) : IWorkflowStep<InvoiceResult>;

[Job("output-read-beta")]
public sealed record OutputReadBeta(string Note) : IWorkflowStep;

// A single step type used twice in one workflow (disambiguated only for the builder). Both members
// share one Wire Name, so reading the type from a descendant that has both as ancestors is ambiguous.
[Job("output-dup")]
public sealed record OutputDup(string OrderId) : IWorkflowStep<InvoiceResult>;

[Job("output-ambiguous-reader")]
public sealed record OutputAmbiguousReader(string Note) : IWorkflowStep;

// Runs Alpha but wrongly writes output AS Beta - the running-step guard must reject this.
public sealed class OutputAlphaWritesBetaHandler(OutputRecorder recorder) : IJobHandler<OutputAlpha>
{
    public Task HandleAsync(OutputAlpha job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("alpha");
        try
        {
            context.SetOutput<OutputBeta, InvoiceResult>(new InvoiceResult(job.OrderId, 1));
        }
        catch (Exception ex)
        {
            recorder.Caught = ex;
        }
        return Task.CompletedTask;
    }
}

// Runs Beta and correctly writes its OWN output - the guard must let this through.
public sealed class OutputBetaWritesSelfHandler(OutputRecorder recorder) : IJobHandler<OutputBeta>
{
    public Task HandleAsync(OutputBeta job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("beta");
        context.SetOutput<OutputBeta, InvoiceResult>(new InvoiceResult(job.OrderId, 7));
        return Task.CompletedTask;
    }
}

public sealed class OutputReadBetaHandler(OutputRecorder recorder) : IJobHandler<OutputReadBeta>
{
    public async Task HandleAsync(OutputReadBeta job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("read-beta");
        recorder.SeenInvoice = await context.Output<OutputBeta, InvoiceResult>(ct);
    }
}

public sealed class OutputDupHandler(OutputRecorder recorder) : IJobHandler<OutputDup>
{
    public Task HandleAsync(OutputDup job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("dup");
        context.SetOutput<OutputDup, InvoiceResult>(new InvoiceResult(job.OrderId, 3));
        return Task.CompletedTask;
    }
}

// Reads OutputDup by type; both same-type ancestors share a Wire Name, so the read is ambiguous.
public sealed class OutputAmbiguousReaderHandler(OutputRecorder recorder) : IJobHandler<OutputAmbiguousReader>
{
    public async Task HandleAsync(OutputAmbiguousReader job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("ambiguous-read");
        try
        {
            await context.Output<OutputDup, InvoiceResult>(ct);
        }
        catch (Exception ex)
        {
            recorder.Caught = ex;
        }
    }
}

[JsonSerializable(typeof(InvoiceResult))]
[JsonSerializable(typeof(KickOff))]
[JsonSerializable(typeof(MakeInvoice))]
[JsonSerializable(typeof(SilentInvoice))]
[JsonSerializable(typeof(ReadInvoice))]
[JsonSerializable(typeof(ReadSilent))]
[JsonSerializable(typeof(SiblingRead))]
[JsonSerializable(typeof(OutputAlpha))]
[JsonSerializable(typeof(OutputBeta))]
[JsonSerializable(typeof(OutputReadBeta))]
[JsonSerializable(typeof(OutputDup))]
[JsonSerializable(typeof(OutputAmbiguousReader))]
internal sealed partial class WorkflowsV2OutputJsonContext : JsonSerializerContext;

/// <summary>
/// Workflows v2 typed Job Output (issue 0264): <c>ctx.SetOutput&lt;TStep,TOut&gt;</c> and
/// <c>ctx.Output&lt;TStep,TOut&gt;</c> - compile-checked read/write of a step's output with no string
/// handle and no caller-passed serializer, riding the existing pull path above the frozen spine.
/// </summary>
public class WorkflowsV2JobOutputTests
{
    // The [Job] source generator wires each producer's output codec BY STEP TYPE onto its generated
    // registration - it reads the output type off the app's JsonSerializerContext - so the typed accessors
    // resolve the serializer with nothing passed at the call site and NO outputTypeInfo hand-wired here.
    private static BackWaveHarness NewHarness(out OutputRecorder recorder)
    {
        var services = new ServiceCollection()
            .AddSingleton<OutputRecorder>()
            .AddTransient<IJobHandler<KickOff>, KickOffHandler>()
            .AddTransient<IJobHandler<MakeInvoice>, MakeInvoiceHandler>()
            .AddTransient<IJobHandler<SilentInvoice>, SilentInvoiceHandler>()
            .AddTransient<IJobHandler<ReadInvoice>, ReadInvoiceHandler>()
            .AddTransient<IJobHandler<ReadSilent>, ReadSilentHandler>()
            .AddTransient<IJobHandler<SiblingRead>, SiblingReadHandler>()
            .AddTransient<IJobHandler<OutputAlpha>, OutputAlphaWritesBetaHandler>()
            .AddTransient<IJobHandler<OutputBeta>, OutputBetaWritesSelfHandler>()
            .AddTransient<IJobHandler<OutputReadBeta>, OutputReadBetaHandler>()
            .AddTransient<IJobHandler<OutputDup>, OutputDupHandler>()
            .AddTransient<IJobHandler<OutputAmbiguousReader>, OutputAmbiguousReaderHandler>()
            .BuildServiceProvider();
        var registry = Generated.BackWaveJobs.CreateRegistry();
        recorder = services.GetRequiredService<OutputRecorder>();
        return new BackWaveHarness(registry, services, new BackWaveHarnessOptions
        {
            RetryPolicy = new Core.RetryPolicy { MaxAttempts = 1 },
        });
    }

    [Fact]
    public async Task SetOutput_ThenTypedRead_FlowsTheAncestorOutputDownstream()
    {
        var h = NewHarness(out var recorder);

        await h.Client.Workflow()
            .Then(new MakeInvoice("order-7"))
            .Then(new ReadInvoice("x"))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Equal(["make", "read"], recorder.Ran);
        var seen = Assert.IsType<DependencyOutput<InvoiceResult>>(recorder.SeenInvoice);
        Assert.True(seen.HasOutput);
        Assert.Equal(new InvoiceResult("order-7", 4200), seen.Output);
    }

    [Fact]
    public async Task TypedRead_OfASucceededAncestorThatEmittedNothing_IsAbsence()
    {
        var h = NewHarness(out var recorder);

        await h.Client.Workflow()
            .Then(new SilentInvoice("order-9"))
            .Then(new ReadSilent("x"))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Equal(["silent", "read"], recorder.Ran);
        var seen = Assert.IsType<DependencyOutput<InvoiceResult>>(recorder.SeenInvoice);
        Assert.False(seen.HasOutput);   // succeeded, emitted nothing
        Assert.Null(seen.Output);
    }

    [Fact]
    public async Task TypedRead_OfANonAncestorSibling_ResolvesToAbsence()
    {
        var h = NewHarness(out var recorder);

        // kick → make (produces output); kick → sibling (a parallel branch that reads make). make is a
        // sibling of the reader, not an ancestor, so it shares no happens-before: the typed read of make
        // must resolve to absence, never a value - the honest runtime scope guarantee.
        await h.Client.Workflow()
            .Then(new KickOff("k"))
            .Then(new MakeInvoice("order-3"))
            .Then(new SiblingRead("s"), after: [typeof(KickOff)])
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Contains("sibling", recorder.Ran);
        var seen = Assert.IsType<DependencyOutput<InvoiceResult>>(recorder.SeenInvoice);
        Assert.False(seen.HasOutput);
        Assert.Null(seen.Output);
    }

    [Fact]
    public void SetOutput_WithNoRegisteredOutputCodec_ThrowsLoudly()
    {
        // The registry knows the step but no output codec was registered for it (outputTypeInfo omitted):
        // the typed accessor cannot resolve a serializer and fails loudly rather than guessing.
        var registry = new JobRegistry(
        [
            JobRegistration.Create<MakeInvoice, MakeInvoiceHandler>(
                "v2-make-invoice", WorkflowsV2OutputJsonContext.Default.MakeInvoice),
        ]);
        var ctx = new JobContext { JobId = Guid.NewGuid(), Attempt = 1, Registry = registry };

        Assert.Throws<InvalidOperationException>(() =>
            ctx.SetOutput<MakeInvoice, InvoiceResult>(new InvoiceResult("o", 1)));
    }

    [Fact]
    public void TypedAccessor_WithNoRegistryWired_ThrowsLoudly()
    {
        // A context not built for handler execution (no registry wired) cannot resolve a step type.
        var ctx = new JobContext { JobId = Guid.NewGuid(), Attempt = 1 };
        Assert.Throws<InvalidOperationException>(() =>
            ctx.SetOutput<MakeInvoice, InvoiceResult>(new InvoiceResult("o", 1)));
    }

    [Fact]
    public async Task SetOutput_ForAStepOtherThanTheRunningStep_ThrowsGuided()
    {
        var h = NewHarness(out var recorder);

        // OutputAlpha's handler wrongly emits output AS OutputBeta. A real pump wires the running step's
        // Wire Name, so the guard rejects it rather than silently buffering Beta-codec bytes as Alpha's
        // output - which a reader of Alpha would then decode with the wrong shape.
        await h.Client.Workflow()
            .Then(new OutputAlpha("order-1"))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Contains("alpha", recorder.Ran);
        var caught = Assert.IsType<InvalidOperationException>(recorder.Caught);
        Assert.Contains("running a different step", caught.Message);
    }

    [Fact]
    public async Task SetOutput_ForTheRunningStep_BuffersAndFlowsDownstream()
    {
        var h = NewHarness(out var recorder);

        // The correct-step case: OutputBeta emits its OWN output, so the guard lets it through and the
        // value persists and flows to a downstream reader unchanged.
        await h.Client.Workflow()
            .Then(new OutputBeta("order-2"))
            .Then(new OutputReadBeta("x"))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Null(recorder.Caught);
        Assert.Equal(["beta", "read-beta"], recorder.Ran);
        var seen = Assert.IsType<DependencyOutput<InvoiceResult>>(recorder.SeenInvoice);
        Assert.True(seen.HasOutput);
        Assert.Equal(new InvoiceResult("order-2", 7), seen.Output);
    }

    [Fact]
    public async Task TypedRead_OfARepeatedAncestorStepType_ThrowsAmbiguityGuidance()
    {
        var h = NewHarness(out var recorder);

        // Two OutputDup steps chained a → b, then a reader whose ancestors are BOTH. Repeated step types
        // share one Wire Name (member names are not persisted), so reading OutputDup by type resolves to
        // two ancestors: an ambiguous read that must fail loudly with guidance, not a bare LINQ error.
        await h.Client.Workflow()
            .Then(new OutputDup("o1"), name: "a")
            .Then(new OutputDup("o2"), name: "b")
            .Then(new OutputAmbiguousReader("r"))
            .EnqueueAsync();
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Contains("ambiguous-read", recorder.Ran);
        var caught = Assert.IsType<InvalidOperationException>(recorder.Caught);
        Assert.Contains("ambiguous", caught.Message);
        Assert.Contains("more than once among this job's ancestors", caught.Message);
    }
}
