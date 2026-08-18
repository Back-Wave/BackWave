using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace BackWave.SourceGenerators.Tests;

public class GeneratorTests
{
    /// <summary>A record job + handler and a [Job] method, covering every member shape.</summary>
    private const string CanonicalSource = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using BackWave.Jobs;

        namespace Acme.Jobs;

        [Job("charge-card", Queue = "payments")]
        public sealed record ChargeCard(string OrderId, int Amount, Guid? CorrelationId = null, string Region = "eu")
        {
            public bool Expedited { get; init; }
        }

        public sealed class ChargeCardHandler : IJobHandler<ChargeCard>
        {
            public Task HandleAsync(ChargeCard job, JobContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        public class Notifications
        {
            [Job("send-welcome")]
            public Task SendWelcomeAsync(string userId, int retries, JobContext context, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }
        """;

    [Theory]
    [InlineData("BackWave.Acme_Jobs_ChargeCard.c6bb3839.g.cs")]
    [InlineData("BackWave.Acme_Jobs_SendWelcome.5cbc1baf.g.cs")]
    [InlineData("BackWave.Jobs.g.cs")]
    public void GeneratedOutput_MatchesSnapshot(string hintName)
    {
        var run = GeneratorHarness.Run(CanonicalSource);

        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Snapshots", hintName));
        Assert.Equal(expected.ReplaceLineEndings(), run.GeneratedSources[hintName].ReplaceLineEndings());
    }

    [Fact]
    public void GeneratedOutput_CompilesWithoutErrors()
    {
        var run = GeneratorHarness.Run(CanonicalSource);

        Assert.Empty(run.GeneratorDiagnostics);
        Assert.Empty(run.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void EmptyWireName_IsACompileError()
    {
        var run = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            [Job("")]
            public sealed record Nameless(string Id);

            public sealed class NamelessHandler : IJobHandler<Nameless>
            {
                public Task HandleAsync(Nameless job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0001", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void MethodSugarJob_WithNoPayloadParameters_CompilesWithoutErrors()
    {
        // A [Job] method whose only parameters are JobContext + CancellationToken carries no payload
        // data, so the generated payload has zero members. The Deserialize reader must still compile:
        // the unknown-property skip has to stand on its own rather than trail a property if/else chain
        // that was never emitted (an `else` with no preceding `if` is a compile error).
        var run = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            public sealed class Maintenance
            {
                [Job("nightly-sweep")]
                public Task SweepAsync(JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        Assert.Empty(run.GeneratorDiagnostics);
        Assert.Empty(run.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void DuplicateWireName_IsACompileError()
    {
        var run = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            [Job("same-name")]
            public sealed record First(string Id);

            [Job("same-name")]
            public sealed record Second(string Id);

            public sealed class FirstHandler : IJobHandler<First>
            {
                public Task HandleAsync(First job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            public sealed class SecondHandler : IJobHandler<Second>
            {
                public Task HandleAsync(Second job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0002", diagnostic.Id);
        Assert.Contains("same-name", diagnostic.GetMessage());
    }

    [Fact]
    public void MissingHandler_IsACompileError()
    {
        var run = GeneratorHarness.Run("""
            using BackWave.Jobs;

            [Job("orphan-job")]
            public sealed record Orphan(string Id);
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0003", diagnostic.Id);
        Assert.Contains("Orphan", diagnostic.GetMessage());
    }

    [Fact]
    public void UnsupportedPayloadMember_IsACompileError()
    {
        var run = GeneratorHarness.Run("""
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            [Job("listy-job")]
            public sealed record Listy(List<string> Items);

            public sealed class ListyHandler : IJobHandler<Listy>
            {
                public Task HandleAsync(Listy job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0004", diagnostic.Id);
        Assert.Contains("JobRegistration.Create", diagnostic.GetMessage());
    }

    [Fact]
    public void UnsupportedConstructorParameter_NamesThePayloadTypeNotTheWireName()
    {
        var run = GeneratorHarness.Run("""
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            [Job("listy-job")]
            public sealed record Listy(List<string> Items);

            public sealed class ListyHandler : IJobHandler<Listy>
            {
                public Task HandleAsync(Listy job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        var message = Assert.Single(run.GeneratorDiagnostics).GetMessage();
        Assert.Contains("Member 'Items' of job payload 'Listy'", message);
        Assert.DoesNotContain("listy-job", message);
    }

    [Fact]
    public void UnsupportedSettableProperty_NamesThePayloadTypeNotTheWireName()
    {
        var run = GeneratorHarness.Run("""
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            [Job("propy-job")]
            public sealed record Propy(string Name)
            {
                public List<string> Items { get; set; } = new();
            }

            public sealed class PropyHandler : IJobHandler<Propy>
            {
                public Task HandleAsync(Propy job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        var message = Assert.Single(run.GeneratorDiagnostics).GetMessage();
        Assert.Contains("Member 'Items' of job payload 'Propy'", message);
        Assert.DoesNotContain("propy-job", message);
    }

    [Fact]
    public void UnsupportedMethodJobParameter_NamesTheGeneratedPayloadRecord()
    {
        var run = GeneratorHarness.Run("""
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            public class Notifications
            {
                [Job("methody-job")]
                public Task SendBatchAsync(List<string> recipients) => Task.CompletedTask;
            }
            """);

        var message = Assert.Single(run.GeneratorDiagnostics).GetMessage();
        Assert.Contains("Member 'recipients' of job payload 'SendBatch'", message);
        Assert.DoesNotContain("methody-job", message);
    }

    [Fact]
    public void NonTaskJobMethod_IsACompileError()
    {
        var run = GeneratorHarness.Run("""
            using BackWave.Jobs;

            public class Worker
            {
                [Job("fire-and-forget")]
                public void Run(string id) { }
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0005", diagnostic.Id);
    }

    [Fact]
    public void EditingAnUnrelatedFile_ReusesCachedGenerationOutputs()
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create(
            "Incremental",
            [CSharpSyntaxTree.ParseText(CanonicalSource, parseOptions)],
            GeneratorHarness.MetadataReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new BackWaveGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);

        // Add an unrelated file: no [Job], no handler — nothing the generator depends on.
        var edited = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "namespace Unrelated; public class Bystander { public int Value; }", parseOptions));
        var result = driver.RunGenerators(edited).GetRunResult().Results[0];

        // Every cached pipeline step is reused; no full re-run from an unrelated edit.
        foreach (var stepName in new[]
                 {
                     BackWaveGenerator.ModelsStep, BackWaveGenerator.HandlersStep, BackWaveGenerator.EmitInputStep,
                 })
        {
            Assert.All(
                result.TrackedSteps[stepName].SelectMany(step => step.Outputs),
                output => Assert.True(
                    output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
                    $"{stepName} re-ran ({output.Reason}) after an unrelated edit"));
        }
    }

    [Fact]
    public void StaticJobMethod_GeneratesDirectCallHandler()
    {
        var run = GeneratorHarness.Run("""
            using System.Threading.Tasks;
            using BackWave.Jobs;

            namespace Acme;

            public static class Maintenance
            {
                [Job("compact-storage")]
                public static Task CompactStorageAsync(string tenant)
                    => Task.CompletedTask;
            }
            """);

        Assert.Empty(run.GeneratorDiagnostics);
        Assert.Empty(run.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        var source = run.GeneratedSources["BackWave.Acme_CompactStorage.d7e75095.g.cs"];
        Assert.Contains("Maintenance.CompactStorageAsync(job.Tenant)", source);
        Assert.Contains("sealed record CompactStorage(string Tenant);", source);
    }

    [Fact]
    public void JobLabels_FlowIntoTheGeneratedRegistrationsDefaultTags()
    {
        var run = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            namespace Acme;

            [Job("nightly-report", Labels = new[] { "urgent", "report" })]
            public sealed record NightlyReport(string Region);

            public sealed class NightlyReportHandler : IJobHandler<NightlyReport>
            {
                public Task HandleAsync(NightlyReport job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        Assert.Empty(run.GeneratorDiagnostics);
        Assert.Empty(run.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        var registry = run.GeneratedSources["BackWave.Jobs.g.cs"];
        Assert.Contains(
            """DefaultTags = global::BackWave.Storage.JobTags.Empty.WithLabel("urgent").WithLabel("report"),""",
            registry);
    }

    [Fact]
    public void JobRetry_FlowsIntoTheGeneratedRegistrationsRetryDisposition()
    {
        var run = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            namespace Acme;

            [Job("charge-card")]
            [Retry(3, 1, 5)]
            public sealed record ChargeCard(string OrderId);

            public sealed class ChargeCardHandler : IJobHandler<ChargeCard>
            {
                public Task HandleAsync(ChargeCard job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        Assert.Empty(run.GeneratorDiagnostics);
        Assert.Empty(run.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        var registry = run.GeneratedSources["BackWave.Jobs.g.cs"];
        Assert.Contains(
            "Retry = global::BackWave.Core.RetryDisposition.FromIntervals(3, new global::System.TimeSpan[] "
                + "{ global::System.TimeSpan.FromSeconds(1d), global::System.TimeSpan.FromSeconds(5d) }),",
            registry);
    }

    [Fact]
    public void NoRetryAttribute_EmitsNoRetryOverride()
    {
        var run = GeneratorHarness.Run(CanonicalSource);

        var registry = run.GeneratedSources["BackWave.Jobs.g.cs"];
        Assert.DoesNotContain("Retry = ", registry);
    }

    [Fact]
    public void RetryWithACeilingBelowOne_ReportsBW0008_InsteadOfSilentlyDroppingTheOverride()
    {
        var run = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            namespace Acme;

            [Job("charge-card")]
            [Retry(0, 1, 5)]
            public sealed record ChargeCard(string OrderId);

            public sealed class ChargeCardHandler : IJobHandler<ChargeCard>
            {
                public Task HandleAsync(ChargeCard job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0008", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void GeneratorRetryBounds_MatchTheRuntimeConstants_SoRaisingOneCannotDriftUnnoticed()
    {
        // The generator cannot reference the runtime type, so it duplicates these bounds. If they drift,
        // valid code fails BW0008/BW0009 or a huge literal reaches FromIntervals. This is the tripwire.
        Assert.Equal(BackWave.Core.RetryDisposition.MaxBackoffIntervals, BackWaveGenerator.MaxBackoffIntervals);
        Assert.Equal(BackWave.Core.RetryDisposition.MaxAttemptCeiling, BackWaveGenerator.MaxAttemptCeiling);
    }

    [Fact]
    public void GeneratorBackoffGate_AcceptsAndRejectsTheSameShapesAsFromIntervals()
    {
        // The generator's DescribeInvalidBackoff duplicates the structural rules FromIntervals enforces
        // (empty, negative, more than the cap, TimeSpan range). Drive one shared set of shapes through
        // both and assert identical accept/reject, so the two copies cannot drift apart unnoticed.
        var cases = new (string Literal, double[] Values)[]
        {
            ("1, 5", [1.0, 5.0]),
            ("", []),
            ("-1", [-1.0]),
            ("1e-9", [1e-9]),
            (
                string.Join(", ", Enumerable.Repeat("1", BackWaveGenerator.MaxBackoffIntervals + 1)),
                [.. Enumerable.Repeat(1.0, BackWaveGenerator.MaxBackoffIntervals + 1)]),
            ("double.PositiveInfinity", [double.PositiveInfinity]),
            ("double.NaN", [double.NaN]),
            ("1e20", [1e20]),
        };

        foreach (var (literal, values) in cases)
        {
            var arguments = literal.Length == 0 ? "3" : $"3, {literal}";
            var run = GeneratorHarness.Run($$"""
                using System.Threading;
                using System.Threading.Tasks;
                using BackWave.Jobs;

                namespace Acme;

                [Job("charge-card")]
                [Retry({{arguments}})]
                public sealed record ChargeCard(string OrderId);

                public sealed class ChargeCardHandler : IJobHandler<ChargeCard>
                {
                    public Task HandleAsync(ChargeCard job, JobContext context, CancellationToken cancellationToken)
                        => Task.CompletedTask;
                }
                """);

            var generatorRejects = run.GeneratorDiagnostics.Any(diagnostic => diagnostic.Id == "BW0009");
            Assert.Equal(FromIntervalsRejectsBackoff(values), generatorRejects);
        }
    }

    private static bool FromIntervalsRejectsBackoff(double[] seconds)
    {
        try
        {
            BackWave.Core.RetryDisposition.FromIntervals(3, Array.ConvertAll(seconds, TimeSpan.FromSeconds));
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            return true;
        }
    }

    [Fact]
    public void RetryWithACeilingAboveTheCap_ReportsBW0008_InsteadOfAllocatingAtStartup()
    {
        var run = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            namespace Acme;

            [Job("charge-card")]
            [Retry(1001, 1)]
            public sealed record ChargeCard(string OrderId);

            public sealed class ChargeCardHandler : IJobHandler<ChargeCard>
            {
                public Task HandleAsync(ChargeCard job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0008", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void RetryWithoutJob_ReportsBW0010_InsteadOfSilentlyIgnoringTheOverride()
    {
        var run = GeneratorHarness.Run("""
            using BackWave.Jobs;

            namespace Acme;

            [Retry(3, 1, 5)]
            public sealed record ChargeCard(string OrderId);
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0010", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void RetryWithNoBackoffIntervals_ReportsBW0009_InsteadOfCrashingAtStartup()
    {
        var run = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            namespace Acme;

            [Job("charge-card")]
            [Retry(3)]
            public sealed record ChargeCard(string OrderId);

            public sealed class ChargeCardHandler : IJobHandler<ChargeCard>
            {
                public Task HandleAsync(ChargeCard job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0009", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void RetryWithANegativeBackoffInterval_ReportsBW0009()
    {
        var run = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            namespace Acme;

            [Job("charge-card")]
            [Retry(3, 1, -5)]
            public sealed record ChargeCard(string OrderId);

            public sealed class ChargeCardHandler : IJobHandler<ChargeCard>
            {
                public Task HandleAsync(ChargeCard job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0009", diagnostic.Id);
    }

    [Fact]
    public void RetryWithMoreThanTwentyBackoffIntervals_ReportsBW0009()
    {
        var run = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            namespace Acme;

            [Job("charge-card")]
            [Retry(3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1)]
            public sealed record ChargeCard(string OrderId);

            public sealed class ChargeCardHandler : IJobHandler<ChargeCard>
            {
                public Task HandleAsync(ChargeCard job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0009", diagnostic.Id);
    }

    [Fact]
    public void RetryWithAnOutOfRangeBackoffInterval_ReportsBW0009_InsteadOfEmittingCodeThatThrows()
    {
        var run = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            namespace Acme;

            [Job("charge-card")]
            [Retry(3, 1, 1e20)]
            public sealed record ChargeCard(string OrderId);

            public sealed class ChargeCardHandler : IJobHandler<ChargeCard>
            {
                public Task HandleAsync(ChargeCard job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0009", diagnostic.Id);
    }

    [Fact]
    public void RetryWithASubTickNegativeBackoff_IsAccepted_MatchingFromIntervalsRounding()
    {
        var run = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            namespace Acme;

            [Job("charge-card")]
            [Retry(3, 1, -1e-9)]
            public sealed record ChargeCard(string OrderId);

            public sealed class ChargeCardHandler : IJobHandler<ChargeCard>
            {
                public Task HandleAsync(ChargeCard job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        Assert.Empty(run.GeneratorDiagnostics);
        Assert.Empty(run.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void SameBareTypeNameInDifferentNamespaces_DoesNotCollide()
    {
        var run = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            namespace Acme.Foo
            {
                [Job("foo-order")]
                public sealed record Order(string Id);

                public sealed class OrderHandler : IJobHandler<Order>
                {
                    public Task HandleAsync(Order job, JobContext context, CancellationToken cancellationToken)
                        => Task.CompletedTask;
                }
            }

            namespace Acme.Bar
            {
                [Job("bar-order")]
                public sealed record Order(string Id);

                public sealed class OrderHandler : IJobHandler<Order>
                {
                    public Task HandleAsync(Order job, JobContext context, CancellationToken cancellationToken)
                        => Task.CompletedTask;
                }
            }
            """);

        // Distinct namespaces ⇒ distinct FQN-keyed hints ⇒ both jobs emit cleanly (no opaque
        // duplicate-hint crash, no diagnostic).
        Assert.Empty(run.GeneratorDiagnostics);
        Assert.Empty(run.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Contains(run.GeneratedSources.Keys, k => k.Contains("Acme_Foo_Order"));
        Assert.Contains(run.GeneratedSources.Keys, k => k.Contains("Acme_Bar_Order"));
    }

    [Fact]
    public void SameMethodSugarNameInOneNamespace_IsACompileError()
    {
        var run = GeneratorHarness.Run("""
            using System.Threading.Tasks;
            using BackWave.Jobs;

            namespace Acme;

            public class Outbound
            {
                [Job("send-a")]
                public Task Send(string to) => Task.CompletedTask;
            }

            public class Inbound
            {
                [Job("send-b")]
                public Task Send(string from) => Task.CompletedTask;
            }
            """);

        // Both method-sugar jobs would generate record/handler/wire 'Send' in namespace Acme
        // (CS0101). Expect a clean BW0006 instead of the duplicate-type compiler error.
        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0006", diagnostic.Id);
        Assert.Contains("Send", diagnostic.GetMessage());
    }

    [Fact]
    public void WorkflowStepOutputAndSeed_WireTheirCodecsFromTheJsonContext()
    {
        // A workflow step that produces a Job Output plus a Workflow Input seed, both listed in the app's
        // JsonSerializerContext: the generator wires the output codec onto the registration and emits the
        // seed-codec map, so a consumer passes no JsonTypeInfo anywhere. (Compilation is not asserted here:
        // AppJson.Default is produced by STJ's own source generator, which this single-generator harness
        // does not run - the BackWave.Tests rewrite proves the end-to-end compile+run.)
        var run = GeneratorHarness.Run("""
            using System.Text.Json.Serialization;
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;
            using BackWave.Pro;

            namespace Acme;

            public sealed record CheckoutSeed(string OrderId) : IWorkflowInput;

            public sealed record InvoiceResult(string OrderId, int Cents);

            [Job("make-invoice")]
            public sealed record MakeInvoice(string OrderId) : IWorkflowStep<InvoiceResult>;

            public sealed class MakeInvoiceHandler : IJobHandler<MakeInvoice>
            {
                public Task HandleAsync(MakeInvoice job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(CheckoutSeed))]
            [JsonSerializable(typeof(InvoiceResult))]
            [JsonSerializable(typeof(MakeInvoice))]
            internal sealed partial class AppJson : JsonSerializerContext;
            """);

        Assert.Empty(run.GeneratorDiagnostics);
        var registry = run.GeneratedSources["BackWave.Jobs.g.cs"];
        // The step's Job Output codec is sourced from the consumer's context - no outputTypeInfo passed.
        Assert.Contains(
            "OutputTypeInfo = global::Acme.AppJson.Default.GetTypeInfo(typeof(global::Acme.InvoiceResult)),",
            registry);
        // The seed-codec map is emitted and wired into both the registry and the module.
        Assert.Contains("public static global::System.Collections.Generic.IReadOnlyDictionary", registry);
        Assert.Contains(
            "[typeof(global::Acme.CheckoutSeed)] = global::Acme.AppJson.Default.GetTypeInfo(typeof(global::Acme.CheckoutSeed))!,",
            registry);
        Assert.Contains(
            "new global::BackWave.Jobs.JobRegistry(CreateRegistrations(), CreateSeedCodecs())", registry);
        Assert.Contains("SeedCodecs = CreateSeedCodecs(),", registry);
    }

    [Fact]
    public void WorkflowStepOutput_NotListedInAnyJsonContext_IsACompileError()
    {
        // The step declares a Job Output type that no JsonSerializerContext lists, so the generator cannot
        // source a codec: a build error, not a silent runtime failure.
        var run = GeneratorHarness.Run("""
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
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0007", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("InvoiceResult", diagnostic.GetMessage());
        Assert.Contains("Job Output", diagnostic.GetMessage());
    }

    [Fact]
    public void WorkflowInputSeed_NotListedInAnyJsonContext_IsACompileError()
    {
        // A type marked IWorkflowInput that no JsonSerializerContext lists is the same build error, pointed
        // at the seed. (The output-less step keeps this focused on the seed diagnostic alone.)
        var run = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
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
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0007", diagnostic.Id);
        Assert.Contains("CheckoutSeed", diagnostic.GetMessage());
        Assert.Contains("Workflow Input seed", diagnostic.GetMessage());
    }

    [Fact]
    public void WorkflowStepArrayOutput_ListedInJsonContext_WiresItsCodec()
    {
        // A step whose Job Output is an array (IWorkflowStep<InvoiceResult[]>), listed as
        // [JsonSerializable(typeof(InvoiceResult[]))]: the array type is recorded by its fully qualified
        // name and resolves to the context like any other shape, so no BW0007 fires and the output codec
        // is wired from the consumer's context - an array output is not a false positive.
        var run = GeneratorHarness.Run("""
            using System.Text.Json.Serialization;
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;
            using BackWave.Pro;

            namespace Acme;

            public sealed record InvoiceResult(string OrderId, int Cents);

            [Job("make-invoices")]
            public sealed record MakeInvoices(string OrderId) : IWorkflowStep<InvoiceResult[]>;

            public sealed class MakeInvoicesHandler : IJobHandler<MakeInvoices>
            {
                public Task HandleAsync(MakeInvoices job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [JsonSerializable(typeof(InvoiceResult[]))]
            internal sealed partial class AppJson : JsonSerializerContext;
            """);

        Assert.Empty(run.GeneratorDiagnostics);
        var registry = run.GeneratedSources["BackWave.Jobs.g.cs"];
        Assert.Contains(
            "OutputTypeInfo = global::Acme.AppJson.Default.GetTypeInfo(typeof(global::Acme.InvoiceResult[])),",
            registry);
    }

    [Fact]
    public void WorkflowStepArrayOutput_NotListedInAnyJsonContext_IsACompileError()
    {
        // Same array output, but no context lists it: the diagnostic still fires for arrays (the fix only
        // stops false positives when the array IS listed, it does not silence the genuine missing-codec case).
        var run = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;
            using BackWave.Pro;

            namespace Acme;

            public sealed record InvoiceResult(string OrderId);

            [Job("make-invoices")]
            public sealed record MakeInvoices(string OrderId) : IWorkflowStep<InvoiceResult[]>;

            public sealed class MakeInvoicesHandler : IJobHandler<MakeInvoices>
            {
                public Task HandleAsync(MakeInvoices job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0007", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("InvoiceResult[]", diagnostic.GetMessage());
        Assert.Contains("Job Output", diagnostic.GetMessage());
    }

    [Fact]
    public void EnumPayloadMember_DecodesStrictly_ThrowingOnAnUnparseableToken()
    {
        var run = GeneratorHarness.Run("""
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            namespace Acme;

            public enum Priority { Low, High }

            [Job("escalate")]
            public sealed record Escalate(Priority Level, Priority? Fallback = null);

            public sealed class EscalateHandler : IJobHandler<Escalate>
            {
                public Task HandleAsync(Escalate job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        Assert.Empty(run.GeneratorDiagnostics);
        Assert.Empty(run.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        var source = run.GeneratedSources.Values.Single(s => s.Contains("Could not decode property"));
        // Non-nullable enum: an unparseable token must throw rather than silently default.
        Assert.Contains("Could not decode property 'Level' as enum", source);
        // Nullable enum: still throws on a bad token, but a JSON null stays null.
        Assert.Contains("Could not decode property 'Fallback' as enum", source);
        Assert.Contains("JsonTokenType.Null", source);
    }
}
