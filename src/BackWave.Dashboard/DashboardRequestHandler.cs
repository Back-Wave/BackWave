using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using BackWave.Dashboard.Components.Pages;
using BackWave.Monitor;
using BackWave.Observers;
using BackWave.Operations;
using BackWave.Storage;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Dashboard;

// Default-deny per-action permissions and antiforgery protection: ADR 0010.
/// <summary>
/// Routes dashboard requests to their views. Reads flow through the Monitor API exclusively,
/// so what the dashboard shows and what test assertions see agree by construction; writes are
/// Operator Actions issued through <see cref="BackWaveOperator"/> (audited), each gated by its
/// own default-deny Permission and antiforgery-protected.
/// </summary>
internal static class DashboardRequestHandler
{
    /// <summary>Named bound: default rows per dashboard page; deeper rows page via the cursor.
    /// The Jobs list lets the viewer pick from <see cref="JobsPageSizes"/>; other views stay at this default.</summary>
    internal const int PageSize = 50;

    /// <summary>Rows shown in an Overview preview table before "see all" takes over the full view.</summary>
    internal const int PreviewRows = 5;

    /// <summary>Top-N rows the Top/Faulting endpoint metrics panels show before folding the rest into
    /// an "other" rollup — bounds panel cardinality against unbounded distinct job types.</summary>
    internal const int TopEndpointRows = 6;

    /// <summary>The rows-per-page choices offered on the Jobs list; anything else falls back to <see cref="PageSize"/>.
    /// The top choice is the store's <c>MaxMonitorPageSize</c>, where the "+1 next-page sentinel" cannot appear.</summary>
    private static readonly int[] JobsPageSizes = [25, 50, 100, 200];

    public static async Task HandleAsync(HttpContext context, BackWaveDashboardOptions options)
    {
        if (!await options.AuthorizeView(context).ConfigureAwait(false))
        {
            // The View Permission said no: no dashboard bytes, not even page chrome.
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var basePath = context.Request.PathBase.Value ?? "";
        var segments = context.Request.Path.Value?.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];

        if (HttpMethods.IsPost(context.Request.Method))
        {
            await HandlePostAsync(context, options, basePath, segments).ConfigureAwait(false);
            return;
        }
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var monitor = context.RequestServices.GetRequiredService<BackWaveMonitor>();
        var actions = await ResolveActionsAsync(context, options).ConfigureAwait(false);

        switch (segments)
        {
            case []:
                // The live-metrics collector is opt-in (AddBackWaveDashboardMetrics). GetService, not
                // GetRequiredService: a null collector renders the panel's graceful empty state.
                var metrics = context.RequestServices.GetService<DashboardMetricsCollector>();
                await ServeLiveAsync(context, OverviewView(monitor, basePath, metrics), options.LiveRefreshInterval).ConfigureAwait(false);
                break;

            case ["executing"]:
                await ServeLiveAsync(context, ExecutingView(monitor, basePath), options.LiveRefreshInterval).ConfigureAwait(false);
                break;

            case ["jobs"]:
                await JobsAsync(context, monitor, basePath, actions).ConfigureAwait(false);
                break;

            case ["jobs", var rawJobId] when Guid.TryParse(rawJobId, out var jobId):
                await JobDetailAsync(context, monitor, basePath, jobId, actions, options).ConfigureAwait(false);
                break;

            case ["tags", "suggest"]:
                // The dashboard's only JSON endpoint: the Jobs page's Label-suggest input fetches it.
                // View-gated for free — AuthorizeView already ran above. Tags are plaintext-searchable
                // by design, so this needs no extra ViewSensitiveData gate.
                await TagsSuggestAsync(context, monitor).ConfigureAwait(false);
                break;

            case ["queues"]:
                await ServeLiveAsync(context, QueuesView(monitor, basePath, actions), options.LiveRefreshInterval).ConfigureAwait(false);
                break;

            case ["failures"]:
                // The open category tab rides the query string (?tab=quarantine) so it survives each
                // live SSE tick — the stream reconnects to this same URL. Read once here; a stream's
                // URL never changes mid-connection.
                var failuresTab = context.Request.Query["tab"] is [{ Length: > 0 } tab] ? tab : "";
                await ServeLiveAsync(context, FailuresView(monitor, basePath, actions, failuresTab), options.LiveRefreshInterval).ConfigureAwait(false);
                break;

            case ["observers"]:
                // Observers are run config — the canonical registration set comes from DI (issue 0102),
                // the same IReadOnlyList<ObserverRegistration> AddObservers registers; [] when no host
                // registered any (an empty surface, never an error). GetService, not GetRequiredService,
                // so the fallback holds.
                var observers = context.RequestServices.GetService<IReadOnlyList<ObserverRegistration>>() ?? [];
                await ServeLiveAsync(context, ObserversView(monitor, basePath, observers), options.LiveRefreshInterval).ConfigureAwait(false);
                break;

            case ["schedules"]:
                await ServeLiveAsync(context, SchedulesView(monitor, basePath, actions), options.LiveRefreshInterval).ConfigureAwait(false);
                break;

            case ["schedules", var scheduleId]:
                await ScheduleInstancesAsync(context, monitor, basePath, scheduleId, actions).ConfigureAwait(false);
                break;

            default:
                // No built-in page matched: try the pages contributed by extensions (a separately
                // installed package owning its own route), then Not Found. Zero extensions ⇒ straight
                // to Not Found, so the base dashboard is unaffected.
                if (!await TryServeExtensionPageAsync(context, options, basePath, segments, actions).ConfigureAwait(false))
                {
                    await NotFoundAsync(context, basePath).ConfigureAwait(false);
                }
                break;
        }
    }

