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
- **Testing is judgement, not a ritual.** Not all code needs tests and not every change needs testing. Small changes (copy, styling, a
  tweak, a small refactor) are not tested or re-tested after each step. Write tests where they carry value: policy rules (explicit
  allow/deny tests), security and data boundaries, logic that is easy to get wrong, bug fixes that could regress. Run the full suite
  (`bash scripts/ci.sh`) for larger changes and before a PR, not after every small implementation step.

## Issues, labels and decisions

Labels: `needs-human`, `decision`, `blocked`, `type:feature|bug|chore`, `area:core|policy|mcp|ui|deploy|evals|voice|rag|tools|admin|ops|agent|security|docs`.
Roadmap map: `docs/roadmap.md` (milestones M5-M13).

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

- Models (#135): `Models:Endpoints:<name>` (BaseUrl, ApiKeyEnv, TimeoutSeconds, Location local|hosted) and `Models:Aliases:<alias>:Targets` (ordered `{Endpoint, Model}` fallback chain; next target on connection error, timeout, 5xx or 429, never on 4xx). The legacy `Model` section is endpoint + alias `default`. A profile picks an alias with `model:` (unknown alias = startup error); voice runs use alias `voice` when configured. Trace steps record the model and endpoint that answered. `GET /models` (admins): endpoint health + aliases.
- Knowledge (ADR 0016, M6): embeddings via alias `embed` (`/v1/embeddings`; compose `LOTS_EMBED_MODEL`, default nomic-embed-text). Tables are created by the shell (not EF), pgvector used when the extension exists (compose image `pgvector/pgvector:*-pg17-trixie`; prod CNPG has 0.8.2 available but needs `CREATE EXTENSION vector` by a superuser), otherwise `real[]` + `lots_dot`. Sources: `upload` | `directory` (under `Knowledge:DirectoryRoots`, default /knowledge) | `url`; readers `*`, `role:x`, `user:id`; config-as-code via `Knowledge:Sources` (compose indexes `./docs` as `lots-docs`). API: `GET /knowledge`, `POST /knowledge/sources` (admins; `personal: true` for anyone = only they can read), `POST .../{id}/documents` (JSON text) and `.../{id}/files` (multipart), `.../reindex`, `GET /knowledge/search?q=`, `GET /knowledge/chunks/{id}`. The agent tool `search_knowledge` must be declared in a profile; results are filtered by the run's user and labelled untrusted. Live store test: `LOTS_TEST_PG=<conn> dotnet test --filter PostgresKnowledgeStoreLive`.
- Live model test (opt-in): `LOTS_TEST_MODEL_URL=http://ollama.local:11434/v1 LOTS_TEST_MODEL=<model> dotnet test --filter Live`
- Homelab MCP server: `docker compose up` also starts `mcp-homelab` (`http://localhost:8089/mcp`, tools `list_containers`, `get_container_logs`) behind a read-only `docker-proxy`.
- Runs API: `POST /runs {"prompt": "..."}` -> 202 + id; `GET /runs/{id}` returns status, final answer and the step trace. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to export GenAI-convention spans.
- Run control: `POST /runs/{id}/cancel` (owner or admin; 200 Cancelled when idle, 202 when a worker holds it: it stops within ~1 s via `runs.CancelRequestedAt`), `POST /runs/{id}/retry` (owner only, failed/cancelled runs -> new run with `retryOf`). `GET /runs/{id}` has `waiting` (queued/model/tool/approval/cancelling). Time limits: `Agent:RunTimeoutSeconds` (300, per execution, approval waits excluded), `Agent:VoiceRunTimeoutSeconds` (60), `Agent:ToolTimeoutSeconds` (60, the model gets a timeout error).
- Local CI gate (GitHub Actions is billing-blocked): `bash scripts/ci.sh` (build, test, build images, fail on root images).
- Conflicting sources (#48): a profile with `detectConflicts: true` has search_knowledge ask the model (alias `judge` if configured, else default) whether the top passages of different documents contradict; a conflict is stored once (`knowledge_conflicts`, by its options) and announced in the tool result (`Conflict: <id> options=k1,k2`). Users vote on the run page (`GET/POST /knowledge/conflicts/{id}[/vote]`, only readable options, one vote per user, 30/h); admins see tallies, suggestions and swings (`GET /knowledge/conflicts`) and mark the authoritative option (`/resolve`). Votes on a changed document stop counting; `Knowledge:VoteWeights:<role>` weights roles. Votes never affect policy.
- Retrieval evals (#57): `dotnet run --project src/Lots.Evals -- --mode retrieval --dev-user claude-test-evals --file evals/knowledge.json [--answers] [--judge-url <openai-compatible> --judge-model <m>] [--min-recall 0.8]` -> recall@1/3/5, MRR, citation precision, facts, judge groundedness in `evals/report-knowledge.md`; fails below the recall@5 gate. Baseline 2026-10-10 (nomic-embed-text, ./docs): recall@5 85 %, MRR 0.68; misses are Swedish questions.
- Evals (needs the stack running with a reachable model): `dotnet run --project src/Lots.Evals -- --url http://localhost:8088 --file evals/homelab.json`; report in `evals/report.md`.
- Profiles: `profiles/*.yaml` (name, instructions, MCP servers, tools with risk class, roles). A tool is visible/callable only if declared there and a role of the run's user grants its risk class. Override the directory with `Profiles__Path`. Until OIDC: acting user from `Auth:Dev:UserId` / `Auth:Dev:Roles` (default `dev` / `operator`).
- Approvals: write-class calls pause the run (`WaitingForApproval`). `GET /approvals` lists pending ones the caller may decide; `POST /approvals/{id}/approve|deny` with a JSON body (`{}` or `{"comment":"..."}`) resumes the run. Roles list what they may approve under `approve:` in the profile. Dev identities: `Auth:Dev:AllowHeaders=true` enables `X-Dev-User`/`X-Dev-Roles` (never outside local dev).
- Audit: `GET /audit?user=&from=&to=&runId=&limit=` (roles in `Auth:AuditRoles`, default admin,auditor). Append-only table `audit_log`: one row per tool decision (Allowed/Denied/ApprovalRequested/ApprovalGranted/ApprovalRefused/ApprovalDenied) with user, profile + version, approver and result.
- Tool calls: `GET /tool-calls?user=&profile=&tool=&status=ok|error|denied&from=&to=&limit=&offset=` = trace steps joined with their audit decision (by `audit_log.ToolCallId`), plus per-tool calls/errors/denied/p50/p95. Own runs only; admins and `Auth:AuditRoles` see all. UI: Integrations -> Tool calls.
- Integrations: `GET /integrations/servers` (admins: per server URL, backend auth, health ok/unavailable/per-user, offered tools and where they are declared) and `GET /integrations/catalog` (admins + auditors: per profile tool risk, status exposed/missing/per-user/unclassified, roles that may use/need approval/approve, call count). Unclassified = offered by a server but in no profile: never visible to the model.
- Admin API (#66, principle 7): `POST /admin/v1/apply {yaml, dryRun, managedBy: api|gitops, prune}` takes multi-document YAML (`kind: Profile` = a profile manifest, `kind: KnowledgeSource` = id/name/sourceKind/location/readers), validates all, returns per-resource action + unified diff, writes all or nothing (422 on errors). A changed profile needs a higher `version`. Profiles from files are read-only (`file`); gitops-managed resources are read-only for the API/UI; `prune` (gitops) deletes ones that left Git. `GET /admin/v1/resources`, `/resources/{kind}/{name}[/versions]`, `DELETE /resources/{kind}/{name}?managedBy=`, `/changes` (audit), `/export` (YAML). Stored in `config_resources` + append-only `config_versions`; `ConfigSyncWorker` reloads profiles on every replica; MCP servers of applied profiles are used without restart. Admins only.
- Auth: `Auth__Mode` is mandatory (shell refuses to start otherwise). `Oidc` validates JWTs from `Auth__Oidc__Authority` (optional `Audience`, `UserClaim`=sub, `RoleClaim`=roles); `Dev` authenticates everyone as `Auth:Dev:UserId`/`Roles` (compose default via `LOTS_AUTH_MODE`, local development only). A run can be read by its owner or `Auth:AdminRoles` (default admin); evals take `--token`/`LOTS_TOKEN`.
- Web UI: `web/` (Vite + React + TS). `cd web && npm ci && npm run dev` for development (proxy not configured: run against the shell via `npm run build` into the image, or open http://localhost:8088/ in compose). The shell serves it from wwwroot; `GET /config` tells it the auth mode (dev switcher or OIDC code flow with PKCE, `Auth__Oidc__ClientId`).
- Profiles with real hostnames live in the git-ignored `profiles.local/` (point compose at it with `LOTS_PROFILES_DIR=./profiles.local`); `examples/profiles/cmdb.yaml` is the generic template. The cmdb profile talks to an MCP server as a service account: the shell fetches a client-credentials token from the IdP using the app password in the env var the profile names (`passwordEnv`; Bitwarden key `CMDB_AGENT_TOKEN` for the homelab CMDB; never put it in a profile). Server `credentials:` types: `bearer` (tokenEnv) and `oauth-client-credentials`. Evals: `--file evals/cmdb.json`.
- Helm: `charts/lots` (external PostgreSQL via `database.existingSecret`, profiles as ConfigMap via `profiles.files`, `route.enabled` for OpenShift). `bash scripts/check-chart.sh` lints and fails on root/hostPath/privileged/fixed UID; it runs in `scripts/ci.sh`. The image was verified with `--read-only --cap-drop ALL --user 1000710000:0`.
- Delegated identity (ADR 0011): a server with `auth: delegated` + `credentials.type: token-exchange` gets per-user tokens via RFC 8693; the user's login token is kept encrypted on the run only while needed (set `DataProtection__KeysPath` to a persistent shared volume). Needs an IdP with token exchange; otherwise use the shared service account.
- Replicas: every shell instance runs a worker; runs are claimed with a lease (`runs.LeaseOwner/LeaseUntilMs`, 30 s TTL, renewed every 10 s, released when done). A crashed replica's run is resumed by another after the lease expires. Verified with two replicas against Postgres.
- Production: https://lots.rosenvall.se (homelab cluster, ArgoCD app `lots`, manifests in `Rosenvalls-Homelab/kubernetes/applications/lots/`, Authentik app `lots` via blueprint `apps-lots.yaml`, groups `lots-admin|planner|operator|auditor` = roles). Deploy: `docker build --platform linux/amd64 -f src/Lots.Shell/Dockerfile -t registry.rosenvall.se/carnufex/lots-shell:sha-<short> .`, `docker push`, then bump `tag@sha256` in the homelab `app.yaml` and push. Real hostnames live only in the homelab repo and `profiles.local/`.
- Voice (ADR 0012/0013): `Speech__BaseUrl` (OpenAI-shaped audio API, our voice service), `Speech__ApiKeyEnv`, `Speech__Voices__sv|en`. Endpoints: `POST /voice/transcribe` (multipart `audio`, optional `language`) and `POST /runs/{id}/speak` (final answer only; there is no free text-to-speech). Metadata only in `voice_usage`.
- Voice service: `services/voice` (Python, faster-whisper + Piper via sherpa-onnx, OpenAI-shaped API). Local run: see its README; container needs the NVIDIA runtime. Shell side: `LOTS_VOICE_URL` / `LOTS_VOICE_KEY` in compose.
- Dictation vocabulary: per user, edited on the website (Runs page); sent as the `prompt` (comma separated) to the voice service, which also corrects near-misses to the user's spelling (`voice/vocabulary.py`). `Speech__Vocabulary` (`LOTS_VOICE_VOCABULARY`) holds deployment-wide words. Real-recording checks: `evals/voice/` + `spikes/voice/eval_recordings.py`; browser checks: `e2e/` (Playwright, fake microphone).
- Conversation mode (Runs page): Microphone on/off, hands-free. Browser-side voice detection (`web/src/voice/mic.ts`), avatar (`Avatar.tsx`), barge-in, turns share a `conversationId`; voice runs (`voice: true`) answer in short spoken style, use `reasoning_effort` low/none and get one reminder if they skip the tools (`Agent__Voice*Effort`). Fixed acknowledgement phrases come from `Speech:Acknowledgements` via `GET /voice/ack`. Keep `OLLAMA_KEEP_ALIVE` high so the model stays loaded.
- Expressive voice and own voice (ADR 0015): build the voice image with `VOICE_CHATTERBOX=1` (compose arg + env, Chatterbox Multilingual on the GPU, ~3.5 GB VRAM, first sentence ~1-4 s) and set `LOTS_VOICE_SV=cb-default LOTS_VOICE_EN=cb-default` for the shell; Piper stays the fast fallback (`sv-nst`, `en-lessac`). Users record their own voice on the **Voice** page (consent required, 8-30 s, deletable); the clip lives only in the voice service (`/models/refs`, `PUT/DELETE /v1/voices/{id}`), the shell keeps its id (`user_settings`). Personality sliders (talkativeness, warmth, formality) become style instructions in the system prompt; expressiveness/pace go to the voice. Defaults that sounded best: exaggeration 0.6, cfg_weight 0.4 with a Swedish reference clip.
- Conversation audio is stored 30 days by default (ADR 0014); history and per-stage timing are issue #46.
