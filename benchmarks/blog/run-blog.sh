#!/usr/bin/env bash
#
# The local battery behind the community blog post: BackWave, Hangfire, Quartz.NET, TickerQ, JobMaster,
# MassTransit's SQL transport, and Acta, all on the same PostgreSQL, in local mode. Local numbers are indicative,
# never official.
#
# Each round runs every (workload, target) cell once, in a target order that rotates per round, so drift in
# machine load or thermals spreads across all systems instead of landing on one. summarize.py takes the
# median across rounds.
#
# Workloads:
#   drain-noop      preload N noop jobs, time the drain             (headline throughput ceiling)
#   drain-10ms      preload N jobs with a 10 ms handler             (overhead against real work)
#   max-sustained   unpaced enqueue and processing at the same time (which side binds is per system)
#   paced-50        50 jobs/s offered for 30 s                      (latency at a light load)
#   paced-500       500 jobs/s offered for 30 s                     (JobMaster cannot carry it, so it is left out)
#
# Before each cell the script waits until the machine is at least IDLE_MIN percent idle over a 3 s sample, and
# records that figure next to the result.
#
# A cell that fails leaves a .failed marker, which summarize.py counts. A rerun skips cells that have a result
# or a marker; RETRY_FAILED=1 runs the marked cells again. Each start appends to run-manifest.txt.
#
# Tunables (env): ROUNDS (5), JOBS (20000), JOBMASTER_JOBS (5000), IDLE_MIN (85), RETRY_FAILED (0), OUT_DIR,
# TARGETS, WORKLOADS.

set -uo pipefail

ROUNDS="${ROUNDS:-5}"
JOBS="${JOBS:-20000}"
JOBMASTER_JOBS="${JOBMASTER_JOBS:-5000}"
IDLE_MIN="${IDLE_MIN:-85}"
TARGETS="${TARGETS:-postgres postgres-4g hangfire-postgres quartz-postgres tickerq-postgres jobmaster-postgres masstransit-postgres acta-postgres}"
WORKLOADS="${WORKLOADS:-drain-noop drain-10ms max-sustained paced-50 paced-500}"
RETRY_FAILED="${RETRY_FAILED:-0}"
OUT_DIR="${OUT_DIR:-benchmarks/results/blog-$(date +%Y%m%d-%H%M)}"

# Every competitor runs as many slots as BackWave's default pool, four per core and at least 8, and the
# four-group BackWave cell splits that same total. JobMaster rounds its slots up to a multiple of 5. The
# summary prints each system's slot count.
SLOTS=$(( $(sysctl -n hw.ncpu) * 4 )); (( SLOTS < 8 )) && SLOTS=8
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/../BackWave.Benchmarks"

mkdir -p "$OUT_DIR"
dotnet build -c Release "$PROJECT" -v quiet -nologo || exit 1

idle_percent() { top -l 2 -n 0 -s 3 | awk '/CPU usage/ {v=$7} END {sub("%","",v); print v}'; }

wait_for_idle() {
  local idle
  while idle="$(idle_percent)"; awk -v i="$idle" -v m="$IDLE_MIN" 'BEGIN{exit !(i < m)}'; do
    echo "    idle ${idle}% < ${IDLE_MIN}%, waiting" >&2
    sleep 30
  done
  echo "$idle"
}

# postgres-4g is BackWave with its slots split over four Worker Groups.
target_args() {
  case "$1" in
    postgres-4g) echo "--target postgres --worker-groups 4 --pool-size $(( SLOTS / 4 ))" ;;
    *)           echo "--target $1" ;;
  esac
}

workload_args() {
  local workload="$1" jobs="$2"
  case "$workload" in
    drain-noop)    echo "--arrival drain --delay-ms 0 --jobs $jobs" ;;
    drain-10ms)    echo "--arrival drain --delay-ms 10 --jobs $jobs" ;;
    max-sustained) echo "--arrival sustained --delay-ms 0 --jobs $jobs --rate 50000 --producers 32" ;;
    paced-50)      echo "--arrival sustained --delay-ms 0 --jobs 1500 --rate 50 --producers 8" ;;
    paced-500)     echo "--arrival sustained --delay-ms 0 --jobs 15000 --rate 500 --producers 32" ;;
    *) echo "unknown workload $workload" >&2; return 1 ;;
  esac
}

{
  echo "start $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "commit $(git -C "$SCRIPT_DIR" rev-parse HEAD), $(git -C "$SCRIPT_DIR" status --porcelain | wc -l | tr -d ' ') uncommitted changes"
  echo "host $(sysctl -n machdep.cpu.brand_string), $(sysctl -n hw.ncpu) cores, $(( $(sysctl -n hw.memsize) / 1073741824 )) GiB"
  echo "rounds $ROUNDS jobs $JOBS jobmaster_jobs $JOBMASTER_JOBS idle_min $IDLE_MIN slots $SLOTS"
  docker ps --format '{{.Names}} {{.Image}}'
} >> "$OUT_DIR/run-manifest.txt"

read -r -a target_list <<< "$TARGETS"
count=${#target_list[@]}

for round in $(seq 1 "$ROUNDS"); do
  for workload in $WORKLOADS; do
    for i in $(seq 0 $((count - 1))); do
      target="${target_list[$(( (i + round - 1) % count ))]}"
      if [[ "$target" == jobmaster-postgres && "$workload" == paced-500 ]]; then continue; fi
      jobs="$JOBS"
      if [[ "$target" == jobmaster-postgres ]]; then jobs="$JOBMASTER_JOBS"; fi

      cell="$OUT_DIR/round-$round/$workload/$target"
      mkdir -p "$(dirname "$cell")"
      if [[ -f "$cell.json" ]]; then continue; fi
      if [[ -f "$cell.failed" && "$RETRY_FAILED" != 1 ]]; then continue; fi

      idle="$(wait_for_idle)"
      echo ">>> round $round $workload $target (idle ${idle}%)"
      echo "$idle" > "$cell.idle-before"
      if ! dotnet run -c Release --no-build --project "$PROJECT" -- \
          $(target_args "$target") --mode local --warmup 1 --runs 1 \
          $(workload_args "$workload" "$jobs") --out "$cell.json" > "$cell.log" 2>&1; then
        echo "    FAILED, see $cell.log"
        rm -f "$cell.json"
        touch "$cell.failed"
      else
        rm -f "$cell.failed"
      fi
    done
  done
done

python3 "$SCRIPT_DIR/summarize.py" "$OUT_DIR" | tee "$OUT_DIR/summary.md"
