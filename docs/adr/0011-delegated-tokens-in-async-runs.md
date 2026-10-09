# 0011 – Delegated identity in asynchronous runs

Status: accepted (2026-10-10)

## Context
ADR 0004 ranks delegated identity (RFC 8693 token exchange) first. But a run is a job: it may continue after the
HTTP request that started it, wait hours for an approval and be resumed by another replica. A user's access token is
short-lived, and the original token must never be passed on to a backend.

## Decision
- A server declares `auth: delegated` with `credentials.type: token-exchange` (token URL, client id, optional client
  secret by environment variable, the backend audience). Both must be present together; the profile parser enforces it.
- When a run whose profile has a delegated server is started over OIDC, the user's login token is kept on the run,
  **encrypted with ASP.NET Data Protection**, together with its expiry. Runs without delegated servers never store it.
- On each call the shell exchanges that token for one scoped to the backend's audience and sends only the exchanged
  token. Exchanged tokens are cached per user and server until shortly before they expire.
- The stored token is deleted when the run completes or fails.
- If the login token has expired (or cannot be decrypted), the delegated call fails with a clear tool result and an
  audit row, rather than falling back to a shared account. Starting a new run gets a fresh token.

## Consequences
- Delegated access only works while the user's token is valid: a run that waits for an approval longer than the
  token lifetime cannot make further delegated calls. Refresh tokens are not stored on purpose (smaller blast radius);
  a later decision can add them for providers that issue long-lived ones.
- Data Protection keys must be persistent (`DataProtection:KeysPath`, chart `dataProtection.existingClaim`) and shared
  between replicas, otherwise stored tokens are unreadable after a restart and delegated calls fail closed.
- In `Auth:Mode=Dev` there is no login token, so delegated servers are unavailable.
- The IdP must support token exchange (e.g. Keycloak). Providers that do not (Authentik at the time of writing) use
  the shared-service-account strategy from ADR 0009.
