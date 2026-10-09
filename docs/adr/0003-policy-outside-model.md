# 0003 – Policy outside the model, deny by default

Status: accepted (2026-10-09)

## Context
Tool output (logs, wiki, CMDB fields, RAG chunks) is untrusted and may contain prompt injection.

## Decision
- Every tool call passes a single choke point where policy is evaluated per call, based on user, profile, tool risk class and data scope.
- Nothing is allowed unless explicitly granted. Tools the user may not call are filtered out before the model sees them when this can be determined upfront.
- Write tools require approval; destructive tools may require approval by a second person.
- Two layers: (1) who may use which profile/agent, enforced by the shell; (2) what each call may do, enforced by the target system via the user's identity where possible.

## Consequences
- The model can never grant itself permissions.
- RAG must filter by the user's permissions at retrieval time (document ACLs synced at indexing).
