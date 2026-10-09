# 0006 – Profiles and integrations as code

Status: accepted (2026-10-09)

## Decision
Layered:
1. Declarative admin API with apply semantics (desired state in, diff computed by the shell) – the single source of truth.
2. CLI: `lots apply -f profiles/`.
3. GitOps: YAML in a repo the shell reconciles against.
4. Later, on demand: CRDs + operator, and/or an OpenTofu/Terraform provider.

- Resources managed from Git are read-only in the UI.
- Policies are tested in CI; profile evals run before rollout.
- Every trace records the profile version (commit) that handled it.
