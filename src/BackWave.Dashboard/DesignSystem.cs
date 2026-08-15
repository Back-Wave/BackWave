using System.Reflection;

namespace BackWave.Dashboard;

/// <summary>
/// The design-system stylesheet, read once from the embedded resource and inlined into the
/// dashboard <c>&lt;head&gt;</c>. Keeping it embedded (not a static file) preserves the
/// dashboard's zero-static-asset hosting contract: the host mounts middleware and nothing else.
/// </summary>
internal static class DesignSystem
{
    public static string Css => Lazy.Value;

    private static readonly Lazy<string> Lazy = new(static () =>
    {
        var assembly = typeof(DesignSystem).Assembly;
        var name = Array.Find(
            assembly.GetManifestResourceNames(),
            n => n.EndsWith("design-system.css", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Embedded design-system.css resource was not found.");
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
}
