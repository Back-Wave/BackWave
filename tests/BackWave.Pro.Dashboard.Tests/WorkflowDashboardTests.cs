using System.Net;
using System.Text.RegularExpressions;
using BackWave.Dashboard;
using BackWave.Monitor;
using BackWave.Operations;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Pro.Dashboard.Tests;

/// <summary>
/// HTTP-level tests for the BackWave Pro Workflows dashboard surface, contributed into the free
/// dashboard through the extension seam: the list, the graph/detail view, the inline member panel, and
/// the gated cancel. The host registers the Pro dashboard (<c>AddBackWaveProDashboard</c>) over the
/// unchanged free dashboard, so these exercise exactly what a consumer who installs the Pro dashboard
/// package gets. A Workflow's status is always a PROJECTION of its members (failure dominates), never
/// stored.
/// </summary>
public sealed class WorkflowDashboardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    /// <summary>A host with the free dashboard mounted at /backwave and the Pro dashboard surfaces
    /// registered over it (unlicensed — the realistic free-use default), so the Workflows surface is
    /// present exactly because the Pro dashboard package is installed.</summary>
    private static async Task<(WebApplication App, InMemoryJobStore Store, HttpClient Http)> StartAsync(
        BackWaveDashboardOptions? options = null)
    {
        var store = new InMemoryJobStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new BackWaveMonitor(store, registry: null));
        builder.Services.AddSingleton(new BackWaveOperator(store));
        builder.Services.AddAntiforgery(); // Operator Actions are antiforgery-protected POSTs
        builder.Services.AddBackWavePro(license: null);   // Pro present (unlicensed); features run in full
        builder.Services.AddBackWaveProDashboard();        // contributes the Workflows surface via the seam

        var app = builder.Build();
        app.UseBackWaveDashboard("/backwave", options);
        await app.StartAsync();
        return (app, store, app.GetTestClient());
    }

    /// <summary>A member NewJob with the given id, ready for a WorkflowDefinition.</summary>
    private static NewJob Member(Guid id, string wireName, IReadOnlyList<Guid>? parents = null, string queue = "default")
        => new NewJob(id, wireName, "{}"u8.ToArray(), queue, T0) { Parents = parents ?? [] };

    /// <summary>Enqueues a two-member linear Workflow (charge → receipt) and returns the ids.</summary>
    private static async Task<(Guid WorkflowId, Guid Charge, Guid Receipt)> EnqueueLinearWorkflowAsync(
        InMemoryJobStore store, string? name = "order-flow", DateTimeOffset? createdAt = null)
    {
        var workflowId = Guid.NewGuid();
        var charge = Guid.NewGuid();
        var receipt = Guid.NewGuid();
        var definition = new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Name = name,
            Members = [Member(charge, "charge"), Member(receipt, "receipt", parents: [charge])],
        };
        Assert.Equal(WorkflowEnqueueResult.Ok, await store.EnqueueWorkflowAsync(definition, createdAt ?? T0));
        return (workflowId, charge, receipt);
    }

    [Fact]
    public async Task Workflows_ListRendersWithDerivedStatusBadges_OrderedByCreatedAt()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            // Oldest first: a Running flow created at T0, then a fully-Succeeded one created later.
            // ListWorkflows orders by CreatedAt, so the earlier-created Workflow leads.
            var running = await EnqueueLinearWorkflowAsync(store, "running-flow", createdAt: T0);
            var (doneId, doneCharge, doneReceipt) = await EnqueueLinearWorkflowAsync(store, "done-flow", createdAt: T0.AddMinutes(1));
            await DriveToTerminalAsync(store, doneCharge, new JobOutcome.Success());
            await DriveToTerminalAsync(store, doneReceipt, new JobOutcome.Success());

            var html = await http.GetStringAsync("/backwave/workflows");

            Assert.Contains("data-screen-label=\"Workflows\"", html);
            Assert.Contains("running-flow", html);
            Assert.Contains("done-flow", html);
            // The derived status badges: the first is still Running, the second every-member-Succeeded.
            Assert.Contains("Running", html);
            Assert.Contains("Succeeded", html);
            // CreatedAt ordering (oldest first): the T0 running flow appears before the later done flow.
            AssertWorkflowsAppearInOrder(html, running.WorkflowId, doneId);
        }
    }

    [Fact]
    public async Task WorkflowDetail_RendersTheDag_WithPerMemberState_AndEdges()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            var (workflowId, charge, receipt) = await EnqueueLinearWorkflowAsync(store);
            // The parent succeeds; the child is then released to Scheduled (no longer Awaiting Parent).
            await DriveToTerminalAsync(store, charge, new JobOutcome.Success());

            var html = await http.GetStringAsync($"/backwave/workflows/{workflowId}");

            Assert.Contains("data-screen-label=\"Workflow\"", html);
            Assert.Contains("Member graph", html);
            Assert.Contains("2 members", html);
            // Per-member state badges and drill-through links to each Job detail page.
            Assert.Contains($"/backwave/jobs/{charge}", html);
            Assert.Contains($"/backwave/jobs/{receipt}", html);
            Assert.Contains("Succeeded", html); // the charged parent
            // The structural edge is surfaced as the child's "depends on" parent — the parent appears
            // BEFORE the child (dependency order), so the charge node leads the receipt node.
            AssertAppearInOrder(html, charge, receipt);
        }
    }

    [Fact]
    public async Task WorkflowDetail_RendersTheGraphCanvas_WithNodesEdgesAndMinimap()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            var (workflowId, charge, receipt) = await EnqueueLinearWorkflowAsync(store);

            var html = await http.GetStringAsync($"/backwave/workflows/{workflowId}");

            // The pan/zoom canvas, its node boxes, an SVG edge for the structural dependency, the
            // minimap, and the graph script that drives them all.
            Assert.Contains("data-graph", html);
            Assert.Contains("bw-gnode", html);
            Assert.Contains("class=\"bw-graph__edge", html);
            Assert.Contains("data-graph-minimap", html);
            Assert.Contains("data-graph-pane", html);
            // Each node still drills through to its standalone Job detail page (the "open" link).
            Assert.Contains($"/backwave/jobs/{charge}", html);
            Assert.Contains($"/backwave/jobs/{receipt}", html);
            // No member selected ⇒ no inline Job detail panel below the graph.
            Assert.DoesNotContain("Transition Log", html);
        }
    }

    [Fact]
    public async Task WorkflowDetail_SelectingAMember_RendersItsJobDetailPanelInline()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            var (workflowId, charge, receipt) = await EnqueueLinearWorkflowAsync(store);
            await DriveToTerminalAsync(store, charge, new JobOutcome.Success());

            // ?member= selects that node in place: the very same JobDetailPanel the Job page renders,
            // with the node marked selected and a link out to the full Job detail page.
            var html = await http.GetStringAsync($"/backwave/workflows/{workflowId}?member={charge}");

            Assert.Contains("Member graph", html);          // graph still shown
            Assert.Contains("is-selected", html);           // the chosen node is highlighted
            Assert.Contains("Transition Log", html);        // the inline detail panel rendered
            Assert.Contains("open full job page", html);     // the link out to the standalone page
            Assert.Contains($"/backwave/jobs/{charge}", html);
        }
    }

    [Fact]
    public async Task WorkflowDetail_MemberNotInThisWorkflow_SelectsNothing()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            var (workflowId, _, _) = await EnqueueLinearWorkflowAsync(store);

            // A well-formed but foreign member id selects nothing — the graph renders, no panel, no error.
            var resp = await http.GetAsync($"/backwave/workflows/{workflowId}?member={Guid.NewGuid()}");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var html = await resp.Content.ReadAsStringAsync();
            Assert.Contains("Member graph", html);
            Assert.DoesNotContain("Transition Log", html);
        }
    }

    [Fact]
    public async Task WorkflowDetail_FailureDominates_IsVisibleAtTheWorkflowLevel()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            // A Workflow with two independent members: one Succeeds, one Dead-Letters. Failure dominates,
            // so the derived Workflow status is Failed even with a Succeeded sibling.
            var workflowId = Guid.NewGuid();
            var ok = Guid.NewGuid();
            var bad = Guid.NewGuid();
            await store.EnqueueWorkflowAsync(new WorkflowDefinition
            {
                WorkflowId = workflowId,
                Name = "mixed-flow",
                Members = [Member(ok, "ok-step"), Member(bad, "bad-step")],
            }, T0);
            await DriveToTerminalAsync(store, ok, new JobOutcome.Success());
            await DriveToTerminalAsync(store, bad, new JobOutcome.Failure(null, "exhausted"));

            var html = await http.GetStringAsync($"/backwave/workflows/{workflowId}");
            // The Workflow-level status reads Failed (failure dominates), with its blurb.
            Assert.Contains("Failed", html);
            Assert.Contains("failure dominates", html);
            // Both per-member states still show on their nodes.
            Assert.Contains("Succeeded", html);
            Assert.Contains("Dead-Lettered", html);
        }
    }

    [Fact]
    public async Task WorkflowDetail_404sForUnknownId()
    {
        var (app, _, http) = await StartAsync();
        await using (app)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/backwave/workflows/{Guid.NewGuid()}")).StatusCode);
        }
    }

    [Fact]
    public async Task WorkflowCancel_WorksEndToEnd_WhenAuthorized_AndShowsTheControlOnlyWhileRunning()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeCancel = _ => ValueTask.FromResult(true),
        });
        await using (app)
        {
            var (workflowId, charge, receipt) = await EnqueueLinearWorkflowAsync(store);

            // Running + Cancel granted ⇒ the Cancel Workflow control renders, with an antiforgery token.
            var (cookie, token) = await AntiforgeryAsync(http, $"/backwave/workflows/{workflowId}");
            Assert.NotEmpty(token);
            var get = await http.GetStringAsync($"/backwave/workflows/{workflowId}");
            Assert.Contains($"/backwave/workflows/{workflowId}/cancel", get);

            var resp = await PostActionAsync(http, $"/backwave/workflows/{workflowId}/cancel", cookie, token);
            Assert.Equal(HttpStatusCode.SeeOther, resp.StatusCode);
            Assert.Equal($"/backwave/workflows/{workflowId}", resp.Headers.Location?.ToString());

            // The fan-out cancelled both non-terminal members; the Workflow now projects Cancelled,
            // distinct from Failed (an operator cancel yields no failed members).
            var monitor = new BackWaveMonitor(store);
            Assert.Equal(JobState.Cancelled, (await monitor.GetJobAsync(charge))!.State);
            Assert.Equal(JobState.Cancelled, (await monitor.GetJobAsync(receipt))!.State);
            Assert.Equal(WorkflowStatus.Cancelled, (await monitor.GetWorkflowAsync(workflowId))!.Status);

            // Now drained (no longer Running), the control is gone.
            var afterHtml = await http.GetStringAsync($"/backwave/workflows/{workflowId}");
            Assert.Contains("Cancelled", afterHtml);
            Assert.DoesNotContain($"/backwave/workflows/{workflowId}/cancel", afterHtml);
        }
    }

    [Fact]
    public async Task WorkflowCancel_IsDenied_AndControlHidden_ForAViewOnlyIdentity()
    {
        var (app, store, http) = await StartAsync(); // all actions default-deny, including Cancel
        await using (app)
        {
            var (workflowId, charge, _) = await EnqueueLinearWorkflowAsync(store);

            // View-only sees the graph, but no Cancel control and no antiforgery token.
            var html = await http.GetStringAsync($"/backwave/workflows/{workflowId}");
            Assert.Contains("Member graph", html);
            Assert.DoesNotContain("/cancel", html);
            Assert.DoesNotContain("__RequestVerificationToken", html);

            // And the POST is refused outright (no new Permission: it is the Cancel Permission).
            var resp = await PostActionAsync(http, $"/backwave/workflows/{workflowId}/cancel", "", "");
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

            // Nothing was cancelled.
            var monitor = new BackWaveMonitor(store);
            Assert.NotEqual(JobState.Cancelled, (await monitor.GetJobAsync(charge))!.State);
        }
    }

    [Fact]
    public async Task Workflows_RenderThroughTheDesignSystemSpine_ListIsLive_DetailIsStatic()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            var (workflowId, _, _) = await EnqueueLinearWorkflowAsync(store);

            var list = await http.GetStringAsync("/backwave/workflows");
            Assert.StartsWith("<!DOCTYPE html>", list);
            Assert.Contains("class=\"shell\"", list);
            Assert.Contains("data-screen-label=\"Workflows\"", list);
            Assert.Contains("--wave-500", list);
            Assert.Contains("new EventSource", list);        // the list is a live view
            Assert.DoesNotContain("_framework/blazor", list);

            var detail = await http.GetStringAsync($"/backwave/workflows/{workflowId}");
            Assert.StartsWith("<!DOCTYPE html>", detail);
            Assert.Contains("data-screen-label=\"Workflow\"", detail);
            Assert.DoesNotContain("new EventSource", detail); // the detail page is static
        }
    }

    [Fact]
    public async Task Workflows_NavEntry_IsSlottedAfterFailures()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            await EnqueueLinearWorkflowAsync(store);

            // The contributed Workflows nav entry appears in the sidebar, slotted right after the
            // built-in Failures entry (its After anchor) and before Observers.
            var html = await http.GetStringAsync("/backwave/");
            var failures = html.IndexOf("/backwave/failures", StringComparison.Ordinal);
            var workflows = html.IndexOf("/backwave/workflows", StringComparison.Ordinal);
            var observers = html.IndexOf("/backwave/observers", StringComparison.Ordinal);
            Assert.True(failures >= 0 && workflows >= 0 && observers >= 0);
            Assert.True(failures < workflows && workflows < observers,
                "The Workflows nav entry should sit between Failures and Observers.");
        }
    }

    /// <summary>Drives one already-enqueued Workflow member to a terminal outcome. Claims the available
    /// batch (worker w1) — sweeping in any claimable siblings, which then sit Leased under w1 — then
    /// reports the target by its current Attempt, whether claimed just now or in an earlier batch.</summary>
    private static async Task DriveToTerminalAsync(InMemoryJobStore store, Guid jobId, JobOutcome outcome)
    {
        await store.ClaimAsync(new ClaimRequest("w1", ["default"], 32, Lease, T0));
        var snapshot = (await new BackWaveMonitor(store).GetJobAsync(jobId))!;
        Assert.Equal(JobState.Leased, snapshot.State); // claimable members are Leased by w1 now
        await store.ReportOutcomeAsync(jobId, "w1", snapshot.Attempt, outcome, T0);
    }

    /// <summary>Asserts each workflow's detail link appears, in the given order, in the rendered HTML.</summary>
    private static void AssertWorkflowsAppearInOrder(string html, params Guid[] workflowIds)
    {
        var positions = workflowIds
            .Select(id => html.IndexOf($"/workflows/{id}", StringComparison.Ordinal))
            .ToList();
        Assert.DoesNotContain(-1, positions);
        for (var i = 1; i < positions.Count; i++)
        {
            Assert.True(positions[i - 1] < positions[i],
                $"Workflow {workflowIds[i - 1]} should appear before {workflowIds[i]}.");
        }
    }

    /// <summary>Asserts each member's Job detail link appears, in the given order, in the rendered HTML.</summary>
    private static void AssertAppearInOrder(string html, params Guid[] jobIds)
    {
        var positions = jobIds
            .Select(id => html.IndexOf($"/jobs/{id}", StringComparison.Ordinal))
            .ToList();
        Assert.DoesNotContain(-1, positions);
        for (var i = 1; i < positions.Count; i++)
        {
            Assert.True(positions[i - 1] < positions[i],
                $"Job {jobIds[i - 1]} should appear before {jobIds[i]}.");
        }
    }

    private static async Task<(string Cookie, string Token)> AntiforgeryAsync(HttpClient http, string getPath)
    {
        var get = await http.GetAsync(getPath);
        get.Headers.TryGetValues("Set-Cookie", out var setCookies);
        var cookie = (setCookies ?? [])
            .FirstOrDefault(c => c.Contains(".AspNetCore.Antiforgery"))?.Split(';')[0] ?? "";
        var html = await get.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        return (cookie, token);
    }

    private static Task<HttpResponseMessage> PostActionAsync(HttpClient http, string path, string cookie, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path);
        if (cookie.Length > 0)
        {
            req.Headers.Add("Cookie", cookie);
        }
        req.Content = new FormUrlEncodedContent(token.Length == 0
            ? []
            : [new KeyValuePair<string, string>("__RequestVerificationToken", token)]);
        return http.SendAsync(req);
    }
}
