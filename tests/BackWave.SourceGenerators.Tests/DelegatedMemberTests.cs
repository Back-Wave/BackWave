using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace BackWave.SourceGenerators.Tests;

/// <summary>
/// Payload members outside the scalar set. The generated codec hands each one to System.Text.Json through the
/// consumer's JsonSerializerContext, and keeps every scalar member. Most tests here run the real STJ generator,
/// so a clean compile proves that the emitted code and the STJ metadata agree.
/// </summary>
public class DelegatedMemberTests
{
    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Text.Json.Serialization;
        using System.Threading;
        using System.Threading.Tasks;
        using BackWave.Jobs;

        """;

    /// <summary>The shape that the design prototype proved, with only the payload type listed.</summary>
    private const string ShipOrderSource = Usings + """
        namespace Acme.Orders;

        public enum Priority { Low, High }

        public sealed record Address(string Street, Priority Zone);

        [Job("ship-order")]
        public sealed record ShipOrder(
            Guid Id,
            Priority Priority,
            List<string> Skus,
            Dictionary<string, int> Counts,
            Address? Ship,
            IReadOnlyList<Address> History,
            int[] Bins);

        public sealed class ShipOrderHandler : IJobHandler<ShipOrder>
        {
            public Task HandleAsync(ShipOrder job, JobContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
        [JsonSerializable(typeof(ShipOrder))]
        internal sealed partial class OrdersJson : JsonSerializerContext;
        """;

    private const string ShipOrderHint = "BackWave.Acme_Orders_ShipOrder.a825b1f6.g.cs";

    [Fact]
    public void ComplexPayload_MatchesSnapshot()
    {
        var run = GeneratorHarness.Run(ShipOrderSource);

        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Snapshots", ShipOrderHint));
        Assert.Equal(expected.ReplaceLineEndings(), run.GeneratedSources[ShipOrderHint].ReplaceLineEndings());
    }

    [Fact]
    public void ComplexPayload_ListedOnlyByItsPayloadType_CompilesWithNoDiagnostic()
    {
        var run = GeneratorHarness.Run(ShipOrderSource, withJsonGenerator: true);

        Assert.Empty(run.GeneratorDiagnostics);
        AssertNoDiagnostics(run.CompilationDiagnostics);
    }

    [Fact]
    public void ComplexPayload_UnrelatedEdit_ReusesCachedGenerationOutputs()
        => GeneratorTests.AssertUnrelatedEditReusesCachedOutputs(ShipOrderSource);

    private const string ShipJobFile = Usings + """
        namespace Acme.Orders;

        public sealed record Address(string Street);

        [Job("ship-order")]
        public sealed record ShipOrder(Guid Id, List<string> Skus, Address? Ship);

        public sealed class ShipOrderHandler : IJobHandler<ShipOrder>
        {
            public Task HandleAsync(ShipOrder job, JobContext context, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }
        """;

    private const string ShipContextFile = Usings + """
        namespace Acme.Orders;

        [JsonSerializable(typeof(ShipOrder))]
        internal sealed partial class OrdersJson : JsonSerializerContext
        {
            private static void Touch()
            {
            }
        }
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ComplexPayload_EditInsideAMethodBody_ReParsesToEqualModels(bool editTheJobFile)
    {
        // The edit re-runs the parse transforms of the edited file. Each new model must be equal to the cached
        // one, so the steps after the parse stay cached.
        var (driver, compilation) = RunIncremental(ShipJobFile, ShipContextFile);
        var (file, body) = editTheJobFile
            ? (0, "return Task.CompletedTask;")
            : (1, "private static void Touch()\n    {");
        var edited = EditTree(compilation, file, body, body + " // edited");

        var result = driver.RunGenerators(edited).GetRunResult().Results[0];

        var reParsed = editTheJobFile ? BackWaveGenerator.ParseStep : BackWaveGenerator.JsonContextsStep;
        Assert.Contains(StepReasons(result, reParsed), reason => reason == IncrementalStepRunReason.Unchanged);
        foreach (var stepName in new[]
                 {
                     BackWaveGenerator.ParseStep, BackWaveGenerator.JsonContextsStep, BackWaveGenerator.ModelsStep,
                     BackWaveGenerator.EmitInputStep,
                 })
        {
            Assert.All(StepReasons(result, stepName), reason => Assert.True(
                reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
                $"{stepName} changed ({reason}) after an edit inside a method body"));
        }
    }

    [Fact]
    public void ComplexPayload_AddingAJsonSerializableListing_ModifiesTheContextModel()
    {
        var (driver, compilation) = RunIncremental(ShipJobFile, ShipContextFile);
        var edited = EditTree(
            compilation, 1, "[JsonSerializable(typeof(ShipOrder))]",
            "[JsonSerializable(typeof(ShipOrder))]\n[JsonSerializable(typeof(Address))]");

        var result = driver.RunGenerators(edited).GetRunResult().Results[0];

        Assert.Contains(
            StepReasons(result, BackWaveGenerator.JsonContextsStep),
            reason => reason == IncrementalStepRunReason.Modified);
        Assert.Contains(
            StepReasons(result, BackWaveGenerator.EmitInputStep),
            reason => reason == IncrementalStepRunReason.Modified);
    }

    private static (GeneratorDriver Driver, Compilation Compilation) RunIncremental(params string[] files)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create(
            "Incremental",
            files.Select(file => CSharpSyntaxTree.ParseText(file, parseOptions)),
            GeneratorHarness.MetadataReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new BackWaveGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        return (driver.RunGenerators(compilation), compilation);
    }

    private static Compilation EditTree(Compilation compilation, int file, string find, string replace)
    {
        var tree = compilation.SyntaxTrees.ElementAt(file);
        var text = tree.GetText().ToString().ReplaceLineEndings("\n");
        Assert.Contains(find, text);
        return compilation.ReplaceSyntaxTree(tree, tree.WithChangedText(SourceText.From(text.Replace(find, replace))));
    }

    private static IEnumerable<IncrementalStepRunReason> StepReasons(GeneratorRunResult result, string stepName)
        => result.TrackedSteps[stepName].SelectMany(step => step.Outputs).Select(output => output.Reason);

    [Fact]
    public void ComplexPayload_RoundTripsAndKeepsTheScalarFormat()
    {
        var probe = CompileProbe(ShipOrderSource + """

            public static class Probe
            {
                public static string Write() => Wire(new ShipOrder(
                    new Guid("11111111-1111-1111-1111-111111111111"),
                    Priority.High,
                    new List<string> { "a", "b" },
                    new Dictionary<string, int> { ["x"] = 1 },
                    new Address("Main St", Priority.Low),
                    new[] { new Address("Old Rd", Priority.High) },
                    new[] { 1, 2, 3 }));

                public static string Reread(string json)
                    => Wire(ShipOrderWire.Deserialize(System.Text.Encoding.UTF8.GetBytes(json)));

                private static string Wire(ShipOrder value)
                    => System.Text.Encoding.UTF8.GetString(ShipOrderWire.Serialize(value));
            }
            """);

        var json = probe.Call("Write");

        // Top-level members belong to the generated codec: original names, an enum as its name. Inside a
        // delegated member the context options apply: snake case, and an enum as a number.
        Assert.Equal(
            """{"Id":"11111111-1111-1111-1111-111111111111","Priority":"High","Skus":["a","b"],"Counts":{"x":1},"Ship":""" +
            """{"street":"Main St","zone":0},"History":[{"street":"Old Rd","zone":1}],"Bins":[1,2,3]}""",
            json);
        Assert.Equal(json, probe.Call("Reread", json));
    }

    [Fact]
    public void ComplexPayload_DecodesTolerantly()
    {
        var probe = CompileProbe(ShipOrderSource + """

            public static class Probe
            {
                public static string Reread(string json)
                    => System.Text.Encoding.UTF8.GetString(
                        ShipOrderWire.Serialize(ShipOrderWire.Deserialize(System.Text.Encoding.UTF8.GetBytes(json))));
            }
            """);

        // An unknown nested property is skipped. A JSON null and a missing complex member both read as null.
        var reread = probe.Call(
            "Reread",
            """{"Id":"11111111-1111-1111-1111-111111111111","Priority":"Low","Ship":{"street":"S","zone":1,"unknown":{"deep":[1]}},"Skus":null,"Extra":[1]}""");

        Assert.Equal(
            """{"Id":"11111111-1111-1111-1111-111111111111","Priority":"Low","Skus":null,"Counts":null,"Ship":""" +
            """{"street":"S","zone":1},"History":null,"Bins":null}""",
            reread);
    }

    /// <summary>
    /// The member shapes where the emitted type strings can go wrong: nested generics, a nested type, the three
    /// nullable reference positions, a struct with and without Nullable&lt;T&gt;, a constructor default, and an
    /// init-only property.
    /// </summary>
    private const string EdgeCaseSource = Usings + """
        namespace Acme.Edges;

        public static class Outer
        {
            public sealed record Inner(int Value);
        }

        public struct Point
        {
            public int X { get; set; }
        }

        [Job("edge-cases")]
        public sealed record EdgeCases(
            Dictionary<string, List<Outer.Inner>> Nested,
            List<string>? MaybeList,
            List<string?> MaybeItems,
            Point? MaybePoint,
            Point At,
            TimeSpan Delay,
            List<int>? Defaulted = null)
        {
            public Uri? Link { get; init; }

            public List<string> Extra { get; init; } = new();
        }

        public sealed class EdgeCasesHandler : IJobHandler<EdgeCases>
        {
            public Task HandleAsync(EdgeCases job, JobContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        [JsonSerializable(typeof(EdgeCases))]
        internal sealed partial class EdgeJson : JsonSerializerContext;

        public static class Probe
        {
            public static string Full() => Wire(new EdgeCases(
                new Dictionary<string, List<Outer.Inner>> { ["k"] = new() { new Outer.Inner(7) } },
                new List<string> { "m" },
                new List<string?> { null, "n" },
                new Point { X = 3 },
                new Point { X = 4 },
                TimeSpan.FromSeconds(90),
                new List<int> { 5 })
            {
                Link = new Uri("https://example.com/a"),
                Extra = new List<string> { "e" },
            });

            public static string Empty() => Wire(new EdgeCases(new(), null, new(), null, default, TimeSpan.Zero));

            public static string Reread(string json)
                => Wire(EdgeCasesWire.Deserialize(System.Text.Encoding.UTF8.GetBytes(json)));

            private static string Wire(EdgeCases value)
                => System.Text.Encoding.UTF8.GetString(EdgeCasesWire.Serialize(value));
        }
        """;

    [Fact]
    public void EdgeCaseMemberShapes_CompileWithNoDiagnostic()
    {
        var run = GeneratorHarness.Run(EdgeCaseSource, withJsonGenerator: true);

        Assert.Empty(run.GeneratorDiagnostics);
        AssertNoDiagnostics(run.CompilationDiagnostics);
    }

    [Fact]
    public void EdgeCaseMemberShapes_RoundTrip()
    {
        var probe = CompileProbe(EdgeCaseSource);

        var full = probe.Call("Full");
        Assert.Equal(
            """{"Nested":{"k":[{"Value":7}]},"MaybeList":["m"],"MaybeItems":[null,"n"],"MaybePoint":{"X":3},"At":""" +
            """{"X":4},"Delay":"00:01:30","Defaulted":[5],"Link":"https://example.com/a","Extra":["e"]}""",
            full);
        Assert.Equal(full, probe.Call("Reread", full));

        var empty = probe.Call("Empty");
        Assert.Equal(
            """{"Nested":{},"MaybeList":null,"MaybeItems":[],"MaybePoint":null,"At":{"X":0},"Delay":"00:00:00","Defaulted":""" +
            """null,"Link":null,"Extra":[]}""",
            empty);
        Assert.Equal(empty, probe.Call("Reread", empty));
    }

    [Fact]
    public void EdgeCaseMemberShapes_MissingMembersReadAsTheirDefaults()
    {
        var probe = CompileProbe(EdgeCaseSource);

        // A missing non-nullable struct reads as default. A JSON null for it is tolerated the same way.
        Assert.Equal(
            """{"Nested":null,"MaybeList":null,"MaybeItems":null,"MaybePoint":null,"At":{"X":0},"Delay":"00:00:00","Defaulted":""" +
            """null,"Link":null,"Extra":null}""",
            probe.Call("Reread", """{"At":null}"""));
    }

    private const string ShipSource = Usings + """
        namespace Acme;

        public sealed record Addr(string? Street, string? City);

        [Job("ship")]
        public sealed record Ship(string Id, Addr? Dest);

        public sealed class ShipHandler : IJobHandler<Ship>
        {
            public Task HandleAsync(Ship job, JobContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        """;

    [Theory]
    [InlineData("A_JobsContext", "B_ApiContext")]
    [InlineData("B_JobsContext", "A_ApiContext")]
    public void MemberTypeListedOnlyInOtherContexts_BindsToThePayloadContext(string jobsContext, string apiContext)
    {
        // The listings of Addr do not count, in either order. If they did, a queued job written while only the
        // payload context served Dest would decode with every property null once the camelCase context took over.
        var run = GeneratorHarness.Run(ShipSource + $$"""
            [JsonSerializable(typeof(Ship))]
            internal sealed partial class {{jobsContext}} : JsonSerializerContext;

            [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
            [JsonSerializable(typeof(Addr))]
            internal sealed partial class {{apiContext}} : JsonSerializerContext;

            [JsonSerializable(typeof(Addr))]
            internal sealed partial class OtherApiContext : JsonSerializerContext;
            """, withJsonGenerator: true);

        Assert.Empty(run.GeneratorDiagnostics);
        AssertNoDiagnostics(run.CompilationDiagnostics);
        var source = run.GeneratedSources.Single(s => s.Key.Contains("Acme_Ship")).Value;
        Assert.Contains($"global::Acme.{jobsContext}.Default.GetTypeInfo(", source);
        Assert.DoesNotContain(apiContext, source);
        Assert.DoesNotContain("OtherApiContext", source);
    }

    [Fact]
    public void PayloadAndMemberTypeListedInTheSameContext_BindToThatContext()
    {
        var run = GeneratorHarness.Run(ShipSource + """
            [JsonSerializable(typeof(Ship))]
            [JsonSerializable(typeof(Addr))]
            internal sealed partial class JobsJson : JsonSerializerContext;
            """, withJsonGenerator: true);

        Assert.Empty(run.GeneratorDiagnostics);
        AssertNoDiagnostics(run.CompilationDiagnostics);
        var source = run.GeneratedSources.Single(s => s.Key.Contains("Acme_Ship")).Value;
        Assert.Contains("global::Acme.JobsJson.Default.GetTypeInfo(", source);
    }

    [Fact]
    public void MemberTypeListedInThePayloadContextAndAnother_BindsToThePayloadContext()
    {
        // The binding does not depend on the other listing: remove it and nothing changes.
        var run = GeneratorHarness.Run(ShipSource + """
            [JsonSerializable(typeof(Ship))]
            [JsonSerializable(typeof(Addr))]
            internal sealed partial class B_JobsJson : JsonSerializerContext;

            [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
            [JsonSerializable(typeof(Addr))]
            internal sealed partial class A_ApiJson : JsonSerializerContext;
            """, withJsonGenerator: true);

        Assert.Empty(run.GeneratorDiagnostics);
        AssertNoDiagnostics(run.CompilationDiagnostics);
        var source = run.GeneratedSources.Single(s => s.Key.Contains("Acme_Ship")).Value;
        Assert.Contains("global::Acme.B_JobsJson.Default.GetTypeInfo(", source);
        Assert.DoesNotContain("A_ApiJson", source);
    }

    [Fact]
    public void OnlyTheMemberTypeListed_IsBW0017ForThePayload()
    {
        var run = GeneratorHarness.Run(ShipSource + """
            [JsonSerializable(typeof(Addr))]
            internal sealed partial class ApiJson : JsonSerializerContext;
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0017", diagnostic.Id);
        Assert.StartsWith("[Job(\"ship\")]", GeneratorTests.SourceAt(run, diagnostic));
        Assert.Equal("global::Acme.Ship", diagnostic.Properties["TypeFqn"]);
        Assert.DoesNotContain(run.GeneratedSources.Keys, k => k.Contains("Acme_Ship"));
    }

    /// <summary>A converter that throws if it runs, so a passing round trip proves that it did not.</summary>
    private const string ShipConverterSource = Usings + """
        using System.Text.Json;

        namespace Acme;

        public sealed record Addr(string? Street, string? City);

        public sealed class ShipConverter : JsonConverter<Ship>
        {
            public override Ship Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                => throw new NotSupportedException("ShipConverter ran");

            public override void Write(Utf8JsonWriter writer, Ship value, JsonSerializerOptions options)
                => throw new NotSupportedException("ShipConverter ran");
        }

        public sealed class ShipConverterAttribute() : JsonConverterAttribute(typeof(ShipConverter));

        public sealed class ShipHandler : IJobHandler<Ship>
        {
            public Task HandleAsync(Ship job, JobContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        """;

    [Theory]
    [InlineData("[JsonConverter(typeof(ShipConverter))]", "")]
    [InlineData("[JsonConverter(typeof(ShipConverter))]", "[JsonSerializable(typeof(Addr))]")]
    [InlineData("[ShipConverter]", "")]
    public void TypeLevelJsonConverterOnAPayloadWithDelegatedMembers_IsBW0016_AndTheJobIsNotEmitted(
        string converter, string memberListing)
    {
        // Without the error, STJ generates no metadata for Addr under a converted payload, so the codec throws
        // "'Acme.BJobs' has no metadata for type 'Acme.Addr'" on the first job. Listing Addr would hide that,
        // but the converter would still never run.
        var run = GeneratorHarness.Run(ShipConverterSource + $$"""
            [Job("ship")]
            {{converter}}
            public sealed record Ship(string Id, Addr? Dest);

            [JsonSerializable(typeof(Ship))]
            {{memberListing}}
            internal sealed partial class BJobs : JsonSerializerContext;
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0016", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.EndsWith("public sealed record Ship(string Id, Addr? Dest);", GeneratorTests.SourceAt(run, diagnostic));
        Assert.Equal(
            "Job payload 'Ship' has members that the generated codec hands to System.Text.Json, so it must not have " +
            "a type-level [JsonConverter] - the generated codec writes the members itself, so the converter never " +
            "runs. Remove the converter, or register this job by hand with JobRegistration.Create.",
            diagnostic.GetMessage());
        Assert.DoesNotContain(run.GeneratedSources.Keys, k => k.Contains("Acme_Ship"));
    }

    [Theory]
    [InlineData("[JsonConverter(typeof(PlainConverter))]")]
    [InlineData("[PlainConverter]")]
    public void TypeLevelJsonConverterOnAScalarOnlyPayload_IsABW0019Warning_AndTheJobIsStillEmitted(string converter)
    {
        // The converter has no effect here either, but STJ metadata is not needed, so the job still round-trips.
        var run = GeneratorHarness.Run(Usings + $$"""
            using System.Text.Json;

            namespace Acme;

            [Job("plain")]
            {{converter}}
            public sealed record Plain(string Id, int Count);

            public sealed class PlainConverter : JsonConverter<Plain>
            {
                public override Plain Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                    => throw new NotSupportedException();

                public override void Write(Utf8JsonWriter writer, Plain value, JsonSerializerOptions options)
                    => throw new NotSupportedException();
            }

            public sealed class PlainConverterAttribute() : JsonConverterAttribute(typeof(PlainConverter));

            public sealed class PlainHandler : IJobHandler<Plain>
            {
                public Task HandleAsync(Plain job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0019", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.EndsWith("public sealed record Plain(string Id, int Count);", GeneratorTests.SourceAt(run, diagnostic));
        Assert.Equal(
            "Job payload 'Plain' has a type-level [JsonConverter], which has no effect - the generated codec writes " +
            "the members itself, so the converter never runs. Remove the converter, or register this job by hand " +
            "with JobRegistration.Create.",
            diagnostic.GetMessage());
        Assert.Contains(run.GeneratedSources.Keys, k => k.Contains("Acme_Plain"));
        AssertNoDiagnostics(run.CompilationDiagnostics);
    }

    [Fact]
    public void PayloadConverterInTheContextOptions_StillGetsMemberMetadata_AndNeverRuns()
    {
        // STJ cannot tell at build time which type an options converter handles, so it still generates
        // metadata for Ship's member types, and the codec works. The codec writes the members itself, so the
        // converter is never asked for Ship.
        var probe = CompileProbe(ShipConverterSource + """
            [Job("ship")]
            public sealed record Ship(string Id, Addr? Dest);

            [JsonSourceGenerationOptions(Converters = new[] { typeof(ShipConverter) })]
            [JsonSerializable(typeof(Ship))]
            internal sealed partial class BJobs : JsonSerializerContext;

            public static class Probe
            {
                public static string Write()
                    => System.Text.Encoding.UTF8.GetString(ShipWire.Serialize(new Ship("s-1", new Addr("Main", "Town"))));

                public static string Read(string json)
                    => ShipWire.Deserialize(System.Text.Encoding.UTF8.GetBytes(json)).ToString();
            }
            """);

        Assert.Equal("""{"Id":"s-1","Dest":{"Street":"Main","City":"Town"}}""", probe.Call("Write"));
        Assert.Equal(
            "Ship { Id = s-1, Dest = Addr { Street = Main, City = Town } }",
            probe.Call("Read", """{"Id":"s-1","Dest":{"Street":"Main","City":"Town"}}"""));
    }

    [Fact]
    public void NamingPolicyInTheContextOptions_RenamesInsideTheMember_NotThePayloadMembers()
    {
        // The codec writes the payload object itself, so the policy reaches only the member value.
        var probe = CompileProbe(ShipConverterSource + """
            [Job("ship")]
            public sealed record Ship(string Id, Addr? Dest);

            [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
            [JsonSerializable(typeof(Ship))]
            internal sealed partial class BJobs : JsonSerializerContext;

            public static class Probe
            {
                public static string Write()
                    => System.Text.Encoding.UTF8.GetString(ShipWire.Serialize(new Ship("s-1", new Addr("Main", "Town"))));
            }
            """);

        Assert.Equal("""{"Id":"s-1","Dest":{"street":"Main","city":"Town"}}""", probe.Call("Write"));
    }

    private const string TaggedJobSource = Usings + """
        namespace Acme;

        [Job("tagged")]
        public sealed record Tagged(string Id, List<string> Tags);

        public sealed class TaggedHandler : IJobHandler<Tagged>
        {
            public Task HandleAsync(Tagged job, JobContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        """;

    [Fact]
    public void MemberTypeListedInTwoContexts_WithThePayloadUnlisted_IsBW0017ForThePayload()
    {
        var run = GeneratorHarness.Run(TaggedJobSource + """
            [JsonSerializable(typeof(List<string>))]
            internal sealed partial class BJson : JsonSerializerContext;

            [JsonSerializable(typeof(List<string>))]
            internal sealed partial class AJson : JsonSerializerContext;
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0017", diagnostic.Id);
        Assert.Equal("global::Acme.Tagged", diagnostic.Properties["TypeFqn"]);
        Assert.DoesNotContain(run.GeneratedSources.Keys, k => k.Contains("Acme_Tagged"));
    }

    [Fact]
    public void MethodSugarMemberTypeListedInTwoContexts_IsBW0012AtTheMember()
    {
        var run = GeneratorHarness.Run(SugarSource + """
            [JsonSerializable(typeof(List<string>))]
            internal sealed partial class MailJson : JsonSerializerContext;

            [JsonSerializable(typeof(List<string>))]
            internal sealed partial class ApiJson : JsonSerializerContext;
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0012", diagnostic.Id);
        Assert.Equal("recipients", GeneratorTests.SourceAt(run, diagnostic));
        Assert.Equal(
            "Type 'System.Collections.Generic.List<string>' (member 'recipients' of job payload 'SendBatch') is " +
            "listed in more than one JsonSerializerContext: Acme.ApiJson, Acme.MailJson. The contexts can serialize " +
            "it differently, so BackWave does not pick one - list the type in only one JsonSerializerContext, or " +
            "put [Job] on a payload record and list that record in one JsonSerializerContext, or register this " +
            "job by hand with JobRegistration.Create.",
            diagnostic.GetMessage());
    }

    [Fact]
    public void MethodSugar_TwoMembersOfATypeListedInTwoContexts_GiveOneBW0012AtTheFirstMember()
    {
        var run = GeneratorHarness.Run(Usings + """
            namespace Acme;

            public sealed record Address(string Street);

            public static class Shipper
            {
                [Job("ship")]
                public static Task ShipAsync(Address from, Address to) => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Address))]
            internal sealed partial class MailJson : JsonSerializerContext;

            [JsonSerializable(typeof(Address))]
            internal sealed partial class ApiJson : JsonSerializerContext;
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0012", diagnostic.Id);
        Assert.Equal("from", GeneratorTests.SourceAt(run, diagnostic));
    }

    [Fact]
    public void MethodSugar_TwoMemberTypesListedOnlyInOneInaccessibleContext_GiveOneBW0013()
    {
        var run = GeneratorHarness.Run(Usings + """
            namespace Acme;

            public sealed record Address(string Street);

            public sealed record Parcel(int Grams);

            public static class Shipper
            {
                [Job("ship")]
                public static Task ShipAsync(Address to, Parcel parcel) => Task.CompletedTask;
            }

            public partial class Outer
            {
                [JsonSerializable(typeof(Address))]
                [JsonSerializable(typeof(Parcel))]
                private sealed partial class HiddenJson : JsonSerializerContext;
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0013", diagnostic.Id);
        Assert.Equal("to", GeneratorTests.SourceAt(run, diagnostic));
    }

    [Fact]
    public void PayloadTypeListedInTwoContexts_IsBW0012_AndTheJobIsNotEmitted()
    {
        var run = GeneratorHarness.Run(TaggedJobSource + """
            [JsonSerializable(typeof(Tagged))]
            internal sealed partial class JobsJson : JsonSerializerContext;

            [JsonSerializable(typeof(Tagged))]
            [JsonSerializable(typeof(List<string>))]
            internal sealed partial class ApiJson : JsonSerializerContext;
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0012", diagnostic.Id);
        Assert.Equal(
            "Type 'Acme.Tagged' (the payload of job 'tagged') is listed in more than one JsonSerializerContext: " +
            "Acme.ApiJson, Acme.JobsJson. The contexts can serialize it differently, so BackWave does not pick one - " +
            "list the type in only one JsonSerializerContext, or register this job by hand with " +
            "JobRegistration.Create.",
            diagnostic.GetMessage());
        Assert.DoesNotContain(run.GeneratedSources.Keys, k => k.Contains("Acme_Tagged"));
    }

    [Fact]
    public void ContextWithTwoPartialParts_ListingThePayloadTwice_IsNotAmbiguous()
    {
        var run = GeneratorHarness.Run(TaggedJobSource + """
            [JsonSerializable(typeof(Tagged))]
            internal sealed partial class JobsJson : JsonSerializerContext;

            [JsonSerializable(typeof(Tagged))]
            internal sealed partial class JobsJson;
            """);

        Assert.Empty(run.GeneratorDiagnostics);
        var source = run.GeneratedSources.Single(s => s.Key.Contains("Acme_Tagged")).Value;
        Assert.Contains("global::Acme.JobsJson.Default.GetTypeInfo(", source);
    }

    [Fact]
    public void ScalarOnlyPayloadListedInTwoContexts_IsNotBW0012()
    {
        var run = GeneratorHarness.Run(Usings + """
            namespace Acme;

            [Job("plain")]
            public sealed record Plain(string Id, int Count);

            public sealed class PlainHandler : IJobHandler<Plain>
            {
                public Task HandleAsync(Plain job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Plain))]
            internal sealed partial class JobsJson : JsonSerializerContext;

            [JsonSerializable(typeof(Plain))]
            internal sealed partial class ApiJson : JsonSerializerContext;
            """);

        Assert.Empty(run.GeneratorDiagnostics);
    }

    [Fact]
    public void ScalarOnlyPayload_GeneratesNoTypeInfoField()
    {
        var run = GeneratorHarness.Run(Usings + """
            namespace Acme;

            [Job("plain")]
            public sealed record Plain(string Id, int Count, string? Note);

            public sealed class PlainHandler : IJobHandler<Plain>
            {
                public Task HandleAsync(Plain job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Plain))]
            internal sealed partial class PlainJson : JsonSerializerContext;
            """);

        var source = run.GeneratedSources.Single(s => s.Key.Contains("Acme_Plain")).Value;
        Assert.DoesNotContain("JsonTypeInfo", source);
        Assert.DoesNotContain("PlainJson", source);
    }

    [Fact]
    public void UnlistedComplexMembers_GiveOneBW0017AtThePayload_AndTheJobIsNotEmitted()
    {
        var run = GeneratorHarness.Run(Usings + """
            namespace Acme;

            [Job("unlisted")]
            public sealed record Unlisted(string Id, List<string> Tags)
            {
                public Dictionary<string, int> Counts { get; init; } = new();
            }

            public sealed class UnlistedHandler : IJobHandler<Unlisted>
            {
                public Task HandleAsync(Unlisted job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [Job("other")]
            public sealed record Other(string Id);

            public sealed class OtherHandler : IJobHandler<Other>
            {
                public Task HandleAsync(Other job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0017", diagnostic.Id);
        Assert.StartsWith("[Job(\"unlisted\")]", GeneratorTests.SourceAt(run, diagnostic));
        Assert.Contains("members 'Tags', 'Counts' of job payload 'Unlisted'", diagnostic.GetMessage());
        Assert.Equal("global::Acme.Unlisted", diagnostic.Properties["TypeFqn"]);

        // The other job still registers. The unresolved job has no codec and no registry entry.
        Assert.DoesNotContain(run.GeneratedSources.Keys, k => k.Contains("Acme_Unlisted"));
        Assert.Contains("\"other\"", run.GeneratedSources["BackWave.Jobs.g.cs"]);
        Assert.DoesNotContain("\"unlisted\"", run.GeneratedSources["BackWave.Jobs.g.cs"]);
    }

    private const string BoxedSource = Usings + """
        namespace Acme;

        public sealed record Box(string Label);

        [Job("boxed")]
        public sealed record Boxed(string Id, Box Box);

        public sealed class BoxedHandler : IJobHandler<Boxed>
        {
            public Task HandleAsync(Boxed job, JobContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        """;

    [Theory]
    [InlineData("[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Serialization)]\n[JsonSerializable(typeof(Boxed))]")]
    [InlineData("[JsonSerializable(typeof(Boxed), GenerationMode = JsonSourceGenerationMode.Serialization)]")]
    public void SerializationOnlyListing_GivesBW0013AtThePayload_AndTheJobIsNotEmitted(string attributes)
    {
        var run = GeneratorHarness.Run(BoxedSource + attributes + """

            internal sealed partial class BoxJson : JsonSerializerContext;
            """, withJsonGenerator: true);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0013", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.StartsWith("[Job(\"boxed\")]", GeneratorTests.SourceAt(run, diagnostic));
        Assert.StartsWith("Type 'Acme.Boxed' (the payload of job 'boxed') is listed by", diagnostic.GetMessage());
        Assert.Contains("'Acme.BoxJson'", diagnostic.GetMessage());
        Assert.Contains("GenerationMode = Serialization", diagnostic.GetMessage());
        Assert.DoesNotContain(run.GeneratedSources.Keys, k => k.Contains("Acme_Boxed"));
    }

    [Fact]
    public void SerializationOnlyListing_OfAPayloadWithTwoComplexMembers_GivesOneBW0013()
    {
        var run = GeneratorHarness.Run(Usings + """
            namespace Acme;

            public sealed record Box(string Label);

            [Job("crates")]
            public sealed record Crates(string Id, Box First, List<Box> Rest);

            public sealed class CratesHandler : IJobHandler<Crates>
            {
                public Task HandleAsync(Crates job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Crates), GenerationMode = JsonSourceGenerationMode.Serialization)]
            internal sealed partial class CratesJson : JsonSerializerContext;
            """, withJsonGenerator: true);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0013", diagnostic.Id);
        Assert.StartsWith("Type 'Acme.Crates' (the payload of job 'crates')", diagnostic.GetMessage());
    }

    [Fact]
    public void SerializationOnlyListing_NextToAMetadataListing_BindsToTheMetadataListing()
    {
        // The serialization-only context sorts first, so it would win the ordinal pick if it were a candidate.
        var probe = CompileProbe(BoxedSource + """
            [JsonSerializable(typeof(Boxed), GenerationMode = JsonSourceGenerationMode.Serialization)]
            internal sealed partial class ABoxWriteJson : JsonSerializerContext;

            [JsonSerializable(typeof(Boxed))]
            internal sealed partial class BBoxJson : JsonSerializerContext;

            public static class Probe
            {
                public static string Reread(string json)
                    => System.Text.Encoding.UTF8.GetString(
                        BoxedWire.Serialize(BoxedWire.Deserialize(System.Text.Encoding.UTF8.GetBytes(json))));
            }
            """);

        Assert.Equal(
            """{"Id":"1","Box":{"Label":"a"}}""",
            probe.Call("Reread", """{"Id":"1","Box":{"Label":"a"}}"""));
    }

    [Theory]
    [InlineData("[JsonSerializable(typeof(Addr), GenerationMode = JsonSourceGenerationMode.Serialization)]\n[JsonSerializable(typeof(Ship))]")]
    [InlineData("[JsonSerializable(typeof(Ship))]\n[JsonSerializable(typeof(Addr), GenerationMode = JsonSourceGenerationMode.Serialization)]")]
    [InlineData("[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Serialization)]\n[JsonSerializable(typeof(Ship), GenerationMode = JsonSourceGenerationMode.Metadata)]\n[JsonSerializable(typeof(Addr))]")]
    // STJ keeps the first listing of a type, so a metadata listing next to it does not make it safe in every order.
    [InlineData("[JsonSerializable(typeof(Addr), GenerationMode = JsonSourceGenerationMode.Serialization)]\n[JsonSerializable(typeof(Addr))]\n[JsonSerializable(typeof(Ship))]")]
    [InlineData("[JsonSerializable(typeof(Addr))]\n[JsonSerializable(typeof(Addr), GenerationMode = JsonSourceGenerationMode.Serialization)]\n[JsonSerializable(typeof(Ship))]")]
    public void SerializationOnlyListingOfAMemberType_InThePayloadContext_GivesBW0013AtThePayload(string attributes)
    {
        // STJ keeps the serialization-only metadata for Addr, so Dest would serialize and then fail to deserialize.
        var run = GeneratorHarness.Run(ShipSource + attributes + """

            internal sealed partial class ShipJson : JsonSerializerContext;
            """, withJsonGenerator: true);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0013", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.StartsWith("[Job(\"ship\")]", GeneratorTests.SourceAt(run, diagnostic));
        Assert.StartsWith(
            "Type 'Acme.Ship' (the payload of job 'ship') is listed by JsonSerializerContext 'Acme.ShipJson'",
            diagnostic.GetMessage());
        Assert.Contains("GenerationMode = Serialization", diagnostic.GetMessage());
        Assert.DoesNotContain(run.GeneratedSources.Keys, k => k.Contains("Acme_Ship"));
    }

    [Theory]
    [InlineData("Spot?", "Spot")]
    [InlineData("Spot", "Spot?")]
    public void SerializationOnlyListingOfANullableValueMemberType_InThePayloadContext_GivesBW0013AtThePayload(
        string memberType, string listedType)
    {
        // A Nullable<T> member reads through the metadata of T, and STJ gives both the mode of the first listing.
        var run = GeneratorHarness.Run(Usings + $$"""
            namespace Acme;

            public struct Spot { public int X { get; set; } }

            [Job("park")]
            public sealed record Park(string Id, {{memberType}} At);

            public sealed class ParkHandler : IJobHandler<Park>
            {
                public Task HandleAsync(Park job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof({{listedType}}), GenerationMode = JsonSourceGenerationMode.Serialization)]
            [JsonSerializable(typeof(Park))]
            internal sealed partial class ParkJson : JsonSerializerContext;
            """, withJsonGenerator: true);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0013", diagnostic.Id);
        Assert.Contains("(the payload of job 'park')", diagnostic.GetMessage());
        Assert.DoesNotContain(run.GeneratedSources.Keys, k => k.Contains("Acme_Park"));
    }

    private const string OrderSource = Usings + """
        namespace Acme;

        public sealed record Address(string Street);

        public sealed record Report(Address Where);

        [Job("order")]
        public sealed record Order(string Id, {0} Ship);

        public sealed class OrderHandler : IJobHandler<Order>
        {
            public Task HandleAsync(Order job, JobContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        """;

    [Theory]
    // STJ gives Address the mode of the first listing that reaches it, here the serialization-only Report.
    [InlineData("Address", "[JsonSerializable(typeof(Report), GenerationMode = JsonSourceGenerationMode.Serialization)]\n[JsonSerializable(typeof(Order))]")]
    // The rule does not depend on the listing order.
    [InlineData("Address", "[JsonSerializable(typeof(Order))]\n[JsonSerializable(typeof(Report), GenerationMode = JsonSourceGenerationMode.Serialization)]")]
    [InlineData("List<Address>", "[JsonSerializable(typeof(Report), GenerationMode = JsonSourceGenerationMode.Serialization)]\n[JsonSerializable(typeof(Order))]")]
    [InlineData("Address", "[JsonSerializable(typeof(Order), GenerationMode = JsonSourceGenerationMode.Serialization)]\n[JsonSerializable(typeof(Order))]")]
    public void AnySerializationOnlyListing_InThePayloadContext_GivesOneBW0013AtThePayload(string memberType, string attributes)
    {
        var run = GeneratorHarness.Run(OrderSource.Replace("{0}", memberType) + attributes + """

            internal sealed partial class AppJson : JsonSerializerContext;
            """, withJsonGenerator: true);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0013", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.StartsWith("[Job(\"order\")]", GeneratorTests.SourceAt(run, diagnostic));
        Assert.StartsWith(
            "Type 'Acme.Order' (the payload of job 'order') is listed by JsonSerializerContext 'Acme.AppJson'",
            diagnostic.GetMessage());
        Assert.Contains("move the serialization-only listings to a separate context", diagnostic.GetMessage());
        Assert.DoesNotContain(run.GeneratedSources.Keys, k => k.Contains("Acme_Order"));
    }

    [Fact]
    public void ContextLevelSerializationMode_InThePayloadContext_GivesBW0013AtThePayload()
    {
        // The payload listing overrides the mode back to metadata, but the context default can still reach a type.
        var run = GeneratorHarness.Run(OrderSource.Replace("{0}", "Address") + """
            [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Serialization)]
            [JsonSerializable(typeof(Order), GenerationMode = JsonSourceGenerationMode.Metadata)]
            internal sealed partial class AppJson : JsonSerializerContext;
            """, withJsonGenerator: true);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0013", diagnostic.Id);
        Assert.StartsWith("[Job(\"order\")]", GeneratorTests.SourceAt(run, diagnostic));
        Assert.Contains("'Acme.AppJson'", diagnostic.GetMessage());
        Assert.Contains("[JsonSourceGenerationOptions(GenerationMode = Serialization)]", diagnostic.GetMessage());
        Assert.DoesNotContain(run.GeneratedSources.Keys, k => k.Contains("Acme_Order"));
    }

    [Fact]
    public void MethodSugar_MemberContextWithAnUnrelatedSerializationOnlyListing_GivesOneBW0013()
    {
        // Both members bind to AppJson, so the one context gives one diagnostic, at the first of them.
        var run = GeneratorHarness.Run(Usings + """
            namespace Acme;

            public sealed record Address(string Street);

            public sealed record Report(string Title);

            public static class Shipper
            {
                [Job("ship")]
                public static Task ShipAsync(Address from, Address to) => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Report), GenerationMode = JsonSourceGenerationMode.Serialization)]
            [JsonSerializable(typeof(Address))]
            internal sealed partial class AppJson : JsonSerializerContext;
            """, withJsonGenerator: true);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0013", diagnostic.Id);
        Assert.Equal("from", GeneratorTests.SourceAt(run, diagnostic));
        Assert.StartsWith(
            "Type 'Acme.Address' (the type of member 'from' of job payload 'Ship') is listed by JsonSerializerContext " +
            "'Acme.AppJson'", diagnostic.GetMessage());
        Assert.Contains("move the serialization-only listings to a separate context", diagnostic.GetMessage());
        Assert.DoesNotContain(run.GeneratedSources.Keys, k => k.Contains("Acme_Ship"));
    }

    [Fact]
    public void SerializationOnlyListingOfAMemberType_InAnotherContext_HasNoEffect()
    {
        // The member binds to the payload context only, so a listing in another context cannot break it.
        var probe = CompileProbe(ShipSource + """
            [JsonSerializable(typeof(Ship))]
            internal sealed partial class B_JobsJson : JsonSerializerContext;

            [JsonSerializable(typeof(Addr), GenerationMode = JsonSourceGenerationMode.Serialization)]
            internal sealed partial class A_ApiJson : JsonSerializerContext;

            public static class Probe
            {
                public static string Reread(string json)
                    => System.Text.Encoding.UTF8.GetString(
                        ShipWire.Serialize(ShipWire.Deserialize(System.Text.Encoding.UTF8.GetBytes(json))));
            }
            """);

        Assert.Equal(
            """{"Id":"1","Dest":{"Street":"s","City":null}}""",
            probe.Call("Reread", """{"Id":"1","Dest":{"Street":"s"}}"""));
    }

    [Theory]
    [InlineData("public partial class Outer { [JsonSerializable(typeof(Boxed))] private sealed partial class BoxJson : JsonSerializerContext; }")]
    [InlineData("public partial class Outer { [JsonSerializable(typeof(Boxed))] protected sealed partial class BoxJson : JsonSerializerContext; }")]
    [InlineData("public partial class Outer { [JsonSerializable(typeof(Boxed))] private protected sealed partial class BoxJson : JsonSerializerContext; }")]
    [InlineData("public partial class Outer { private partial class Inner { [JsonSerializable(typeof(Boxed))] internal sealed partial class BoxJson : JsonSerializerContext; } }")]
    public void InaccessibleContext_GivesBW0013AtThePayload_AndTheJobIsNotEmitted(string declaration)
    {
        var run = GeneratorHarness.Run(BoxedSource + declaration, withJsonGenerator: true);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0013", diagnostic.Id);
        Assert.StartsWith("[Job(\"boxed\")]", GeneratorTests.SourceAt(run, diagnostic));
        Assert.Contains("'Acme.Boxed'", diagnostic.GetMessage());
        Assert.Contains("not accessible", diagnostic.GetMessage());
        Assert.DoesNotContain(run.GeneratedSources.Keys, k => k.Contains("Acme_Boxed"));
        AssertNoDiagnostics(run.CompilationDiagnostics);
    }

    [Fact]
    public void ContextNestedInAFileLocalType_GivesBW0013AtThePayload_AndTheJobIsNotEmitted()
    {
        // Roslyn calls the nested context accessible, but generated code in another file cannot name it.
        // The STJ generator cannot build such a context either, so only BackWave's own output must be clean.
        var run = GeneratorHarness.Run(BoxedSource + """
            file partial class Outer
            {
                [JsonSerializable(typeof(Boxed))]
                internal sealed partial class BoxJson : JsonSerializerContext;
            }
            """, withJsonGenerator: true);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0013", diagnostic.Id);
        Assert.Contains("'Acme.Outer.BoxJson'", diagnostic.GetMessage());
        Assert.Contains("not accessible", diagnostic.GetMessage());
        Assert.DoesNotContain(run.GeneratedSources.Keys, k => k.Contains("Acme_Boxed"));
        AssertNoDiagnostics(run.CompilationDiagnostics.Where(
            d => d.Location.SourceTree?.FilePath.Contains("BackWave.SourceGenerators") == true));
    }

    [Fact]
    public void InaccessibleContext_NextToAnAccessibleContext_BindsToTheAccessibleContext()
    {
        // The private context sorts first, so it would win the ordinal pick if it were a candidate.
        var run = GeneratorHarness.Run(BoxedSource + """
            public partial class AOuter
            {
                [JsonSerializable(typeof(Boxed))]
                private sealed partial class BoxJson : JsonSerializerContext;
            }

            [JsonSerializable(typeof(Boxed))]
            internal sealed partial class BBoxJson : JsonSerializerContext;
            """, withJsonGenerator: true);

        Assert.Empty(run.GeneratorDiagnostics);
        AssertNoDiagnostics(run.CompilationDiagnostics);
        var source = run.GeneratedSources.Single(s => s.Key.Contains("Acme_Boxed")).Value;
        Assert.Contains("global::Acme.BBoxJson.Default.GetTypeInfo(", source);
    }

    [Fact]
    public void JsonSerializableOnAClassThatIsNotAContext_DoesNotBindTheCodec()
    {
        // [JsonSerializable] compiles on any class, but only a JsonSerializerContext has the Default property
        // that the generated codec reads its metadata from.
        var run = GeneratorHarness.Run(BoxedSource + """
            [JsonSerializable(typeof(Boxed))]
            internal sealed partial class NotACtx;
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0017", diagnostic.Id);
        Assert.DoesNotContain(run.GeneratedSources.Keys, k => k.Contains("Acme_Boxed"));
        AssertNoDiagnostics(run.CompilationDiagnostics);
    }

    private const string SugarSource = Usings + """
        namespace Acme;

        public class Mailer
        {
            [Job("send-batch")]
            public Task SendBatchAsync(List<string> recipients, string subject, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        """;

    [Fact]
    public void MethodSugar_WithTheMemberTypeListed_CompilesWithNoDiagnostic()
    {
        var run = GeneratorHarness.Run(SugarSource + """
            [JsonSerializable(typeof(List<string>))]
            internal sealed partial class MailJson : JsonSerializerContext;
            """, withJsonGenerator: true);

        Assert.Empty(run.GeneratorDiagnostics);
        AssertNoDiagnostics(run.CompilationDiagnostics);
        var source = run.GeneratedSources.Single(s => s.Key.Contains("Acme_SendBatch")).Value;
        Assert.Contains(
            "sealed record SendBatch(global::System.Collections.Generic.List<string> Recipients, string Subject);", source);
    }

    [Fact]
    public void MethodSugar_WithNoListing_GivesBW0017ThatNamesTheMemberTypeToList()
    {
        var run = GeneratorHarness.Run(SugarSource);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0017", diagnostic.Id);
        Assert.Equal("recipients", GeneratorTests.SourceAt(run, diagnostic));
        Assert.Contains("member 'recipients' of job payload 'SendBatch'", diagnostic.GetMessage());
        Assert.Equal("global::System.Collections.Generic.List<string>", diagnostic.Properties["TypeFqn"]);
        Assert.Contains(
            "Add [JsonSerializable(typeof(System.Collections.Generic.List<string>))]", diagnostic.GetMessage());
    }

    [Fact]
    public void MethodSugar_TwoUnlistedMembersOfOneType_GiveOneBW0017_AndOneForEachOtherType()
    {
        var run = GeneratorHarness.Run(Usings + """
            namespace Acme;

            public class Mailer
            {
                [Job("send-batch")]
                public Task SendBatchAsync(List<string> to, List<string> cc, Dictionary<string, int> counts)
                    => Task.CompletedTask;
            }
            """);

        Assert.All(run.GeneratorDiagnostics, d => Assert.Equal("BW0017", d.Id));
        Assert.Equal(["to", "counts"], run.GeneratorDiagnostics.Select(d => GeneratorTests.SourceAt(run, d)));
        Assert.Contains("members 'to', 'cc' of job payload 'SendBatch'", run.GeneratorDiagnostics[0].GetMessage());
        Assert.Equal(
            "global::System.Collections.Generic.Dictionary<string, int>", run.GeneratorDiagnostics[1].Properties["TypeFqn"]);
    }

    [Fact]
    public void MethodSugar_ParameterTypesNotPublicOutsideTheAssembly_GiveAnInternalRecordThatCompiles()
    {
        // Mailer and Inner are declared public, but each sits in an internal class, so the record must be internal.
        var probe = CompileProbe(Usings + """
            namespace Acme;

            internal static class Holder
            {
                public sealed record Inner(string Value);
            }

            internal enum Level { Low, High }

            internal static class Jobs
            {
                public static class Mailer
                {
                    [Job("nest")]
                    public static Task NestAsync(Holder.Inner inner, Level level) => Task.CompletedTask;
                }
            }

            [JsonSerializable(typeof(Holder.Inner))]
            internal sealed partial class NestJson : JsonSerializerContext;

            public static class Probe
            {
                public static string Write()
                    => System.Text.Encoding.UTF8.GetString(NestWire.Serialize(new Nest(new Holder.Inner("v"), Level.High)));
            }
            """);

        Assert.Equal("""{"Inner":{"Value":"v"},"Level":"High"}""", probe.Call("Write"));
    }

    [Fact]
    public void MethodSugarMemberTypesListedByTwoContexts_TakeTheLargerMaxDepth_AndCompile()
    {
        var probe = CompileProbe(Usings + """
            namespace Acme;

            public sealed record Box(string Label);

            public sealed record Bag(string Label);

            public static class Packer
            {
                [Job("pair")]
                public static Task PairAsync(Box box, Bag bag) => Task.CompletedTask;
            }

            [JsonSourceGenerationOptions(MaxDepth = 10)]
            [JsonSerializable(typeof(Box))]
            internal sealed partial class BoxJson : JsonSerializerContext;

            [JsonSerializable(typeof(Bag))]
            internal sealed partial class BagJson : JsonSerializerContext;

            public static class Probe
            {
                public static string Reread(string json)
                    => System.Text.Encoding.UTF8.GetString(
                        PairWire.Serialize(PairWire.Deserialize(System.Text.Encoding.UTF8.GetBytes(json))));
            }
            """);

        const string Json = """{"Box":{"Label":"a"},"Bag":{"Label":"b"}}""";
        Assert.Equal(Json, probe.Call("Reread", Json));
    }

    [Fact]
    public void MethodSugar_NullableAnnotationsReachTheGeneratedRecord()
    {
        var run = GeneratorHarness.Run(Usings + """
            namespace Acme;

            public class Mailer
            {
                [Job("send-maybe")]
                public Task SendMaybeAsync(List<string?>? recipients) => Task.CompletedTask;
            }

            [JsonSerializable(typeof(List<string>))]
            internal sealed partial class MailJson : JsonSerializerContext;
            """, withJsonGenerator: true);

        AssertNoDiagnostics(run.CompilationDiagnostics);
        var source = run.GeneratedSources.Single(s => s.Key.Contains("Acme_SendMaybe")).Value;
        Assert.Contains("sealed record SendMaybe(global::System.Collections.Generic.List<string?>? Recipients);", source);
    }

    [Fact]
    public void UnresolvedMemberType_GivesBW0004ThatSaysTheTypeDidNotResolve()
    {
        var run = GeneratorHarness.Run(Usings + """
            namespace Acme;

            [Job("ghost")]
            public sealed record Ghost(string Id, List<Missing> Items);

            public sealed class GhostHandler : IJobHandler<Ghost>
            {
                public Task HandleAsync(Ghost job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Ghost))]
            internal sealed partial class GhostJson : JsonSerializerContext;
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0004", diagnostic.Id);
        Assert.Equal("Items", GeneratorTests.SourceAt(run, diagnostic));
        Assert.Contains("the type did not resolve when the BackWave generator ran", diagnostic.GetMessage());
    }

    /// <summary>
    /// A member type that System.Text.Json rejects by design is BW0004 at build time, even when a context lists the
    /// payload type. Listing a type does not help, so the diagnostic carries no type for the code fix to list.
    /// </summary>
    [Theory]
    [InlineData("Type", "reflection types such as 'System.Type'")]
    [InlineData("System.Reflection.MethodInfo", "reflection types such as 'System.Reflection.MethodInfo'")]
    [InlineData("Action", "delegates such as 'System.Action'")]
    [InlineData("Func<int, string>", "delegates such as 'System.Func<int, string>'")]
    [InlineData("Callback", "delegates such as 'Acme.Callback'")]
    [InlineData("Delegate", "delegates such as 'System.Delegate'")]
    [InlineData("IntPtr", "native-sized integers such as 'nint'")]
    [InlineData("UIntPtr?", "native-sized integers such as 'nuint'")]
    [InlineData("System.Runtime.Serialization.SerializationInfo", "'System.Runtime.Serialization.SerializationInfo'")]
    [InlineData("List<Type>", "reflection types such as 'System.Type'")]
    [InlineData("Action[]", "delegates such as 'System.Action'")]
    [InlineData("Dictionary<string, Func<int>>", "delegates such as 'System.Func<int>'")]
    public void ClassPayloadMember_OfATypeStjRejects_IsBW0004(string memberType, string rejected)
    {
        var run = GeneratorHarness.Run(Usings + $$"""
            namespace Acme;

            public delegate void Callback();

            [Job("rejected")]
            public sealed record Rejected(string Id, {{memberType}} Value);

            public sealed class RejectedHandler : IJobHandler<Rejected>
            {
                public Task HandleAsync(Rejected job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Rejected))]
            internal sealed partial class RejectedJson : JsonSerializerContext;
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0004", diagnostic.Id);
        Assert.Equal("Value", GeneratorTests.SourceAt(run, diagnostic));
        Assert.Contains(
            $"System.Text.Json rejects {rejected} by design - use a supported type instead, or register this job by " +
            "hand with JobRegistration.Create",
            diagnostic.GetMessage());
        Assert.False(diagnostic.Properties.ContainsKey("TypeFqn"));
        Assert.Empty(run.GeneratedSources);
    }

    /// <summary>
    /// System.Text.Json ignores the Item1, Item2, ... fields of a value tuple, so it would write {} and read back
    /// default values. A tuple member is BW0004 at build time, wherever it sits in the member type.
    /// </summary>
    [Theory]
    [InlineData("(int A, string B)", "(int A, string B)")]
    [InlineData("ValueTuple<int, string>", "(int, string)")]
    [InlineData("List<(int, string)>", "(int, string)")]
    [InlineData("(int, string)[]", "(int, string)")]
    [InlineData("Dictionary<string, (int, string)>", "(int, string)")]
    [InlineData("(int, string)?", "(int, string)")]
    public void ClassPayloadMember_OfATupleType_IsBW0004(string memberType, string tuple)
    {
        var run = GeneratorHarness.Run(Usings + $$"""
            namespace Acme;

            [Job("tup")]
            public sealed record Tup(string Id, {{memberType}} Pair);

            public sealed class TupHandler : IJobHandler<Tup>
            {
                public Task HandleAsync(Tup job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Tup))]
            internal sealed partial class TupJson : JsonSerializerContext;
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0004", diagnostic.Id);
        Assert.Equal("Pair", GeneratorTests.SourceAt(run, diagnostic));
        Assert.Contains(
            $"System.Text.Json ignores the fields of tuples such as '{tuple}', so the value would not round-trip - " +
            "use a record instead, or register this job by hand with JobRegistration.Create",
            diagnostic.GetMessage());
        Assert.False(diagnostic.Properties.ContainsKey("TypeFqn"));
        Assert.Empty(run.GeneratedSources);
    }

    private const string AbstractTypesSource = """
        public interface IShape { string Name { get; } }

        public abstract record Animal(string Name);

        [JsonPolymorphic]
        [JsonDerivedType(typeof(Circle), "circle")]
        public interface IPolyShape { }

        public sealed record Circle(double Radius) : IPolyShape;

        [JsonDerivedType(typeof(Dog), "dog")]
        public abstract record PolyAnimal;

        public sealed record Dog(bool Good) : PolyAnimal;

        [JsonConverter(typeof(CodeConverter))]
        public interface ICode { string Value { get; } }

        public sealed record Code(string Value) : ICode;

        public sealed class CodeConverter : JsonConverter<ICode>
        {
            public override ICode Read(ref System.Text.Json.Utf8JsonReader reader, Type type, System.Text.Json.JsonSerializerOptions options)
                => new Code(reader.GetString()!);

            public override void Write(System.Text.Json.Utf8JsonWriter writer, ICode value, System.Text.Json.JsonSerializerOptions options)
                => writer.WriteStringValue(value.Value);
        }

        """;

    /// <summary>
    /// System.Text.Json writes an interface or abstract member with the properties of the declared type only, and
    /// throws when it reads one back. Such a member is BW0004 at build time, wherever it sits in the member type.
    /// IReadOnlySet is the one collection interface that STJ writes but cannot read back, as
    /// <see cref="StjReadsAStackBackInReverse_AndCannotReadAReadOnlySetOrABag"/> shows.
    /// </summary>
    [Theory]
    [InlineData("IShape", "interfaces such as 'Acme.IShape'")]
    [InlineData("Animal", "abstract classes such as 'Acme.Animal'")]
    [InlineData("List<IShape>", "interfaces such as 'Acme.IShape'")]
    [InlineData("IReadOnlyList<IShape>", "interfaces such as 'Acme.IShape'")]
    [InlineData("Dictionary<string, Animal>", "abstract classes such as 'Acme.Animal'")]
    [InlineData("IReadOnlySet<int>", "interfaces such as 'System.Collections.Generic.IReadOnlySet<int>'")]
    public void ClassPayloadMember_OfAnInterfaceOrAbstractType_IsBW0004(string memberType, string rejected)
    {
        var diagnostic = RejectedMemberDiagnostic(memberType);

        Assert.Contains(
            $"System.Text.Json cannot create {rejected} when it reads the value back - annotate the type with " +
            "[JsonPolymorphic] and [JsonDerivedType], or use a concrete type instead, or register this job by hand " +
            "with JobRegistration.Create",
            diagnostic.GetMessage());
    }

    /// <summary>System.Text.Json reads an object back as a JsonElement, so the value would come back as another type.</summary>
    [Theory]
    [InlineData("object", "object")]
    [InlineData("Dictionary<string, object>", "object")]
    [InlineData("dynamic", "dynamic")]
    public void ClassPayloadMember_OfTypeObject_IsBW0004(string memberType, string rejected)
    {
        var diagnostic = RejectedMemberDiagnostic(memberType);

        Assert.Contains(
            $"System.Text.Json reads '{rejected}' back as a JsonElement, so the value would not round-trip - use a " +
            "concrete type or JsonElement instead, or register this job by hand with JobRegistration.Create",
            diagnostic.GetMessage());
    }

    /// <summary>
    /// The interfaces and abstract types that System.Text.Json reads back: the collection interfaces it maps to a
    /// concrete collection, JsonNode, and a type that declares its polymorphism or its own converter.
    /// </summary>
    [Fact]
    public void ClassPayloadMember_OfAnInterfaceOrAbstractTypeStjReadsBack_CompilesWithNoDiagnostic()
    {
        var run = GeneratorHarness.Run(Usings + "using System.Collections.Immutable;\n\nnamespace Acme;\n\n" + AbstractTypesSource + """
            [Job("readable")]
            public sealed record Readable(
                IReadOnlyList<int> Numbers,
                IDictionary<string, int> Counts,
                IImmutableList<string> Names,
                System.Text.Json.Nodes.JsonNode? Extra,
                IPolyShape Shape,
                List<PolyAnimal> Pets,
                ICode Code);

            public sealed class ReadableHandler : IJobHandler<Readable>
            {
                public Task HandleAsync(Readable job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Readable))]
            internal sealed partial class ReadableJson : JsonSerializerContext;
            """, withJsonGenerator: true);

        Assert.Empty(run.GeneratorDiagnostics);
        AssertNoDiagnostics(run.CompilationDiagnostics);
    }

    /// <summary>The STJ facts behind the stack and collection rules, from the real STJ source generator.</summary>
    [Fact]
    public void StjReadsAStackBackInReverse_AndCannotReadAReadOnlySetOrABag()
    {
        var probe = CompileProbe(Usings + """
            using System.Collections.Concurrent;
            using System.Collections.Immutable;
            using System.Text.Json;

            namespace Acme;

            [JsonSerializable(typeof(IImmutableStack<int>))]
            [JsonSerializable(typeof(Stack<int>))]
            [JsonSerializable(typeof(IReadOnlySet<int>))]
            [JsonSerializable(typeof(ConcurrentBag<int>))]
            [JsonSerializable(typeof(ArraySegment<int>))]
            internal sealed partial class CollectionsJson : JsonSerializerContext;

            public static class Probe
            {
                public static string RoundTripImmutableStack()
                {
                    IImmutableStack<int> stack = ImmutableStack.Create(1, 2);
                    var json = JsonSerializer.Serialize(stack, CollectionsJson.Default.IImmutableStackInt32);
                    return string.Join(",", stack) + "|" + string.Join(",", JsonSerializer.Deserialize(json, CollectionsJson.Default.IImmutableStackInt32)!);
                }

                public static string RoundTripStack()
                {
                    var stack = new Stack<int>(new[] { 1, 2 });
                    var json = JsonSerializer.Serialize(stack, CollectionsJson.Default.StackInt32);
                    return string.Join(",", stack) + "|" + string.Join(",", JsonSerializer.Deserialize(json, CollectionsJson.Default.StackInt32)!);
                }

                public static string ReadSet() => Read(() => JsonSerializer.Deserialize("[1]", CollectionsJson.Default.IReadOnlySetInt32));

                public static string ReadBag() => Read(() => JsonSerializer.Deserialize("[1]", CollectionsJson.Default.ConcurrentBagInt32));

                public static string ReadSegment() => Read(() => JsonSerializer.Deserialize("[1]", CollectionsJson.Default.ArraySegmentInt32));

                private static string Read(Func<object?> read)
                {
                    try
                    {
                        read();
                        return "read";
                    }
                    catch (Exception exception)
                    {
                        return exception.GetType().Name;
                    }
                }
            }
            """);

        Assert.Equal("2,1|1,2", probe.Call("RoundTripImmutableStack"));
        Assert.Equal("2,1|1,2", probe.Call("RoundTripStack"));
        Assert.Equal("NotSupportedException", probe.Call("ReadSet"));
        Assert.Equal("NotSupportedException", probe.Call("ReadBag"));
        Assert.Equal("NotSupportedException", probe.Call("ReadSegment"));
    }

    /// <summary>
    /// System.Text.Json reads a stack back in reverse order, as
    /// <see cref="StjReadsAStackBackInReverse_AndCannotReadAReadOnlySetOrABag"/> shows. A stack member is BW0004 at
    /// build time, wherever it sits in the member type.
    /// </summary>
    [Theory]
    [InlineData("Stack<int>", "System.Collections.Generic.Stack<int>")]
    [InlineData("System.Collections.Concurrent.ConcurrentStack<int>", "System.Collections.Concurrent.ConcurrentStack<int>")]
    [InlineData("System.Collections.Immutable.ImmutableStack<int>", "System.Collections.Immutable.ImmutableStack<int>")]
    [InlineData("System.Collections.Immutable.IImmutableStack<int>", "System.Collections.Immutable.IImmutableStack<int>")]
    [InlineData("UndoStack", "Acme.UndoStack")]
    [InlineData("List<Stack<int>>", "System.Collections.Generic.Stack<int>")]
    [InlineData("Stack<int>[]", "System.Collections.Generic.Stack<int>")]
    public void ClassPayloadMember_OfAStack_IsBW0004(string memberType, string stack)
    {
        var diagnostic = RejectedMemberDiagnostic(memberType);

        Assert.Contains(
            $"System.Text.Json reads stacks such as '{stack}' back in reverse order, so the value would not " +
            "round-trip - use a list or an array instead, or register this job by hand with JobRegistration.Create",
            diagnostic.GetMessage());
    }

    /// <summary>
    /// System.Text.Json writes these collections but throws when it reads them back: it needs a public
    /// parameterless constructor and ICollection&lt;T&gt; or IDictionary&lt;TKey, TValue&gt; to fill one.
    /// </summary>
    [Theory]
    [InlineData("System.Collections.ObjectModel.ReadOnlyCollection<int>", "System.Collections.ObjectModel.ReadOnlyCollection<int>")]
    [InlineData("System.Collections.ObjectModel.ReadOnlyDictionary<string, int>", "System.Collections.ObjectModel.ReadOnlyDictionary<string, int>")]
    [InlineData("System.Collections.ObjectModel.ReadOnlyObservableCollection<int>", "System.Collections.ObjectModel.ReadOnlyObservableCollection<int>")]
    [InlineData("System.Collections.Concurrent.ConcurrentBag<int>", "System.Collections.Concurrent.ConcurrentBag<int>")]
    [InlineData("System.Collections.Concurrent.BlockingCollection<int>", "System.Collections.Concurrent.BlockingCollection<int>")]
    [InlineData("ArraySegment<int>", "System.ArraySegment<int>")]
    [InlineData("SeededBag", "Acme.SeededBag")]
    [InlineData("InternalBag", "Acme.InternalBag")]
    [InlineData("ReadOnlyBag", "Acme.ReadOnlyBag")]
    [InlineData("List<System.Collections.Concurrent.ConcurrentBag<int>>", "System.Collections.Concurrent.ConcurrentBag<int>")]
    public void ClassPayloadMember_OfACollectionStjCannotRead_IsBW0004(string memberType, string collection)
    {
        var diagnostic = RejectedMemberDiagnostic(memberType);

        Assert.Contains(
            $"System.Text.Json writes collections such as '{collection}' but cannot read them back - use a list, an " +
            "array, or a dictionary instead, or register this job by hand with JobRegistration.Create",
            diagnostic.GetMessage());
    }

    [Theory]
    [InlineData("System.Collections.ArrayList", "System.Collections.ArrayList")]
    [InlineData("System.Collections.Hashtable", "System.Collections.Hashtable")]
    [InlineData("System.Collections.BitArray", "System.Collections.BitArray")]
    [InlineData("LegacyList", "Acme.LegacyList")]
    [InlineData("List<System.Collections.ArrayList>", "System.Collections.ArrayList")]
    public void ClassPayloadMember_OfANonGenericCollection_IsBW0004(string memberType, string collection)
    {
        var diagnostic = RejectedMemberDiagnostic(memberType);

        Assert.Contains(
            $"System.Text.Json cannot round-trip non-generic collections such as '{collection}' - use a generic " +
            "collection instead, or register this job by hand with JobRegistration.Create",
            diagnostic.GetMessage());
    }

    [Theory]
    [InlineData("int[,]", "int[,]")]
    [InlineData("List<string[,,]>", "string[,,]")]
    public void ClassPayloadMember_OfAMultiDimensionalArray_IsBW0004(string memberType, string array)
    {
        var diagnostic = RejectedMemberDiagnostic(memberType);

        Assert.Contains(
            $"System.Text.Json cannot write multi-dimensional arrays such as '{array}' - use a jagged array instead, " +
            "or register this job by hand with JobRegistration.Create",
            diagnostic.GetMessage());
    }

    /// <summary>The concrete collections that System.Text.Json fills, including the queues and the immutable types.</summary>
    [Fact]
    public void ClassPayloadMember_OfACollectionStjReadsBack_CompilesWithNoDiagnostic()
    {
        var run = GeneratorHarness.Run(Usings + """
            using System.Collections.Concurrent;
            using System.Collections.Immutable;
            using System.Collections.ObjectModel;

            namespace Acme;

            public sealed class Tags : List<string>;

            public sealed class Work : Queue<int>;

            public struct Slots : ICollection<int>
            {
                public int Count => 0;
                public bool IsReadOnly => false;
                public void Add(int item) { }
                public void Clear() { }
                public bool Contains(int item) => false;
                public void CopyTo(int[] array, int arrayIndex) { }
                public bool Remove(int item) => false;
                public IEnumerator<int> GetEnumerator() => throw new NotSupportedException();
                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
            }

            [Job("readable")]
            public sealed record Readable(
                Queue<int> Queue,
                ConcurrentQueue<int> Concurrent,
                Work Work,
                SortedSet<int> Sorted,
                LinkedList<int> Linked,
                ObservableCollection<int> Observed,
                Collection<int> Plain,
                ConcurrentDictionary<string, int> Counts,
                SortedList<string, int> Ranks,
                ImmutableArray<int> Frozen,
                ImmutableList<string> Names,
                ImmutableQueue<int> Line,
                ImmutableSortedDictionary<string, int> Index,
                Tags Tags,
                Slots Slots,
                int[][] Grid);

            public sealed class ReadableHandler : IJobHandler<Readable>
            {
                public Task HandleAsync(Readable job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Readable))]
            internal sealed partial class ReadableJson : JsonSerializerContext;
            """, withJsonGenerator: true);

        Assert.Empty(run.GeneratorDiagnostics);
        AssertNoDiagnostics(run.CompilationDiagnostics);
    }

    private const string CollectionTypesSource = """
        public sealed class UndoStack : Stack<int>;

        public sealed class SeededBag(int seed) : List<int>;

        public sealed class InternalBag : List<int>
        {
            internal InternalBag() { }
        }

        public sealed class ReadOnlyBag : IReadOnlyCollection<int>
        {
            public int Count => 0;
            public IEnumerator<int> GetEnumerator() => throw new NotSupportedException();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public sealed class LegacyList : System.Collections.ArrayList;

        """;

    private static Diagnostic RejectedMemberDiagnostic(string memberType)
    {
        var run = GeneratorHarness.Run(Usings + "namespace Acme;\n\n" + AbstractTypesSource + CollectionTypesSource + $$"""
            [Job("rejected")]
            public sealed record Rejected(string Id, {{memberType}} Value);

            public sealed class RejectedHandler : IJobHandler<Rejected>
            {
                public Task HandleAsync(Rejected job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Rejected))]
            internal sealed partial class RejectedJson : JsonSerializerContext;
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0004", diagnostic.Id);
        Assert.Equal("Value", GeneratorTests.SourceAt(run, diagnostic));
        Assert.False(diagnostic.Properties.ContainsKey("TypeFqn"));
        Assert.Empty(run.GeneratedSources);
        return diagnostic;
    }

    [Theory]
    [InlineData("Type", "reflection types such as 'System.Type'")]
    [InlineData("Action<string>", "delegates such as 'System.Action<string>'")]
    [InlineData("nint", "native-sized integers such as 'nint'")]
    [InlineData("Span<int>", "ref structs such as 'System.Span<int>'")]
    [InlineData("ReadOnlySpan<char>", "ref structs such as 'System.ReadOnlySpan<char>'")]
    [InlineData("int*", "pointers such as 'int*'")]
    [InlineData("delegate*<void>", "pointers such as 'delegate*<void>'")]
    public void MethodSugarParameter_OfATypeStjRejects_IsBW0004(string parameterType, string rejected)
    {
        var run = GeneratorHarness.Run(Usings + $$"""
            namespace Acme;

            public static class Jobs
            {
                [Job("rejected")]
                public static unsafe Task Rejected({{parameterType}} value) => Task.CompletedTask;
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0004", diagnostic.Id);
        Assert.Equal("value", GeneratorTests.SourceAt(run, diagnostic));
        Assert.StartsWith("Member 'value' of job payload 'Rejected'", diagnostic.GetMessage());
        Assert.Contains($"System.Text.Json rejects {rejected} by design", diagnostic.GetMessage());
        Assert.False(diagnostic.Properties.ContainsKey("TypeFqn"));
        Assert.Empty(run.GeneratedSources);
    }

    [Theory]
    [InlineData("[JsonPropertyName(\"tags\")]", "JsonPropertyName")]
    [InlineData("[JsonIgnore]", "JsonIgnore")]
    [InlineData("[JsonInclude]", "JsonInclude")]
    [InlineData("[JsonRequired]", "JsonRequired")]
    [InlineData("[JsonPropertyOrder(1)]", "JsonPropertyOrder")]
    public void PropertyOnlyJsonAttributeOnADelegatedProperty_IsBW0011_WithRemoveFix(string attribute, string attributeName)
    {
        var diagnostic = AttributedDelegatedPropertyDiagnostic(attribute);

        Assert.Equal(
            $"Member 'Tags' of job payload 'Attributed' has [{attributeName}], which has no effect - the generated " +
            "codec serializes this member with the metadata of its type 'System.Collections.Generic.List<string>', " +
            "not of the property. Remove the attribute, or register this job by hand with JobRegistration.Create.",
            diagnostic.GetMessage());
    }

    [Theory]
    [InlineData("[JsonConverter(typeof(JsonStringEnumConverter))]", "JsonConverter")]
    [InlineData("[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]", "JsonNumberHandling")]
    [InlineData("[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]", "JsonObjectCreationHandling")]
    public void TypeLevelJsonAttributeOnADelegatedProperty_IsBW0011_WithMoveToTypeFix(string attribute, string attributeName)
    {
        var diagnostic = AttributedDelegatedPropertyDiagnostic(attribute);

        Assert.Equal(
            $"Member 'Tags' of job payload 'Attributed' has [{attributeName}], which has no effect - the generated " +
            "codec serializes this member with the metadata of its type 'System.Collections.Generic.List<string>', " +
            "not of the property. Move the setting to the type 'System.Collections.Generic.List<string>' if you own " +
            "it, or register this job by hand with JobRegistration.Create.",
            diagnostic.GetMessage());
    }

    [Fact]
    public void TwoPropertyOnlyJsonAttributesOnADelegatedProperty_AreOneBW0011_WithRemoveFix()
    {
        var diagnostic = AttributedDelegatedPropertyDiagnostic("[JsonPropertyName(\"tags\")] [JsonRequired]");

        Assert.Equal(
            "Member 'Tags' of job payload 'Attributed' has [JsonPropertyName] and [JsonRequired], which have no " +
            "effect - the generated codec serializes this member with the metadata of its type " +
            "'System.Collections.Generic.List<string>', not of the property. Remove the attributes, or register " +
            "this job by hand with JobRegistration.Create.",
            diagnostic.GetMessage());
    }

    [Fact]
    public void TwoTypeLevelJsonAttributesOnADelegatedProperty_AreOneBW0011_WithMoveToTypeFix()
    {
        var diagnostic = AttributedDelegatedPropertyDiagnostic(
            "[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] " +
            "[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]");

        Assert.Equal(
            "Member 'Tags' of job payload 'Attributed' has [JsonNumberHandling] and [JsonObjectCreationHandling], " +
            "which have no effect - the generated codec serializes this member with the metadata of its type " +
            "'System.Collections.Generic.List<string>', not of the property. Move the settings to the type " +
            "'System.Collections.Generic.List<string>' if you own it, or register this job by hand with " +
            "JobRegistration.Create.",
            diagnostic.GetMessage());
    }

    [Fact]
    public void MixedJsonAttributesOnADelegatedProperty_AreOneBW0011_ThatNamesTheFixForEach()
    {
        var diagnostic = AttributedDelegatedPropertyDiagnostic(
            "[JsonPropertyName(\"tags\")] [JsonRequired] [JsonConverter(typeof(JsonStringEnumConverter))]");

        Assert.Equal(
            "Member 'Tags' of job payload 'Attributed' has [JsonPropertyName], [JsonRequired], and [JsonConverter], " +
            "which have no effect - the generated codec serializes this member with the metadata of its type " +
            "'System.Collections.Generic.List<string>', not of the property. Remove [JsonPropertyName] and " +
            "[JsonRequired], move the setting of [JsonConverter] to the type " +
            "'System.Collections.Generic.List<string>' if you own it, or register this job by hand with " +
            "JobRegistration.Create.",
            diagnostic.GetMessage());
    }

    private static Diagnostic AttributedDelegatedPropertyDiagnostic(string attribute)
    {
        var run = GeneratorHarness.Run(Usings + $$"""
            namespace Acme;

            [Job("attributed")]
            public sealed record Attributed(string Id)
            {
                {{attribute}}
                public List<string> Tags { get; init; } = new();
            }

            public sealed class AttributedHandler : IJobHandler<Attributed>
            {
                public Task HandleAsync(Attributed job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Attributed))]
            internal sealed partial class AttributedJson : JsonSerializerContext;
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0011", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("Tags", GeneratorTests.SourceAt(run, diagnostic));
        Assert.Empty(run.GeneratedSources);
        return diagnostic;
    }

    [Fact]
    public void JsonAttributeOnAPositionalDelegatedMember_IsBW0011()
    {
        var run = GeneratorHarness.Run(Usings + """
            namespace Acme;

            [Job("attributed")]
            public sealed record Attributed(string Id, [property: JsonPropertyName("tags")] List<string> Tags);

            public sealed class AttributedHandler : IJobHandler<Attributed>
            {
                public Task HandleAsync(Attributed job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Attributed))]
            internal sealed partial class AttributedJson : JsonSerializerContext;
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0011", diagnostic.Id);
        Assert.Equal("Tags", GeneratorTests.SourceAt(run, diagnostic));
    }

    [Fact]
    public void JsonAttributeOnAScalarMember_IsABW0018Warning_AndTheJobIsStillEmitted()
    {
        var run = GeneratorHarness.Run(Usings + """
            namespace Acme;

            [Job("scalar-attributed")]
            public sealed record ScalarAttributed([property: JsonPropertyName("id")] string Id)
            {
                [JsonIgnore]
                public int Count { get; init; }
            }

            public sealed class ScalarAttributedHandler : IJobHandler<ScalarAttributed>
            {
                public Task HandleAsync(ScalarAttributed job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        Assert.All(run.GeneratorDiagnostics, d =>
        {
            Assert.Equal("BW0018", d.Id);
            Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        });
        Assert.Equal(
            ["Count: [JsonIgnore]", "Id: [JsonPropertyName]"],
            run.GeneratorDiagnostics
                .Select(d => $"{GeneratorTests.SourceAt(run, d)}: [{d.GetMessage().Split('[', ']')[1]}]")
                .Order());
        Assert.Contains(run.GeneratedSources.Keys, k => k.Contains("Acme_ScalarAttributed"));
        AssertNoDiagnostics(run.CompilationDiagnostics);
    }

    [Fact]
    public void TwoJsonAttributesOnAScalarMember_AreOneBW0018Warning()
    {
        var run = GeneratorHarness.Run(Usings + """
            namespace Acme;

            [Job("scalar-attributed")]
            public sealed record ScalarAttributed(
                [property: JsonPropertyName("id")][property: JsonRequired] string Id);

            public sealed class ScalarAttributedHandler : IJobHandler<ScalarAttributed>
            {
                public Task HandleAsync(ScalarAttributed job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0018", diagnostic.Id);
        Assert.Equal("Id", GeneratorTests.SourceAt(run, diagnostic));
        Assert.Equal(
            "Member 'Id' of job payload 'ScalarAttributed' has [JsonPropertyName] and [JsonRequired], which have no " +
            "effect - the generated codec writes this member itself and does not read System.Text.Json attributes. " +
            "Remove the attributes, or register this job by hand with JobRegistration.Create.",
            diagnostic.GetMessage());
        Assert.Contains(run.GeneratedSources.Keys, k => k.Contains("Acme_ScalarAttributed"));
    }

    [Fact]
    public void DerivedJsonConverterAttributeOnAMember_IsBW0011OnADelegatedMember_AndBW0018OnAScalarMember()
    {
        var run = GeneratorHarness.Run(Usings + """
            using System.Text.Json;

            namespace Acme;

            [Job("converted")]
            public sealed record Converted([property: Shout] string Id, [property: Shout] List<string> Tags);

            public sealed class ShoutConverter : JsonConverter<string>
            {
                public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                    => reader.GetString()!;

                public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
                    => writer.WriteStringValue(value.ToUpperInvariant());
            }

            public sealed class ShoutAttribute() : JsonConverterAttribute(typeof(ShoutConverter));

            public sealed class ConvertedHandler : IJobHandler<Converted>
            {
                public Task HandleAsync(Converted job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Converted))]
            internal sealed partial class ConvertedJson : JsonSerializerContext;
            """);

        Assert.Equal(
            ["BW0011 Tags: [Shout]", "BW0018 Id: [Shout]"],
            run.GeneratorDiagnostics
                .Select(d => $"{d.Id} {GeneratorTests.SourceAt(run, d)}: [{d.GetMessage().Split('[', ']')[1]}]")
                .Order());
    }

    [Fact]
    public void AttributeOutsideSystemTextJsonOnAMember_IsNeitherBW0011NorBW0018()
    {
        var run = GeneratorHarness.Run(Usings + """
            namespace Acme;

            [Job("marked")]
            public sealed record Marked(
                [property: Obsolete("scalar")] string Id,
                [property: Obsolete("delegated")] List<string> Tags);

            public sealed class MarkedHandler : IJobHandler<Marked>
            {
                public Task HandleAsync(Marked job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Marked))]
            internal sealed partial class MarkedJson : JsonSerializerContext;
            """);

        Assert.Empty(run.GeneratorDiagnostics);
        Assert.Contains(run.GeneratedSources.Keys, k => k.Contains("Acme_Marked"));
    }

    [Fact]
    public void JsonAttributeOnAScalarMember_NextToAMemberThatFails_StillGivesTheBW0018Warning()
    {
        var run = GeneratorHarness.Run(Usings + """
            namespace Acme;

            [Job("half-broken")]
            public sealed record HalfBroken([property: JsonPropertyName("id")] string Id, object Blob);

            public sealed class HalfBrokenHandler : IJobHandler<HalfBroken>
            {
                public Task HandleAsync(HalfBroken job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        Assert.Equal(
            ["BW0004 Blob", "BW0018 Id"],
            run.GeneratorDiagnostics.Select(d => $"{d.Id} {GeneratorTests.SourceAt(run, d)}").Order());
    }

    [Fact]
    public void JsonAttributeOnAScalarMember_OfAPayloadWithBW0016_StillGivesTheBW0018Warning()
    {
        var run = GeneratorHarness.Run(ShipConverterSource + """
            [Job("ship")]
            [JsonConverter(typeof(ShipConverter))]
            public sealed record Ship([property: JsonPropertyName("id")] string Id, Addr? Dest);

            [JsonSerializable(typeof(Ship))]
            internal sealed partial class BJobs : JsonSerializerContext;
            """);

        Assert.Equal(
            ["BW0016", "BW0018"],
            run.GeneratorDiagnostics.Select(d => d.Id).Order());
    }

    /// <summary>
    /// A class payload whose constructor parameter is wider than its property. <paramref name="parameterless"/>
    /// adds a public parameterless constructor, which STJ binds instead, so the context has metadata only for
    /// the property type.
    /// </summary>
    private static string WiderParameterSource(string parameterType, string propertyType, bool parameterless) => Usings + $$"""
        namespace Acme;

        [Job("tag-it")]
        public sealed class Tagged
        {
            {{(parameterless ? "public Tagged() { }" : "")}}

            public Tagged(string id, {{parameterType}} tags)
            {
                Id = id;
                Tags = new(tags);
            }

            public string Id { get; set; } = "";

            public {{propertyType}} Tags { get; set; } = new();
        }

        public sealed class TaggedHandler : IJobHandler<Tagged>
        {
            public Task HandleAsync(Tagged job, JobContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        [JsonSerializable(typeof(Tagged))]
        internal sealed partial class TagJson : JsonSerializerContext;

        public static class Probe
        {
            public static string Write() => Wire(new Tagged("t-1", new HashSet<string> { "a", "b" }));

            public static string Reread(string json)
                => Wire(TaggedWire.Deserialize(System.Text.Encoding.UTF8.GetBytes(json)));

            private static string Wire(Tagged value)
                => System.Text.Encoding.UTF8.GetString(TaggedWire.Serialize(value));
        }
        """;

    [Theory]
    [InlineData("IEnumerable<string>", "List<string>", true)]
    [InlineData("IEnumerable<string>", "List<string>", false)]
    [InlineData("IReadOnlySet<string>", "HashSet<string>", false)]
    public void ConstructorParameterWiderThanItsProperty_DelegatesAsThePropertyType_AndRoundTrips(
        string parameterType, string propertyType, bool parameterless)
    {
        // STJ always has metadata for a property type, but for a parameter type only when it binds that
        // constructor. With a parameterless constructor, it does not. A parameter type that STJ cannot read
        // back, such as IReadOnlySet, does not matter: the codec reads the property type.
        var probe = CompileProbe(WiderParameterSource(parameterType, propertyType, parameterless));

        var json = probe.Call("Write");
        Assert.Equal("""{"Id":"t-1","Tags":["a","b"]}""", json);
        Assert.Equal(json, probe.Call("Reread", json));
    }

    [Fact]
    public void PropertyTypeThatDoesNotConvertToItsConstructorParameter_IsBW0004()
    {
        var run = GeneratorHarness.Run(
            WiderParameterSource("List<string>", "IEnumerable<string>", parameterless: false), withJsonGenerator: true);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0004", diagnostic.Id);
        Assert.Contains(
            "the property type 'System.Collections.Generic.IEnumerable<string>' does not convert to the constructor " +
            "parameter type 'System.Collections.Generic.List<string>'",
            diagnostic.GetMessage());
        Assert.DoesNotContain(run.GeneratedSources.Keys, hint => hint.Contains("Tagged"));
    }

    [Fact]
    public void ScalarConstructorParameterOfAComplexProperty_IsBW0004()
    {
        var run = GeneratorHarness.Run(Usings + """
            namespace Acme;

            public sealed record Money(decimal Value);

            [Job("charge")]
            public sealed class Charge
            {
                public Charge(string id, decimal amount)
                {
                    Id = id;
                    Amount = new Money(amount);
                }

                public string Id { get; }

                public Money Amount { get; }
            }

            public sealed class ChargeHandler : IJobHandler<Charge>
            {
                public Task HandleAsync(Charge job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Charge))]
            internal sealed partial class ChargeJson : JsonSerializerContext;
            """, withJsonGenerator: true);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0004", diagnostic.Id);
        Assert.Equal("Amount", GeneratorTests.SourceAt(run, diagnostic));
        Assert.Equal(
            "Member 'Amount' of job payload 'Charge' (type 'Acme.Money') is not supported by the generated codec: " +
            "the constructor parameter type 'decimal' is not the property type 'Acme.Money', so the codec cannot " +
            "write the value that it passes to the constructor - make the two types the same, or register this " +
            "job by hand with JobRegistration.Create",
            diagnostic.GetMessage());
        Assert.DoesNotContain(run.GeneratedSources.Keys, hint => hint.Contains("Charge"));
        AssertNoDiagnostics(run.CompilationDiagnostics);
    }

    [Fact]
    public void ConstructorParameterWiderThanItsProperty_ReadsAMissingMemberAsTheParameterDefault()
    {
        // An older payload without the member, or with a JSON null, gets the default the user wrote, so the
        // constructor fallback runs.
        var probe = CompileProbe(Usings + """
            namespace Acme;

            public record struct Point(int X, int Y);

            [Job("widen")]
            public sealed class Widened
            {
                public Widened(string id, Point? at = null)
                {
                    Id = id;
                    At = at ?? new Point(-1, -1);
                }

                public string Id { get; }

                public Point At { get; }
            }

            public sealed class WidenedHandler : IJobHandler<Widened>
            {
                public Task HandleAsync(Widened job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Widened))]
            internal sealed partial class WidenJson : JsonSerializerContext;

            public static class Probe
            {
                public static string Reread(string json)
                    => System.Text.Encoding.UTF8.GetString(
                        WidenedWire.Serialize(WidenedWire.Deserialize(System.Text.Encoding.UTF8.GetBytes(json))));
            }
            """);

        Assert.Equal(
            """{"Id":"w-1","At":{"X":-1,"Y":-1}}""",
            probe.Call("Reread", """{"Id":"w-1"}"""));
        Assert.Equal(
            """{"Id":"w-1","At":{"X":-1,"Y":-1}}""",
            probe.Call("Reread", """{"Id":"w-1","At":null}"""));
    }

    [Fact]
    public void JsonElementMember_HoldingAJsonNull_RoundTrips()
    {
        // BW0004 tells the user to use JsonElement for an untyped value, and a JSON null is a value there.
        var probe = CompileProbe(Usings + """
            using System.Text.Json;

            namespace Acme;

            [Job("store-raw")]
            public sealed record StoreRaw(string Id, JsonElement Raw, JsonElement? MaybeRaw);

            public sealed class StoreRawHandler : IJobHandler<StoreRaw>
            {
                public Task HandleAsync(StoreRaw job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(StoreRaw))]
            internal sealed partial class RawJson : JsonSerializerContext;

            public static class Probe
            {
                public static string Reread(string json)
                    => System.Text.Encoding.UTF8.GetString(
                        StoreRawWire.Serialize(StoreRawWire.Deserialize(System.Text.Encoding.UTF8.GetBytes(json))));
            }
            """);

        const string Json = """{"Id":"r-1","Raw":null,"MaybeRaw":null}""";
        Assert.Equal(Json, probe.Call("Reread", Json));
        Assert.Equal(Json, probe.Call("Reread", """{"Id":"r-1"}"""));
        Assert.Equal(
            """{"Id":"r-1","Raw":{"a":[1]},"MaybeRaw":null}""",
            probe.Call("Reread", """{"Id":"r-1","Raw":{"a":[1]},"MaybeRaw":null}"""));
    }

    [Fact]
    public void JsonElementMember_LeftUnset_WritesNullAndRoundTrips()
    {
        // STJ cannot write an Undefined JsonElement, so it writes as null. The null reads back as kind Null, which writes
        // the same bytes.
        var probe = CompileProbe(Usings + """
            using System.Text.Json;

            namespace Acme;

            [Job("note")]
            public sealed record Note(string Id)
            {
                public JsonElement Extra { get; init; }

                public JsonElement? Maybe { get; init; }

                public JsonElement? MaybeUnset { get; init; } = default(JsonElement);
            }

            public sealed class NoteHandler : IJobHandler<Note>
            {
                public Task HandleAsync(Note job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Note))]
            internal sealed partial class NoteJson : JsonSerializerContext;

            public static class Probe
            {
                public static string Write() => System.Text.Encoding.UTF8.GetString(NoteWire.Serialize(new Note("n-1")));

                public static string Reread(string json)
                    => System.Text.Encoding.UTF8.GetString(
                        NoteWire.Serialize(NoteWire.Deserialize(System.Text.Encoding.UTF8.GetBytes(json))));
            }
            """);

        const string Json = """{"Id":"n-1","Extra":null,"Maybe":null,"MaybeUnset":null}""";
        Assert.Equal(Json, probe.Call("Write"));
        Assert.Equal(Json, probe.Call("Reread", Json));
        Assert.Equal(Json, probe.Call("Reread", """{"Id":"n-1"}"""));
    }

    [Fact]
    public void JsonElementProperty_WithANullableConstructorParameter_CompilesAndRoundTrips()
    {
        var probe = CompileProbe(Usings + """
            using System.Text.Json;

            namespace Acme;

            [Job("raw")]
            public sealed class RawJob
            {
                public RawJob(string id, JsonElement? raw = null)
                {
                    Id = id;
                    Raw = raw ?? default;
                }

                public string Id { get; }

                public JsonElement Raw { get; }
            }

            public sealed class RawJobHandler : IJobHandler<RawJob>
            {
                public Task HandleAsync(RawJob job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(RawJob))]
            internal sealed partial class RawJobJson : JsonSerializerContext;

            public static class Probe
            {
                public static string Write() => System.Text.Encoding.UTF8.GetString(RawJobWire.Serialize(new RawJob("r-1")));

                public static string Reread(string json)
                    => System.Text.Encoding.UTF8.GetString(
                        RawJobWire.Serialize(RawJobWire.Deserialize(System.Text.Encoding.UTF8.GetBytes(json))));
            }
            """);

        Assert.Equal("""{"Id":"r-1","Raw":null}""", probe.Call("Write"));
        Assert.Equal("""{"Id":"r-1","Raw":null}""", probe.Call("Reread", """{"Id":"r-1"}"""));
        Assert.Equal("""{"Id":"r-1","Raw":{"a":[1]}}""", probe.Call("Reread", """{"Id":"r-1","Raw":{"a":[1]}}"""));
    }

    [Fact]
    public void ConstructorDefault_OfEveryLiteralShape_CompilesAndReadsBackForAMissingMember()
    {
        var probe = CompileProbe(Usings + """
            using System.Globalization;

            namespace Acme;

            public enum Shade { Dark = -1, Light = 1 }

            [Flags]
            public enum Mode { None = 0, Read = 1, Write = 2 }

            [Job("grade")]
            public sealed record Grade(
                string Id,
                char Mark = 'a',
                char? Quote = '\'',
                char Slash = '\\',
                double Ratio = double.NaN,
                double? MaybeRatio = double.NaN,
                float Cap = float.NegativeInfinity,
                Shade Tint = Shade.Dark,
                Shade? MaybeTint = Shade.Dark,
                Mode Modes = Mode.Read | Mode.Write);

            public sealed class GradeHandler : IJobHandler<Grade>
            {
                public Task HandleAsync(Grade job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Grade))]
            internal sealed partial class GradeJson : JsonSerializerContext;

            public static class Probe
            {
                public static string Defaults()
                {
                    var g = GradeWire.Deserialize(System.Text.Encoding.UTF8.GetBytes("{\"Id\":\"g-1\"}"));
                    return string.Join("|", g.Mark, g.Quote, g.Slash, g.Ratio.ToString(CultureInfo.InvariantCulture),
                        g.MaybeRatio?.ToString(CultureInfo.InvariantCulture), g.Cap.ToString(CultureInfo.InvariantCulture),
                        g.Tint, g.MaybeTint, g.Modes);
                }
            }
            """);

        Assert.Equal("a|'|\\|NaN|NaN|-Infinity|Dark|Dark|Read, Write", probe.Call("Defaults"));
    }

    [Theory]
    [InlineData("", "64")]
    [InlineData("[JsonSourceGenerationOptions(MaxDepth = 100)]", "100")]
    [InlineData("[JsonSourceGenerationOptions(MaxDepth = int.MaxValue)]", "999")]
    public void RecursiveMember_AtTheDeepestDepthThatSerializes_DecodesBack(string options, string deepest)
    {
        // Whatever the codec writes, it must read back: else the job enqueues and every attempt fails to decode.
        var probe = CompileProbe(Usings + $$"""
            namespace Acme;

            public sealed class Node
            {
                public Node? Next { get; set; }
            }

            [Job("walk")]
            public sealed record Walk(string Id, Node Head);

            public sealed class WalkHandler : IJobHandler<Walk>
            {
                public Task HandleAsync(Walk job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            {{options}}
            [JsonSerializable(typeof(Walk))]
            internal sealed partial class WalkJson : JsonSerializerContext;

            public static class Probe
            {
                public static string Deepest()
                {
                    byte[]? deepest = null;
                    var nodes = 0;
                    var head = new Node();
                    for (var depth = 1; depth < 1000; depth++, head = new Node { Next = head })
                    {
                        try
                        {
                            deepest = WalkWire.Serialize(new Walk("w-1", head));
                            nodes = depth;
                        }
                        catch (InvalidOperationException)
                        {
                            break;
                        }
                    }
                    WalkWire.Deserialize(deepest!);
                    return nodes.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            """);

        Assert.Equal(deepest, probe.Call("Deepest"));
    }

    [Fact]
    public void ContextWithoutMemberMetadata_FailsEachCallClearly_AndLeavesTheCodecUsable()
    {
        // A hand-written context: it lists the payload type, so the member binds to it, but it has no
        // metadata at runtime. Without the STJ generator nothing replaces its GetTypeInfo.
        var probe = CompileProbe(Usings + """
            using System.Text.Json;
            using System.Text.Json.Serialization.Metadata;

            namespace Acme;

            public sealed record Crate(string Code);

            [Job("pack-crate")]
            public sealed record PackCrate(string Id, Crate? Crate);

            public sealed class PackCrateHandler : IJobHandler<PackCrate>
            {
                public Task HandleAsync(PackCrate job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(PackCrate))]
            internal sealed class EmptyJson() : JsonSerializerContext(null)
            {
                public static EmptyJson Default { get; } = new();

                protected override JsonSerializerOptions? GeneratedSerializerOptions => null;

                public override JsonTypeInfo? GetTypeInfo(Type type) => null;
            }

            public static class Probe
            {
                public static string Write(string code)
                    => Outcome(() => System.Text.Encoding.UTF8.GetString(
                        PackCrateWire.Serialize(new PackCrate("p-1", code.Length == 0 ? null : new Crate(code)))));

                public static string Read(string json)
                    => Outcome(() => PackCrateWire.Deserialize(System.Text.Encoding.UTF8.GetBytes(json)).ToString());

                private static string Outcome(Func<string> call)
                {
                    try
                    {
                        return call();
                    }
                    catch (Exception exception)
                    {
                        return exception.GetType().Name + ": " + exception.Message;
                    }
                }
            }
            """, withJsonGenerator: false);

        const string Missing =
            "InvalidOperationException: Member 'Crate' of job payload 'PackCrate' ([Job] \"pack-crate\") cannot be " +
            "serialized: JsonSerializerContext 'Acme.EmptyJson' has no metadata for type 'Acme.Crate'. Add " +
            "[JsonSerializable(typeof(Acme.Crate))] to 'Acme.EmptyJson', or register this job by hand with " +
            "JobRegistration.Create.";

        // Each call that needs the metadata fails the same plain way, on encode and on decode.
        Assert.Equal(Missing, probe.Call("Write", "c-1"));
        Assert.Equal(Missing, probe.Call("Write", "c-1"));
        Assert.Equal(Missing, probe.Call("Read", """{"Id":"p-1","Crate":{"Code":"c-1"}}"""));

        // A call that does not need it still works: the failure did not poison the codec.
        Assert.Equal("""{"Id":"p-1","Crate":null}""", probe.Call("Write", ""));
        Assert.Equal("PackCrate { Id = p-1, Crate =  }", probe.Call("Read", """{"Id":"p-1","Crate":null}"""));
    }

    [Fact]
    public void MethodSugar_ContextWithoutMemberMetadata_NamesTheParameterAsWritten()
    {
        var probe = CompileProbe(Usings + """
            using System.Text.Json;
            using System.Text.Json.Serialization.Metadata;

            namespace Acme;

            public sealed record Crate(string Code);

            public static class Packing
            {
                [Job("pack")]
                public static Task PackAsync(Crate crate) => Task.CompletedTask;
            }

            [JsonSerializable(typeof(Crate))]
            internal sealed class EmptyJson() : JsonSerializerContext(null)
            {
                public static EmptyJson Default { get; } = new();

                protected override JsonSerializerOptions? GeneratedSerializerOptions => null;

                public override JsonTypeInfo? GetTypeInfo(Type type) => null;
            }

            public static class Probe
            {
                public static string Write()
                {
                    try
                    {
                        return System.Text.Encoding.UTF8.GetString(PackWire.Serialize(new Pack(new Crate("c-1"))));
                    }
                    catch (InvalidOperationException exception)
                    {
                        return exception.Message;
                    }
                }
            }
            """, withJsonGenerator: false);

        Assert.StartsWith("Member 'crate' of job payload 'Pack' ([Job] \"pack\")", probe.Call("Write"));
    }

    /// <summary>No warning and no error. Hidden diagnostics, such as an unused using, do not count.</summary>
    internal static void AssertNoDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        var reported = diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();
        Assert.True(reported.Count == 0, string.Join(Environment.NewLine, reported));
    }

    /// <summary>Emits the run (with the STJ generator by default), loads the assembly, and returns its static Probe class.</summary>
    private static ProbeClass CompileProbe(string source, bool withJsonGenerator = true)
    {
        var run = GeneratorHarness.Run(source, withJsonGenerator);
        Assert.Empty(run.GeneratorDiagnostics);
        AssertNoDiagnostics(run.CompilationDiagnostics);

        using var image = new MemoryStream();
        var emit = run.OutputCompilation.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var probe = Assembly.Load(image.ToArray()).GetTypes().Single(t => t.Name == "Probe");
        return new ProbeClass(probe);
    }

    private sealed record ProbeClass(Type Type)
    {
        public string Call(string method, params object[] arguments)
            => (string)Type.GetMethod(method)!.Invoke(null, arguments)!;
    }
}
