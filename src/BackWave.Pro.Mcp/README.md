# BackWave.Pro.Mcp

A [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server over your
[BackWave](https://backwave.app) jobs, mounted inside your own ASP.NET Core host — no separate
service to run. AI agents connect over MCP streamable HTTP and use tools that read your queues,
jobs, schedules, workflows, observers, and audit trail — and, only where you grant it, act on them:
requeue, cancel, pause/resume a queue, trigger a schedule, set a concurrency limit.

> **Licensing.** Free to use for organizations under **$1M USD in annual revenue**; a license is
> required above that. Like the rest of Pro, it soft-fails: an unlicensed host still serves the full
> tool surface and logs a one-line notice. See the included EULA for terms.

Two lines: register inside the `AddBackWave` block, then mount on the pipeline:

```csharp
using BackWave.Pro.Mcp;

builder.Services.AddBackWave(bw =>
{
    bw.UseStore(/* ... */).UseJobs(BackWaveJobs.Module);
    bw.AddMcp(); // the MCP server, next to your worker groups
});

var app = builder.Build();
app.UseBackWaveProMcp(); // serves MCP at /backwave-mcp
```

Point any MCP client at the endpoint, e.g.
`claude mcp add --transport http backwave https://yourapp.example.com/backwave-mcp`.

## Permissions: delegation, never ownership

Every permission is a callback you supply — BackWave never sees your users or roles; it asks your
host whether the current request may do a thing. The defaults are safe:

- **Viewing is allowed** (`AuthorizeView`) — the read-only tool surface works the moment you mount
  it, which keeps local development frictionless. Production hosts delegate this to their own
  authorization.
- **Every write action is denied** — each must be consciously granted, one action at a time:
  `AuthorizeRequeue`, `AuthorizeCancel` (also gates cancelling a whole workflow),
  `AuthorizePauseQueue`, `AuthorizeTriggerSchedule`, `AuthorizeSetConcurrencyLimit`. A tool whose
  permission is denied for the current request is hidden from the client's tool list, and calling
  it directly returns a tool-execution error.
- **Sensitive data is denied** — raw job payload and output content sits behind a triple lock:
  `AuthorizeViewSensitiveData` (default deny) AND `ExposeSensitiveData` (a host-level switch) AND
  the absence of an environment kill-switch — setting `BACKWAVE_MCP_DISABLE_SENSITIVE_DATA` to
  `1`/`true`/`yes`/`on` on the host forces payload exposure off no matter what the code grants.

Every write action lands in BackWave's audit trail with the actor `ResolveActor` supplies
(default: the authenticated user name, falling back to `"mcp"`).

```csharp
bw.AddMcp(mcp =>
{
    mcp.AuthorizeView    = ctx => ValueTask.FromResult(ctx.User.IsInRole("ops"));
    mcp.AuthorizeRequeue = ctx => ValueTask.FromResult(ctx.User.IsInRole("ops-admin"));
    mcp.ResolveActor     = ctx => ctx.User.Identity?.Name ?? "mcp";
});
```

## Authenticate the mount with your host's own auth

MCP clients are not browsers — there are no cookies or antiforgery tokens here. Protect the
endpoint the way you protect any API: put your own bearer/API-key authentication in front of the
mount, and let the permission callbacks judge the authenticated request. Both mounting shapes
compose with that:

```csharp
// Middleware shape: mount after auth so the permission callbacks see an authenticated user.
app.UseAuthentication();
app.UseAuthorization();
app.UseBackWaveProMcp("/backwave-mcp");
```

```csharp
// Endpoint shape: chain endpoint policies — RequireAuthorization, rate limiting — directly.
app.MapBackWaveProMcp("/backwave-mcp")
   .RequireAuthorization("ops-policy");
```

An MCP client then authenticates like any API caller — for example
`claude mcp add --transport http backwave https://yourapp.example.com/backwave-mcp --header "Authorization: Bearer <token>"` —
and `ResolveActor` can stamp that identity into the audit trail.

## Writing a raw client? Responses are SSE-framed

The endpoint speaks MCP streamable HTTP, stateless: every JSON-RPC request is its own POST (send
`Accept: application/json, text/event-stream`), any node of a multi-node deployment serves any
request, and the server frames each response as Server-Sent Events (`Content-Type:
text/event-stream`, the JSON-RPC response on a `data:` line) — even for single-shot POSTs.
Standard MCP SDKs and clients handle this transparently; only a hand-rolled HTTP client needs to
parse the framing.

Requires **BackWave.Hosting** (the host wiring) and **BackWave.Pro** — register
`AddBackWavePro(...)` alongside, as with any Pro package. Full documentation:
https://backwave.app
