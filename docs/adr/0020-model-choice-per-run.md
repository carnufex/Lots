# 0020. A run may choose a configured model alias and reasoning effort, restricted by role

Status: accepted (2026-10-10). Decides #119.

## Context

To pick models on evidence (ADR 0007 follow-ups), the same eval set has to run against several models and reasoning efforts. Until
now a run's model was fixed by its profile (`model:` alias) or the voice alias, so a comparison meant editing config and restarting
between runs. Letting a request name a model must not become a way around policy: arbitrary endpoints would bypass data
classification (ADR 0018), and an expensive hosted model could be used by anyone.

## Decision

1. `POST /runs` takes optional `model` (a **configured alias**, never a raw endpoint or model name) and `reasoningEffort`
   (`none|minimal|low|medium|high`). Both are stored on the run, shown in `GET /runs/{id}` and kept by retries.
2. Only roles in `Models:ChooseRoles` may set them (default `admin` and `evaluator`); anyone else gets 403. `evaluator` is meant for
   eval identities: no profile grants it tools, so adding it does not widen what a run can do.
3. The choice only replaces the alias. Routing by data class still decides per call which targets of that alias may see the run's
   data, so a choice can never send data to an endpoint that is not cleared for it.
4. `Lots.Evals --models a,b --efforts none,low` runs a dataset for every combination and writes a side-by-side report
   (`evals/report-models.md`): pass rate, completion, tool and argument accuracy, refusal, judge, p50/p95 latency, tokens and cost.
   Each combination is stored and diffed against its own history.

## Consequences

- Comparisons need no config change or restart, and their runs are ordinary runs: traced, audited and under quota.
- Cost data comes from the price table (`Usage`), so hosted models show their real cost next to local ones.
- The UI does not offer a model picker. Exposing one to end users is a separate decision.
