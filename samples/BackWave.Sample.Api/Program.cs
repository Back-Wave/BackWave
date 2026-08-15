using BackWave;
using BackWave.Core;
using BackWave.Dashboard;
using BackWave.EntityFrameworkCore;
using BackWave.Generated;
using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Monitor;
using BackWave.Observers;
using BackWave.Operations;
using BackWave.Pro;
using BackWave.Pro.Dashboard;
using BackWave.Pro.Mcp;
using BackWave.Sample.Api;
using BackWave.Storage;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// ── Storage selection ────────────────────────────────────────────────────────
// BackWave:Store = InMemory (default) | Postgres | SqlServer. In-Memory is F5-and-go with
// zero infra; the relational stores need the sample's own `docker compose up -d` first. Each
// kind carries its own store/DbContext/schema behavior (see StoreKind.cs).
var store = StoreKind.FromConfiguration(builder.Configuration);

// ── BackWave wiring — exactly how a consumer wires it up ─────────────────────
// Process-wide state the jobs share: a singleton injected into the scoped SampleJobs (its
// cluster-wide concurrency high-water mark must outlive any one Attempt). SampleJobs itself —
// the class that declares the [Job] methods — is registered (scoped) by UseJobs below.
builder.Services.AddSingleton<ConcurrencyTracker>();

// Shared readiness signal for the poll-from-a-step escape-hatch demo (see /workflows/escape-hatches).
builder.Services.AddSingleton<ExternalReadinessGate>();

builder.Services.AddBackWave(backwave =>
{
    backwave
        // The factory overload hands us the provider, so the store gets the app's own ILoggerFactory -
        // which is what opts a relational store into the schema-migration log (off by default).
        .UseStore(serviceProvider => store.CreateStore(
            builder.Configuration, serviceProvider.GetRequiredService<ILoggerFactory>()))
        // One call registers the Job Registry, a scoped handler per [Job], and the scoped class that
        // declares the [Job] methods (SampleJobs) — no hand-written registration to keep in sync with
        // the registry (ADR 0021 + 0106).
        .UseJobs(BackWaveJobs.Module);

    // Two Worker Groups hosted in-process — single `dotnet run`, no separate worker host.
    // Strict: 'critical' preempts 'bulk' preempts 'limited' (starvation of the tail is deliberate).
    backwave.AddWorkerGroup(new WorkerGroupOptions
    {
        Name = "strict-priority",
        Policy = new DispatchPolicy.Strict(["critical", "bulk", "limited"]),
        PollInterval = TimeSpan.FromMilliseconds(250),
        RetryPolicy = new RetryPolicy { MaxAttempts = 5, Backoff = _ => TimeSpan.FromMilliseconds(500) },
    });

    // Weighted: smooth weighted round-robin, 'high' gets 6 turns per 1 of 'low'. A low ceiling
    // here lets the flaky job (queue 'low') reach Dead-Lettered quickly.
    backwave.AddWorkerGroup(new WorkerGroupOptions
    {
        Name = "weighted-fair",
        Policy = new DispatchPolicy.Weighted([("high", 6), ("low", 1)]),
        PollInterval = TimeSpan.FromMilliseconds(250),
        RetryPolicy = new RetryPolicy { MaxAttempts = 3, Backoff = _ => TimeSpan.FromMilliseconds(500) },
    });

    // A Transition Observer hosted in-process alongside the Worker Groups — single `dotnet run`,
    // no separate host. The dummy SlackObserver pretends to post to Slack but really logs one
    // structured console line per delivery, reading the job's payload body to prove the lazy read.
    // Subscribed to the order-notification job's TERMINAL transitions only (Succeeded AND
    // Dead-Lettered), filtered to its Wire Name so it never fires for other job types.
    backwave.AddObservers(obs =>
    {
        obs.ConfigurePump(o => o.PollInterval = TimeSpan.FromSeconds(2));
        obs.Add<SlackObserver>("slack", new ObserverSubscription([JobState.Succeeded, JobState.DeadLettered])
        {
            WireName = "order-notification",
        });
    });

    // Light up the dashboard's live metrics panel (throughput sparklines, Top/Faulting endpoints, and
    // approximate p95/p99 latency) from inside the same block. A MeterListener fills a bounded, per-node,
    // in-memory ring buffer from the BackWave Meter this process already emits — no store surface, resets
    // on restart. Without this call the panel renders its honest "live throughput is off" empty state.
    backwave.AddDashboardMetrics();

    // BackWave Pro MCP server, registered in the same block and mounted below (UseBackWaveProMcp).
    // Defaults are dev-friendly: viewing is allowed, so an MCP client (e.g. `claude mcp add --transport
    // http backwave http://localhost:5283/backwave-mcp`) can list and call the read tools out of the
    // box. Writes and sensitive data are default-deny; like the dashboard permissions below, this dev
    // sample opts into all of them so the write and workflow tools appear and can be exercised by
    // hand — see the "MCP server" walkthrough in README.md. Production hosts delegate each callback
    // to their own authorization instead.
    backwave.AddMcp(mcp =>
    {
        mcp.AuthorizeRequeue = _ => ValueTask.FromResult(true);
        mcp.AuthorizeCancel = _ => ValueTask.FromResult(true); // also unlocks cancel_workflow
        mcp.AuthorizePauseQueue = _ => ValueTask.FromResult(true);
        mcp.AuthorizeTriggerSchedule = _ => ValueTask.FromResult(true);
        mcp.AuthorizeSetConcurrencyLimit = _ => ValueTask.FromResult(true);
        mcp.AuthorizeViewSensitiveData = _ => ValueTask.FromResult(true);
        // Stamped into the audit record of every write an MCP client performs, so those rows are
        // distinguishable from the dashboard's ("sample-operator") in list_audit_records / the UI.
        mcp.ResolveActor = _ => "sample-mcp";
    });
});

