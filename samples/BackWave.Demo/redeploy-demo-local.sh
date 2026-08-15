#!/usr/bin/env bash
# Rebuild the demo image from current source and swap the local container. The container always
# listens on 8080 internally; it's published to a fixed host port (HOST_PORT) so the URL is stable
# across redeploys. Override with HOST_PORT=<n> in the environment if you need a different port.
# Run from anywhere; it always builds with the repo root as the Docker context, because
# the demo references many src/* projects and the shared Directory.Build.* files.
#
#   ./samples/BackWave.Demo/redeploy-demo-local.sh
#
# Set a Pro licence to clear the unlicensed-Pro notice:
#   BackWave__ProLicense=<key> ./samples/BackWave.Demo/redeploy-demo-local.sh
set -euo pipefail

# Fixed host port for the demo (container listens on 8080 internally).
HOST_PORT="${HOST_PORT:-22020}"

# Repo root = two levels up from this script (samples/BackWave.Demo/ -> repo root).
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

echo "==> Building backwave-demo image (context: $REPO_ROOT)"
docker build -f samples/BackWave.Demo/Dockerfile -t backwave-demo .

echo "==> Swapping container on host port $HOST_PORT"
docker rm -f backwave-demo >/dev/null 2>&1 || true

RUN_ARGS=(-d --name backwave-demo --restart unless-stopped -p "${HOST_PORT}:8080")
if [[ -n "${BackWave__ProLicense:-}" ]]; then
  RUN_ARGS+=(-e "BackWave__ProLicense=${BackWave__ProLicense}")
  echo "    (Pro licence applied)"
fi
docker run "${RUN_ARGS[@]}" backwave-demo

echo "==> Waiting for health check"
for _ in $(seq 1 20); do
  code="$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:${HOST_PORT}/" || true)"
  if [[ "$code" == "200" ]]; then
    echo "    http://localhost:${HOST_PORT}/ -> 200 OK"
    exit 0
  fi
  sleep 1
done

echo "    Health check did not return 200 on port ${HOST_PORT}. Recent logs:" >&2
docker logs backwave-demo 2>&1 | tail -20 >&2
exit 1
