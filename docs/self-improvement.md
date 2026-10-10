# Self-improvement

Lots records how every run went, and an agent can read those records to find what goes wrong. That agent can only read: its output
is a proposal that a person reviews (ADR 0019). This page covers what exists today.

## The loop

1. **Observe.** Every finished run gets an outcome row: problems, signals and timing, without content (see [Operating Lots](operations.md#run-outcomes)).
   Traces, logs and metrics add detail when the observability stack runs.
2. **Analyse.** The `self-improve` profile gives an agent read-only tools over outcomes, traces, audit, logs and metrics.
3. **Mine.** Runs that went wrong are clustered and turned into draft eval cases that a person accepts, edits or rejects (below).
4. **Propose, evaluate, approve.** A proposal is a reviewed Git change to a profile, with its eval delta, merged by a person (below).
5. **Follow up.** After the merge, the new profile version's outcomes are compared with the old one's.

## The Insights page

**Insights** (Administration) gives people the same view, for the roles in `Insights:Roles`:

- **Overview:** run outcomes with success rate, p95, tool errors, bad ratings and cost, a daily trend, and tables per profile version,
  model, channel and tool. The latest runs with a problem link to their run and, when `traceUrl` is set, to their trace.
- **Failures:** the mining queue. Accept a candidate, edit it first, or reject it with a note, run the mining now, or download the
  accepted cases.
- **Proposals:** each proposal with its rationale, diff and eval delta. Open its pull request, or reject it. After a merge, check the
  effect: the follow-up verdict, and a revert diff when it regressed.

It works with only PostgreSQL. The trace links appear when a tracing UI is configured.

## The introspection tools

| Tool | Answers |
|---|---|
| `outcomes_summary` | success rate, p50/p95, cost and top problems per profile version and channel |
| `list_failed_runs` | runs with a problem (timeout, denied, tool error, refused, rated bad, ...), with run ids |
| `get_run_trace` | one run's steps with time, tokens and policy decisions |
| `get_run_audit` | one run's audit rows |
| `tool_error_breakdown` | calls, errors and kinds of errors per tool |
| `slow_stages` | model, tool and total time per profile version and channel, the slowest runs |
| `compare_profile_versions` | the versions of one profile side by side |
| `retrieval_misses` | knowledge searches that found nothing |
| `search_logs` | shell log lines from Loki (`Introspection:LokiUrl`) |
| `promql_query` | PromQL over Lots metrics only (`Introspection:PrometheusUrl`) |

**Bounded.** At most 30 days, 25 rows per answer, and the tool output cap.

**Content.** Prompts, tool arguments and results and error text are shown only for runs the caller may read anyway (their own, or
any run as admin). Other runs show metadata only.

**Untrusted.** Everything comes back inside the untrusted-data envelope, and injected instructions are flagged like any tool output.
A run that read them needs an approval for writes, and this profile has no writes.

## Using it

Apply [`examples/profiles/self-improve.yaml`](https://github.com/carnufex/Lots/blob/main/examples/profiles/self-improve.yaml) and
give the analysts the `self-improve` role (admins have it implicitly through the profile).

**In Lots.** Chat with the `self-improve` profile: "Why did voice runs fail yesterday?"

**From Claude Code or another MCP client.** The same tools are an MCP server at `/mcp/introspect`. Sign in with an API token with the
`read` scope:

```bash
claude mcp add --transport http lots-introspect https://lots.example.com/mcp/introspect --header "Authorization: Bearer lots_pat_..."
```

Calls from outside go through the same policy as runs. The token's user needs a role that the `self-improve` profile grants, and every
call is an audit row (empty run id, reason "via /mcp/introspect").

**Evals.** `evals/self-improve.json` checks that the agent cites run ids, compares versions with numbers, refuses to change anything,
and that an operator gets no access.

## Failure mining

Once a day (`Mining:IntervalHours`), or on demand with `POST /insights/mine`, Lots looks at the run outcomes of the last
`Mining:WindowDays` days. It selects the runs with a signal:

- failed, timed out or hit the step limit;
- a policy denial or a refused approval;
- a tool error, an empty answer, or a voice run that skipped the tools;
- rated bad, asked again by the user, or more than three times slower than usual.

**Clustering.** Runs are grouped by signature: profile version, problem, and for tool problems the tool and the kind of error.
With an embedding model, a signature is split further by how alike the prompts are (`Mining:Similarity`, default 0.82). Clusters are
ranked by impact, which is the number of runs times the severity.

**Drafts.** Each cluster gets a draft eval case. Its question comes from the most recent run, with secrets and every kind of personal
data masked. Its expectations follow the problem:

- forbidden tools for a denial;
- the expected tool for a tool error;
- the tools to use for a voice run that skipped them;
- judge criteria in every case.

**Review.** `GET /insights/candidates?state=open` is the queue. For each candidate, a person:

- accepts it as is or edited (`POST /insights/candidates/{id}/accept`, body `{"case": {...}}`);
- or rejects it with a note (`.../reject`).

A decision stands: mining updates open candidates only. Accepted cases form the dataset `GET /insights/eval-cases`. Save it under
`evals/` and it runs in Lots.Evals and the regression gate. Readers and reviewers are the roles in `Insights:Roles` (default admin,
auditor, self-improve).

## Proposals

A proposal changes one profile's `instructions`, `description`, `model` alias or `detectConflicts`, and nothing else. It is rejected
if the result differs in roles, tools and risk classes, servers and credentials, approval rules, policy tests, sensitivity, personal
data masking, delegates or telemetry. The change is made as text: comments and layout stay, the version goes up by one, and the
proposal carries a diff.

1. **Create.** The `self-improve` agent calls `propose_change` with the complete new text and the run ids it rests on. A person can
   also create one with `POST /insights/proposals`. Nothing changes in the running shell.
2. **Evaluate.** `dotnet run --project src/Lots.Evals -- --mode proposal --proposal <id> --file evals/<dataset>.json` applies the proposed
   profile temporarily under its own name (`<profile>-proposal-<id>`) and runs the dataset against both. It records the delta with the
   proposal: pass rates, metrics, regressed and fixed cases. Then it removes the temporary profile. A proposal that regresses any case is
   marked so. The identity needs admin rights, because it applies a profile.
3. **Pull request.** `POST /insights/proposals/{id}/pr` opens a pull request in the configuration repository with the proposed file,
   the rationale and the eval delta. It needs `Proposals:Git` (`Provider` github or gitea, `ApiUrl`, `Repo`, `BaseBranch`,
   `PathTemplate`, and the token in the environment variable `TokenEnv`). It is refused if the file on the base branch has moved on.
   Without Git, apply the diff by hand.
4. **Merge.** A person reviews and merges, and GitOps applies the new version.
5. **Follow up.** `GET /insights/proposals/{id}/follow-up` compares the outcomes of the new version with the old once each has
   `Proposals:FollowUpMinRuns` (20) runs. It answers `improved`, `no-effect` or `regressed`, the last with a revert diff.

`GET /insights/proposals` lists them, and `.../reject` closes one with a note.

