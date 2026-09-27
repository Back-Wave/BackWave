---
name: verify-backwave
description: Launch the real BackWave Sample.Api (the sample host with the BackWave dashboard at /backwave) on a private port with its own SQLite file or Postgres container, create real jobs through the sample's HTTP endpoints, drive the dashboard in a private headless Chrome, capture screenshot, accessibility, API, and database evidence, and clean up. Use to prove a BackWave change works end to end, to reproduce a bug that a user or operator would see, or whenever you need to see or screenshot the dashboard running.
---

# Verify BackWave

Everything goes through one helper, `.agents/skills/verify-backwave/scripts/bwv`. Run it from anywhere in the worktree. The examples below use this alias:

```bash
bwv() { "$(git rev-parse --show-toplevel)/.agents/skills/verify-backwave/scripts/bwv" "$@"; }
```

**Primary surface:** the BackWave dashboard at `/backwave` on `samples/BackWave.Sample.Api`, driven with `chrome-devtools-axi`. The jobs on the dashboard are real: the sample's own HTTP endpoints enqueue them, and the sample's real workers run them.

**Secondary surfaces:** these surfaces have no drive recipe in the feature map. `bwv api` reaches the first two on the same run.

- The Sample HTTP API: `/jobs/*`, `/workflows/*`, `/schedules/*`, `/ops/*`, `/monitor/*`, `/tx`, and Swagger at `/swagger`.
- The Pro MCP server at `/backwave-mcp` on the same host.
- `samples/BackWave.Demo`: a SQLite host with the dashboard at `/`, deployed to demo.backwave.app.
- The library API itself (`BackWaveClient`, `BackWaveOperator`, `BackWaveMonitor`) and the test projects under `tests/`.

Before you drive a feature, read [`features/README.md`](features/README.md) and the matching feature file. The map lists every entry point. A proof that uses one convenient entry point is incomplete when the map lists others.

## Isolation contract

Each run owns these resources. All of them use the same run id (`YYYYMMDD-HHMMSS-<pid>`):

| Resource | Name | How it stays private |
|---|---|---|
| Sample.Api process | `dotnet <scratch>/app/BackWave.Sample.Api.dll` | A private copy of the build output. `bwv` identifies it by its PID and by this path, never by a process name. |
| HTTP port | `http://127.0.0.1:<port>` | A free port from the OS. `bwv` never uses the default `5283` from `appsettings.json`. |
| Scratch directory | `${TMPDIR}/bw-verify-<run>/` | Holds the app copy and the SQLite file `backwave.db`. |
| Postgres (only with `--store postgres`) | container `backwave-verify-<run>`, label `backwave.verify.run=<run>` | `postgres:17-alpine` on a free loopback port. It never uses the compose containers on `5398` or `14331`. |
| Browser | `CHROME_DEVTOOLS_AXI_SESSION=bw-verify-<run>` | A private bridge and an isolated headless Chrome profile. `bwv` never uses the `default` session. |
| Evidence | `.verify-evidence/<run>/` at the worktree root | Gitignored. Cleanup never deletes it. |

Other agents can run tests, `docker compose`, or their own `bwv` run at the same time. Never stop a process, a container, or a browser session that this run did not start. Do not run `docker compose down` for this skill: the compose file owns the shared test databases.

Parallel runs in one worktree share `bin/` and `obj/`. For this reason, `bwv launch` builds under a lock at `.verify-evidence/.build.lock`, and then copies the output to the run's scratch directory. A later build by another run cannot change the files under a live app.

## Launch

```bash
bwv launch                    # SQLite co-resident store (the default)
bwv launch --store postgres   # a private Postgres container
bwv launch --store inmemory   # no database, fastest start
```

This command does these steps:

