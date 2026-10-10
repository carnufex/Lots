#!/usr/bin/env bash
# Lints the Helm chart and fails if the rendered manifests are not restricted-SCC (OpenShift) friendly.
set -euo pipefail
cd "$(dirname "$0")/.."

ARGS=(--set database.existingSecret=db --set auth.oidc.authority=https://idp.example/ --set mcpHomelab.enabled=true
      --set ingress.enabled=true --set route.enabled=true --set gitops.enabled=true --set gitops.repo=https://git.example/cfg.git
      --set mcpToolpack.enabled=true --set mcpToolpack.kubernetesReadOnly=true
      --set metrics.serviceMonitor.enabled=true --set metrics.grafanaDashboard=true --set metrics.alerts.enabled=true
      --set networkPolicy.enabled=true --set networkPolicy.cilium.enabled=true --set 'networkPolicy.cilium.toolpackFqdns={docs.example.com}'
      --set 'egress.allowedHosts={*.corp.example}'
      --set 'secretFiles[0].secretName=cmdb' --set 'secretFiles[0].mountPath=/run/secrets/cmdb')

helm lint charts/lots "${ARGS[@]}" >/dev/null
OUT="$(helm template t charts/lots "${ARGS[@]}")"

fail() { echo "chart check FAILED: $1" >&2; exit 1; }

echo "$OUT" | grep -qE 'hostPath|hostNetwork|hostPID|privileged: true' && fail "host access or privileged container"
echo "$OUT" | grep -qE 'runAsUser: 0\b|runAsNonRoot: false' && fail "runs as root"
echo "$OUT" | grep -qE 'runAsUser:' && fail "fixed runAsUser (breaks OpenShift's arbitrary UID assignment)"
echo "$OUT" | grep -qE 'containerPort: ([0-9]|[0-9]{2}|[0-9]{3})$' && fail "privileged port"

WORKLOADS=$(echo "$OUT" | grep -c '^kind: Deployment')
CONTAINERS=$(echo "$OUT" | grep -cE '^\s+image: ')
[[ "$(echo "$OUT" | grep -c 'runAsNonRoot: true')" -eq "$WORKLOADS" ]] || fail "every workload needs runAsNonRoot"
[[ "$(echo "$OUT" | grep -c 'allowPrivilegeEscalation: false')" -eq "$CONTAINERS" ]] || fail "every container needs allowPrivilegeEscalation: false"
[[ "$(echo "$OUT" | grep -c 'readOnlyRootFilesystem: true')" -eq "$CONTAINERS" ]] || fail "every container needs a read-only root filesystem"

echo "$OUT" | grep -q '169.254.0.0/16' || fail "network policy must exclude the metadata range"

# Required values must be enforced.
helm template t charts/lots --set auth.oidc.authority=https://idp.example/ >/dev/null 2>&1 && fail "renders without database.existingSecret"
helm template t charts/lots --set database.existingSecret=db >/dev/null 2>&1 && fail "renders Oidc mode without an authority"

echo "chart ok ($WORKLOADS workloads, $CONTAINERS containers)"
