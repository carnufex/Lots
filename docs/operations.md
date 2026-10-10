# Operating Lots

The runbook: what Lots depends on, how to see its health, how to back it up, upgrade it, and what to do when something breaks.

## What it depends on

| Dependency | Required | When it is down |
|---|---|---|
| PostgreSQL | yes | the shell is not ready (`/health/ready` 503); nothing works |
| Model endpoint(s) | for answers | runs fail with the endpoint error; an alias with a second target falls back |
| Identity provider (OIDC) | for sign-in | new logins fail; tokens already issued keep working until they expire |
| MCP / tool servers | per profile | tools of that server are missing; runs answer without them |
| Voice service | for voice | dictation and spoken answers are off; text works |
| Data volume (`/data`) | for audio and keys | conversation audio is not stored; delegated tokens fail closed after a restart |

- `GET /health` (liveness, no database), `GET /health/ready` (database).
- `GET /health/dependencies` (admins): every dependency with latency and the last error.
- `GET /status` and `/status.html` (public): coarse components only.
- Metrics: `GET /metrics`, Grafana dashboard `deploy/grafana/lots-dashboard.json`, alert rules (Helm `metrics.alerts.enabled`).

## Backup and restore

Back up three things together:

1. **PostgreSQL**: everything except audio. Use your platform's backups (CloudNativePG `ScheduledBackup` to object storage) or
   `pg_dump -Fc lots > lots.dump`. Restore with `pg_restore -d lots --clean lots.dump`.
2. **The data volume** (`/data`): `audio/` (conversation audio, encrypted) and `keys/` (Data Protection keys).
3. **Data Protection keys** are what decrypt stored audio, delegated login tokens and connected-account tokens. Without the keys
   those become unreadable (audio is lost; users reconnect accounts). Keep `keys/` in the same backup as the database.

Knowledge indexes are rebuilt from their sources (re-index on the Knowledge page) and need no backup of their own, except uploaded
documents, which live in the database.

The own-voice clips live in the voice service's `/models/refs` volume: back it up with the voice service.

## Upgrades and rollback

- Database migrations run at start-up (`Database:MigrateOnStartup`, default true). With several replicas, the first replica to start
  migrates; the others wait on the migration lock.
- Migrations only add tables and columns within a release line, so the previous image keeps working against an upgraded database for a
  rollback of one release. Rolling back further: restore the database backup taken before the upgrade.
- Knowledge tables are created and upgraded by the shell itself (`knowledge_schema`), also idempotently.
- Deploy: build and push the image (your registry; `scripts/release.sh`, docs/releasing.md), bump the tag, let ArgoCD sync. Check
  `/health/dependencies` and the dashboard afterwards.

## High availability

- Replicas are safe: runs are leased (30 s, renewed every 10 s). A crashed replica's run continues on another after the lease expires.
- Knowledge indexing is leased per source; the audit sealer takes a Postgres advisory lock; GitOps applies are idempotent.
- Shared state: the database and the `/data` volume (ReadWriteMany if several replicas store audio).

## Run outcomes

Every finished run gets one row in `run_outcomes`, kept current by a background job (`Outcomes:*`). The job also backfills older
runs, recomputes a row when its feedback changes, and recomputes recent runs whose follow-up signals are still open. A row holds the run's:

- **shape:** steps, model and tool calls, tokens, cost, model and tool time;
- **problems:** tool errors, policy denials, approval refusals, timeout, step limit, a voice run that skipped the tools, an empty
  answer, a refusal;
- **signals:** feedback, a follow-up turn within `Outcomes:FollowUpMinutes` (often a correction), the same prompt asked again;
- **context:** profile version, model, channel and trace id.

It holds no prompt or answer, and the user only as `lots.user.hash`.

`GET /insights/outcomes?from=&to=&profile=&model=&channel=&status=&problem=&limit=` (roles in `Auth:AuditRoles`) returns:

- success rate and p50/p95 wall time per profile version, model and channel;
- calls and error rate per tool;
- the latest runs with their main problem.

