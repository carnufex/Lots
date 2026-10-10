#!/usr/bin/env bash
# Pins every FROM image in the Dockerfiles to the digest its tag points to right now (#91), so a build uses exactly the base
# image that was reviewed and scanned. Re-run to take base image updates (security fixes), then rebuild and re-scan.
# Usage: scripts/pin-base-images.sh [--check]   (--check: fail if a FROM line is not pinned, change nothing)
set -euo pipefail
cd "$(dirname "$0")/.."

FILES=$(git ls-files '*Dockerfile')
if [[ "${1:-}" == "--check" ]]; then
  if grep -nE '^FROM [^ @]+( |$)' $FILES; then echo "unpinned base images above (run scripts/pin-base-images.sh)" >&2; exit 1; fi
  echo "base images pinned"
  exit 0
fi

for f in $FILES; do
  while read -r image; do
    ref="${image%@*}"
    digest=$(docker buildx imagetools inspect "$ref" --format '{{json .Manifest.Digest}}' | tr -d '"')
    [[ "$digest" == sha256:* ]] || { echo "cannot resolve $ref" >&2; exit 1; }
    python - "$f" "$image" "$ref@$digest" <<'PY'
import sys
path, old, new = sys.argv[1:]
text = open(path, encoding="utf-8", newline="").read()
text = "\n".join(
    line.replace(f"FROM {old}", f"FROM {new}", 1) if line.startswith(f"FROM {old}") else line
    for line in text.split("\n"))
open(path, "w", encoding="utf-8", newline="").write(text)
PY
    echo "$f: $ref@$digest"
  done < <(grep -E '^FROM ' "$f" | awk '{print $2}' | sort -u)
done
