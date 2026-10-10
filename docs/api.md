# Lots API

The web UI, `lotsctl` and integrations use the same HTTP API. The contract is an OpenAPI 3 document generated from the endpoints:

- live: `GET /openapi/v1.json` on any shell (anonymous; it describes the API, it grants nothing)
- reviewed copy: [`docs/api/openapi.json`](api/openapi.json). A test fails when the endpoints and this file differ, so every API
  change shows up in review. After an intended change: `LOTS_UPDATE_OPENAPI=1 dotnet test --filter OpenApiContract`.

Generate a client from either with any OpenAPI generator (`openapi-generator`, NSwag, `openapi-typescript`, Kiota).

## Authentication

Every call needs `Authorization: Bearer <token>`:

- **OIDC access token** from the identity provider (what the web UI uses).
- **Personal API token** for scripts and CI: Usage page → API tokens, or `POST /me/tokens` while logged in. Tokens start with
  `lots_pat_`, are shown once, always expire, act as the person who made them and are limited to their scopes:

  | Scope | Allows |
  |---|---|
  | `read` | every `GET` the person may make |
  | `runs` | start, cancel and retry runs; voice endpoints |
  | `approvals` | approve and deny |
  | `admin` | every other change (profiles, knowledge sources, quotas, ...) if the person is an admin |

  A request outside the token's scopes gets `403` with the missing scope in the message.

## Stability

- Version **1**. Within it, changes are additive only: new endpoints, new optional request fields, new response fields. Clients
  must ignore response fields they do not know.
- Removing or renaming a field or endpoint, or changing its meaning, is a breaking change: it gets a new version and the old one
  keeps working for at least one release with a deprecation note in the changelog.
- Endpoints under `/admin/` and `/integrations/` are for operators and may change with a minor release; everything else is the
  integrator surface.
- Errors: `400` validation (body lists the fields), `401` no or bad token, `403` not allowed (roles, scopes, data class), `404` not
  found or not yours, `409` conflict, `429` quota or rate limit, `5xx` with a message safe to show.

## Examples

Start a run and wait for the answer.

### curl

```bash
export LOTS=https://lots.example.org LOTS_TOKEN=lots_pat_...
id=$(curl -s -X POST "$LOTS/runs" -H "Authorization: Bearer $LOTS_TOKEN" -H "Content-Type: application/json" \
  -d '{"prompt":"Which containers are unhealthy?","profile":"homelab"}' | jq -r .id)
until curl -s "$LOTS/runs/$id" -H "Authorization: Bearer $LOTS_TOKEN" | jq -e '.status|test("Completed|Failed|Cancelled")' >/dev/null; do sleep 1; done
curl -s "$LOTS/runs/$id" -H "Authorization: Bearer $LOTS_TOKEN" | jq -r .finalAnswer
```

A run that needs an approval pauses with status `WaitingForApproval`; someone allowed to approve decides on
`POST /approvals/{id}/approve` or `/deny`, and the run continues.

### C#

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;

using var http = new HttpClient { BaseAddress = new Uri("https://lots.example.org") };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("LOTS_TOKEN"));

var started = await (await http.PostAsJsonAsync("/runs", new { prompt = "Which containers are unhealthy?", profile = "homelab" }))
    .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Started>();
Run run;
do
{
    await Task.Delay(1000);
    run = (await http.GetFromJsonAsync<Run>($"/runs/{started!.Id}"))!;
} while (run.Status is "Pending" or "Running" or "WaitingForApproval");
Console.WriteLine(run.FinalAnswer ?? run.Error);

record Started(Guid Id, string Status);
record Run(Guid Id, string Status, string? FinalAnswer, string? Error);
```

### TypeScript

```ts
const base = 'https://lots.example.org'
const headers = { Authorization: `Bearer ${process.env.LOTS_TOKEN}`, 'Content-Type': 'application/json' }

const { id } = await (await fetch(`${base}/runs`, {
  method: 'POST',
  headers,
  body: JSON.stringify({ prompt: 'Which containers are unhealthy?', profile: 'homelab' }),
})).json()

let run: { status: string; finalAnswer: string | null; error: string | null }
do {
  await new Promise((r) => setTimeout(r, 1000))
  run = await (await fetch(`${base}/runs/${id}`, { headers })).json()
} while (['Pending', 'Running', 'WaitingForApproval'].includes(run.status))
console.log(run.finalAnswer ?? run.error)
```

### Other common calls

| Call | Purpose |
|---|---|
| `GET /runs`, `GET /runs/{id}` | your runs; one run with its step trace |
| `POST /runs/{id}/cancel`, `/retry` | stop a run; start it again |
| `GET /approvals` | pending approvals you may decide |
| `GET /knowledge/search?q=` | search the knowledge you may read |
| `GET /audit?user=&from=&to=` | audit log (admin, auditor) |
| `GET /usage`, `GET /me/quota` | tokens, cost and limits |
| `GET /me/export`, `DELETE /me/data` | your data |
