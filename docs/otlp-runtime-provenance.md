# OTLP Runtime Provenance

`PhantomMetricsExporter` emits an Information-level structured event named
`PhantomMetricsProvenance` after building its provider and seeding playback
outcomes. It contains:

| Structured field | Meaning |
| --- | --- |
| `ServiceInstanceId` | Exact `service.instance.id` from the built provider |
| `ServiceName`, `ServiceNamespace`, `ServiceVersion` | Actual provider resource identity, not assumed defaults |
| `AssemblyMvid` | `ManifestModule.ModuleVersionId` of the loaded plugin assembly |
| `PluginVersion` | Loaded assembly version (package 0.5.7.7) |
| `InformationalVersion` | Loaded assembly informational version, including the build's source revision when stamped |
| `SourceRevision` | Informational version suffix after `+`, or null when unstamped |
| `ProcessId`, `ProcessStartTimeUtc` | Host process PID and UTC start time, to distinguish PID reuse |

The default service name is `jellyfin-phantom-library`. Explicit
`OTEL_RESOURCE_ATTRIBUTES` and `OTEL_SERVICE_NAME` take precedence over defaults;
later builder resource customizations take precedence over those. Logging reads
the completed provider, never a separately generated identity. An intentionally
empty/replaced resource is logged as-is, including missing values, not repaired
with a fictitious identifier. Optional metadata is null if absent.

OpenTelemetry 1.10 generates the default instance ID once per SDK/process, not
once per provider. Repeated `StartAsync` while running is a no-op; stop disposes
the provider, and a subsequent start builds and logs a new provider with the same
SDK process identity. A new process normally gets a new generated ID, but an
operator-supplied ID may be reused across restarts. Never use that ID alone to
infer process freshness. PID is also not globally unique across containers.

## Correlation Procedure

This is a code prerequisite, not evidence of a deployment. The deployment owner
must build merged current main containing both this change and the 32-tuple
seeded exporter, archive its build provenance, then deploy that exact image
digest. Resolve the inactive rollout target immediately before repinning it;
do not infer it from an old color name. Do not change the active target.

1. Record the build commit, immutable image digest, packaged plugin DLL checksum,
   assembly MVID, assembly version and informational version in the CI artifact
   record. Stamp the actual checkout commit with MSBuild `SourceRevisionId` if
   the build context omits Git metadata. Do not invent a revision when absent.
   Inspect the *built artifact*, not a mutable running PVC path. For example,
   in a fresh PowerShell process on .NET 9 or newer with `$ArtifactDll` set to
   the absolute artifact path:

   ```powershell
   $a = [System.Reflection.Assembly]::LoadFile($ArtifactDll)
   $info = $a.GetCustomAttributesData() | Where-Object {
       $_.AttributeType.FullName -eq 'System.Reflection.AssemblyInformationalVersionAttribute'
   }
   [pscustomobject]@{
       Mvid = $a.ManifestModule.ModuleVersionId
       PluginVersion = $a.GetName().Version.ToString()
       InformationalVersion = if ($info) { $info.ConstructorArguments[0].Value } else { $null }
       Sha256 = (Get-FileHash -Algorithm SHA256 $ArtifactDll).Hash
   } | ConvertTo-Json
   ```

2. After rollout, capture pod and container identity plus timestamped logs. Set
   `NAMESPACE`, `POD` and `CONTAINER` from the freshly resolved target. Record
   pod UID, container ID, imageID digest, restart count and running start time:

   ```bash
   kubectl -n "$NAMESPACE" get pod "$POD" -o json | jq --arg c "$CONTAINER" \
     '{pod: .metadata.name, uid: .metadata.uid,
       container: [.status.containerStatuses[] | select(.name == $c) |
         {name, containerID, imageID, restartCount, state}]}'
   kubectl -n "$NAMESPACE" logs "$POD" -c "$CONTAINER" --timestamps
   ```

   Preserve the `Phantom OTLP runtime provenance` record from this container's
   current startup. Recapture pod/container metadata after the log read and
   reject the evidence if UID, container ID or restart count changed. Do not
   mix `--previous` logs with the current container. Match MVID, plugin version
   and informational revision against the archived artifact and its image
   digest. Package version 0.5.7.7 advances the entrypoint's PVC replacement
   gate; a correct image digest alone does not prove the PVC plugin was loaded.

3. Query the correct Mimir tenant and retain the response at an explicit UTC
   evaluation time after startup. Set `MIMIR_PROMETHEUS_URL` to the deployment's
   Prometheus API base and `EVALUATION_TIME` to that timestamp; supply the
   deployment's authentication/tenant headers as required:

   ```bash
   curl --fail --get "$MIMIR_PROMETHEUS_URL/api/v1/query" \
     --data-urlencode 'query=target_info' \
     --data-urlencode "time=$EVALUATION_TIME"
   ```

   Require API `status=success` and find the exact resource identity from the
   startup event. Standard OTLP-to-Prometheus translation uses
   `instance=ServiceInstanceId` and `job=ServiceNamespace/ServiceName` (or just
   `ServiceName` without a namespace). Other resource attributes such as
   `service.version` become labels such as `service_version`; deployments can
   change translation/promotion, so inspect actual labels before selecting.
   Filter subsequent queries by the exact observed job/instance, not just the
   service name or pod name. Verify sample timestamps with
   `timestamp(target_info{job="...",instance="..."})` after startup, and exclude
   stale samples from an old process, especially with a configured reused ID.
   Escape label values as PromQL strings; do not interpolate arbitrary values
   into an unescaped selector. Stop if identity is missing or ambiguous.

4. The parent deployment acceptance must verify exactly the fresh 32 playback
   outcome tuples (2 flows x 2 item types x 8 causes), zeros before attempts,
   then per-attempt increments, on this same proven instance. A provenance log,
   `target_info`, or successful unit test alone is not that acceptance. Preserve
   startup time, evaluation time, selectors and full sample responses together.

## Trust Limits

MVID identifies a loaded build module; it is not a cryptographic attestation.
An informational revision is build-supplied metadata, not proof that source was
clean. Correlate both with the trusted immutable CI artifact and image record.
Hashing a DLL currently at a replaceable PVC/file path only identifies that
file's bytes at read time: it does **not** prove the process loaded those bytes.
The plugin deliberately does not label such a hash as loaded provenance.
No new metric dimensions or changes to the 32-tuple seeding are introduced.
