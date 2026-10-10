Closes #

## What and why

## How it was checked

- [ ] `bash scripts/ci.sh` passes (build, tests, chart, template, public and image checks)
- [ ] Policy changes have explicit allow **and** deny tests
- [ ] No secrets or real hostnames; new dependencies pass `python scripts/license-check.py`
- [ ] Docs, `CLAUDE.md` and an ADR are updated where a decision or convention changed