`lots_run_outcomes_total{profile,channel,status,problem}` counts outcomes for dashboards.

## Observability stack

Lots works without any telemetry backend: run outcomes, the audit log and the run trace are in PostgreSQL. To see metrics, traces and
logs together, start the default stack:

```bash
LOTS_OTLP_ENDPOINT=http://otel-collector:4317 LOTS_LOG_FORMAT=json docker compose --profile observability up -d
```

| Component | Role | Config |
|---|---|---|
| OpenTelemetry Collector | receives traces and logs over OTLP; tail sampling keeps every error, denied or approval trace and every trace over 10 s, plus `LOTS_TRACE_SAMPLE_PERCENT` (25) of the rest | `deploy/observability/otel-collector.yaml` |
| Tempo | traces (a week) | `deploy/observability/tempo.yaml` |
| Loki | logs over OTLP; `trace_id`, `span_id` and the `lots_*` fields are structured metadata | `deploy/observability/loki.yaml` |
| Prometheus | scrapes `/metrics` (OpenMetrics, with exemplars) and evaluates the alert rules | `deploy/observability/prometheus.yml`, `alerts.yml` |
| Grafana | <http://localhost:3000> (`LOTS_GRAFANA_PORT`), anonymous viewer; the Lots dashboard is the home page | `deploy/observability/grafana/`, dashboards from `deploy/grafana/` |

Grafana links the datasources:

- a metric exemplar opens its trace;
- a trace opens its logs;
- a log line opens its trace (the derived field `trace_id`).

Metrics are scraped, not pushed: `Telemetry__OtlpMetrics` stays false unless nothing scrapes `/metrics`.

**In a cluster.** Most clusters already run a collector, Prometheus, Loki and Tempo. Point the chart at them:

- `otlpEndpoint` covers the shell, the MCP servers and the voice service;
- `metrics.serviceMonitor` and `metrics.alerts` for Prometheus;
- `metrics.grafanaDashboard` for the dashboard.

`deploy/observability/otel-collector.yaml` is a starting point for the collector's sampling.

## Logs

Outside Development, the shell, the MCP servers and the voice service write **one JSON object per line** to stdout. `Logging__Format`
(shell, MCP servers) and `VOICE_LOG_FORMAT` (voice) switch between `json` and `text`. With `OTEL_EXPORTER_OTLP_ENDPOINT` set, the
shell also exports its logs over OTLP, redacted the same way.

| Field | Meaning |
|---|---|
| `time`, `level`, `category`, `msg` | UTC time, `trace`/`debug`/`info`/`warn`/`error`/`fatal`, the logger, the message |
| `trace_id`, `span_id` | The active trace, the same as the run's trace (`traceId` on the run page) |
| `lots.run.id`, `lots.profile`, `lots.profile.version`, `lots.conversation.id` | On every line written while a run executes |
| `lots.user.hash` | A keyed hash of the user id, never the id itself. Set `Telemetry__UserHashKey` for hashes that are stable across restarts and replicas |
| other fields | The event's own values in snake_case, e.g. `tool`, `decision`, `reason`, `server`, `attempt` |

Joining logs, traces and audit: `lots.run.id` is the audit log's `runId`, and `trace_id` is the trace in Tempo or Jaeger.

**Redaction.** Registered secrets, credential shapes and the personal data kinds in `Privacy:RedactLogs` are masked in the message,
every field, every scope value and exceptions, before a line is written or exported. Prompts, answers and tool results are not logged.

**Levels.** The defaults are Information for Lots and Warning for ASP.NET Core, EF Core commands and HttpClient. Change them per
category with `Logging__LogLevel__<Category>`. Events worth knowing:

