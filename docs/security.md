# Security controls

What Lots does to keep the agent within bounds, and what an operator configures. Decisions behind it are in `docs/adr/`.

## Prompt injection (ADR 0017)

Tool and knowledge output is data. Every result reaches the model inside an untrusted-data envelope; hidden Unicode is removed,
fake chat turns are neutralised, and instruction-like lines are flagged in place. A run that has seen flagged output needs an
approval for any later write or destructive call, even where the role could run it directly (`Agent:EscalateAfterInjection`).
Flagged steps show "possible injection" in the run view and count in `lots_tool_injections_suspected_total`.

Measure it: `dotnet run --project src/Lots.Evals -- --mode injection --file evals/injection.json --dev-user claude-test-redteam --repeat 3`
reports the attack success rate over knowledge documents, filenames and container logs.

## Egress (#86)

The shell only opens connections that configuration asks for: MCP servers, identity token endpoints (client credentials, token
exchange, user connections), notification and audit webhooks, knowledge URL sources. Each connection is checked when it is opened,
on the address actually connected to, so DNS rebinding and redirects to internal addresses do not get through.

| Setting | Default | Meaning |
|---|---|---|
| `Egress:Default:AllowedHosts` | any | host patterns (`*.corp.example`) for every purpose |
| `Egress:Default:AllowPrivateNetworks` | `true` | loopback, RFC 1918, CGNAT, IPv6 ULA; backends usually live there |
| `Egress:Default:AllowLinkLocal` | `false` | 169.254.0.0/16 and fe80::/10, i.e. the cloud metadata service |
| `Egress:Mcp`, `:Identity`, `:Webhook`, `:Knowledge` | inherit | the same three settings, narrowing one purpose |

A server or webhook on a forbidden address fails with `egress denied: …`, visible on the Servers tab and in the logs. Helm:
`egress.allowedHosts`, `egress.webhookHosts`, `egress.allowPrivateNetworks`.

The tool pack (`src/Lots.Mcp.Toolpack`) uses the same rules (`src/Shared/Egress.cs`) but stricter defaults: `fetch` reaches only
`Fetch:AllowedHosts`, on web ports, never private networks unless `Fetch:AllowPrivateNetworks`; OpenAPI tools only their base URL.
Responses are size-capped and requests time out (fetch 20 s, knowledge 30 s, webhooks 15 s, token endpoints 20 s, MCP connect 10 s).

At the network layer, `networkPolicy.enabled` adds NetworkPolicies: the shell may reach anything except 169.254.0.0/16, the tool
pack only public addresses on 80/443 and accepts traffic only from the shell. With Cilium, `networkPolicy.cilium.enabled` limits
the tool pack to `networkPolicy.cilium.toolpackFqdns` by DNS name.

## Secrets (#87)

Profiles name secrets, they never contain them: credential fields (`tokenEnv`, `passwordEnv`, `clientSecretEnv`, audit
`WebhookAuthHeaderRef`) take a reference, `NAME` / `env:NAME` for an environment variable or `file:/path` for a file. Files are
read on every use, so rotation needs no restart. A profile in which a line looks like a credential (a JWT, `Bearer …`, a private
key, a cloud or Git token, `password: …`) is rejected when it is loaded or applied.

Masking: every value the shell resolves from a reference, every environment variable whose name contains
key/token/secret/password/credential, the database password and every backend token it fetches or exchanges is replaced by
`[redacted]` in log messages and exceptions, tool results (the trace and what the model sees), stored tool arguments and run
errors. Credential shapes are masked even when the shell never saw the value (a token printed in a container log). Structured
log properties written by a JSON or OTLP log exporter are not rewritten; the shell does not attach secrets to them.

Insecure settings: outside `ASPNETCORE_ENVIRONMENT=Development` the shell refuses to start with `Auth:Mode=Dev`,
`Auth:Dev:AllowHeaders=true` or an OIDC authority over plain http (loopback excepted). `Security:AllowInsecureSettings=true`
overrides this for a throwaway demo and logs a warning on every start. Local compose sets Development.

### Kubernetes Secrets and External Secrets Operator

Any secret store that ESO supports (Bitwarden Secrets Manager, Vault, cloud key vaults) ends as a Kubernetes Secret; give it to
the shell as an env var or a file:

```yaml
apiVersion: external-secrets.io/v1
kind: ExternalSecret
metadata: { name: lots-cmdb, namespace: lots }
spec:
  refreshInterval: 1h
  secretStoreRef: { kind: ClusterSecretStore, name: bitwarden }   # or a Vault store
  target: { name: lots-cmdb }
  data:
    - secretKey: token
      remoteRef: { key: CMDB_AGENT_TOKEN }
```

