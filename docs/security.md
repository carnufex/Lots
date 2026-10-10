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
