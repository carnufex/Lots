# Scheduled and triggered runs

A schedule (#101) starts runs without a person: on a cron expression, when a webhook is called (an alert fired), or when an admin
presses **Run now** (Profiles page). Schedules are config as code, applied like profiles (`kind: Schedule`, via `lotsctl apply`,
the admin API or GitOps); see [`examples/schedules/daily-report.yaml`](../examples/schedules/daily-report.yaml).

## Identity and policy

Each schedule runs as its own **service identity** (`identity.user`, which must start with `svc-`) with the **roles you list**,
which must exist in the profile. Nothing else changes: policy is evaluated per tool call as for a person, write and destructive
calls need approvals, and the people who may approve are notified as usual. The audit log shows the service identity and the
trigger. Give a schedule the smallest role that can do the job.

## Triggers

- **Cron**: five fields, `timeZone` optional (default UTC). Every replica checks every 20 s; each occurrence is claimed by one
  replica (`schedule_fires`), so it runs once. After downtime only the latest missed occurrence runs, and only if it is less than
  an hour late. `Schedules:Enabled=false` stops firing on a replica.
- **Webhook**: `POST /triggers/<name>` with `Authorization: Bearer <secret>` (the value of `webhook.secretRef`). Up to 64 KB of
  body; it is appended to the prompt as untrusted data, wrapped and checked like any tool result (ADR 0017), so instructions in
  an alert cannot steer the run, and a suspicious payload makes its write calls need an approval. At most one run per
  `webhook.minIntervalSeconds` (default 30). A wrong secret, an unknown or disabled schedule all answer `401`.

Alertmanager: `webhook_configs: [{ url: https://lots.example.org/triggers/on-alert, http_config: { authorization: { credentials_file: /etc/alertmanager/lots-secret } } }]`.

## Results

- **UI**: runs appear on the Runs page for the owner (the service identity), admins, and `deliver.viewers` (`role:<name>`,
  `user:<id>`). The Profiles page lists schedules with their next run and last result.
- **E-mail and webhooks**: `deliver.email` and `deliver.webhooks` (https, Slack/Teams style text) get the answer when the run
  ends, through the notification outbox (retries with backoff). E-mail needs `Notifications:Email` configured. Global
  notification webhooks subscribed to `run.finished` get it too.
