# Stale available / empty-candidate live verification

Run from the task worktree:

```sh
beehive submodule worktree exec phantom-library bee-availability-stale-candidate-cache-refresh-001 -- ./scripts/tests/availability-stale-candidate-reprobe-live-rig.test.sh
```

This mandatory check runs seven fast assertion-guard regressions, then the existing
[isolated PostgreSQL playback rig](postgres-playback-parity-rig.md) with
`--suite stale-candidate`. It builds the checkout's production plugin
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

## Measured recovery (2026-11-26)

Historical red: plugin 0.5.7.45, reproduced again on 0.5.7.46 / schema 24 /
Jellyfin 10.11.9 / PostgreSQL 16.16. BOTH flows passed exclusion, single-tick
promotion (priority 50, generation 0→1), fresh Torrentio persistence and repository
restoration, but eleven REST browses still omitted the target. Both versions
remained `1:external-tv-metadata-v3`: `CompleteAvailableAsync` invalidated only on
an availability **status** transition, missing candidate-cache recovery.

Plugin **0.5.7.47**, same schema/server/PostgreSQL versions, passed the unchanged
live contract and ordinary 35/36 PostgreSQL playback. Fresh-build candidate and
installed DLL SHA256 were both
`8ba37dc6f0f9b3ac58b9c2288e88ecbd0e868def1618e50b84b0db507b1dd42b`.

```text
STALE_EXCLUDED movie repository=true REST=true control=99000003 candidates=0
EAGER_REPROBE movie ticks=1 priority=50 generation=0->1 candidates=1
VISIBILITY_RESTORED movie repository=true REST=true
STALE_EXCLUDED episode repository=true REST=true control=99100002 candidates=0
EAGER_REPROBE episode ticks=1 priority=50 generation=0->1 candidates=1
VISIBILITY_RESTORED episode repository=true REST=true
STALE_CANDIDATE_REPROBE_OK backend=postgres movie=passed episode=passed
CHANNEL_E2E_PLAYBACK_OK
CHANNEL_EPISODE_E2E_PLAYBACK_OK
PLAYBACK_PARITY_OK backend=postgres movie=passed episode=passed
```

The movie run advanced only MoviesVersion from `1` to `1795702499328`; the episode
run advanced only ShowsVersion from `1` to `1795702555784` (same salt). REST
restored on the first post-tick browse in each run; TV then navigated the restored
series, Season 1 and episode 1. No fixture invalidation, restart after exclusion,
additional tick or seeded successful candidate was added.

### Invalidation design

Before writing successful probe candidates, the worker remembers whether channel
invalidation is needed: the existing status-transition case, OR a previously
assessed available row (`CandidateMagnet != null`) with no unexpired candidates
for its tuple/preset. After candidate, magnet-cache and availability persistence,
it uses the existing `BumpFor`/`ChannelStateProvider` path. Both ordinary Torrentio
and full-indexer reconciliation use this same completion method. It does not bump
both channels, delete cache files or alter repository browse SQL/eager priority.
Fresh available caches and never-assessed available rows retain the old no-bump
behavior; transient errors or failed candidate writes do not add invalidations.

The existing tuple/preset candidate reader is reused rather than copying browse
SQL. This adds one bounded indexed read only for already-assessed available
successes. It is intentionally conservative: an expired cache or another preset's
candidates may already permit repository browse, so such recovery can also bump
the affected channel. It never relaxes browse eligibility. The broader existing
lease/candidate write transaction boundaries and cross-process channel state are
unchanged; this acceptance proves the same running Jellyfin instance.

`AvailabilityCacheInvalidationTests` supplies 28 movie/episode cases (six red on
the old code), including missing/expired/fresh/never-assessed cache states,
unknown/unavailable transitions, reconciliation, transients and failed candidate
persistence. Together with `AvailabilityProbeWorkerTests`: 50/50 pass. Repository
and channel/discovery neighbors: 215/215 pass. The live harness still exercises the
real HTTP selector/worker without these unit-test seams.

No operator steps needed for this task. Plugin version metadata is bumped for a
future normal install; no schema change, gostream handoff, image publication or
production deployment/flip was performed.
