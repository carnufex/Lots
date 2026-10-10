#!/usr/bin/env bash
# Cuts a release without GitHub Actions (#129). See docs/releasing.md.
#
#   scripts/release.sh 0.2.0 --dry-run     preview: release notes and a packaged chart under out/release/, nothing changed
#   scripts/release.sh 0.2.0               local release: bump versions, CHANGELOG, full CI, commit, tag, images, chart, SBOMs
#   scripts/release.sh 0.2.0 --push        the same, then push images, chart (OCI), commit and tag; signs when COSIGN_KEY is set
#
# Options: --with-voice (also build the voice image, CHATTERBOX=1), --skip-ci (only when CI just passed on this commit).
# Images go to $LOTS_REGISTRY (e.g. registry.example.com/org): <registry>/lots-shell:<version> and :sha-<commit>.
set -euo pipefail
cd "$(dirname "$0")/.."

version="${1:-}"
shift || true
flags=" $* "
has() { [[ "$flags" == *" $1 "* ]]; }
die() { echo "release: $*" >&2; exit 1; }

[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]] || die "usage: scripts/release.sh <major.minor.patch> [--dry-run|--push] [--with-voice] [--skip-ci]"
tag="v$version"
out=out/release
mkdir -p "$out"

if has --dry-run; then
  python scripts/changelog.py "$version" > "$out/notes-$version.md"
  helm package charts/lots --version "$version" --app-version "$version" -d "$out" >/dev/null
  echo "dry run: $out/notes-$version.md and $out/lots-$version.tgz (nothing committed, tagged, built or pushed)"
  exit 0
fi

# Preconditions: a clean main that has everything from origin, and a version that does not exist yet.
git fetch -q origin
[[ "$(git rev-parse --abbrev-ref HEAD)" == main ]] || die "release from main"
[[ -z "$(git status --porcelain --untracked-files=no)" ]] || die "uncommitted changes"
[[ "$(git rev-list --count HEAD..origin/main)" == 0 ]] || die "behind origin/main: pull first"
git rev-parse -q --verify "refs/tags/$tag" >/dev/null && die "$tag exists"
[[ -n "${LOTS_REGISTRY:-}" ]] || die "set LOTS_REGISTRY (e.g. registry.example.com/org)"
registry="${LOTS_REGISTRY%/}"

# One version everywhere: .NET assemblies (VERSION via Directory.Build.props), the chart, the web app.
echo "$version" > VERSION
sed -i -E "s/^version: .*/version: $version/; s/^appVersion: .*/appVersion: \"$version\"/" charts/lots/Chart.yaml
(cd web && npm version "$version" --no-git-tag-version --allow-same-version >/dev/null)
python scripts/changelog.py "$version" --write

if ! has --skip-ci; then
  bash scripts/ci.sh
fi

git add VERSION CHANGELOG.md charts/lots/Chart.yaml web/package.json web/package-lock.json
git commit -q -m "Release $tag"
git tag -a "$tag" -m "Lots $tag"
sha="$(git rev-parse --short HEAD)"

build() { # name dockerfile [build args...]
  local name=$1 file=$2; shift 2
  local context=.
  [[ "$file" == services/voice/* ]] && context=services/voice
  docker build --platform linux/amd64 --build-arg "SOURCE_REVISION=$sha" "$@" \
    -t "$registry/$name:$version" -t "$registry/$name:sha-$sha" -f "$file" "$context" >"$out/build-$name.log" 2>&1 \
    || { tail -30 "$out/build-$name.log" >&2; die "image $name failed to build (log: $out/build-$name.log)"; }
  echo "built $registry/$name:$version"
}
images=(lots-shell lots-mcp-homelab lots-mcp-toolpack)
build lots-shell src/Lots.Shell/Dockerfile
build lots-mcp-homelab src/Lots.Mcp.Homelab/Dockerfile
build lots-mcp-toolpack src/Lots.Mcp.Toolpack/Dockerfile
if has --with-voice; then
  build lots-voice services/voice/Dockerfile --build-arg CHATTERBOX=1
  images+=(lots-voice)
fi

helm package charts/lots -d "$out" >/dev/null
refs_local=()
for i in "${images[@]}"; do refs_local+=("$registry/$i:$version"); done
bash scripts/supply-chain.sh sbom "${refs_local[@]}" >/dev/null
for i in "${images[@]}"; do
  n=$(echo "$registry/$i:$version" | tr '/:' '__')
  cp "out/sbom/$n.cdx.json" "$out/$i-$version.cdx.json"
done
python scripts/changelog.py "$version" --since "$(git describe --tags --abbrev=0 "$tag^" 2>/dev/null || git rev-list --max-parents=0 HEAD)" > "$out/notes-$version.md" || true

if ! has --push; then
  echo "released $tag locally ($sha). Nothing pushed. To publish: scripts/release.sh is done; run the push steps in docs/releasing.md,"
  echo "or delete the tag (git tag -d $tag && git reset --hard HEAD~1) to start over."
  exit 0
fi

refs=()
for i in "${images[@]}"; do
  docker push -q "$registry/$i:$version" >/dev/null
  docker push -q "$registry/$i:sha-$sha" >/dev/null
  digest=$(docker inspect --format '{{index .RepoDigests 0}}' "$registry/$i:$version")
  refs+=("$digest")
  echo "pushed $digest"
done
helm push "$out/lots-$version.tgz" "oci://$registry/charts" >/dev/null && echo "pushed chart oci://$registry/charts/lots:$version"
if [[ -n "${COSIGN_KEY:-}${COSIGN_KEY_B64:-}" ]]; then
  bash scripts/supply-chain.sh sign "${refs[@]}"
else
  echo "not signed: neither COSIGN_KEY nor COSIGN_KEY_B64 is set (ADR 0021)"
fi
git push -q origin main "$tag"
printf '%s\n' "${refs[@]}" > "$out/digests-$version.txt"
echo "published $tag: digests in $out/digests-$version.txt"
