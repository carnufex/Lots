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
2. Build and push (`scripts/release.sh`, or by hand as `registry.example.com/org/<app>:sha-<short>`).
3. `bash scripts/supply-chain.sh sbom registry.example.com/org/<app>:sha-<short>` and keep the SBOM with the release.
4. Sign the pushed digest: `bash scripts/supply-chain.sh sign registry.example.com/org/<app>@sha256:…`
   (needs the key as `COSIGN_KEY` (a file) or `COSIGN_KEY_B64` (as kept in the secret store), `COSIGN_PASSWORD`,
   `REGISTRY_USERNAME` and `REGISTRY_PASSWORD`). Signatures are stored in the registry next
   to the image (`sha256-<digest>.sig`, a tagged manifest, so registry GC keeps it); there is no public transparency log.
5. Anyone can check: `bash scripts/supply-chain.sh verify registry.example.com/org/<app>@sha256:…` against `cosign.pub`.

The key pair (ADR 0021): the private key, base64-encoded, and its password live in the operator's secret store (Bitwarden Secrets
Manager for the reference deployment), never on disk except decoded into a temporary file while signing. `cosign.pub` is in the
repository root. A cluster can enforce the signatures with the same public key (Kyverno `verifyImages` or the sigstore policy
controller); that is a separate change in the deployment repository.
