using System.Threading.RateLimiting;
using BackWave;
using BackWave.Core;
using BackWave.Dashboard;
using BackWave.Demo;
using BackWave.Generated;
using BackWave.Hosting;
using BackWave.Observers;
using BackWave.Operations;
using BackWave.Pro;
using BackWave.Pro.Dashboard;
using BackWave.Pro.Mcp;
using BackWave.Sqlite;
using BackWave.Storage;

var builder = WebApplication.CreateBuilder(args);

// The whole world lives in one ephemeral SQLite file inside the container — the Embedded Adapter,
// co-resident, dogfooding the "zero operational overhead" story. Recycled hourly, so its lifetime is
// the container's. Path resolves under the app base directory so it lands somewhere predictable.
var dataSource = Path.Combine(AppContext.BaseDirectory, "backwave-demo.db");

// Process-wide state the jobs share (the Concurrency Limit high-water mark must outlive any Attempt).
builder.Services.AddSingleton<ConcurrencyTracker>();

builder.Services.AddBackWave(backwave =>
{
    backwave
        .UseStore(_ => new SqliteJobStore(new SqliteStoreOptions
        {
            ConnectionString = $"Data Source={dataSource}",
            AutoMigrate = true, // embedded Schema/*.sql self-applies on startup
        }))
        // One call registers the Job Registry, a scoped handler per [Job], and the class that declares
        // the [Job] methods (DemoJobs) — including the continuous generate-workload generator.
        .UseJobs(BackWaveJobs.Module);

    // Two Worker Groups in-process. Strict: 'critical' preempts 'bulk' preempts 'limited'.
    backwave.AddWorkerGroup(new WorkerGroupOptions
    {
        Name = "strict-priority",
        Policy = new DispatchPolicy.Strict(["critical", "bulk", "limited"]),
        PollInterval = TimeSpan.FromMilliseconds(250),
        RetryPolicy = new RetryPolicy { MaxAttempts = 5, Backoff = _ => TimeSpan.FromMilliseconds(500) },
    });

    // Weighted: 'high' gets 6 turns per 1 of 'low'. A low attempt ceiling lets flaky work on 'low'
    // reach Dead-Lettered quickly, keeping "Needs attention" populated.
    backwave.AddWorkerGroup(new WorkerGroupOptions
    {
        Name = "weighted-fair",
        Policy = new DispatchPolicy.Weighted([("high", 6), ("low", 1)]),
        PollInterval = TimeSpan.FromMilliseconds(250),
        RetryPolicy = new RetryPolicy { MaxAttempts = 3, Backoff = _ => TimeSpan.FromMilliseconds(500) },
    });

    // A Transition Observer, so the dashboard's Observers surface shows real delivery history and lag.
    // Subscribed to the order-notification job's TERMINAL transitions only, filtered to its Wire Name.
    backwave.AddObservers(obs =>
    {
        obs.ConfigurePump(o => o.PollInterval = TimeSpan.FromSeconds(2));
        obs.Add<DemoObserver>("demo", new ObserverSubscription([JobState.Succeeded, JobState.DeadLettered])
        {
            WireName = "order-notification",
        });
    });

    // Light up the dashboard's live metrics panel: per-second throughput (enqueued / processed /
    // failed) and Top / Faulting Endpoints for the node hosting the demo. Per-node and ephemeral —
    // it resets on the hourly recycle — which is exactly right for a single-node live demo.
    backwave.AddDashboardMetrics();

    // BackWave Pro MCP server, mounted below (UseBackWaveProMcp) at /backwave-mcp for AI agents.
    // Deliberately READ-ONLY on the public demo: no write gate is granted and sensitive-data stays
    // locked, so every write tool (pause/requeue/cancel/trigger/set-limit) is hidden from tools/list
    // and job payloads/outputs are unreadable. Unlike the co-resident dashboard, this endpoint is
    // reached without antiforgery or a browser, so it exposes only the safe read surface — queue
    // depths, job/workflow search, observer lag — even though the data itself is synthetic.
    backwave.AddMcp();
});