| Event | Category | Level |
|---|---|---|
| Tool denied, approval requested or granted or refused | `Lots.Shell.Core.Runs.AgentRunner` | Information (plain allows at Debug) |
| Run claimed, cancelled, lease lost | `Lots.Shell.Core.Runs.RunWorker` | Information / Warning |
| Model endpoint failed, falling back | `Lots.Shell.Core.Models.ModelRouting` | Warning |
| MCP server unavailable, retrying | `Lots.Shell.Core.Mcp.McpToolSource` | Warning |
| Profile or resource invalid, GitOps sync | `Lots.Shell.Core.Config.*` | Warning / Error |

## Traces

One trace per run (`traceId` on the run page). A resumed execution, for example after an approval, starts a new trace that links
to the first. Every Lots span carries `lots.run.id`, `lots.profile`, `lots.profile.version`, `lots.user.hash`, `lots.channel` and
`gen_ai.conversation.id`, so changes can be compared before and after a profile version. Spans export over OTLP when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set (shell, MCP servers, voice service).

| Step | Span | Notable attributes |
|---|---|---|
| Run execution | `invoke_agent <profile>` | `lots.run.resumed`, link to the first trace |
| Model call | `chat <model>` | `gen_ai.request.model`, `gen_ai.response.model`, `gen_ai.usage.input_tokens`/`output_tokens`, `gen_ai.response.finish_reasons`, `gen_ai.request.reasoning_effort`, `lots.model.alias`, `lots.model.endpoint`, `lots.model.rerouted`, `lots.model.attempt`, error status |
| Tool call | `execute_tool <tool>` | `lots.risk_class`, `lots.policy.decision`, `lots.policy.rule`, `lots.policy.reason`, `lots.tool.decision`, `lots.tool.outcome`, `lots.tool.result_bytes`, `lots.backend.auth` |
| Approval wait | `approval_wait` | real start and end, outcome, risk, approvals required |
| Knowledge search | `retrieve knowledge` | query size, hits, sources |
| Memory | `read_memory` | whether memories were used |
| Token exchange | `token_exchange` | server, cached, status (never the token) |
| Speech | `speech_to_text`, `text_to_speech` | language, audio seconds, time to first audio, fallback |
| MCP server, voice service | `POST /mcp`, `POST /v1/audio/...` | child spans in the same trace: the shell sends `traceparent` |

**Content.** Prompts, answers, tool arguments and results are span events (`gen_ai.user.message`, `gen_ai.choice`,
`gen_ai.tool.arguments`, `gen_ai.tool.result`), redacted. They are only recorded with `Telemetry__CaptureContent=true`, which is the
default in Development only (ADR 0019).

**Not traced.** The browser itself: a turn starts at the shell's HTTP request. Endpointing in the browser is measured in History,
not as a span.

**Cardinality.** Metric labels are never user ids or free text. Per-user and per-run detail lives on spans and in Postgres.

## Troubleshooting

| Symptom | Look at | Usual cause and fix |
|---|---|---|
| Runs stay `Running`, `LotsStuckRuns` fires | `lots_runs_stuck`, shell logs | a replica died mid-run: the lease expires and another resumes it; if not, restart the shell |
| Runs fail with model errors | `/health/dependencies`, Models page | model endpoint down or out of GPU memory; add a fallback target to the alias |
| Answers are slow | dashboard "Model latency", History "where the time goes" | model not loaded (`OLLAMA_KEEP_ALIVE`), GPU shared with voice (#84) |
| Voice unavailable | `/health/dependencies` (voice service) | the GPU PC is off or the voice container stopped |
| Approvals not noticed | `lots_notifications_undelivered`, outbox | webhook URL or SMTP settings; the outbox retries 5 times |
| Audit tamper check fails | `GET /audit/verify` | someone changed or removed audit rows in the database: investigate from the first broken entry |
| Knowledge search returns nothing | Knowledge page status, Models page `embed` | embedding model missing on the endpoint, or the source failed to index |

## Secrets and rotation

Profiles reference secrets by name (`NAME`, `env:NAME`, `file:/path`). File references are re-read on use: rotate the mounted secret and
the next call uses it. Environment variables need a restart. Never put a secret value in a profile.
