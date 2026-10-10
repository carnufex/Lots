#!/usr/bin/env bash
# Local CI: build, test, build container images, fail if an image would run as root.
# GitHub Actions is billing-blocked for this account, so this is the gate until it is re-enabled.
# Usage: scripts/ci.sh [--no-images] [--no-scan] [--evals]
#   --evals runs the eval datasets against a running stack (docker compose up, LOTS_DEV_HEADERS=true) and fails on a pass rate
#   below LOTS_EVAL_MIN_PASS (default 0.8) or any regression since the last stored run. LOTS_URL, LOTS_EVAL_FILES override.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "== build + test"
dotnet restore --locked-mode --nologo -v q
dotnet build --nologo -v q --no-restore
dotnet test --nologo --no-build -v q

echo "== helm chart"
bash scripts/check-chart.sh

echo "== profile template"
bash scripts/check-template.sh

echo "== base images pinned"
bash scripts/pin-base-images.sh --check

if [[ " $* " == *" --evals "* ]]; then
  echo "== evals"
  url="${LOTS_URL:-http://localhost:8088}"
  curl -fsS "$url/health/ready" >/dev/null || { echo "FAIL: evals need a running stack at $url (docker compose up)" >&2; exit 1; }
  for f in ${LOTS_EVAL_FILES:-evals/homelab.json}; do
    dotnet run --project src/Lots.Evals --no-build -- --url "$url" --file "$f" --report "evals/report-$(basename "$f" .json).md"       --dev-user claude-test-evals --dev-roles operator --min-pass "${LOTS_EVAL_MIN_PASS:-0.8}" --label ci
  done
fi

[[ " $* " == *" --no-images "* ]] && { echo "== images skipped"; exit 0; }

echo "== images"
check_image() { # name, dockerfile
  docker build -q -t "lots-ci/$1:ci" -f "$2" . >/dev/null
  local uid
  uid="$(docker run --rm --entrypoint id "lots-ci/$1:ci" -u)"
  if [[ "$uid" == "0" ]]; then
    echo "FAIL: $1 image runs as root" >&2
    return 1
  fi
  echo "ok: $1 runs as uid $uid"
}
check_image shell src/Lots.Shell/Dockerfile
check_image mcp-homelab src/Lots.Mcp.Homelab/Dockerfile
check_image mcp-toolpack src/Lots.Mcp.Toolpack/Dockerfile

if [[ " $* " != *" --no-scan "* ]]; then
  echo "== vulnerability scan"
  bash scripts/supply-chain.sh scan
fi
echo "== CI passed"