// BackWave Pro: unlocks the Workflow feature this sample exercises (Workflow / EnqueueAsync /
// CancelWorkflow). The license is read from config (BackWave:ProLicense); when it's absent the value is
// null — the realistic free-use default for a small org — so startup logs the one-line unlicensed
// warning and everything runs in full. Set it (e.g. BackWave__ProLicense=<key>) to run licensed.
builder.Services.AddBackWavePro(builder.Configuration["BackWave:ProLicense"]);

// BackWave Pro dashboard surfaces: adds the Workflows tab/graph (and the unlicensed-Pro banner) to the
// dashboard mounted below. The Workflows surface appears because this package is installed — the base
// dashboard alone shows none. Registered after AddBackWavePro so the evaluated license is available.
builder.Services.AddBackWaveProDashboard();

// ── OpenTelemetry - the copy-paste reference wiring ──────────────────────────
// BackWave's Core and Hosting packages stay BCL-only: they emit their traces, metrics, and logs on
// plain ActivitySource/Meter/ILogger names and you pay nothing until you subscribe. This block is
// that subscription - the whole point of the sample as a consumer example. AddBackWaveInstrumentation()
// (from the BackWave.OpenTelemetry package) wires the Core job-lifecycle signals; the configured
// store's per-adapter opt-in adds its db.* store spans/fault meter on top (see StoreKind). Everything
// exports over BOTH the console exporter (always on, so `dotnet run` prints spans/metrics/logs with no
// infra) and OTLP (lit up only when an endpoint is configured, so a bare run stays quiet).
//
// OTLP endpoint from the OTel-standard env var OTEL_EXPORTER_OTLP_ENDPOINT (also honored from
// appsettings via IConfiguration). Point it at an Aspire dashboard or a collector and traces, metrics,
// and logs flow there with no other change; leave it unset and only the console exporter runs.
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
var otlpEnabled = !string.IsNullOrWhiteSpace(otlpEndpoint);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("BackWave.Sample.Api"))
    .WithTracing(tracing =>
    {
        tracing.AddBackWaveInstrumentation(); // Core job-lifecycle spans: send, receive, process
        store.AddAdapterTracing(tracing);     // the configured store's db.* round-trip spans (opt-in)
        tracing.AddConsoleExporter();
        if (otlpEnabled)
        {
            tracing.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpEndpoint!));
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddBackWaveInstrumentation(); // Core instruments incl. messaging.process.duration + the schedule/queue histograms
        store.AddAdapterMetrics(metrics);     // the configured store's store-fault meter (opt-in)

        // Exemplars carry the trace-id of the sampled measurement that landed in a histogram bucket, so
        // a slow-bucket spike on messaging.process.duration / backwave.schedule.delay / backwave.job.queue.wait
        // links straight through to the originating trace in a viewer. GUIDANCE: trace/span ids ride ONLY
        // as exemplars, never as metric attributes - putting a trace-id on a metric would explode its
        // cardinality. TraceBased records an exemplar only when the measurement happened inside a sampled
        // trace (which, under the default AlwaysOn sampler here, is every job).
        metrics.SetExemplarFilter(ExemplarFilterType.TraceBased);

        metrics.AddConsoleExporter();
        if (otlpEnabled)
        {
            metrics.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpEndpoint!));
        }
    })
    .WithLogging(
        logging =>
        {
            logging.AddConsoleExporter();
            if (otlpEnabled)
            {
                logging.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpEndpoint!));
            }
        },
        options =>
        {
            // Bridge BackWave's structured [LoggerMessage] catalog (enqueued, lease lost/reclaimed,
            // retry scheduled, dead-lettered, store fault, migration, ...) into OTel Logs with its
            // scopes (job_id, wire_name, attempt, queue) and structured state intact.
            options.IncludeScopes = true;
            options.IncludeFormattedMessage = true;
        });

// The conditional gate the /workflows/checkout scenario's .If lowers to. A gate is an ordinary job (step +
// handler), so it must be registered like any other before a workflow that uses it can run - AddWorkflowGate
// does both in one call from the gate's source-generated codec. It runs on the served 'critical' queue (the
// AddWorkflowGate default is 'default', which no Worker Group here serves) so the gate actually dispatches.
builder.Services.AddWorkflowGate<LargeOrderGate, PriceOrder, OrderPrice, CheckoutSeed>(
    "large-order-gate",
    CheckoutJsonContext.Default.WorkflowGateLargeOrderGatePriceOrderOrderPriceCheckoutSeed,
    queue: "critical");

// The EF unit of work for the Transactional Enqueue demo — a no-op on the In-Memory store.
store.AddDbContext(builder.Services, builder.Configuration);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(swagger =>
    // Pre-fills the order-notification request body with a realistic example (no new package).
    swagger.SchemaFilter<OrderNotificationRequestExample>());

