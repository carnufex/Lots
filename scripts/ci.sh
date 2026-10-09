#!/usr/bin/env bash
# Local CI: build, test, build container images, fail if an image would run as root.
# GitHub Actions is billing-blocked for this account, so this is the gate until it is re-enabled.
# Usage: scripts/ci.sh [--no-images]
set -euo pipefail
cd "$(dirname "$0")/.."

echo "== build + test"
dotnet build --nologo -v q
dotnet test --nologo --no-build -v q

echo "== helm chart"
bash scripts/check-chart.sh

[[ "${1:-}" == "--no-images" ]] && { echo "== images skipped"; exit 0; }

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
echo "== CI passed"
