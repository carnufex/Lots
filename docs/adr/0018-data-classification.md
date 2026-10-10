# 0018. Data classification: four classes, clearance per model endpoint, routing decided per call

Status: accepted (2026-10-10). Decides #89.

## Context

Lots can mix local models (Ollama/vLLM on our own GPUs) and hosted ones. Some tool results (HR data, customer records, incident
details) must not leave the organisation, while most homelab data may. The model must not decide where data goes (principle 1),
and the decision has to be visible and audited.

## Decision

1. **Four ordered classes**: `public < internal < confidential < restricted`.
2. **Data carries a class**: each profile has a `sensitivity` (default `internal`) and each tool may override it; knowledge
   sources have one (default `internal`), and `search_knowledge` results take the class of the most sensitive source they quote.
3. **Endpoints carry a clearance**: `Models:Endpoints:<name>:Clearance`, the highest class the endpoint may see. Default:
   `restricted` for `Location: local`, `internal` for `Location: hosted`. A hosted endpoint therefore never sees confidential data
   unless an admin says so explicitly.
4. **A run's class only rises**: it is the highest class of every tool result it has read. Every model call carries it.
5. **Routing per call**: the call uses the targets of its alias that are cleared for the run's class. If none is, it goes to
   `Models:SensitiveAlias` (typically a local model) and the step and an audit row (`ModelRerouted`, tool `model:<alias>`) say
   so. If no endpoint at all is cleared, nothing is sent: the run fails with an explanation and an audit row (`ModelBlocked`).
6. **Tools no model may see are not offered**: a tool whose class is above the highest clearance its profile can reach (own alias
   plus the sensitive alias) is hidden from the model and denied if called, like any tool the user's roles do not grant.

## Consequences

- Existing deployments keep working: every endpoint is local unless configured otherwise, so nothing is rerouted or blocked.
- Adding a hosted model is safe by default: runs that touch confidential data move to the sensitive alias or stop.
- The class of the prompt itself is not inferred; a user who pastes confidential text into a prompt is outside this control.
  A profile used for such work should simply have no hosted model.
- Classification is as good as the labels: tools and sources must be labelled by their owners; the defaults are conservative
  for hosted models only.