// The dashboard's Operator Actions (Phase 1) are antiforgery-protected POSTs; the pure
// middleware does not get Razor/Blazor antiforgery for free, so the host registers it.
builder.Services.AddAntiforgery();

var app = builder.Build();

app.Logger.LogInformation("BackWave sample starting on the {Store} store.", store);

// ── Startup schema bootstrap ─────────────────────────────────────────────────
// Relational stores create the business table before the store migrates (see StoreKind);
await store.EnsureDatabaseAsync(app.Services);

// Set the 'limited' Queue's cluster-wide Concurrency Limit (also triggers relational AutoMigrate).
// Through the Operator — like every operator action, it lands in the audit log with its actor.
await app.Services.GetRequiredService<BackWaveOperator>()
    .SetConcurrencyLimitAsync("limited", 1, actor: "startup");

// ── HTTP surface ─────────────────────────────────────────────────────────────
app.UseSwagger();
app.UseSwaggerUI();

// The identity stamped into every Operator Action's audit record (dashboard + /ops endpoints).
const string actor = "sample-operator";

// Write-capable dashboard. View defaults to allow; the four Operator Actions are default-deny
// (ADR 0010), so this dev sample opts into all of them to demo the controls. Production hosts
// would delegate each to their own authorization. The actions are antiforgery-protected, so
// the host registers antiforgery services (see AddAntiforgery above). The sample also opts into
// ViewSensitiveData (also default-deny, dev-permissive here) so the Job-detail payload card and
// the gated Failure Detail render (ExposeSensitiveData already defaults on).
app.UseBackWaveDashboard("/backwave", new BackWaveDashboardOptions
{
    AuthorizeView = _ => ValueTask.FromResult(true),
    AuthorizeRequeue = _ => ValueTask.FromResult(true),
    AuthorizeCancel = _ => ValueTask.FromResult(true),
    AuthorizePauseQueue = _ => ValueTask.FromResult(true),
    AuthorizeTriggerSchedule = _ => ValueTask.FromResult(true),
    AuthorizeViewSensitiveData = _ => ValueTask.FromResult(true),
    ResolveActor = _ => actor,
});

// BackWave Pro MCP endpoint (registered via backwave.AddMcp() above). An MCP client pointed at
// http://localhost:5283/backwave-mcp can list and call the full tool surface — reads out of the
// box, writes and workflow-cancel because the sample granted them in the AddMcp block above. Try
// `get_queue_depths` after seeding, or follow the "MCP server" walkthrough in README.md.
app.UseBackWaveProMcp();

app.MapGet("/", () => Results.Redirect("/swagger"))
    .ExcludeFromDescription();

// ── Demo seed (marketing screenshots) ────────────────────────────────────────
// One call fills every dashboard panel with a busy, production-shaped workload: a big Succeeded
// backlog, a sustained "Executing now" pool, a Scheduled future backlog, Dead-Lettered + Quarantined
// failures, Workflows across states, Recurring Schedules, tenant-tagged reports, and a Queue pinned
// at its Concurrency Limit. Terminals are retained (24h / 14d, no drain pump here) so it all stays on
// screen. Re-run it to top the in-flight pool back up before another shot.
var demo = app.MapGroup("/demo").WithTags("Demo seed");

demo.MapPost("/seed", async (
    BackWaveClient client, IJobStore store, BackWaveOperator op,
    int completed = 1000, int inFlight = 120, int scheduled = 200,
    int failures = 40, int quarantine = 12, int workflows = 24, int limited = 30) =>
    Results.Ok(await DemoSeed.RunAsync(
        client, store, op, actor, completed, inFlight, scheduled, failures, quarantine, workflows, limited)))
    .WithSummary("Seed a realistic, busy dashboard for marketing screenshots — enqueues a large, varied workload across every panel. Tune the counts via query params; re-run to top up the in-flight pool.");

// ── Enqueue scenarios ────────────────────────────────────────────────────────
var jobs = app.MapGroup("/jobs").WithTags("Enqueue");

jobs.MapPost("/enqueue", async (BackWaveClient client, string name) =>
{
    var id = await client.EnqueueAsync(new Greet(name, 0), dueTime: DateTimeOffset.UtcNow);
    return Results.Ok(new { jobId = id, queue = "critical" });
}).WithSummary("Immediate enqueue: a 'greet' on the 'critical' queue, due now.");

jobs.MapPost("/delayed", async (BackWaveClient client, string name, int seconds) =>
{
    var id = await client.EnqueueAsync(new Greet(name, 0), dueTime: DateTimeOffset.UtcNow.AddSeconds(seconds));
    return Results.Ok(new { jobId = id, dueInSeconds = seconds });
}).WithSummary("Delayed / scheduled enqueue: same job with a future due time.");

jobs.MapPost("/strict-burst", async (BackWaveClient client, int count, int delayMs = 1000) =>
{
    for (var i = 0; i < count; i++)
    {
        await client.EnqueueAsync(new Process($"bulk-{i}", delayMs), dueTime: DateTimeOffset.UtcNow);
        await client.EnqueueAsync(new Greet($"critical-{i}", delayMs), dueTime: DateTimeOffset.UtcNow);
    }

    return Results.Ok(new { enqueued = count * 2, delayMs, note = "Each job sleeps delayMs while running — watch the pool fill in 'Executing now', and every 'critical' drains before any 'bulk'." });
}).WithSummary("Strict dispatch: 'critical' preempts 'bulk' in the Strict Worker Group. delayMs holds each job in flight so concurrency is visible.");

