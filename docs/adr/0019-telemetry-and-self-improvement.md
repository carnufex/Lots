# 0019. Telemetry data model and the self-improvement loop

Status: accepted (2026-10-10). Decides #137; shapes M14 (#138-#146).

## Context

The shell already emits OpenTelemetry spans (#76), Prometheus metrics (#75), cost (#77), per-stage voice timing (#46) and a
hash-chained audit log (#81). There is no shared model for what is recorded, how records are correlated, who may read them, and
how an improvement found in them may reach production. M14 wants the shell to use its own telemetry to get better without
weakening the principles: policy outside the model, untrusted tool output, everything traced, config as code.

## Decision

1. **Correlation.** One `trace_id` per run (resumed executions link to it, #76). Every span, log line and metric exemplar
   carries the same stable attributes where they apply: `lots.run.id`, `lots.conversation.id`, `lots.user.hash` (a keyed hash,
   never the user id in clear), `lots.profile`, `lots.profile.version`, `lots.model`, `lots.tool`, `lots.risk_class`,
   `lots.channel` (web, voice, api, schedule, ...). OTel GenAI semantic conventions are used wherever they define a name
   (`gen_ai.operation.name`, `gen_ai.request.model`, `gen_ai.usage.*`, `gen_ai.tool.name`, ...).
2. **Content capture.** Prompts, model output, tool arguments and results and retrieved chunks are content. In production they
   are not exported to the telemetry backends unless the profile opts in, and then only redacted (secrets always, personal data
   per `pii`, ADR 0018/#90) and with a short TTL. In development and evals capture is on. The shell's own run trace in Postgres
   is governed separately (retention, `pii.scope`).
3. **Backends.** Default open stack: OTel Collector → Tempo (traces), Loki (logs), Prometheus (metrics), Grafana. It is optional:
   run outcomes, the audit log and the run trace stay in Postgres, so the shell works fully without the stack.
4. **The loop.** observe → analyse (an agent with a read-only profile) → propose (a diff to profile instructions or tool
   descriptions, written as a Git branch or pull request) → evaluate (the eval harness and its regression gate) → **a person
   approves and merges** → config as code applies it → the new `profile.version` shows up in traces, so the effect is measurable.
   The analysing agent can never apply a change, never change roles, policy or approval rules, and reads other users' content
   only under the same policy as any other tool.
5. **Trust.** Telemetry content is untrusted data (principle 4, ADR 0017): it reaches the analysing agent through the same
   envelope and flagging as any tool result and cannot change its instructions or permissions.

## Consequences

- M14 issues build on this: JSON logs with trace correlation (#138), complete span coverage (#139), the default stack (#140), a
  run outcome record (#141), an introspection MCP server (#142), failure mining (#143), proposals as Git changes (#144),
  telemetry privacy (#145), an Insights page (#146).
- The self-improvement profile is `read` only; its only output with an effect is a proposed change that a person reviews.
- Exported telemetry never contains clear user ids; joining it back to a person needs the shell's own data and its policy.
