# 0022. Playbooks: step-by-step flows declared as config and enforced by the shell

Status: accepted (2026-10-10). Decides #159.

## Context

Runbooks exist today only as documents in Knowledge. The agent retrieves them, but nothing makes it follow them: it can skip a step,
reorder steps or call a tool that the runbook does not need at that point. Other agent platforms offer "procedures" for this. Free-form
procedures edited in the UI would conflict with config as code (principle 7).

## Decision

1. A **playbook** is a declarative resource (`kind: Playbook`), versioned and applied like profiles: API, `lotsctl` or GitOps. It
   belongs to a profile and names:
   - a trigger description, used for selection;
   - ordered steps, each with its instruction, the tools allowed in that step and the approvals it needs;
   - a success check per step, and the playbook's outputs.
2. **The shell enforces it, not the model.** While a run follows a playbook, the policy choke point narrows the visible and callable
   tools to the current step's tools. These are always a subset of what the profile and the user's roles already grant: a playbook can
   only narrow, never widen. The run moves to the next step only when the step's check passes.
3. A run follows a playbook when the user picks it, or when routing selects it with the user's confirmation. The trace shows the steps,
   and the audit log records the playbook name and version with every decision.
4. Playbooks sit next to profiles and Knowledge in the UI, as a tab on the context page. They are not under Integrations.
5. Every playbook comes with eval cases. A skipped or reordered step is a failure.

## Consequences

- Known operational flows become repeatable and reviewable in Git, and enforcement does not depend on the model's compliance.
- Policy gains one more input, the current step, so the policy engine and its tests grow. A playbook's tool list is validated against
  the profile when the playbook is applied.
- Implementation is tracked separately; until it lands, runbooks stay Knowledge documents.
