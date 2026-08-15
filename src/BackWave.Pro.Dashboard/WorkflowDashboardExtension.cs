using BackWave.Dashboard;
using BackWave.Monitor;
using BackWave.Operations;
using BackWave.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Pro.Dashboard;

/// <summary>
/// A dashboard extension that contributes the BackWave Pro Workflows surface to the BackWave
/// dashboard: the Workflows navigation entry, the workflow list and graph pages, and the
/// cancel-workflow action. It plugs into the base dashboard through the dashboard's own extension
/// seam, so the base dashboard shows the Workflows surface only when this extension is registered —
/// and renders identically to its un-extended self otherwise. Register it with
/// <see cref="BackWaveProDashboardServiceCollectionExtensions.AddBackWaveProDashboard"/>.
/// </summary>
/// <remarks>
/// Workflows are a BackWave Pro feature: this surface appears whenever the Pro dashboard package is
/// installed, independent of license state (an unlicensed deployment still shows the surface, with the
/// separate unlicensed-Pro banner above it). The pages read through the Pro workflow monitor surface;
/// the inline member-job panel reads job-level data through the base monitor, behind the dashboard's
/// own sensitive-data permission. The cancel action reuses the dashboard's existing cancel permission.
/// </remarks>
public sealed class WorkflowDashboardExtension : IDashboardExtension
{
    // The Workflows nav glyph (three connected nodes), inline like the dashboard's built-in icons
    // (zero static assets); currentColor lets it inherit the sidebar ink.
    private const string WorkflowsIcon =
        """<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 8C6.65685 8 8 6.65685 8 5C8 3.34315 6.65685 2 5 2C3.34315 2 2 3.34315 2 5C2 6.65685 3.34315 8 5 8Z"/><path d="M19 8C20.6569 8 22 6.65685 22 5C22 3.34315 20.6569 2 19 2C17.3431 2 16 3.34315 16 5C16 6.65685 17.3431 8 19 8Z"/><path d="M12 22C13.6569 22 15 20.6569 15 19C15 17.3431 13.6569 16 12 16C10.3431 16 9 17.3431 9 19C9 20.6569 10.3431 22 12 22Z"/><path d="M5 8V11C5 12.4001 5 13.1002 5.27248 13.635C5.51217 14.1054 5.89462 14.4878 6.36502 14.7275C6.8998 15 7.59987 15 9 15H10M19 8V11C19 12.4001 19 13.1002 18.7275 13.635C18.4878 14.1054 18.1054 14.4878 17.635 14.7275C17.1002 15 16.4001 15 15 15H14"/></svg>""";

    /// <summary>The Workflows sidebar entry, slotted just after the built-in Failures entry.</summary>
    /// <returns>The single Workflows navigation entry.</returns>
    public IEnumerable<DashboardNavEntry> NavEntries() =>
    [
        new("workflows", "Workflows", "/workflows") { Icon = WorkflowsIcon, After = "failures" },
    ];

    /// <summary>The Workflow list (live) and graph/detail pages.</summary>
    /// <returns>The workflow page routes this extension serves.</returns>
    public IEnumerable<DashboardPageRoute> PageRoutes() =>
    [
        new() { Template = "workflows", Component = typeof(Workflows), Live = true, LoadAsync = LoadListAsync },
        new() { Template = "workflows/{id}", Component = typeof(WorkflowDetail), LoadAsync = LoadDetailAsync },
    ];

    /// <summary>The cancel-workflow action, gated on the dashboard's cancel permission.</summary>
    /// <returns>The workflow action routes this extension handles.</returns>
    public IEnumerable<DashboardActionRoute> ActionRoutes() =>
    [
        new() { Template = "workflows/{id}/cancel", Permission = options => options.AuthorizeCancel, HandleAsync = CancelAsync },
    ];

    // The Workflows list: every Workflow ordered by creation time, each with its derived status and
    // member count, through the Pro monitor surface. Live, because a Workflow's status moves under it
    // as members reach terminal states.
    private static async Task<Dictionary<string, object?>?> LoadListAsync(DashboardPageContext context)
    {
        var monitor = context.Http.RequestServices.GetRequiredService<BackWaveMonitor>();
        return new Dictionary<string, object?>
        {
            ["BasePath"] = context.BasePath,
            ["Items"] = await monitor.ListWorkflowsAsync().ConfigureAwait(false),
        };
    }

    // The full graph: members as JobSnapshots, the immutable structural edges, and the derived status,
    // through the Pro monitor surface. A non-Guid id or an unknown Workflow returns null, so the
    // dashboard renders its Not Found page.
    private static async Task<Dictionary<string, object?>?> LoadDetailAsync(DashboardPageContext context)
    {
        if (!Guid.TryParse(context.RouteValues["id"], out var workflowId))
        {
            return null;
        }
        var monitor = context.Http.RequestServices.GetRequiredService<BackWaveMonitor>();
        if (await monitor.GetWorkflowAsync(workflowId).ConfigureAwait(false) is not { } workflow)
        {
            return null;
        }

        var parameters = new Dictionary<string, object?>
        {
            ["BasePath"] = context.BasePath,
            ["Workflow"] = workflow,
            ["Actions"] = context.Actions,
        };

        // In-place member selection (?member=): when the query names a member of THIS Workflow, fetch
        // its Job detail through the SAME base-monitor reads the standalone Job page uses (history,
        // gating, payload-behind-gate) so the embedded member panel shows identical data. A member id
        // that is missing, malformed, or not part of this Workflow simply selects nothing — never an error.
        if (context.Http.Request.Query["member"] is [{ Length: > 0 } rawMember]
            && Guid.TryParse(rawMember, out var memberId)
            && workflow.Members.FirstOrDefault(m => m.JobId == memberId) is { } selected)
        {
            IReadOnlyList<Guid> gatingParents = selected.State == JobState.AwaitingParent
                ? (await monitor.GetDependencyEdgesAsync(memberId).ConfigureAwait(false)).GatingParents
                : [];
            var canViewSensitiveData = context.Actions.CanViewSensitiveData && context.Options.SensitiveDataExposureEnabled;
            var payload = canViewSensitiveData
                ? await monitor.GetJobPayloadAsync(memberId).ConfigureAwait(false)
                : null;
            var output = canViewSensitiveData
                ? await monitor.GetJobOutputViewAsync(memberId).ConfigureAwait(false)
                : null;
            parameters["Selected"] = selected;
            parameters["SelectedGatingParents"] = gatingParents;
            parameters["SelectedHistory"] = await monitor.GetJobHistoryAsync(memberId).ConfigureAwait(false);
            parameters["HistoryDisabled"] = monitor.IsHistoryRecordingDisabled;
            parameters["SelectedPayload"] = payload;
            parameters["SelectedOutput"] = output;
            parameters["SelectedCanViewSensitiveData"] = canViewSensitiveData;
        }
        return parameters;
    }

    // Cancel the Workflow's non-terminal members through the Pro operator surface, stamped with the
    // resolved actor, then redirect back to the Workflow's detail page. A non-Guid id (only reachable
    // by a hand-crafted POST) redirects to the list rather than acting.
    private static async Task<string> CancelAsync(DashboardActionContext context)
    {
        if (!Guid.TryParse(context.RouteValues["id"], out var workflowId))
        {
            return $"{context.BasePath}/workflows";
        }
        var @operator = context.Http.RequestServices.GetRequiredService<BackWaveOperator>();
        await @operator.CancelWorkflowAsync(workflowId, context.Actor).ConfigureAwait(false);
        return $"{context.BasePath}/workflows/{workflowId}";
    }
}
