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
/// Endpoint-level contract tests for the Pro dashboard's state-mutating route, contributed through the
/// ADR 0033 extension seam (issue 0206). The only mutating Pro route today is workflow cancel
/// (<c>workflows/{id}/cancel</c>); there is no workflow retry/restart route (restart exists as a domain
/// capability, not an operator/store action), so the enumeration below is the complete Pro write path.
///
/// These pin the same four dimensions the free dashboard's contract tests do: default-deny denies
/// (asserted against store state), the antiforgery gate rejects a tokenless POST, the edges (unknown /
/// already-terminal / malformed workflow) yield a documented result and never a 500, and a performed
/// cancel fans out through the store and audits each member exactly once.
/// </summary>
public sealed class WorkflowWritePathContractTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    private const string DashboardActor = "dashboard";

    /// <summary>A host with the free dashboard mounted and the Pro dashboard surfaces registered over it
    /// (unlicensed — the free-use default), so the Workflows write path is present exactly because the
    /// Pro dashboard package is installed.</summary>
    private static async Task<(WebApplication App, InMemoryJobStore Store, HttpClient Http)> StartAsync(
        BackWaveDashboardOptions? options = null)
    {
        var store = new InMemoryJobStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new BackWaveMonitor(store, registry: null));
        builder.Services.AddSingleton(new BackWaveOperator(store));
        builder.Services.AddAntiforgery();
        builder.Services.AddBackWavePro(license: null);
        builder.Services.AddBackWaveProDashboard();
        var app = builder.Build();
        app.UseBackWaveDashboard("/backwave", options);
        await app.StartAsync();
        return (app, store, app.GetTestClient());
    }

    private static NewJob Member(Guid id, string wireName, IReadOnlyList<Guid>? parents = null)
        => new(id, wireName, "{}"u8.ToArray(), "default", T0) { Parents = parents ?? [] };

    /// <summary>Enqueues a two-member linear Workflow (charge → receipt), still Running, and returns the ids.</summary>
    private static async Task<(Guid WorkflowId, Guid Charge, Guid Receipt)> EnqueueRunningWorkflowAsync(InMemoryJobStore store)
    {
        var workflowId = Guid.NewGuid();
        var charge = Guid.NewGuid();
        var receipt = Guid.NewGuid();
        Assert.Equal(WorkflowEnqueueResult.Ok, await store.EnqueueWorkflowAsync(new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Name = "order-flow",
            Members = [Member(charge, "charge"), Member(receipt, "receipt", parents: [charge])],
        }, T0));
        return (workflowId, charge, receipt);
    }

    /// <summary>Enqueues a Workflow of two INDEPENDENT (no dependency edge) members, both Running, so a
    /// cancel fan-out cancels each DIRECTLY — neither is swept to terminal by the other's cascade. Returns
    /// the ids.</summary>
    private static async Task<(Guid WorkflowId, Guid First, Guid Second)> EnqueueIndependentWorkflowAsync(InMemoryJobStore store)
    {
        var workflowId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.Equal(WorkflowEnqueueResult.Ok, await store.EnqueueWorkflowAsync(new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Name = "fan-out",
            Members = [Member(first, "step-a"), Member(second, "step-b")],
        }, T0));
        return (workflowId, first, second);
    }

    private static BackWaveDashboardOptions CancelGranted() =>
        new() { AuthorizeCancel = _ => ValueTask.FromResult(true) };

    [Fact]
    public void TheWorkflowCancelRoute_IsTheCompleteProWritePath_SoANewOneIsConspicuous()
    {
        // The Pro dashboard extension (WorkflowDashboardExtension.ActionRoutes) contributes exactly one
        // state-mutating route. Add a new one there ⇒ add it here AND give it contract coverage below.
        var routes = new WorkflowDashboardExtension().ActionRoutes().Select(r => r.Template).ToArray();
        Assert.Equal(["workflows/{id}/cancel"], routes);
    }

    // ── 1. Authorization: default-deny actually denies ──────────────────────────

    [Fact]
    public async Task WorkflowCancel_DeniedByDefault_Rejects403_AndCancelsNothing()
    {
        var (app, store, http) = await StartAsync(); // Cancel default-denies
        await using (app)
        {
            var (workflowId, charge, receipt) = await EnqueueRunningWorkflowAsync(store);

            var resp = await PostActionAsync(http, $"/backwave/workflows/{workflowId}/cancel", "", "");
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

            var monitor = new BackWaveMonitor(store);
            Assert.NotEqual(JobState.Cancelled, (await monitor.GetJobAsync(charge))!.State);
            Assert.NotEqual(JobState.Cancelled, (await monitor.GetJobAsync(receipt))!.State);
            Assert.DoesNotContain(await store.ListAuditRecordsAsync(charge.ToString()), r => r.Actor == DashboardActor);
        }
    }

    // ── 2. Antiforgery: a tokenless POST is rejected even when authorized ────────

    [Fact]
    public async Task WorkflowCancel_MissingAntiforgeryToken_Rejects400_EvenWhenAuthorized()
    {
        var (app, store, http) = await StartAsync(CancelGranted());
        await using (app)
        {
            var (workflowId, charge, _) = await EnqueueRunningWorkflowAsync(store);

            var resp = await PostActionAsync(http, $"/backwave/workflows/{workflowId}/cancel", "", "");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

            var monitor = new BackWaveMonitor(store);
            Assert.NotEqual(JobState.Cancelled, (await monitor.GetJobAsync(charge))!.State);
            Assert.DoesNotContain(await store.ListAuditRecordsAsync(charge.ToString()), r => r.Actor == DashboardActor);
        }
    }

    // ── 3 & 4. Effect fidelity + audit: authorized ⇒ fan-out lands, audited per member ──

    [Fact]
    public async Task WorkflowCancel_Authorized_CancelsEveryNonTerminalMember_AndAuditsEachExactlyOnce()
    {
        var (app, store, http) = await StartAsync(CancelGranted());
        await using (app)
        {
            // Independent members so the fan-out cancels each DIRECTLY (a linear Workflow would cascade
            // the child to terminal off the parent's cancel, leaving it with no direct audit record).
            var (workflowId, first, second) = await EnqueueIndependentWorkflowAsync(store);

            var (cookie, token) = await AntiforgeryAsync(http, $"/backwave/workflows/{workflowId}");
            Assert.NotEmpty(token);

            var resp = await PostActionAsync(http, $"/backwave/workflows/{workflowId}/cancel", cookie, token);
            Assert.Equal(HttpStatusCode.SeeOther, resp.StatusCode);
            Assert.Equal($"/backwave/workflows/{workflowId}", resp.Headers.Location?.ToString());

            // Both non-terminal members are cancelled; the Workflow projects Cancelled (an operator
            // cancel yields no failed members, so it is not Failed).
            var monitor = new BackWaveMonitor(store);
            Assert.Equal(JobState.Cancelled, (await monitor.GetJobAsync(first))!.State);
            Assert.Equal(JobState.Cancelled, (await monitor.GetJobAsync(second))!.State);
            Assert.Equal(WorkflowStatus.Cancelled, (await monitor.GetWorkflowAsync(workflowId))!.Status);

            // The fan-out audits each member's cancel exactly once, against the dashboard actor.
            foreach (var member in (Guid[])[first, second])
            {
                var recorded = (await store.ListAuditRecordsAsync(member.ToString()))
                    .Where(r => r.Actor == DashboardActor).ToList();
                Assert.Single(recorded);
                Assert.Equal(OperatorAction.Cancel, recorded[0].Action);
            }
        }
    }

    // ── 2b. Edge matrix: documented result, never a 500 ─────────────────────────

    [Fact]
    public async Task WorkflowCancel_OnUnknownWorkflow_Is303NoOp_NeverA500()
    {
        var (app, store, http) = await StartAsync(CancelGranted());
        await using (app)
        {
            // A decoy running Workflow gives the detail page a cancel form (hence a token).
            var (decoyId, decoyCharge, _) = await EnqueueRunningWorkflowAsync(store);
            var (cookie, token) = await AntiforgeryAsync(http, $"/backwave/workflows/{decoyId}");

            var unknownId = Guid.NewGuid();
            var resp = await PostActionAsync(http, $"/backwave/workflows/{unknownId}/cancel", cookie, token);
            // A valid-but-unknown id redirects to its own (Not Found) detail page — the documented no-op.
            Assert.Equal(HttpStatusCode.SeeOther, resp.StatusCode);
            Assert.Equal($"/backwave/workflows/{unknownId}", resp.Headers.Location?.ToString());

            // Nothing was cancelled anywhere; the bystander Workflow is untouched.
            var monitor = new BackWaveMonitor(store);
            Assert.NotEqual(JobState.Cancelled, (await monitor.GetJobAsync(decoyCharge))!.State);
            Assert.DoesNotContain(await store.ListAuditRecordsAsync(decoyCharge.ToString()), r => r.Actor == DashboardActor);
        }
    }

    [Fact]
    public async Task WorkflowCancel_OnAlreadyTerminalWorkflow_Is303NoOp_NoAudit_NeverA500()
    {
        var (app, store, http) = await StartAsync(CancelGranted());
        await using (app)
        {
            var (workflowId, charge, receipt) = await EnqueueRunningWorkflowAsync(store);
            // Drive both members to Succeeded so the whole Workflow is terminal.
            await DriveToTerminalAsync(store, charge, new JobOutcome.Success());
            await DriveToTerminalAsync(store, receipt, new JobOutcome.Success());

            // A separate running decoy provides the cancel form/token (a terminal Workflow shows none).
            var (decoyId, _, _) = await EnqueueRunningWorkflowAsync(store);
            var (cookie, token) = await AntiforgeryAsync(http, $"/backwave/workflows/{decoyId}");

            var resp = await PostActionAsync(http, $"/backwave/workflows/{workflowId}/cancel", cookie, token);
            Assert.Equal(HttpStatusCode.SeeOther, resp.StatusCode);

            // Terminal members are skipped by the fan-out: no state change, no audit for them.
            var monitor = new BackWaveMonitor(store);
            Assert.Equal(JobState.Succeeded, (await monitor.GetJobAsync(charge))!.State);
            Assert.Equal(JobState.Succeeded, (await monitor.GetJobAsync(receipt))!.State);
            Assert.Empty(await store.ListAuditRecordsAsync(charge.ToString()));
            Assert.Empty(await store.ListAuditRecordsAsync(receipt.ToString()));
        }
    }

    [Fact]
    public async Task WorkflowCancel_OnMalformedId_Is303ToTheList_NeverA500()
    {
        var (app, store, http) = await StartAsync(CancelGranted());
        await using (app)
        {
            // A running decoy provides the cancel form/token; the malformed POST is hand-crafted.
            var (decoyId, _, _) = await EnqueueRunningWorkflowAsync(store);
            var (cookie, token) = await AntiforgeryAsync(http, $"/backwave/workflows/{decoyId}");

            // The route template matches any single {id} segment; a non-Guid id is only reachable by a
            // hand-crafted POST and redirects to the list rather than acting.
            var resp = await PostActionAsync(http, "/backwave/workflows/not-a-guid/cancel", cookie, token);
            Assert.Equal(HttpStatusCode.SeeOther, resp.StatusCode);
            Assert.Equal("/backwave/workflows", resp.Headers.Location?.ToString());
        }
    }

    /// <summary>Drives one already-enqueued Workflow member to a terminal outcome (claims the available
    /// batch, then reports the target by its current Attempt).</summary>
    private static async Task DriveToTerminalAsync(InMemoryJobStore store, Guid jobId, JobOutcome outcome)
    {
        await store.ClaimAsync(new ClaimRequest("w1", ["default"], 32, Lease, T0));
        var snapshot = (await new BackWaveMonitor(store).GetJobAsync(jobId))!;
        Assert.Equal(JobState.Leased, snapshot.State);
        await store.ReportOutcomeAsync(jobId, "w1", snapshot.Attempt, outcome, T0);
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
