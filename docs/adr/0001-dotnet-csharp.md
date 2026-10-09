# 0001 – .NET/C# for the shell

Status: accepted (2026-10-09)

## Context
The shell is an I/O-bound orchestrator: latency is dominated by model and tool calls, not CPU. Go and Rust were considered.

## Decision
Build the shell in .NET/C# with Vertical Slice Architecture and FastEndpoints.

## Consequences
- Fastest delivery given existing expertise; official MCP C# SDK available.
- Rewrite risk is managed through contracts (MCP, OIDC, OpenAI-compatible API), not language choice.
