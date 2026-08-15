using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace BackWave.SourceGenerators.CodeFixes;

/// <summary>
/// A design-time code fix for BW0007 (a workflow Job Output or Workflow Input seed type that no
/// JsonSerializerContext lists). It offers to add <c>[JsonSerializable(typeof(T))]</c> for the
/// offending type to a JsonSerializerContext in the project, so the BackWave codec generator can
/// then wire the codec and BW0007 clears on the next build. When one context exists the attribute
/// is added to it; when several exist each is offered as a target; when none exists the fix
/// scaffolds a minimal partial context and lists the type on it.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(WorkflowCodecCodeFixProvider)), Shared]
public sealed class WorkflowCodecCodeFixProvider : CodeFixProvider
{
    private const string DiagnosticId = "BW0007";
    private const string TypeFqnKey = "TypeFqn";
    private const string GlobalPrefix = "global::";
    private const string JsonSerializableAttributeFqn = "global::System.Text.Json.Serialization.JsonSerializableAttribute";
    private const string JsonSerializerContextFqn = "global::System.Text.Json.Serialization.JsonSerializerContext";
    private const string JsonSerializerContextMetadataName = "System.Text.Json.Serialization.JsonSerializerContext";
    private const string ScaffoldContextName = "BackWaveWorkflowJsonContext";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticId);

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            if (!diagnostic.Properties.TryGetValue(TypeFqnKey, out var typeFqn) || string.IsNullOrEmpty(typeFqn))
            {
                // The generator stashes the offending type FQN in Properties; without it the fix
                // cannot know which type to list (for an output it is not the type at the location).
                continue;
            }

            await RegisterForDiagnosticAsync(context, diagnostic, typeFqn!).ConfigureAwait(false);
        }
    }

    private static async Task RegisterForDiagnosticAsync(CodeFixContext context, Diagnostic diagnostic, string typeFqn)
    {
        var project = context.Document.Project;
        var compilation = await project.GetCompilationAsync(context.CancellationToken).ConfigureAwait(false);
        if (compilation is null)
        {
            return;
        }

        var contextBase = compilation.GetTypeByMetadataName(JsonSerializerContextMetadataName);
        if (contextBase is null)
        {
            // System.Text.Json is not referenced: there is nothing to derive a context from.
            return;
        }

        var contexts = await FindJsonSerializerContextsAsync(project, contextBase, context.CancellationToken).ConfigureAwait(false);
        var typeDisplay = ShortName(typeFqn);

        if (contexts.Count == 0)
        {
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Create a JsonSerializerContext listing '{typeDisplay}'",
                    ct => ScaffoldContextAsync(context.Document, typeFqn, ct),
                    equivalenceKey: "BW0007_ScaffoldContext"),
                diagnostic);
            return;
        }

        // Several contexts are offered in the generator's own ordinal-first order, so the first
        // action listed is the type -> context binding the generator would then pick.
        foreach (var target in contexts)
        {
            var contextName = target.Symbol.Name;
            var document = target.Document;
            var declaration = target.Declaration;
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Add [JsonSerializable(typeof({typeDisplay}))] to '{contextName}'",
                    ct => AddToExistingContextAsync(document, declaration, typeFqn, ct),
                    equivalenceKey: $"BW0007_AddTo_{target.ContextFqn}"),
                diagnostic);
        }
    }

    private readonly record struct ContextTarget(
        INamedTypeSymbol Symbol, Document Document, ClassDeclarationSyntax Declaration, string ContextFqn);

    private static async Task<List<ContextTarget>> FindJsonSerializerContextsAsync(
        Project project, INamedTypeSymbol contextBase, CancellationToken cancellationToken)
    {
        var found = new List<ContextTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in project.Documents)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
            {
                continue;
            }
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (model is null)
            {
                continue;
            }
            foreach (var classDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(classDeclaration, cancellationToken) is not INamedTypeSymbol symbol
                    || !DerivesFrom(symbol, contextBase))
                {
                    continue;
                }
                var fqn = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                // A partial context yields one class node per part; the attribute only needs one part.
                if (!seen.Add(fqn))
                {
                    continue;
                }
                found.Add(new ContextTarget(symbol, document, classDeclaration, fqn));
            }
        }
        found.Sort(static (a, b) => string.CompareOrdinal(a.ContextFqn, b.ContextFqn));
        return found;
    }

    private static bool DerivesFrom(INamedTypeSymbol symbol, INamedTypeSymbol baseType)
    {
        for (var current = symbol.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<Solution> AddToExistingContextAsync(
        Document document, ClassDeclarationSyntax declaration, string typeFqn, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document.Project.Solution;
        }
        var attributeList = BuildAttributeList(typeFqn).WithAdditionalAnnotations(Formatter.Annotation);
        var newRoot = root.ReplaceNode(declaration, declaration.AddAttributeLists(attributeList));
        var newDocument = document.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDocument, Formatter.Annotation, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return formatted.Project.Solution;
    }

    private static async Task<Document> ScaffoldContextAsync(
        Document document, string typeFqn, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        var scaffold = BuildScaffoldContext(typeFqn).WithAdditionalAnnotations(Formatter.Annotation);
        SyntaxNode newRoot;
        if (root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault() is { } namespaceDeclaration)
        {
            newRoot = root.ReplaceNode(namespaceDeclaration, namespaceDeclaration.AddMembers(scaffold));
        }
        else if (root is CompilationUnitSyntax compilationUnit)
        {
            newRoot = compilationUnit.AddMembers(scaffold);
        }
        else
        {
            return document;
        }

        var newDocument = document.WithSyntaxRoot(newRoot);
        return await Formatter.FormatAsync(newDocument, Formatter.Annotation, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static AttributeListSyntax BuildAttributeList(string typeFqn)
    {
        var argument = SyntaxFactory.AttributeArgument(
            SyntaxFactory.TypeOfExpression(SyntaxFactory.ParseTypeName(typeFqn)));
        var attribute = SyntaxFactory.Attribute(
            SyntaxFactory.ParseName(JsonSerializableAttributeFqn),
            SyntaxFactory.AttributeArgumentList(SyntaxFactory.SingletonSeparatedList(argument)));
        return SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attribute));
    }

    private static ClassDeclarationSyntax BuildScaffoldContext(string typeFqn)
    {
        var baseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(JsonSerializerContextFqn));
        return SyntaxFactory.ClassDeclaration(ScaffoldContextName)
            .WithAttributeLists(SyntaxFactory.SingletonList(BuildAttributeList(typeFqn)))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.InternalKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword),
                SyntaxFactory.Token(SyntaxKind.PartialKeyword)))
            .WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(baseType)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    private static string ShortName(string typeFqn)
    {
        var name = typeFqn.StartsWith(GlobalPrefix, StringComparison.Ordinal)
            ? typeFqn.Substring(GlobalPrefix.Length)
            : typeFqn;
        var lastDot = name.LastIndexOf('.');
        return lastDot >= 0 ? name.Substring(lastDot + 1) : name;
    }
}
