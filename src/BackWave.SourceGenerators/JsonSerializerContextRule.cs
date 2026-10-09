using System.Linq;
using Microsoft.CodeAnalysis;

namespace BackWave.SourceGenerators;

/// <summary>
/// Which JsonSerializerContext listings the generated codec can read metadata from. The generator and the code fix
/// both compile this file, so the code fix offers only the contexts that the generator binds.
/// </summary>
internal static class JsonSerializerContextRule
{
    private const string JsonSerializerContextName = "System.Text.Json.Serialization.JsonSerializerContext";
    private const string JsonSourceGenerationOptionsAttributeName =
        "System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute";
    public const string JsonSerializableAttributeName = "System.Text.Json.Serialization.JsonSerializableAttribute";

    /// <summary>JsonSourceGenerationMode.Serialization: a fast-path writer with no metadata to deserialize with.</summary>
    private const int SerializationOnlyMode = 2;

    /// <summary>
    /// True when the class derives from JsonSerializerContext. [JsonSerializable] compiles on any class, but only a
    /// context has the Default property that the generated codec reads.
    /// </summary>
    public static bool IsJsonSerializerContext(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == JsonSerializerContextName)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Why the generated codec cannot use a listing on the context, with the fix, or null when it can.
    /// <paramref name="listing"/> is the [JsonSerializable] application, or null for a listing that does not exist
    /// yet. Its GenerationMode overrides the context-level one, as in the STJ generator.
    /// </summary>
    public static string? UnusableReason(INamedTypeSymbol context, Compilation compilation, AttributeData? listing)
    {
        // The generated code names the context from its own files, so a private, protected, or file-local
        // context (or one nested in such a type) would fail the build inside generated code. Roslyn reports
        // IsFileLocal only on the declaring type and still calls a type nested in a file-local one accessible.
        var fileLocal = false;
        for (INamedTypeSymbol? current = context; current is not null; current = current.ContainingType)
        {
            fileLocal |= current.IsFileLocal;
        }
        if (fileLocal || !compilation.IsSymbolAccessibleWithin(context, compilation.Assembly))
        {
            return "the context is not accessible from the generated code - make the context, and every " +
                "type that contains it, internal or public, and not file-local";
        }

        var contextMode = GenerationMode(context.GetAttributes().FirstOrDefault(
            a => a.AttributeClass?.ToDisplayString() == JsonSourceGenerationOptionsAttributeName));
        if ((GenerationMode(listing) ?? contextMode) == SerializationOnlyMode)
        {
            return "the listing generates serialization-only code (GenerationMode = Serialization), which " +
                "cannot deserialize - remove GenerationMode = Serialization, or list the type on a " +
                "context that generates metadata";
        }
        return null;
    }

    /// <summary>
    /// Why the context cannot serve a delegated payload member, with the fix, or null when it can. STJ gives each
    /// type the mode of the first listing that reaches it, through any listed type, so one serialization-only
    /// listing, or a serialization-only context default, can leave a member type with no metadata to deserialize
    /// with. The rule checks the context, not the type graph, so it does not depend on the order of the listings.
    /// </summary>
    public static string? SerializationOnlyReason(INamedTypeSymbol context)
    {
        var attributes = context.GetAttributes();
        if (GenerationMode(attributes.FirstOrDefault(
                a => a.AttributeClass?.ToDisplayString() == JsonSourceGenerationOptionsAttributeName))
            == SerializationOnlyMode)
        {
            return "the context generates serialization-only code by default " +
                "([JsonSourceGenerationOptions(GenerationMode = Serialization)]), and System.Text.Json can give that " +
                "mode to a type this job needs, which then cannot deserialize - list the job types on a context " +
                "without GenerationMode = Serialization";
        }
        if (attributes.Any(a => a.AttributeClass?.ToDisplayString() == JsonSerializableAttributeName
                && GenerationMode(a) == SerializationOnlyMode))
        {
            return "the context has a serialization-only listing (GenerationMode = Serialization), and System.Text.Json " +
                "gives each type the mode of the first listing that reaches it, so a type this job needs can be left " +
                "unable to deserialize - move the serialization-only listings to a separate context";
        }
        return null;
    }

    /// <summary>The GenerationMode an attribute sets, or null when it sets none.</summary>
    private static int? GenerationMode(AttributeData? attribute)
        => attribute?.NamedArguments
            .Where(a => a.Key == "GenerationMode")
            .Select(a => a.Value.Value as int?)
            .FirstOrDefault();
}
