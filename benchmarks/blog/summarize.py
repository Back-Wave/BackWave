"""Summarizes a run-blog.sh results directory into Markdown tables, one per workload.

Each cell is the median across rounds of each round's single measured run, with the min and max across
rounds. A round that failed, whose work audit failed, or that ran with a diagnostic knob engaged is left
out of the figures and counted next to the system name.

A paced round is carried when the producers delivered at least 98% of the offered rate and the system
completed jobs at that rate too, over the whole window from the first enqueue to the last completion. A
system that keeps up finishes its last job a few milliseconds after the last enqueue, so the floor leaves
room for that. A backlog stretches the window, so it shows up as a completion rate below the floor.
"""

import json
import statistics
import sys
from pathlib import Path

PACED_FLOOR = 0.98
SYSTEMS = {
    "postgres": "BackWave",
    "postgres-4g": "BackWave (4 groups)",
    "hangfire-postgres": "Hangfire",
    "quartz-postgres": "Quartz.NET",
    "tickerq-postgres": "TickerQ",
    "jobmaster-postgres": "JobMaster",
}
WORKLOADS = ["drain-noop", "drain-10ms", "max-sustained", "paced-50", "paced-500"]


def load_cells(root: Path) -> dict[tuple[str, str], list[dict]]:
    cells: dict[tuple[str, str], list[dict]] = {}
    for path in sorted(root.glob("round-*/*/*.json")):
        result = json.loads(path.read_text())
        result["_idle"] = float(path.with_suffix(".idle-before").read_text())
        cells.setdefault((path.parent.name, path.stem), []).append(result)
    for path in sorted(root.glob("round-*/*/*.failed")):
        cells.setdefault((path.parent.name, path.stem), []).append({"_failed": True})
    return cells


def spread(values: list[float], fmt: str) -> str:
    median = statistics.median(values)
    if len(values) == 1:
        return format(median, fmt)
    return f"{format(median, fmt)} [{format(min(values), fmt)}-{format(max(values), fmt)}]"


def clean(run: dict) -> bool:
    return (not run.get("_failed") and not run.get("Diagnostic")
            and all(not a["Problems"] for a in run.get("WorkAudits", [])))


def slots(run: dict) -> int:
    dials = run["TuningDials"]
    return int(dials["worker-pool-size"]) * int(dials.get("worker-groups", 1))


def carried(run: dict, offered: float) -> bool:
    floor = PACED_FLOOR * offered
    completing = run["ThroughputJobsPerSecond"]["Median"]
    arrivals = [a["ArrivalJobsPerSecond"] or 0 for a in run.get("WorkAudits", [])]
    return all(a >= floor for a in arrivals) and completing >= floor


def row(target: str, all_runs: list[dict]) -> str:
    runs = [r for r in all_runs if clean(r)]
    name = SYSTEMS.get(target, target)
    excluded = len(all_runs) - len(runs)
    if excluded:
        name += f" ({excluded} of {len(all_runs)} rounds left out: failed, failed audit, or diagnostic)"
    if not runs:
        return f"| {name} | " + " | ".join(["-"] * 10) + f" | {audit(all_runs)} |"
    workload = runs[0]["Workload"]
    offered = workload["SustainedRatePerSecond"]
    if workload["Arrival"] == "Sustained" and offered < 50000:
        missed = [r for r in runs if not carried(r, offered)]
        if missed:
            name += f" (not carried in {len(missed)} of {len(runs)} rounds)"
    return (
        f"| {name} | {spread([r['ThroughputJobsPerSecond']['Median'] for r in runs], ',.0f')} "
        f"| {spread([r['EndToEndP50Ms']['Median'] for r in runs], ',.1f')} "
        f"| {spread([r['EndToEndP99Ms']['Median'] for r in runs], ',.1f')} "
        f"| {spread([r['EnqueueP50Ms']['Median'] for r in runs], ',.2f')} "
        f"| {max(r['Resources']['PeakConnections'] for r in runs)} "
        f"| {spread([r['Resources']['CpuPercent'] for r in runs], ',.1f')} "
        f"| {'/'.join(str(s) for s in sorted({slots(r) for r in runs}))} "
        f"| {workload['JobCount']:,} | {len(runs)} | {min(r['_idle'] for r in runs):.0f} "
        f"| {audit(all_runs)} |"
    )


def audit(runs: list[dict]) -> str:
    audits = [a for r in runs for a in r.get("WorkAudits", [])]
    crashed = sum(1 for r in runs if r.get("_failed"))
    failed = [a for a in audits if a["Problems"]]
    if not audits and not crashed:
        return "n/a"
    if not failed and not crashed:
        return "PASS"
    parts = []
    if failed:
        parts.append(f"FAIL {len(failed)}/{len(audits)}: {failed[0]['Problems'][0]}")
    if crashed:
        parts.append(f"{crashed} round(s) failed, see the .log")
    return "; ".join(parts)


def main() -> None:
    root = Path(sys.argv[1])
    cells = load_cells(root)
    first = next(r for runs in cells.values() for r in runs if not r.get("_failed"))["Manifest"]
    print(f"Local mode, indicative only. {first['Os']}, {first['ProcessArchitecture']}, "
          f"{first['ProcessorCount']} cores, {first['DotnetVersion']}, PostgreSQL {first['DbVersion']}.\n")
    for workload in WORKLOADS:
        targets = [t for t in SYSTEMS if (workload, t) in cells]
        if not targets:
            continue
        print(f"## {workload}\n")
        print("| System | Throughput (jobs/s) | e2e p50 (ms) | e2e p99 (ms) | enqueue p50 (ms) "
              "| Peak DB conns | CPU % | Slots | Jobs | Rounds | Min idle % before | Work audit |")
        print("|---|---|---|---|---|---|---|---|---|---|---|---|")
        for target in targets:
            print(row(target, cells[(workload, target)]))
        print()


if __name__ == "__main__":
    main()
