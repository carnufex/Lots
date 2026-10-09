# 0010 – License: Apache-2.0

Status: accepted (2026-10-10)

## Context
Others should be able to run the shell with their own profiles. The decision blocked making the repository public (issue #11).

## Decision
Lots is licensed under the **Apache License 2.0**. It maximises adoption, includes a patent grant and lets others build on it, including closed products. A commercial edition with enterprise features stays possible later, because the project owner keeps the copyright on the core.

## Consequences
- `LICENSE` is added at the repository root; source files do not need per-file headers.
- Contributions are accepted under the same license.
- Third-party dependencies must be Apache-2.0-compatible (MIT, BSD, Apache-2.0); copyleft dependencies need a new decision.
- Resolves issue #11.
