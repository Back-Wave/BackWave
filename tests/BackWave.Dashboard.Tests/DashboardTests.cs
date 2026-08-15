using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BackWave.Core;
using BackWave.Dashboard;
using BackWave.Diagnostics;
using BackWave.Jobs;
using BackWave.Monitor;
using BackWave.Observers;
using BackWave.Operations;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Dashboard.Tests;

// A couple of throwaway job types so a JobRegistry can be built — the registry-backed
// Wire Name facet (Monitor.GetKnownWireNames) needs real registrations to surface options.
public sealed record SendEmail(string To);
public sealed record BuildReport(string Name);

public sealed class SendEmailHandler : IJobHandler<SendEmail>
{
    public Task HandleAsync(SendEmail job, JobContext context, CancellationToken ct) => Task.CompletedTask;
}

public sealed class BuildReportHandler : IJobHandler<BuildReport>
{
    public Task HandleAsync(BuildReport job, JobContext context, CancellationToken ct) => Task.CompletedTask;
}

[JsonSerializable(typeof(SendEmail))]
[JsonSerializable(typeof(BuildReport))]
internal sealed partial class DashboardJsonContext : JsonSerializerContext;

/// <summary>
/// HTTP-level integration tests through the mounted middleware (issue 0026): every view,
/// rendered against the In-Memory Store, all data flowing through the Monitor API.
/// </summary>
public class DashboardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    private static NewJob Job(string wireName = "send-email", string queue = "default", DateTimeOffset? dueTime = null)
        => new(Guid.NewGuid(), wireName, "{}"u8.ToArray(), queue, dueTime ?? T0);

    /// <summary>A host app with the dashboard mounted at /backwave and its own route beside it.</summary>
    private static async Task<(WebApplication App, InMemoryJobStore Store, HttpClient Http)> StartAsync(
        BackWaveDashboardOptions? options = null, JobRegistry? registry = null,
        JobHistoryPolicy historyPolicy = JobHistoryPolicy.TransitionsAndFailureDetail,
        IReadOnlyList<ObserverRegistration>? observers = null,
        bool metrics = false)
    {
        // The store records under the same policy the Monitor reports, so the dashboard's read of
        // the policy matches the rows actually present (issue 0060).
        var store = new InMemoryJobStore(historyPolicy: historyPolicy);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        // The Monitor carries the registry so its Wire Name facet has options to surface; it reads the
        // Job History Policy directly from the store, so the dashboard can render the explicit
        // history-disabled state without a second, drift-prone configuration.
        builder.Services.AddSingleton(new BackWaveMonitor(store, registry));
        builder.Services.AddSingleton(new BackWaveOperator(store));
        builder.Services.AddAntiforgery(); // Operator Actions are antiforgery-protected POSTs
        // The Observer-delivery surface sources its registration set from DI (issue 0102), the same
        // canonical IReadOnlyList<ObserverRegistration> AddObservers registers; the dashboard reads it
        // per request. A test that registers none leaves the [] fallback to render the empty surface.
        if (observers is not null)
        {
            builder.Services.AddSingleton(observers);
        }
        // Opt into the live-metrics panel: registers the MeterListener-backed collector as a singleton
        // + hosted service (issues 0159/0160). Left off, the panel renders its graceful empty state.
        if (metrics)
        {
            builder.Services.AddBackWaveDashboardMetrics();
        }

        var app = builder.Build();
        app.UseBackWaveDashboard("/backwave", options);
        app.MapGet("/ping", () => "pong");
        await app.StartAsync();
        return (app, store, app.GetTestClient());
    }

    /// <summary>A registry of two job types, used where the Wire Name facet must surface options.</summary>
    private static JobRegistry SampleRegistry() => new(
    [
        JobRegistration.Create<SendEmail, SendEmailHandler>("send-email", DashboardJsonContext.Default.SendEmail),
        JobRegistration.Create<BuildReport, BuildReportHandler>("build-report", DashboardJsonContext.Default.BuildReport),
    ]);

    private static async Task<JobRecord> RunToTerminalAsync(InMemoryJobStore store, NewJob job, JobOutcome outcome)
    {
        await store.EnqueueAsync(job, now: T0);
        var claimed = (await store.ClaimAsync(new ClaimRequest("w1", [job.Queue], 32, Lease, T0)))
            .Single(j => j.JobId == job.JobId);
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, outcome, T0);
        return claimed;
    }

    [Fact]
    public async Task MountsAsMiddleware_RendersAgainstTheInMemoryStore_AndLeavesTheHostAlone()
    {
        var (app, _, http) = await StartAsync();
        await using (app)
        {
            var overview = await http.GetAsync("/backwave/");
            Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
            var html = await overview.Content.ReadAsStringAsync();
            Assert.Contains("BackWave", html);
            // The brand carries the inline logo mark with an accessible label.
            Assert.Contains("role=\"img\" aria-label=\"BackWave\"", html);

            // The host's own routes are untouched beside the mounted dashboard.
            Assert.Equal("pong", await http.GetStringAsync("/ping"));
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/backwave/no-such-view")).StatusCode);
        }
    }

    [Fact]
    public async Task Overview_ShowsQueueDepths_Throughput_AndFailureCounts()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            await store.EnqueueAsync(Job(queue: "emails"), now: T0);
            await store.EnqueueAsync(Job(queue: "emails"), now: T0);
            await store.EnqueueAsync(Job(queue: "reports", wireName: "build-report"), now: T0);
            await store.ClaimAsync(new ClaimRequest("w1", ["emails"], 1, Lease, T0));
            await RunToTerminalAsync(store, Job(queue: "emails"), new JobOutcome.Failure(null, "boom"));

            var html = await http.GetStringAsync("/backwave/");
            Assert.Contains("Queue depths", html);
            Assert.Contains("emails", html);
            Assert.Contains("reports", html);
            Assert.Contains("Throughput", html);
            // No metrics collector registered here, so the live panel renders its honest empty state
            // pointing at the opt-in registration — never a fabricated rate series.
            Assert.Contains("AddBackWaveDashboardMetrics", html);
            Assert.Contains("Dead-Lettered", html);
            Assert.Contains("Quarantined", html);
        }
    }

    [Fact]
    public async Task Overview_RendersThroughTheDesignSystemSpine_AsStaticSsr()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            await store.EnqueueAsync(Job(queue: "emails"), now: T0);

            var response = await http.GetAsync("/backwave/");
            var html = await response.Content.ReadAsStringAsync();

            // A complete document rendered via the Razor component pipeline (the shell + Overview).
            Assert.StartsWith("<!DOCTYPE html>", html);
            Assert.Contains("class=\"shell\"", html);                 // shared layout/shell
            Assert.Contains("bw-sidenav__item--active", html);        // Overview is the active nav item
            Assert.Contains("data-screen-label=\"Overview\"", html);  // the Overview screen component

            // The token-based design system is inlined in <head> — zero static assets to host.
            Assert.Contains("<style>", html);
            Assert.Contains("--wave-500", html);
            Assert.Contains("bw-stat__value", html);

            // Live views ride one SSE connection: the EventSource client is the dashboard's
            // only script — still no Blazor circuit/WASM.
            Assert.Contains("new EventSource", html);
            Assert.DoesNotContain("_framework/blazor", html);
        }
    }

    [Fact]
    public async Task Overview_MetricsPanel_RendersGracefulEmptyState_WhenNoCollectorRegistered()
    {
        // The static-SSR / no-AddBackWaveDashboardMetrics path: the live panel must never crash or
        // fabricate a series — it renders an honest note pointing at the opt-in registration.
        var (app, _, http) = await StartAsync();
        await using (app)
        {
            var html = await http.GetStringAsync("/backwave/");
            Assert.Contains("Throughput", html);
            Assert.Contains("Live throughput is off", html);
            Assert.Contains("AddBackWaveDashboardMetrics", html);
            // No sparklines and no endpoint panels without a collector.
            Assert.DoesNotContain("class=\"bw-spark\"", html);
            Assert.DoesNotContain("Top endpoints", html);
            Assert.DoesNotContain("Faulting endpoints", html);
        }
    }

    [Fact]
    public async Task Overview_MetricsPanel_RendersSparklinesAndEndpoints_FromSeededCollector()
    {
        // With the collector registered, feed the counters it already listens for through a meter
        // named "BackWave" — the same name AddBackWave's Meter uses — and the panel renders live
        // sparklines plus the Top/Faulting endpoint rankings, keyed by wire_name.
        var (app, _, http) = await StartAsync(metrics: true);
        await using (app)
        {
            using var meter = new Meter(BackWaveDiagnostics.SourceName);
            var processed = meter.CreateCounter<long>("messaging.client.consumed.messages");
            var failed = meter.CreateCounter<long>("backwave.jobs.failed");
            var attempts = meter.CreateCounter<long>("backwave.job.attempts");

            static KeyValuePair<string, object?> Wire(string name) => new("messaging.destination.template", name);

            // send-email is the busy endpoint; build-report is the faulting one.
            processed.Add(20, Wire("send-email"));
            attempts.Add(20, Wire("send-email"));
            processed.Add(5, Wire("build-report"));
            attempts.Add(8, Wire("build-report"));
            failed.Add(3, Wire("build-report"));

            var html = await http.GetStringAsync("/backwave/");

            // The empty state is gone; the live surface is present.
            Assert.DoesNotContain("Live throughput is off", html);
            Assert.Contains("class=\"bw-spark\"", html);          // server-rendered SVG sparklines
            Assert.Contains("Top endpoints", html);
            Assert.Contains("Faulting endpoints", html);
            // Both endpoints surface by wire_name — busiest in Top, faulting in Faulting.
            Assert.Contains("send-email", html);
            Assert.Contains("build-report", html);
        }
    }

    [Fact]
    public async Task Overview_MetricsPanel_RendersApproxLatencyPercentiles_FromSeededDurationHistogram()
    {
        // The percentile layer (issue 0162): feed the messaging.process.duration histogram the collector
        // now listens for, and the endpoint panels surface interpolated p95/p99, labelled approximate.
        var (app, _, http) = await StartAsync(metrics: true);
        await using (app)
        {
            using var meter = new Meter(BackWaveDiagnostics.SourceName);
            var processed = meter.CreateCounter<long>("messaging.client.consumed.messages");
            var attempts = meter.CreateCounter<long>("backwave.job.attempts");
            var duration = meter.CreateHistogram<double>("messaging.process.duration");

            var wire = new KeyValuePair<string, object?>("messaging.destination.template", "send-email");

            // The instrument records SECONDS: 95 executions at 40ms (0.040s, the ≤0.05s bucket) and 5 at
            // 70ms (0.070s, the ≤0.075s bucket). Interpolating from those bucket counts: p95 (rank 95)
            // sits at the top of the 0.025–0.05s bucket → 50ms; p99 (rank 99) sits 80% into the
            // 0.05–0.075s bucket → 70ms. The collector scales the seconds result to ms for display.
            for (var i = 0; i < 100; i++)
            {
                processed.Add(1, wire);
                attempts.Add(1, wire);
                duration.Record(i < 95 ? 0.040 : 0.070, wire);
            }

            var html = await http.GetStringAsync("/backwave/");

            // The percentile columns render with the approximate label and the interpolated millisecond
            // values for the endpoint — never a dash (which would mean no latency was recorded).
            Assert.Contains("approx", html);
            Assert.Contains("50 ms", html); // p95
            Assert.Contains("70 ms", html); // p99
        }
    }

    [Fact]
    public async Task LiveView_StreamsTheRenderedFragmentOverSse_WhenAskedWithLiveFlag()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            await store.EnqueueAsync(Job(queue: "emails"), now: T0);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await http.GetAsync(
                "/backwave/?live=1", HttpCompletionOption.ResponseHeadersRead, cts.Token);

            // Server-Sent Events, not an HTML page — the browser opens ONE of these per page.
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);

            // The loop pushes its first fragment immediately; read until it has fully landed
            // ("Queue depths" sits near the end of the Overview fragment).
            var received = "";
            while (!received.Contains("Queue depths", StringComparison.Ordinal))
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null) break;
                received += line + "\n";
            }

            Assert.Contains("event: update", received);                       // a framed SSE event
            Assert.Contains("data-screen-label=\"Overview\"", received);      // the Overview fragment
            Assert.Contains("Queue depths", received);                        // its live content
            Assert.DoesNotContain("<!DOCTYPE html>", received);               // a fragment, not a document
        }
    }

    [Fact]
    public async Task LiveView_ClosesTheSseStreamOnNavigation_SoItNeverStrandsAConnection()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            await store.EnqueueAsync(Job(queue: "emails"), now: T0);

            var html = await http.GetStringAsync("/backwave/");

            // The SSE client holds an HTTP/1.1 socket for the life of the page. The dashboard
            // navigates between sections as full-page loads, so the client must release that socket
            // on the way out — otherwise a stranded stream lingers in the ~6-per-origin pool and the
            // next section's document GET stalls behind it (then cancels on the next click). It hangs
            // its close on pagehide, and reopens from the back/forward cache via pageshow.
            Assert.Contains("new EventSource", html);
            Assert.Contains(".close()", html);
            Assert.Contains("'pagehide'", html);
            Assert.Contains("'pageshow'", html);
        }
    }

    [Fact]
    public async Task PortedViews_RenderThroughTheDesignSystemSpine_AsStaticSsr()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            var job = Job(wireName: "charge-card");
            await store.EnqueueAsync(job, now: T0);
            await store.UpsertScheduleAsync(new ScheduleRecord
            {
                ScheduleId = "nightly-sync",
                Cron = CronExpression.Parse("0 3 * * *").Canonical,
                WireName = "sync-inventory",
                Payload = "{}"u8.ToArray(),
                Queue = "default",
                Cursor = T0,
            });

            foreach (var (path, screen, active, live) in (( string Path, string Screen, string Active, bool Live)[])
                     [
                         ("/backwave/jobs", "Jobs", "jobs", false),
                         ($"/backwave/jobs/{job.JobId}", "Job", "jobs", false),
                         ("/backwave/failures", "Failures", "failures", true),
                         ("/backwave/schedules", "Recurring Schedules", "schedules", true),
                         ("/backwave/schedules/nightly-sync", "Schedule", "schedules", false),
                     ])
            {
                var html = await http.GetStringAsync(path);
                Assert.StartsWith("<!DOCTYPE html>", html);
                Assert.Contains("class=\"shell\"", html);                     // shared shell
                Assert.Contains($"data-screen-label=\"{screen}\"", html);     // the screen component
                Assert.Contains("--wave-500", html);                          // inlined design tokens
                if (live)
                {
                    Assert.Contains("new EventSource", html);                 // live views ship the SSE client
                }
                else
                {
                    // Static views may carry small inlined progressive-enhancement scripts (theme
                    // toggle, payload copy) but never the SSE live client — the runtime that holds
                    // an open connection. The no-Blazor-circuit assertion below covers the rest.
                    Assert.DoesNotContain("new EventSource", html);           // static views never open the SSE live client
                }
                Assert.DoesNotContain("_framework/blazor", html);             // never a Blazor circuit/WASM
            }
        }
    }

    [Fact]
    public async Task JobDetail_RendersTheTransitionLog_WithGlossaryCopy()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            // A full lifecycle: enqueue → claim → fail/retry → claim → succeed. The timeline
            // surfaces every state change, through the Monitor API only.
            var job = Job(wireName: "charge-card");
            await store.EnqueueAsync(job, now: T0);
            var first = (await store.ClaimAsync(new ClaimRequest("w1", [job.Queue], 32, Lease, T0)))
                .Single(j => j.JobId == job.JobId);
            var retryAt = T0.AddMinutes(5);
            await store.ReportOutcomeAsync(first.JobId, "w1", first.Attempt, new JobOutcome.Failure(retryAt, "transient"), T0);
            var second = (await store.ClaimAsync(new ClaimRequest("w1", [job.Queue], 32, Lease, retryAt)))
                .Single(j => j.JobId == job.JobId);
            await store.ReportOutcomeAsync(second.JobId, "w1", second.Attempt, new JobOutcome.Success(), retryAt);

            var html = await http.GetStringAsync($"/backwave/jobs/{job.JobId}");

            // The card.
            Assert.Contains("Transition Log", html);
            // The recorded resulting states, by their glossary spellings.
            Assert.Contains("Scheduled", html);
            Assert.Contains("Leased", html);
            Assert.Contains("Succeeded", html);
            // The timeline is recorded under Virtual Time, so the deterministic instants render
            // (the dashboard's one time format: UTC, sortable, second precision).
            Assert.Contains(retryAt.ToUniversalTime().ToString("u"), html);
        }
    }

    [Fact]
    public async Task JobDetail_WhenHistoryDisabled_ShowsExplicitDisabledState_NotABlankTimeline()
    {
        // Job History Policy Off (issue 0060): the Monitor reports recording disabled, so the card
        // shows an explicit "history disabled" state — NOT the on-but-empty "No transitions" copy,
        // and NOT a blank timeline that would read as broken.
        var (app, store, http) = await StartAsync(historyPolicy: JobHistoryPolicy.Off);
        await using (app)
        {
            // A full lifecycle runs, but Off records nothing — the timeline is empty by design.
            var job = Job(wireName: "charge-card");
            await RunToTerminalAsync(store, job, new JobOutcome.Success());

            var html = await http.GetStringAsync($"/backwave/jobs/{job.JobId}");

            Assert.Contains("Transition Log", html);
            // The explicit disabled copy and aside.
            Assert.Contains("History recording is", html);
            Assert.Contains("disabled", html);
            Assert.Contains("recording disabled", html);
            // It is NOT the on-but-empty empty state.
            Assert.DoesNotContain("No transitions recorded yet.", html);
        }
    }

    [Fact]
    public async Task JobDetail_WithDefaultPolicy_RendersTheTimeline_NotTheDisabledState()
    {
        // The default (full) policy: the timeline renders as before, never the disabled copy.
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            var job = Job(wireName: "charge-card");
            await RunToTerminalAsync(store, job, new JobOutcome.Success());

            var html = await http.GetStringAsync($"/backwave/jobs/{job.JobId}");

            Assert.Contains("Transition Log", html);
            Assert.Contains("Succeeded", html);
            Assert.DoesNotContain("History recording is", html);
        }
    }

    [Fact]
    public async Task Failures_ShowDeadLetteredAndQuarantined_Separately()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            await RunToTerminalAsync(store, Job(wireName: "kept-failing"), new JobOutcome.Failure(null, "exhausted"));
            await RunToTerminalAsync(store, Job(wireName: "lost-handler"), new JobOutcome.Unroutable("no handler for Wire Name"));

            // Both categories are always tabbed (each tab labelled and counted), but only the active
            // tab's table renders. The default tab is Dead-Lettered: its job shows, Quarantined's does not.
            var html = await http.GetStringAsync("/backwave/failures");
            Assert.Contains("Dead-Lettered", html);
            Assert.Contains("Quarantined", html);
            Assert.Contains("kept-failing", html);
            Assert.DoesNotContain("lost-handler", html);

            // The Quarantined tab (?tab=quarantine) surfaces the routing failure, kept apart from the
            // Dead-Lettered list so a long backlog of one never buries the other.
            var quarantineHtml = await http.GetStringAsync("/backwave/failures?tab=quarantine");
            Assert.Contains("lost-handler", quarantineHtml);
            Assert.DoesNotContain("kept-failing", quarantineHtml);
        }
    }

    [Fact]
    public async Task Failures_RenderTags_DeepLinkingToTheJobsListFilteredByStateAndTag()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            await RunToTerminalAsync(
                store,
                TaggedJob("kept-failing", JobTags.Empty.WithLabel("urgent").WithTag("tenant", "acme")),
                new JobOutcome.Failure(null, "exhausted"));

            var html = await http.GetStringAsync("/backwave/failures");
            // The Tags column renders the pills for the Dead-Lettered job.
            Assert.Contains("class=\"bw-tags\"", html);
            Assert.Contains(">urgent<", html);
            Assert.Contains(">tenant:acme<", html);
            // Each pill deep-links to the Jobs list filtered to this terminal state AND the clicked Tag
            // (the ampersands are HTML-encoded in the rendered attribute).
            Assert.Contains("/backwave/jobs?state=DeadLettered&amp;tl=urgent", html);
            Assert.Contains("/backwave/jobs?state=DeadLettered&amp;tk=tenant&amp;tv=acme", html);
        }
    }

    [Fact]
    public async Task HistoricalTables_RenderNewestFirst()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            // Distinct Sequences in enqueue order: each later job carries a higher Sequence.
            var oldest = Job(wireName: "oldest");
            var middle = Job(wireName: "middle");
            var newest = Job(wireName: "newest");
            await store.EnqueueAsync(oldest, now: T0);
            await store.EnqueueAsync(middle, now: T0);
            await store.EnqueueAsync(newest, now: T0);

            // The Jobs list opts into newest-first: the most-recently-enqueued id appears first.
            var jobsHtml = await http.GetStringAsync("/backwave/jobs");
            AssertAppearInOrder(jobsHtml, newest.JobId, middle.JobId, oldest.JobId);

            // Failures table, same opt-in: Dead-Lettered jobs render newest-first.
            var deadOld = await RunToTerminalAsync(store, Job(wireName: "dead-old"), new JobOutcome.Failure(null, "exhausted"));
            var deadNew = await RunToTerminalAsync(store, Job(wireName: "dead-new"), new JobOutcome.Failure(null, "exhausted"));
            var failuresHtml = await http.GetStringAsync("/backwave/failures");
            AssertAppearInOrder(failuresHtml, deadNew.JobId, deadOld.JobId);
        }
    }

    [Fact]
    public async Task Overview_SurfacesHealthSignals_AndNewestFirstPreviews()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            // A paused Queue: silent backlog, the first thing to check when nothing drains.
            await store.PauseQueueAsync("paused-q", actor: "op", now: T0);

            // A Queue at its Concurrency Limit: cap of 1 with exactly one live Lease.
            await store.SetConcurrencyLimitAsync("capped-q", 1, actor: "op", now: T0);
            await store.EnqueueAsync(Job(queue: "capped-q"), now: T0);
            await store.ClaimAsync(new ClaimRequest("w1", ["capped-q"], 1, Lease, T0));

            // An errored Recurring Schedule: an unresolvable IANA zone poisons the row.
            await store.UpsertScheduleAsync(new ScheduleRecord
            {
                ScheduleId = "broken-sched",
                Cron = CronExpression.Parse("0 3 * * *").Canonical,
                WireName = "sync-inventory",
                Payload = "{}"u8.ToArray(),
                Queue = "default",
                Cursor = T0,
                TimeZoneId = "Mars/Olympus", // no such zone on this host
            });

            // A complete state rollup: Succeeded and Cancelled now ride the stat row too.
            await RunToTerminalAsync(store, Job(wireName: "done"), new JobOutcome.Success());

            // Needs-attention rows, newest-first; the second is the more recent failure.
            var deadOld = await RunToTerminalAsync(store, Job(wireName: "dead-old"), new JobOutcome.Failure(null, "exhausted"));
            var quarantinedNew = await RunToTerminalAsync(store, Job(wireName: "lost-handler"), new JobOutcome.Unroutable("no handler"));

            // Executing-now: an enqueued job left Leased (claimed, never reported).
            var leased = Job(wireName: "in-flight");
            await store.EnqueueAsync(leased, now: T0);
            await store.ClaimAsync(new ClaimRequest("w2", [leased.Queue], 1, Lease, T0));

            var html = await http.GetStringAsync("/backwave/");

            // Health signals, glossary-named.
            Assert.Contains("paused queues", html);
            Assert.Contains("paused-q", html);
            Assert.Contains("queues at concurrency limit", html);
            Assert.Contains("capped-q", html);
            Assert.Contains("recurring schedules", html);
            Assert.Contains("1 errored", html);

            // Promoted state cards complete the rollup.
            Assert.Contains("succeeded", html);
            Assert.Contains("cancelled", html);

            // Both previews, deep-linking to their full views.
            Assert.Contains("Needs attention", html);
            Assert.Contains("Executing now", html);
            Assert.Contains("/backwave/failures", html);
            Assert.Contains("/backwave/executing", html);

            // Needs-attention merges Dead-Lettered + Quarantined newest-first; Executing shows the Lease.
            AssertAppearInOrder(html, quarantinedNew.JobId, deadOld.JobId);
            Assert.Contains($"/jobs/{leased.JobId}", html);
        }
    }

    [Fact]
    public async Task Overview_EmptyStore_ZeroesSignals_AndRendersEmptyPreviews()
    {
        var (app, _, http) = await StartAsync();
        await using (app)
        {
            var html = await http.GetStringAsync("/backwave/");

            // Zeroed health signals render without error on a fresh store (em-dash/dot are
            // HTML-encoded by Razor, so assert on the stable text either side).
            Assert.Contains("all Queues claiming", html);
            Assert.Contains("none capped out", html);
            Assert.Contains("0 errored", html);
            Assert.Contains("0 skipped ticks", html);

            // Explicit empty states for both previews.
            Assert.Contains("Failures show up here as they happen", html);
            Assert.Contains("Active workers appear here", html);
        }
    }

    /// <summary>Asserts each id's job link appears, in the given order, in the rendered HTML.</summary>
    private static void AssertAppearInOrder(string html, params Guid[] jobIds)
    {
        var positions = jobIds
            .Select(id => html.IndexOf($"/jobs/{id}", StringComparison.Ordinal))
            .ToList();
        Assert.DoesNotContain(-1, positions); // every job rendered
        for (var i = 1; i < positions.Count; i++)
        {
            Assert.True(positions[i - 1] < positions[i],
                $"Job {jobIds[i - 1]} should appear before {jobIds[i]} (newest-first).");
        }
    }

    [Fact]
    public async Task JobSearch_Filters_ByStateAndQueue()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            // Terminal first: its claim would otherwise sweep up everything due in "emails".
            await RunToTerminalAsync(store, Job(wireName: "already-done", queue: "emails"), new JobOutcome.Success());
            await store.EnqueueAsync(Job(wireName: "wanted", queue: "emails"), now: T0);
            await store.EnqueueAsync(Job(wireName: "other-queue", queue: "reports"), now: T0);

            var html = await http.GetStringAsync("/backwave/jobs?state=Scheduled&queue=emails");
            Assert.Contains("wanted", html);
            Assert.DoesNotContain("other-queue", html);
            Assert.DoesNotContain("already-done", html);

            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/backwave/jobs?state=NoSuchState")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/backwave/jobs?after=abc")).StatusCode);
        }
    }

    [Fact]
    public async Task JobFilters_RenderAsStyledDropdowns_SourcedThroughTheMonitor()
    {
        var (app, store, http) = await StartAsync(registry: SampleRegistry());
        await using (app)
        {
            // Queue options come from the depth counts: only Queues that actually have jobs.
            await store.EnqueueAsync(Job(wireName: "send-email", queue: "emails"), now: T0);
            await store.EnqueueAsync(Job(wireName: "build-report", queue: "reports"), now: T0);

            var html = await http.GetStringAsync("/backwave/jobs");

            // Each filter is the .bw-select WRAPPER (div) over a bare <select>, not the class on
            // the <select> itself — so the chevron and styling actually render.
            Assert.DoesNotContain("<select class=\"bw-select\"", html);
            Assert.Matches(new Regex("""<div class="bw-select">\s*<select id="state" name="state">"""), html);
            Assert.Matches(new Regex("""<div class="bw-select">\s*<select id="queue" name="queue">"""), html);
            Assert.Matches(new Regex("""<div class="bw-select">\s*<select id="wire" name="wire">"""), html);

            // Queue dropdown: options are the distinct Queues with jobs (from Monitor depths).
            Assert.Matches(new Regex("""<select id="queue"[^>]*>.*?<option value="emails">emails</option>""", RegexOptions.Singleline), html);
            Assert.Matches(new Regex("""<select id="queue"[^>]*>.*?<option value="reports">reports</option>""", RegexOptions.Singleline), html);

            // Wire Name dropdown: options are the known Wire Names (the registry-backed facet),
            // ordered by Wire Name, even ones with no jobs of that exact name present.
            Assert.Matches(new Regex("""<select id="wire"[^>]*>.*?<option value="build-report">build-report</option>.*?<option value="send-email">send-email</option>""", RegexOptions.Singleline), html);

            // The free-text Schedule input is gone from the form.
            Assert.DoesNotContain("name=\"schedule\"", html);
        }
    }

    [Fact]
    public async Task ScheduleDeepLink_StillFilters_ThoughTheScheduleInputIsGone()
    {
        var (app, store, http) = await StartAsync(registry: SampleRegistry());
        await using (app)
        {
            await store.UpsertScheduleAsync(new ScheduleRecord
            {
                ScheduleId = "nightly-sync",
                Cron = CronExpression.Parse("0 3 * * *").Canonical,
                WireName = "build-report",
                Payload = "{}"u8.ToArray(),
                Queue = "reports",
                Cursor = T0,
            });
            var tick = T0.AddDays(1).AddHours(3);
            await store.MintDueAsync(
                [new MintDecision("nightly-sync", ExpectedCursor: T0, NewCursor: tick, Ticks: [tick], SkippedTicks: [])]);
            var adHoc = Job(wireName: "send-email", queue: "emails");
            await store.EnqueueAsync(adHoc, now: T0); // an unrelated, ad-hoc job

            // ?schedule=<id> still narrows the Jobs list to that schedule's minted instances.
            // Assert on JobIds in the table — "send-email" alone also appears as a Wire Name
            // dropdown option, so a bare substring check would be ambiguous.
            var filtered = await http.GetStringAsync("/backwave/jobs?schedule=nightly-sync");
            var minted = (await new BackWaveMonitor(store).ListJobsAsync(
                new JobQuery { ScheduleId = "nightly-sync" })).Single();
            Assert.Contains(minted.JobId.ToString(), filtered);   // the minted instance row
            Assert.DoesNotContain(adHoc.JobId.ToString(), filtered); // the ad-hoc job is excluded

            // …even though there is no Schedule field in the rendered form.
            Assert.DoesNotContain("name=\"schedule\"", filtered);
        }
    }

    [Fact]
    public async Task JobSearch_PagesThroughTheFullSet_ViaTheCursor()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            for (var i = 0; i < 60; i++) // one page is 50 rows
            {
                await store.EnqueueAsync(Job(wireName: $"job-{i:D2}"), now: T0);
            }

            // The Jobs list renders newest-first (issue 0063): the last-enqueued rows lead.
            var firstPage = await http.GetStringAsync("/backwave/jobs");
            Assert.Contains("job-59", firstPage);
            Assert.Contains("job-10", firstPage);
            Assert.DoesNotContain("job-09", firstPage);

            // The "Next page" link carries the §5.9 after-sequence cursor (now direction-relative).
            var next = Regex.Match(firstPage, """href="(/backwave/jobs\?after=\d+)""").Groups[1].Value;
            Assert.NotEmpty(next);
            var secondPage = await http.GetStringAsync(next);
            Assert.Contains("job-09", secondPage);
            Assert.Contains("job-00", secondPage);
            Assert.DoesNotContain("job-10", secondPage);
            Assert.DoesNotContain("Next page", secondPage); // ten rows left: no third page
        }
    }

    [Fact]
    public async Task JobIdSearch_RedirectsToTheDetailPage_AndTakesPrecedenceOverOtherFilters()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            var job = Job(wireName: "charge-card");
            await store.EnqueueAsync(job, now: T0);

            // The Job ID field renders in the filter form as a styled text input.
            var form = await http.GetStringAsync("/backwave/jobs");
            Assert.Contains("""<input id="id" name="id" class="bw-input" """, form);

            // A valid full Guid → 302 straight to that job's detail page (TestServer doesn't auto-follow).
            var hit = await http.GetAsync($"/backwave/jobs?id={job.JobId}");
            Assert.Equal(HttpStatusCode.Found, hit.StatusCode);
            Assert.Equal($"/backwave/jobs/{job.JobId}", hit.Headers.Location?.ToString());

            // The id wins even when State/Queue/Wire filters are submitted alongside it.
            var withFilters = await http.GetAsync($"/backwave/jobs?id={job.JobId}&state=Scheduled&queue=default&wire=send-email");
            Assert.Equal(HttpStatusCode.Found, withFilters.StatusCode);
            Assert.Equal($"/backwave/jobs/{job.JobId}", withFilters.Headers.Location?.ToString());

            // An unknown-but-valid id redirects too; following it lands on the existing Not Found page.
            var unknownId = Guid.NewGuid();
            var unknown = await http.GetAsync($"/backwave/jobs?id={unknownId}");
            Assert.Equal(HttpStatusCode.Found, unknown.StatusCode);
            var landed = await http.GetAsync(unknown.Headers.Location!.ToString());
            Assert.Equal(HttpStatusCode.NotFound, landed.StatusCode);

            // A present-but-malformed id is treated like an unknown job reference: Not Found, no redirect.
            var malformed = await http.GetAsync("/backwave/jobs?id=not-a-guid");
            Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
        }
    }

    [Fact]
    public async Task ClearFilter_ReturnsToTheUnfilteredFirstPage_PreservingTheRowsPerPage()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            await store.EnqueueAsync(Job(), now: T0);

            // At the default size the Clear link is the bare /jobs path (no carried params).
            var atDefault = await http.GetStringAsync("/backwave/jobs?state=Scheduled&queue=default");
            Assert.Contains("""<a class="bw-btn" href="/backwave/jobs">Clear</a>""", atDefault);

            // A non-default rows-per-page is a display preference, so Clear keeps only the size.
            var atSize = await http.GetStringAsync("/backwave/jobs?state=Scheduled&size=100&after=5");
            Assert.Contains("""<a class="bw-btn" href="/backwave/jobs?size=100">Clear</a>""", atSize);
        }
    }

    [Fact]
    public async Task RowsPerPage_ChangesTheEffectivePageSize_AndStaysCorrectAtThe200Boundary()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            // Enough to fill the largest page (200) and prove no row is silently dropped.
            for (var i = 0; i < 250; i++)
            {
                await store.EnqueueAsync(Job(wireName: $"job-{i:D3}"), now: T0);
            }

            // The Rows dropdown is the styled .bw-select with the chosen size marked selected.
            var html = await http.GetStringAsync("/backwave/jobs?size=25");
            Assert.Matches(new Regex("""<div class="bw-select">\s*<select id="size" name="size">"""), html);
            Assert.Contains("""<option value="25" selected>25</option>""", html);

            // Each size shows exactly that many rows on the first page (newest first).
            foreach (var size in new[] { 25, 50, 100, 200 })
            {
                var page = await http.GetStringAsync($"/backwave/jobs?size={size}");
                var rows = Regex.Matches(page, """/jobs/[0-9a-f-]{36}""").Count;
                Assert.Equal(size, rows);
            }

            // Bad sizes fall back to the default 50.
            var fallback = await http.GetStringAsync("/backwave/jobs?size=999");
            Assert.Equal(50, Regex.Matches(fallback, """/jobs/[0-9a-f-]{36}""").Count);

            // 200 boundary: the +1 sentinel clamps away, so a full 200-row page still offers Next,
            // and that Next link carries size=200. Walk it and prove the remaining 50 rows arrive.
            var first200 = await http.GetStringAsync("/backwave/jobs?size=200");
            Assert.Contains("Next page", first200);
            // The href is HTML-encoded in markup (& → &amp;); decode it before following.
            var next = Regex.Match(first200, """href="(/backwave/jobs\?after=\d+&amp;size=200)""").Groups[1].Value;
            Assert.NotEmpty(next);
            var second200 = await http.GetStringAsync(next.Replace("&amp;", "&"));
            Assert.Equal(50, Regex.Matches(second200, """/jobs/[0-9a-f-]{36}""").Count); // the rest, none dropped
            Assert.DoesNotContain("Next page", second200);
        }
    }

    [Fact]
    public async Task JobDetail_SpeaksTheGlossary_And404sForUnknownIds()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            var job = Job(wireName: "charge-card");
            await store.EnqueueAsync(job, now: T0);
            await store.ClaimAsync(new ClaimRequest("w1", ["default"], 32, Lease, T0));

            var html = await http.GetStringAsync($"/backwave/jobs/{job.JobId}");
            Assert.Contains("charge-card", html);
            Assert.Contains("Leased", html);
            Assert.Contains("Lease", html);
            Assert.Contains("Attempt", html);

            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/backwave/jobs/{Guid.NewGuid()}")).StatusCode);
        }
    }

    [Fact]
    public async Task Schedules_ListTheSchedule_AndItsMintedInstances_Distinctly()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
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
            var tick = T0.AddDays(1).AddHours(3);
            await store.MintDueAsync(
                [new MintDecision("nightly-sync", ExpectedCursor: T0, NewCursor: tick, Ticks: [tick], SkippedTicks: [])]);

            var schedules = await http.GetStringAsync("/backwave/schedules");
            Assert.Contains("nightly-sync", schedules);
            Assert.Contains("sync-inventory", schedules);

            var instances = await http.GetStringAsync("/backwave/schedules/nightly-sync");
            Assert.Contains("Minted instances", instances);
            Assert.Contains("sync-inventory", instances);

            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/backwave/schedules/no-such-schedule")).StatusCode);
        }
    }

    [Fact]
    public async Task TheViewPermission_GatesEveryView_With403()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeView = _ => ValueTask.FromResult(false),
        });
        await using (app)
        {
            await store.EnqueueAsync(Job(), now: T0);
            foreach (var path in (string[])["/backwave/", "/backwave/jobs", "/backwave/failures", "/backwave/schedules"])
            {
                var response = await http.GetAsync(path);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Empty(await response.Content.ReadAsStringAsync()); // no dashboard bytes leak
            }

            // The host's own routes are not behind BackWave's Permission.
            Assert.Equal("pong", await http.GetStringAsync("/ping"));
        }
    }

    [Fact]
    public async Task UnsupportedMethods_Are405_AndUnknownPostRoutes_Are404()
    {
        var (app, _, http) = await StartAsync();
        await using (app)
        {
            // PUT/DELETE are never dashboard verbs.
            var put = await http.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/backwave/jobs"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);

            // POST is now a verb (Operator Actions), but only on action routes; others 404.
            var post = await http.PostAsync("/backwave/jobs", new StringContent(""));
            Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        }
    }

    [Fact]
    public async Task Requeue_WorksEndToEnd_WhenAuthorized_AsAnAuditedTransition()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeRequeue = _ => ValueTask.FromResult(true),
        });
        await using (app)
        {
            var job = Job(wireName: "kept-failing");
            await RunToTerminalAsync(store, job, new JobOutcome.Failure(null, "exhausted"));
            var monitor = new BackWaveMonitor(store);
            Assert.Equal(JobState.DeadLettered, (await monitor.GetJobAsync(job.JobId))!.State);

            // The control renders (permission passed) and carries an antiforgery token.
            var (cookie, token) = await AntiforgeryAsync(http, "/backwave/failures");
            Assert.NotEmpty(token);

            var resp = await PostActionAsync(http, $"/backwave/jobs/{job.JobId}/requeue", cookie, token);
            Assert.Equal(HttpStatusCode.SeeOther, resp.StatusCode);
            // The unified redirect (issue 0066): a requeue from any view lands on the job's detail
            // page showing the now-Scheduled state, matching Cancel — not back on Failures.
            Assert.Equal($"/backwave/jobs/{job.JobId}", resp.Headers.Location?.ToString());

            // Audited Core transition: Dead-Lettered → Scheduled, Attempt reset to 0.
            var requeued = (await monitor.GetJobAsync(job.JobId))!;
            Assert.Equal(JobState.Scheduled, requeued.State);
            Assert.Equal(0, requeued.Attempt);
            Assert.Contains(await store.ListAuditRecordsAsync(job.JobId.ToString()),
                r => r.Action == OperatorAction.Requeue && r.Actor == "dashboard");
        }
    }

    [Fact]
    public async Task Requeue_IsDenied_AndControlHidden_ForAViewOnlyIdentity()
    {
        var (app, store, http) = await StartAsync(); // all four actions default-deny
        await using (app)
        {
            var job = Job(wireName: "kept-failing");
            await RunToTerminalAsync(store, job, new JobOutcome.Failure(null, "exhausted"));

            // View-only sees the failures, but no Requeue control and no antiforgery token.
            var html = await http.GetStringAsync("/backwave/failures");
            Assert.Contains("kept-failing", html);
            Assert.DoesNotContain("/requeue", html);
            Assert.DoesNotContain("__RequestVerificationToken", html);

            // And the POST is refused outright.
            var resp = await PostActionAsync(http, $"/backwave/jobs/{job.JobId}/requeue", "", "");
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

            var monitor = new BackWaveMonitor(store);
            Assert.Equal(JobState.DeadLettered, (await monitor.GetJobAsync(job.JobId))!.State); // unchanged
        }
    }

    [Fact]
    public async Task StateMutatingPost_WithoutAntiforgeryToken_IsRejected_EvenWhenAuthorized()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeRequeue = _ => ValueTask.FromResult(true),
        });
        await using (app)
        {
            var job = Job(wireName: "kept-failing");
            await RunToTerminalAsync(store, job, new JobOutcome.Failure(null, "exhausted"));

            // Authorized, but no antiforgery cookie/token on the POST.
            var resp = await PostActionAsync(http, $"/backwave/jobs/{job.JobId}/requeue", "", "");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

            var monitor = new BackWaveMonitor(store);
            Assert.Equal(JobState.DeadLettered, (await monitor.GetJobAsync(job.JobId))!.State); // unchanged
        }
    }

    [Fact]
    public async Task Cancel_WorksEndToEnd_FromTheJobsList_WhenAuthorized()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeCancel = _ => ValueTask.FromResult(true),
        });
        await using (app)
        {
            var job = Job(wireName: "long-running");
            await store.EnqueueAsync(job, now: T0);

            var (cookie, token) = await AntiforgeryAsync(http, "/backwave/jobs");
            var resp = await PostActionAsync(http, $"/backwave/jobs/{job.JobId}/cancel", cookie, token);
            Assert.Equal(HttpStatusCode.SeeOther, resp.StatusCode);

            // A pending job cancels immediately to the Cancelled terminal state.
            var monitor = new BackWaveMonitor(store);
            Assert.Equal(JobState.Cancelled, (await monitor.GetJobAsync(job.JobId))!.State);
        }
    }

    [Fact]
    public async Task JobsTable_PicksTheEligibleActionPerRow_RequeueCancelOrNone()
    {
        // Both Operator Actions granted so the column can offer either control per row.
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeRequeue = _ => ValueTask.FromResult(true),
            AuthorizeCancel = _ => ValueTask.FromResult(true),
        });
        await using (app)
        {
            // A mix of states in one list: requeue-eligible (Dead-Lettered, Quarantined),
            // cancel-eligible (Scheduled, non-terminal), and ineligible (Succeeded, terminal).
            var deadLettered = Job(wireName: "kept-failing");
            await RunToTerminalAsync(store, deadLettered, new JobOutcome.Failure(null, "exhausted"));
            var quarantined = Job(wireName: "lost-handler");
            await RunToTerminalAsync(store, quarantined, new JobOutcome.Unroutable("no handler"));
            var succeeded = Job(wireName: "done");
            await RunToTerminalAsync(store, succeeded, new JobOutcome.Success());
            var scheduled = Job(wireName: "pending");
            await store.EnqueueAsync(scheduled, now: T0);

            var html = await http.GetStringAsync("/backwave/jobs");

            // Requeue for the two requeue-eligible states; never Cancel for them.
            Assert.Contains($"/backwave/jobs/{deadLettered.JobId}/requeue", html);
            Assert.Contains($"/backwave/jobs/{quarantined.JobId}/requeue", html);
            Assert.DoesNotContain($"/backwave/jobs/{deadLettered.JobId}/cancel", html);
            Assert.DoesNotContain($"/backwave/jobs/{quarantined.JobId}/cancel", html);
            // Cancel for the non-terminal job; never Requeue for it.
            Assert.Contains($"/backwave/jobs/{scheduled.JobId}/cancel", html);
            Assert.DoesNotContain($"/backwave/jobs/{scheduled.JobId}/requeue", html);
            // No control at all for the Succeeded terminal job.
            Assert.DoesNotContain($"/backwave/jobs/{succeeded.JobId}/requeue", html);
            Assert.DoesNotContain($"/backwave/jobs/{succeeded.JobId}/cancel", html);
        }
    }

    [Fact]
    public async Task JobsTable_EachControlAppearsOnlyWhenItsPermissionIsGranted()
    {
        // Only Cancel granted: a Dead-Lettered row shows no Requeue (the requeue Permission is denied),
        // and the cancel-ineligible terminal row shows nothing either.
        var (cancelApp, cancelStore, cancelHttp) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeCancel = _ => ValueTask.FromResult(true),
        });
        await using (cancelApp)
        {
            var deadLettered = Job(wireName: "kept-failing");
            await RunToTerminalAsync(cancelStore, deadLettered, new JobOutcome.Failure(null, "exhausted"));
            var scheduled = Job(wireName: "pending");
            await cancelStore.EnqueueAsync(scheduled, now: T0);

            var html = await cancelHttp.GetStringAsync("/backwave/jobs");
            Assert.DoesNotContain($"/backwave/jobs/{deadLettered.JobId}/requeue", html);
            Assert.DoesNotContain($"/backwave/jobs/{deadLettered.JobId}/cancel", html); // terminal: not cancellable
            Assert.Contains($"/backwave/jobs/{scheduled.JobId}/cancel", html);
        }

        // Only Requeue granted: a Scheduled row shows no Cancel (the cancel Permission is denied),
        // while a Dead-Lettered row still offers Requeue.
        var (requeueApp, requeueStore, requeueHttp) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeRequeue = _ => ValueTask.FromResult(true),
        });
        await using (requeueApp)
        {
            var deadLettered = Job(wireName: "kept-failing");
            await RunToTerminalAsync(requeueStore, deadLettered, new JobOutcome.Failure(null, "exhausted"));
            var scheduled = Job(wireName: "pending");
            await requeueStore.EnqueueAsync(scheduled, now: T0);

            var html = await requeueHttp.GetStringAsync("/backwave/jobs");
            Assert.Contains($"/backwave/jobs/{deadLettered.JobId}/requeue", html);
            Assert.DoesNotContain($"/backwave/jobs/{scheduled.JobId}/cancel", html);
            Assert.DoesNotContain($"/backwave/jobs/{scheduled.JobId}/requeue", html); // not requeue-eligible
        }
    }

    [Fact]
    public async Task Requeue_FromTheJobsList_RedirectsToJobDetail_AsAnAuditedTransition()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeRequeue = _ => ValueTask.FromResult(true),
        });
        await using (app)
        {
            var job = Job(wireName: "kept-failing");
            await RunToTerminalAsync(store, job, new JobOutcome.Failure(null, "exhausted"));

            // The Requeue control renders on the Jobs list and carries an antiforgery token.
            var (cookie, token) = await AntiforgeryAsync(http, "/backwave/jobs");
            Assert.NotEmpty(token);

            var resp = await PostActionAsync(http, $"/backwave/jobs/{job.JobId}/requeue", cookie, token);
            Assert.Equal(HttpStatusCode.SeeOther, resp.StatusCode);
            // Unified redirect: the job's detail page, showing the now-Scheduled state.
            Assert.Equal($"/backwave/jobs/{job.JobId}", resp.Headers.Location?.ToString());

            // Audited Core transition: Dead-Lettered → Scheduled, Attempt reset, acting operator recorded.
            var monitor = new BackWaveMonitor(store);
            var requeued = (await monitor.GetJobAsync(job.JobId))!;
            Assert.Equal(JobState.Scheduled, requeued.State);
            Assert.Equal(0, requeued.Attempt);
            Assert.Contains(await store.ListAuditRecordsAsync(job.JobId.ToString()),
                r => r.Action == OperatorAction.Requeue && r.Actor == "dashboard");
        }
    }

    [Fact]
    public async Task PauseQueue_StopsClaiming_AndResume_RestoresIt_WhenAuthorized()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizePauseQueue = _ => ValueTask.FromResult(true),
        });
        await using (app)
        {
            await store.EnqueueAsync(Job(wireName: "work", queue: "q"), now: T0);

            var (cookie, token) = await AntiforgeryAsync(http, "/backwave/queues");
            var paused = await PostActionAsync(http, "/backwave/queues/q/pause", cookie, token);
            Assert.Equal(HttpStatusCode.SeeOther, paused.StatusCode);

            // A paused Queue yields nothing to Claim.
            Assert.Empty(await store.ClaimAsync(new ClaimRequest("w1", ["q"], 32, Lease, T0)));

            var resumed = await PostActionAsync(http, "/backwave/queues/q/resume", cookie, token);
            Assert.Equal(HttpStatusCode.SeeOther, resumed.StatusCode);
            Assert.NotEmpty(await store.ClaimAsync(new ClaimRequest("w1", ["q"], 32, Lease, T0)));
        }
    }

    [Fact]
    public async Task TriggerSchedule_MintsOneInstance_WhenAuthorized()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeTriggerSchedule = _ => ValueTask.FromResult(true),
        });
        await using (app)
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

            var (cookie, token) = await AntiforgeryAsync(http, "/backwave/schedules");
            var resp = await PostActionAsync(http, "/backwave/schedules/nightly-sync/trigger", cookie, token);
            Assert.Equal(HttpStatusCode.SeeOther, resp.StatusCode);

            var monitor = new BackWaveMonitor(store);
            var instances = await monitor.ListJobsAsync(new JobQuery { ScheduleId = "nightly-sync" });
            Assert.Single(instances);
        }
    }

    [Fact]
    public async Task ExecutingNow_ShowsLeasedJobs_WithLeaseOwner()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            var job = Job(wireName: "charge-card", queue: "billing");
            await store.EnqueueAsync(job, now: T0);
            await store.ClaimAsync(new ClaimRequest("worker-7", ["billing"], 32, Lease, T0));

            var html = await http.GetStringAsync("/backwave/executing");
            Assert.Contains("Executing now", html);
            Assert.Contains("charge-card", html);
            Assert.Contains("worker-7", html);      // the Lease owner, surfaced from the read model (0054)
            Assert.Contains("Lease owner", html);   // the Lease columns of the executing-now table
            Assert.Contains("Lease expires", html);
        }
    }

    [Fact]
    public async Task Queues_ShowPausedDistinctly_AndInUseAgainstTheConcurrencyLimit()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            await store.EnqueueAsync(Job(wireName: "a", queue: "q"), now: T0);
            await store.EnqueueAsync(Job(wireName: "b", queue: "q"), now: T0);
            await store.SetConcurrencyLimitAsync("q", 5, actor: "op", now: T0);
            await store.ClaimAsync(new ClaimRequest("w1", ["q"], 1, Lease, T0)); // one in-use (Leased)
            await store.PauseQueueAsync("q", "op", T0);

            var html = await http.GetStringAsync("/backwave/queues");
            Assert.Contains("Queues", html);
            Assert.Contains("Paused", html);        // paused shown distinctly (0055)
            Assert.Contains("1 / 5", html);         // in-use / cap, composed from depths + the cap
        }
    }

    [Fact]
    public async Task JobDetail_ForAwaitingParent_ShowsRemainingGatingParents_NotHistory()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            var parent = Job(wireName: "parent-job");
            await store.EnqueueAsync(parent, now: T0);
            var child = Job(wireName: "child-job") with { Parents = [parent.JobId] };
            await store.EnqueueAsync(child, now: T0);

            var html = await http.GetStringAsync($"/backwave/jobs/{child.JobId}");
            Assert.Contains("Awaiting Parent", html);
            Assert.Contains("Still blocking this Dependency", html);     // the gating panel (0056)
            Assert.Contains(parent.JobId.ToString(), html);               // the still-gating parent
        }
    }

    [Fact]
    public async Task JobDetail_ShowsThePayload_OnlyWithViewSensitiveData_LabelledAsOpaqueBytes()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeViewSensitiveData = _ => ValueTask.FromResult(true),
        });
        await using (app)
        {
            var job = new NewJob(Guid.NewGuid(), "charge-card",
                """{"amount":4200,"currency":"USD"}"""u8.ToArray(), "default", T0);
            await store.EnqueueAsync(job, now: T0);

            var html = await http.GetStringAsync($"/backwave/jobs/{job.JobId}");
            // The payload bytes appear, in a payload card clearly labelled as opaque bytes. JSON
            // bytes are pretty-printed and syntax-highlighted for display (tokens carry .bw-json__*
            // classes), so the key and value render as separate highlighted spans rather than as one
            // contiguous run — display only; BackWave still never parses the payload for the job.
            Assert.Contains("Payload", html);                      // the payload card renders
            Assert.Contains("amount", html);                       // the key, highlighted
            Assert.Contains("bw-json__num\">4200", html);          // the value, in a number token
            Assert.Contains("aria-label=\"Copy payload\"", html);  // the payload card's copy button
        }
    }

    [Fact]
    public async Task JobDetail_PayloadCard_CarriesACopyButton_OverThePrettyPrintedPre()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeViewSensitiveData = _ => ValueTask.FromResult(true),
        });
        await using (app)
        {
            var job = new NewJob(Guid.NewGuid(), "charge-card",
                """{"amount":4200,"currency":"USD"}"""u8.ToArray(), "default", T0);
            await store.EnqueueAsync(job, now: T0);

            var html = await http.GetStringAsync($"/backwave/jobs/{job.JobId}");
            // The pretty-printed <pre> is wrapped in a copyable code block with a copy button, and the
            // page ships the delegated copy script. The script copies the <pre>'s textContent, which
            // for highlighted JSON is exactly the formatted payload (the .bw-json__* spans add no
            // characters) — so the clipboard gets the same pretty text the reader sees.
            Assert.Contains("bw-codeblock", html);                 // the wrapper the copy script hooks onto
            Assert.Contains("bw-fence", html);                     // framed as a code fence with a header bar
            Assert.Contains("data-bw-copy", html);                 // the button the script hooks
            Assert.Contains("bw-json", html);                      // the pretty <pre> remains the copy source
            Assert.Contains("pre.textContent", html);              // the script copies the rendered text verbatim
        }
    }

    [Fact]
    public async Task JobDetail_OmitsThePayload_WithoutViewSensitiveData_PageStill200()
    {
        var (app, store, http) = await StartAsync(); // ViewSensitiveData default-deny
        await using (app)
        {
            var job = new NewJob(Guid.NewGuid(), "charge-card",
                """{"amount":4200,"currency":"USD"}"""u8.ToArray(), "default", T0);
            await store.EnqueueAsync(job, now: T0);

            var response = await http.GetAsync($"/backwave/jobs/{job.JobId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); // absent payload is not an error
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("charge-card", html);                 // the rest of the page still renders
            Assert.DoesNotContain("amount&quot;:4200", html);     // but no payload bytes leak
            Assert.DoesNotContain("aria-label=\"Copy payload\"", html); // no payload card at all
        }
    }

    [Fact]
    public async Task JobDetail_OmitsThePayload_WhenExposureIsDisabled_EvenWithPermission()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeViewSensitiveData = _ => ValueTask.FromResult(true),
            ExposeSensitiveData = false, // host kill-switch: payloads never leave storage
        });
        await using (app)
        {
            var job = new NewJob(Guid.NewGuid(), "charge-card",
                """{"amount":4200,"currency":"USD"}"""u8.ToArray(), "default", T0);
            await store.EnqueueAsync(job, now: T0);

            var response = await http.GetAsync($"/backwave/jobs/{job.JobId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("charge-card", html);
            Assert.DoesNotContain("amount&quot;:4200", html);     // exposure off wins over permission
            Assert.DoesNotContain("aria-label=\"Copy payload\"", html); // no payload card at all
        }
    }

    // A recognizable Failure Detail string — exception-shaped, with a stack frame — seeded
    // directly through the store so the dashboard test exercises only the gated display.
    private const string FailureDetailText =
        "System.InvalidOperationException: card declined\n   at ChargeCardHandler.HandleAsync()";

    /// <summary>
    /// Seeds a failed-then-retried job: the failing transition carries Failure Detail, then a
    /// successful retry succeeds. Returns the job id.
    /// </summary>
    private static async Task<Guid> SeedFailedThenRetriedJobAsync(InMemoryJobStore store)
    {
        var job = Job(wireName: "charge-card");
        await store.EnqueueAsync(job, now: T0);
        var first = (await store.ClaimAsync(new ClaimRequest("w1", [job.Queue], 32, Lease, T0)))
            .Single(j => j.JobId == job.JobId);
        var retryAt = T0.AddMinutes(5);
        await store.ReportOutcomeAsync(
            first.JobId, "w1", first.Attempt, new JobOutcome.Failure(retryAt, "card declined"), T0,
            failureDetail: FailureDetailText);
        var second = (await store.ClaimAsync(new ClaimRequest("w1", [job.Queue], 32, Lease, retryAt)))
            .Single(j => j.JobId == job.JobId);
        await store.ReportOutcomeAsync(second.JobId, "w1", second.Attempt, new JobOutcome.Success(), retryAt);
        return job.JobId;
    }

    [Fact]
    public async Task JobDetail_ShowsFailureDetailInTheTimeline_OnlyWithViewSensitiveData()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeViewSensitiveData = _ => ValueTask.FromResult(true),
        });
        await using (app)
        {
            var jobId = await SeedFailedThenRetriedJobAsync(store);

            var html = await http.GetStringAsync($"/backwave/jobs/{jobId}");
            // The stack trace appears inline in the Transition Log, behind the same
            // ViewSensitiveData gate as the payload.
            Assert.Contains("Failure Detail", html);
            Assert.Contains("InvalidOperationException", html);
            Assert.Contains("ChargeCardHandler.HandleAsync", html);
            // The timeline still shows the rest of the lifecycle.
            Assert.Contains("Transition Log", html);
            Assert.Contains("Succeeded", html);
        }
    }

    [Fact]
    public async Task JobDetail_HidesFailureDetail_WithoutViewSensitiveData_TimelineStill200()
    {
        var (app, store, http) = await StartAsync(); // ViewSensitiveData default-deny
        await using (app)
        {
            var jobId = await SeedFailedThenRetriedJobAsync(store);

            var response = await http.GetAsync($"/backwave/jobs/{jobId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the page still renders
            var html = await response.Content.ReadAsStringAsync();
            // The transition row still shows (state/attempt/time), but the detail is withheld.
            Assert.Contains("Transition Log", html);
            Assert.Contains("Succeeded", html);
            Assert.DoesNotContain("Failure Detail", html);
            Assert.DoesNotContain("ChargeCardHandler.HandleAsync", html);
        }
    }

    [Fact]
    public async Task JobDetail_HidesFailureDetail_WhenExposureIsDisabled_EvenWithPermission()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeViewSensitiveData = _ => ValueTask.FromResult(true),
            ExposeSensitiveData = false, // host kill-switch: diagnostics never leave storage
        });
        await using (app)
        {
            var jobId = await SeedFailedThenRetriedJobAsync(store);

            var response = await http.GetAsync($"/backwave/jobs/{jobId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("Transition Log", html);
            Assert.DoesNotContain("Failure Detail", html);     // exposure off wins over permission
            Assert.DoesNotContain("ChargeCardHandler.HandleAsync", html);
        }
    }

    /// <summary>
    /// Seeds a Succeeded job carrying the given Job Output blob (ADR 0026): enqueue, claim, then
    /// report Success with the output, which co-commits on the Succeeded transition. Returns the id.
    /// </summary>
    private static async Task<Guid> SeedSucceededJobWithOutputAsync(InMemoryJobStore store, byte[] output)
    {
        var job = Job(wireName: "charge-card");
        await store.EnqueueAsync(job, now: T0);
        var claimed = (await store.ClaimAsync(new ClaimRequest("w1", [job.Queue], 32, Lease, T0)))
            .Single(j => j.JobId == job.JobId);
        await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0, output: output);
        return job.JobId;
    }

    [Fact]
    public async Task JobDetail_ShowsTheOutput_OnlyWithViewSensitiveData_LabelledAsOpaqueBytes()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeViewSensitiveData = _ => ValueTask.FromResult(true),
        });
        await using (app)
        {
            var jobId = await SeedSucceededJobWithOutputAsync(
                store, """{"receiptId":9001}"""u8.ToArray());

            var html = await http.GetStringAsync($"/backwave/jobs/{jobId}");
            // The output bytes appear, in an Output card labelled as opaque bytes, rendered
            // best-effort exactly like the payload (JSON pretty-printed and highlighted), behind
            // the same ViewSensitiveData gate.
            Assert.Contains("Output", html);                       // the output card renders
            Assert.Contains("receiptId", html);                    // the key, highlighted
            Assert.Contains("bw-json__num\">9001", html);          // the value, in a number token
            Assert.Contains("aria-label=\"Copy output\"", html);   // the output card's copy button
        }
    }

    [Fact]
    public async Task JobDetail_OmitsTheOutput_WithoutViewSensitiveData_PageStill200()
    {
        var (app, store, http) = await StartAsync(); // ViewSensitiveData default-deny
        await using (app)
        {
            var jobId = await SeedSucceededJobWithOutputAsync(
                store, """{"receiptId":9001}"""u8.ToArray());

            var response = await http.GetAsync($"/backwave/jobs/{jobId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); // absent output is not an error
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("Transition Log", html);              // the rest of the page still renders
            Assert.DoesNotContain("receiptId", html);             // but no output bytes leak
            Assert.DoesNotContain("aria-label=\"Copy output\"", html); // no output card at all
        }
    }

    [Fact]
    public async Task JobDetail_WithNoOutput_RendersCleanly_NoOutputCard()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeViewSensitiveData = _ => ValueTask.FromResult(true),
        });
        await using (app)
        {
            // A Succeeded job that emitted no output — the Output card simply does not render, and
            // the page is otherwise healthy (no error, no empty-card chrome).
            var job = Job(wireName: "charge-card");
            await RunToTerminalAsync(store, job, new JobOutcome.Success());

            var response = await http.GetAsync($"/backwave/jobs/{job.JobId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("Transition Log", html);
            Assert.DoesNotContain("bw-card__title\">Output", html); // no Output card at all
        }
    }

    [Fact]
    public async Task JobDetail_WithEmptyOutput_HidesTheOutputCard()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeViewSensitiveData = _ => ValueTask.FromResult(true),
        });
        await using (app)
        {
            // A Succeeded job that set OUTPUT but with empty bytes — non-null, yet nothing to show.
            // It must read identically to no-output: an empty 0-byte Output card is noise, not signal.
            var jobId = await SeedSucceededJobWithOutputAsync(store, []);

            var response = await http.GetAsync($"/backwave/jobs/{jobId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("Transition Log", html);
            Assert.DoesNotContain("bw-card__title\">Output", html); // no empty Output card
        }
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

    // ── Observer-delivery health (issue 0082) ───────────────────────────────────
    // The surface lists registered observers with their durable cursor and their dead-lettered
    // deliveries — METADATA ONLY (no payload, no Failure Detail), each linking to the Job detail
    // page where the existing ViewSensitiveData gate continues to govern sensitive content.

    /// <summary>A registration whose subscription watches every state of every job.</summary>
    private static ObserverRegistration ObserverWatchingAll(string id = "audit-observer")
        => new(id, new ObserverSubscription([
            JobState.Scheduled, JobState.Leased, JobState.Succeeded, JobState.Cancelled,
            JobState.DeadLettered, JobState.Quarantined, JobState.AwaitingParent,
        ]));

    /// <summary>
    /// Drives a job to terminal (so transition rows are appended to the Observer log), then claims
    /// those deliveries for <paramref name="observerId"/> and reports the first DeadLettered — the
    /// public store path that produces a real <c>ObserverDeadLetterRecord</c>. Returns the job id
    /// the dead-lettered delivery points at.
    /// </summary>
    private static async Task<Guid> SeedDeadLetteredDeliveryAsync(InMemoryJobStore store, string observerId)
    {
        var job = Job(wireName: "charge-card");
        await RunToTerminalAsync(store, job, new JobOutcome.Success());

        var states = (IReadOnlyList<JobState>)
        [
            JobState.Scheduled, JobState.Leased, JobState.Succeeded, JobState.Cancelled,
            JobState.DeadLettered, JobState.Quarantined, JobState.AwaitingParent,
        ];
        var claim = await store.ClaimObserverDeliveriesAsync(
            new ObserverClaimRequest(observerId, states, null, null, "obs-worker", MaxRows: 32, Lease, T0));
        Assert.True(claim.Acquired);
        Assert.NotEmpty(claim.Deliveries);

        // Dead-letter the first delivery; the rest are marked delivered so the cursor sweeps cleanly.
        var outcomes = claim.Deliveries
            .Select((d, i) => new ObserverDeliveryOutcome(
                d.Position,
                i == 0 ? ObserverDeliveryDisposition.DeadLettered : ObserverDeliveryDisposition.Delivered))
            .ToList();
        await store.ReportObserverDeliveriesAsync(new ObserverDeliveryReport(observerId, "obs-worker", outcomes, T0));
        return job.JobId;
    }

    [Fact]
    public async Task Observers_ListRegisteredObservers_WithCursor_AndDeadLetteredDeliveries()
    {
        var observer = ObserverWatchingAll();
        var (app, store, http) = await StartAsync(observers: [observer]);
        await using (app)
        {
            var jobId = await SeedDeadLetteredDeliveryAsync(store, observer.Id);

            var html = await http.GetStringAsync("/backwave/observers");

            // The registered observer's id and the surface chrome.
            Assert.Contains("audit-observer", html);
            Assert.Contains(">Observers</h1>", html);
            // The durable cursor line is present (a number; the cursor advanced past the swept prefix).
            Assert.Contains("Delivery cursor", html);
            // The dead-lettered deliveries section, with at least one record.
            Assert.Contains("Dead-lettered deliveries", html);
            Assert.Contains("1 dead-lettered", html);
            // The dead-lettered delivery links to the Job detail page — where ViewSensitiveData governs.
            Assert.Contains($"/backwave/jobs/{jobId}", html);
        }
    }

    [Fact]
    public async Task Observers_ShowPendingLag_WhenMatchingTransitionsAwaitDelivery()
    {
        var observer = ObserverWatchingAll();
        var (app, store, http) = await StartAsync(observers: [observer]);
        await using (app)
        {
            // Record matching transitions but never deliver them to the observer → a pending backlog.
            var job = new NewJob(Guid.NewGuid(), "charge-card", "{}"u8.ToArray(), "default", T0);
            await store.EnqueueAsync(job, now: T0);
            var claimed = (await store.ClaimAsync(new ClaimRequest("w1", [job.Queue], 32, Lease, T0)))
                .Single(j => j.JobId == job.JobId);
            await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0);

            var html = await http.GetStringAsync("/backwave/observers");

            Assert.Contains("Pending deliveries", html);   // the new lag row
            Assert.Contains("not yet delivered", html);     // pending-detail copy
            Assert.Contains("behind", html);                // the health badge reports it is behind, not clean
        }
    }

    [Fact]
    public async Task Observers_RenderMetadataOnly_NeverPayloadOrFailureDetail()
    {
        var observer = ObserverWatchingAll();
        var (app, store, http) = await StartAsync(observers: [observer]);
        await using (app)
        {
            // Seed a job whose payload + Failure Detail are recognizable strings, then dead-letter a
            // delivery for it. NONE of that sensitive content may appear on the Observer surface.
            var job = new NewJob(Guid.NewGuid(), "charge-card",
                """{"secret":"sk-live-PAYLOAD-LEAK"}"""u8.ToArray(), "default", T0);
            await store.EnqueueAsync(job, now: T0);
            var first = (await store.ClaimAsync(new ClaimRequest("w1", [job.Queue], 32, Lease, T0)))
                .Single(j => j.JobId == job.JobId);
            await store.ReportOutcomeAsync(
                first.JobId, "w1", first.Attempt, new JobOutcome.Failure(null, "boom"), T0,
                failureDetail: "System.Exception: FAILURE-DETAIL-LEAK\n   at Secret.Frame()");

            var states = (IReadOnlyList<JobState>)
                [JobState.Scheduled, JobState.Leased, JobState.DeadLettered, JobState.Succeeded];
            var claim = await store.ClaimObserverDeliveriesAsync(
                new ObserverClaimRequest(observer.Id, states, null, null, "obs-worker", 32, Lease, T0));
            var outcomes = claim.Deliveries
                .Select((d, i) => new ObserverDeliveryOutcome(
                    d.Position,
                    i == 0 ? ObserverDeliveryDisposition.DeadLettered : ObserverDeliveryDisposition.Delivered))
                .ToList();
            await store.ReportObserverDeliveriesAsync(new ObserverDeliveryReport(observer.Id, "obs-worker", outcomes, T0));

            var html = await http.GetStringAsync("/backwave/observers");

            // The dead-lettered delivery is surfaced (it links to the job)…
            Assert.Contains($"/backwave/jobs/{job.JobId}", html);
            // …but neither the payload bytes nor the Failure Detail diagnostics leak onto this
            // surface. The security-relevant invariant: the surface is metadata-only and opens no new
            // path past the gate. (The page's reassurance copy NAMES "payload"/"Failure Detail" as
            // things it never shows, so assert on the seeded sensitive VALUES, not those labels.)
            Assert.DoesNotContain("PAYLOAD-LEAK", html);
            Assert.DoesNotContain("FAILURE-DETAIL-LEAK", html);
            Assert.DoesNotContain("Secret.Frame", html);
            Assert.DoesNotContain("sk-live", html);
            // No payload card chrome from the Job-detail page bleeds in here either (the copy
            // button renders only when a real payload is shown behind the gate).
            Assert.DoesNotContain("aria-label=\"Copy payload\"", html);
        }
    }

    [Fact]
    public async Task Observers_EmptyRegistration_RendersAnExplicitEmptySurface_NotAnError()
    {
        var (app, _, http) = await StartAsync(); // no observers registered (the default)
        await using (app)
        {
            var response = await http.GetAsync("/backwave/observers");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("No Transition Observers are registered", html);
            // It renders through the shared shell like every other view, and is the active nav item.
            Assert.Contains("data-screen-label=\"Observers\"", html);
            Assert.Contains("bw-sidenav__item--active", html);
        }
    }

    [Fact]
    public async Task Observers_HealthyObserver_ShowsNoDeadLetters_ButStillListsItWithItsCursor()
    {
        var observer = ObserverWatchingAll("clean-observer");
        var (app, store, http) = await StartAsync(observers: [observer]);
        await using (app)
        {
            // A delivery that succeeds: the observer is healthy, so no dead-letters, but it still lists.
            var job = Job(wireName: "charge-card");
            await RunToTerminalAsync(store, job, new JobOutcome.Success());
            var claim = await store.ClaimObserverDeliveriesAsync(new ObserverClaimRequest(
                observer.Id, observer.Subscription.States, null, null, "obs-worker", 32, Lease, T0));
            await store.ReportObserverDeliveriesAsync(new ObserverDeliveryReport(observer.Id, "obs-worker",
                [.. claim.Deliveries.Select(d => new ObserverDeliveryOutcome(d.Position, ObserverDeliveryDisposition.Delivered))], T0));

            var html = await http.GetStringAsync("/backwave/observers");
            Assert.Contains("clean-observer", html);
            Assert.Contains("delivering cleanly", html);
            Assert.Contains("No dead-lettered deliveries for this Observer.", html);
        }
    }

    [Fact]
    public async Task Observers_IsAFullDocument_ThroughTheDesignSystemSpine_AndShipsTheLiveClient()
    {
        var observer = ObserverWatchingAll();
        var (app, _, http) = await StartAsync(observers: [observer]);
        await using (app)
        {
            var html = await http.GetStringAsync("/backwave/observers");
            Assert.StartsWith("<!DOCTYPE html>", html);
            Assert.Contains("class=\"shell\"", html);
            Assert.Contains("data-screen-label=\"Observers\"", html);
            Assert.Contains("--wave-500", html);
            Assert.Contains("new EventSource", html);          // a live view, like Failures
            Assert.DoesNotContain("_framework/blazor", html);
        }
    }

    // ── Job Tags (ADR 0022, issue 0113): pills, click-to-filter, facets ─────────

    private static NewJob TaggedJob(string wireName, JobTags tags, string queue = "default")
        => Job(wireName: wireName, queue: queue) with { Tags = tags };

    [Fact]
    public async Task JobTable_RendersTagPills_LabelBare_KeyedAsKeyColonValue_EmptySetRendersNothing()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            await store.EnqueueAsync(
                TaggedJob("tagged", JobTags.Empty.WithLabel("urgent").WithTag("tenant", "acme")), now: T0);
            await store.EnqueueAsync(TaggedJob("bare", JobTags.Empty), now: T0);

            var html = await http.GetStringAsync("/backwave/jobs");

            // A Label renders as its bare value; a Keyed Tag renders key:value (a DISPLAY choice).
            Assert.Contains(">urgent</a>", html);
            Assert.Contains(">tenant:acme</a>", html);
            // The pills sit in the tag container; the untagged row renders no pill (no bw-tag link
            // for it) — the empty set is a dash placeholder, never an empty bw-tags container.
            Assert.Contains("class=\"bw-tags\"", html);
            Assert.Contains("class=\"bw-tags__empty\"", html);
        }
    }

    [Fact]
    public async Task TagPill_ClickToFilter_ProducesAHasLabelOrHasKeyValueFilter_ComposedWithScalarFilters()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            await store.EnqueueAsync(TaggedJob("acme-job", JobTags.Empty.WithTag("tenant", "acme")), now: T0);
            await store.EnqueueAsync(TaggedJob("other-job", JobTags.Empty.WithTag("tenant", "globex")), now: T0);
            await store.EnqueueAsync(TaggedJob("urgent-job", JobTags.Empty.WithLabel("urgent")), now: T0);

            // has key=value: the keyed pill encodes as a matched tk=/tv= pair; following it narrows
            // the list to the matching job only.
            var keyed = await http.GetStringAsync("/backwave/jobs?tk=tenant&tv=acme");
            Assert.Contains("acme-job", keyed);
            Assert.DoesNotContain("other-job", keyed);
            Assert.DoesNotContain("urgent-job", keyed);
            // The active-filter chip shows the AND-ed predicate.
            Assert.Contains("Active tag filters", keyed);

            // has-label: the label pill encodes as a tl= param.
            var labelled = await http.GetStringAsync("/backwave/jobs?tl=urgent");
            Assert.Contains("urgent-job", labelled);
            Assert.DoesNotContain("acme-job", labelled);

            // Tag filters AND-compose with State/Queue/Wire — the keyed filter plus a non-matching
            // state yields nothing, proving composition (not replacement).
            var composed = await http.GetStringAsync("/backwave/jobs?tk=tenant&tv=acme&state=Succeeded");
            Assert.DoesNotContain("acme-job", composed);
        }
    }

    [Fact]
    public async Task TagFilter_LabelContainingAColon_RoundTripsWithoutBeingSplit()
    {
        // THE anti-parsing case (ADR 0022): a Label whose value itself contains a colon. The pill text
        // shows the colon verbatim; the click-to-filter URL encodes the WHOLE value as one tl= param
        // (the colon percent-encoded inside it), so nothing ever splits on the colon. Following the
        // URL matches the Label exactly — never a phantom key "ratio 3" / value "1".
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            await store.EnqueueAsync(TaggedJob("colon-job", JobTags.Empty.WithLabel("ratio 3:1")), now: T0);
            await store.EnqueueAsync(TaggedJob("decoy-job", JobTags.Empty.WithTag("ratio 3", "1")), now: T0);

            var list = await http.GetStringAsync("/backwave/jobs");
            // The pill shows the colon as ordinary data (the Label is bare, not key:value).
            Assert.Contains(">ratio 3:1</a>", list);
            // The Label pill href encodes the whole value into a single tl= param — colon as %3A,
            // never a tk=/tv= split. This is the URL the click follows. (The unrelated DECOY job,
            // a genuine Keyed Tag "ratio 3"="1", does render its own tk=/tv= pair — proving the two
            // are structurally distinct, not told apart by parsing the colon.)
            Assert.Contains("tl=ratio%203%3A1", list);

            // Following that exact URL filters to the colon Label only — the tenant-style decoy that
            // would result from a (wrong) first-colon split is NOT matched.
            var filtered = await http.GetStringAsync("/backwave/jobs?tl=ratio%203%3A1");
            Assert.Contains("colon-job", filtered);
            Assert.DoesNotContain("decoy-job", filtered);
        }
    }

    [Fact]
    public async Task Facets_RenderLabelCounts_AndRespectTheActiveFilter()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            // Three jobs carry "urgent"; one also carries "vip"; an untagged-of-that-label job exists.
            await store.EnqueueAsync(TaggedJob("a", JobTags.Empty.WithLabel("urgent")), now: T0);
            await store.EnqueueAsync(TaggedJob("b", JobTags.Empty.WithLabel("urgent")), now: T0);
            await store.EnqueueAsync(TaggedJob("c", JobTags.Empty.WithLabel("urgent").WithLabel("vip")), now: T0);
            await store.EnqueueAsync(TaggedJob("d", JobTags.Empty.WithLabel("vip")), now: T0);

            var unfiltered = await http.GetStringAsync("/backwave/jobs");
            // The Top-labels facet card renders, with counts. "urgent" has 3, "vip" has 2.
            Assert.Contains("Top labels", unfiltered);
            Assert.Matches(new Regex(">urgent\\s*<span class=\"bw-tag__count\">3</span>"), unfiltered);
            Assert.Matches(new Regex(">vip\\s*<span class=\"bw-tag__count\">2</span>"), unfiltered);

            // Scoped by the active filter: within jobs that carry "urgent", "vip"'s count drops to 1
            // (only job c carries both) — proving the facet uses the current JobQuery as baseQuery.
            var scoped = await http.GetStringAsync("/backwave/jobs?tl=urgent");
            Assert.Matches(new Regex(">vip\\s*<span class=\"bw-tag__count\">1</span>"), scoped);
        }
    }

    [Fact]
    public async Task TagSuggest_ReturnsLabelValuesAsJson_ForThePresentEmptyKeyStage()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            await store.EnqueueAsync(TaggedJob("a", JobTags.Empty.WithLabel("urgent")), now: T0);
            await store.EnqueueAsync(TaggedJob("b", JobTags.Empty.WithLabel("urchin")), now: T0);
            await store.EnqueueAsync(TaggedJob("c", JobTags.Empty.WithLabel("vip")), now: T0);

            // key= present-but-empty selects the Label dimension; the prefix matches case-insensitively.
            var response = await http.GetAsync("/backwave/tags/suggest?key=&prefix=UR&max=20");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var values = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("value").GetString()).ToList();
            // Both "urgent" and "urchin" prefix-match "UR" (ASCII case-insensitive); "vip" does not.
            Assert.Contains("urchin", values);
            Assert.Contains("urgent", values);
            Assert.DoesNotContain("vip", values);
            // Label suggestions carry an empty key (the has-label dimension).
            Assert.All(doc.RootElement.EnumerateArray(), e => Assert.Equal("", e.GetProperty("key").GetString()));
        }
    }

    [Fact]
    public async Task JobsPage_RendersTheTagSuggestInput_WiredToTheSuggestEndpoint()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            // With an active Label filter, the input's base href carries the existing predicate, so a
            // picked suggestion ANDs onto it (and the client appends its own tl= or tk=/tv= param).
            await store.EnqueueAsync(TaggedJob("a", JobTags.Empty.WithLabel("urgent")), now: T0);

            var html = await http.GetStringAsync("/backwave/jobs?tl=urgent");
            // The input is present and points at the JSON suggest endpoint; the dropdown script ships.
            Assert.Contains("data-bw-suggest=\"/backwave/tags/suggest\"", html);
            Assert.Contains("data-bw-suggest-input", html);
            Assert.Contains("data-bw-suggest-base=\"/backwave/jobs?tl=urgent\"", html);
            // Two-stage window size drives the scroll-virtualized paging (issue 0215).
            Assert.Contains("data-bw-suggest-window=\"20\"", html);
            Assert.Contains("Filter by Tag", html);
            Assert.Contains("data-bw-suggest]", html); // the inline dropdown script's selector
            // WAI-ARIA combobox scaffolding (ADR 0043): the static roles/wiring the server renders,
            // which the inline keyboard script drives (per-option role/id + aria-activedescendant).
            Assert.Contains("role=\"combobox\"", html);
            Assert.Contains("aria-autocomplete=\"list\"", html);
            Assert.Contains("aria-expanded=\"false\"", html);
            Assert.Contains("aria-controls=\"bw-suggest-menu\"", html);
            Assert.Contains("role=\"listbox\"", html);
            Assert.Contains("id=\"bw-suggest-menu\"", html);
        }
    }

    [Fact]
    public async Task TagSuggest_StageOne_MixesLabelSuggestionsWithKeyDrillIns()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            await store.EnqueueAsync(TaggedJob("a", JobTags.Empty.WithLabel("urgent").WithTag("tenant", "acme")), now: T0);
            await store.EnqueueAsync(TaggedJob("b", JobTags.Empty.WithLabel("vip").WithTag("team", "core")), now: T0);

            // Stage one = NO key param (Key=null): the endpoint mixes Labels (key="") with key
            // drill-ins (value="").
            var response = await http.GetAsync("/backwave/tags/suggest?prefix=&max=20");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var entries = doc.RootElement.EnumerateArray()
                .Select(e => (Key: e.GetProperty("key").GetString(), Value: e.GetProperty("value").GetString()))
                .ToList();

            // Labels come through as key="" value=<label>; keys come through as key=<key> value=""
            // (the client renders those as drill-ins).
            Assert.Contains(("", "urgent"), entries);
            Assert.Contains(("", "vip"), entries);
            Assert.Contains(("team", ""), entries);
            Assert.Contains(("tenant", ""), entries);
        }
    }

    [Fact]
    public async Task TagSuggest_StageTwo_ReturnsValuesUnderTheSelectedKey()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            await store.EnqueueAsync(TaggedJob("a", JobTags.Empty.WithTag("tenant", "acme")), now: T0);
            await store.EnqueueAsync(TaggedJob("b", JobTags.Empty.WithTag("tenant", "aardvark")), now: T0);
            await store.EnqueueAsync(TaggedJob("c", JobTags.Empty.WithTag("team", "core")), now: T0);

            // Stage two = key=<selected>: only that key's values, and each carries the canonical key so
            // the client composes an exact has-key/value chip (tk=/tv=).
            var response = await http.GetAsync("/backwave/tags/suggest?key=tenant&prefix=a&max=20");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var entries = doc.RootElement.EnumerateArray()
                .Select(e => (Key: e.GetProperty("key").GetString(), Value: e.GetProperty("value").GetString()))
                .ToList();

            Assert.All(entries, e => Assert.Equal("tenant", e.Key));
            var values = entries.Select(e => e.Value).ToList();
            Assert.Contains("aardvark", values);
            Assert.Contains("acme", values);
            Assert.DoesNotContain("core", values); // a different key's value
        }
    }

    [Fact]
    public async Task TagSuggest_KeysetCursor_PagesAValueSetAWindowAtATime()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            // Five values under one key; a window of two pages through them via the ak/av cursor.
            foreach (var value in new[] { "v1", "v2", "v3", "v4", "v5" })
            {
                await store.EnqueueAsync(TaggedJob($"j-{value}", JobTags.Empty.WithTag("tenant", value)), now: T0);
            }

            static async Task<List<string>> PageAsync(HttpClient http, string url)
            {
                using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
                return doc.RootElement.EnumerateArray().Select(e => e.GetProperty("value").GetString()!).ToList();
            }

            var first = await PageAsync(http, "/backwave/tags/suggest?key=tenant&prefix=&max=2");
            Assert.Equal(new[] { "v1", "v2" }, first);

            // Feed the last suggestion back as the ak/av keyset cursor to fetch the next window.
            var second = await PageAsync(http, "/backwave/tags/suggest?key=tenant&prefix=&max=2&ak=tenant&av=v2");
            Assert.Equal(new[] { "v3", "v4" }, second);

            var third = await PageAsync(http, "/backwave/tags/suggest?key=tenant&prefix=&max=2&ak=tenant&av=v4");
            Assert.Equal(new[] { "v5" }, third); // a short final window: nothing after it
        }
    }

    [Fact]
    public async Task JobDetail_ShowsTags_LinkingBackToAFilteredJobList()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            var job = TaggedJob("detail", JobTags.Empty.WithLabel("urgent").WithTag("tenant", "acme"));
            await store.EnqueueAsync(job, now: T0);

            var html = await http.GetStringAsync($"/backwave/jobs/{job.JobId}");
            Assert.Contains(">urgent</a>", html);
            Assert.Contains(">tenant:acme</a>", html);
            Assert.Contains("tk=tenant&amp;tv=acme", html); // links back to the filtered list
        }
    }

    // ---- ADR 0044 responsive card transform: the data-label markup contract (issue 0217) ----
    //
    // Below a narrow container width every data-grid .ptable restyles each <td> into a
    // `label: value` card line whose label comes from data-label. Because that transform is the
    // opt-out DEFAULT, a data-grid cell shipped without a non-empty data-label renders a
    // blank-labeled card — silent breakage. Data grids render as exactly class="ptable"; property
    // tables opt out as class="ptable ptable--plain". This helper isolates the data grids on a
    // rendered page and asserts every one of their cells is labeled.

    private static void AssertEveryDataGridCellIsLabeled(string html)
    {
        var grids = Regex.Matches(html, "<table class=\"ptable\">(.*?)</table>", RegexOptions.Singleline);
        Assert.NotEmpty(grids); // the page under test actually rendered at least one data grid
        foreach (Match grid in grids)
        {
            var cells = Regex.Matches(grid.Groups[1].Value, "<td\\b[^>]*>");
            Assert.NotEmpty(cells);
            foreach (Match cell in cells)
            {
                Assert.True(
                    Regex.IsMatch(cell.Value, "data-label=\"[^\"]+\""),
                    $"A data-grid <td> lacks a non-empty data-label (ADR 0044 card contract): {cell.Value}");
            }
        }
    }

    [Fact]
    public async Task JobTable_EveryDataCell_CarriesANonEmptyDataLabel_IncludingTagsAndActions()
    {
        // Both Operator Actions granted so the Actions column renders; a registry so the view is happy.
        var (app, store, http) = await StartAsync(
            new BackWaveDashboardOptions
            {
                AuthorizeRequeue = _ => ValueTask.FromResult(true),
                AuthorizeCancel = _ => ValueTask.FromResult(true),
            },
            registry: SampleRegistry());
        await using (app)
        {
            // A tagged job (populates the Tags cell) and a bare one (the empty-tags placeholder), plus a
            // requeue-eligible terminal job so the Actions cell holds a real control — every conditional
            // cell of the shared JobTable is exercised.
            await store.EnqueueAsync(
                TaggedJob("send-email", JobTags.Empty.WithLabel("urgent").WithTag("tenant", "acme")), now: T0);
            await store.EnqueueAsync(TaggedJob("build-report", JobTags.Empty), now: T0);
            await RunToTerminalAsync(store, Job(wireName: "send-email"), new JobOutcome.Failure(null, "boom"));

            var html = await http.GetStringAsync("/backwave/jobs");

            AssertEveryDataGridCellIsLabeled(html);
            // The two conditional columns really rendered (proving their cells were covered above).
            Assert.Contains("data-label=\"Tags\"", html);
            Assert.Contains("data-label=\"Actions\"", html);
        }
    }

    [Fact]
    public async Task ExecutingTable_EveryDataCell_CarriesANonEmptyDataLabel()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            await store.EnqueueAsync(Job(wireName: "charge-card", queue: "billing"), now: T0);
            await store.ClaimAsync(new ClaimRequest("worker-7", ["billing"], 32, Lease, T0));

            var html = await http.GetStringAsync("/backwave/executing");
            AssertEveryDataGridCellIsLabeled(html);
        }
    }

    [Fact]
    public async Task QueuesTable_EveryDataCell_CarriesANonEmptyDataLabel_IncludingActions()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizePauseQueue = _ => ValueTask.FromResult(true), // renders the Actions column
        });
        await using (app)
        {
            await store.EnqueueAsync(Job(wireName: "work", queue: "q"), now: T0);
            await store.SetConcurrencyLimitAsync("q", 5, actor: "op", now: T0);
            await store.ClaimAsync(new ClaimRequest("w1", ["q"], 1, Lease, T0));

            var html = await http.GetStringAsync("/backwave/queues");
            AssertEveryDataGridCellIsLabeled(html);
            Assert.Contains("data-label=\"Actions\"", html);
        }
    }

    [Fact]
    public async Task SchedulesTable_EveryDataCell_CarriesANonEmptyDataLabel_IncludingActions()
    {
        var (app, store, http) = await StartAsync(new BackWaveDashboardOptions
        {
            AuthorizeTriggerSchedule = _ => ValueTask.FromResult(true), // renders the Actions column
        });
        await using (app)
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

            var html = await http.GetStringAsync("/backwave/schedules");
            AssertEveryDataGridCellIsLabeled(html);
            Assert.Contains("data-label=\"Actions\"", html);
        }
    }

    [Fact]
    public async Task ObserverDeadLetterTable_EveryDataCell_CarriesANonEmptyDataLabel_CursorTableIsPlain()
    {
        var observer = ObserverWatchingAll();
        var (app, store, http) = await StartAsync(observers: [observer]);
        await using (app)
        {
            await SeedDeadLetteredDeliveryAsync(store, observer.Id);

            var html = await http.GetStringAsync("/backwave/observers");
            // The dead-lettered-deliveries grid (a data grid) is fully labeled…
            AssertEveryDataGridCellIsLabeled(html);
            // …while the per-observer cursor/pending list is a 2-column property table and opts out.
            Assert.Contains("class=\"ptable ptable--plain\"", html);
        }
    }

    [Fact]
    public async Task PropertyTables_OnJobDetailAndScheduleDetail_OptOutOfTheCardTransform()
    {
        var (app, store, http) = await StartAsync();
        await using (app)
        {
            var job = Job(wireName: "detail-job");
            await store.EnqueueAsync(job, now: T0);
            await store.UpsertScheduleAsync(new ScheduleRecord
            {
                ScheduleId = "nightly-sync",
                Cron = CronExpression.Parse("0 3 * * *").Canonical,
                WireName = "sync-inventory",
                Payload = "{}"u8.ToArray(),
                Queue = "default",
                Cursor = T0,
            });

            var jobDetail = await http.GetStringAsync($"/backwave/jobs/{job.JobId}");
            Assert.Contains("class=\"ptable ptable--plain\"", jobDetail); // the Job-facts property table

            var scheduleDetail = await http.GetStringAsync("/backwave/schedules/nightly-sync");
            Assert.Contains("class=\"ptable ptable--plain\"", scheduleDetail); // the Schedule-facts table
        }
    }

    [Fact]
    public void AMountPathWithoutALeadingSlash_IsRefusedLoudly()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        using var app = builder.Build();
        Assert.Throws<ArgumentException>(() => app.UseBackWaveDashboard("backwave"));
    }

    [Fact]
    public async Task TheBaseDashboard_HasNoWorkflowSurface()
    {
        // Workflows are a Pro feature: the base dashboard, with no Pro dashboard extension registered,
        // contributes no Workflows navigation entry and serves no workflow routes. (The surface lives
        // in the BackWave.Pro.Dashboard package and lights up only when it is installed.)
        var (app, _, http) = await StartAsync();
        await using (app)
        {
            var overview = await http.GetStringAsync("/backwave/");
            Assert.DoesNotContain("/backwave/workflows", overview);
            Assert.DoesNotContain(">Workflows<", overview);

            // Both the list and a detail route are Not Found without the extension.
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/backwave/workflows")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await http.GetAsync($"/backwave/workflows/{Guid.NewGuid()}")).StatusCode);
        }
    }
}
