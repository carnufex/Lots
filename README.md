# Lots

A self-hostable agent shell with identity, policy and audit built in.

A *lots* (Swedish for maritime pilot) guides ships through waters it knows, while command stays with the captain.
Lots agents know the systems; they act strictly within the permissions of the person they work for.

## What it is

- **Shell:** agent loop, model abstraction, OIDC login, per-call policy, approvals, audit and trace, web UI.
- **Profiles:** pluggable domains. A profile brings MCP servers, tool risk classes (read / write / destructive), role grants, approval rules and instructions. Adding one needs no change to the shell.
- **Config as code:** profiles are declarative YAML, loaded at startup and validated (all errors reported at once).
- **Runs anywhere:** Docker Compose, Kubernetes, OpenShift (restricted SCC). Any OpenAI-compatible model, including fully self-hosted ones such as Ollama.

```
 browser ──OIDC──▶ shell ──policy per call──▶ MCP servers ──▶ your systems
                    │  ▲                         (read-only socket proxy,
                    │  └── model (OpenAI API)     CMDB, ... one per profile)
                    ▼
                PostgreSQL: runs, trace, approvals, audit
```

Principles (details in [`CLAUDE.md`](CLAUDE.md) and [`docs/adr/`](docs/adr/)):
the model never decides what it may do; tools are denied by default; tool output is untrusted data; every
decision is audited; runs are jobs that can pause for approval and survive restarts.

## Quick start

Requires Docker with Compose v2 and a reachable OpenAI-compatible model.

```bash
export LOTS_MODEL_URL=http://host.docker.internal:11434/v1   # e.g. Ollama
export LOTS_MODEL=<a model that supports tool calling>
docker compose up --build
```

Open http://localhost:8088. Compose starts the shell (with the UI), PostgreSQL, the example homelab MCP server and a
**read-only** Docker socket proxy, in `Auth__Mode=Dev` (everyone is one local user; local development only).

Try: *"Which containers are unhealthy and why?"*

Run the evals against your model: `dotnet run --project src/Lots.Evals`.

## Adding a profile

1. Copy [`examples/profiles/cmdb.yaml`](examples/profiles/cmdb.yaml) to a new folder, say `my-profiles/`, next to `profiles/homelab.yaml`.
2. Point it at your MCP server, list the tools it offers with a risk class each, and define roles:

   ```yaml
   tools:
     - { name: list_things, risk: read }
     - { name: restart_thing, risk: write }
   roles:
     - { name: operator, allow: [read] }
     - { name: admin, allow: [read, write], requireApproval: [write], approve: [write] }
   ```
3. Start with `LOTS_PROFILES_DIR=./my-profiles docker compose up`. A tool the profile does not declare is never shown to the model and never callable.

Secrets are never part of a profile: it names the environment variable that holds them (`passwordEnv`, `tokenEnv`).

## Production

- **Auth:** set `Auth__Mode=Oidc` and `Auth__Oidc__Authority` (any OIDC provider; roles from the `roles` claim, configurable).
- **Kubernetes / OpenShift:** [`charts/lots`](charts/lots). External PostgreSQL, secrets by reference, restricted-SCC compatible. `bash scripts/check-chart.sh` verifies the hardening.
- **Replicas:** any number; runs are leased, so each is executed by one instance and a crashed instance's run is resumed by another.

## Development

```bash
dotnet test                 # shell, MCP server, policy, leases, auth, evals scoring
cd web && npm ci && npm run build
bash scripts/ci.sh          # build, test, chart checks, images (fails on root images)
python scripts/license-check.py   # dependency licenses must be permissive
```

Layout: `src/Lots.Shell` (API, worker, policy), `src/Lots.Mcp.Homelab` (example MCP server), `src/Lots.Evals`,
`web/` (React UI), `profiles/`, `examples/`, `charts/`, `docs/adr/`.

## Status

Early development; the API is not yet stable. See the milestones and [`docs/adr/`](docs/adr/).

## License

[Apache-2.0](LICENSE). Third-party licenses: [`docs/third-party-licenses.md`](docs/third-party-licenses.md).
Security issues: see [`SECURITY.md`](SECURITY.md). Contributing: see [`CONTRIBUTING.md`](CONTRIBUTING.md).
