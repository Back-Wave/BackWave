using System.Text.RegularExpressions;

namespace BackWave.SourceGenerators.Tests;

/// <summary>
/// Covers the BW0007 and BW0017 code fix: it adds [JsonSerializable(typeof(T))] for the type the
/// diagnostic names to a JsonSerializerContext (existing single, existing several, or scaffolded),
/// and applying it clears the diagnostic when the generator re-runs on the fixed source.
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

    // A class payload with a complex member, in a project whose only context lists something else:
    // BW0017 at the payload, and the fix lists the payload type.
    private const string ComplexMemberNotListedSource = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using System.Text.Json.Serialization;
        using BackWave.Jobs;

        namespace Acme;

        public sealed record Unrelated(string Value);

        [Job("tag-order")]
        public sealed record TagOrder(string OrderId, List<string> Tags);

        public sealed class TagOrderHandler : IJobHandler<TagOrder>
        {
            public Task HandleAsync(TagOrder job, JobContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        [JsonSerializable(typeof(Unrelated))]
        internal sealed partial class AppJson : JsonSerializerContext;
        """;

    // A method-sugar job with a nested generic parameter and no context anywhere: the fix scaffolds a
    // context that lists the parameter type, because no payload type exists in source to list.
    private const string SugarComplexParameterNoContextSource = """
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using BackWave.Jobs;

        namespace Acme;

        public sealed record Line(string Sku);

        public static class Jobs
        {
            [Job("bucket-lines")]
            public static Task BucketLines(Dictionary<string, List<Line>> buckets) => Task.CompletedTask;
        }
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

        Assert.Contains(
            "internal sealed partial class BackWaveJsonContext : global::System.Text.Json.Serialization.JsonSerializerContext;",
            outcome.FixedSource);
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

    [Fact]
    public async Task ComplexMember_ClassPayload_ListsThePayloadType_AndTheFixedSourceCompiles()
    {
        var outcome = await CodeFixHarness.ApplyAsync(
            ComplexMemberNotListedSource, actions => Assert.Single(actions), diagnosticId: "BW0017");

        Assert.Equal("Add [JsonSerializable(typeof(TagOrder))] to 'AppJson'", Assert.Single(outcome.Actions).Title);
        Assert.Contains("JsonSerializableAttribute(typeof(global::Acme.TagOrder))", outcome.FixedSource);
        AssertFixedSourceCompiles(outcome.FixedSource);
    }

    [Fact]
    public async Task ComplexMember_SugarJob_ScaffoldsAContextThatListsTheParameterType_AndTheFixedSourceCompiles()
    {
        var outcome = await CodeFixHarness.ApplyAsync(
            SugarComplexParameterNoContextSource, actions => Assert.Single(actions), diagnosticId: "BW0017");

        // The title drops every namespace, type arguments included; the attribute keeps them all.
        Assert.Equal(
            "Create a JsonSerializerContext listing 'Dictionary<string, List<Line>>'",
            Assert.Single(outcome.Actions).Title);
        Assert.Contains("class BackWaveJsonContext", outcome.FixedSource);
        Assert.Contains(
            "JsonSerializableAttribute(typeof(global::System.Collections.Generic.Dictionary<string, " +
            "global::System.Collections.Generic.List<global::Acme.Line>>))",
            outcome.FixedSource);
        AssertFixedSourceCompiles(outcome.FixedSource);
    }

    // Contexts that the generated codec cannot use. A listing on any of them gives BW0013, not a fix.
    private const string UnusableContexts = """

        public partial class Outer
        {
            [JsonSerializable(typeof(Unrelated))]
            private sealed partial class HiddenJson : JsonSerializerContext;

            [JsonSerializable(typeof(Unrelated))]
            protected sealed partial class GuardedJson : JsonSerializerContext;
        }

        [JsonSerializable(typeof(Unrelated))]
        file sealed partial class LocalJson : JsonSerializerContext;

        file partial class FileOuter
        {
            [JsonSerializable(typeof(Unrelated))]
            internal sealed partial class NestedJson : JsonSerializerContext;
        }

        [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Serialization)]
        [JsonSerializable(typeof(Unrelated))]
        internal sealed partial class WriteOnlyJson : JsonSerializerContext;
        """;

    [Fact]
    public async Task UnusableContexts_AreNotOffered()
    {
        var actions = await CodeFixHarness.RegisterActionsAsync(ComplexMemberNotListedSource + UnusableContexts, "BW0017");

        Assert.Equal("Add [JsonSerializable(typeof(TagOrder))] to 'AppJson'", Assert.Single(actions).Title);
    }

    [Fact]
    public async Task ComplexMember_ContextWithAnySerializationOnlyListing_IsNotOffered()
    {
        // The generator rejects a job payload context with any serialization-only listing, so the fix skips it too.
        var actions = await CodeFixHarness.RegisterActionsAsync(ComplexMemberNotListedSource + """

            [JsonSerializable(typeof(Unrelated[]), GenerationMode = JsonSourceGenerationMode.Serialization)]
            [JsonSerializable(typeof(Unrelated))]
            internal sealed partial class MixedJson : JsonSerializerContext;
            """, "BW0017");

        Assert.Equal("Add [JsonSerializable(typeof(TagOrder))] to 'AppJson'", Assert.Single(actions).Title);
    }

    [Fact]
    public async Task OnlyUnusableContexts_ScaffoldsAContext_AndTheFixedSourceCompiles()
    {
        var source = ComplexMemberNotListedSource.Replace(
            "[JsonSerializable(typeof(Unrelated))]\ninternal sealed partial class AppJson : JsonSerializerContext;", "");
        Assert.DoesNotContain("AppJson", source);

        // The STJ generator cannot compile a context that is, or is nested in, a file-local type, so the
        // compile check leaves both out.
        var compilable = UnusableContexts
            .Replace("[JsonSerializable(typeof(Unrelated))]\nfile sealed partial class LocalJson : JsonSerializerContext;", "")
            .Replace(
                "file partial class FileOuter\n{\n    [JsonSerializable(typeof(Unrelated))]\n" +
                "    internal sealed partial class NestedJson : JsonSerializerContext;\n}", "");
        Assert.DoesNotContain("LocalJson", compilable);
        Assert.DoesNotContain("FileOuter", compilable);

        var outcome = await CodeFixHarness.ApplyAsync(
            source + compilable, actions => Assert.Single(actions), diagnosticId: "BW0017");

        Assert.Equal("Create a JsonSerializerContext listing 'TagOrder'", Assert.Single(outcome.Actions).Title);
        AssertFixedSourceCompiles(outcome.FixedSource);
    }

    [Fact]
    public async Task Scaffold_WhenTheNamespaceHasABackWaveJsonContext_UsesAFreeName_AndTheFixedSourceCompiles()
    {
        // The existing context is not a target, so the fix scaffolds. A class with the same name would merge into it.
        var source = ComplexMemberNotListedSource.Replace(
            "[JsonSerializable(typeof(Unrelated))]\ninternal sealed partial class AppJson : JsonSerializerContext;",
            "[JsonSerializable(typeof(Unrelated), GenerationMode = JsonSourceGenerationMode.Serialization)]\n" +
            "internal sealed partial class BackWaveJsonContext : JsonSerializerContext;");
        Assert.DoesNotContain("AppJson", source);

        var outcome = await CodeFixHarness.ApplyAsync(source, actions => Assert.Single(actions), diagnosticId: "BW0017");

        Assert.Contains(
            "internal sealed partial class BackWaveJsonContext2 : global::System.Text.Json.Serialization.JsonSerializerContext;",
            outcome.FixedSource);
        AssertFixedSourceCompiles(outcome.FixedSource);
    }

    [Fact]
    public async Task EachDiagnosticId_GetsItsOwnEquivalenceKey()
    {
        var complex = await CodeFixHarness.RegisterActionsAsync(ComplexMemberNotListedSource, "BW0017");
        var workflow = await CodeFixHarness.RegisterActionsAsync(OutputNotListedSource, "BW0007");
        var scaffold = await CodeFixHarness.RegisterActionsAsync(SugarComplexParameterNoContextSource, "BW0017");

        // Fix-all groups by equivalence key, so a key shared across IDs would batch unrelated fixes together.
        Assert.Equal("BW0017_AddTo_global::Acme.AppJson", Assert.Single(complex).EquivalenceKey);
        Assert.Equal("BW0007_AddTo_global::Acme.AppJson", Assert.Single(workflow).EquivalenceKey);
        Assert.Equal("BW0017_ScaffoldContext", Assert.Single(scaffold).EquivalenceKey);
    }

    // Two class payloads with complex members, neither listed: one BW0017 per payload type. The trailing
    // contexts line decides the fix: one context, several, or none.
    private const string TwoComplexMembersNotListedSource = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using System.Text.Json.Serialization;
        using BackWave.Jobs;

        namespace Acme;

        public sealed record Unrelated(string Value);

        [Job("tag-order")]
        public sealed record TagOrder(string OrderId, List<string> Tags);

        public sealed class TagOrderHandler : IJobHandler<TagOrder>
        {
            public Task HandleAsync(TagOrder job, JobContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        [Job("count-lines")]
        public sealed record CountLines(string OrderId, Dictionary<string, int> Counts);

        public sealed class CountLinesHandler : IJobHandler<CountLines>
        {
            public Task HandleAsync(CountLines job, JobContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        """;

    private const string OneContext = """

        [JsonSerializable(typeof(Unrelated))]
        internal sealed partial class AppJson : JsonSerializerContext;
        """;

    private const string TwoContexts = """

        [JsonSerializable(typeof(Unrelated))]
        internal sealed partial class AppJsonA : JsonSerializerContext;

        [JsonSerializable(typeof(Unrelated))]
        internal sealed partial class AppJsonB : JsonSerializerContext;
        """;

    [Fact]
    public async Task FixAll_TwoUnlistedTypes_OneContext_ListsBothOnTheContext_AndTheFixedSourceCompiles()
    {
        var fixedSource = await CodeFixHarness.FixAllAsync(
            TwoComplexMembersNotListedSource + OneContext, "BW0017", "BW0017_AddTo_global::Acme.AppJson");

        Assert.Contains("JsonSerializableAttribute(typeof(global::Acme.TagOrder))", fixedSource);
        Assert.Contains("JsonSerializableAttribute(typeof(global::Acme.CountLines))", fixedSource);
        AssertFixedSourceCompiles(fixedSource);
    }

    [Fact]
    public async Task FixAll_TwoUnlistedTypes_NoContext_ScaffoldsOneContextListingBoth_AndTheFixedSourceCompiles()
    {
        var fixedSource = await CodeFixHarness.FixAllAsync(
            TwoComplexMembersNotListedSource, "BW0017", "BW0017_ScaffoldContext");

        Assert.Single(Regex.Matches(fixedSource, "class BackWaveJsonContext"));
        Assert.Contains("JsonSerializableAttribute(typeof(global::Acme.TagOrder))", fixedSource);
        Assert.Contains("JsonSerializableAttribute(typeof(global::Acme.CountLines))", fixedSource);
        AssertFixedSourceCompiles(fixedSource);
    }

    [Fact]
    public async Task ComplexMember_MultipleContexts_SurfaceOneActionPerContextInOrdinalOrder()
    {
        var actions = await CodeFixHarness.RegisterActionsAsync(ComplexMemberNotListedSource + TwoContexts, "BW0017");

        Assert.Equal(
            ["Add [JsonSerializable(typeof(TagOrder))] to 'AppJson'",
             "Add [JsonSerializable(typeof(TagOrder))] to 'AppJsonA'",
             "Add [JsonSerializable(typeof(TagOrder))] to 'AppJsonB'"],
            actions.Select(a => a.Title));
    }

    [Fact]
    public async Task FixAll_TwoUnlistedTypes_TwoContexts_ListsBothOnTheChosenContext_AndTheFixedSourceCompiles()
    {
        var fixedSource = await CodeFixHarness.FixAllAsync(
            TwoComplexMembersNotListedSource + TwoContexts, "BW0017", "BW0017_AddTo_global::Acme.AppJsonB");

        // Both listings land on AppJsonB, after its existing one and before its declaration.
        var afterAppJsonA = fixedSource[fixedSource.IndexOf("class AppJsonA", StringComparison.Ordinal)..];
        Assert.Contains("JsonSerializableAttribute(typeof(global::Acme.TagOrder))", afterAppJsonA);
        Assert.Contains("JsonSerializableAttribute(typeof(global::Acme.CountLines))", afterAppJsonA);
        Assert.Equal(2, Regex.Matches(fixedSource, "JsonSerializableAttribute").Count);
        AssertFixedSourceCompiles(fixedSource);
    }

    [Fact]
    public async Task FixAll_PerDiagnosticId_ThenTheOther_ListsEveryTypeOnTheSameContext()
    {
        // A workflow output (BW0007) and two complex payloads (BW0017) that all need the one context.
        var source = TwoComplexMembersNotListedSource + """
            [Job("make-invoice")]
            public sealed record MakeInvoice(string OrderId) : BackWave.Pro.IWorkflowStep<InvoiceResult>;

            public sealed record InvoiceResult(string OrderId);

            public sealed class MakeInvoiceHandler : IJobHandler<MakeInvoice>
            {
                public Task HandleAsync(MakeInvoice job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(MakeInvoice))]
            internal sealed partial class AppJson : JsonSerializerContext;
            """;

        // Each fix-all only takes its own ID, so the BW0007 type is not listed by the BW0017 pass.
        var afterComplex = await CodeFixHarness.FixAllAsync(source, "BW0017", "BW0017_AddTo_global::Acme.AppJson");
        Assert.DoesNotContain("typeof(global::Acme.InvoiceResult)", afterComplex);

        var fixedSource = await CodeFixHarness.FixAllAsync(afterComplex, "BW0007", "BW0007_AddTo_global::Acme.AppJson");
        Assert.Contains("JsonSerializableAttribute(typeof(global::Acme.TagOrder))", fixedSource);
        Assert.Contains("JsonSerializableAttribute(typeof(global::Acme.CountLines))", fixedSource);
        Assert.Contains("JsonSerializableAttribute(typeof(global::Acme.InvoiceResult))", fixedSource);
        AssertClearsBw0007(fixedSource);
        AssertFixedSourceCompiles(fixedSource);
    }

    /// <summary>
    /// Runs the BackWave and STJ generators over the fixed source: the generator reports nothing, the job is
    /// emitted, and the output compiles with no warning.
    /// </summary>
    private static void AssertFixedSourceCompiles(string fixedSource)
    {
        var rerun = GeneratorHarness.Run(fixedSource, withJsonGenerator: true);
        Assert.Empty(rerun.GeneratorDiagnostics);
        Assert.Contains(rerun.GeneratedSources.Values, source => source.Contains("global::System.Text.Json.JsonSerializer.Serialize(writer"));
        DelegatedMemberTests.AssertNoDiagnostics(rerun.CompilationDiagnostics);
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
