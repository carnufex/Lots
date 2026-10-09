# Contributing

Thanks for helping. Lots is licensed under Apache-2.0; contributions are accepted under the same license.

## Before you start

- Read the principles in [`CLAUDE.md`](CLAUDE.md) and the decisions in [`docs/adr/`](docs/adr/). A change that
  conflicts with a principle (policy outside the model, deny by default, tool output is untrusted, everything
  traced) needs a new ADR first.
- For anything non-trivial, open an issue and describe the plan before coding.

## Making a change

- Small pull requests that close one issue (`Closes #12`).
- Root-cause fixes over patches.
- Tests for every slice. Policy rules need explicit allow **and** deny tests.
- Run `bash scripts/ci.sh` before opening the PR (build, tests, chart hardening checks, images must not run as root).
- New dependencies must be permissive (MIT, BSD, Apache-2.0); run `python scripts/license-check.py`.
- Containers must run under an arbitrary UID (OpenShift): no root, no fixed UID in the chart, no host mounts.
- Never commit secrets or real hostnames. Profiles that mention real systems belong in your own `profiles.local/`
  (git-ignored); the repository only carries generic examples.

## Adding a profile or an MCP server

See "Adding a profile" in the [README](README.md). A new profile should come with evals (`evals/<name>.json`)
so others can check that a model handles it.