jobs.MapPost("/weighted-burst", async (BackWaveClient client, int count, int delayMs = 1000) =>
{
    for (var i = 0; i < count; i++)
    {
        await client.EnqueueAsync(new WeightedWork($"high-{i}", delayMs), dueTime: DateTimeOffset.UtcNow, queue: "high");
        await client.EnqueueAsync(new WeightedWork($"low-{i}", delayMs), dueTime: DateTimeOffset.UtcNow, queue: "low");
    }

    return Results.Ok(new { enqueued = count * 2, delayMs, note = "Each job sleeps delayMs while running — 'high' runs ~6x as often as 'low' (smooth weighted round-robin)." });
}).WithSummary("Weighted dispatch: 6:1 smooth weighted round-robin across 'high' and 'low'. delayMs holds each job in flight so concurrency is visible.");

jobs.MapPost("/concurrency-burst", async (BackWaveClient client, int count) =>
{
    for (var i = 0; i < count; i++)
    {
        await client.EnqueueAsync(new LimitedWork($"slot-{i}"), dueTime: DateTimeOffset.UtcNow);
    }

    return Results.Ok(new { enqueued = count, note = "The 'limited' queue's Concurrency Limit is 1 — peak concurrency never exceeds it." });
}).WithSummary("Concurrency Limit: only one 'limited-work' runs at a time, cluster-wide.");

jobs.MapPost("/flaky", async (BackWaveClient client, string label) =>
{
    var id = await client.EnqueueAsync(new Flaky(label), dueTime: DateTimeOffset.UtcNow);
    return Results.Ok(new { jobId = id, note = "Always fails; after the Weighted group's 3 attempts it lands Dead-Lettered." });
}).WithSummary("Retries → Dead-Lettered: a job that always throws.");

jobs.MapPost("/order-notification", async (BackWaveClient client, OrderNotificationRequest body, bool fail = false) =>
{
    // Map the clean request DTO onto the generated payload; `?fail` drives the document's fail flag.
    var id = await client.EnqueueAsync(
        new OrderNotification(body.OrderRef, body.CustomerEmail, body.Channel, body.ItemCount, body.TotalAmount, fail),
        dueTime: DateTimeOffset.UtcNow);
    return Results.Ok(new
    {
        jobId = id,
        queue = "low",
        fail,
        note = $"On 'low' (Weighted group, 3 attempts). With ?fail=true the handler throws every attempt → Dead-Lettered; otherwise it Succeeds. Watch the console for the dummy 'slack-observer:' line on the terminal transition (Succeeded, or Dead-Lettered with ?fail=true) — it lifts orderRef + customerEmail from the payload body. Open /backwave/jobs/{id} for the JSON payload card, the transition timeline, and (with fail) the captured Failure Detail.",
    });
}).WithSummary("Example body → observability + observer: enqueue a structured 'order-notification' document, watch the dummy Slack Observer log the terminal transition (with payload body) to the console, then view its payload card, timeline, and (with ?fail=true) Failure Detail in the dashboard.");

jobs.MapPost("/tagged-report", async (BackWaveClient client, string tenant = "acme", decimal amount = 1500m, bool priority = false) =>
{
    // Enqueue-time Tags (ADR 0022): a Keyed 'tenant' dimension plus an optional 'priority' Label.
    // These union with the [Job]'s type-default Labels ({billing, report}) and the runtime Tags the
    // handler adds ({processed, amount-band=...}) — all set semantics, so no duplicates.
    var tags = JobTags.Empty.WithTag("tenant", tenant);
    if (priority)
    {
        tags = tags.WithLabel("priority");
    }

    var id = await client.EnqueueAsync(new TaggedReport(tenant, amount), dueTime: DateTimeOffset.UtcNow, tags: tags);
    return Results.Ok(new
    {
        jobId = id,
        queue = "bulk",
        tags = tags.Select(t => t.IsLabel ? t.Value : $"{t.Key}={t.Value}"),
        note = $"Tags from three sources union onto this one job: type-default Labels [billing, report], enqueue-time tenant={tenant}{(priority ? " + priority Label" : "")}, and runtime [processed, amount-band=...]. See them as pills at /backwave/jobs/{id}, filter via GET /monitor/tagged, or group via GET /monitor/facet.",
    });
}).WithSummary("Tags showcase: type-default + enqueue-time + runtime Tags (Labels and Keyed) all union onto one job.");

jobs.MapPost("/quarantine", async (IJobStore store, string? wireName) =>
{
    // Enqueue an unregistered Wire Name straight through the store. No handler can route it,
    // so the pump transitions it to Quarantined (a routing/decoding failure, not an execution one).
    var id = Guid.NewGuid();
    var result = await store.EnqueueAsync(
        new NewJob(id, wireName ?? "ghost-job", "{}"u8.ToArray(), "critical", DateTimeOffset.UtcNow),
        now: DateTimeOffset.UtcNow);
    return Results.Ok(new { jobId = id, enqueue = result.ToString(), note = "Unregistered Wire Name → Quarantined once claimed." });
}).WithSummary("Quarantine: enqueue an unregistered Wire Name directly through the store.");

