{{/*
Common helpers for the phantom-library chart (single-environment).
*/}}

{{/* Chart name (fixed). */}}
{{- define "phantom-library.name" -}}
phantom-library
{{- end -}}

{{/* Resource-name suffix: "-<nameSuffix>" or "" when unset. */}}
{{- define "phantom-library.suffix" -}}
{{- with .Values.nameSuffix }}-{{ . }}{{ end -}}
{{- end -}}

{{/* Base resource name: phantom-library[-<nameSuffix>]. */}}
{{- define "phantom-library.fullname" -}}
phantom-library{{ include "phantom-library.suffix" . }}
{{- end -}}

{{/* Value of the `color:` label (blue/green flip tooling). Defaults to nameSuffix; may be empty. */}}
{{- define "phantom-library.colorLabel" -}}
{{- default .Values.nameSuffix .Values.colorLabel -}}
{{- end -}}

{{/* Common labels applied to every object. */}}
{{- define "phantom-library.labels" -}}
app.kubernetes.io/name: phantom-library
app.kubernetes.io/part-of: phantom-library
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: phantom-library-{{ .Chart.Version }}
{{- end -}}

{{/*
Workload selector labels — stable, minimal, and INSTANCE-scoped so two workload releases (blue +
green) in one namespace never cross-select each other's Pods, and the workload Service never selects
a Prowlarr Pod that shares the release (component discriminator).
*/}}
{{- define "phantom-library.workloadSelector" -}}
app.kubernetes.io/name: phantom-library
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/component: jellyfin
{{- end -}}

{{/*
gostorm-UI hostname: explicit .Values.gostream.hostname, else "gostorm." prepended to .Values.hostname.
*/}}
{{- define "phantom-library.gostormHost" -}}
{{- default (printf "gostorm.%s" .Values.hostname) .Values.gostream.hostname -}}
{{- end -}}

{{/* TLS secret name: explicit .Values.tls.secretName, else "<fullname>-tls". */}}
{{- define "phantom-library.tlsSecret" -}}
{{- default (printf "%s-tls" (include "phantom-library.fullname" .)) .Values.tls.secretName -}}
{{- end -}}

{{/*
Fully-qualified image ref for a container ("gostream" or "jellyfin").
Usage: {{ include "phantom-library.image" (dict "which" "gostream" "root" $) }}
*/}}
{{- define "phantom-library.image" -}}
{{- $img := index .root.Values.images .which -}}
{{- printf "%s@%s" $img.repository $img.digest -}}
{{- end -}}