1. Records the run in `.verify-evidence/<run>/run.env` and makes it the current run. It records each resource name before it creates the resource.
2. Runs `dotnet build` on `samples/BackWave.Sample.Api` under the build lock, and copies `bin/Debug/net10.0` to the scratch directory.
3. For `--store postgres`, starts the run's container and waits until Postgres accepts TCP connections.
4. Starts the app copy with `--urls http://127.0.0.1:<port>` and the store flags. It sets `DOTNET_hostBuilder__reloadConfigOnChange=false` (see [Gotchas](#gotchas)).
5. Waits until `/backwave` returns 200, then makes sure that `app.log` names the expected store.

Ready means that the last line is `bwv: READY run=<run> store=<store> pid=<pid> url=http://127.0.0.1:<port>/backwave`. A warm launch takes 6-8 s. A cold build takes about one minute.

The Sample.Api enqueues nothing at startup. The dashboard is empty until you call a `/jobs/*` or `/workflows/*` endpoint. For a busy dashboard, call `bwv api POST /demo/seed seed` (about 1,500 jobs).

After you change product code, run `bwv cleanup`, then `bwv launch`. A running app never rebuilds itself.

Teardown is `bwv cleanup` (see [Cleanup](#cleanup)).

## Doctor

```bash
bwv doctor
```

This is a read-only check. Run it after launch, before every proof, and first when anything looks wrong. Every line must read `ok`:

- The run is `active` (not cleaned).
- The recorded PID still runs this run's `<scratch>/app/BackWave.Sample.Api.dll`.
- That PID holds the listening socket on the run's port.
- `/backwave` serves the page with the title `Overview · BackWave`.
- `app.log` names the store: `SqliteCoResident`, `Postgres`, or `InMemory`.
- The build matches the worktree. The fingerprint covers the committed files, the uncommitted edits, and the untracked files. It ignores docs, `*.md`, `.agents`, and `.claude`. If you edit product code after the launch, this check fails.
- The store exists: the SQLite file, or the Postgres container with the run's label.
- The state of the run's browser session.

The exit code is not zero when a check fails. Do not drive a run that fails doctor. Correct the cause, or run `bwv cleanup` and then `bwv launch`.

## Drive

The dashboard is server-rendered HTML with live refresh over Server-Sent Events (SSE). Operator actions are HTML forms that POST with an antiforgery token and redirect with a 303. For this reason, the only valid way to do an operator action is a click in the browser. Do not POST to `/backwave/...` with curl.

Drive the browser with `bwv browser`. It passes every argument to `chrome-devtools-axi` in the run's own session. For `open` and `newpage`, a path that starts with `/` expands to the run's base URL:

```bash
bwv api POST '/jobs/flaky?label=proof' flaky    # arrange: a real job that dead-letters
bwv browser open /backwave/failures              # prints the page snapshot with uids
bwv shot failures-before                         # evidence of the start state
bwv browser snapshot | grep -B2 -A12 'link "<first 8 chars of the job id>'   # find the row's Requeue button uid
bwv browser click @<uid>                         # the real click on "Requeue"
bwv shot job-after-requeue                       # the 303 lands on the job detail page
```

Rules for handles:

- A `uid` (for example `@g3:3_42`) is valid only for the snapshot that printed it. Take a new `snapshot` after each navigation or click, and read the uid from it.
- `bwv shot` also takes a snapshot, so it makes all earlier uids stale. A click on a stale uid fails with `STALE_REF` and the page does not change. Take the snapshot, read the uid, and click at once. Do not put a `shot` between them:

  ```bash
  uid=$(bwv browser snapshot | grep -A6 'link "critical"' | grep 'button "Pause"' | grep -o 'g[0-9]*:[0-9_]*')
  bwv browser click "@$uid"
  ```

- Do not hide the output of `click`. Read it, and make sure that it has no error.
- Find an element by its accessible name in the snapshot: headings (`heading "Failures"`), links (`link "critical"`), buttons (`button "Requeue"`), and comboboxes (`combobox "State"`).
- Many rows have a button with the same name, for example one `Pause` button per queue. Find the row's link first (`link "critical"`), then use the button that comes after it in the snapshot.
- To list the form actions of a page without a click, run `bwv browser eval "() => [...document.querySelectorAll('form')].map(f => f.getAttribute('action'))"`. The actions name the target: `/backwave/queues/critical/pause`, `/backwave/jobs/<id>/requeue`, and `/backwave/jobs/<id>/cancel`.
- Navigation links and filter links are plain URLs. You can `open` their URL directly, for example `/backwave/jobs?tk=tenant&tv=acme`. This is the same request as a click on the link.

Dashboard pages: `/backwave` (Overview), `/backwave/executing`, `/backwave/jobs`, `/backwave/jobs/<id>`, `/backwave/queues`, `/backwave/failures` (`?tab=quarantine`), `/backwave/workflows`, `/backwave/workflows/<id>`, `/backwave/observers`, and `/backwave/schedules`.

To wait for a job to reach a state, run `bwv wait-job <jobId> <State> [timeout-s]`. The states are `Scheduled`, `AwaitingParent`, `Leased`, `Succeeded`, `Cancelled`, `DeadLettered`, and `Quarantined`.

## Evidence

Everything goes to `.verify-evidence/<run>/`:

| Path | Content |
|---|---|
| `api/<name>.json`, `api/requests.log` | `bwv api METHOD /path [name] [json]`: the response body, and one log line per call with the HTTP status |
| `api/job-<id>-<State>.json` | `bwv wait-job`: the job as `/monitor/jobs/<id>` returned it in that state |
| `ui/<name>.png`, `ui/<name>.txt` | `bwv shot <name>`: the screenshot and the accessibility snapshot of the current page |
| `db/<name>.txt` | `bwv sql <name> "<query>"`: a read-only query on the run's own store |
| `db/final-backwave.db` | SQLite runs only: the database file as it was when cleanup stopped the app |
| `app.log` | The app's console output: ASP.NET Core logs and OpenTelemetry console export |
| `run.env`, `build.log`, `cleanup.log` | What ran, and what cleanup removed |

`bwv api`, `bwv shot`, and `bwv sql` never overwrite a file. Give each capture a new name.

Proof standards:

- **Use the real user path.** Create jobs through the sample's HTTP endpoints, as a client application does. Do operator actions with a click on the dashboard. Do not write rows to the database, and do not add test-only endpoints.
- **Capture the action and the resulting state.** Take a `shot` before the key click and after it. The shot after the click must show the page that the 303 redirect opened.
- **Verify side effects on the other side of the boundary.** After an operator action, read the store with `bwv sql`. For example, read the new row in `backwave_operator_audit` (`backwave.operator_audit` on Postgres), and the job's `state` and `attempt` in `backwave_jobs`. Read the job again with `bwv api GET /monitor/jobs/<id>`. When the feature logs, quote the `app.log` line.
- **Use mocks only at existing production boundaries.** The sample has no mocks. Its Slack observer writes a `slack-observer:` line to `app.log` and sends nothing. Do not replace a store, a worker, or a clock to make a proof pass.
- **Observe what a dry run skips.** A 303 response or a green toast does not prove the action. Make sure that the stored state changed: the audit row exists, the job state changed, and a paused queue no longer claims jobs.

Keep evidence out of git. `.verify-evidence/` is in `.gitignore`. Never force-add it.

## Cleanup

```bash
bwv cleanup                  # the current run
BWV_RUN=<run> bwv cleanup    # a run that is not current (see: bwv runs)
```

Cleanup does these steps, in this order:

1. Stops the run's browser session. An open dashboard page holds an SSE request, and the host waits for open requests before it stops.
2. Sends SIGTERM to the app only if the recorded PID still runs this run's app path. After 20 s, it sends SIGKILL. If the PID now runs a different command, cleanup prints `REFUSED` and does not stop it.
3. Removes the container and its volumes only if the container has the label `backwave.verify.run=<run>`. Otherwise it prints `REFUSED`.
4. Copies the SQLite file to `db/final-backwave.db`, then deletes the scratch directory `${TMPDIR}/bw-verify-<run>/`.
5. Sets the run to `cleaned` in `run.env`, writes `cleanup.log`, and prints the evidence path and its file count.

Cleanup never stops anything by a process name. If a launch died before it recorded the PID, cleanup finds the app by the run's own app path. That path holds the run id.

Run cleanup after every failed iteration too. `bwv runs` lists every run in this worktree with its status and whether its app is live. Clean every run that is still `active` when you finish.

## Helpers

| Helper | Invocation |
|---|---|
| `scripts/bwv` | `bwv launch [--store sqlite\|postgres\|inmemory] \| doctor \| url [path] \| api <METHOD> <path> [name] [json] \| wait-job <jobId> <State> [timeout-s] \| browser <args...> \| shot <name> \| sql <name> <query> \| cleanup \| runs`. Run `bwv` with no arguments to see the usage. |
| `chrome-devtools-axi` | Through `bwv browser` only, so that the run's session is always set. Run `chrome-devtools-axi --help` for the full command list. |
| Swagger UI | `bwv url /swagger` prints the URL. The page lists every sample endpoint and its parameters. |

## Gotchas

- **A launch that hangs before `READY` with an empty `app.log`** can be a stalled `sync()`. The appsettings FileSystemWatcher calls `sync()` on macOS. A stuck Time Machine backup to a network volume blocks `sync()` for all processes. `bwv` sets `DOTNET_hostBuilder__reloadConfigOnChange=false` to prevent this. If you start the sample yourself, set it too.
- **The first `bwv browser` call takes 20-40 s.** The bridge starts `npx chrome-devtools-mcp@latest`. `bwv` sets `CHROME_DEVTOOLS_AXI_BRIDGE_TIMEOUT_MS=120000`. The default deadline of 30 s fails on a cold start.
- **`app.log` grows by about 800 KB per minute.** The OpenTelemetry console exporter writes a span for each idle poll. To find a log line, `grep` for the message text or for `LogRecord.FormattedMessage`.
- **Every page shows the Pro banner** "BackWave Pro is running without a license key." It is a `status` region at the top of `main`. It is correct behavior, not a fault.
- **`/jobs/tagged-report` needs `priority=true`**, not `priority=high`. The parameter is a bool, and a wrong value returns 400.
- **`/jobs/flaky` dead-letters after 3 attempts in about 3 s.** It runs in the `Weighted` group on the `low` queue.
- **The Overview counts only queues that hold jobs.** The Queues page also lists `limited` (concurrency limit 1, set at startup by the actor `startup`).
- **SQL Server is not wired into `bwv`.** The sample supports `--BackWave:Store=SqlServer`, but `bwv` does not start a private SQL Server container. Do not point a run at the compose container on port `14331`: other test runs share it.
- **`sqlite3 -readonly` fails on this WAL database.** `bwv sql` uses `PRAGMA query_only=1` instead.
- **macOS has no `timeout` command.** Use `bwv wait-job` or a loop with `sleep` to wait.
