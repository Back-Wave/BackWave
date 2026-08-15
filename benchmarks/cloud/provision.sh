#!/usr/bin/env bash
#
# Provision a fresh native-x86-64 Ubuntu box and run the OFFICIAL BackWave benchmark battery on it.
#
# This is the on-box half of benchmarks/cloud/aws-official-run.md. Run it from anywhere inside a checkout
# of the repo (it resolves the repo root from its own location). It installs Docker + the .NET 10 SDK,
# brings up Postgres + SQL Server (Developer edition) co-located on loopback via the repo's own
# docker-compose.yml, seeds the BackWave + Hangfire databases the battery expects, and runs
# benchmarks/run-official.sh in official mode.
#
# Native x86-64 Linux ONLY. Official mode is gated to native x86-64 (a Rosetta/Arm host measures the
# emulator, not the engine), so this script refuses anything else up front rather than wasting a provision.
#
#   ./benchmarks/cloud/provision.sh
#
# Tunables pass straight through to run-official.sh via the environment, e.g. a faster smoke pass:
#   RUNS=2 JOBS=20000 ANCHOR_JOBS=5000 ./benchmarks/cloud/provision.sh
# Publishable numbers use the defaults (RUNS=5, JOBS=100000) — don't trim them for the real run.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

log() { printf '\n=== %s ===\n' "$*"; }

# 0. Native x86-64 Linux only — mirror the harness official-mode gate before doing any work.
if [[ "$(uname -s)" != "Linux" || "$(uname -m)" != "x86_64" ]]; then
  echo "FATAL: official numbers require native x86-64 Linux; this host is $(uname -s)/$(uname -m)." >&2
  echo "       Run with MODE=local for indicative-only numbers, or move to an x86-64 box." >&2
  exit 1
fi

# 1. Docker (+ the compose plugin).
if ! command -v docker >/dev/null 2>&1; then
  log "Installing Docker"
  curl -fsSL https://get.docker.com | sudo sh
fi
# Use sudo for docker only if the invoking user can't reach the daemon (fresh box: not yet in the group).
DOCKER="docker"
docker ps >/dev/null 2>&1 || DOCKER="sudo docker"

# 2. .NET 10 SDK — user-local install, no apt-repo juggling, runs as the invoking (non-root) user.
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  log "Installing .NET 10 SDK"
  curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$DOTNET_ROOT"
fi

# 3. Databases up and healthy. The repo compose exposes PG on :5499 and SQL Server on :14330, both on
#    loopback — exactly the co-located topology the methodology requires (ADR 0027). --wait blocks until
#    both healthchecks pass (SQL Server's first boot can take ~30s).
log "Starting Postgres + SQL Server"
$DOCKER compose up -d --wait

# 4. Seed the databases the official battery needs. Compose auto-creates only PG backwave_test, so add
#    PG hangfire_test and both SQL Server databases. Idempotent — safe to re-run.
log "Seeding databases"
$DOCKER compose exec -T postgres psql -U backwave -d backwave_test -tAc \
  "SELECT 1 FROM pg_database WHERE datname='hangfire_test'" | grep -q 1 \
  || $DOCKER compose exec -T postgres psql -U backwave -d backwave_test -c "CREATE DATABASE hangfire_test"
$DOCKER compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'BackWave!Passw0rd' -C -b -Q \
  "IF DB_ID('backwave_test') IS NULL CREATE DATABASE backwave_test; IF DB_ID('hangfire_test') IS NULL CREATE DATABASE hangfire_test;"

# 5. DSNs. The harness defaults already match these compose ports/creds, but set them explicitly so the
#    whole run is reproducible from this script alone (and so a stray local override can't sneak in).
export BACKWAVE_POSTGRES_DSN="Host=localhost;Port=5499;Username=backwave;Password=backwave;Database=backwave_test"
export BACKWAVE_HANGFIRE_POSTGRES_DSN="Host=localhost;Port=5499;Username=backwave;Password=backwave;Database=hangfire_test"
export BACKWAVE_SQLSERVER_DSN="Server=localhost,14330;User Id=sa;Password=BackWave!Passw0rd;TrustServerCertificate=true;Database=backwave_test"
export BACKWAVE_HANGFIRE_SQLSERVER_DSN="Server=localhost,14330;User Id=sa;Password=BackWave!Passw0rd;TrustServerCertificate=true;Database=hangfire_test"

# 6. Run the official battery (run-official.sh defaults MODE=official; the harness gate re-verifies x86-64).
log "Running official battery (default RUNS=5 — allow 30-90 min)"
benchmarks/run-official.sh

log "Done"
echo "Results: $REPO_ROOT/benchmarks/results/*.json"
echo 'Transcribe only cells with "Publishable": true into docs/performance/.'
