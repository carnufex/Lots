# 0004 – Authentication strategy per integration

Status: accepted (2026-10-09)

## Context
OBO/token exchange cannot be assumed; legacy systems must be supported.

## Decision
Each integration declares one strategy, strongest first:
1. Delegated (token exchange RFC 8693 / OBO) – recommended
2. User-connected account (OAuth or personal token stored encrypted)
3. Impersonation (service account "acting as" the user)
4. Service account per role
5. Shared service account (shell policy is the only control)

- The original user token is never passed through; tokens are exchanged per target audience.
- Admins see the strategy and its strength; audit records which was used.
- The strategy is hidden from end users: they never see which backend account handled a call.
- Secrets are referenced, never stored in config (Kubernetes Secrets via External Secrets Operator, Vault, OpenBao).
