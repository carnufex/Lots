# Lots

Lots is a self-hostable agent shell. The shell owns the agent loop, identity, policy, approvals, audit and UI.
Domains plug in as **profiles** (MCP servers + tool risk classes + scopes + instructions + evals).
First profiles: `homelab` and `cmdb`. Other organisations must be able to add their own profiles without touching the shell.

Design background: see `docs/adr/`. Decisions live there, not in closed issues.

## Non-negotiable principles

1. **Policy lives outside the model.** The model never decides what it is allowed to do. Every tool call passes one choke point (`ToolInvoker`) where policy is evaluated per call.
2. **Deny by default, least privilege.** A tool not explicitly allowed for the user + profile is not callable, and ideally not even shown to the model.
3. **Identity follows the user.** Prefer delegated identity (token exchange / OBO). Weaker strategies are supported for legacy systems, but are explicit per integration and recorded in audit. The end user never sees which backend account was used.
4. **Tool output is untrusted data.** Logs, wiki pages, CMDB fields and RAG chunks may contain prompt injection. Never let tool output change permissions or instructions.
5. **Everything is traced.** Every model call and tool call is persisted: who, profile + version, tool, arguments, result, approval, latency, tokens.
6. **Contracts over implementations.** MCP for tools, OIDC for identity, OpenAI-compatible API for models, declarative admin API for config. Any component must be replaceable.
7. **Config as code.** Profiles, integrations, role mappings and policies are declarative YAML applied via API/CLI/GitOps. Resources managed from Git are read-only in the UI.
8. **Agent runs are jobs, not HTTP requests.** A run can pause (e.g. waiting for approval), persist and resume.

## Stack

- .NET / C#, Vertical Slice Architecture, FastEndpoints
- PostgreSQL (state, traces; pgvector later for RAG)
- React + TypeScript frontend (later milestone), dark minimalist UI
- Containers: Docker Compose locally, Helm for Kubernetes/OpenShift
- Dev model: any OpenAI-compatible endpoint (Ollama/vLLM in homelab)
- Dev IdP: Authentik in homelab (OIDC)

## Container rules (OpenShift compatibility)

- Never assume root. Images must run under an arbitrary UID with group 0.
- Writable directories: `chgrp -R 0` and `chmod -R g=u`.
- No privileged ports, no host mounts in the Helm chart.
- The homelab MCP server reaches Docker only through a read-only socket proxy, never the raw socket.

## How we work

- **Plan first.** For anything non-trivial, write the plan in the issue before coding.
- **Root-cause fixes** over patches.
- **Ask before any change to a cluster** or shared homelab infrastructure. Local compose is fine.
- Small PRs that close one issue. Reference the issue (`Closes #12`).
- Tests for every slice; policy rules get explicit allow/deny tests.

## Issues, labels and decisions

Labels: `needs-human`, `decision`, `blocked`, `type:feature|bug|chore`, `area:core|policy|mcp|ui|deploy|evals`.

**At the start of every session:**
1. `gh issue list --label needs-human --state open`
2. For each, check for comments from Christopher newer than the last Claude comment. If he has answered, act on it.
3. Report what was unblocked before starting other work.

**When a decision is needed:**
1. Open an issue labelled `needs-human` (+ `decision` if architectural).
2. Body: context, 2-3 options with trade-offs, a recommendation, and what is blocked until it is decided.
3. Continue with unblocked work; do not guess on irreversible choices.

**When a decision is made:**
1. Write a short ADR in `docs/adr/NNNN-title.md` (context, decision, consequences).
2. Link the ADR from the issue, remove `needs-human`, close the issue.
3. Update this file if the decision changes a principle or convention.

## Commands

To be filled in as the solution takes shape (build, test, compose up, evals).
