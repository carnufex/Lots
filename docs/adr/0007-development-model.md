# 0007: Default model for development

## Context
The loop is model-agnostic (OpenAI-compatible API). Local models are free and self-hosted but vary in tool-calling quality; hosted models are best at tool calling but data leaves the homelab.

## Decision
Use both. Develop against a hosted model for fast iteration; run the eval harness against local models (Ollama/vLLM in the homelab) to learn which are good enough.

## Consequences
- Model client config must make base URL, model and API key reference switchable per environment.
- Evals are the arbiter for which local model is acceptable.
- No production or sensitive homelab data is sent to hosted models during development.
