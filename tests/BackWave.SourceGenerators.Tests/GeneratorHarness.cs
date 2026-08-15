using System.Collections.Immutable;
using BackWave.SourceGenerators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace BackWave.SourceGenerators.Tests;

internal sealed record GeneratorRun(
    ImmutableArray<Diagnostic> GeneratorDiagnostics,
    ImmutableArray<Diagnostic> CompilationDiagnostics,
    IReadOnlyDictionary<string, string> GeneratedSources);

/// <summary>Runs the generator over a source string against the real BCL + BackWave references.</summary>
internal static class GeneratorHarness
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(() =>
    {
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = trusted
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(Jobs.JobAttribute).Assembly.Location));
        // BackWave.Pro carries the workflow markers (IWorkflowStep<T>, IWorkflowInput) the codec
        // generator keys off - a workflow-shaped fixture references them.
        references.Add(MetadataReference.CreateFromFile(typeof(Pro.IWorkflowInput).Assembly.Location));
        return references;
    });

    /// <summary>The same reference set the single-run harness uses — for the incrementality test's own driver.</summary>
    public static IReadOnlyList<MetadataReference> MetadataReferences => References.Value;

    public static GeneratorRun Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            "GeneratorTests",
            [CSharpSyntaxTree.ParseText(source)],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver
            .Create(new BackWaveGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);

        var generated = driver.GetRunResult().Results[0].GeneratedSources
            .ToDictionary(s => s.HintName, s => s.SourceText.ToString());

        return new GeneratorRun(
            generatorDiagnostics,
            outputCompilation.GetDiagnostics(),
            generated);
    }
}
