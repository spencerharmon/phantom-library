# Stale available / empty-candidate live verification

Run from the task worktree:

```sh
beehive submodule worktree exec phantom-library bee-availability-stale-candidate-reprobe-verify-001 -- ./scripts/tests/availability-stale-candidate-reprobe-live-rig.test.sh
```

This mandatory check runs seven fast assertion-guard regressions, then the existing
[isolated PostgreSQL playback rig](postgres-playback-parity-rig.md) with
`--suite stale-candidate`. It builds the checkout's unchanged production plugin
and patched Jellyfin; supports the same candidate/server/FFmpeg injection variables.
No global seed, production DB/image, fixed ports, systemd unit or operator action.
Both movie and episode run even if one fails; any failure makes the check nonzero.
Ordinary 35/36 playback remains the default suite, unchanged.

## What the fixture controls

- Synthetic Jellyfin startup and PostgreSQL schema creation remain real. Discovery
  runs through its scheduled-task REST API, using existing TMDB/gostream mocks.
- Availability is disabled with a one-hour timer before the owned server restart.
  A separately built **test-only assembly**, `tools/rig-scenarios/AvailabilityRigControl`,
  is copied beside the candidate ONLY for this suite. It is not referenced by the
  solution, packaged plugin, install script or production image. Its endpoints
  require elevation, loopback and `PHANTOM_RIG_AVAILABILITY_CONTROL=1`.
- The controller uses the actual DI `PhantomDb` for `ListVisibleMovieRowsAsync` and
  `ListVisibleSeriesRowsAsync`, not copied SQL. Reflection calls exactly one private
  `TickAsync` on the actual hosted `AvailabilityProbeWorker`, temporarily enabling
  its existing configuration. No replacement worker, selector or probe delegate.
  The timer remains an hour away; the short scenario never relies on its timing.
- The current DLL's policy hash is read through that controller. Other discovery
  backlog is deferred. The target is `available`, has a non-null stale magnet,
  zero `source_candidates`, no materialised state, priority zero and a future due
  time with the **current policy hash**. Thus ordinary due/policy-change claims
  cannot masquerade as eager promotion. A never-assessed available control must
  remain visible, preventing an empty/wrong endpoint from proving exclusion.
- An AFTER UPDATE trigger in the disposable PostgreSQL DB **only records** target
  row writes. It cannot modify their values. The trace must show due/priority>=50
  promotion BEFORE the one generation increment/lease and successful completion.
- A loopback Torrentio HTTP fixture returns distinct fresh movie/episode hashes.
  The real client, selector, worker and repositories must write fresh
  `source=availability_probe` candidates and complete the availability row.
  The scenario never inserts a successful candidate or repairs channel versions.
- REST channel browse must then restore the target within ten seconds; TV must
  navigate restored series → season → episode. Repository visibility alone does
  NOT count as success. No restart, metadata refresh, cache deletion, DataVersion
  write or second probe is allowed to manufacture restoration.

Evidence is retained under `artifacts/playback-parity-*/`: raw REST responses,
SQL before/after/update trace, channel versions, oracle traffic, actual candidate
and installed DLL hashes, database identity, server logs and cleanup receipts.
Data/config/processes/containers are cleaned by the shared rig even on failure.
Private test control build files are retained with the evidence, never installed.

## Observed defect, not a passing acceptance (2026-11-26)

On plugin 0.5.7.45 / schema 24 / Jellyfin 10.11.9 / PostgreSQL 16.16, BOTH flows
pass exclusion, single-tick promotion (priority 50, generation 0→1), fresh
Torrentio candidate persistence and repository restoration. BOTH fail REST
restoration: channel versions remain `1:external-tv-metadata-v3`, and eleven
successive browse responses still omit the target. `CompleteAvailableAsync`
currently calls `BumpFor` only when the previous status was not `available`;
this stale case remains `available` throughout. A separate corrective task owns
that application change. Keep this check red until the real cache path is fixed;
do not add a fixture-side invalidation or fall back to copied SQL.

No operator steps needed. This is isolated test infrastructure, not deployment.
