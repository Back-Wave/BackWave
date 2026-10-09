using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Globalization;
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
/// A design-time code fix for BW0017 (a job payload type that no JsonSerializerContext lists) and
/// BW0007 (a workflow Job Output or Workflow Input seed type that no JsonSerializerContext lists). It
/// offers to add <c>[JsonSerializable(typeof(T))]</c> for the type the diagnostic names to a
/// JsonSerializerContext in the project, so the BackWave generator can then wire the codec and the
/// diagnostic clears on the next build. When one context exists the attribute is added to it; when
/// several exist each is offered as a target; when none exists the fix scaffolds a minimal partial
/// context and lists the type on it. A context that the generated codec cannot use is not a target: a
/// private, protected, or file-local context, or one that generates serialization-only code. For BW0017 a context
/// with any serialization-only listing or default is not a target either, because the generator rejects it for a
/// job payload.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(JsonSerializableCodeFixProvider)), Shared]
public sealed class JsonSerializableCodeFixProvider : CodeFixProvider
{
    private const string TypeFqnKey = "TypeFqn";
    private const string JsonSerializableAttributeFqn = "global::System.Text.Json.Serialization.JsonSerializableAttribute";
    private const string JsonSerializerContextFqn = "global::System.Text.Json.Serialization.JsonSerializerContext";
    private const string JsonSerializerContextMetadataName = "System.Text.Json.Serialization.JsonSerializerContext";
    private const string ScaffoldContextName = "BackWaveJsonContext";
    private const string UnlistedPayloadTypeId = "BW0017";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("BW0007", "BW0017");

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => ListEveryTypeFixAllProvider.Instance;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var project = context.Document.Project;
        var compilation = await project.GetCompilationAsync(context.CancellationToken).ConfigureAwait(false);
        if (compilation?.GetTypeByMetadataName(JsonSerializerContextMetadataName) is null)
        {
            // System.Text.Json is not referenced: there is nothing to derive a context from.
            return;
        }

