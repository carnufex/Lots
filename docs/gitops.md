# Configuration as code: GitOps

Lots' configuration (profiles, knowledge sources) is declarative YAML. There are three ways to manage it, and they can be mixed:

| Managed by | How | Editable in the UI |
|---|---|---|
| `file` | manifests in the image or a ConfigMap (`Profiles__Path`, Helm `profiles.files`) | no |
| `gitops` | a Git repository applied by the shell's GitOps sync or by `lotsctl apply` | no |
| `api` | the admin API (`POST /admin/v1/apply`) or the admin pages | yes |

A resource applied from Git becomes read-only for the API and UI; Git wins. Every change, whatever its source, is a new version in
`config_versions` with who, when and the spec, and the admin pages show the diff between versions.

## Repository layout

Any directory of `.yaml`/`.yml` files; hidden folders (`.git`, `.github`) are ignored. Several resources may share a file, separated
by `---`. Example:

```yaml
kind: Profile
name: ops
version: 3            # must increase when the content changes
instructions: |
  You help the operations team.
servers:
  - name: k8s
    url: http://mcp-k8s:8080/mcp
tools:
  - name: list_pods
    risk: read
roles:
  - name: operator
    allow: [read]
---
kind: KnowledgeSource
id: runbooks
name: Runbooks
sourceKind: directory     # upload | directory | url
location: /knowledge/runbooks
readers: ["role:operator"]
```

Start a repository from what is running: `lotsctl export --url https://lots.example.com -o lots.yaml`.

## Option 1: the shell syncs the repository (Helm)

```yaml
gitops:
  enabled: true
  repo: https://git.example.com/ops/lots-config.git
  ref: main
  path: lots            # sub-directory with the resources
  credentialsSecret: lots-git   # GITSYNC_USERNAME / GITSYNC_PASSWORD for HTTPS
  prune: true
```

A git-sync sidecar keeps a checkout under `/git/current`; the shell applies it every minute as `gitops`, prunes resources that were
removed, and `GET /admin/v1/gitops` shows the last sync, the revision and the drift (what applying now would change).

## Option 2: your CD applies it (ArgoCD, pipelines)

Run `lotsctl` with a service identity that has the admin role (client credentials from your IdP):

```bash
lotsctl validate -f lots/                                  # offline, in the pull request
lotsctl diff -f lots/ --url "$LOTS_URL" --output markdown  # plan as a PR comment
lotsctl apply -f lots/ --url "$LOTS_URL" --prune           # on merge
```

With ArgoCD, put `lotsctl apply` in a `PostSync` hook Job of the application that deploys Lots. The token comes from
`--token-url`/`--client-id` with the secret in `LOTS_CLIENT_SECRET` (an ExternalSecret).
