# Lots

A self-hostable agent shell with identity, policy and audit built in.

A *lots* (Swedish for maritime pilot) guides ships through waters it knows, while command stays with the captain. Lots agents know
your systems, and they act strictly within the permissions of the person they work for.

```
 browser ──OIDC──▶ shell ──policy per call──▶ MCP servers ──▶ your systems
 Slack, mail ──▶    │  ▲                       (one or more per profile)
                    │  └── models (any OpenAI-compatible endpoint, routed by data class)
                    ▼
                PostgreSQL: runs, traces, approvals, audit, knowledge
```

- **The shell** owns the agent loop, sign-in, policy, approvals, audit and the web UI.
- **Profiles** plug domains in: MCP servers, tools with risk classes, roles, instructions and tests. Adding one needs no change to the
  shell ([Authoring a profile](authoring-profiles.md)).
- **Runs anywhere**: Docker Compose, Kubernetes and OpenShift (restricted SCC), with any OpenAI-compatible model, including fully
  self-hosted ones.

## Start here

| If you want to | Read |
|---|---|
| Understand how Lots decides what an agent may do | [Concepts](concepts.md) |
| Run it on your machine in ten minutes | [Quickstart](quickstart.md) |
| Connect your own system | [Authoring a profile](authoring-profiles.md) |
| Run it in production | [Operating Lots](operations.md), [Helm chart](helm.md), [Security](security.md) |
| Integrate with it | [API](api.md) |
| Know why it is built this way | [Decisions](adr/index.md) |

## Principles

1. **Policy lives outside the model.** Every tool call passes one choke point, where policy is evaluated per call.
2. **Deny by default.** A tool that is not explicitly allowed for the user and profile is not callable, and not even shown to the model.
3. **Identity follows the user.** Delegated identity where the backend supports it. Weaker strategies are explicit and audited.
4. **Tool output is untrusted data.** Logs, wiki pages and tickets can carry prompt injection. They never change permissions.
5. **Everything is traced.** Every model call and tool call is recorded: who, which profile version, arguments, result, approval.
6. **Contracts over implementations.** MCP for tools, OIDC for identity, the OpenAI API for models, declarative YAML for configuration.
7. **Config as code.** Profiles and policies are YAML applied through an API, the CLI or GitOps.
8. **Runs are jobs, not requests.** A run can pause for an approval, survive a restart and resume on another replica.

Lots is licensed under [Apache-2.0](https://github.com/carnufex/Lots/blob/main/LICENSE).