// ── Dependencies ────────────────────────────────────────────────────────────
var dependencies = app.MapGroup("/dependencies").WithTags("Dependencies");

dependencies.MapPost("/on-success", async (BackWaveClient client, string name) =>
{
    var parent = await client.EnqueueAsync(new Greet($"parent-{name}", 0), dueTime: DateTimeOffset.UtcNow);
    var child = await client.EnqueueDependencyAsync(
        new Greet($"child-of-{name}", 0), parentId: parent, mode: DependencyMode.OnSuccess);
    return Results.Ok(new { parent, child, note = "Child runs only because the parent Succeeded." });
}).WithSummary("On-success dependency: child released when the parent Succeeds.");

dependencies.MapPost("/on-any-terminal", async (BackWaveClient client, string name) =>
{
    // Parent is a flaky job that Dead-Letters; an on-success child would Cancel, but
    // on-any-terminal still runs once the parent reaches any terminal state.
    var parent = await client.EnqueueAsync(new Flaky($"parent-{name}"), dueTime: DateTimeOffset.UtcNow);
    var child = await client.EnqueueDependencyAsync(
        new Greet($"after-{name}", 0), parentId: parent, mode: DependencyMode.OnAnyTerminal);
    return Results.Ok(new { parent, child, note = "Child runs once the (Dead-Lettered) parent reaches a terminal state." });
}).WithSummary("On-any-terminal dependency: child released whatever the parent's terminal state.");

// ── Workflows ─────────────────────────────────────────────────────────────────
// A Workflow (ADR 0023) is a named, identity-bearing group of jobs wired into a DAG by Dependency
// edges, enqueued atomically. Watch any of these render as a graph at /backwave/workflows/{id}.
var workflows = app.MapGroup("/workflows").WithTags("Workflows");

workflows.MapPost("/order-fulfillment", async (
    BackWaveClient client, string orderRef = "ORD-1001", decimal amount = 1299.00m, int itemCount = 3, bool fail = false) =>
{
    // Diamond + tail: validate fans out to two parallel branches, which fan back in to pack, then notify.
    //   validate ─┬─> charge ──┐
    //             └─> reserve ─┴─> pack ──> notify
    var id = await client.Workflow($"order-fulfillment {orderRef}")
        .Then(new ValidateOrder(orderRef))
        .Then(new ChargePayment(orderRef, amount, fail))                          // fan-out branch A of validate
        .Then(new ReserveInventory(orderRef, itemCount), after: [typeof(ValidateOrder)]) // fan-out branch B of validate
        .Then(new PackShipment(orderRef), after: [typeof(ChargePayment), typeof(ReserveInventory)]) // fan-in
        .Then(new OrderNotification(orderRef, "buyer@example.com", "email", itemCount, amount, false)) // tail on pack
        .EnqueueAsync();
    return Results.Ok(new
    {
        workflowId = id,
        fail,
        graph = $"/backwave/workflows/{id}",
        note = fail
            ? "?fail=true → 'charge' Dead-Letters; its on-success dependents 'pack' and 'notify' Cancel, and the Workflow projects Failed (failure dominates). Watch the graph badges flip."
            : "'validate' runs, then 'charge' and 'reserve' in parallel, then 'pack' once both Succeed, then 'notify' (which also trips the Slack Observer). Open the graph link.",
    });
}).WithSummary("Workflow (diamond + tail): an order-fulfillment DAG with fan-out, fan-in, and a terminal notify. ?fail=true shows failure-dominates + downstream Cancel.");

workflows.MapPost("/fan-out-fan-in", async (BackWaveClient client, string label = "demo") =>
{
    // Mirrors River's canonical workflow example: a → {b1, b2} → c (same job type per node, like
    // River's MyJobArgs{}; the structure is the lesson). delayMs holds each node briefly so the
    // parallel middle is visible in the graph.
    var id = await client.Workflow($"fan-out/fan-in {label}")
        .Then(new DiamondA($"a-{label}", 600))
        .Then(new DiamondB1($"b1-{label}", 600))                                  // child of a
        .Then(new DiamondB2($"b2-{label}", 600), after: [typeof(DiamondA)])       // also a child of a
        .Then(new DiamondC($"c-{label}", 600), after: [typeof(DiamondB1), typeof(DiamondB2)]) // fan-in
        .EnqueueAsync();
    return Results.Ok(new
    {
        workflowId = id,
        graph = $"/backwave/workflows/{id}",
        note = "River-style fan-out/fan-in: 'a' runs, then 'b1' and 'b2' in parallel, then 'c' after both complete.",
    });
}).WithSummary("Workflow (fan-out/fan-in): the canonical a → {b1, b2} → c shape, for direct comparison with River's example.");

