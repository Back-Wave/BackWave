namespace BackWave.SourceGenerators.Tests;

/// <summary>
/// Covers the BW0007 code fix: it adds [JsonSerializable(typeof(T))] for the offending workflow
/// output/seed type to a JsonSerializerContext (existing single, existing several, or scaffolded),
/// and applying it clears BW0007 when the generator re-runs on the fixed source.
/// </summary>
public class CodeFixTests
{
    // A workflow step whose Job Output type is listed in no context: BW0007 on the output type.
    private const string OutputNotListedSource = """
        using System.Threading;
        using System.Threading.Tasks;
        using System.Text.Json.Serialization;
        using BackWave.Jobs;
        using BackWave.Pro;

        namespace Acme;

        public sealed record InvoiceResult(string OrderId);

        [Job("make-invoice")]
        public sealed record MakeInvoice(string OrderId) : IWorkflowStep<InvoiceResult>;

        public sealed class MakeInvoiceHandler : IJobHandler<MakeInvoice>
        {
            public Task HandleAsync(MakeInvoice job, JobContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        [JsonSerializable(typeof(MakeInvoice))]
        internal sealed partial class AppJson : JsonSerializerContext;
        """;

    // A Workflow Input seed listed in no context: BW0007 on the seed type.
    private const string SeedNotListedSource = """
        using System.Threading;
        using System.Threading.Tasks;
        using System.Text.Json.Serialization;
        using BackWave.Jobs;
        using BackWave.Pro;

        namespace Acme;

        public sealed record CheckoutSeed(string OrderId) : IWorkflowInput;

        [Job("charge-card")]
        public sealed record ChargeCard(string OrderId) : IWorkflowStep;

        public sealed class ChargeCardHandler : IJobHandler<ChargeCard>
        {
            public Task HandleAsync(ChargeCard job, JobContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        [JsonSerializable(typeof(ChargeCard))]
        internal sealed partial class AppJson : JsonSerializerContext;
        """;

    // Two contexts, neither listing the offending output type: the fix offers each as a target.
    private const string OutputNotListedMultiContextSource = """
        using System.Threading;
        using System.Threading.Tasks;
        using System.Text.Json.Serialization;
        using BackWave.Jobs;
        using BackWave.Pro;

        namespace Acme;

        public sealed record InvoiceResult(string OrderId);
        public sealed record Filler(string Value);

        [Job("make-invoice")]
        public sealed record MakeInvoice(string OrderId) : IWorkflowStep<InvoiceResult>;

        public sealed class MakeInvoiceHandler : IJobHandler<MakeInvoice>
        {
            public Task HandleAsync(MakeInvoice job, JobContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        [JsonSerializable(typeof(MakeInvoice))]
        internal sealed partial class AppJsonA : JsonSerializerContext;

        [JsonSerializable(typeof(Filler))]
        internal sealed partial class AppJsonB : JsonSerializerContext;
        """;

    // A workflow output with no JsonSerializerContext anywhere: the fix scaffolds one.
    private const string OutputNotListedNoContextSource = """
        using System.Threading;
        using System.Threading.Tasks;
        using BackWave.Jobs;
        using BackWave.Pro;

        namespace Acme;

        public sealed record InvoiceResult(string OrderId);

        [Job("make-invoice")]
        public sealed record MakeInvoice(string OrderId) : IWorkflowStep<InvoiceResult>;

        public sealed class MakeInvoiceHandler : IJobHandler<MakeInvoice>
        {
            public Task HandleAsync(MakeInvoice job, JobContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }
        """;

    [Fact]
    public async Task OutputType_AddsJsonSerializableToSingleContext_AndClearsBw0007()
    {
        var outcome = await CodeFixHarness.ApplyAsync(OutputNotListedSource, actions => Assert.Single(actions));

        // The attribute is emitted fully qualified so it binds regardless of the file's usings.
        Assert.Contains("JsonSerializableAttribute(typeof(global::Acme.InvoiceResult))", outcome.FixedSource);
        AssertClearsBw0007(outcome.FixedSource);
    }

    [Fact]
    public async Task SeedType_AddsJsonSerializableToSingleContext_AndClearsBw0007()
    {
        var outcome = await CodeFixHarness.ApplyAsync(SeedNotListedSource, actions => Assert.Single(actions));

        Assert.Contains("JsonSerializableAttribute(typeof(global::Acme.CheckoutSeed))", outcome.FixedSource);
        AssertClearsBw0007(outcome.FixedSource);
    }

    [Fact]
    public async Task SingleContext_ActionTitleNamesTheContextAndType()
    {
        var actions = await CodeFixHarness.RegisterActionsAsync(OutputNotListedSource);

        var action = Assert.Single(actions);
        Assert.Equal("Add [JsonSerializable(typeof(InvoiceResult))] to 'AppJson'", action.Title);
    }

    [Fact]
    public async Task MultipleContexts_SurfaceOneActionPerContextInOrdinalOrder()
    {
        var actions = await CodeFixHarness.RegisterActionsAsync(OutputNotListedMultiContextSource);

        Assert.Equal(2, actions.Count);
        // Ordinal-first over context FQN: AppJsonA before AppJsonB, matching the generator's own tie-break.
        Assert.Equal("Add [JsonSerializable(typeof(InvoiceResult))] to 'AppJsonA'", actions[0].Title);
        Assert.Equal("Add [JsonSerializable(typeof(InvoiceResult))] to 'AppJsonB'", actions[1].Title);
    }

    [Fact]
    public async Task MultipleContexts_ApplyingOrdinalFirst_ClearsBw0007()
    {
        var outcome = await CodeFixHarness.ApplyAsync(OutputNotListedMultiContextSource, actions => actions[0]);

        // The attribute lands on the ordinal-first context, right where AppJsonA is declared.
        Assert.Contains("JsonSerializableAttribute(typeof(global::Acme.InvoiceResult))", outcome.FixedSource);
        Assert.Contains("class AppJsonA", outcome.FixedSource);
        AssertClearsBw0007(outcome.FixedSource);
    }

    [Fact]
    public async Task NoContext_ScaffoldsAContext_AndClearsBw0007()
    {
        var outcome = await CodeFixHarness.ApplyAsync(OutputNotListedNoContextSource, actions => Assert.Single(actions));

        Assert.Contains("class BackWaveWorkflowJsonContext", outcome.FixedSource);
        Assert.Contains("JsonSerializerContext", outcome.FixedSource);
        Assert.Contains("JsonSerializableAttribute(typeof(global::Acme.InvoiceResult))", outcome.FixedSource);
        AssertClearsBw0007(outcome.FixedSource);
    }

    [Fact]
    public async Task NoContext_ActionOffersToCreateAContext()
    {
        var actions = await CodeFixHarness.RegisterActionsAsync(OutputNotListedNoContextSource);

        var action = Assert.Single(actions);
        Assert.Equal("Create a JsonSerializerContext listing 'InvoiceResult'", action.Title);
    }

    private static void AssertClearsBw0007(string fixedSource)
    {
        // Re-run the generator on the fixed source: the offending type now resolves to a listing
        // context, so no BW0007 is reported. (STJ's own generator does not run in this harness, so
        // compilation errors from the unresolved <Context>.Default are expected and not asserted on.)
        var rerun = GeneratorHarness.Run(fixedSource);
        Assert.DoesNotContain(rerun.GeneratorDiagnostics, d => d.Id == "BW0007");
    }
}
