# Pumps

A Pump is the worker loop of one Worker Group in one process. The Pumps page lists the last status that each Pump's own process wrote: its state, its fault, and the time of its last report. The list has one row per Worker Group per node, so it covers the whole cluster. The sample runs two groups, `strict-priority` and `weighted-fair`, so a single-node run shows two Pumps.

## Sub-features

- `pumps-list` shows one row per Worker Group Pump, with its worker id, state, fault, and last report time.
- `pumps-state` shows the Pump state as a badge: `Running`, `Degraded`, `Stopping`, `Halted`, `Stopped`, or `Stale`.
- `pumps-fault` names the exception type or the broken invariant of a `Degraded` or `Halted` Pump.

## How to get to it (user POV)

- Choose `Pumps` in the sidebar. The page is `/backwave/pumps`.

## Driving it with bwv

Preconditions:

- `bwv doctor` passes.
- The run has been up for a few seconds, so each Pump wrote its first status.

- **Open Pumps.** Choose `Pumps`. Run `bwv browser open /backwave/pumps`, then `bwv shot pumps`. The page has `heading "Pumps"`, the column headers `PUMP`, `STATE`, `FAULT`, and `LAST REPORT`, and one row for each worker group: `strict-priority` and `weighted-fair`. Each row reads `Running` with the fault `-`.
- **Read the store.** Run `bwv sql pumps "select worker_group, state from backwave_pumps"` (`backwave.pumps` on Postgres). Each worker group has one row, with `state` 0 (Running).
- **See a Degraded Pump.** This step needs a Postgres or Oracle run. Stop and start the run's own container (see the SKILL.md store-outage gotcha). After the group rides out the fault, its row reads `Degraded`, and the `FAULT` cell names the exception type.

## Gotchas

- A Pump writes its status at startup and then every 30 seconds. A new run shows its Pumps within a few seconds.
- `Stale` and `Stopped` are the page's own verdict against the current time, not states that a Pump writes. A Pump that misses more than three reports reads `Stale`.
- There is no `/monitor/pumps` HTTP endpoint. Read the Pump rows with `bwv sql`, not `bwv api`.
- The `PUMP` cell shows the worker group and the full worker id (`<host>:<group>:<guid>`). Match on the worker group.
- A SQLite or in-memory run has no container to stop, so the `Degraded` and `Halted` states need a Postgres or Oracle run.
- A node that runs an older BackWave version writes no status, so it does not appear here.
