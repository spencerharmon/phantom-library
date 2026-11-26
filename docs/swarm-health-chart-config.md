# Durable swarm-health plugin configuration (chart 2.13.0)

Supply the endpoint at deployment time; the chart knows no release roles or site URLs:

```yaml
swarmHealth:
  enabled: true
  serviceUrl: "http://swarm-health.example.com:8095"
```

`enabled` controls **chart management**, not whether an operator-configured service
is running. Defaults (`false`, `""`) neither enable measurement on a new PVC nor
change a retained PVC's service URL. To explicitly disable a previously enabled
service, keep `enabled: true` and set `serviceUrl: ""`. To return ownership to the
operator, use `enabled: false`; the last persisted setting is retained.

## One startup path

The former Prowlarr-only `sed` init container is replaced by `wire-plugin-config`.
It executes the chart-packaged `files/merge-plugin-config.py` using a digest-pinned
Python stdlib image (`pluginConfigMerge.image`). The ConfigMap, volume and init
container are workload-scoped, and share the workload's release-derived name.
`PHANTOM_SWARM_HEALTH_SERVICE_URL` is passed as a quoted Kubernetes env value only
when management is enabled; it is never interpolated into shell or Python source.
Prowlarr settings use this same merge, with the existing mounted Secret contract.
No image-entrypoint update or plugin code change is necessary.

Before Jellyfin starts, the merge creates a minimal `<PluginConfiguration>` on a
fresh PVC or updates only the managed direct child fields in existing
`plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml`. Jellyfin's real
serializer supplies all other defaults on first load. Nested/unknown settings,
comments and unrelated secrets remain; file mode and ownership are retained.
Writes use a same-directory temporary file, fsync and atomic replace. Equal output
is a no-op. Existing malformed XML, a DOCTYPE, wrong root, duplicate managed
fields, unreadable/empty Prowlarr secret or invalid input fails init, without
replacing the original file or printing its contents/secrets.

Helm schema rejects non-boolean `enabled`, missing/null/non-string URLs. At runtime,
nonempty URLs must be HTTP(S) base URLs with a hostname and a valid optional port,
without credentials, whitespace, raw double quotes/backslashes/angle brackets,
query or fragment. Paths (including `&`/apostrophes and percent-encoded characters)
are supported. XML text is escaped by a real serializer; JSON values files and
Kubernetes YAML quoting preserve values independently. An empty managed URL is the
explicit disable case, not a fallback to the old PVC value.

URL/enable changes alter the Pod template; script changes alter its checksum
annotation. Both roll the StatefulSet and re-run the merge **before** the plugin
loads. A PVC edit alone is not a rollout procedure. Do not hand-edit live PVCs.
Image/plugin updates continue to leave the configurations directory intact.

## Evidence / development

```sh
./scripts/tests/p05-swarm-health-config.test.sh
```

The check renders the real chart, follows its ConfigMap/mount/command/env wiring
and executes the packaged startup program on fresh and retained synthetic PVCs.
It builds the plugin and pinned Jellyfin fork, boots six isolated actual Jellyfin
processes, reads `/Plugins/{id}/Configuration`, and observes the real
`SwarmHealthWorker`/HTTP client sending both movie and episode hashes to a loopback
HTTP service. Stored peer counts are checked in the rig's synthetic SQLite DB,
then re-proven on restart. Explicit clear is checked over a full worker interval
with due candidates present. No fake plugin consumer, production DB clone, cluster
mutation, DHT network dependency or shared host rig is involved.

Requires .NET 9, Python3/PyYAML and ffmpeg; Helm has the existing vendored fallback.
An empty `jellyfin/` is bootstrapped from the repository's pinned fork archive
`248462dbb7f62f70b1473e6c22e54a7cfd05a5c9`; no installed host Jellyfin is used.
The HTTP service gives deterministic counts (11 movie / 12 episode); this proves
config delivery and measurement transport, **not live DHT quality or playback**.
The existing swarm-health service tests and terminal deployment acceptance own
those separate effects. All processes/data are test-owned and cleaned on exit.

Publish with the existing `helm package` / `helm push` flow to a runtime-provided
OCI registry, pull the resulting version back, record the manifest digest and
compare the unpacked chart bytes. The consuming deployment owns release values,
immutable chart pin, effective configuration checks and rollout verification; this
chart change never mutates production routing.
