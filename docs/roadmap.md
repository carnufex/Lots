# Roadmap

The plan lives in GitHub **milestones and issues**; this page is the map. Decisions live in `docs/adr/`.

Lots is a self-hostable agent shell: the shell owns the loop, identity, policy, approvals, audit and UI; domains plug in as profiles.
The roadmap below follows from that: what the shell must do well, then what is built on top of it.

| Milestone | Theme | State |
|---|---|---|
| M1 | Loop, homelab read tools, trace | done (CI pipeline blocked by Actions billing, #8) |
| M2 | Identity, profiles, policy, approvals | done |
| M3 | UI and second profile (cmdb) | done |
| M4 | Run anywhere, public-ready (Helm, leases, delegated identity) | done |
| M5 | Voice: dictation, spoken answers, conversation mode, own voice, meetings | in progress |
| M6 | **Knowledge (RAG)**: ingestion, embeddings, hybrid search, citations, ACLs, conflict voting | planned |
| M7 | **Tools and integrations**: one Integrations page (tool calls, MCP servers, catalog, credentials) | planned |
| M8 | **Admin and config as code**: admin API, `lotsctl`, GitOps, editors for profiles, policy, models, identity | planned |
| M9 | **Operate**: metrics, traces, cost, quotas, retention, history with audio, backups, GPU guard | planned |
| M10 | **Security hardening**: prompt injection, egress, secrets, data classification, privacy | planned |
| M11 | **Agent capabilities and channels**: chat UX, cancel, memory, scheduling, sub-agents, Slack/Teams, API | planned |
| M12 | **Evals and quality**: datasets, judges, model comparison, feedback loop, voice quality | planned |
| M13 | **Docs, packaging and community**: docs site, templates, release process, public-readiness | planned |

## Why these were added (gaps found 2026-10-10)

The first plan stopped at "a working shell with voice". Looking at what an organisation needs to *run* it, these were missing:

- **Knowledge**: no retrieval, embeddings, vector store, or access-controlled sources. Biggest functional gap (M6).
- **Tools as a product surface**: tools and MCP servers were two placeholders; operators need one place with tool calls, servers, a
  catalog with risk classes, credentials and health (M7).
- **Config as code was only half true**: profiles load from files, but there is no admin API, CLI or GitOps path, and no policy
  simulator ("why was this denied") (M8).
- **Operations**: no metrics, cost accounting, quotas, retention or backup story; the 2026-10-10 GPU memory stall showed why (M9).
- **Security beyond policy**: prompt injection tests, egress control, data classification, PII handling (M10).
- **Agent behaviour**: text chat is one run per prompt; no streaming, cancel, memory, scheduled runs or other channels (M11).
- **Quality loop**: evals exist but no datasets, judges, history or feedback (M12).
- **Adoption**: no docs site, templates or release process while GitHub Actions is blocked (M13).

## Navigation (web UI)

Work: Runs, History, Approvals, Audit. Capabilities: Knowledge, **Integrations** (tabs: Tool calls, MCP servers, Catalog, Credentials),
Voice, Transcription, Models. Administration: Profiles, Policy, Identity. Pages that are not built yet show what they will do and
which milestone tracks them.