workflows.MapPost("/job-output", async (BackWaveClient client, string datasetRef = "ds-2048") =>
{
    // Job Output (ADR 0026) — River's LoadDeps. A diamond where the fan-in PULLS the output of its
    // transitive Dependency ancestors (never injected into its args):
    //   ingest ─┬─> enrich ──┐
    //           └─> score ───┴─> publish        publish reads enrich + score (direct) AND ingest (transitive).
    var id = await client.Workflow($"job-output {datasetRef}")
        .Then(new Ingest(datasetRef))
        .Then(new Enrich(datasetRef))                                            // child of ingest
        .Then(new Score(datasetRef), after: [typeof(Ingest)])                   // also a child of ingest
        .Then(new Publish(datasetRef), after: [typeof(Enrich), typeof(Score)])  // fan-in
        .EnqueueAsync();
    return Results.Ok(new
    {
        workflowId = id,
        graph = $"/backwave/workflows/{id}",
        note = "'ingest' emits a DatasetSummary via SetOutput; 'enrich' and 'score' pull it and emit their own; "
            + "'publish' pulls BOTH direct parents AND — transitively — their shared grandparent 'ingest'. "
            + "Pull, never push: BackWave never injects a parent's output into a child. Watch the publish log line; "
            + "each stage's output is visible at /backwave/jobs/{jobId} behind the ViewSensitiveData permission.",
    });
}).WithSummary("Workflow (Job Output / River LoadDeps): a fan-in pulls the output of its transitive Dependency ancestors.");

workflows.MapPost("/checkout", async (
    BackWaveClient client, string orderRef = "ORD-9042", decimal amount = 1299.00m,
    int itemCount = 3, bool expedite = false) =>
{
    // Every Workflows v2 shape in one graph, so the dashboard graph view can be confirmed to render each:
    //   price-order ─┬─> reserve-stock ────────────────┐
    //                └─> notify-warehouse ─> confirm-pick ┴─> authorize-charge ─> [large-order-gate]
    //                                                          │(compensation)         ├─> express-ship ─┐
    //                                                          v                       └─> standard-ship ┴─> prepare-handoff
    //                                                    refund-charge                                          │
    //                             send-receipt <─ print-label <─ pack-parcel <──────────────────────────────────┘
    // PARALLEL fan-out + fan-in, a saga COMPENSATION side-branch, a seed-aware CONDITIONAL gate (cancels the
    // not-taken arm), an OnAnyTerminal converge past it, a spliced CHILD workflow, and typed Job Output.
    var cents = (int)Math.Round(amount * 100);
    var id = await client.Workflow(new CheckoutSeed(orderRef, expedite, ExpressThresholdCents: 100_000), name: $"checkout {orderRef}")
        .Then(new PriceOrder(orderRef, cents))
        .Parallel(
            WorkflowBranch.Step(new ReserveStock(orderRef, itemCount)),
            WorkflowBranch.Do(b => b.Then(new NotifyWarehouse(orderRef)).Then(new ConfirmPick(orderRef))))
        .Then(new AuthorizeCharge(orderRef, cents))
        .WithCompensation(new RefundCharge(orderRef))
        .If<LargeOrderGate, PriceOrder, OrderPrice, CheckoutSeed>(
            then: b => b.Then(new ExpressShip(orderRef)),
            otherwise: b => b.Then(new StandardShip(orderRef)))
        .Then(new PrepareHandoff(orderRef), mode: DependencyMode.OnAnyTerminal)
        .ThenWorkflow<FulfilmentWorkflow, FulfilmentSeed>(new FulfilmentSeed(orderRef))
        .Then(new SendReceipt(orderRef))
        .EnqueueAsync();
    var takenArm = (expedite || cents >= 100_000) ? "express-ship" : "standard-ship";
    return Results.Ok(new
    {
        workflowId = id,
        takenArm,
        graph = $"/backwave/workflows/{id}",
        note = "All Workflows v2 shapes in one graph: a PARALLEL fan-out (reserve-stock alongside "
             + "notify-warehouse → confirm-pick), a fan-in authorize-charge, a saga COMPENSATION side-branch "
             + "(refund-charge, which no-ops when the charge settled), a seed-aware CONDITIONAL large-order-gate "
             + "(cancels the not-taken shipping arm), an OnAnyTerminal converge at prepare-handoff, a spliced "
             + "CHILD workflow (pack-parcel → print-label), and a terminal send-receipt. The gate cancels one "
             + "arm, so the derived Workflow status is Cancelled even though every step that ran Succeeded - "
             + "read per-step state, not the rollup.",
    });
}).WithSummary("Workflow (Workflows v2 all-features): parallel + conditional + child splice + compensation + typed Job Output.");

// ── Workflows v2 escape hatches (delays & waits without .Delay / .WaitFor) ──────
// Workflows v2 ships no .Delay step and no .WaitFor step by design. These endpoints show the honest
// alternatives over the ordinary enqueue API - a fixed-floor delay, a completion-anchored delay, a
// poll-from-a-step wait, and an external-enqueue trigger. Guide: docs/workflows-v2-escape-hatches.md.
var escape = app.MapGroup("/workflows/escape-hatches").WithTags("Workflows v2 escape hatches");

escape.MapPost("/delay-fixed-floor", async (BackWaveClient client, string label = "demo", int seconds = 30) =>
{
    // Start-anchored (fixed) floor: the job cannot run before this instant, whatever else happens.
    var dueTime = DateTimeOffset.UtcNow.AddSeconds(seconds);
    var id = await client.EnqueueAsync(new Greet($"floored-{label}", 0), dueTime);
    return Results.Ok(new
    {
        jobId = id,
        dueTime,
        note = $"Fixed-floor delay: 'greet' is deferred and cannot run before {dueTime:O}. A plain EnqueueAsync(dueTime), no .Delay step needed.",
    });
}).WithSummary("Delay escape hatch (fixed floor): defer a step with EnqueueAsync(dueTime).");

