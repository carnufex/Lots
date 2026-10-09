#!/usr/bin/env bash
# Creates labels, the M1 milestone and its issues in the current GitHub repo.
# Run from the repo root after `gh auth login`. Labels are idempotent; issues are not,
# so run this once.
set -euo pipefail

REPO="$(gh repo view --json nameWithOwner -q .nameWithOwner)"
echo "Bootstrapping $REPO"

# --- Labels -----------------------------------------------------------------
label() { gh label create "$1" --color "$2" --description "$3" --force >/dev/null; echo "label: $1"; }

label "needs-human"   "d93f0b" "Waiting for a decision from Christopher"
label "decision"      "5319e7" "Architectural decision; results in an ADR"
label "blocked"       "b60205" "Waiting on something outside the team"
label "type:feature"  "0e8a16" "New capability"
label "type:bug"      "d73a4a" "Something is broken"
label "type:chore"    "c5def5" "Tooling, CI, housekeeping"
label "area:core"     "1d76db" "Agent loop, runs, model client"
label "area:policy"   "1d76db" "Identity, policy, approvals, audit"
label "area:mcp"      "1d76db" "MCP client and profile servers"
label "area:ui"       "1d76db" "Frontend"
label "area:deploy"   "1d76db" "Containers, compose, Helm, OpenShift"
label "area:evals"    "1d76db" "Evaluation harness and test sets"

# --- Milestone --------------------------------------------------------------
M1="M1: Loop, homelab read tools, trace"
if ! gh api "repos/$REPO/milestones?state=all" -q '.[].title' | grep -qxF "$M1"; then
  gh api "repos/$REPO/milestones" -f title="$M1" \
    -f description="Agent loop against an OpenAI-compatible model, MCP client, read-only homelab MCP server, persisted trace. Runs with docker compose up." >/dev/null
fi
echo "milestone: $M1"

issue() { # title, labels, body
  gh issue create --title "$1" --label "$2" --milestone "$M1" --body "$3" >/dev/null
  echo "issue: $1"
}

# --- M1 issues --------------------------------------------------------------
issue "Solution skeleton and docker compose" "type:chore,area:deploy" \
"- .NET solution with Vertical Slice layout and FastEndpoints
- PostgreSQL with migrations
- Dockerfiles that run as an arbitrary non-root UID (see CLAUDE.md container rules)
- \`docker compose up\` starts shell + Postgres
- Health endpoint

Done when: compose starts cleanly and the health endpoint returns 200."

issue "Model client for OpenAI-compatible APIs" "type:feature,area:core" \
"- Chat completions with tool calling against any OpenAI-compatible endpoint
- Config: base URL, model, API key reference
- Verified against Ollama in the homelab
- Records tokens and latency per call

Done when: an integration test completes a tool-calling round trip against a local model."

issue "Agent loop as a persisted run" "type:feature,area:core" \
"- A run is a job, not an HTTP request: created, stepped, persisted, resumable
- Loop: call model, execute tool calls, feed results back, stop on final answer or max steps
- All tool calls go through a single \`ToolInvoker\` (policy hook; in M1 it allows read-only tools only)
- Tool output is treated as untrusted data

Done when: a run survives a restart of the shell mid-run and completes."

issue "MCP client" "type:feature,area:mcp" \
"- Connect to MCP servers over streamable HTTP using the official C# SDK
- List tools, map them to the model's tool format, call them via \`ToolInvoker\`
- Server list from config (profile manifests come in M2)

Done when: the loop can call a tool on an external MCP server."

issue "Homelab MCP server (read-only)" "type:feature,area:mcp" \
"Tools:
- \`list_containers\`
- \`get_container_logs\` (tail, with a size cap)

Docker is reached only through a read-only socket proxy, never the raw socket.
Large outputs are truncated with a clear marker so the context does not flood.

Done when: the agent can answer \"which containers are unhealthy and why?\" in the homelab."

issue "Trace of every step" "type:feature,area:core" \
"- Persist each model call and tool call: run, step, tool, arguments, result (truncated), latency, tokens
- Read endpoint: GET a run with all its steps
- Export via OpenTelemetry using the GenAI semantic conventions

Done when: a completed run can be read back step by step."

issue "Minimal API to start and inspect runs" "type:feature,area:core" \
"- POST a prompt to start a run, GET run status and trace
- No auth in M1 (local only); OIDC arrives in M2

Done when: a run can be started and followed with curl."

issue "CI pipeline" "type:chore,area:deploy" \
"- GitHub Actions: build, test, build container images
- Fails on test failures and on images that require root

Done when: every PR gets a green or red check."

issue "Eval harness skeleton" "type:feature,area:evals" \
"- A small set of questions with expected tool calls and expected answer facts
- Runs against a configured model and reports pass/fail
- Starts with 5 homelab questions

Done when: evals can be run with one command and produce a report."

# --- Decisions needed --------------------------------------------------------
issue "Decide: product name and domain" "needs-human,decision" \
"Working name is **Lots**. Alternatives discussed: LotsAI, AILots, Lots.ai.

Recommendation: product and repo name \`Lots\`, C# namespace \`Lots.*\`. Use a domain only for the website (e.g. a .eu domain fits the European positioning). Check GitHub, NuGet and trademark conflicts first.

Blocks: nothing in M1 (namespaces can be renamed cheaply now, expensively later)."

issue "Decide: license" "needs-human,decision" \
"Others should be able to run the shell with their own profiles.

Options:
1. Apache-2.0: maximal adoption, patent grant, others may build closed products on it.
2. AGPL-3.0: modifications offered as a service must be shared; deters some enterprises.
3. Source-available (e.g. BSL) or closed: keeps commercial options open, limits community.

Recommendation: decide before the repo becomes public. Apache-2.0 if the goal is adoption and portfolio value; keep the option of a commercial edition for enterprise features.

Blocks: making the repo public."

issue "Decide: default model for development" "needs-human,decision" \
"Options:
1. Local model via Ollama in the homelab: free and fully self-hosted, but tool-calling quality varies.
2. Hosted API model: best tool calling, faster iteration, data leaves the homelab.
3. Both: develop against a hosted model, run evals against local models.

Recommendation: 3. The loop is model-agnostic anyway, and the evals tell us which local models are good enough.

Blocks: the model client integration test."

echo "Done."
