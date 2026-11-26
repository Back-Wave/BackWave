#!/usr/bin/env bash
# Usage: tests/formal/commit-horizon/check.sh [--quick | --deep | --config <name>.cfg]
#   --quick   skips the liveness runs.
#   --deep    also runs Deep.cfg, the real protocol at larger bounds (about 2.5 minutes more).
#   --config  runs only the named config, for example Liveness.cfg or Deep.cfg.
set -uo pipefail

usage() {
  echo "usage: $0 [--quick | --deep | --config <name>.cfg]" >&2
  exit 2
}
mode="${1:-}"
only=""
case "$#:$mode" in
  0: | 1:--quick | 1:--deep) ;;
  2:--config) [[ -n "$2" ]] || usage; only="$(basename "$2")" ;;
  *) usage ;;
esac

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
tlc="$here/../tlc.sh"
spec="$here/CommitHorizon.tla"
failures="$here/.failures"
logs="$(mktemp -d)"
trap 'rm -rf "$logs" "$here"/.cursor-only-*.cfg' EXIT
failed=0
ran=0
rm -rf "$failures"

run() {
  local cfg="$1" want="$2" name log start status got states depth result
  name="$(basename "$cfg")"; name="${name#.cursor-only-}"
  [[ -z "$only" || "$name" == "$only" ]] || return 0
  ran=$((ran + 1))
  log="$logs/$(basename "$cfg").log"
  start=$(date +%s)
  TLC_LOG="$log" "$tlc" "$spec" -config "$cfg"
  status=$?
  got="$(grep -oE 'Invariant [A-Za-z]+ is violated|Temporal properties were violated|Deadlock reached' "$log" | head -1)"
  states="$(grep -oE '[0-9,]+ distinct states found' "$log" | tail -1)"
  depth="$(grep -cE '^State [0-9]+:' "$log")"
  if [[ "$want" == pass ]]; then
    [[ $status -eq 0 ]] && result=ok || result=FAIL
  else
    [[ $status -eq 12 && "$got" == "Invariant $want is violated" ]] && result=ok || result=FAIL
  fi
  local found=""
  [[ -n "$got" ]] && found=", got \"$got\", trace $depth states"
  printf '%-4s %-28s want %s, exit %s, %s%s, %ss\n' \
    "$result" "$name" "$want" "$status" "$states" "$found" "$(( $(date +%s) - start ))"
  if [[ $result == FAIL ]]; then
    failed=1
    mkdir -p "$failures"
    cp "$log" "$failures/$(basename "$cfg").log"
    echo "     full TLC output: $failures/$(basename "$cfg").log"
    if [[ $status -ne 0 && $status -ne 12 ]]; then
      tail -n 20 "$log" | sed 's/^/     | /'
    fi
  fi
}

cursor_only() {
  local cfg="$1" tmp="$here/.cursor-only-$(basename "$1")"
  sed 's/^INVARIANTS .*/INVARIANTS TypeOK CursorSafe/' "$cfg" >"$tmp"
  run "$tmp" CursorSafe
}

cd "$here"
run CommitHorizon.cfg pass
run LockFirst.cfg pass
run RetryFiltered.cfg pass
[[ "$mode" == --quick ]] || run Liveness.cfg pass
[[ "$mode" == --quick ]] || run LivenessLockFirst.cfg pass
[[ "$mode" == --deep || "$only" == Deep.cfg ]] && run Deep.cfg pass
for m in ProbePreviousOnly ProbeCurrentOnly PAfterProbes SequenceCache DrawBeforeRegister; do
  run "Mutant$m.cfg" HorizonSafe
  cursor_only "Mutant$m.cfg"
done
run MutantNoHorizon.cfg CursorSafe

if [[ $ran -eq 0 ]]; then
  echo "no config named $only in $here" >&2
  exit 2
fi
if [[ $failed -ne 0 ]]; then
  echo "check FAILED"
  exit 1
fi
echo "check passed"
