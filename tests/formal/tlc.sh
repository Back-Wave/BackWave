#!/usr/bin/env bash
# Runs the TLC model checker from a pinned tla2tools.jar.
# Usage: tests/formal/tlc.sh <Spec.tla> [extra TLC args]   (the .cfg beside the spec is used)
set -euo pipefail

TLA_VERSION="1.7.4"
TLA_SHA256="936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88"
JAVA_PIN="temurin-21.0.13+9.0.LTS"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cache="$here/.cache"
jar="$cache/tla2tools-$TLA_VERSION.jar"

if [[ $# -lt 1 ]]; then
  echo "usage: $0 <Spec.tla> [extra TLC args]" >&2
  exit 2
fi

if [[ ! -f "$jar" ]]; then
  mkdir -p "$cache"
  curl -fsSL -o "$jar.tmp" "https://github.com/tlaplus/tlaplus/releases/download/v$TLA_VERSION/tla2tools.jar"
  mv "$jar.tmp" "$jar"
fi
actual="$(shasum -a 256 "$jar" | cut -d' ' -f1)"
if [[ "$actual" != "$TLA_SHA256" ]]; then
  echo "tla2tools.jar checksum mismatch: got $actual, want $TLA_SHA256" >&2
  exit 1
fi

if [[ -n "${JAVA_HOME:-}" && -x "$JAVA_HOME/bin/java" ]]; then
  java=("$JAVA_HOME/bin/java")
elif java -version >/dev/null 2>&1; then
  java=(java)
elif command -v mise >/dev/null 2>&1; then
  mise install "java@$JAVA_PIN" >/dev/null
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
trap 'rm -rf "$states"' EXIT

cd "$spec_dir"
"${java[@]}" -XX:+UseParallelGC -cp "$jar" tlc2.TLC \
  -workers auto -cleanup -metadir "$states" "$spec_file" "$@"
