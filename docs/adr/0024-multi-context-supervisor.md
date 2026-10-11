# 0024. Multi-context questions: a tool-less supervisor over read-only sub-runs

Status: accepted (2026-10-11). Implements #151, builds on ADR 0023.

## Context

Some questions span contexts ("do the firing alerts relate to the crash-looping pods?"). One profile cannot answer them, and merging
the tools of several profiles into one prompt would hurt tool selection and mix their policies.

## Decision

1. **When.** The router answers `multi` when two or three contexts fit about equally, or a runner-up is itself strongly relevant, and
   the user can only *read* in all of them. A user who could write in one of them is asked instead. `POST /runs` also accepts
   `contexts: [..]` (2-3, all read-only for the user) for "ask them all".
2. **How.** The run becomes a supervisor (`SuperviseJson`). It has no tools. It starts one sub-run per context in parallel through the
   same code as `delegate` (#103): the same user and roles, the parent's data class and taint, the step and time limits of
   `Agent:Delegation`, and `ReadOnly`.
3. **Read-only is policy, not prompt.** `Principal.ReadOnly` makes `PolicyEngine.Decide` deny every non-read tool, so the choke point
   (`ToolInvoker`) refuses a write even if a model asks for one that it was not offered.
4. **What the supervisor sees.** Only the sub-runs' final answers, inside the untrusted-data envelope, never their tool output. An
   answer from a context that used none of its tools is passed on as "could not answer", so invented "tool output" written as text
   cannot become evidence. A context the user cannot use, or one that fails, degrades the answer instead of failing it.
5. **Traced.** One trace: a `supervise` span with a `context <name>` span per sub-run; `context:<name>` steps on the run; the outcome
   records the contexts asked; the routing eval has `multi` cases, and single-context cases guard against unnecessary fan-out.

## Consequences

- A multi answer costs one model loop per context plus one composing call; the margins (`Routing:*:MultiScore`, `MultiMargin`) are
  tuned with the routing eval.
- Write-class work is never fanned out; it stays a single-context run with approvals as usual.
