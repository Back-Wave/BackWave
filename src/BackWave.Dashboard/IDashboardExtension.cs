namespace BackWave.Dashboard;

/// <summary>
/// A contribution point through which a separately-installed package adds surfaces to the BackWave
/// dashboard without the base dashboard knowing about it. Register an implementation in the
/// application's service container with
/// <see cref="BackWaveDashboardExtensions.AddBackWaveDashboardExtension{TExtension}(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>;
/// the dashboard resolves every registered extension at render time and folds in what each returns.
/// With no extension registered the dashboard renders exactly as it does on its own, so installing
/// the base package alone is unaffected.
/// </summary>
/// <remarks>
/// An extension contributes navigation entries and a banner (presentation chrome), and may also
/// contribute whole pages and actions of its own through <see cref="PageRoutes"/> and
/// <see cref="ActionRoutes"/> — the dashboard routes matching requests to them, applying the same
/// permission, antiforgery, and live-refresh handling its built-in surfaces get. Because an
/// implementation is resolved from the container, it can take constructor dependencies to drive what
/// it shows.
/// </remarks>
public interface IDashboardExtension
{
    /// <summary>
    /// The extra sidebar navigation entries this extension contributes. Returned entries are shown
    /// after the dashboard's built-in entries, in registration order. Return an empty sequence to
    /// add no navigation.
    /// </summary>
    /// <returns>The navigation entries to append to the sidebar; never <see langword="null"/>.</returns>
    IEnumerable<DashboardNavEntry> NavEntries() => [];

    /// <summary>
    /// An HTML fragment rendered at the top of the page content area, above the page itself — a
    /// banner or notice. The returned markup is emitted verbatim into the page, so it must be
    /// well-formed, self-contained HTML; the dashboard does not sanitize or wrap it. Return
    /// <see langword="null"/> to show nothing, which keeps the page identical to the un-extended
    /// dashboard.
    /// </summary>
    /// <param name="basePath">
    /// The path the dashboard is mounted at (for example <c>"/backwave"</c>), so the fragment can
    /// build links that resolve correctly under the mount point. Empty when mounted at the root.
    /// </param>
    /// <returns>The banner HTML to render, or <see langword="null"/> to render no banner.</returns>
    string? Banner(string basePath) => null;

    /// <summary>
    /// The read-only pages this extension serves. The dashboard matches an incoming GET against each
    /// returned route's template and, on a match, renders that page through the same view-permission
    /// and live-refresh path its built-in pages use. Return an empty sequence to add no pages.
    /// </summary>
    /// <returns>The page routes this extension serves; never <see langword="null"/>.</returns>
    IEnumerable<DashboardPageRoute> PageRoutes() => [];

    /// <summary>
    /// The state-changing actions this extension handles. The dashboard matches an incoming POST
    /// against each returned route's template and, on a match, enforces the route's permission and the
    /// antiforgery token before running its handler — the same protection its built-in actions get.
    /// Return an empty sequence to add no actions.
    /// </summary>
    /// <returns>The action routes this extension handles; never <see langword="null"/>.</returns>
    IEnumerable<DashboardActionRoute> ActionRoutes() => [];
}
