# 0005 – Deployment targets

Status: accepted (2026-10-09)

## Decision
- Same container images everywhere: Docker Compose for local/small installs, Helm chart for Kubernetes and OpenShift.
- OpenShift compatibility from day one: arbitrary non-root UID, group-0 writable dirs, Routes/Ingress.
- Compatibility is verified in the homelab (OKD or Single Node OpenShift).
- Initial model: single-tenant installation per customer, in their environment or with a European provider.