escape.MapPost("/delay-after-completion", async (BackWaveClient client, string label = "demo", int cooldownSeconds = 30) =>
{
    // Completion-anchored: stage A runs now; when it finishes it self-schedules stage B at +cooldown.
    var id = await client.EnqueueAsync(new CooldownWarmup(label, cooldownSeconds), dueTime: DateTimeOffset.UtcNow);
    return Results.Ok(new
    {
        jobId = id,
        note = $"Completion-anchored delay: 'cooldown-warmup' runs now; on completion it enqueues 'cooldown-followup' due +{cooldownSeconds}s. The delay is measured from when A finished.",
    });
}).WithSummary("Delay escape hatch (completion-anchored): a step self-schedules the next at now + delay.");

escape.MapPost("/wait-poll", async (BackWaveClient client, string reference = "shipment-1", int maxPolls = 5) =>
{
    // Poll-from-a-step: the step re-enqueues itself on a backoff until its condition holds.
    var id = await client.EnqueueAsync(new PollExternal(reference, 1, maxPolls), dueTime: DateTimeOffset.UtcNow);
    return Results.Ok(new
    {
        jobId = id,
        reference,
        note = $"Polling '{reference}' up to {maxPolls} times on a backoff. Mark it ready via POST /workflows/escape-hatches/wait-poll/ready?reference={reference}, and the next poll continues.",
    });
}).WithSummary("Wait escape hatch (poll-from-a-step): a step self-re-enqueues until an external condition holds.");

escape.MapPost("/wait-poll/ready", (ExternalReadinessGate gate, string reference = "shipment-1") =>
{
    gate.MarkReady(reference);
    return Results.Ok(new { reference, note = "Marked ready. The next poll of the waiting step observes it and continues." });
}).WithSummary("Flip the external condition the poll-from-a-step wait is waiting on.");

escape.MapPost("/wait-external-enqueue", async (BackWaveClient client, string payload = "order.paid") =>
{
    // External-enqueue trigger: an out-of-band event enqueues the continuation directly. No poller and
    // no reserved slot - the waiting step exists only once the event fires.
    var id = await client.EnqueueAsync(new ProcessWebhook(payload), dueTime: DateTimeOffset.UtcNow);
    return Results.Ok(new
    {
        jobId = id,
        note = "External-enqueue trigger: the out-of-band event enqueued 'process-webhook' directly. That is the durable wait - no .WaitFor step, just an ordinary enqueue when the event arrives.",
    });
}).WithSummary("Wait escape hatch (external enqueue): an out-of-band event enqueues the continuation directly.");

// ── Recurring schedules ──────────────────────────────────────────────────────
var schedules = app.MapGroup("/schedules").WithTags("Recurring");

schedules.MapPost("/recurring", async (BackWaveClient client, string? id) =>
{
    var scheduleId = id ?? "heartbeat";
    await client.UpsertRecurringAsync(scheduleId, Cron.EveryMinute(), new Greet("scheduled", 0), queue: "critical");
    return Results.Ok(new { scheduleId, cron = Cron.EveryMinute().Canonical });
}).WithSummary("Recurring schedule: mint a 'greet' every minute.");

schedules.MapPost("/no-overlap", async (BackWaveClient client, string? id) =>
{
    var scheduleId = id ?? "no-overlap";
    await client.UpsertRecurringAsync(
        scheduleId, Cron.EveryMinute(), new Greet("no-overlap", 0), queue: "critical", noOverlap: true);
    return Results.Ok(new { scheduleId, noOverlap = true, note = "A tick is skipped (and recorded) while a prior instance is still live." });
}).WithSummary("No-Overlap recurring schedule.");

schedules.MapPost("/catch-up", async (BackWaveClient client, string? id) =>
{
    var scheduleId = id ?? "catch-up";
    await client.UpsertRecurringAsync(
        scheduleId, Cron.EveryMinute(), new Greet("catch-up", 0), queue: "critical", catchUp: CatchUpPolicy.Coalesce);
    return Results.Ok(new { scheduleId, catchUp = nameof(CatchUpPolicy.Coalesce), note = "Missed ticks coalesce into exactly one make-up run." });
}).WithSummary("Catch-Up (Coalesce) recurring schedule.");

schedules.MapDelete("/{id}", async (BackWaveClient client, string id) =>
{
    await client.RemoveRecurringAsync(id);
    return Results.Ok(new { removed = id });
}).WithSummary("Remove a recurring schedule.");

schedules.MapGet("/", async (BackWaveMonitor monitor) => Results.Ok(await monitor.ListSchedulesAsync()))
    .WithSummary("List every Recurring Schedule with cursor, next due tick, and skipped ticks.");

// ── Operator actions ─────────────────────────────────────────────────────────
var ops = app.MapGroup("/ops").WithTags("Operator Actions");

ops.MapPost("/jobs/{id:guid}/requeue", async (BackWaveOperator op, Guid id) =>
    Results.Ok(new { result = (await op.RequeueAsync(id, actor)).ToString() }))
    .WithSummary("Requeue a Dead-Lettered or Quarantined job.");

ops.MapPost("/jobs/{id:guid}/cancel", async (BackWaveOperator op, Guid id) =>
    Results.Ok(new { result = (await op.CancelJobAsync(id, actor)).ToString() }))
    .WithSummary("Cancel a job (cooperative if it is executing).");

