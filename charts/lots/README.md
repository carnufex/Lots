# Lots Helm chart

Runs the Lots shell. Optionally it also runs the voice service on a GPU, the homelab MCP server and the tool packs. It works on plain
Kubernetes and on OpenShift's restricted SCC:

- containers never run as root, and the platform assigns the UID;
- no host mounts or privileged ports;
- read-only root filesystems with all capabilities dropped.

`scripts/check-chart.sh` enforces this on every CI run.

```bash
helm install lots charts/lots -n lots -f charts/lots/examples/kubernetes.yaml    # or examples/openshift.yaml
```

## Required

- `database.existingSecret`: a Secret with the PostgreSQL connection string. PostgreSQL itself is external.
- `auth.oidc.authority`: in the default `Oidc` mode.
- `voice.apiKey.existingSecret`: when `voice.enabled`.

## Voice on a GPU

`voice.enabled: true` runs the voice service with one `nvidia.com/gpu`. It needs the NVIDIA GPU operator or device plugin, and
`runtimeClassName` if your cluster uses a runtime class. The shell is wired to it automatically.

- **`/models` must be persistent.** It holds several GB of models and the users' own voice references, which are personal data
  (ADR 0015). Use `voice.models.existingClaim`, or `voice.models.create: true`. A created claim is kept on uninstall, so delete it
  yourself when you erase the data.
- **First start.** An init container downloads the Piper voices, and Whisper and Chatterbox are fetched on first use. Allow internet
  egress once; with `networkPolicy.enabled`, `voice.modelDownloadEgress` opens 443 to public addresses. Empty that list afterwards.
  Only the shell may call the voice service.
- **GPU memory.** Whisper medium plus Piper needs about 3 GB. `chatterbox: true` adds about 3.5 GB. The service falls back to the
  fast voice when memory runs low.
- **Voice elsewhere.** To use a voice service outside the cluster (a GPU workstation), set `voice.url` and `voice.apiKey`
  and leave `enabled: false`.

## Values

Generated from the comments in `values.yaml` by `python scripts/chart-values-doc.py`. CI fails when this table is stale.

