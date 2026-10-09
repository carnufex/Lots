# Lots

A self-hostable agent shell with identity, policy and audit built in.

A *lots* (Swedish for maritime pilot) guides ships through waters it knows, while command stays with the captain.
Lots agents know the systems; they act strictly within the permissions of the person they work for.

## What it is

- **Shell:** agent loop, model abstraction, OIDC login, per-call policy, approvals, audit/trace, UI.
- **Profiles:** pluggable domains. A profile brings MCP servers, tool risk classes (read / write / destructive), data scopes, instructions and evals.
- **Config as code:** profiles, integrations, role mappings and policies are declarative and applied via API, CLI or GitOps.
- **Runs anywhere:** Docker Compose, Kubernetes, OpenShift. Any OpenAI-compatible model, including fully self-hosted.

## Status

Early development. See milestones and `docs/adr/`.