// BackWave Pro, licensed: the Workflows tab presents clean (no unlicensed banner). The key is supplied
// at deploy time as a host secret (BackWave:ProLicense); absent locally, Pro still runs in full.
builder.Services.AddBackWavePro(builder.Configuration["BackWave:ProLicense"]);
builder.Services.AddBackWaveProDashboard();

// The conditional gate the checkout workflow's .If lowers to. A gate is an ordinary job (step + handler),
// so it must be registered before a workflow that uses it can run - AddWorkflowGate does both in one call
// from the gate's source-generated codec. It runs on the served 'critical' queue (the AddWorkflowGate
// default is 'default', which no Worker Group here serves) so the gate actually dispatches.
builder.Services.AddWorkflowGate<LargeOrderGate, PriceOrder, OrderPrice, CheckoutSeed>(
    "large-order-gate",
    CheckoutJsonContext.Default.WorkflowGateLargeOrderGatePriceOrderOrderPriceCheckoutSeed,
    queue: "critical");

// The dashboard's Operator Actions are antiforgery-protected POSTs; the middleware needs the services.
builder.Services.AddAntiforgery();

// A light per-IP rate limit protects the process (not the data — the hourly recycle covers that) from a
// scripted-load DoS. Only the state-changing POSTs (the Operator Actions) are limited; reads pass free.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return RateLimitPartition.GetNoLimiter("reads");
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromSeconds(10),
            QueueLimit = 0,
        });
    });
});

var app = builder.Build();

// noindex + robots-disallow on the whole subdomain (churning job rows should never be crawled), and a
// robots.txt served before the dashboard middleware can claim the path.
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    if (context.Request.Path == "/robots.txt")
    {
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync("User-agent: *\nDisallow: /\n");
        return;
    }

    await next();
});

app.UseRateLimiter();

// BackWave Pro MCP endpoint (registered via backwave.AddMcp() above), mounted before the root
// dashboard so its /backwave-mcp branch claims the path first. Read-only: an AI agent pointed here
// can list and call the read tools, but the write tools are hidden and sensitive data is locked.
// Sits after UseRateLimiter, so its POSTs share the per-IP fixed-window limit.
app.UseBackWaveProMcp();

// The identity stamped into every Operator Action's audit record.
const string actor = "demo-visitor";

// Write-capable dashboard at the ROOT. Everything is opted in because the data is synthetic and
// pressing the buttons for real is the whole point: all four Operator Actions plus ViewSensitiveData.
// A ~3s live-refresh keeps it snappy while bounding SSE re-render cost.
app.UseBackWaveDashboard("/", new BackWaveDashboardOptions
{
    AuthorizeView = _ => ValueTask.FromResult(true),
    AuthorizeRequeue = _ => ValueTask.FromResult(true),
    AuthorizeCancel = _ => ValueTask.FromResult(true),
    AuthorizePauseQueue = _ => ValueTask.FromResult(true),
    AuthorizeTriggerSchedule = _ => ValueTask.FromResult(true),
    AuthorizeViewSensitiveData = _ => ValueTask.FromResult(true),
    ResolveActor = _ => actor,
    LiveRefreshInterval = TimeSpan.FromSeconds(3),
});

// ── Startup: cap the 'limited' queue, arm the continuous generator, seed the baseline ──────────────
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var op = services.GetRequiredService<BackWaveOperator>();
    var client = services.GetRequiredService<BackWaveClient>();
    var store = services.GetRequiredService<IJobStore>();

    // Set the 'limited' Queue's cluster-wide Concurrency Limit (also triggers the store's AutoMigrate).
    await op.SetConcurrencyLimitAsync("limited", 1, actor: "startup");

    // The self-replenishing heartbeat: a recurring schedule mints generate-workload every minute, and
    // each run enqueues a fresh wave so "Executing now" never empties between the boot seed's long-holds.
    await client.UpsertRecurringAsync(
        "workload-generator", Cron.EveryMinute(), new GenerateWorkload("recurring"), queue: "critical");

    // Fill every panel now, so a visitor landing seconds after a recycle already sees a busy dashboard.
    await DemoSeed.RunAsync(client, store, op, actor);
}

app.Logger.LogInformation("BackWave.Demo started — dashboard mounted at / on the SQLite store ({DataSource}).", dataSource);

app.Run();
