using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;

namespace BackWave.Dashboard;

/// <summary>
/// A read-only page a dashboard extension contributes through its
/// <see cref="IDashboardExtension.PageRoutes"/>. The dashboard matches an incoming GET against the
/// <see cref="Template"/>, runs the same view-permission check and (for a live page) the same
/// server-sent-events refresh path its built-in pages use, then renders <see cref="Component"/> with
/// the parameters <see cref="LoadAsync"/> returns. The extension supplies only what is specific to its
/// page; the dashboard owns the cross-cutting machinery.
/// </summary>
public sealed record DashboardPageRoute
{
    /// <summary>
    /// The URL template the page is served at, relative to the dashboard's mount point, with no
    /// leading slash. Literal segments match exactly; a segment written as <c>{name}</c> captures that
    /// path segment and exposes it on <see cref="DashboardPageContext.RouteValues"/> under
    /// <c>name</c>. For example <c>"reports"</c> or <c>"reports/{id}"</c>.
    /// </summary>
    public required string Template { get; init; }

    /// <summary>
    /// The Razor component type to render for this page (an <c>IComponent</c>). It is rendered to HTML
    /// exactly as a built-in page is, so it may use the dashboard's own layout and embedded components.
    /// The parameters returned by <see cref="LoadAsync"/> are passed to it.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    public required Type Component { get; init; }

    /// <summary>
    /// Whether the page is live: its data changes under the viewer and should refresh in place over a
    /// server-sent-events stream, the way the built-in live pages do. When <see langword="false"/> the
    /// page is rendered once per request. Defaults to <see langword="false"/>.
    /// </summary>
    public bool Live { get; init; }

    /// <summary>
    /// Loads the page's render parameters for one request, keyed by the component's parameter names.
    /// Return <see langword="null"/> to render the dashboard's Not Found page (for example, when the
    /// captured id names nothing that exists). For a live page this is re-invoked on each refresh tick.
    /// </summary>
    public required Func<DashboardPageContext, Task<Dictionary<string, object?>?>> LoadAsync { get; init; }
}

/// <summary>
/// The per-request context handed to a <see cref="DashboardPageRoute.LoadAsync"/> loader: everything a
/// contributed page needs to read its data and honor the dashboard's permissions, without the
/// extension re-implementing any of the request handling.
/// </summary>
/// <param name="Http">
/// The current request's <see cref="HttpContext"/> — use it to read query-string arguments and to
/// resolve services (such as the BackWave monitor) from <see cref="HttpContext.RequestServices"/>.
/// </param>
/// <param name="RouteValues">
/// The segments captured from the page's <see cref="DashboardPageRoute.Template"/>, keyed by the
/// <c>{name}</c> placeholders. Empty when the template has no placeholders.
/// </param>
/// <param name="Actions">
/// The write affordances and sensitive-data view permission the caller holds for this request, already
/// resolved against the dashboard's default-deny permissions. Pass it to the rendered component so it
/// shows only the controls the caller may use.
/// </param>
/// <param name="Options">
/// The dashboard's configured options — chiefly whether sensitive data exposure is enabled — so the
/// loader gates raw content the same way the built-in pages do.
/// </param>
/// <param name="BasePath">
/// The path the dashboard is mounted at (for example <c>"/backwave"</c>), so the page can build links
/// that resolve under the mount point. Empty when mounted at the root.
/// </param>
public sealed record DashboardPageContext(
    HttpContext Http,
    IReadOnlyDictionary<string, string> RouteValues,
    DashboardActions Actions,
    BackWaveDashboardOptions Options,
    string BasePath);

/// <summary>
/// A state-changing action a dashboard extension contributes through its
/// <see cref="IDashboardExtension.ActionRoutes"/>. The dashboard matches an incoming POST against the
/// <see cref="Template"/>, enforces the chosen <see cref="Permission"/> and validates the antiforgery
/// token — the same protection its built-in actions get — then runs <see cref="HandleAsync"/> and
/// redirects to the path it returns. The extension never re-implements the security-critical steps.
/// </summary>
public sealed record DashboardActionRoute
{
    /// <summary>
    /// The URL template the action is posted to, relative to the dashboard's mount point, with no
    /// leading slash. Literal segments match exactly; a <c>{name}</c> segment captures that path
    /// segment onto <see cref="DashboardActionContext.RouteValues"/>. For example
    /// <c>"reports/{id}/archive"</c>.
    /// </summary>
    public required string Template { get; init; }

    /// <summary>
    /// Selects which of the dashboard's default-deny permissions guards this action. Given the
    /// dashboard's options, return the permission callback to check — for example the cancel
    /// permission for a cancel-like action. The dashboard denies the action with <c>403</c> when the
    /// callback returns <see langword="false"/>, and requires a valid antiforgery token regardless.
    /// </summary>
    public required Func<BackWaveDashboardOptions, Func<HttpContext, ValueTask<bool>>> Permission { get; init; }

    /// <summary>
    /// Performs the action and returns the path to redirect the browser to afterward (the
    /// post-redirect-get pattern), relative to the host so it may include the dashboard's mount path.
    /// Runs only after the permission and antiforgery checks have passed.
    /// </summary>
    public required Func<DashboardActionContext, Task<string>> HandleAsync { get; init; }
}

/// <summary>
/// The per-request context handed to a <see cref="DashboardActionRoute.HandleAsync"/> handler after the
/// dashboard has authorized the request: everything the action needs to do its work and record who did
/// it.
/// </summary>
/// <param name="Http">
/// The current request's <see cref="HttpContext"/> — use it to resolve services (such as the BackWave
/// operator) from <see cref="HttpContext.RequestServices"/>.
/// </param>
/// <param name="RouteValues">
/// The segments captured from the action's <see cref="DashboardActionRoute.Template"/>, keyed by the
/// <c>{name}</c> placeholders.
/// </param>
/// <param name="Actor">
/// The resolved identity of the caller performing the action, to stamp into the audit record the
/// operator writes.
/// </param>
/// <param name="BasePath">
/// The path the dashboard is mounted at, so the handler can build the redirect path under the mount
/// point. Empty when mounted at the root.
/// </param>
public sealed record DashboardActionContext(
    HttpContext Http,
    IReadOnlyDictionary<string, string> RouteValues,
    string Actor,
    string BasePath);
