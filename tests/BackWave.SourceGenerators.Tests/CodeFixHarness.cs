using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BackWave.SourceGenerators;
using BackWave.SourceGenerators.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace BackWave.SourceGenerators.Tests;

internal sealed record CodeFixOutcome(IReadOnlyList<CodeAction> Actions, string FixedSource);

/// <summary>
/// Drives the JsonSerializable CodeFixProvider end to end: build a compilation from an AdhocWorkspace document,
/// run the generator to get the diagnostic (carrying its Properties), let the provider register
/// actions, apply a chosen one, and hand back the fixed source so the caller can re-run the generator.
/// </summary>
internal static class CodeFixHarness
{
    /// <summary>Registers the fixes for the single <paramref name="diagnosticId"/> in <paramref name="source"/> without applying one.</summary>
    public static async Task<IReadOnlyList<CodeAction>> RegisterActionsAsync(string source, string diagnosticId = "BW0007")
    {
        var (_, actions, _) = await BuildAndRegisterAsync(source, diagnosticId);
        return actions;
    }

    /// <summary>Applies the action selected by <paramref name="choose"/> and returns the fixed document text.</summary>
    public static async Task<CodeFixOutcome> ApplyAsync(
        string source, Func<IReadOnlyList<CodeAction>, CodeAction> choose, string diagnosticId = "BW0007")
    {
        var (documentId, actions, _) = await BuildAndRegisterAsync(source, diagnosticId);
        var chosen = choose(actions);

        var operations = await chosen.GetOperationsAsync(CancellationToken.None);
        var apply = operations.OfType<ApplyChangesOperation>().Single();
        var fixedDocument = apply.ChangedSolution.GetDocument(documentId)!;
        var fixedText = (await fixedDocument.GetTextAsync()).ToString();
        return new CodeFixOutcome(actions, fixedText);
    }

    /// <summary>
    /// Runs the provider's fix-all over every <paramref name="diagnosticId"/> in <paramref name="source"/>, for the
    /// action with <paramref name="equivalenceKey"/>, and returns the fixed document text.
    /// </summary>
    public static async Task<string> FixAllAsync(string source, string diagnosticId, string equivalenceKey)
    {
        using var workspace = new AdhocWorkspace();
        var (document, generatorDiagnostics) = await BuildAsync(workspace, source);
        // The generator reports file-path locations; the IDE maps them back onto the document before it
        // hands them to fix-all, which skips any diagnostic that is not in source.
        var tree = (await document.GetSyntaxTreeAsync())!;
        var diagnostics = generatorDiagnostics
            .Select(d => Diagnostic.Create(d.Descriptor, Location.Create(tree, d.Location.SourceSpan), d.Properties))
            .ToImmutableArray();
        var provider = new JsonSerializableCodeFixProvider();
        var fixAllContext = new FixAllContext(
            document,
            provider,
            FixAllScope.Document,
            equivalenceKey,
            [diagnosticId],
            new FixedDiagnosticProvider(diagnostics),
            CancellationToken.None);

        var action = await provider.GetFixAllProvider()!.GetFixAsync(fixAllContext);
        Assert.NotNull(action);
        var operations = await action!.GetOperationsAsync(CancellationToken.None);
        var apply = operations.OfType<ApplyChangesOperation>().Single();
        var fixedDocument = apply.ChangedSolution.GetDocument(document.Id)!;
        return (await fixedDocument.GetTextAsync()).ToString();
    }

    private static async Task<(DocumentId DocumentId, IReadOnlyList<CodeAction> Actions, Diagnostic Diagnostic)>
        BuildAndRegisterAsync(string source, string diagnosticId)
    {
        using var workspace = new AdhocWorkspace();
        var (document, generatorDiagnostics) = await BuildAsync(workspace, source);
        var diagnostic = generatorDiagnostics.Single(d => d.Id == diagnosticId);

        var provider = new JsonSerializableCodeFixProvider();
        var actions = new List<CodeAction>();
        var codeFixContext = new CodeFixContext(
            document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
        await provider.RegisterCodeFixesAsync(codeFixContext);

        return (document.Id, actions, diagnostic);
    }

    private static async Task<(Document Document, ImmutableArray<Diagnostic> GeneratorDiagnostics)> BuildAsync(
        AdhocWorkspace workspace, string source)
    {
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);

        var projectInfo = ProjectInfo
            .Create(projectId, VersionStamp.Default, "CodeFixTests", "CodeFixTests", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Latest))
            .WithMetadataReferences(GeneratorHarness.MetadataReferences);

        var solution = workspace.CurrentSolution
            .AddProject(projectInfo)
            .AddDocument(documentId, "Input.cs", SourceText.From(source));
        var document = solution.GetDocument(documentId)!;

        // Build the compilation from the document's OWN tree so the diagnostic's Location tree is the one
        // the CodeFixContext binds against, and run the generator to surface the real diagnostic (with Properties).
        var syntaxTree = (await document.GetSyntaxTreeAsync())!;
        var compilation = CSharpCompilation.Create(
            "CodeFixTests",
            [syntaxTree],
            GeneratorHarness.MetadataReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        CSharpGeneratorDriver
            .Create(new BackWaveGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out var generatorDiagnostics);
        return (document, generatorDiagnostics);
    }

    /// <summary>Hands fix-all the generator's diagnostics, which no analyzer in the workspace would report.</summary>
    private sealed class FixedDiagnosticProvider(ImmutableArray<Diagnostic> diagnostics) : FixAllContext.DiagnosticProvider
    {
        public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<Diagnostic>>(diagnostics);

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<Diagnostic>>([]);

        public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<Diagnostic>>(diagnostics);
    }
}
