#!/usr/bin/env bash
# Supply chain checks (#91), run locally while GitHub Actions is unavailable. The tools run as their official containers, at
# pinned versions, so nothing has to be installed.
#
#   scripts/supply-chain.sh sbom [image...]   CycloneDX SBOMs for the images and the source tree -> out/sbom/
#   scripts/supply-chain.sh scan [image...]   fail on known high/critical vulnerabilities (NuGet, npm, pip, images)
#   scripts/supply-chain.sh sign <ref@digest...>   sign pushed images with cosign (COSIGN_KEY = key file, or COSIGN_KEY_B64 =
#                                                   the key base64-encoded as stored in the secret store; COSIGN_PASSWORD,
#                                                   REGISTRY_USERNAME, REGISTRY_PASSWORD)
#   scripts/supply-chain.sh verify <ref...>        check a signature against cosign.pub
#
# Images default to the ones scripts/ci.sh builds. Accepted findings go in .trivyignore with a reason and a review date.
set -euo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1 # Git Bash on Windows: keep container paths as they are

SYFT=anchore/syft:v1.18.1
TRIVY=aquasec/trivy:0.58.1
COSIGN=ghcr.io/sigstore/cosign/cosign:v2.4.1
DEFAULT_IMAGES=(lots-ci/shell:ci lots-ci/mcp-homelab:ci lots-ci/mcp-toolpack:ci)

cmd="${1:-}"; shift || true
images=("$@"); [[ ${#images[@]} -eq 0 ]] && images=("${DEFAULT_IMAGES[@]}")
sock=(-v /var/run/docker.sock:/var/run/docker.sock)
# Host paths for bind mounts: Docker Desktop on Windows needs C:/... paths, not Git Bash's /c/... ones.
host() { if command -v cygpath >/dev/null; then cygpath -m "$1"; else echo "$1"; fi; }
ROOT=$(host "$PWD")

case "$cmd" in
  sbom)
    mkdir -p out/sbom
    for img in "${images[@]}"; do
      name=$(echo "$img" | tr '/:' '__')
      docker run --rm "${sock[@]}" -v "$ROOT/out/sbom:/out" "$SYFT" "docker:$img" -q -o "cyclonedx-json=/out/$name.cdx.json"
      echo "sbom: out/sbom/$name.cdx.json"
    done
    # The source tree: NuGet lock files, npm lock file, the voice service's pinned Python packages.
    docker run --rm -v "$ROOT:/src:ro" -v "$ROOT/out/sbom:/out" "$SYFT" dir:/src -q --exclude './**/node_modules/**' --exclude './**/.venv/**' --exclude './.git/**' --exclude './**/bin/**' \
      --exclude './**/obj/**' --exclude './out/**' -o cyclonedx-json=/out/source.cdx.json
    echo "sbom: out/sbom/source.cdx.json"
    ;;

  scan)
    failed=0
    echo "== NuGet (direct and transitive)"
    out=$(dotnet list package --vulnerable --include-transitive 2>&1)
    if echo "$out" | grep -q "has the following vulnerable packages"; then echo "$out"; failed=1; else echo "no known vulnerable NuGet packages"; fi

    echo "== npm (web, production dependencies)"
    (cd web && npm audit --omit=dev --audit-level=high) || failed=1

    echo "== pip (voice service, pinned versions)"
    mkdir -p out/scan
    grep -v '^#' services/voice/constraints.txt > out/scan/requirements.txt
    docker run --rm -v lots-trivy-cache:/root/.cache -v "$ROOT/out/scan:/scan:ro" -v "$ROOT/.trivyignore:/.trivyignore:ro" "$TRIVY" fs -q \
      --scanners vuln --severity HIGH,CRITICAL --ignore-unfixed --ignorefile /.trivyignore --exit-code 1 /scan/requirements.txt || failed=1

    for img in "${images[@]}"; do
      echo "== image $img"
      docker run --rm "${sock[@]}" -v lots-trivy-cache:/root/.cache -v "$ROOT/.trivyignore:/.trivyignore:ro" "$TRIVY" image -q \
        --scanners vuln --severity HIGH,CRITICAL --ignore-unfixed --ignorefile /.trivyignore --exit-code 1 "$img" || failed=1
    done
    [[ $failed -eq 0 ]] && echo "== no known high/critical vulnerabilities" || { echo "== vulnerabilities found (see above)" >&2; exit 1; }
    ;;

  sign)
    if [[ -z "${COSIGN_KEY:-}" && -n "${COSIGN_KEY_B64:-}" ]]; then
      keydir=$(mktemp -d); trap 'rm -rf "$keydir"' EXIT   # the decoded key exists only while signing
      echo "$COSIGN_KEY_B64" | base64 -d > "$keydir/cosign.key"
      COSIGN_KEY="$keydir/cosign.key"
    fi
    : "${COSIGN_KEY:?path to the cosign private key (or COSIGN_KEY_B64)}" "${COSIGN_PASSWORD:?its password}"
    : "${REGISTRY_USERNAME:?registry user}" "${REGISTRY_PASSWORD:?registry password}"
    for ref in "$@"; do
      [[ "$ref" == *@sha256:* ]] || { echo "sign by digest, not tag: $ref" >&2; exit 1; }
      # No public transparency log: the registry is private. Verification uses cosign.pub.
      docker run --rm -e COSIGN_PASSWORD -v "$(host "$(cd "$(dirname "$COSIGN_KEY")" && pwd)/$(basename "$COSIGN_KEY")"):/cosign.key:ro" "$COSIGN" \
        sign --key /cosign.key --tlog-upload=false -y \
        --registry-username "$REGISTRY_USERNAME" --registry-password "$REGISTRY_PASSWORD" "$ref"
      echo "signed: $ref"
    done
    ;;

  verify)
    for ref in "$@"; do
      docker run --rm -v "$ROOT/cosign.pub:/cosign.pub:ro" "$COSIGN" verify --key /cosign.pub --insecure-ignore-tlog=true \
        ${REGISTRY_USERNAME:+--registry-username "$REGISTRY_USERNAME" --registry-password "$REGISTRY_PASSWORD"} "$ref" >/dev/null
      echo "verified: $ref"
    done
    ;;

  *)
    sed -n '2,12p' "$0"; exit 2 ;;
esac
