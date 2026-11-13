# Pumps

A Pump is one worker group in one process. The Pumps page lists every Pump in the cluster with its state, its fault, and the time of its last report. Each running host writes its own row and updates it on start and every 30 s. The sample runs two worker groups, so the page shows two Pumps: `strict-priority` and `weighted-fair`.

## Sub-features

- `pumps-list` lists each Pump with its worker id, state, fault, and last report time.
- `pumps-running` shows `Running` and no fault for a healthy worker group.

## How to get to it (user POV)

- Choose `Pumps` in the sidebar.
- Open `/backwave/pumps`.

## Driving it with bwv

Preconditions:

- `bwv doctor` passes.
- The sample runs its two worker groups. The page has rows at once; the feature needs no job.

- **Open Pumps.** Choose `Pumps`. Run `bwv browser open /backwave/pumps`, then `bwv shot pumps`. The page has `heading "Pumps"`, the columns `PUMP`, `STATE`, `FAULT`, and `LAST REPORT`, and two rows. Each row names a worker group (`strict-priority`, `weighted-fair`), shows `Running`, shows `-` for the fault, and shows a recent report time.
- **Proof.** Read the store. Run `bwv sql pumps "select worker_group, state, fault_type from backwave_pumps"` (`backwave.pumps` on Postgres). There are two rows with `state` 0 (Running) and an empty `fault_type`.

## Gotchas

- The state codes are `Running` 0, `Degraded` 1, `Halted` 2, `Stopping` 3, and `Stopped` 4.
- The sample reaches only `Running`. The `Degraded` and `Halted` badges need an unclassified store fault, and the sample has no endpoint that injects one.
- `Stale` is not a stored state. It is the reader's verdict when a Pump misses more than three reports.
- The last report time refreshes every 30 s. A row can read a few seconds old and still be healthy.
- The Oracle store keeps the same data in the `backwave` schema. Query it as `SELECT worker_group, state, fault_type FROM pumps`.
