# Security policy

Lots decides what an AI agent may do on behalf of people, so security reports are taken seriously.

## Reporting a vulnerability

Please **do not open a public issue**. Report privately through GitHub's
["Report a vulnerability"](https://github.com/carnufex/Lots/security/advisories/new) form on this repository.
Include what you found, how to reproduce it and the impact you see. You will get an acknowledgement within a
few days.

## What is in scope

- Bypassing policy: a tool being visible or callable without a declared tool and a role granting its risk class.
- Skipping approvals, approving without the right role, or reading another user's runs.
- Authentication bypass, token validation flaws, `Auth:Mode=Dev` behaviour leaking into an `Oidc` deployment.
- Prompt injection that changes permissions or instructions (tool output must never do that).
- Secrets exposed in logs, traces, audit rows or error messages.
- Container or chart settings that weaken the restricted-SCC hardening.

## Design assumptions

- `Auth:Mode=Dev` and `Auth:Dev:AllowHeaders` are for local development only. The shell refuses to start without an explicit `Auth:Mode`.
- Policy is enforced in the shell (`ToolInvoker`), never by the model. The audit log is append-only.
- Secrets are referenced by environment variable name in profiles, never stored in them.
