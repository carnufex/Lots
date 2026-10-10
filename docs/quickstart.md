# Quickstart

Ten minutes from clone to an agent that answers questions about the containers on your machine, within policy.

You need Docker with Compose v2, and a model server with an OpenAI-compatible API that supports tool calling. [Ollama](https://ollama.com)
works: `ollama pull qwen3.5`.

## 1. Start the stack

```bash
git clone https://github.com/carnufex/Lots.git && cd Lots
cat > .env <<'ENV'
LOTS_AUTH_MODE=Dev
LOTS_MODEL_URL=http://host.docker.internal:11434/v1
LOTS_MODEL=qwen3.5
ENV
docker compose up --build -d
```

Compose starts:

- the shell with the web UI on <http://localhost:8088>;
- PostgreSQL;
- the example homelab MCP server, which can list containers and read their logs;
- a **read-only** Docker socket proxy in front of it.

`Dev` mode signs everyone in as one local user. It is for your machine only, and the shell refuses it in production.

## 2. Ask something

Open <http://localhost:8088>, go to **Chat** and ask:

> Which containers are running, and is any of them unhealthy?

Open **Trace** on the answer. You see each model call and each tool call, and the policy decision for each one.

## 3. See policy at work

Ask: *Restart the lots-postgres-1 container.* The homelab profile has no restart tool, so the model cannot even see one, and it
tells you it cannot. That is deny by default.

Open `profiles/homelab.yaml`: the tools, their risk classes and the roles that grant them are all there. Its `policyTests` are checked
whenever the profile loads.

## 4. Add your own domain

```bash
dotnet new install ./templates/lots-profile
dotnet new lots-profile -n Contoso.Tickets --profile tickets
```

You get an MCP server, its tests, a profile and an eval dataset. Follow [Authoring a profile](authoring-profiles.md), then apply it:

```bash
dotnet run --project src/Lots.Ctl -- apply -f Contoso.Tickets/profiles --url http://localhost:8088
```

## 5. Measure it

```bash
dotnet run --project src/Lots.Evals -- --file evals/homelab.json
```

This runs the homelab questions against your model, scores the answers and keeps the result to compare with the next run. See
[Evals](evals.md).

## Next

- Sign in with your identity provider: set `Auth__Mode=Oidc` and `Auth__Oidc__Authority` ([Security](security.md)).
- Run it on Kubernetes or OpenShift: [Helm chart](helm.md).
- Add voice: the GPU voice service in `services/voice`, or `voice.enabled` in the chart.