```yaml
# values.yaml
extraSecretEnv:
  - { name: CMDB_AGENT_TOKEN, secretName: lots-cmdb, key: token }   # profile: passwordEnv: CMDB_AGENT_TOKEN
secretFiles:
  - { secretName: lots-cmdb, mountPath: /run/secrets/cmdb }          # or: passwordEnv: file:/run/secrets/cmdb/token
```

Prefer `secretFiles`: a refreshed Secret reaches the mounted file within a minute, an env var only on the next restart.

### Vault

- **Vault Secrets Operator or ESO**: as above (`VaultStaticSecret` / ESO `vault` provider → Kubernetes Secret → `secretFiles`).
- **Vault Agent injector**: annotate the pod (`podAnnotations`) so the agent renders the secret into a shared volume and reference
  the file:

```yaml
podAnnotations:
  vault.hashicorp.com/agent-inject: "true"
  vault.hashicorp.com/role: lots
  vault.hashicorp.com/agent-inject-secret-cmdb: kv/data/lots/cmdb
  vault.hashicorp.com/agent-inject-template-cmdb: '{{ with secret "kv/data/lots/cmdb" }}{{ .Data.data.token }}{{ end }}'
# profile: passwordEnv: file:/vault/secrets/cmdb
```

### Bitwarden Secrets Manager without Kubernetes

For compose or a VM, let `bws` inject the secrets as environment variables for the process; nothing is written to disk:

```bash
bws run --project-id <project> -- docker compose up -d
```

The machine-account access token for `bws` is itself a secret: keep it in the host's credential store, not in `.env`.

## Authentication and browser hardening (#88)

Threat model: `docs/threat-model.md` (STRIDE for the shell, the MCP boundary and voice).

- **OIDC**: authorization code flow with PKCE in the browser (oidc-client-ts); tokens are kept in `sessionStorage`, sent as
  `Authorization: Bearer`, never in cookies, so there is no CSRF surface. The shell requires signed tokens with an expiry and
  allows 60 s clock skew (`Auth:Oidc:ClockSkewSeconds`). Access-token lifetime is the IdP's setting: keep it short (5-15 min).
- **No CORS**: the API answers same-origin browsers only; scripts and CI use API tokens.
- **API tokens**: Usage page → API tokens, or `POST /me/tokens {"name","scopes":["read","runs","approvals","admin"],"expiresInDays"}`.
  Send as `Authorization: Bearer lots_pat_…` (lotsctl: `--token`). Scopes: `read` = any GET, `runs` = start/cancel/retry runs and
  voice, `approvals` = decide approvals, `admin` = every other change. A token acts as its creator with at most the roles they had
  when it was made (and no more than their roles at their latest login), never manages tokens, and expires after at most
  `Auth:ApiTokens:MaxDays` (90). Only a SHA-256 hash is stored. Admins list and revoke all tokens at `/admin/api-tokens`.
  `Auth:ApiTokens:Enabled=false` turns them off.
