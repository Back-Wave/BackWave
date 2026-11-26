#!/usr/bin/env bash
# Runs the TLC model checker from a pinned tla2tools.jar, one run at a time on this machine.
# Usage: tests/formal/tlc.sh <Spec.tla> [extra TLC args]   (the .cfg beside the spec is used)
#   TLC_WORKERS  worker threads (default: half the CPU cores; "auto" uses every core)
#   TLC_HEAP     JVM maximum heap, as for -Xmx (default: 4g)
#   TLC_LOG      file for TLC's output (default: stdout); this script's own messages stay on stderr
# A run waits while another holds /tmp/backwave-tlc.lock, a symlink to the holder's PID and
# start time. A lock whose holder has exited is removed.
set -euo pipefail

TLA_VERSION="1.7.4"
TLA_SHA256="936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88"
JAVA_PIN="temurin-21.0.13+9.0.LTS"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cache="$here/.cache"
jar="$cache/tla2tools-$TLA_VERSION.jar"
lock=/tmp/backwave-tlc.lock
cores="$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 2)"
workers="${TLC_WORKERS:-$(( cores > 1 ? cores / 2 : 1 ))}"
heap="${TLC_HEAP:-4g}"

if [[ $# -lt 1 ]]; then
  echo "usage: $0 <Spec.tla> [extra TLC args]" >&2
  exit 2
fi

started() { ps -p "$1" -o lstart= 2>/dev/null; }
me="$$ $(started $$)"

release_lock() {
  [[ "$(readlink "$lock" 2>/dev/null)" == "$me" ]] && rm -f "$lock"
  return 0
}

states=""
trap 'release_lock; [[ -z "$states" ]] || rm -rf "$states"' EXIT

waiting=""
until ln -s "$me" "$lock" 2>/dev/null; do
  holder="$(readlink "$lock" 2>/dev/null)" || continue
  if [[ "$holder" != "${holder%% *} $(started "${holder%% *}")" ]]; then
    [[ "$(readlink "$lock" 2>/dev/null)" != "$holder" ]] || rm -f "$lock"
    continue
  fi
  if [[ -z "$waiting" ]]; then
    echo "Waiting for the TLC run of PID ${holder%% *} to finish ($lock)." >&2
    waiting=1
  fi
  sleep 2
done

sha256() { shasum -a 256 "$1" | cut -d' ' -f1; }
if [[ ! -f "$jar" || "$(sha256 "$jar")" != "$TLA_SHA256" ]]; then
  mkdir -p "$cache"
  curl -fsSL -o "$jar.tmp" "https://github.com/tlaplus/tlaplus/releases/download/v$TLA_VERSION/tla2tools.jar"
  actual="$(sha256 "$jar.tmp")"
  if [[ "$actual" != "$TLA_SHA256" ]]; then
    rm -f "$jar.tmp"
    echo "tla2tools.jar checksum mismatch: got $actual, want $TLA_SHA256" >&2
    exit 1
  fi
  mv "$jar.tmp" "$jar"
fi

if [[ -n "${JAVA_HOME:-}" && -x "$JAVA_HOME/bin/java" ]]; then
  java=("$JAVA_HOME/bin/java")
elif java -version >/dev/null 2>&1; then
  java=(java)
elif command -v mise >/dev/null 2>&1; then
  mise install --quiet "java@$JAVA_PIN" >/dev/null
  java=(mise exec "java@$JAVA_PIN" -- java)
else
  echo "No Java runtime found. Install Java 11 or later, or install mise." >&2
  exit 1
fi

spec="$1"
shift
spec_dir="$(cd "$(dirname "$spec")" && pwd)"
spec_file="$(basename "$spec")"
states="$(mktemp -d)"

cd "$spec_dir"
[[ -z "${TLC_LOG:-}" ]] || exec >"$TLC_LOG" 2>&1
"${java[@]}" "-Xmx$heap" -XX:+UseParallelGC -cp "$jar" tlc2.TLC \
  -workers "$workers" -cleanup -metadir "$states" "$spec_file" "$@"