<!-- values:begin -->
| Key | Default | Description |
|---|---|---|
| `image.repository` | `ghcr.io/carnufex/lots-shell` |  |
| `image.tag` | `""` | defaults to the chart appVersion |
| `image.pullPolicy` | `IfNotPresent` |  |
| `imagePullSecrets` | `[]` |  |
| `replicaCount` | `1` | Replicas are safe: every replica runs a worker, and a run is executed by whoever holds its lease. |
| `auth.mode` | `Oidc` | Oidc \| Dev. Dev authenticates everyone as one user: local development only. |
| `auth.oidc.authority` | `""` | required for Oidc, e.g. https://authentik.example.com/application/o/lots/ |
| `auth.oidc.clientId` | `lots` |  |
| `auth.oidc.audience` | `""` |  |
| `auth.oidc.userClaim` | `sub` |  |
| `auth.oidc.roleClaim` | `roles` |  |
| `auth.oidc.rolePrefix` | `""` | e.g. "lots-" when the IdP's group claim is the role claim: group lots-admin = role admin |
| `auth.adminRoles` | `admin` |  |
| `auth.auditRoles` | `"admin,auditor"` |  |
| `database.existingSecret` | `""` | PostgreSQL is external. The Secret must contain the Npgsql connection string. |
| `database.connectionStringKey` | `connection-string` |  |
| `database.migrateOnStartup` | `true` |  |
| `model.baseUrl` | `""` | OpenAI-compatible endpoint, e.g. http://ollama:11434/v1 |
| `model.name` | `""` |  |
| `model.apiKey.existingSecret` | `""` |  |
| `model.apiKey.key` | `api-key` |  |
| `model.embedModel` | `""` | Embedding model on the same endpoint (knowledge, ADR 0016), e.g. nomic-embed-text or bge-m3. Empty: knowledge search is off. |
| `voice` |  | Voice (#133, ADR 0012): speech to text and speech output. Either run the voice service in this chart on a GPU node (enabled: true), or point the shell at one running elsewhere (url), for example on a separate GPU machine. |
| `voice.enabled` | `false` |  |
| `voice.url` | `""` | external voice service, e.g. http://gpu-pc.lan:8700/v1 (used when enabled is false) |
| `voice.apiKey.existingSecret` | `""` | Secret holding the key the shell and the voice service share (required when enabled) |
| `voice.apiKey.key` | `api-key` |  |
| `voice.defaultLanguage` | `sv` | sv \| en \| auto: the dictation language when the user has not chosen one |
| `voice.voices.sv` | `sv-nst` | cb-default = the expressive voice (needs chatterbox: true) |
| `voice.voices.en` | `en-ljspeech` | public domain; en-lessac is research-only (VOICE_RESEARCH_VOICES) |
| `voice.vocabulary` | `""` | deployment-wide dictation words, comma separated (names, products) |
| `voice.image.repository` | `registry.example.com/lots-voice` | built from services/voice (VOICE_CHATTERBOX=1 for the expressive voice) |
| `voice.image.tag` | `""` |  |
| `voice.image.pullPolicy` | `IfNotPresent` |  |
| `voice.chatterbox` | `false` | expressive voice and own voices (ADR 0015): ~3.5 GB extra VRAM; the image must contain it |
| `voice.sttCompute` | `float16` | int8_float16 halves Whisper's VRAM at a small accuracy cost |
| `voice.maxConcurrency` | `2` |  |
| `voice.gpu.count` | `1` | GPUs requested; 0 runs on CPU (slow, for trying it out) |
| `voice.gpu.resourceName` | `nvidia.com/gpu` |  |
| `voice.runtimeClassName` | `""` | e.g. nvidia, when the GPU operator installs a runtime class instead of the default runtime |
| `voice.models` |  | /models holds the models (several GB) and the users' own voice references (personal data). Use a persistent claim: existingClaim, or create: true to let the chart create one (kept on uninstall). Neither: an emptyDir, re-downloaded on every start and own voices lost. |
| `voice.models.existingClaim` | `""` |  |
| `voice.models.create` | `false` |  |
| `voice.models.size` | `30Gi` |  |
| `voice.models.storageClassName` | `""` |  |
| `voice.fetchModels` | `true` | init container that downloads the Piper voices into an empty volume (needs internet once) |
| `voice.modelDownloadEgress` | `(list, see values.yaml)` | Egress for model downloads when networkPolicy.enabled. Empty it after the first start to cut the voice service off entirely. |
| `voice.podSecurityContext` | `{}` | plain Kubernetes with a root-owned volume: { fsGroup: 10001 }. OpenShift assigns fsGroup itself. |
| `voice.extraEnv` | `[]` | e.g. [{ name: VOICE_MIN_FREE_VRAM_MB, value: "1500" }] |
| `voice.resources.requests` | `{ cpu: 500m, memory: 4Gi }` |  |
| `voice.resources.limits` | `{ memory: 12Gi }` |  |
| `voice.nodeSelector` | `{}` | e.g. { nvidia.com/gpu.present: "true" } |
| `voice.tolerations` | `[]` | e.g. [{ key: nvidia.com/gpu, operator: Exists, effect: NoSchedule }] |
| `voice.affinity` | `{}` |  |
| `audio` |  | Conversation audio (ADR 0014): stored encrypted for audioRetentionDays on this PVC (shared by replicas). Empty = not stored. |
| `audio.existingClaim` | `""` |  |
| `audio.retentionDays` | `30` |  |
| `knowledge` |  | Knowledge sources of kind "directory" read files under /knowledge. Mount a PVC there (synced from Git by your own job/sidecar). Sources themselves are config as code: Knowledge__Sources__0__Id etc. through extraEnv, or created on the Knowledge page. |
| `knowledge.existingClaim` | `""` |  |
| `gitops` |  | GitOps (#68): a git-sync sidecar keeps a checkout of your configuration repository; the shell applies every YAML file under `path` as gitops-managed (read-only in the UI) and prunes what was removed. Alternatively run `lotsctl apply` from your CD. |
| `gitops.enabled` | `false` |  |
| `gitops.repo` | `""` | e.g. https://git.example.com/ops/lots-config.git |
| `gitops.ref` | `main` |  |
| `gitops.path` | `""` | sub-directory with the resources ("" = repository root) |
| `gitops.period` | `60s` |  |
| `gitops.prune` | `true` |  |
| `gitops.credentialsSecret` | `""` | optional Secret with GITSYNC_USERNAME / GITSYNC_PASSWORD (HTTPS) |
| `gitops.image` | `registry.k8s.io/git-sync/git-sync:v4.4.2` |  |
| `extraEnv` | `[]` | Plain extra environment variables (non-secret configuration such as Models__Aliases__voice__..., Knowledge__Sources__...). |
| `profiles` |  | Profiles. Empty: use the profiles baked into the image. Otherwise: file name -> YAML manifest, mounted as a ConfigMap. |
| `profiles.files` | `{}` |  |
| `profiles.defaultProfile` | `""` |  |
| `extraSecretEnv` | `[]` | Extra environment variables sourced from Secrets, e.g. the password a profile references via passwordEnv. |
| `secretFiles` | `[]` | Secrets mounted as files, for file: references in profiles (#87). Files are re-read on every use, so a rotated secret (ExternalSecrets refresh, Vault Secrets Operator) takes effect without a restart. |
| `dataProtection` |  | Persistent Data Protection keys (needed for delegated identity, ADR 0011: stored login tokens are encrypted with them). Use a PVC shared by all replicas. Empty: keys are ephemeral and delegated calls fail closed after a restart. |
| `dataProtection.existingClaim` | `""` |  |
| `metrics` |  | Metrics (#75) at /metrics: a ServiceMonitor for the Prometheus Operator and the Grafana dashboard as a sidecar ConfigMap. |
| `metrics.serviceMonitor.enabled` | `false` |  |
| `metrics.serviceMonitor.interval` | `30s` |  |
| `metrics.serviceMonitor.labels` | `{}` | e.g. release: kube-prometheus-stack |
| `metrics.grafanaDashboard` | `false` |  |
| `metrics.alerts` |  | PrometheusRule with the alerts of #83 (dependencies down, failing/stuck runs, approval backlog, slow model, GPU memory). |
| `metrics.alerts.enabled` | `false` |  |
| `metrics.alerts.failedRunRatio` | `0.2` |  |
| `metrics.alerts.approvalAgeSeconds` | `3600` |  |
| `metrics.alerts.modelP95Seconds` | `30` |  |
| `metrics.alerts.gpuFreeBytes` | `1073741824` |  |
| `otlpEndpoint` | `""` | OTLP endpoint (e.g. an OpenTelemetry Collector) for traces and logs of the shell, the MCP servers and the voice service (#140). Metrics stay on /metrics for Prometheus; deploy/observability/otel-collector.yaml is a collector config with tail sampling. |
| `telemetry.otlpMetrics` | `false` | also push metrics over OTLP: only when nothing scrapes /metrics, or they are counted twice |
| `telemetry.content` | `metadata` | off \| metadata \| redacted \| full: what traces carry of runs (#145); profiles may set their own |
| `telemetry.allowFullContent` | `false` | let a profile use content: full (no personal-data masking) |
| `telemetry.userHashKey.existingSecret` | `""` | Secret with a key for stable lots.user.hash values across restarts and replicas |
| `telemetry.userHashKey.key` | `user-hash-key` |  |
| `service.port` | `80` |  |
| `ingress.enabled` | `false` |  |
| `ingress.className` | `""` |  |
| `ingress.annotations` | `{}` |  |
| `ingress.host` | `lots.example.com` |  |
| `ingress.tls.secretName` | `""` |  |
| `route` |  | OpenShift: expose with a Route instead of an Ingress. |
| `route.enabled` | `false` |  |
| `route.host` | `""` |  |
| `route.tlsTermination` | `edge` |  |
| `mcpHomelab` |  | Homelab MCP server. It needs Docker access through a READ-ONLY socket proxy that you provide (no host mounts in this chart), so it is off by default. |
| `mcpHomelab.enabled` | `false` |  |
| `mcpHomelab.image.repository` | `ghcr.io/carnufex/lots-mcp-homelab` |  |
| `mcpHomelab.image.tag` | `""` |  |
| `mcpHomelab.dockerUrl` | `http://docker-proxy:2375` |  |
| `mcpToolpack` |  | Tool packs (#63/#64): fetch, files, SQL, OpenAPI, Kubernetes, monitoring, Git. Each pack is off unless configured in env. |
| `mcpToolpack.enabled` | `false` |  |
| `mcpToolpack.image.repository` | `ghcr.io/carnufex/lots-mcp-toolpack` |  |
| `mcpToolpack.image.tag` | `""` |  |
| `mcpToolpack.kubernetesReadOnly` | `false` | creates a ServiceAccount with a read-only ClusterRole (no secrets) and enables the kubernetes pack |
| `mcpToolpack.filesClaim` | `""` | PVC mounted read-only at /files (set env Files__Root: /files) |
| `mcpToolpack.env` | `{}` |  |
| `mcpToolpack.secretEnv` | `[]` | Prometheus__Url: http://kube-prometheus-stack-prometheus.monitoring:9090 Alertmanager__Url: http://kube-prometheus-stack-alertmanager.monitoring:9093 Git__Repos__0: my-org/* Fetch__AllowedHosts__0: docs.example.com |
| `egress` |  | Egress rules applied by the shell itself (#86): which hosts MCP servers, token endpoints, webhooks and knowledge URL sources may be on. Link-local/metadata addresses are always blocked unless allowLinkLocal is set. |
| `egress.allowedHosts` | `[]` | empty = any host; e.g. ["*.corp.example", "hooks.slack.com"] |
| `egress.allowPrivateNetworks` | `true` | backends usually live on private networks |
| `egress.webhookHosts` | `[]` | narrower list for notification/audit webhooks; empty = the default above |
| `networkPolicy` |  | Network-layer egress control (#86). Needs a CNI that enforces NetworkPolicy. |
| `networkPolicy.enabled` | `false` |  |
| `networkPolicy.ingressFrom` | `[]` | e.g. [{ namespaceSelector: { matchLabels: { kubernetes.io/metadata.name: ingress-nginx } } }] |
| `networkPolicy.egressCidrs` | `(list, see values.yaml)` |  |
| `networkPolicy.extraEgress` | `[]` |  |
| `networkPolicy.toolpackEgress` | `(list, see values.yaml)` | what the tool packs may reach besides DNS (the shell is never one of them) |
| `networkPolicy.cilium.enabled` | `false` | adds a CiliumNetworkPolicy limiting the tool pack to DNS names |
| `networkPolicy.cilium.toolpackFqdns` | `[]` | e.g. ["docs.example.com", "*.wiki.example.org"] |
| `resources.requests` | `{ cpu: 100m, memory: 256Mi }` |  |
| `resources.limits` | `{ memory: 512Mi }` |  |
| `nodeSelector` | `{}` |  |
| `tolerations` | `[]` |  |
| `affinity` | `{}` |  |
| `podAnnotations` | `{}` |  |
<!-- values:end -->
