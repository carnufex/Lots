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
