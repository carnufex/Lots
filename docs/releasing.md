# Releasing

Releases are cut locally with `scripts/release.sh`, because GitHub Actions is not available. The script does what a release pipeline
would do, and nothing is published unless you pass `--push`.

## Versions

- **Semantic versions.** MAJOR for breaking changes to the API, profile format or configuration. MINOR for features. PATCH for fixes.
- **One source.** The `VERSION` file feeds the .NET assemblies (through `Directory.Build.props`), the Helm chart (`version` and
  `appVersion` move together) and `web/package.json`.
- **Running version.** `GET /health` returns it, with the commit for images: `{"status":"ok","version":"0.2.0+1a2b3c4"}`.
- **Tags.** Git tags are `vX.Y.Z`. Images are tagged `X.Y.Z` and `sha-<commit>`. Deployments pin `tag@sha256:digest`.

## Cutting a release

```bash
scripts/release.sh 0.2.0 --dry-run                          # preview the notes and the chart under out/release/
LOTS_REGISTRY=registry.example.com/org scripts/release.sh 0.2.0           # local release, nothing pushed
LOTS_REGISTRY=registry.example.com/org scripts/release.sh 0.2.0 --push    # and publish
```

A local release does, in order:

1. **Checks.** You are on a clean `main` that has everything from `origin`, and the tag does not exist yet.
2. **Version bump.** Writes `VERSION`, `charts/lots/Chart.yaml` and `web/package.json`, and prepends the release notes to `CHANGELOG.md`
   (`scripts/changelog.py`).
3. **Full CI.** Runs `scripts/ci.sh`: build, tests, chart and template checks, images, root check and vulnerability scan. Skip it with
   `--skip-ci` only when CI has just passed on this commit.
4. **Commit and tag.** Commits `Release vX.Y.Z` and adds an annotated tag.
5. **Artifacts.** Builds the shell, homelab MCP and tool pack images, plus the voice image with `--with-voice`. Packages the chart.
   Writes CycloneDX SBOMs. Everything lands in `out/release/`.

`--push` then:

- pushes the images and records their digests in `out/release/digests-X.Y.Z.txt`;
- pushes the chart as an OCI artifact to `oci://$LOTS_REGISTRY/charts`;
- signs the images when `COSIGN_KEY` or `COSIGN_KEY_B64` is set (ADR 0021);
- pushes the commit and the tag.

To abandon a local release before pushing: `git tag -d vX.Y.Z && git reset --hard HEAD~1`.

## Release notes

`scripts/changelog.py` builds the notes from git history since the previous tag:

- **Grouping.** Commits are grouped by the type label of the issues they close (`Closes #N`), using `gh` when it is available.
- **Upgrade notes.** Lines in a commit body that start with `Upgrade:` or `BREAKING:`, plus a note when the release adds database
  migrations.
- **Decisions.** The ADRs added since the last release.

Write an `Upgrade:` line in the commit whenever an operator must do something or might be surprised: a renamed setting, a stricter
validation, a new required value.

## Upgrading a deployment

1. Read the upgrade notes of every release between yours and the new one.
2. Back up PostgreSQL. Migrations run on start (`Database:MigrateOnStartup`) and are not undone by going back to an older image.
3. Validate your profiles and other resources against the new version before rolling out: `lotsctl validate -f <dir>` with the new
   `lotsctl`.
4. Roll out by pinning the new `tag@digest` (GitOps) or with `helm upgrade` with the new chart. Replicas can be replaced one at a time:
   runs are leased, so an interrupted run is resumed by another replica.
5. Check `GET /health` for the version and `GET /health/ready`, then run the eval gate (`scripts/ci.sh --evals` against the
   deployment, or `Lots.Evals` with a token).

When GitHub Actions is available again, the same steps belong in a workflow (#8). The registry stays the primary publish target.
