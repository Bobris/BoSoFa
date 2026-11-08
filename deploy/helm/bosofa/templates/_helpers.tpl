{{- define "bosofa.name" -}}{{ .Release.Name | trunc 40 | trimSuffix "-" }}{{- end -}}
{{- define "bosofa.peers" -}}{{ include "bosofa.name" . }}-peers{{- end -}}
{{- define "bosofa.selector" -}}
app.kubernetes.io/name: bosofa
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}
{{- define "bosofa.labels" -}}
{{ include "bosofa.selector" . }}
app.kubernetes.io/version: {{ .Values.image.tag | default .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}
