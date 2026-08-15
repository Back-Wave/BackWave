#!/usr/bin/env bash
#
# The scripted official Benchmark Harness battery (ADR 0027, bench-0142).
#
# Runs the full published matrix — BackWave vs Hangfire, on PostgreSQL and SQL Server, across the noop-drain,
# noop-sustained, and 10 ms-anchor workloads — plus the BackWave-only scale-out curve, and writes one
# self-labelled JSON per cell (each carrying its own environment manifest and `publishable` flag).
#
# Run this on the PINNED NATIVE-x86-64 instance with the database co-located on loopback. Official mode refuses
# to run on any non-native-x86-64 host (e.g. Apple Silicon / Rosetta), so a laptop can only produce indicative
# `local`-mode numbers — which must never be transcribed into docs/performance/.
#
# Connection strings default to the local docker-compose databases; override them for the pinned instance:
#   export BACKWAVE_POSTGRES_DSN=...           BACKWAVE_SQLSERVER_DSN=...
#   export BACKWAVE_HANGFIRE_POSTGRES_DSN=...  BACKWAVE_HANGFIRE_SQLSERVER_DSN=...
#
# Tunables (env overrides):
#   MODE         official | local            (default: official)
#   JOBS         noop job count              (default: 100000)
#   ANCHOR_JOBS  10 ms-anchor job count      (default: 20000)
#   RATE         sustained enqueue rate/sec  (default: 50000)
#   PRODUCERS    concurrent producer tasks   (default: 8; raise if sustained is producer-bound)
#   WARMUP       warmup runs discarded       (default: 1)
#   RUNS         measured runs               (default: 5)
#   NODES        scale-out node counts       (default: 1,2,4,8)
#   OUT_DIR      results directory           (default: benchmarks/results)

set -euo pipefail

MODE="${MODE:-official}"
JOBS="${JOBS:-100000}"
ANCHOR_JOBS="${ANCHOR_JOBS:-20000}"
RATE="${RATE:-50000}"
PRODUCERS="${PRODUCERS:-8}"
WARMUP="${WARMUP:-1}"
RUNS="${RUNS:-5}"
NODES="${NODES:-1,2,4,8}"
OUT_DIR="${OUT_DIR:-benchmarks/results}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/BackWave.Benchmarks"

mkdir -p "$OUT_DIR"

# bench <target> <label> <args...> — one harness invocation into a labelled JSON cell.
bench() {
  local target="$1" label="$2"; shift 2
  echo ">>> $label ($target)"
  dotnet run -c Release --project "$PROJECT" -- \
    --target "$target" --mode "$MODE" --warmup "$WARMUP" --runs "$RUNS" \
    --out "$OUT_DIR/${target}-${label}.json" "$@"
}

# The published matrix: noop drain (headline ceiling), noop sustained (contention), and the 10 ms anchor.
for target in backwave_pg backwave_mssql hangfire_pg hangfire_mssql; do
  case "$target" in
    backwave_pg)     t=postgres ;;
    backwave_mssql)  t=sqlserver ;;
    hangfire_pg)     t=hangfire-postgres ;;
    hangfire_mssql)  t=hangfire-sqlserver ;;
  esac
  # Sustained runs fan the producer across $PRODUCERS tasks so the arrival side outpaces the cluster's drain
  # capacity — a single producer would cap sustained throughput at its own enqueue rate (bench-0137).
  bench "$t" noop-drain         --arrival drain     --delay-ms 0  --jobs "$JOBS"
  bench "$t" noop-sustained     --arrival sustained --delay-ms 0  --jobs "$JOBS" --rate "$RATE" --producers "$PRODUCERS"
  bench "$t" anchor10-drain     --arrival drain     --delay-ms 10 --jobs "$ANCHOR_JOBS"
  bench "$t" anchor10-sustained --arrival sustained --delay-ms 10 --jobs "$ANCHOR_JOBS" --rate "$RATE" --producers "$PRODUCERS"
done

# BackWave-only scale-out curve (no competitor — single-node is the comparison baseline).
echo ">>> scale-out curve ($NODES nodes)"
dotnet run -c Release --project "$PROJECT" -- \
  --scale-out "$NODES" --mode "$MODE" --jobs "$JOBS" --delay-ms 0 \
  --out "$OUT_DIR/scaleout.json"

echo
echo "Done. Results in $OUT_DIR/ — transcribe only cells with \"Publishable\": true into docs/performance/."
