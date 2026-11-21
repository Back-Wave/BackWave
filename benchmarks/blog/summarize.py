"""Summarizes a run-blog.sh results directory into Markdown tables, one per workload.

Each cell is the median across rounds of each round's single measured run, with the min and max across
rounds. A round whose work audit failed, or that ran with a diagnostic knob engaged, is left out of the
figures and counted next to the system name. A paced cell that delivered less than 98% of its offered rate
is marked as not carried, because its latency then measures a backlog and not the system at that load.
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
    return cells


def spread(values: list[float], fmt: str) -> str:
    median = statistics.median(values)
    if len(values) == 1:
        return format(median, fmt)
    return f"{format(median, fmt)} [{format(min(values), fmt)}-{format(max(values), fmt)}]"


def clean(run: dict) -> bool:
    return not run.get("Diagnostic") and all(not a["Problems"] for a in run.get("WorkAudits", []))


def row(target: str, all_runs: list[dict]) -> str:
    runs = [r for r in all_runs if clean(r)]
    name = SYSTEMS.get(target, target)
    excluded = len(all_runs) - len(runs)
    if excluded:
        name += f" ({excluded} of {len(all_runs)} rounds left out: failed audit or diagnostic)"
    if not runs:
        return f"| {name} | " + " | ".join(["-"] * 9) + f" | {audit(all_runs)} |"
    throughput = [r["ThroughputJobsPerSecond"]["Median"] for r in runs]
    workload = runs[0]["Workload"]
    offered = workload["SustainedRatePerSecond"]
    paced = workload["Arrival"] == "Sustained" and offered < 50000
    carried = [t >= PACED_FLOOR * offered for t in throughput] if paced else []
    status = ""
    if paced and not all(carried):
        status = f" (not carried in {carried.count(False)} of {len(carried)} rounds)"
    return (
        f"| {name}{status} | {spread(throughput, ',.0f')} "
        f"| {spread([r['EndToEndP50Ms']['Median'] for r in runs], ',.1f')} "
        f"| {spread([r['EndToEndP99Ms']['Median'] for r in runs], ',.1f')} "
        f"| {spread([r['EnqueueP50Ms']['Median'] for r in runs], ',.2f')} "
        f"| {max(r['Resources']['PeakConnections'] for r in runs)} "
        f"| {spread([r['Resources']['CpuPercent'] for r in runs], ',.1f')} "
        f"| {workload['JobCount']:,} | {len(runs)} | {min(r['_idle'] for r in runs):.0f} "
        f"| {audit(all_runs)} |"
    )


def audit(runs: list[dict]) -> str:
    audits = [a for r in runs for a in r.get("WorkAudits", [])]
    if not audits:
        return "n/a"
    failed = [a for a in audits if a["Problems"]]
    return "PASS" if not failed else f"FAIL {len(failed)}/{len(audits)}: {failed[0]['Problems'][0]}"


def main() -> None:
    root = Path(sys.argv[1])
    cells = load_cells(root)
    first = next(iter(cells.values()))[0]["Manifest"]
    print(f"Local mode, indicative only. {first['Os']}, {first['ProcessArchitecture']}, "
          f"{first['ProcessorCount']} cores, {first['DotnetVersion']}, PostgreSQL {first['DbVersion']}.")
    slots = sorted({r["TuningDials"].get("worker-pool-size") for (_, t), rs in cells.items() if t != "postgres-4g" for r in rs})
    print(f"Run slots per system: {', '.join(s for s in slots if s)}.\n")
    for workload in WORKLOADS:
        targets = [t for t in SYSTEMS if (workload, t) in cells]
        if not targets:
            continue
        print(f"## {workload}\n")
        print("| System | Throughput (jobs/s) | e2e p50 (ms) | e2e p99 (ms) | enqueue p50 (ms) "
              "| Peak DB conns | CPU % | Jobs | Rounds | Min idle % before | Work audit |")
        print("|---|---|---|---|---|---|---|---|---|---|---|")
        for target in targets:
            print(row(target, cells[(workload, target)]))
        print()


if __name__ == "__main__":
    main()
