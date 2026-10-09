# 0002 – Standard contracts for tools, models and identity

Status: accepted (2026-10-09)

## Decision
- Tools are exposed through MCP servers. Profiles ship MCP servers; the shell is an MCP client.
- Models are called through an OpenAI-compatible chat completions API (Ollama, vLLM, Mistral, others).
- Identity is OIDC against the customer's IdP (Entra ID, Keycloak, Authentik, ...).

## Consequences
- Any component can be swapped without rewriting the shell.
- Model quality for tool calling varies; it is measured with evals, not assumed.
