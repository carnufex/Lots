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

- Build/test: `dotnet test`
- Run locally: `docker compose up --build` (shell on `http://localhost:8088`, override with `LOTS_PORT`; `GET /health`, `GET /health/ready`)
- New migration: `dotnet ef migrations add <Name> --project src/Lots.Shell -o Persistence/Migrations`

- Live model test (opt-in): `LOTS_TEST_MODEL_URL=http://ollama.local:11434/v1 LOTS_TEST_MODEL=<model> dotnet test --filter Live`
- Homelab MCP server: `docker compose up` also starts `mcp-homelab` (`http://localhost:8089/mcp`, tools `list_containers`, `get_container_logs`) behind a read-only `docker-proxy`.
- Runs API: `POST /runs {"prompt": "..."}` -> 202 + id; `GET /runs/{id}` returns status, final answer and the step trace. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to export GenAI-convention spans.
- Local CI gate (GitHub Actions is billing-blocked): `bash scripts/ci.sh` (build, test, build images, fail on root images).
- Evals (needs the stack running with a reachable model): `dotnet run --project src/Lots.Evals -- --url http://localhost:8088 --file evals/homelab.json`; report in `evals/report.md`.
- Profiles: `profiles/*.yaml` (name, instructions, MCP servers, tools with risk class, roles). A tool is visible/callable only if declared there and a role of the run's user grants its risk class. Override the directory with `Profiles__Path`. Until OIDC: acting user from `Auth:Dev:UserId` / `Auth:Dev:Roles` (default `dev` / `operator`).
- Approvals: write-class calls pause the run (`WaitingForApproval`). `GET /approvals` lists pending ones the caller may decide; `POST /approvals/{id}/approve|deny` with a JSON body (`{}` or `{"comment":"..."}`) resumes the run. Roles list what they may approve under `approve:` in the profile. Dev identities: `Auth:Dev:AllowHeaders=true` enables `X-Dev-User`/`X-Dev-Roles` (never outside local dev).
- Audit: `GET /audit?user=&from=&to=&runId=&limit=` (roles in `Auth:AuditRoles`, default admin,auditor). Append-only table `audit_log`: one row per tool decision (Allowed/Denied/ApprovalRequested/ApprovalGranted/ApprovalRefused/ApprovalDenied) with user, profile + version, approver and result.
- Auth: `Auth__Mode` is mandatory (shell refuses to start otherwise). `Oidc` validates JWTs from `Auth__Oidc__Authority` (optional `Audience`, `UserClaim`=sub, `RoleClaim`=roles); `Dev` authenticates everyone as `Auth:Dev:UserId`/`Roles` (compose default via `LOTS_AUTH_MODE`, local development only). A run can be read by its owner or `Auth:AdminRoles` (default admin); evals take `--token`/`LOTS_TOKEN`.
- Web UI: `web/` (Vite + React + TS). `cd web && npm ci && npm run dev` for development (proxy not configured: run against the shell via `npm run build` into the image, or open http://localhost:8088/ in compose). The shell serves it from wwwroot; `GET /config` tells it the auth mode (dev switcher or OIDC code flow with PKCE, `Auth__Oidc__ClientId`).
- Profiles with real hostnames live in the git-ignored `profiles.local/` (point compose at it with `LOTS_PROFILES_DIR=./profiles.local`); `examples/profiles/cmdb.yaml` is the generic template. The cmdb profile talks to an MCP server as a service account: the shell fetches a client-credentials token from the IdP using the app password in the env var the profile names (`passwordEnv`; Bitwarden key `CMDB_AGENT_TOKEN` for the homelab CMDB; never put it in a profile). Server `credentials:` types: `bearer` (tokenEnv) and `oauth-client-credentials`. Evals: `--file evals/cmdb.json`.
- Helm: `charts/lots` (external PostgreSQL via `database.existingSecret`, profiles as ConfigMap via `profiles.files`, `route.enabled` for OpenShift). `bash scripts/check-chart.sh` lints and fails on root/hostPath/privileged/fixed UID; it runs in `scripts/ci.sh`. The image was verified with `--read-only --cap-drop ALL --user 1000710000:0`.
- Replicas: every shell instance runs a worker; runs are claimed with a lease (`runs.LeaseOwner/LeaseUntilMs`, 30 s TTL, renewed every 10 s, released when done). A crashed replica's run is resumed by another after the lease expires. Verified with two replicas against Postgres.
