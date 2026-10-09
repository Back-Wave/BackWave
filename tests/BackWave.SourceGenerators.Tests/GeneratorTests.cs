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
    public void UnlistedComplexMember_IsACompileErrorThatNamesTheTypeToList()
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
        Assert.Equal("BW0017", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(
            "No JsonSerializerContext in this assembly lists type 'Listy', which the generated codec needs for member " +
            "'Items' of job payload 'Listy'. Add [JsonSerializable(typeof(Listy))] to a JsonSerializerContext in this " +
            "assembly, or register this job by hand with JobRegistration.Create.",
            diagnostic.GetMessage());
        Assert.Equal("global::Listy", diagnostic.Properties["TypeFqn"]);
        Assert.Empty(run.GeneratedSources);
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
        Assert.Contains("member 'Items' of job payload 'Listy'", message);
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
        Assert.Contains("member 'Items' of job payload 'Propy'", message);
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

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        // The member is named as the user wrote the parameter, not as the generated record property.
        Assert.Contains("member 'recipients' of job payload 'SendBatch'", diagnostic.GetMessage());
        Assert.DoesNotContain("methody-job", diagnostic.GetMessage());
        Assert.Equal("recipients", SourceAt(run, diagnostic));
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

    [Theory]
    [InlineData("public class Worker { [Job(\"send\")] public Task Send<T>(string id) => Task.CompletedTask; }")]
    [InlineData("public class Worker<T> { [Job(\"send\")] public Task Send(string id) => Task.CompletedTask; }")]
    [InlineData("public class Outer<T> { public class Worker { [Job(\"send\")] public Task Send(string id) => Task.CompletedTask; } }")]
    public void GenericJobMethod_IsBW0005(string declaration)
    {
        // The generated handler calls the method with no type argument, so it could never compile.
        var run = GeneratorHarness.Run($$"""
            using System.Threading.Tasks;
            using BackWave.Jobs;

            {{declaration}}
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0005", diagnostic.Id);
        Assert.Contains("must not be generic", diagnostic.GetMessage());
        Assert.Empty(run.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Theory]
    [InlineData(
        "[Job(\"wrap\")] public sealed record Wrap<T>(string Id);",
        "public sealed class WrapHandler<T> : IJobHandler<Wrap<T>>",
        "Wrap<T>")]
    [InlineData(
        "public class Outer<T> { [Job(\"wrap\")] public sealed record Wrap(string Id); }",
        "public sealed class WrapHandler<T> : IJobHandler<Outer<T>.Wrap>",
        "Outer<T>.Wrap")]
    public void GenericJobType_IsBW0014(string declaration, string handler, string payload)
    {
        // A queued job names one concrete payload type, and an open generic type is not one.
        var run = GeneratorHarness.Run($$"""
            using System.Threading;
            using System.Threading.Tasks;
            using BackWave.Jobs;

            {{declaration}}

            {{handler}}
            {
                public Task HandleAsync({{payload}} job, JobContext context, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """);

        var diagnostic = Assert.Single(run.GeneratorDiagnostics);
        Assert.Equal("BW0014", diagnostic.Id);
        Assert.Empty(run.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void EditingAnUnrelatedFile_ReusesCachedGenerationOutputs()
        => AssertUnrelatedEditReusesCachedOutputs(CanonicalSource);

    /// <summary>Adds an unrelated file to a compilation of <paramref name="source"/>: no pipeline step re-runs.</summary>
    internal static void AssertUnrelatedEditReusesCachedOutputs(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create(
            "Incremental",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
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
    public void WorkflowStepOutputAndSeed_ListedInTwoJsonContexts_AreBW0012()
    {
        // Each context can apply its own options, so a silent pick between them would change the wire format
        // of stored outputs and seeds when a context is added. Both types are a build error instead.
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
            internal sealed partial class AppJson : JsonSerializerContext;

            [JsonSerializable(typeof(CheckoutSeed))]
            [JsonSerializable(typeof(InvoiceResult))]
            internal sealed partial class ApiJson : JsonSerializerContext;
            """);

        Assert.All(run.GeneratorDiagnostics, d => Assert.Equal("BW0012", d.Id));
        Assert.Collection(
            run.GeneratorDiagnostics.Select(d => d.GetMessage()),
            message => Assert.Equal(
                "Type 'Acme.InvoiceResult' (the Job Output of step 'make-invoice') is listed in more than one " +
                "JsonSerializerContext: Acme.ApiJson, Acme.AppJson. The contexts can serialize it differently, so " +
                "BackWave does not pick one - list the type in only one JsonSerializerContext, or register this job " +
                "by hand with JobRegistration.Create.", message),
            message => Assert.Equal(
                "Type 'Acme.CheckoutSeed' (a Workflow Input seed) is listed in more than one JsonSerializerContext: " +
                "Acme.ApiJson, Acme.AppJson. The contexts can serialize it differently, so BackWave does not pick " +
                "one - list the type in only one JsonSerializerContext, or remove IWorkflowInput from the type and " +
                "pass its JsonTypeInfo explicitly to start the workflow and to read the seed.", message));
        var registry = run.GeneratedSources["BackWave.Jobs.g.cs"];
        Assert.DoesNotContain("OutputTypeInfo = ", registry);
        Assert.DoesNotContain("[typeof(global::Acme.CheckoutSeed)]", registry);
    }


    [Fact]
    public void WorkflowOutputAndSeed_ListedOnlyForSerialization_AreBW0013()
    {
        // A serialization-only listing generates no metadata to read the value back with, so the codec it
        // would wire throws on every decode. Each workflow type gets its own build error instead.
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

            [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Serialization)]
            [JsonSerializable(typeof(CheckoutSeed))]
            [JsonSerializable(typeof(InvoiceResult))]
            internal sealed partial class AppJson : JsonSerializerContext;
            """);

        Assert.Equal(2, run.GeneratorDiagnostics.Length);
        Assert.All(run.GeneratorDiagnostics, d => Assert.Equal("BW0013", d.Id));
        var messages = run.GeneratorDiagnostics.Select(d => d.GetMessage()).ToList();
        Assert.Contains(messages, m => m.Contains("'Acme.InvoiceResult'") && m.Contains("the Job Output of step 'make-invoice'"));
        Assert.Contains(messages, m => m.Contains("'Acme.CheckoutSeed'") && m.Contains("a Workflow Input seed"));
        Assert.All(messages, m => Assert.Contains("GenerationMode = Serialization", m));
        Assert.DoesNotContain("OutputTypeInfo", run.GeneratedSources["BackWave.Jobs.g.cs"]);
        Assert.DoesNotContain("CheckoutSeed", run.GeneratedSources["BackWave.Jobs.g.cs"]);
    }

    [Fact]
    public void WorkflowOutputAndSeed_ListedOnlyByAPrivateContext_AreBW0013()
    {
        // The generated registry cannot name a private nested context, so wiring it would fail the build
        // with CS0122 inside generated code. Each workflow type gets a build error that says why instead.
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

            public partial class Outer
            {
                [JsonSerializable(typeof(CheckoutSeed))]
                [JsonSerializable(typeof(InvoiceResult))]
                private sealed partial class AppJson : JsonSerializerContext;
            }
            """, withJsonGenerator: true);

        Assert.Equal(2, run.GeneratorDiagnostics.Length);
        Assert.All(run.GeneratorDiagnostics, d => Assert.Equal("BW0013", d.Id));
        var messages = run.GeneratorDiagnostics.Select(d => d.GetMessage()).ToList();
        Assert.Contains(messages, m => m.Contains("'Acme.InvoiceResult'") && m.Contains("the Job Output of step 'make-invoice'"));
        Assert.Contains(messages, m => m.Contains("'Acme.CheckoutSeed'") && m.Contains("a Workflow Input seed"));
        Assert.All(messages, m => Assert.Contains("'Acme.Outer.AppJson'", m));
        Assert.All(messages, m => Assert.Contains("not accessible", m));
        Assert.Empty(run.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
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

    /// <summary>
    /// The source text that a diagnostic points at. A generator diagnostic carries a file location with no
    /// syntax tree, so the text comes from the tree of the run that has the same path.
    /// </summary>
    internal static string SourceAt(GeneratorRun run, Diagnostic diagnostic)
        => run.OutputCompilation.SyntaxTrees
            .Single(t => t.FilePath == diagnostic.Location.GetLineSpan().Path)
            .GetText()
            .ToString(diagnostic.Location.SourceSpan);
}
