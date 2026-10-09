{{- define "lots.name" -}}{{ .Release.Name }}{{- end -}}

{{- define "lots.labels" -}}
app.kubernetes.io/name: lots
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version }}
{{- end -}}

{{- define "lots.selector" -}}
app.kubernetes.io/name: lots
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{/* Pod/container security: valid for the OpenShift restricted SCC (the UID is assigned by the platform). */}}
{{- define "lots.podSecurityContext" -}}
runAsNonRoot: true
seccompProfile:
  type: RuntimeDefault
{{- end -}}

{{- define "lots.containerSecurityContext" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: true
capabilities:
  drop: ["ALL"]
{{- end -}}
