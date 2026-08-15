# BackWave.Dashboard

The monitoring dashboard for [BackWave](https://backwave.app). Mount it on a route and watch jobs,
queues, failures, schedules, and observers in real time. It's server-rendered, with the design system
inlined at render time, so there are zero static assets to host or version.

```csharp
using BackWave.Dashboard;

var app = builder.Build();

app.UseBackWaveDashboard("/backwave", new BackWaveDashboardOptions
{
    // View defaults to allow; the Operator Actions are default-deny, so authorize each to your app's rules.
    AuthorizeView          = _ => ValueTask.FromResult(true),
    AuthorizeRequeue       = ctx => ValueTask.FromResult(ctx.User.IsInRole("ops")),
    AuthorizeCancel        = ctx => ValueTask.FromResult(ctx.User.IsInRole("ops")),
    AuthorizePauseQueue    = ctx => ValueTask.FromResult(ctx.User.IsInRole("ops")),
    AuthorizeTriggerSchedule = ctx => ValueTask.FromResult(ctx.User.IsInRole("ops")),
    ResolveActor           = ctx => ctx.User.Identity?.Name ?? "anonymous",
});
```

Then browse to `/backwave`.

## What you get

- **Live views** of jobs, queues, "executing now", failures with captured detail, schedules, and
  transition observers.
- **Operator Actions**: requeue, cancel, pause/resume a queue, trigger a schedule. Each is default-deny
  and antiforgery-protected, and every action is written to an audit log with its actor.
- **Job detail**: payload, tags, the transition timeline, and (when exposed) failure detail.

For the Workflows tab and graph view, add **BackWave.Pro.Dashboard**. Full documentation:
https://backwave.app