ops.MapPost("/queues/{queue}/pause", async (BackWaveOperator op, string queue) =>
{
    await op.PauseQueueAsync(queue, actor);
    return Results.Ok(new { paused = queue });
}).WithSummary("Pause a queue cluster-wide.");

ops.MapPost("/queues/{queue}/resume", async (BackWaveOperator op, string queue) =>
{
    await op.ResumeQueueAsync(queue, actor);
    return Results.Ok(new { resumed = queue });
}).WithSummary("Resume a paused queue.");

ops.MapPost("/schedules/{id}/trigger", async (BackWaveOperator op, string id) =>
    Results.Ok(new { result = (await op.TriggerScheduleNowAsync(id, actor)).ToString() }))
    .WithSummary("Trigger a Recurring Schedule now (mint one instance immediately).");

ops.MapPost("/workflows/{id:guid}/cancel", async (BackWaveOperator op, Guid id) =>
    Results.Ok(new { result = (await op.CancelWorkflowAsync(id, actor)).ToString() }))
    .WithSummary("Cancel a whole Workflow — fans the per-job Cancel out over its non-terminal members (reuses the Cancel permission).");

// ── Monitor reads ────────────────────────────────────────────────────────────
var monitor = app.MapGroup("/monitor").WithTags("Monitor");

monitor.MapGet("/jobs/{id:guid}", async (BackWaveMonitor m, Guid id) =>
    await m.GetJobAsync(id) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound())
    .WithSummary("One job's snapshot.");

monitor.MapGet("/jobs", async (BackWaveMonitor m, JobState? state, string? queue) =>
    Results.Ok(await m.ListJobsAsync(new JobQuery { State = state, Queue = queue })))
    .WithSummary("List jobs, optionally filtered by state and/or queue.");

monitor.MapGet("/queues", async (BackWaveMonitor m) => Results.Ok(await m.GetQueueDepthsAsync()))
    .WithSummary("Queue depths: job counts by queue and state.");

monitor.MapGet("/workflows", async (BackWaveMonitor m) => Results.Ok(await m.ListWorkflowsAsync()))
    .WithSummary("List every Workflow (oldest first) with its derived status and member count.");

monitor.MapGet("/workflows/{id:guid}", async (BackWaveMonitor m, Guid id) =>
    await m.GetWorkflowAsync(id) is { } view ? Results.Ok(view) : Results.NotFound())
    .WithSummary("One Workflow's graph: members (as snapshots), the structural Dependency edges, and the derived status.");

monitor.MapGet("/tagged", async (BackWaveMonitor m, string? tenant, string? label) =>
{
    // Tag predicates (ADR 0022) are AND-ed: a job must satisfy every one. OR is out of scope —
    // a caller wanting OR runs two queries. HasKeyValue matches a Keyed Tag; HasLabel a bare Label.
    var predicates = new List<JobTagPredicate>();
    if (tenant is not null)
    {
        predicates.Add(JobTagPredicate.HasKeyValue("tenant", tenant));
    }

    if (label is not null)
    {
        predicates.Add(JobTagPredicate.HasLabel(label));
    }

    return Results.Ok(await m.ListJobsAsync(new JobQuery { TagPredicates = predicates }));
}).WithSummary("Filter jobs by Tags (AND-ed): ?tenant=acme (Keyed Tag) and/or ?label=billing (Label). Try the 'tagged-report' job first.");

monitor.MapGet("/facet", async (BackWaveMonitor m, string key = "") =>
    Results.Ok(await m.GetTagFacetAsync(key)))
    .WithSummary("Facet jobs by one tag dimension: ?key=tenant for per-tenant counts (or amount-band), or ?key= (empty) to facet Labels.");

// ── Transactional Enqueue ────────────────────────────────────────────────────
app.MapPost("/tx", async (IServiceProvider sp, BackWaveClient client, bool fail) =>
{
    if (!store.SupportsTransactionalEnqueue)
    {
        return Results.Conflict(new
        {
            error = "This store deployment does not support Transactional Enqueue.",
            guidance = "Use a co-resident relational store: BackWave:Store=Sqlite (a local file, no Docker), " +
                "or BackWave:Store=Postgres / SqlServer with `docker compose up -d` in this sample's folder, then retry. " +
                "BackWave:Store=SqliteDedicated keeps BackWave on its own file and deliberately forgoes Transactional Enqueue.",
        });
    }

    using var scope = sp.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();

    var rowId = Guid.NewGuid();
    await using var transaction = await db.Database.BeginTransactionAsync();

    db.BusinessRows.Add(new SampleBusinessRow { Id = rowId, Note = fail ? "to be rolled back" : "committed" });
    await db.SaveChangesAsync();

    // The job rides on the EF transaction: it commits or rolls back atomically with the row.
    var jobId = await client.EnqueueAsync(new TxFinalize(rowId), db, dueTime: DateTimeOffset.UtcNow);

    if (fail)
    {
        await transaction.RollbackAsync();
        return Results.Ok(new { rowId, jobId, committed = false, note = "Both the business row and the job were rolled back." });
    }

    await transaction.CommitAsync();
    return Results.Ok(new { rowId, jobId, committed = true, note = "The business row and the job committed atomically." });
}).WithTags("Transactional Enqueue")
  .WithSummary("Commit/roll back a business row and a job together (relational stores only).");

app.Run();
