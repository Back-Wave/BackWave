#!/usr/bin/env bash
# Model-checks LeaseFencing.tla on each config and compares the result with
# the config's first line, "\* expect: pass" or "\* expect: <rule>".
# Usage: tests/formal/lease-fencing/check.sh                   the adapters at the sync commit
#        tests/formal/lease-fencing/check.sh --mutants         the planted breaks in mutants/
#        tests/formal/lease-fencing/check.sh --all             both
#        tests/formal/lease-fencing/check.sh --config <name>   one config, for example
#                                                              oracle.cfg or mutants/claim-no-lock-oracle.cfg
# Exits 0 only when every config ends as it expects.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
tlc="$here/../tlc.sh"

usage() {
  echo "usage: $0 [--mutants | --all | --config <name>]" >&2
  exit 2
}
case "$#:${1:-}" in
  0:) cfgs=("$here"/*.cfg) ;;
  1:--mutants) cfgs=("$here"/mutants/*.cfg) ;;
  1:--all) cfgs=("$here"/*.cfg "$here"/mutants/*.cfg) ;;
  2:--config)
    cfgs=()
    for c in "$2" "$here/$2" "$here/mutants/$2"; do
      [[ -f "$c" ]] && cfgs=("$(cd "$(dirname "$c")" && pwd)/$(basename "$c")") && break
    done
    if [[ ${#cfgs[@]} -eq 0 || "${cfgs[0]}" != "$here"/* ]]; then
      echo "no config $2 in $here or $here/mutants" >&2
      exit 2
    fi
    ;;
  *) usage ;;
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
  TLC_LOG="$log" "$tlc" "$here/LeaseFencing.tla" -config "$rel"
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
  if [[ -z "$expect" || -z "$got" || "$got" != "$expect" ]]; then
    mark="  MISMATCH"
    mismatches=$((mismatches + 1))
  fi
  printf '%-72s %-20s %-20s %10s %5s%s\n' "$rel" "$expect" "$got" "$distinct" "$secs" "$mark"
  if [[ -z "$got" || "$got" == error:* ]]; then
    tail -n 20 "$log" | sed 's/^/    | /'
  fi
done
echo "TLC output for each config: $logs"
if [[ $mismatches -gt 0 ]]; then
  echo "$mismatches config(s) did not end as expected." >&2
  exit 1
fi
