# Threat model (STRIDE)

Scope: the shell (API, worker, web UI), the MCP boundary (shell ↔ tool servers ↔ backends) and voice. Controls are described in
`docs/security.md`; decisions in `docs/adr/`. Reviewed 2026-10-10 (#88). Review again when a trust boundary changes.

## System and trust boundaries

```
 Browser (SPA) ──OIDC code+PKCE──► IdP
    │ Bearer JWT / lots_pat_ token
    ▼
 Shell API ──► Postgres (runs, traces, audit, knowledge)
    │  worker: agent loop ──OpenAI-compatible──► model endpoint (local or hosted)
    │  ToolInvoker (policy, approvals, audit) ──MCP over HTTP──► tool servers ──► backends (Docker, CMDB, k8s, Git, web)
    └──► voice service (STT/TTS on the GPU host)
```

Boundaries: (1) browser ↔ shell, (2) shell ↔ IdP, (3) shell ↔ model, (4) shell ↔ MCP servers and their backends,
(5) shell ↔ voice, (6) tool/knowledge content ↔ model (data that may carry instructions), (7) operators ↔ configuration.

## Shell (API, worker, UI)

| | Threat | Mitigation | Residual |
|---|---|---|---|
| **S** | Forged identity: header identities, unsigned or expired JWTs, stolen API tokens | OIDC JWTs validated (issuer, signature, expiry required, 60 s skew); Dev auth and `X-Dev-*` headers refused outside Development; API tokens hashed, scoped, always expiring, revocable, never more than the creator's current roles | A stolen bearer token works until it expires (minutes for JWTs, at most `Auth:ApiTokens:MaxDays` for API tokens) |
| **T** | Tampering with runs, approvals, audit | Approvals bound to run + tool call id and decided by authorised roles (two-person rule for destructive); audit is append-only and hash-chained with verification (`/audit/verify`), forwarded to syslog/webhooks | A database superuser can rewrite history; the forwarded copy and the chain detect it |
| **R** | Users deny actions | Every tool decision audited with user, profile version, approver, result; runs keep the full trace | — |
| **I** | Data leaks: other users' runs, secrets in logs/traces, cross-origin reads, caching | Run/conversation reads by owner or admin only; knowledge ACLs applied inside the query; secrets masked in logs, traces and tool results; no CORS; `Cache-Control: no-store`; CSP, `nosniff`, no referrer; the model sees only tools the user may call | Admins can read all runs by design |
| **D** | Run floods, hung tools or models | Quotas per user/profile; run, tool and model timeouts; MCP connect timeout and backoff; leases so a crashed replica's runs resume | No per-IP rate limit in the shell: put one in the ingress |
| **E** | Model escalates its own permissions; prompt injection steers write tools | Policy outside the model (deny by default, per call); approvals for write/destructive; tainted runs need approval for any write (ADR 0017); tool output enveloped and flagged | Text can still bias an answer; it cannot cause a write without a person |

Browser specifics: tokens live in `sessionStorage` (oidc-client-ts), not cookies, so there is no CSRF surface; the CSP forbids
inline and foreign scripts (XSS would otherwise reach that storage), framing is denied, and the only external origin the page may
talk to is the IdP. Answers are rendered as text, never as HTML, and remote images are not loaded.

## MCP boundary

| | Threat | Mitigation | Residual |
|---|---|---|---|
| **S** | A tool server impersonated, or a backend reached as the wrong user | Server URLs are config as code (Git, admin API with history); delegated identity via token exchange per user where the IdP supports it, otherwise an explicit service account recorded in audit (ADR 0009/0011) | With a shared service account the backend cannot tell users apart; Lots's audit can |
| **T** | Tool results altered or crafted to manipulate the model | Treated as untrusted data (envelope, flagging, escalation); results stored as returned for review | — |
| **R** | Backend actions without a trace | Every call through `ToolInvoker`; audit row per decision with backend auth strategy | Actions a backend takes on its own are outside Lots |
| **I** | SSRF through the shell or tool pack; secrets in tool output; tokens forwarded too widely | Egress rules on every connection (link-local/metadata blocked, per-purpose host lists, rebinding-safe); NetworkPolicies; secrets masked; exchanged tokens audience-bound per server | `AllowPrivateNetworks` is on for the shell by default because backends are internal |
| **D** | A dead or slow server stalls runs | Per-server locks, 10 s connect timeout, exponential backoff, tool timeout | — |
| **E** | A tool does more than its risk class says | Risk classes declared per tool in the profile; tool packs are read-only by default (SQL read-only transactions, k8s read-only ClusterRole without secrets, Docker via read-only socket proxy) | A server that lies about a tool's effect must be caught in review of the profile |

## Voice

| | Threat | Mitigation | Residual |
|---|---|---|---|
| **S** | Spoken commands or cloned voices treated as authority | Voice is never an authority: approvals and writes need the UI (ADR 0013); own-voice clips need consent and are used only for the owner (ADR 0015) | A cloned voice can still sound like the user to listeners |
| **T** | Altered recordings | Conversation audio stored encrypted with Data Protection | — |
| **R** | "I never said that" | Transcripts are part of the run; audio kept 30 days (ADR 0014) | — |
| **I** | Recordings leak | Encrypted at rest, owner/admin access, retention purge, export/delete per user; voice service shares a key with the shell only | The voice service sees audio in clear while processing |
| **D** | GPU exhaustion | GPU guard falls back to the fast voice; request limits in the voice service | — |
| **E** | Voice service used directly | API key required; LAN-only deployment | Same host as other GPU workloads |

## Open items

- Rate limiting at the ingress (per IP and per token) is an operator task; document per deployment.
- Refresh tokens: the SPA asks for a new login when the access token expires unless the IdP grants `offline_access`; long-lived
  refresh tokens are not stored by the shell.
- PII redaction in stored traces beyond secrets is #90.
