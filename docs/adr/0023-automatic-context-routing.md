# 0023. Automatic context routing: a router in front of the run

Status: accepted (2026-10-11). Implements #150.

## Context

Users had to pick a profile before asking. With several profiles (homelab, cmdb, ...) that is friction, and a wrong pick gives a
confident wrong answer. Merging every profile's tools into one prompt would hurt tool selection and mix policy. A profile must stay the
unit of policy, tools and instructions (principles 2 and 3); only *who picks it* changes.

## Decision

1. **The shell routes, before the run.** `POST /runs` without a profile (or with `auto`) asks the router; `POST /route` returns the same
   decision without starting anything. The model never chooses its own context.
2. **Policy first.** Candidates are the contexts where one of the user's roles grants something. A context the user cannot use is not
   scored, so routing can never widen access. One usable context: the router is skipped.
3. **Cheap score.** Embedding similarity between the question and each context's description, tool names and `routing.examples`
   (profile YAML), cached per profile version and warmed at startup. When the embedding is unsure, a word overlap is the second
   opinion; when no embedding model answers in time, words alone. No main-model call, no tools, and the input is the user's message only.
4. **Stickiness.** A conversation keeps its context unless another one leads by a margin.
5. **Never a silent guess.** Low confidence, a close second, or a winner that lets the user *write* without a clear lead: the shell
   answers 409 with the candidates and the user picks with one click (`routing: chosen`).
6. **Visible and correctable.** Each turn shows its context; "use X instead" answers the turn again there (`corrected`) and marks the
   old run's outcome `misrouted`, which failure mining picks up.
7. **Traced and gated.** The run records `RoutingMode` and the scores (`lots.routing.*` span attributes, `run_outcomes.routing`).
   `Lots.Evals --mode routing` scores `evals/routing.json` (accuracy and p95 latency, `--min-accuracy`, `--max-p95-ms`).

In the UI profiles are called **contexts**; YAML and the API keep `profile`.

## Consequences

- Clients that sent no profile used to get the default profile; now they get the routed one (the default is still the fallback when
  the user can use no context at all, so policy denies as before).
- Thresholds depend on the embedding model (`Routing:Embedding:*`, `Routing:Lexical:*`); the routing eval is how a change is checked.
- Questions that span contexts are #151 (a supervisor with one sub-run per context); until then they are asked like a close call.