        // The targets depend only on the diagnostic ID, so each ID walks the project once.
        var contextsById = new Dictionary<string, List<ContextTarget>>(StringComparer.Ordinal);
        foreach (var diagnostic in context.Diagnostics)
        {
            if (!diagnostic.Properties.TryGetValue(TypeFqnKey, out var typeFqn) || string.IsNullOrEmpty(typeFqn))
            {
                // The generator stashes the offending type FQN in Properties; without it the fix
                // cannot know which type to list (for an output it is not the type at the location).
                continue;
            }

            if (!contextsById.TryGetValue(diagnostic.Id, out var contexts))
            {
                contexts = await FindJsonSerializerContextsAsync(project, diagnostic.Id, context.CancellationToken)
                    .ConfigureAwait(false);
                contextsById.Add(diagnostic.Id, contexts);
            }
            RegisterForDiagnostic(context, diagnostic, typeFqn!, contexts);
        }
    }

    private static void RegisterForDiagnostic(
        CodeFixContext context, Diagnostic diagnostic, string typeFqn, IReadOnlyList<ContextTarget> contexts)
    {
        var typeDisplay = ShortName(typeFqn);

        if (contexts.Count == 0)
        {
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Create a JsonSerializerContext listing '{typeDisplay}'",
                    ct => ScaffoldContextAsync(context.Document, [typeFqn], ct),
                    equivalenceKey: $"{diagnostic.Id}_ScaffoldContext"),
                diagnostic);
            return;
        }

        // Several contexts are offered in ordinal order, so the order of the actions is stable.
        foreach (var target in contexts)
        {
            var contextName = target.Symbol.Name;
            var document = target.Document;
            var declaration = target.Declaration;
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Add [JsonSerializable(typeof({typeDisplay}))] to '{contextName}'",
                    ct => AddToExistingContextAsync(document, declaration, [typeFqn], ct),
                    equivalenceKey: $"{diagnostic.Id}_AddTo_{target.ContextFqn}"),
                diagnostic);
        }
    }

    private readonly record struct ContextTarget(
        INamedTypeSymbol Symbol, Document Document, ClassDeclarationSyntax Declaration, string ContextFqn);

    private static async Task<List<ContextTarget>> FindJsonSerializerContextsAsync(
        Project project, string diagnosticId, CancellationToken cancellationToken)
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
                    || !JsonSerializerContextRule.IsJsonSerializerContext(symbol)
                    || JsonSerializerContextRule.UnusableReason(symbol, model.Compilation, listing: null) is not null
                    || (diagnosticId == UnlistedPayloadTypeId
                        && JsonSerializerContextRule.SerializationOnlyReason(symbol) is not null))
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

    private static async Task<Solution> AddToExistingContextAsync(
        Document document, ClassDeclarationSyntax declaration, IReadOnlyList<string> typeFqns, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document.Project.Solution;
        }
        var attributeLists = typeFqns
            .Select(typeFqn => BuildAttributeList(typeFqn).WithAdditionalAnnotations(Formatter.Annotation))
            .ToArray();
        var newRoot = root.ReplaceNode(declaration, declaration.AddAttributeLists(attributeLists));
        var newDocument = document.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDocument, Formatter.Annotation, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return formatted.Project.Solution;
    }

    private static async Task<Document> ScaffoldContextAsync(
        Document document, IReadOnlyList<string> typeFqns, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            return document;
        }

        SyntaxNode newRoot;
        if (root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault() is { } namespaceDeclaration)
        {
            if (model.GetDeclaredSymbol(namespaceDeclaration, cancellationToken) is not INamespaceSymbol namespaceSymbol)
            {
                return document;
            }
            newRoot = root.ReplaceNode(
                namespaceDeclaration, namespaceDeclaration.AddMembers(BuildScaffoldContext(namespaceSymbol, typeFqns)));
        }
        else if (root is CompilationUnitSyntax compilationUnit)
        {
            newRoot = compilationUnit.AddMembers(BuildScaffoldContext(model.Compilation.GlobalNamespace, typeFqns));
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

    /// <summary>
    /// A new context in <paramref name="target"/>. The name is free in that namespace, so the new class does not
    /// merge into an existing partial type with the same name.
    /// </summary>
    private static ClassDeclarationSyntax BuildScaffoldContext(INamespaceSymbol target, IReadOnlyList<string> typeFqns)
    {
        var name = ScaffoldContextName;
        for (var suffix = 2; !target.GetTypeMembers(name).IsEmpty; suffix++)
        {
            name = ScaffoldContextName + suffix.ToString(CultureInfo.InvariantCulture);
        }

        var baseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(JsonSerializerContextFqn));
        return SyntaxFactory.ClassDeclaration(name)
            .WithAttributeLists(SyntaxFactory.List(typeFqns.Select(BuildAttributeList)))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.InternalKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword),
                SyntaxFactory.Token(SyntaxKind.PartialKeyword)))
            .WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(baseType)))
            .WithOpenBraceToken(default)
            .WithCloseBraceToken(default)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithAdditionalAnnotations(Formatter.Annotation);
    }

    /// <summary>
    /// Fix-all for one action: lists every type its diagnostics name in a single edit per project, on the context the
    /// action targets or on one scaffolded context. The batch fixer would make one edit per diagnostic to the same
    /// class, keep only one of them, and drop the rest as conflicting.
    /// </summary>
    private sealed class ListEveryTypeFixAllProvider : FixAllProvider
    {
        private const string AddToKeyPart = "_AddTo_";
        private const string ScaffoldKeyPart = "_ScaffoldContext";

        public static readonly ListEveryTypeFixAllProvider Instance = new();

        public override IEnumerable<FixAllScope> GetSupportedFixAllScopes()
            => [FixAllScope.Document, FixAllScope.Project, FixAllScope.Solution];

        public override Task<CodeAction?> GetFixAsync(FixAllContext fixAllContext)
        {
            var key = fixAllContext.CodeActionEquivalenceKey;
            if (key is null)
            {
                return Task.FromResult<CodeAction?>(null);
            }
            return Task.FromResult<CodeAction?>(CodeAction.Create(
                "Add [JsonSerializable] for every unlisted type",
                ct => FixAllAsync(fixAllContext, key, ct),
                equivalenceKey: key));
        }

        private static async Task<Solution> FixAllAsync(FixAllContext fixAllContext, string key, CancellationToken cancellationToken)
        {
            // The key is "<id>_AddTo_<context FQN>" or "<id>_ScaffoldContext"; only diagnostics with that id offer it.
            var addTo = key.IndexOf(AddToKeyPart, StringComparison.Ordinal);
            var scaffold = key.EndsWith(ScaffoldKeyPart, StringComparison.Ordinal);
            if (addTo < 0 && !scaffold)
            {
                return fixAllContext.Solution;
            }
            var diagnosticId = scaffold ? key.Substring(0, key.Length - ScaffoldKeyPart.Length) : key.Substring(0, addTo);
            var contextFqn = scaffold ? null : key.Substring(addTo + AddToKeyPart.Length);

            var projects = fixAllContext.Scope == FixAllScope.Solution
                ? fixAllContext.Solution.Projects
                : [fixAllContext.Project];
            var solution = fixAllContext.Solution;
            foreach (var original in projects)
            {
                var diagnostics = fixAllContext.Scope == FixAllScope.Document && fixAllContext.Document is { } document
                    ? await fixAllContext.GetDocumentDiagnosticsAsync(document).ConfigureAwait(false)
                    : await fixAllContext.GetAllDiagnosticsAsync(original).ConfigureAwait(false);
                var located = diagnostics
                    .Where(d => d.Id == diagnosticId && d.Location.SourceTree is not null)
                    .Select(d => (Diagnostic: d, TypeFqn: d.Properties.TryGetValue(TypeFqnKey, out var fqn) ? fqn : null))
                    .Where(d => !string.IsNullOrEmpty(d.TypeFqn))
                    .ToList();
                if (located.Count == 0)
                {
                    continue;
                }
                var typeFqns = located.Select(d => d.TypeFqn!).Distinct(StringComparer.Ordinal)
                    .OrderBy(fqn => fqn, StringComparer.Ordinal).ToList();

                var project = solution.GetProject(original.Id)!;
                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation?.GetTypeByMetadataName(JsonSerializerContextMetadataName) is null)
                {
                    continue;
                }
                var contexts = await FindJsonSerializerContextsAsync(project, diagnosticId, cancellationToken).ConfigureAwait(false);

                // Each project gets the edit its own diagnostics would offer for this key, or none.
                if (scaffold)
                {
                    if (contexts.Count != 0)
                    {
                        continue;
                    }
                    // One context for the whole project, in the file of the first diagnostic.
                    var first = located
                        .OrderBy(d => d.Diagnostic.Location.SourceTree!.FilePath, StringComparer.Ordinal)
                        .ThenBy(d => d.Diagnostic.Location.SourceSpan.Start)
                        .First();
                    var host = project.GetDocument(first.Diagnostic.Location.SourceTree);
                    if (host is null)
                    {
                        continue;
                    }
                    solution = (await ScaffoldContextAsync(host, typeFqns, cancellationToken).ConfigureAwait(false)).Project.Solution;
                }
                else if (contexts.FirstOrDefault(c => c.ContextFqn == contextFqn) is { Document: not null } target)
                {
                    solution = await AddToExistingContextAsync(target.Document, target.Declaration, typeFqns, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            return solution;
        }
    }

    /// <summary>The type as a reader writes it: every qualified name keeps only its last part, type arguments included.</summary>
    private static string ShortName(string typeFqn)
        => new ShortNameRewriter().Visit(SyntaxFactory.ParseTypeName(typeFqn)).ToString();

    private sealed class ShortNameRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node) => Visit(node.Right);

        public override SyntaxNode? VisitAliasQualifiedName(AliasQualifiedNameSyntax node) => Visit(node.Name);
    }
}
