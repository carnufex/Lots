# Concepts

## Runs

A **run** is one question and everything the agent did to answer it: the model calls, the tool calls, the approvals it waited for,
and the final answer. Runs are jobs, not HTTP requests. `POST /runs` returns at once, and a worker executes the run. The run can
pause, for example waiting for an approval, and continue later, on any replica. Chat turns, scheduled runs, webhooks, Slack and mail
all create runs.

A run is fixed to its **user**, the user's **roles** at the moment the run started, and a **profile**.

## Conversations

A **conversation** is what a person calls "a chat": an ordered set of turns, and every turn is a run that shares the
conversation's id. Earlier turns are part of the context of the next one. Runs that do not come from a person talking (scheduled
jobs, webhooks, API calls, sub-agents) have no conversation.

In the web UI this is one model:

- **Chat** is where conversations happen, typed or spoken. Voice is a *mode* of a conversation: dictation fills the message box,
  and voice mode talks hands-free with the avatar, in the same thread.
- **History** lists past conversations (search and filters), shows where their time went (**Timing**), and has a **Runs** tab
  with every run you may see, scheduled and API runs included.
- A run's own page (`#/runs/<id>`) shows its steps, policy decisions and trace, and links back to its conversation.

## Profiles

In the UI a profile is called a **context**. Users do not have to choose one: the shell routes every question to the context it
belongs to, among those the user's roles allow, asks with one click when it is unsure, and shows which context answered with a
"use X instead" (ADR 0023).

A question that spans contexts is answered by asking each of them: one read-only sub-run per context, and a supervisor without tools
that combines their answers with context labels (ADR 0024).

A **profile** is a domain: the tools the agent may use there and the rules for them. It is a YAML resource:

| Part | What it does |
|---|---|
| `servers` | MCP servers that provide the tools, and how the backend learns who is asking (`auth`, `credentials`) |
| `tools` | Every tool that may be used, with a **risk class**: `read`, `write` or `destructive`. Undeclared tools do not exist for the model |
| `roles` | Which risk classes a role may use, which need an approval, and which it may approve |
| `instructions` | What the model is told about the domain. Instructions never grant anything |
| `sensitivity` | The data class of the tool results, which decides which model endpoints may see them |
| `policyTests` | Allow, deny and approval expectations that must hold, or the profile is rejected |

Profiles are config as code: applied with `lotsctl`, through GitOps, or edited in the UI when they are API-managed. Each change is a new
version, and runs and audit rows record the version they ran under.

## Policy: one choke point

Every tool call, whatever produced it, goes through the shell's **ToolInvoker**:

1. Is the tool declared in the run's profile? If not, **deny**.
2. Does one of the run's roles grant the tool's risk class? If not, **deny**.
3. Does the class need an approval for that role, or has the run read text that looked like an injection attempt? Then **pause** for one.
4. Is the tool's data class above what the model endpoints may see? Then the tool is hidden and denied.
5. Otherwise, **call** the tool. The result is wrapped as untrusted data, with secrets and optionally personal data masked.

Each decision is an audit row: who, profile and version, tool, arguments, decision, approver and result. The audit log is
append-only and hash-chained.

## Approvals

An approval pauses the run, `WaitingForApproval`, until someone whose role may approve that risk class decides. The requester never
approves their own call. A profile can require two different approvers, a comment, or an expiry for a risk class. Approvers are
notified, and an approver can be out of office with a delegate.

## Identity

People sign in with OIDC. Roles come from a token claim, for example IdP groups `lots-*`. Toward backends, Lots prefers **delegated
identity**: a token exchanged per call, so the backend sees the user. Weaker strategies exist for systems that cannot do that:
per-role service accounts, a shared service account, or user-connected OAuth. The strategy is explicit per server and recorded in the
audit log. Service identities (`svc-*`) run schedules and webhooks with their own minimal roles.

## Models and data classes

Models are aliases (`default`, `voice`, `embed`, ...) mapping to one or more OpenAI-compatible endpoints, with fallback between them.
Every endpoint has a **clearance**: the highest data class it may see (`public < internal < confidential < restricted`). A run's
class rises with what it reads, and each model call is routed to an endpoint cleared for it, or blocked. A hosted model therefore never
sees confidential data unless an admin clears it.

## Knowledge

Knowledge sources (uploaded documents, directories, URLs) are indexed for retrieval. Search results are filtered by the asking user's
own access, so the agent sees only what the user may read, and answers cite their passages. Retrieved text is untrusted data like any
tool output.

## Voice

An optional GPU voice service does speech to text (Whisper) and speech output (Piper, or the expressive Chatterbox voice). It also
offers conversation mode in the browser and lets users record their own voice with consent. Audio of conversations is stored
encrypted for a retention period. Voice never adds permissions: a spoken question is a run like any other.

## Evals

Evals ask a running Lots the questions users would ask, as a dedicated identity, and score the answers:

- the right tool with the right arguments;
- facts in the answer;
- no forbidden tool;
- a refusal where policy denies;
- an optional LLM judge.

Results are kept and compared, and a regression fails the gate. Users' thumbs-down answers become new eval cases.
