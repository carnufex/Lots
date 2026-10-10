# Acme.Tickets: a Lots profile with its MCP server

Made from the `lots-profile` template. Three parts, each with its own kind of test:

| Part | File | Tested by |
|---|---|---|
| MCP server: the tools and the access to your system | `src/Acme.Tickets.Mcp/` | `dotnet test` (tools as plain code) |
| Profile: risk classes, roles, approvals, instructions | `profiles/tickets-profile.yaml` | its `policyTests`, checked by `lotsctl validate` and on every load |
| Agent behaviour: does it use the tools well and refuse what it may not do | `evals/tickets-profile.json` | `Lots.Evals` against a running shell |

## Run it

```bash
dotnet test
docker compose up --build -d          # needs the Lots stack running (network lots_default)
lotsctl validate -f profiles          # offline, the shell's own parser and policy tests
```

Add the profile to the shell: point `LOTS_PROFILES_DIR` at `profiles/` (or copy the file into the shell's profiles folder), restart
the shell, or apply it with `lotsctl apply -f profiles --url http://localhost:8088`. Then, from the Lots repository:

```bash
dotnet run --project src/Lots.Evals -- --file <this folder>/evals/tickets-profile.json --dev-user claude-test-evals --dev-roles support
```

## Make it yours

1. Replace `TicketStore` with a client for your system. Give it the narrowest credentials that work. The secret goes in an environment
   variable or a secret store, never in the profile.
2. Write one tool per question people ask. Name and describe it so the model can tell when to use it. Return text the model can read.
   Make errors answers ("No ticket #999."), not exceptions.
3. Classify every tool in the profile. When in doubt, use the higher class.
4. Write the instructions: the domain, which tool answers what, and how to report results. Instructions never grant permissions.
5. Add a policy test for every rule you care about, and an eval case for every question the profile must handle well or must refuse.

Guide: `docs/authoring-profiles.md` in the Lots repository.
