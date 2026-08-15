namespace BackWave.Dashboard;

/// <summary>
/// A sidebar navigation entry contributed by a dashboard extension. A separately-installed package
/// returns these from its extension to add its own sections to the dashboard's left navigation,
/// alongside the built-in entries. The base dashboard renders contributed entries after its own,
/// in the order the extensions are registered.
/// </summary>
/// <param name="Key">
/// A stable identifier for the section, unique among all entries. The dashboard uses it to mark the
/// entry active when the current page matches; pick a short, lowercase, URL-safe token such as
/// <c>"metrics"</c>.
/// </param>
/// <param name="Label">The human-readable text shown for the entry in the sidebar, such as <c>"Metrics"</c>.</param>
/// <param name="Href">
/// The path the entry links to, relative to the dashboard's mount point and starting with <c>'/'</c>
/// (for example <c>"/metrics"</c>). The dashboard prefixes it with its own mount path at render time,
/// so the same entry works no matter where the host mounts the dashboard.
/// </param>
public sealed record DashboardNavEntry(string Key, string Label, string Href)
{
    /// <summary>
    /// An inline SVG glyph drawn beside the entry's label, matching the built-in entries' icons.
    /// Supply the raw <c>&lt;svg&gt;…&lt;/svg&gt;</c> markup; it is emitted verbatim, so use
    /// <c>currentColor</c> for strokes and fills to inherit the sidebar ink, and keep it
    /// self-contained (no external references). When <see langword="null"/>, the entry renders with
    /// no icon, leaving the label aligned with the built-in entries.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// The <see cref="Key"/> of the built-in entry this one should appear immediately after, so the
    /// contributed entry lands in a chosen slot rather than at the end of the sidebar. For example
    /// <c>"failures"</c> places the entry just below the Failures entry. When <see langword="null"/>
    /// or no entry with that key exists, the entry is appended after the built-in entries.
    /// </summary>
    public string? After { get; init; }
}