- **Headers** on every response: a content security policy (own scripts only, plus `blob:` for the microphone worklet; the IdP is
  the only other origin for connect/frame/form; no framing), `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
  `Referrer-Policy: no-referrer`, `Cross-Origin-Opener-Policy: same-origin`, a `Permissions-Policy` that allows only the microphone,
  `Cache-Control: no-store` for API responses, and HSTS outside Development (`Security:Hsts`). `Security:CspConnectSrc` adds origins
  to `connect-src` if a deployment needs one.

## Data classification (ADR 0018, #89)

Classes `public < internal < confidential < restricted`. Label data where it comes from: `sensitivity:` on a profile (default for
its tools, `internal` if unset) and per tool; knowledge sources get a class when created (Knowledge page, API `sensitivity`, or
`Knowledge:Sources:N:Sensitivity`). Give model endpoints a clearance: `Models:Endpoints:<name>:Clearance` (default `restricted`
for `Location: local`, `internal` for `hosted`) and name a local fallback with `Models:SensitiveAlias`.

A run's class is the highest class it has read (shown on the run page). Each model call goes only to endpoints cleared for it;
otherwise to the sensitive alias (step marked "Rerouted", audit `ModelRerouted`), otherwise nowhere (run fails, audit
`ModelBlocked`). Tools whose class no model of the profile may see are hidden and denied. The Models page shows each endpoint's
clearance.

```yaml
# appsettings / env: a hosted model for everyday questions, the local one for anything confidential
Models:
  Endpoints:
    cloud: { BaseUrl: https://api.example/v1, ApiKeyEnv: CLOUD_KEY, Location: hosted }   # cleared for internal
    gpu:   { BaseUrl: http://ollama:11434/v1 }                                            # local: restricted
  Aliases:
    default: { Targets: [ { Endpoint: cloud, Model: big } ] }
    local:   { Targets: [ { Endpoint: gpu, Model: qwen3.5 } ] }
  SensitiveAlias: local
```

## Personal data in traces (#90)

Opt in per profile:

```yaml
pii:
  redact: [email, phone, personnummer, card, tokens]
  scope: trace     # or: all
```

`scope: trace` masks what Lots keeps as a record while the run itself still works with the real data: tool results and arguments
in the trace, model replies in the trace, and tool arguments in the audit log (masked before the row is sealed into the hash
chain, so exports are masked too). The user still gets the answer they asked for. `scope: all` also masks the stored prompt,
conversation and answer when the run ends (completed, failed or cancelled), so nothing personal stays in the database beyond the
run.

Recognised: e-mail addresses; phone numbers that start like one (`+`, `00` or a Swedish trunk `0`: `070-123 45 67`,
`08-123 456 78`, `+46 70 123 45 67`); Swedish personal identity and coordination numbers in all common forms, checked with the
Luhn digit so dates, build numbers and order numbers are not masked; payment card numbers (Luhn); credentials (as in Secrets).
Masks are `[email]`, `[phone]`, `[personnummer]`, `[card]`, `[redacted]`.

Logs are deployment-wide: `Privacy:RedactLogs` (for example `Privacy__RedactLogs__0=email`). The personal data export
(`/me/export`) is the user's own data and is not masked.

## Own voice: consent, limits and erasure (#93, ADR 0013/0015)

- **Consent**: a recording is accepted only with the user's explicit confirmation of a versioned statement; every registration,
  withdrawal, admin revocation and erasure is appended to `voice_consents` (who, when, which statement, clip length, never audio),
  kept like the audit log (`Retention:AuditDays`).
- **Owner only**: the agent speaks with a recorded voice only to its owner (the voice is taken from the caller's own settings, so
  an admin reading someone else's run hears the deployment voice). Voice is never an authority: no approvals or writes by voice.
- **Limits**: `Speech:VoiceRegistrationsPerDay` (5) per user, clip length 8-30 s, size `Speech:MaxAudioBytes`.
- **Admins** see who has a recorded voice and the consent trail on the Voice page (`GET /admin/voices`) and can revoke one
  (`DELETE /admin/voices/{user}`).
- **Erasure is verified**: deleting a voice (by the user, an admin, or with "delete my data") asks the voice service afterwards
  whether the clip still exists (`GET /v1/voices/{id}`); if it does, nothing is deleted and the user is told to retry.
- **Conversation audio** is purged after `Speech:AudioRetentionDays` (30) by the retention job (ADR 0014).

## Who sees which pages (#157)

`GET /me/capabilities` lists the pages and contexts the caller may use, computed with the same checks the endpoints make. The web UI
builds its menu from it, and a direct link to a page without access shows an explanation. Hiding is only UX: every endpoint authorises
each request itself. A test checks, for every page and role set, that the menu matches the endpoint.

| Area | Roles (configuration key, default) |
|---|---|
| Chat, History, Runs, Usage, Voice, Knowledge, Integrations | every signed-in user; admin-only tabs inside check their own rights |
| Approvals | roles with `approve:` in a profile, or admins |
| Audit | `Auth:AuditRoles` (admin, auditor) |
| Models, Profiles, Policy, Identity | `Auth:AdminRoles` (admin) |
| Feedback (review queue) | `Feedback:ReviewRoles` (admin) |
| Insights | `Insights:Roles` (admin, auditor, self-improve) |

Contexts are the profiles in which one of the caller's roles grants something.


## Viewing as other roles (#156)

Admins (by their real roles, `Auth:AdminRoles`) can see Lots as other roles with **View as** in the top bar, or
`POST /me/preview {"roles": [...], "minutes": 30, "allowWrites": false}`. The shell returns a short-lived token
(Data Protection, bound to the user, at most 60 minutes) that the browser sends as `X-Lots-Preview`.

- The preview only narrows. Every tool call is decided for the previewed roles **and** the admin's real roles, and the
  stricter decision wins; a preview can never reach a tool the admin could not.
- Write and destructive tools are refused unless the preview was started with `allowWrites`. Approving is off.
- The menu and every endpoint use the previewed roles, so a page that would be refused shows *no access*.
- A run started in a preview keeps those limits for its whole life. Audit rows of its tool decisions carry a
  `preview` field (`as <roles> (actor's roles <roles>)`) that is part of the hash chain.
- Leaving is dropping the header (**Leave preview**); an expired, tampered or someone else's token is ignored.

The **Policy** page also shows a roles × tools matrix (`GET /admin/v1/policy/matrix?profile=`, admins and auditors):
each cell is the policy engine's decision for a user with only that role.

In dev mode the identity switcher picks roles from the deployment (`devRoles` in `/config`) and can save personas
in the browser.
