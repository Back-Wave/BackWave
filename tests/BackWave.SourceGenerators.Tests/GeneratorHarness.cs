using System.Collections.Immutable;
using System.Reflection;
using BackWave.SourceGenerators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace BackWave.SourceGenerators.Tests;

internal sealed record GeneratorRun(
    ImmutableArray<Diagnostic> GeneratorDiagnostics,
    ImmutableArray<Diagnostic> CompilationDiagnostics,
    IReadOnlyDictionary<string, string> GeneratedSources,
    Compilation OutputCompilation);

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

    /// <summary>
    /// The System.Text.Json source generator from the targeting pack (the csproj copies it to the output). The
    /// STJ generator runs on the same input as the BackWave generator, so, as in a real build, it cannot see the
    /// sources that BackWave generates.
    /// </summary>
    private static readonly Lazy<ISourceGenerator> JsonGenerator = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "JsonSourceGenerator", "System.Text.Json.SourceGeneration.dll");
        var generatorType = Assembly.LoadFrom(path).GetTypes()
            .Single(t => !t.IsAbstract && typeof(IIncrementalGenerator).IsAssignableFrom(t));
        return ((IIncrementalGenerator)Activator.CreateInstance(generatorType)!).AsSourceGenerator();
    });

    /// <summary>
    /// Runs the BackWave generator over <paramref name="source"/>. With <paramref name="withJsonGenerator"/>, the
    /// System.Text.Json generator runs too, so the output compilation holds real JsonSerializerContext metadata.
    /// </summary>
    public static GeneratorRun Run(string source, bool withJsonGenerator = false)
    {
        var compilation = CSharpCompilation.Create(
            "GeneratorTests",
            [CSharpSyntaxTree.ParseText(source)],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        ISourceGenerator[] generators = withJsonGenerator
            ? [new BackWaveGenerator().AsSourceGenerator(), JsonGenerator.Value]
            : [new BackWaveGenerator().AsSourceGenerator()];
        var driver = CSharpGeneratorDriver
            .Create(generators)
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);

        var generated = driver.GetRunResult().Results[0].GeneratedSources
            .ToDictionary(s => s.HintName, s => s.SourceText.ToString());

        return new GeneratorRun(
            generatorDiagnostics,
            outputCompilation.GetDiagnostics(),
            generated,
            outputCompilation);
    }
}
