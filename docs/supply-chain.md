# Supply chain

What goes into the images, how it is checked, and how a release is published (#91). Everything runs locally (`scripts/`) while
GitHub Actions is unavailable; the tools run as their official containers at pinned versions.

## Pinned inputs

| Input | How it is pinned | Update |
|---|---|---|
| Base images | `FROM image:tag@sha256:…` in every Dockerfile | `scripts/pin-base-images.sh`, then rebuild and scan |
| NuGet packages | `packages.lock.json` per project (`Directory.Build.props`); CI and image builds restore with `--locked-mode` | change the version in the `.csproj`, `dotnet restore --force-evaluate`, commit the lock file |
| npm packages | `web/package-lock.json`, `npm ci` | `npm install <pkg>@<version>` in `web/` |
| Voice (Python) | `services/voice/constraints.txt`, installed with `pip -c` | see the header of that file |

`scripts/ci.sh` fails if a `FROM` line is not pinned or a lock file does not match the projects. NuGet's own audit fails a
build on a known high or critical advisory (`NU1903`/`NU1904` are errors). Licenses: `scripts/license-check.py`.

## Checks

```bash
bash scripts/supply-chain.sh scan      # NuGet, npm, pip and image vulnerabilities (HIGH/CRITICAL with a fix): fails on findings
bash scripts/supply-chain.sh sbom      # CycloneDX SBOMs for each image and the source tree in out/sbom/
```

`scan` runs inside `scripts/ci.sh` (skip with `--no-scan`). Accepted findings go in `.trivyignore`, each with an expiry and an
issue; an expired entry fails the scan again. Current exceptions: the voice service's chatterbox pins (#147).

## Publishing a release

1. `bash scripts/ci.sh` (build, tests, chart, pinned bases, images, scan).
2. Build and push as in `CLAUDE.md` (`registry.rosenvall.se/carnufex/<app>:sha-<short>`).
3. `bash scripts/supply-chain.sh sbom registry.rosenvall.se/carnufex/<app>:sha-<short>` and keep the SBOM with the release.
4. Sign the pushed digest: `bash scripts/supply-chain.sh sign registry.rosenvall.se/carnufex/<app>@sha256:…`
   (needs `COSIGN_KEY`, `COSIGN_PASSWORD`, `REGISTRY_USERNAME`, `REGISTRY_PASSWORD`). Signatures are stored in the registry next
   to the image (`sha256-<digest>.sig`, a tagged manifest, so registry GC keeps it); there is no public transparency log.
5. Anyone can check: `bash scripts/supply-chain.sh verify registry.rosenvall.se/carnufex/<app>@sha256:…` against `cosign.pub`.

Where the signing key lives is an open decision (#148); until it is made, step 4 is skipped and `cosign.pub` does not exist.
