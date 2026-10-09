# 0009 – Backend identity: service account first, delegation as extension point

Status: accepted (2026-10-09)

## Context
ADR 0004 defines the authentication strategies per integration. The shell now authenticates users via OIDC
(issue #16) and enforces policy per call, but calls to MCP servers still need an identity towards the backend.

## Decision
- Each MCP server in a profile uses one strategy from ADR 0004. The first implemented strategy is
  **shared service account** (strategy 5): the shell's policy and audit are the control, the backend sees one account.
- The homelab profile uses it: the MCP server reaches Docker only through a read-only socket proxy.
- Delegated identity (token exchange / OBO, strategy 1) is the target for integrations whose backend supports it.
  It is added per integration without changing the policy or audit model.
- The strategy used is recorded per call in the audit log (follow-up issue); end users never see it.

## Consequences
- Per-user attribution at the backend is not available for service-account integrations; the audit log is the
  record of who acted.
- New integrations must state their strategy; "shared service account" must be a conscious choice, not a default
  for write-capable tools.
- Resolves issue #18.
