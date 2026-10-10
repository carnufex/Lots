#!/usr/bin/env bash
# Public-readiness checks (#131): no secrets in the tree or the history (gitleaks; known synthetic test tokens are listed in
# .gitleaksignore), and none of your own hostnames or addresses in tracked files. Those patterns are private by nature, so they
# live in the git-ignored .public-patterns (one extended regex per line, e.g. example-corp\.com); without that file only the
# secret scan runs.
set -euo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
fail() { echo "public check FAILED: $1" >&2; exit 1; }
root=$(if command -v cygpath >/dev/null; then cygpath -m "$PWD"; else echo "$PWD"; fi)

if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  docker run --rm -v "$root:/repo:ro" zricethezav/gitleaks:v8.21.2 git /repo --no-banner --redact --gitleaks-ignore-path /repo/.gitleaksignore \
    >/dev/null 2>&1 || fail "gitleaks found secrets (docker run --rm -v \$PWD:/repo zricethezav/gitleaks:v8.21.2 git /repo --redact -v)"
else
  echo "public check: Docker not available, secret scan skipped"
fi

if [[ -f .public-patterns ]]; then
  pattern=$(grep -vE '^\s*(#|$)' .public-patterns | paste -sd'|' -)
  if [[ -n "$pattern" ]] && hits=$(git grep -n -I -E "$pattern" -- . ':!.public-patterns'); then
    echo "$hits" | cut -c1-160 >&2
    fail "tracked files contain private hostnames or addresses (.public-patterns)"
  fi
else
  echo "public check: no .public-patterns, hostname scan skipped"
fi
echo "public ok"
