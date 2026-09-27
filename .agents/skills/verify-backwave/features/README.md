# BackWave verification map

This directory is the maintained source for verifying the user-facing behavior of BackWave, as an operator sees it on the dashboard. Read the index before you drive the dashboard. Then use the matching feature file as the recipe. Every command below is `bwv`, the helper in `../scripts/bwv` (see `../SKILL.md`).

## Baseline preconditions

- `bwv launch` printed `READY`, and `bwv doctor` passes every check on the run that you drive.
- The run's Sample.Api starts with no jobs. Each recipe creates its own jobs through the sample's HTTP endpoints with `bwv api`.
- The run's store is its own SQLite file (the default) or its own Postgres container (`--store postgres`). The queues are `critical`, `bulk`, `low`, and `limited` (concurrency limit 1).
- The operator actor on the dashboard and on `/ops/*` is `sample-operator`. The startup limit on `limited` has the actor `startup`.
- Never drive an app, a container, or a browser session that this run did not start.

## Driving conventions

- Arrange state through the sample's HTTP API (`bwv api POST /jobs/...`). Do every operator action with a click on the dashboard.
- Open a dashboard page with `bwv browser open /backwave/...`. The command prints the page snapshot with uids.
- Read each uid from the latest snapshot. A uid is not valid after a navigation or a click.
- Find elements by their accessible name in the snapshot: `heading "Queues"`, `link "critical"`, `button "Pause"`, `combobox "State"`. When many rows have a button with the same name, find the row's link first, then use the button that comes after it.
- Filter and navigation links are plain URLs. `bwv browser open <url>` on the link's URL is the same request as a click on it.
- Use `bwv wait-job <jobId> <State>` to wait for a job. Do not use a fixed `sleep`.
- A run keeps its state until cleanup. If a recipe needs a clean state, use a new run.

## Proof and skip reporting

- Capture the user action and the resulting state: `bwv shot <name>` before the key click and after it.
- UI proof is the `ui/<name>.png` screenshot and its `ui/<name>.txt` snapshot. The snapshot must show the page heading.
- For an operator action, also read the store with `bwv sql` into `db/<name>.txt`, and read the job or queue with `bwv api GET /monitor/...`. The page alone does not prove the action.
- Report every artifact with the run id, the feature ID, and the entry point, for example `.verify-evidence/<run>/ui/pause-after.png` for `queue-pause` from the Queues page.
- If you did not reach an entry point, report the command, its output, and the unmet precondition. Do not report it as verified through a different entry point.
- `bwv cleanup` keeps all evidence. Name the evidence path in the report.

## Feature entry contract

Each feature file starts with an H1 title and one paragraph that describes the user-visible behavior. It then uses exactly four H2 sections in this order.

1. `Sub-features` lists short IDs with one line for each behavior.
2. `How to get to it (user POV)` lists every user entry point.
3. `Driving it with bwv` starts with `Preconditions:` and uses labeled bullets. Each bullet pairs a user action with an exact `bwv` command and the observable result.
4. `Gotchas` lists traps that can waste or invalidate a verification run.

Keep implementation details out of the map. Name only user paths, stable handles, required state, commands, and observable proof.

## Features

- [Jobs and job detail](./jobs-and-job-detail.md) covers the Jobs list and its filters, the job detail page (payload, Transition Log, Failure Detail), and Cancel of a scheduled job.
- [Failures and Requeue](./failures-and-requeue.md) covers the Dead-Lettered and Quarantined tabs, Requeue from the Failures page and from the Jobs list, and the audit row.
- [Queue pause and resume](./queue-pause-resume.md) covers Pause and Resume on the Queues page, the Overview "Paused Queues" count, the stalled claims, and the audit rows.
- [Workflows](./workflows.md) covers the Workflows list, the member graph of `order-fulfillment`, member inspection, and a failed workflow.
- [Tag filter](./tag-filter.md) covers the tag pills on jobs, the Label and key-value filters, the combined filter, and the "Top labels" facet.

Secondary surfaces have no recipe yet: the Sample HTTP API and Swagger as a surface of their own, the MCP server at `/backwave-mcp`, the `BackWave.Demo` host, Recurring Schedules, Observers, and Executing now. Report them as not verified with this skill.
