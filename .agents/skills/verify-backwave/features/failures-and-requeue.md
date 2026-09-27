# Failures and Requeue

A job that fails every attempt goes to Dead-Lettered, and a job that the store refuses to run goes to Quarantined. The Failures page lists both, in two tabs. An operator chooses `Requeue` to give a failed job a new attempt. BackWave writes an audit row for the action, and the job runs again.

## Sub-features

- `failures-dead-lettered` lists the Dead-Lettered jobs with their count on the `Dead-Lettered` tab.
- `failures-quarantined` lists the Quarantined jobs on the `Quarantined` tab.
- `failures-requeue` requeues one job from its row, and opens the job detail page.
- `failures-requeue-from-jobs` requeues the same kind of job from its row on the Jobs list.
- `failures-overview` shows the failed jobs in the Overview "Needs attention" table and the `DEAD-LETTERED` count.

## How to get to it (user POV)

- Choose `Failures` in the sidebar. `Quarantined` is the second tab, at `/backwave/failures?tab=quarantine`.
- Choose `ALL FAILURES →` on the Overview "Needs attention" table.
- Choose `Jobs`, then filter `State` to `Dead-Lettered`. Each row has a `Requeue` button.

## Driving it with bwv

Preconditions:

- `bwv doctor` passes.
- No earlier recipe in this run used the names `flaky`, `flaky-2`, or `failures-*`.

- **Create a failure.** Enqueue a job that always fails. Run `bwv api POST '/jobs/flaky?label=proof' flaky`, then `bwv wait-job <jobId> DeadLettered`. The job reaches `DeadLettered` in about 3 s, after 3 attempts.
- **Open Failures.** Choose `Failures`. Run `bwv browser open /backwave/failures`, then `bwv shot failures-before`. The page has `heading "Failures"`, `link "Dead-Lettered1"`, `link "Quarantined0"`, and a row with the job id prefix, `flaky`, `low`, `Dead-Lettered`, `3`, and `button "Requeue"`.
- **Requeue.** Choose `Requeue` in the job's row. Run `bwv browser snapshot | grep -A12 'link "<first 8 chars>'` to find the button uid, then `bwv browser click @<uid>`. The browser lands on `/backwave/jobs/<id>`.
- **See the new attempts.** Wait for the job to fail again. Run `bwv wait-job <jobId> DeadLettered`, then `bwv browser open /backwave/jobs/<jobId>` and `bwv shot failures-after`. The Transition Log shows `14 RECORDED`, newest first. The first 7 rows end at `Dead-Lettered` attempt 3. Then the requeue adds `Scheduled` attempt 0 and 3 new attempts.
- **Requeue from Jobs.** Create a second failure and requeue it from the Jobs list. Run `bwv api POST '/jobs/flaky?label=two' flaky-2`, `bwv wait-job <jobId> DeadLettered`, and `bwv browser open '/backwave/jobs?state=Dead-Lettered'`. Click the `Requeue` button in that row. The browser lands on the job detail page.
- **Proof.** Read the audit and the job. Run `bwv sql failures-audit "select actor, action, target from backwave_operator_audit"` (`backwave.operator_audit` on Postgres). There is one row for each click, with actor `sample-operator`, action 1 (Requeue), and the job id. Run `bwv api GET /monitor/jobs/<jobId> failures-job`. The job is back in state 5 (Dead-Lettered), because the handler always fails.

## Gotchas

- A requeued `flaky` job fails again. Prove the requeue with the audit row and the new rows in the Transition Log, not with the final state.
- The `Requeue` redirect goes to the job detail page, not back to the Failures page.
- Run `wait-job` only after the click landed on the job page. Before the requeue, the job is already `DeadLettered`, so `wait-job` returns at once.
- A requeue sets the attempt back to 0. Each attempt gets its own `Failure Detail` row in the Transition Log.
- The sample has no endpoint that makes a Quarantined job on purpose. The `Quarantined` tab stays at 0 unless you seed with `bwv api POST /demo/seed seed`.
- `POST /ops/jobs/<id>/requeue` does the same action through the API. It is not a dashboard proof.
- The `Dead-Lettered` tab count updates only when the page loads again.
