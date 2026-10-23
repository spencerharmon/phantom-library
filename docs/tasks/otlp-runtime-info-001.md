# otlp-runtime-info-001: metrics-queryable loaded-build provenance

## Problem and scope

Day 14 has fresh dev zero seeds but no organic volume. Attribution is possible
through startup logs, while current `target_info` exposes only SDK metadata.
Make loaded-build identity queryable without requiring playback or retained
startup logs. This is one source-level instrumentation fix, not a deployment.
Existing `otlp-runtime-provenance-001` logging and
`playback-outcome-zero-seed-deploy-001` runtime verification are not reimplemented.

## Design

Publish `phantom_runtime_info` as an observable information gauge, value 1,
through the actual `PhantomMetricsExporter` provider on every collection.
Include loaded assembly MVID, plugin version and informational version/source
revision when available. Read loaded assembly metadata, never a mutable mounted
DLL path. Reuse the extraction semantics of `LogProvenance` where useful.

Inherit the completed provider's resource identity, including environment and
late builder overrides; never invent a second instance ID or replace configured
`service.version`. Keep build labels off the playback counters. Omit unavailable
optional metadata rather than fabricating a revision. Do not export endpoints,
secrets, host identifiers or per-item/user labels as build metadata.

Own the instrument lifecycle so repeated start, stop/restart and concurrent
providers cannot duplicate or cross-export provenance. Preserve existing
startup logs and all 32 outcome tuples and positive counter values. No changes
to playback behavior, seeding semantics, or production routing are in scope.

Invariant: an enabled production exporter publishes one truthful loaded-build
information point per collection with its own actual resource, even with no
playback, without altering movie/episode or cold/warm outcome accounting.

## Acceptance

Extend `PhantomFlowMetricsTests` using its real exporter/provider/reader seam,
not only a detached helper or MeterListener. Add failing-before/passing-after
tests that start `PhantomMetricsExporter` and collect twice without attempts:
exactly one gauge point of value 1 each time, loaded MVID/version equality and
correct resource identity. Cover environment/late builder overrides, missing
optional revision, duplicate start, stop/restart, disabled exporter, and two
providers with distinct configured identities and no leaked points. Retain
the existing four movie/episode x cold/warm seed/positive-count tests unchanged.

Check (approved `dotnet-test` framework):

```sh
env MSBUILDDISABLENODEREUSE=1 dotnet test tests/Jellyfin.Plugin.PhantomLibrary.Tests/Jellyfin.Plugin.PhantomLibrary.Tests.csproj -p:UseSharedCompilation=false --filter FullyQualifiedName~PhantomFlowMetricsTests
```

Run on the assigned fix branch using:

```sh
beehive submodule worktree exec phantom-library bee-otlp-runtime-info-001 -- env MSBUILDDISABLENODEREUSE=1 dotnet test tests/Jellyfin.Plugin.PhantomLibrary.Tests/Jellyfin.Plugin.PhantomLibrary.Tests.csproj -p:UseSharedCompilation=false --filter FullyQualifiedName~PhantomFlowMetricsTests
```

Follow the repo's build-server cleanup rules. Evidence must include the newly
added test names/counts and fail-before/pass-after output; an unchanged filtered
suite passing is not completion. This source-level Check proves real provider
collection, not downstream deployment. A later deploy must remain inactive/dev
only and verify fresh queryable metadata against the loaded process before
claiming ingestion. A value of 1 is not evidence of successful playback,
organic traffic, healthy outcome instrumentation, or cryptographic attestation.
