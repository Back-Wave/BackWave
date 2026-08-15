using System.Collections.Generic;
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
/// Drives the BW0007 CodeFixProvider end to end: build a compilation from an AdhocWorkspace document,
/// run the generator to get the BW0007 diagnostic (carrying its Properties), let the provider register
/// actions, apply a chosen one, and hand back the fixed source so the caller can re-run the generator.
/// </summary>
internal static class CodeFixHarness
{
    /// <summary>Registers the fixes for the single BW0007 in <paramref name="source"/> without applying one.</summary>
    public static async Task<IReadOnlyList<CodeAction>> RegisterActionsAsync(string source)
    {
        var (_, actions, _) = await BuildAndRegisterAsync(source);
        return actions;
    }

    /// <summary>Applies the action selected by <paramref name="choose"/> and returns the fixed document text.</summary>
    public static async Task<CodeFixOutcome> ApplyAsync(string source, Func<IReadOnlyList<CodeAction>, CodeAction> choose)
    {
        var (documentId, actions, _) = await BuildAndRegisterAsync(source);
        var chosen = choose(actions);

        var operations = await chosen.GetOperationsAsync(CancellationToken.None);
        var apply = operations.OfType<ApplyChangesOperation>().Single();
        var fixedDocument = apply.ChangedSolution.GetDocument(documentId)!;
        var fixedText = (await fixedDocument.GetTextAsync()).ToString();
        return new CodeFixOutcome(actions, fixedText);
    }

    private static async Task<(DocumentId DocumentId, IReadOnlyList<CodeAction> Actions, Diagnostic Diagnostic)>
        BuildAndRegisterAsync(string source)
    {
        using var workspace = new AdhocWorkspace();
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
        // the CodeFixContext binds against, and run the generator to surface the real BW0007 (with Properties).
        var syntaxTree = (await document.GetSyntaxTreeAsync())!;
        var compilation = CSharpCompilation.Create(
            "CodeFixTests",
            [syntaxTree],
            GeneratorHarness.MetadataReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        CSharpGeneratorDriver
            .Create(new BackWaveGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out var generatorDiagnostics);
        var diagnostic = generatorDiagnostics.Single(d => d.Id == "BW0007");

        var provider = new WorkflowCodecCodeFixProvider();
        var actions = new List<CodeAction>();
        var codeFixContext = new CodeFixContext(
            document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
        await provider.RegisterCodeFixesAsync(codeFixContext);

        return (documentId, actions, diagnostic);
    }
}
