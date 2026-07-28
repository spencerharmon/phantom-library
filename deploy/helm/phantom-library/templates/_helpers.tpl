{{/*
Common helpers for the phantom-library chart.
*/}}

{{/* Chart name (fixed; resources are named phantom-library-<color> to match the pre-Helm manifests). */}}
{{- define "phantom-library.name" -}}
phantom-library
{{- end -}}

{{/* Common labels applied to every object. */}}
{{- define "phantom-library.labels" -}}
app.kubernetes.io/name: phantom-library
app.kubernetes.io/part-of: phantom-library
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: phantom-library-{{ .Chart.Version }}
{{- end -}}

{{/*
Role a color currently holds: "prod", "dev", or "" (none).
Usage: {{ include "phantom-library.roleOf" (dict "color" $color "root" $) }}
*/}}
{{- define "phantom-library.roleOf" -}}
{{- $color := .color -}}
{{- $root := .root -}}
{{- if eq $color $root.Values.roles.prod -}}
prod
{{- else if eq $color $root.Values.roles.dev -}}
dev
{{- end -}}
{{- end -}}

{{/*
Fully-qualified image ref for a container ("gostream" or "jellyfin") for a given color, honouring a
per-color override in .Values.colors.<color>.images.<which>, else the shared .Values.images.<which>.
Usage: {{ include "phantom-library.image" (dict "which" "gostream" "color" $color "root" $) }}
*/}}
{{- define "phantom-library.image" -}}
{{- $which := .which -}}
{{- $root := .root -}}
{{- $colorCfg := index $root.Values.colors .color -}}
{{- $img := index $root.Values.images $which -}}
{{- if and $colorCfg $colorCfg.images (index $colorCfg.images $which) -}}
{{- $img = index $colorCfg.images $which -}}
{{- end -}}
{{- printf "%s@%s" $img.repository $img.digest -}}
{{- end -}}