    // ── Operator Actions (POST) ─────────────────────────────────────────────────
    // Each route checks its own Permission (403 if denied) then validates the antiforgery
    // token (400 if missing/invalid), issues the audited action through BackWaveOperator
    // stamped with the resolved actor, and redirects (303 See Other, the POST-Redirect-GET
    // pattern) back to the originating view.

    private static async Task HandlePostAsync(
        HttpContext context, BackWaveDashboardOptions options, string basePath, string[] segments)
    {
        switch (segments)
        {
            case ["jobs", var raw, "requeue"] when Guid.TryParse(raw, out var jobId):
                if (!await GuardAsync(context, options.AuthorizeRequeue).ConfigureAwait(false)) return;
                await Operator(context).RequeueAsync(jobId, options.ResolveActor(context)).ConfigureAwait(false);
                SeeOther(context, $"{basePath}/jobs/{jobId}");
                return;

            case ["jobs", var raw, "cancel"] when Guid.TryParse(raw, out var jobId):
                if (!await GuardAsync(context, options.AuthorizeCancel).ConfigureAwait(false)) return;
                await Operator(context).CancelJobAsync(jobId, options.ResolveActor(context)).ConfigureAwait(false);
                SeeOther(context, $"{basePath}/jobs/{jobId}");
                return;

            case ["queues", var queue, "pause"]:
                if (!await GuardAsync(context, options.AuthorizePauseQueue).ConfigureAwait(false)) return;
                await Operator(context).PauseQueueAsync(queue, options.ResolveActor(context)).ConfigureAwait(false);
                SeeOther(context, $"{basePath}/");
                return;

            case ["queues", var queue, "resume"]:
                if (!await GuardAsync(context, options.AuthorizePauseQueue).ConfigureAwait(false)) return;
                await Operator(context).ResumeQueueAsync(queue, options.ResolveActor(context)).ConfigureAwait(false);
                SeeOther(context, $"{basePath}/");
                return;

            case ["schedules", var scheduleId, "trigger"]:
                if (!await GuardAsync(context, options.AuthorizeTriggerSchedule).ConfigureAwait(false)) return;
                await Operator(context).TriggerScheduleNowAsync(scheduleId, options.ResolveActor(context)).ConfigureAwait(false);
                SeeOther(context, $"{basePath}/schedules");
                return;

            default:
                // No built-in action matched: try the actions contributed by extensions, then 404.
                if (!await TryHandleExtensionActionAsync(context, options, basePath, segments).ConfigureAwait(false))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                }
                return;
        }
    }

    // ── Extension-contributed routes ────────────────────────────────────────────
    // A separately-installed package contributes whole pages (GET) and actions (POST) declaratively
    // through IDashboardExtension; the security-bearing parts — the View gate (already enforced at
    // HandleAsync entry), the action Permission + antiforgery (GuardAsync), the live SSE path, and the
    // post-redirect-get — stay here, run identically to the built-in routes. The extension supplies
    // only the route template, the component/handler, and the loader.

    private static async Task<bool> TryServeExtensionPageAsync(
        HttpContext context, BackWaveDashboardOptions options, string basePath, string[] segments, DashboardActions actions)
    {
        foreach (var extension in Extensions(context))
        {
            foreach (var route in extension.PageRoutes() ?? [])
            {
                if (!TryMatchTemplate(route.Template, segments, out var routeValues)) continue;

                var pageContext = new DashboardPageContext(context, routeValues, actions, options, basePath);
                if (route.Live)
                {
                    // A live page refreshes in place over SSE exactly like a built-in live view. Probe
                    // the loader once: null ⇒ Not Found (the resource does not exist); otherwise stream,
                    // reusing the probe as the first frame (seed) so the initial render loads only once.
                    // A later tick that returns null (the resource was deleted while the page is open)
                    // swaps the live region to Not Found — it never freezes on this first snapshot.
                    var first = await route.LoadAsync(pageContext).ConfigureAwait(false);
                    if (first is null)
                    {
                        await NotFoundAsync(context, basePath).ConfigureAwait(false);
                        return true;
                    }
                    await ServeLiveAsync(
                        context,
                        new LiveView(route.Component, () => route.LoadAsync(pageContext), basePath),
                        options.LiveRefreshInterval,
                        seed: first).ConfigureAwait(false);
                    return true;
                }

                var parameters = await route.LoadAsync(pageContext).ConfigureAwait(false);
                if (parameters is null)
                {
                    await NotFoundAsync(context, basePath).ConfigureAwait(false);
                    return true;
                }
                var html = await DashboardRenderer.RenderAsync(context, route.Component, parameters, document: true).ConfigureAwait(false);
                await WriteAsync(context, html).ConfigureAwait(false);
                return true;
            }
        }
        return false;
    }

    private static async Task<bool> TryHandleExtensionActionAsync(
        HttpContext context, BackWaveDashboardOptions options, string basePath, string[] segments)
    {
        foreach (var extension in Extensions(context))
        {
            foreach (var route in extension.ActionRoutes() ?? [])
            {
                if (!TryMatchTemplate(route.Template, segments, out var routeValues)) continue;

                // The same default-deny Permission + antiforgery gate the built-in actions use.
                if (!await GuardAsync(context, route.Permission(options)).ConfigureAwait(false)) return true;
                var actionContext = new DashboardActionContext(context, routeValues, options.ResolveActor(context), basePath);
                var redirect = await route.HandleAsync(actionContext).ConfigureAwait(false);
                SeeOther(context, redirect);
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<IDashboardExtension> Extensions(HttpContext context)
        => context.RequestServices.GetServices<IDashboardExtension>();

    /// <summary>Matches a route template ("a/{id}/b") against the request path segments, capturing each
    /// <c>{name}</c> segment into <paramref name="routeValues"/>. Returns false (and an empty map) on
    /// a length or literal-segment mismatch.</summary>
    private static bool TryMatchTemplate(string template, string[] segments, out Dictionary<string, string> routeValues)
    {
        routeValues = [];
        var parts = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != segments.Length) return false;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length > 2 && part[0] == '{' && part[^1] == '}')
            {
                routeValues[part[1..^1]] = segments[i];
            }
            else if (!string.Equals(part, segments[i], StringComparison.Ordinal))
            {
                routeValues.Clear();
                return false;
            }
        }
        return true;
    }

    /// <summary>Authorization then antiforgery; sets the response status and returns false if either fails.</summary>
    private static async Task<bool> GuardAsync(HttpContext context, Func<HttpContext, ValueTask<bool>> permission)
    {
        if (!await permission(context).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return false;
        }
        try
        {
            await Antiforgery(context).ValidateRequestAsync(context).ConfigureAwait(false);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return false;
        }
    }

    /// <summary>Resolves which Operator Actions the caller may take and the antiforgery token to stamp into forms.</summary>
    private static async Task<DashboardActions> ResolveActionsAsync(HttpContext context, BackWaveDashboardOptions options)
    {
        var canRequeue = await options.AuthorizeRequeue(context).ConfigureAwait(false);
        var canCancel = await options.AuthorizeCancel(context).ConfigureAwait(false);
        var canPauseQueue = await options.AuthorizePauseQueue(context).ConfigureAwait(false);
        var canTriggerSchedule = await options.AuthorizeTriggerSchedule(context).ConfigureAwait(false);
        // ViewSensitiveData is a READ gate, not an Operator Action: it grants no form, so it needs
        // no antiforgery token. Still its own default-deny Permission.
        var canViewSensitiveData = await options.AuthorizeViewSensitiveData(context).ConfigureAwait(false);

        // Fully read-only posture: no granted flag at all means no controls and no sensitive
        // content, so don't even touch the antiforgery dependency.
        if (!(canRequeue || canCancel || canPauseQueue || canTriggerSchedule || canViewSensitiveData))
        {
            return DashboardActions.None;
        }

        // Antiforgery tokens exist only for write forms; mint them only when a WRITE action is
        // granted. A ViewSensitiveData-only caller renders no form, so the token fields stay "".
        var anyWriteAction = canRequeue || canCancel || canPauseQueue || canTriggerSchedule;
        var tokens = anyWriteAction
            ? Antiforgery(context).GetAndStoreTokens(context)
            : default;
        return new DashboardActions
        {
            CanRequeue = canRequeue,
            CanCancel = canCancel,
            CanPauseQueue = canPauseQueue,
            CanTriggerSchedule = canTriggerSchedule,
            CanViewSensitiveData = canViewSensitiveData,
            AntiforgeryFieldName = tokens?.FormFieldName ?? "",
            AntiforgeryToken = tokens?.RequestToken ?? "",
        };
    }

    private static BackWaveOperator Operator(HttpContext context)
        => context.RequestServices.GetRequiredService<BackWaveOperator>();

    private static IAntiforgery Antiforgery(HttpContext context)
        => context.RequestServices.GetService<IAntiforgery>()
           ?? throw new InvalidOperationException(
               "BackWave dashboard Operator Actions require antiforgery services. Call " +
               "services.AddAntiforgery() in the host when you opt into AuthorizeRequeue/" +
               "AuthorizeCancel/AuthorizePauseQueue/AuthorizeTriggerSchedule.");

    private static void SeeOther(HttpContext context, string location)
    {
        context.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.Response.Headers.Location = location;
    }

    // ── Live views (SSE) ────────────────────────────────────────────────────────
    // A live view is a read-only screen whose data changes under it. Each is described once,
    // as a component type plus a loader that re-reads the Monitor; ServeLiveAsync renders it as
    // a one-shot document, or — when the browser asks with ?live=1 — streams just the #bw-live
    // fragment over a single Server-Sent Events connection (no repeated page GETs). The server
    // re-renders on its own cadence and pushes only when the fragment actually changed.

    /// <summary>A read-only screen plus a loader that re-reads its data each render tick. The loader
    /// returns <see langword="null"/> when its resource no longer exists; the live path then renders
    /// Not Found instead of freezing on the last good snapshot. <see cref="BasePath"/> resolves that
    /// Not-Found render's links — built-in views never return null, so they leave it at default.</summary>
    private sealed record LiveView(
        [property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
        Type Component, Func<Task<Dictionary<string, object?>?>> LoadAsync, string BasePath = "");

    private static LiveView OverviewView(
        BackWaveMonitor monitor, string basePath, DashboardMetricsCollector? metrics) => new(
        typeof(Overview),
        // Point-in-time health (issue 0064): depths + settings + schedules give paused Queues,
        // Queues at their Concurrency Limit, and Schedule health. Live per-node throughput comes from
        // the opt-in metrics collector (ADR 0032), re-snapshotted each SSE tick — null when the
        // collector is not registered, which the panel renders as a graceful empty state. Two previews
        // surface the newest Needs-attention (Dead-Lettered + Quarantined) and Executing-now (Leased)
        // jobs, newest-first via issue 0063.
        async () => new Dictionary<string, object?>
        {
            ["BasePath"] = basePath,
            ["Metrics"] = metrics?.Snapshot(TopEndpointRows),
            ["Depths"] = await monitor.GetQueueDepthsAsync().ConfigureAwait(false),
            ["Settings"] = await monitor.GetQueueSettingsAsync().ConfigureAwait(false),
            ["Schedules"] = await monitor.ListSchedulesAsync().ConfigureAwait(false),
            ["DeadLettered"] = await monitor.ListJobsAsync(
                new JobQuery { State = JobState.DeadLettered, SortDirection = JobSortDirection.NewestFirst, MaxResults = PreviewRows }).ConfigureAwait(false),
            ["Quarantined"] = await monitor.ListJobsAsync(
                new JobQuery { State = JobState.Quarantined, SortDirection = JobSortDirection.NewestFirst, MaxResults = PreviewRows }).ConfigureAwait(false),
            ["Executing"] = await monitor.ListJobsAsync(
                new JobQuery { State = JobState.Leased, SortDirection = JobSortDirection.NewestFirst, MaxResults = PreviewRows }).ConfigureAwait(false),
            ["PreviewRows"] = PreviewRows,
        });

    private static LiveView ExecutingView(BackWaveMonitor monitor, string basePath) => new(
        typeof(Executing),
        // Executing-now (issue 0054): the jobs with a live Lease, surfaced cluster-wide. Now is
        // recomputed each tick so the heartbeat countdown actually counts down.
        async () => new Dictionary<string, object?>
        {
            ["BasePath"] = basePath,
            ["Jobs"] = await monitor.ListJobsAsync(
                new JobQuery { State = JobState.Leased, MaxResults = PageSize }).ConfigureAwait(false),
            ["Now"] = DateTimeOffset.UtcNow,
        });

    private static LiveView QueuesView(BackWaveMonitor monitor, string basePath, DashboardActions actions) => new(
        typeof(Queues),
        // Queues operational state (issue 0055): depths give in-use (Leased) and total; the
        // settings read gives paused + Concurrency Limit cap.
        async () => new Dictionary<string, object?>
        {
            ["BasePath"] = basePath,
            ["Depths"] = await monitor.GetQueueDepthsAsync().ConfigureAwait(false),
            ["Settings"] = await monitor.GetQueueSettingsAsync().ConfigureAwait(false),
            ["Actions"] = actions,
        });

    /// <summary>Serves a live view: an SSE stream when the client asks with <c>?live=1</c>,
    /// otherwise the one-shot full document (which carries the EventSource client that asks).</summary>
    private static Task ServeLiveAsync(
        HttpContext context, LiveView view, TimeSpan interval, Dictionary<string, object?>? seed = null)
        => context.Request.Query["live"] is ["1"]
            ? StreamAsync(context, view, interval, seed)
            : RenderOnceAsync(context, view, seed);

    private static async Task RenderOnceAsync(HttpContext context, LiveView view, Dictionary<string, object?>? seed)
    {
        // Reuse the caller's probe (seed) for the first render rather than loading a second time.
        var parameters = seed ?? await view.LoadAsync().ConfigureAwait(false);
        if (parameters is null)
        {
            await NotFoundAsync(context, view.BasePath).ConfigureAwait(false);
            return;
        }
        parameters["Live"] = true;         // emit #bw-live + the EventSource client
        parameters["ContentOnly"] = false; // full document
        var html = await DashboardRenderer.RenderAsync(context, view.Component, parameters, document: true).ConfigureAwait(false);
        await WriteAsync(context, html).ConfigureAwait(false);
    }

    /// <summary>Holds the response open and pushes the re-rendered #bw-live fragment as Server-Sent
    /// Events, every <paramref name="interval"/>, but only when the markup changed since the last
    /// push (a comment heartbeat keeps the connection warm otherwise). Ends when the browser
    /// disconnects.</summary>
    private static async Task StreamAsync(
        HttpContext context, LiveView view, TimeSpan interval, Dictionary<string, object?>? seed)
    {
        var ct = context.RequestAborted;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no"; // don't let a reverse proxy buffer the stream
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        string? last = null;
        var pending = seed; // the caller's probe powers the first tick; later ticks reload
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var parameters = pending ?? await view.LoadAsync().ConfigureAwait(false);
                pending = null;
                string fragment;
                if (parameters is null)
                {
                    // The resource vanished while the page is open: swap the live region to the
                    // Not-Found fragment rather than leave the last good snapshot frozen in place.
                    fragment = await DashboardRenderer.RenderAsync(
                        context, typeof(NotFound),
                        new Dictionary<string, object?> { ["BasePath"] = view.BasePath, ["ContentOnly"] = true },
                        document: false).ConfigureAwait(false);
                }
                else
                {
                    parameters["ContentOnly"] = true; // just the #bw-live inner markup, no chrome
                    fragment = await DashboardRenderer
                        .RenderAsync(context, view.Component, parameters, document: false).ConfigureAwait(false);
                }

                if (fragment == last)
                {
                    await context.Response.WriteAsync(": ping\n\n", ct).ConfigureAwait(false);
                }
                else
                {
                    last = fragment;
                    await context.Response.WriteAsync(ToEventData("update", fragment), ct).ConfigureAwait(false);
                }
                await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The browser navigated away or closed the tab — a normal end to the stream.
        }
    }

    /// <summary>Frames a payload as one SSE event; each line becomes its own <c>data:</c> field
    /// (the wire format forbids raw newlines inside a field), then a blank line ends the event.</summary>
    private static string ToEventData(string @event, string payload)
    {
        var sb = new StringBuilder().Append("event: ").Append(@event).Append('\n');
        foreach (var line in payload.Split('\n'))
        {
            sb.Append("data: ").Append(line).Append('\n');
        }
        return sb.Append('\n').ToString();
    }

    private static async Task JobsAsync(HttpContext context, BackWaveMonitor monitor, string basePath, DashboardActions actions)
    {
        var query = context.Request.Query;

        // Job-id search (issue 0065): the id is the unique key, so it wins outright — resolve it
        // BEFORE building any State/Queue/Wire filter. A valid Guid 302-redirects to the detail page
        // (an unknown-but-valid id then lands on the existing Not Found view via that route); a
        // present-but-malformed id is treated like an unknown job reference and renders Not Found.
        if (query["id"] is [{ Length: > 0 } rawId])
        {
            if (Guid.TryParse(rawId, out var jobId))
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = $"{basePath}/jobs/{jobId}";
                return;
            }
            await NotFoundAsync(context, basePath).ConfigureAwait(false);
            return;
        }

        // Rows-per-page (issue 0065): a display preference validated to the allowed set; default 50.
        var pageSize = query["size"] is [{ Length: > 0 } rawSize]
            && int.TryParse(rawSize, out var parsedSize) && JobsPageSizes.Contains(parsedSize)
                ? parsedSize
                : PageSize;

        JobState? state = null;
        if (query["state"] is [{ Length: > 0 } rawState])
        {
            if (!Enum.TryParse<JobState>(rawState, ignoreCase: true, out var parsed))
            {
                await BadRequestAsync(context, $"Unknown state '{rawState}'.").ConfigureAwait(false);
                return;
            }
            state = parsed;
        }
        long? after = null;
        if (query["after"] is [{ Length: > 0 } rawAfter])
        {
            if (!long.TryParse(rawAfter, out var parsed))
            {
                await BadRequestAsync(context, $"The 'after' cursor must be a number (got '{rawAfter}').").ConfigureAwait(false);
                return;
            }
            after = parsed;
        }

        // Tag filters (ADR 0022, issue 0113): the click-to-filter pills/facets round-trip their
        // predicates through the query string (tl=label, tk=/tv= for key=value). They AND with the
        // scalar State/Queue/Wire filters and with each other — JobQuery composes them all.
        var tagPredicates = TagFilterUrl.Parse(query);

        var filter = new JobQuery
        {
            State = state,
            Queue = NonEmpty(query["queue"]),
            WireName = NonEmpty(query["wire"]),
            ScheduleId = NonEmpty(query["schedule"]),
            TagPredicates = tagPredicates,
            AfterSequence = after,
            SortDirection = JobSortDirection.NewestFirst, // historical table: most recent jobs first
            MaxResults = pageSize + 1, // one extra row = "there is a next page" (clamps at the 200 boundary)
        };
        var page = await monitor.ListJobsAsync(filter).ConfigureAwait(false);

        // Filter options, sourced through the Monitor only: Queue names that actually have jobs
        // (distinct, from the depth counts) and the known Wire Names (the registry-backed facet).
        var depths = await monitor.GetQueueDepthsAsync().ConfigureAwait(false);
        var queueOptions = depths.Select(d => d.Queue).Distinct().OrderBy(q => q, StringComparer.Ordinal).ToList();
        var wireNameOptions = monitor.GetKnownWireNames();

        // Facet display (ADR 0022 §0112): break down the Labels (key="") and scope them by the
        // ACTIVE filter — the same JobQuery as the listing (minus the page cursor, which is a window
        // not a population) — so the counts describe the jobs currently in view. Metadata only.
        // Cap the card to the top 20 Labels by count (issue 0210) — an at-a-glance summary, not an
        // exhaustive index; the Label-filter input below it reaches the long tail via Tag Suggest.
        var facetScope = filter with { AfterSequence = null, MaxResults = int.MaxValue };
        var facets = await monitor.GetTagFacetAsync(string.Empty, facetScope, 20).ConfigureAwait(false);

        var html = await DashboardRenderer.RenderDocumentAsync<Components.Pages.Jobs>(context, new Dictionary<string, object?>
        {
            ["BasePath"] = basePath,
            ["Filter"] = filter,
            ["Page"] = page,
            ["PageSize"] = pageSize,
            ["PageSizes"] = JobsPageSizes,
            ["Actions"] = actions,
            ["QueueOptions"] = queueOptions,
            ["WireNameOptions"] = wireNameOptions,
            ["Facets"] = facets,
        }).ConfigureAwait(false);
        await WriteAsync(context, html).ConfigureAwait(false);
    }

    // Tag Suggest as JSON (issue 0214): prefix-complete Tag tokens for the Jobs page's Label-filter
    // input. Reads flow through the Monitor like every other view. The query maps to a TagSuggestQuery:
    //   prefix — the token being typed (empty matches all).
    //   key    — the stage selector. ABSENT ⇒ Key=null (stage one: Labels and keys together).
    //            PRESENT-BUT-EMPTY (key=) ⇒ Key="" (stage two on the Label dimension). The Label input
    //            sends key= explicitly, so this endpoint returns only Label values today.
    //   ak/av  — the keyset cursor: the previous window's last suggestion echoed back as after-key /
    //            after-value, rebuilt into TagSuggestQuery.After to page forward without gaps or dupes.
    //   max    — the requested window size (the store clamps it to [1, TagSuggestQuery.MaxSuggestResults]).
    private static async Task TagsSuggestAsync(HttpContext context, BackWaveMonitor monitor)
    {
        var query = context.Request.Query;
        var prefix = query["prefix"] is [{ } p] ? p : "";
        // ContainsKey distinguishes an absent key (stage one, Key=null) from a present-but-empty key=
        // (the Label dimension, Key=""); a bare NonEmpty check would collapse the two.
        string? key = query.ContainsKey("key") ? query["key"].ToString() : null;
        TagSuggestion? after = query.ContainsKey("ak") || query.ContainsKey("av")
            ? new TagSuggestion(query["ak"].ToString(), query["av"].ToString())
            : null;

        var suggest = new TagSuggestQuery { Prefix = prefix, Key = key, After = after };
        if (query["max"] is [{ } rawMax] && int.TryParse(rawMax, out var max))
        {
            suggest = suggest with { MaxResults = max }; // the store clamps out-of-range values
        }

        var suggestions = await monitor.SuggestTagsAsync(suggest, context.RequestAborted).ConfigureAwait(false);

        // Write UTF-8 JSON straight to the body — trim-safe and allocation-light, no reflection
        // serializer or DTO array: an array of {"key","value"} objects carrying the canonical stored
        // casing so the composed Tag predicate matches exactly.
        context.Response.ContentType = "application/json; charset=utf-8";
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var suggestion in suggestions)
            {
                writer.WriteStartObject();
                writer.WriteString("key", suggestion.Key);
                writer.WriteString("value", suggestion.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task JobDetailAsync(
        HttpContext context, BackWaveMonitor monitor, string basePath, Guid jobId,
        DashboardActions actions, BackWaveDashboardOptions options)
    {
        if (await monitor.GetJobAsync(jobId).ConfigureAwait(false) is not { } job)
        {
            await NotFoundAsync(context, basePath).ConfigureAwait(false);
            return;
        }
        // Dependency gating (issue 0056): for an Awaiting-Parent job, the parents still
        // gating it — the remaining gating set, not the full original parent history.
        IReadOnlyList<Guid> gatingParents = job.State == JobState.AwaitingParent
            ? (await monitor.GetDependencyEdgesAsync(jobId).ConfigureAwait(false)).GatingParents
            : [];
        // Transition Log (issue 0057): the job's timeline of state changes, oldest first, through
        // the Monitor only. Job History Policy (issue 0060): when recording is Off the timeline is
        // empty by design, so the card renders an explicit "history disabled" state — never a blank
        // timeline that reads as broken. The Monitor is the single read surface for the policy too.
        var historyDisabled = monitor.IsHistoryRecordingDisabled;
        var history = await monitor.GetJobHistoryAsync(jobId).ConfigureAwait(false);
        // The sensitive-data gate (issue 0058/0059): the viewer holds ViewSensitiveData AND the
        // host leaves sensitive-data exposure on. Gates both the payload card and the Failure
        // Detail inline in the timeline — raw content that may carry secrets/PII.
        var canViewSensitiveData = actions.CanViewSensitiveData && options.SensitiveDataExposureEnabled;
        // Payload (issue 0058): opaque bytes read only behind the gate. Absent payload is not an
        // error — the card simply does not render.
        var payload = canViewSensitiveData
            ? await monitor.GetJobPayloadAsync(jobId).ConfigureAwait(false)
            : null;
        // Job Output (issue 0134, ADR 0026): the opaque blob a handler emitted via SetOutput on its
        // Succeeded Attempt, read only behind the SAME gate as the payload — output may carry
        // secrets/PII. Absent output is not an error: the card simply does not render.
        var output = canViewSensitiveData
            ? await monitor.GetJobOutputViewAsync(jobId).ConfigureAwait(false)
            : null;
        var html = await DashboardRenderer.RenderDocumentAsync<JobDetail>(context, new Dictionary<string, object?>
        {
            ["BasePath"] = basePath,
            ["Job"] = job,
            ["Actions"] = actions,
            ["GatingParents"] = gatingParents,
            ["History"] = history,
            ["HistoryDisabled"] = historyDisabled,
            ["Payload"] = payload,
            ["Output"] = output,
            ["CanViewSensitiveData"] = canViewSensitiveData,
        }).ConfigureAwait(false);
        await WriteAsync(context, html).ConfigureAwait(false);
    }

    private static LiveView FailuresView(BackWaveMonitor monitor, string basePath, DashboardActions actions, string tab) => new(
        typeof(Failures),
        // Glossary distinction, never collapsed (invariant I5): Dead-Lettered jobs ran and
        // kept failing; Quarantined jobs could not be routed or decoded. Both lists load every
        // tick — the inactive tab still shows a live count badge — but only the active tab's
        // table renders, so a long Dead-Lettered list never buries the Quarantined one.
        async () => new Dictionary<string, object?>
        {
            ["BasePath"] = basePath,
            ["DeadLettered"] = await monitor.ListJobsAsync(
                new JobQuery { State = JobState.DeadLettered, SortDirection = JobSortDirection.NewestFirst, MaxResults = PageSize }).ConfigureAwait(false),
            ["Quarantined"] = await monitor.ListJobsAsync(
                new JobQuery { State = JobState.Quarantined, SortDirection = JobSortDirection.NewestFirst, MaxResults = PageSize }).ConfigureAwait(false),
            ["PageSize"] = PageSize,
            ["Actions"] = actions,
            ["ActiveTab"] = tab,
        });

    private static LiveView ObserversView(
        BackWaveMonitor monitor, string basePath, IReadOnlyList<ObserverRegistration> observers) => new(
        typeof(ObserverDeliveries),
        // Observer-delivery health (issue 0082, §5.13/§0077). Observers are run config — a stable id
        // keying a durable cursor — so the SET comes from the dashboard options, not the Monitor; the
        // cursor and dead-letters for each come THROUGH the Monitor (the single read surface). Metadata
        // only: the dead-letter records carry no payload or Failure Detail, and each links to the Job
        // detail page where the ViewSensitiveData gate still governs sensitive content.
        async () => new Dictionary<string, object?>
        {
            ["BasePath"] = basePath,
            ["Observers"] = await LoadObserverHealthAsync(monitor, observers).ConfigureAwait(false),
        });

    /// <summary>Reads each registered Observer's lag and dead-letters through the Monitor, in order.</summary>
    private static async Task<IReadOnlyList<ObserverDeliveries.ObserverHealth>> LoadObserverHealthAsync(
        BackWaveMonitor monitor, IReadOnlyList<ObserverRegistration> observers)
    {
        var health = new List<ObserverDeliveries.ObserverHealth>(observers.Count);
        foreach (var registration in observers)
        {
            var lag = await monitor.GetObserverLagAsync(registration.Id, registration.Subscription).ConfigureAwait(false);
            health.Add(new ObserverDeliveries.ObserverHealth(
                registration.Id,
                DescribeSubscription(registration.Subscription),
                lag,
                await monitor.ListObserverDeadLettersAsync(registration.Id).ConfigureAwait(false)));
        }
        return health;
    }

    /// <summary>A one-line, human-readable summary of an Observer's subscription filter (metadata only).</summary>
    private static string DescribeSubscription(ObserverSubscription subscription)
    {
        var states = subscription.States.Count == 0
            ? "no states"
            : string.Join(", ", subscription.States.Select(DashboardGlossary.StateName));
        var wire = subscription.WireName is { } wireName ? $", Wire Name {wireName}" : ", any Wire Name";
        var queue = subscription.Queue is { } q ? $", Queue {q}" : ", any Queue";
        return states + wire + queue;
    }

    private static LiveView SchedulesView(BackWaveMonitor monitor, string basePath, DashboardActions actions) => new(
        typeof(Schedules),
        async () => new Dictionary<string, object?>
        {
            ["BasePath"] = basePath,
            ["Items"] = await monitor.ListSchedulesAsync().ConfigureAwait(false),
            ["Actions"] = actions,
        });

    private static async Task ScheduleInstancesAsync(
        HttpContext context, BackWaveMonitor monitor, string basePath, string scheduleId, DashboardActions actions)
    {
        // The schedule and the jobs it mints are distinct things with distinct lifecycles.
        var schedules = await monitor.ListSchedulesAsync().ConfigureAwait(false);
        if (schedules.FirstOrDefault(s => s.ScheduleId == scheduleId) is not { } schedule)
        {
            await NotFoundAsync(context, basePath).ConfigureAwait(false);
            return;
        }
        var instances = await monitor.ListJobsAsync(
            new JobQuery { ScheduleId = scheduleId, SortDirection = JobSortDirection.NewestFirst, MaxResults = PageSize }).ConfigureAwait(false);
        var html = await DashboardRenderer.RenderDocumentAsync<ScheduleDetail>(context, new Dictionary<string, object?>
        {
            ["BasePath"] = basePath,
            ["Schedule"] = schedule,
            ["Instances"] = instances,
            ["PageSize"] = PageSize,
            ["Actions"] = actions,
        }).ConfigureAwait(false);
        await WriteAsync(context, html).ConfigureAwait(false);
    }

    private static string? NonEmpty(Microsoft.Extensions.Primitives.StringValues values)
        => values is [{ Length: > 0 } value] ? value : null;

    private static Task WriteAsync(HttpContext context, string html)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(html);
    }

    private static async Task NotFoundAsync(HttpContext context, string basePath)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        var html = await DashboardRenderer.RenderDocumentAsync<NotFound>(context, new Dictionary<string, object?>
        {
            ["BasePath"] = basePath,
        }).ConfigureAwait(false);
        await WriteAsync(context, html).ConfigureAwait(false);
    }

    private static Task BadRequestAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(message);
    }
}
