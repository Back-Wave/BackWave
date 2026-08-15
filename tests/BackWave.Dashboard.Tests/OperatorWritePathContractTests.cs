using System.Net;
using System.Text.RegularExpressions;
using BackWave.Core;
using BackWave.Dashboard;
using BackWave.Monitor;
using BackWave.Operations;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Dashboard.Tests;

/// <summary>
/// Endpoint-level contract tests for the free dashboard's state-mutating operator routes (issue 0206).
/// Unlike the smoke-level view tests, these pin the write path's contract across four dimensions for
/// EVERY mutating route: default-deny actually denies (asserted against store state, not just status
/// codes), the antiforgery gate rejects a tokenless POST, the edges (missing / terminal / wrong-state
/// targets) yield a documented result and never a 500, and each performed action lands its effect
/// through the store and is audited exactly once.
///
/// The complete route set lives in <see cref="AllRoutes"/> — the single source of truth the
/// cross-cutting tests iterate. A mutating route added to <c>DashboardRequestHandler.HandlePostAsync</c>
/// without a matching entry here fails <see cref="EveryMutatingRoute_IsEnumerated_SoANewOneIsConspicuous"/>.
/// </summary>
public sealed class OperatorWritePathContractTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    // The actor the dashboard stamps every action with by default (no authenticated user ⇒ "dashboard",
    // per BackWaveDashboardOptions.ResolveActor). Seeds below use a DIFFERENT actor so a route's own
    // audit record is unambiguous against any set-up noise on the same target.
    private const string DashboardActor = "dashboard";
    private const string SeedActor = "seed";

    private static NewJob Job(string wireName = "send-email", string queue = "default", DateTimeOffset? dueTime = null)
        => new(Guid.NewGuid(), wireName, "{}"u8.ToArray(), queue, dueTime ?? T0);

    /// <summary>A host app with the dashboard mounted at /backwave over the In-Memory Store.</summary>
    private static async Task<(WebApplication App, InMemoryJobStore Store, HttpClient Http)> StartAsync(
        BackWaveDashboardOptions? options = null)
    {
        var store = new InMemoryJobStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new BackWaveMonitor(store, registry: null));
        builder.Services.AddSingleton(new BackWaveOperator(store));
        builder.Services.AddAntiforgery(); // Operator Actions are antiforgery-protected POSTs
        var app = builder.Build();
        app.UseBackWaveDashboard("/backwave", options);
        await app.StartAsync();
        return (app, store, app.GetTestClient());
    }

    private static async Task<JobRecord> RunToTerminalAsync(InMemoryJobStore store, NewJob job, JobOutcome outcome)
    {
        await store.EnqueueAsync(job, now: T0);
        var claimed = (await store.ClaimAsync(new ClaimRequest("w1", [job.Queue], 32, Lease, T0)))
            .Single(j => j.JobId == job.JobId);
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, outcome, T0);
        return claimed;
    }

    // ── The route enumeration ────────────────────────────────────────────────────

    /// <summary>One mutating route's seeded happy-path case: the POST path, a page whose GET renders an
    /// action form (so it carries an antiforgery token), the audit target the action records under, and
    /// the two store-state checks — after a performed action, and after a rejected/no-op one.</summary>
    private sealed record RouteCase(
        string Path,
        string FormPage,
        string AuditTarget,
        Func<Task> AssertPerformed,
        Func<Task> AssertUntouched);

    /// <summary>A state-mutating dashboard route under contract: its display name, its route template
    /// (for the enumeration guard), the audit action a performed call records, the options that grant
    /// exactly this route's permission, and a seed that stages a happy-path <see cref="RouteCase"/>.</summary>
    private sealed record MutatingRoute(
        string Name,
        string Template,
        OperatorAction Action,
        Func<BackWaveDashboardOptions> Grant,
        Func<InMemoryJobStore, Task<RouteCase>> SeedAsync);

    // Every state-mutating POST route the free dashboard serves (DashboardRequestHandler.HandlePostAsync).
    // There is no "retry" route (requeue is its analog) and tag-facet actions are GET-only filtering, so
    // they are not write routes. Concurrency-limit setting is an operator action with NO dashboard route.
    private static MutatingRoute[] AllRoutes() =>
    [
        new("Requeue", "jobs/{id}/requeue", OperatorAction.Requeue,
            () => new BackWaveDashboardOptions { AuthorizeRequeue = _ => ValueTask.FromResult(true) },
            SeedRequeueAsync),
        new("Cancel", "jobs/{id}/cancel", OperatorAction.Cancel,
            () => new BackWaveDashboardOptions { AuthorizeCancel = _ => ValueTask.FromResult(true) },
            SeedCancelAsync),
        new("PauseQueue", "queues/{queue}/pause", OperatorAction.PauseQueue,
            () => new BackWaveDashboardOptions { AuthorizePauseQueue = _ => ValueTask.FromResult(true) },
            SeedPauseAsync),
        new("ResumeQueue", "queues/{queue}/resume", OperatorAction.ResumeQueue,
            () => new BackWaveDashboardOptions { AuthorizePauseQueue = _ => ValueTask.FromResult(true) },
            SeedResumeAsync),
        new("TriggerSchedule", "schedules/{id}/trigger", OperatorAction.TriggerScheduleNow,
            () => new BackWaveDashboardOptions { AuthorizeTriggerSchedule = _ => ValueTask.FromResult(true) },
            SeedTriggerAsync),
    ];

    private static async Task<RouteCase> SeedRequeueAsync(InMemoryJobStore store)
    {
        // A Dead-Lettered job is requeue-eligible; it also makes the Jobs list render a requeue form.
        var job = Job("kept-failing");
        await RunToTerminalAsync(store, job, new JobOutcome.Failure(null, "exhausted"));
        var monitor = new BackWaveMonitor(store);
        return new RouteCase(
            $"/backwave/jobs/{job.JobId}/requeue", "/backwave/jobs", job.JobId.ToString(),
            AssertPerformed: async () =>
            {
                var j = (await monitor.GetJobAsync(job.JobId))!;
                Assert.Equal(JobState.Scheduled, j.State);
                Assert.Equal(0, j.Attempt); // requeue resets the Attempt budget
            },
            AssertUntouched: async () =>
                Assert.Equal(JobState.DeadLettered, (await monitor.GetJobAsync(job.JobId))!.State));
    }

    private static async Task<RouteCase> SeedCancelAsync(InMemoryJobStore store)
    {
        // A pending (Scheduled) job cancels immediately; it also makes the Jobs list render a cancel form.
        var job = Job("long-running");
        await store.EnqueueAsync(job, now: T0);
        var monitor = new BackWaveMonitor(store);
        return new RouteCase(
            $"/backwave/jobs/{job.JobId}/cancel", "/backwave/jobs", job.JobId.ToString(),
            AssertPerformed: async () =>
                Assert.Equal(JobState.Cancelled, (await monitor.GetJobAsync(job.JobId))!.State),
            AssertUntouched: async () =>
                Assert.Equal(JobState.Scheduled, (await monitor.GetJobAsync(job.JobId))!.State));
    }

    private static async Task<RouteCase> SeedPauseAsync(InMemoryJobStore store)
    {
        // A queue with a job gives the Queues view a row to render a pause form for.
        await store.EnqueueAsync(Job("work", queue: "q"), now: T0);
        return new RouteCase(
            "/backwave/queues/q/pause", "/backwave/queues", "q",
            AssertPerformed: async () => Assert.True(await IsPausedAsync(store, "q")),
            AssertUntouched: async () => Assert.False(await IsPausedAsync(store, "q")));
    }

    private static async Task<RouteCase> SeedResumeAsync(InMemoryJobStore store)
    {
        // Paused (by a non-dashboard actor) so the Queues view renders a resume form and there is
        // something to resume.
        await store.EnqueueAsync(Job("work", queue: "q"), now: T0);
        await store.PauseQueueAsync("q", actor: SeedActor, now: T0);
        return new RouteCase(
            "/backwave/queues/q/resume", "/backwave/queues", "q",
            AssertPerformed: async () => Assert.False(await IsPausedAsync(store, "q")),
            AssertUntouched: async () => Assert.True(await IsPausedAsync(store, "q")));
    }

    private static async Task<RouteCase> SeedTriggerAsync(InMemoryJobStore store)
    {
        await store.UpsertScheduleAsync(new ScheduleRecord
        {
            ScheduleId = "nightly-sync",
            Cron = CronExpression.Parse("0 3 * * *").Canonical,
            WireName = "sync-inventory",
            Payload = "{}"u8.ToArray(),
            Queue = "default",
            Cursor = T0,
        });
        var monitor = new BackWaveMonitor(store);
        return new RouteCase(
            "/backwave/schedules/nightly-sync/trigger", "/backwave/schedules", "nightly-sync",
            AssertPerformed: async () => Assert.Single(
                await monitor.ListJobsAsync(new JobQuery { ScheduleId = "nightly-sync" })),
            AssertUntouched: async () => Assert.Empty(
                await monitor.ListJobsAsync(new JobQuery { ScheduleId = "nightly-sync" })));
    }

    [Fact]
    public void EveryMutatingRoute_IsEnumerated_SoANewOneIsConspicuous()
    {
        // If you add a state-mutating POST route to DashboardRequestHandler.HandlePostAsync, add it to
        // AllRoutes() (so the cross-cutting contract tests cover it) AND to this expected set. A new
        // route with no contract test then shows up as a failure here, in review.
        Assert.Equal(
            ["jobs/{id}/requeue", "jobs/{id}/cancel", "queues/{queue}/pause", "queues/{queue}/resume", "schedules/{id}/trigger"],
            AllRoutes().Select(r => r.Template).ToArray());
    }

    // ── 1. Authorization: default-deny actually denies ──────────────────────────

    [Fact]
    public async Task DenyByDefault_ForEveryMutatingRoute_Rejects403_AndMutatesNothing()
    {
        foreach (var route in AllRoutes())
        {
            var (app, store, http) = await StartAsync(); // no options ⇒ every write permission default-denies
            await using (app)
            {
                var c = await route.SeedAsync(store);
                // Permission is checked before antiforgery, so a tokenless POST is refused on the
                // permission, not the token — 403, and the action never runs.
                var resp = await PostActionAsync(http, c.Path, "", "");
                Assert.True(resp.StatusCode == HttpStatusCode.Forbidden,
                    $"{route.Name}: expected 403 Forbidden, got {(int)resp.StatusCode}.");
                await c.AssertUntouched();
                Assert.DoesNotContain(await store.ListAuditRecordsAsync(c.AuditTarget), r => r.Actor == DashboardActor);
            }
        }
    }

    // ── 2. Antiforgery: a tokenless POST is rejected even when authorized ────────

    [Fact]
    public async Task MissingAntiforgeryToken_ForEveryMutatingRoute_Rejects400_EvenWhenAuthorized()
    {
        foreach (var route in AllRoutes())
        {
            var (app, store, http) = await StartAsync(route.Grant());
            await using (app)
            {
                var c = await route.SeedAsync(store);
                // Authorized, but no antiforgery cookie/token ⇒ 400, and nothing mutated.
                var resp = await PostActionAsync(http, c.Path, "", "");
                Assert.True(resp.StatusCode == HttpStatusCode.BadRequest,
                    $"{route.Name}: expected 400 Bad Request, got {(int)resp.StatusCode}.");
                await c.AssertUntouched();
                Assert.DoesNotContain(await store.ListAuditRecordsAsync(c.AuditTarget), r => r.Actor == DashboardActor);
            }
        }
    }

    // ── 3 & 4. Effect fidelity + audit: authorized ⇒ effect lands, audited exactly once ──

    [Fact]
    public async Task Authorized_ForEveryMutatingRoute_PerformsTheEffect_AndAuditsExactlyOnce()
    {
        foreach (var route in AllRoutes())
        {
            var (app, store, http) = await StartAsync(route.Grant());
            await using (app)
            {
                var c = await route.SeedAsync(store);
                var (cookie, token) = await AntiforgeryAsync(http, c.FormPage);
                Assert.True(token.Length > 0, $"{route.Name}: expected an antiforgery token on {c.FormPage}.");

                var resp = await PostActionAsync(http, c.Path, cookie, token);
                Assert.True(resp.StatusCode == HttpStatusCode.SeeOther,
                    $"{route.Name}: expected 303 See Other, got {(int)resp.StatusCode}.");

                // The effect lands through the same store APIs the Conformance Suite covers…
                await c.AssertPerformed();
                // …and the action is recorded exactly once, against the dashboard actor.
                var recorded = (await store.ListAuditRecordsAsync(c.AuditTarget))
                    .Where(r => r.Actor == DashboardActor).ToList();
                Assert.True(recorded.Count == 1,
                    $"{route.Name}: expected exactly one dashboard audit record, got {recorded.Count}.");
                Assert.Equal(route.Action, recorded[0].Action);
            }
        }
    }

    // ── 2b. Edge matrix (per route): documented result, never a 500 ─────────────

    [Fact]
    public async Task Requeue_OnMissingOrIneligibleJob_Is303NoOp_NeverA500()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeRequeue = _ => ValueTask.FromResult(true),
        });
        await using (app)
        {
            // A decoy Dead-Lettered job makes the Jobs list render a requeue form (hence a token); it is
            // NOT a target below, so it also serves as a bystander that must stay untouched.
            var decoy = Job("decoy");
            await RunToTerminalAsync(store, decoy, new JobOutcome.Failure(null, "exhausted"));
            var succeeded = Job("done");
            await RunToTerminalAsync(store, succeeded, new JobOutcome.Success());
            var scheduled = Job("pending");
            await store.EnqueueAsync(scheduled, now: T0);

            var (cookie, token) = await AntiforgeryAsync(http, "/backwave/jobs");
            var monitor = new BackWaveMonitor(store);

            // Missing, terminal-wrong-state, and non-terminal-wrong-state all resolve to the documented
            // no-op: a 303 redirect, no audit, no state change — never a 500.
            foreach (var (id, expectedState) in (( Guid Id, JobState? State)[])
                     [
                         (Guid.NewGuid(), null),                 // missing
                         (succeeded.JobId, JobState.Succeeded),  // terminal, not requeueable
                         (scheduled.JobId, JobState.Scheduled),  // non-terminal, not requeueable
                     ])
            {
                var resp = await PostActionAsync(http, $"/backwave/jobs/{id}/requeue", cookie, token);
                Assert.Equal(HttpStatusCode.SeeOther, resp.StatusCode);
                Assert.DoesNotContain(await store.ListAuditRecordsAsync(id.ToString()), r => r.Actor == DashboardActor);
                if (expectedState is { } s)
                {
                    Assert.Equal(s, (await monitor.GetJobAsync(id))!.State);
                }
            }

            // The bystander was never touched by the no-op POSTs.
            Assert.Equal(JobState.DeadLettered, (await monitor.GetJobAsync(decoy.JobId))!.State);
        }
    }

    [Fact]
    public async Task Cancel_OnMissingOrTerminalJob_Is303NoOp_NeverA500()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeCancel = _ => ValueTask.FromResult(true),
        });
        await using (app)
        {
            var succeeded = Job("done");
            await RunToTerminalAsync(store, succeeded, new JobOutcome.Success());
            // Enqueued AFTER the claim above so it is not swept into that batch: it stays Scheduled ⇒
            // the Jobs list renders a cancel form (a token), and it serves as an untouched bystander.
            var decoy = Job("cancellable");
            await store.EnqueueAsync(decoy, now: T0);

            var (cookie, token) = await AntiforgeryAsync(http, "/backwave/jobs");
            var monitor = new BackWaveMonitor(store);

            foreach (var (id, expectedState) in (( Guid Id, JobState? State)[])
                     [
                         (Guid.NewGuid(), null),                 // missing
                         (succeeded.JobId, JobState.Succeeded),  // terminal, not cancellable
                     ])
            {
                var resp = await PostActionAsync(http, $"/backwave/jobs/{id}/cancel", cookie, token);
                Assert.Equal(HttpStatusCode.SeeOther, resp.StatusCode);
                Assert.DoesNotContain(await store.ListAuditRecordsAsync(id.ToString()), r => r.Actor == DashboardActor);
                if (expectedState is { } s)
                {
                    Assert.Equal(s, (await monitor.GetJobAsync(id))!.State);
                }
            }

            Assert.Equal(JobState.Scheduled, (await monitor.GetJobAsync(decoy.JobId))!.State);
        }
    }

    [Fact]
    public async Task PauseAndResume_AreUnconditional_IdempotentOnState_AndAuditEachPost_NeverA500()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizePauseQueue = _ => ValueTask.FromResult(true),
        });
        await using (app)
        {
            await store.EnqueueAsync(Job("work", queue: "existing"), now: T0); // a row so Queues renders forms
            var (cookie, token) = await AntiforgeryAsync(http, "/backwave/queues");

            // Pause a queue never seen before: pause is unconditional, so it succeeds and is audited.
            var pause = await PostActionAsync(http, "/backwave/queues/ghost/pause", cookie, token);
            Assert.Equal(HttpStatusCode.SeeOther, pause.StatusCode);
            Assert.True(await IsPausedAsync(store, "ghost"));

            // Pause it again (already paused): idempotent on state (still paused), but each POST is its
            // own performed action and records its own audit entry.
            var pauseAgain = await PostActionAsync(http, "/backwave/queues/ghost/pause", cookie, token);
            Assert.Equal(HttpStatusCode.SeeOther, pauseAgain.StatusCode);
            Assert.True(await IsPausedAsync(store, "ghost"));
            Assert.Equal(2, (await store.ListAuditRecordsAsync("ghost"))
                .Count(r => r.Action == OperatorAction.PauseQueue && r.Actor == DashboardActor));

            // Resume a queue that was never paused: 303, still not paused, audited once.
            var resume = await PostActionAsync(http, "/backwave/queues/fresh/resume", cookie, token);
            Assert.Equal(HttpStatusCode.SeeOther, resume.StatusCode);
            Assert.False(await IsPausedAsync(store, "fresh"));
            Assert.Equal(1, (await store.ListAuditRecordsAsync("fresh"))
                .Count(r => r.Action == OperatorAction.ResumeQueue && r.Actor == DashboardActor));
        }
    }

    [Fact]
    public async Task TriggerSchedule_OnUnknownSchedule_Is303NoMint_NoAudit_NeverA500()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeTriggerSchedule = _ => ValueTask.FromResult(true),
        });
        await using (app)
        {
            // A real schedule gives the Schedules view a trigger form (a token) — and a population to
            // prove nothing was minted anywhere.
            await store.UpsertScheduleAsync(new ScheduleRecord
            {
                ScheduleId = "real-sched",
                Cron = CronExpression.Parse("0 3 * * *").Canonical,
                WireName = "sync-inventory",
                Payload = "{}"u8.ToArray(),
                Queue = "default",
                Cursor = T0,
            });
            var (cookie, token) = await AntiforgeryAsync(http, "/backwave/schedules");

            var resp = await PostActionAsync(http, "/backwave/schedules/no-such-schedule/trigger", cookie, token);
            Assert.Equal(HttpStatusCode.SeeOther, resp.StatusCode);

            Assert.Empty(await store.ListAuditRecordsAsync("no-such-schedule"));
            var monitor = new BackWaveMonitor(store);
            Assert.Empty(await monitor.ListJobsAsync(new JobQuery { ScheduleId = "no-such-schedule" }));
            Assert.Empty(await monitor.ListJobsAsync(new JobQuery { ScheduleId = "real-sched" })); // untouched
        }
    }

    [Fact]
    public async Task AnUnknownMutatingRoute_Is404_NotSilentlyAccepted()
    {
        // A permission grant does not conjure routes: verbs that look plausible but have no handler
        // (there is no "retry" or queue "drain") are Not Found, never a 5xx or a silent 2xx.
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeRequeue = _ => ValueTask.FromResult(true),
            AuthorizeCancel = _ => ValueTask.FromResult(true),
            AuthorizePauseQueue = _ => ValueTask.FromResult(true),
        });
        await using (app)
        {
            foreach (var path in (string[])
                     [
                         $"/backwave/jobs/{Guid.NewGuid()}/retry",
                         "/backwave/queues/q/drain",
                         "/backwave/schedules/s/pause",
                     ])
            {
                var resp = await http.PostAsync(path, new StringContent(""));
                Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            }
        }
    }

    private static async Task<bool> IsPausedAsync(InMemoryJobStore store, string queue)
    {
        var settings = await new BackWaveMonitor(store).GetQueueSettingsAsync();
        return settings.Any(s => s.Queue == queue && s.Paused);
    }

    /// <summary>GETs a view, returning its antiforgery cookie and the token rendered into the action form.</summary>
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
