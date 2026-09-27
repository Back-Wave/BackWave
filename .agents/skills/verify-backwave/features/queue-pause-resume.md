# Queue pause and resume

An operator pauses a queue from the Queues page. A paused queue keeps its jobs but no worker claims them, and the Overview shows the queue under "Paused Queues". Resume starts the claims again, and the waiting jobs run. BackWave writes an audit row for each action.

## Sub-features

- `queue-list` shows each queue with its state (`Claiming` or `Paused`), the in-use slots and the cap, the depth, and one action button.
- `queue-pause` pauses one queue and redirects to the Overview.
- `queue-paused-stalls` keeps new jobs on a paused queue in `Scheduled`.
- `queue-resume` resumes the queue, and the waiting jobs run.
- `queue-audit` records one audit row for each pause and each resume.

## How to get to it (user POV)

- Choose `Queues` in the sidebar, then `Pause` or `Resume` in the queue's row.
- Choose `<n> QUEUES →` above the Overview "Queue depths" table. The count is the number of queues that hold jobs.
- The Overview card `PAUSED QUEUES` shows the count and the names of the paused queues.

## Driving it with bwv

Preconditions:

- `bwv doctor` passes.
- The `critical` queue is `Claiming`. A new run starts with every queue claiming.
- The `critical` queue holds at least one job. On a new run, the Queues page lists only `limited`. The first step adds a job.
- No earlier recipe in this run used the names `pause-*`, `warmup`, `stalled`, or `resumed`.

- **Put a job on critical.** Enqueue a greeting. Run `bwv api POST '/jobs/enqueue?name=Warmup' warmup`, then `bwv wait-job <jobId> Succeeded`. The job reaches `Succeeded` in about 1 s.
- **Open Queues.** Choose `Queues`. Run `bwv browser open /backwave/queues`, then `bwv shot pause-before`. The page has `heading "Queues"` and the rows `critical` and `limited`, each `Claiming` with `button "Pause"`.
- **Pause critical.** Choose `Pause` in the `critical` row. Run `uid=$(bwv browser snapshot | grep -A6 'link "critical"' | grep 'button "Pause"' | grep -o 'g[0-9]*:[0-9_]*')`, then `bwv browser click "@$uid"` at once. The browser lands on the Overview.
- **See the paused state.** Capture the Overview. Run `bwv shot pause-overview`. The `PAUSED QUEUES` card shows `1` and `critical`.
- **Stall a job.** Enqueue a job on the paused queue. Run `bwv api POST '/jobs/enqueue?name=Paused' stalled`, then `bwv api GET /monitor/jobs/<jobId> pause-stalled-job`. After 5 s or more, the job still has `"state": 0` (Scheduled).
- **Resume critical.** Choose `Resume` in the `critical` row. Run `bwv browser open /backwave/queues` and `bwv shot pause-queues-paused`. The `critical` row is `Paused` with `button "Resume"`. Take a new snapshot, read the uid of `button "Resume"` after `link "critical"`, and click it. The browser lands on the Overview, and `PAUSED QUEUES` shows `0`.
- **See the job run.** Wait for the stalled job. Run `bwv wait-job <jobId> Succeeded`. The job reaches `Succeeded` within a few seconds of the resume.
- **Proof.** Read the audit. Run `bwv sql pause-audit "select actor, action, target from backwave_operator_audit order by sequence"` (`backwave.operator_audit` on Postgres). It has two rows with actor `sample-operator` and target `critical`: action 3 (Pause), then action 4 (Resume). The first row is `startup`, `5`, `limited`. Run `bwv shot pause-after` on `/backwave/queues`. The `critical` row is `Claiming` again.

## Gotchas

- Every row has a button with the same name. Find the button after the queue's own link, or read the form actions (`/backwave/queues/<queue>/pause`) with `bwv browser eval`.
- Pause and Resume redirect to the Overview, not back to the Queues page.
- The Overview "Queue depths" table and the Queues page list only the queues that hold jobs. The exception is `limited`, which has a limit from startup. The sample also has `bulk` and `low`.
- `bwv shot` takes a snapshot, so it makes the uid from the earlier snapshot stale. A click on that uid fails with `STALE_REF` and the page stays on Queues. Take the snapshot and click at once.
- A paused `critical` queue also stalls workflow members on `critical`, for example `validate-order`. Resume the queue before you drive the Workflows recipe on the same run.
- `limited` has the cap `1` and an audit row from the actor `startup` (action 5). Filter the audit query by actor or target if the run has that row.
