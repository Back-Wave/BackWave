# Jobs and job detail

An operator finds a job on the Jobs page, filters the list by state, queue, wire name, or job id, and opens the job detail page. The detail page shows the job's fields, its JSON payload, its Transition Log, and for a failed job the Failure Detail. An operator can also cancel a job that is still Scheduled.

## Sub-features

- `jobs-list` lists jobs with job id, wire name, queue, state, attempt, due time, terminal time, tags, and actions.
- `jobs-filter` narrows the list with the `State`, `Queue`, `Wire Name`, `Job ID`, and `Rows` controls.
- `job-detail` shows the fields, the payload card, and the Transition Log of one job.
- `job-failure-detail` shows `TERMINAL CAUSE` and the exception text of a failed job.
- `job-cancel` cancels a Scheduled job from its row on the Jobs list.

## How to get to it (user POV)

- Choose `Jobs` in the sidebar, then a job id link in the `JOB` column.
- Choose a queue link on the Queues page. It opens the Jobs list with `?queue=<name>`.
- Choose a job id link on the Overview "Needs attention" table or on the Failures page.
- Choose `Open full job page` on a workflow member.
- Open `/backwave/jobs/<id>` from a link that the sample API returns.

## Driving it with bwv

Preconditions:

- `bwv doctor` passes.
- No earlier recipe in this run used the names `greet`, `later`, or `flaky`.

- **Enqueue a job.** Enqueue a greeting. Run `bwv api POST '/jobs/enqueue?name=Ada' greet`, then `bwv wait-job <jobId> Succeeded`. The response has `jobId` and `"queue": "critical"`, and the job reaches `Succeeded` in about 1 s.
- **Open the list.** Choose `Jobs`. Run `bwv browser open /backwave/jobs`. The snapshot has `heading "Jobs"`, the comboboxes `State`, `Queue`, `Wire Name`, `Rows`, the textbox `Job ID`, and a row with `link "<first 8 chars>…"` and `greet`.
- **Filter the list.** Filter by queue and state. Run `bwv browser open '/backwave/jobs?state=Succeeded&queue=critical'`. The `State` combobox shows `Succeeded`, the `Queue` combobox shows `critical`, and only matching rows show.
- **Open job detail.** Choose the job id link. Run `bwv browser click @<uid of the job link>`, then `bwv shot job-detail`. The page has `heading "Job <id>"`, `WIRE NAME greet`, `QUEUE critical`, `STATE Succeeded`, `ATTEMPT 1`, a `Payload` card with `"Name": "Ada"`, and `Transition Log` with `3 RECORDED` rows: `Scheduled`, `Leased`, `Succeeded`.
- **See a failure.** Enqueue a job that always fails. Run `bwv api POST '/jobs/flaky?label=x' flaky`, `bwv wait-job <jobId> DeadLettered`, then `bwv browser open /backwave/jobs/<jobId>`. The page shows `STATE Dead-Lettered`, `ATTEMPT 3`, `TERMINAL CAUSE flaky 'x' always fails (attempt 3)`, and a `Failure Detail` section with `System.InvalidOperationException`.
- **Cancel a scheduled job.** Enqueue a job due in 10 minutes. Run `bwv api POST '/jobs/delayed?name=Later&seconds=600' later`, then `bwv browser open '/backwave/jobs?state=Scheduled'`. Find the `Cancel` button in that job's row and run `bwv browser click @<uid>`. The browser lands on `/backwave/jobs/<id>` with `STATE Cancelled`.
- **Proof.** Read the store. Run `bwv sql jobs "select job_id, state, attempt from backwave_jobs"` (`backwave.jobs` on Postgres), and `bwv sql cancel-audit "select actor, action, target from backwave_operator_audit"`. The states are 3 (Succeeded), 5 (Dead-Lettered), and 4 (Cancelled). The audit table has one row with actor `sample-operator`, action 0 (Cancel), and the job id as target.

## Gotchas

- The list shows the first 8 characters of the job id and `…`. Match the link on that prefix.
- The `Wire Name` combobox lists every registered job type, also types with no jobs yet.
- Only the Jobs list rows have the `Cancel` and `Requeue` buttons. The job detail page has no action button.
- A Scheduled job with a due time in the past runs at once. Use `seconds=600` so that the job stays Scheduled while you click.
- The Transition Log lists the newest transition first.
