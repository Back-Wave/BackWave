#!/usr/bin/env bash
# Model-checks LeaseFencing.tla on each config and compares the result with
# the config's first line, "\* expect: pass" or "\* expect: <rule>".
# Usage: tests/formal/lease-fencing/check.sh             the adapters at the sync commit
#        tests/formal/lease-fencing/check.sh --mutants   the planted breaks in mutants/
#        tests/formal/lease-fencing/check.sh --all       both
# Exits 0 only when every config ends as it expects.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
tlc="$here/../tlc.sh"

case "${1:-}" in
  "") cfgs=("$here"/*.cfg) ;;
  --mutants) cfgs=("$here"/mutants/*.cfg) ;;
  --all) cfgs=("$here"/*.cfg "$here"/mutants/*.cfg) ;;
  *) echo "usage: $0 [--mutants | --all]" >&2; exit 2 ;;
esac

logs="$(mktemp -d "${TMPDIR:-/tmp}/lease-fencing.XXXXXX")"
mismatches=0
printf '%-72s %-20s %-20s %10s %5s\n' config expect got distinct secs
for cfg in "${cfgs[@]}"; do
  rel="${cfg#"$here"/}"
  expect="$(sed -n 's/^\\\* expect: *//p' "$cfg" | head -n 1)"
  log="$logs/${rel//\//_}.out"
  start=$SECONDS
  set +e
  "$tlc" "$here/LeaseFencing.tla" -config "$rel" >"$log" 2>&1
  code=$?
  set -e
  secs=$((SECONDS - start))
  if [[ $code -eq 0 ]] && grep -q 'No error has been found' "$log"; then
    got=pass
  elif [[ $code -eq 12 ]]; then
    got="$(sed -n 's/^Error: Invariant \(.*\) is violated\.$/\1/p' "$log" | head -n 1)"
  else
    got="error:$code"
  fi
  distinct="$(sed -n 's/.* \([0-9,]*\) distinct states found.*/\1/p' "$log" | tail -n 1)"
  mark=""
  if [[ "$got" != "$expect" ]]; then
    mark="  MISMATCH"
    mismatches=$((mismatches + 1))
  fi
  printf '%-72s %-20s %-20s %10s %5s%s\n' "$rel" "$expect" "$got" "$distinct" "$secs" "$mark"
done
echo "TLC output for each config: $logs"
if [[ $mismatches -gt 0 ]]; then
  echo "$mismatches config(s) did not end as expected." >&2
  exit 1
fi
