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
- Tests where they carry value: policy rules (explicit allow **and** deny tests), security and data boundaries, logic that is
  easy to get wrong, and bug fixes that could regress. Copy, styling and small refactors do not need new tests.
- Run `bash scripts/ci.sh` before opening the PR (build, tests, chart hardening checks, images must not run as root).
- New dependencies must be permissive (MIT, BSD, Apache-2.0); run `python scripts/license-check.py` (add
  `--voice-image lots-voice:local` for the voice service). New models go in `docs/model-licenses.md`: no research-only or
  non-commercial models as defaults.
- Containers must run under an arbitrary UID (OpenShift): no root, no fixed UID in the chart, no host mounts.
- Never commit secrets or real hostnames. Profiles that mention real systems belong in your own `profiles.local/`
  (git-ignored); the repository only carries generic examples.

## Adding a profile or an MCP server

Start from the template (`dotnet new install ./templates/lots-profile`) and follow [docs/authoring-profiles.md](docs/authoring-profiles.md). A new profile should come with evals (`evals/<name>.json`)
so others can check that a model handles it.

## Conduct

See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Security issues go through [SECURITY.md](SECURITY.md), never a public issue.
