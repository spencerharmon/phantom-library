# availability-stale-candidate-reprobe-001 — close the stale-available-with-no-live-candidates browse gap

Enqueued by `playback-error-reduction-004` (series ROI Priority 12, day 4) as
the biggest remaining behaviour win now that both primary dials
(`availability-probe-reconcile-001`, `browse-prune-dead-swarm-001`) are
`DONE`. Attacks the SAME top-ranked cause bucket dial #1 and dial #2 both
targeted (`no_candidate` / `availability_abstain` on the cold
`materialise_then_play` flow), by closing a residual gap in dial #2's own
prune predicate.

## The gap

`PhantomDb.ListVisibleMovieRowsAsync` / `ListVisibleSeriesRowsAsync` admit an
`availability_items` row with `status='available'` to default browse whenever
EITHER:

- no `source_candidates` rows exist at all for that key (`NOT EXISTS`), or
- at least one non-`'invalid'`, non-dead-swarm `source_candidates` row exists.

The first branch is CORRECT for a brand-new item that has never been probed
(never had a candidate to invalidate) — it must stay visible so it can be
tried. But it ALSO silently admits an item whose candidates existed, were
cached, and then EXPIRED OUT of the `source_candidates` table (the
`MagnetCacheTtlHours` TTL elapsed and the rows aged out / were pruned) while
the `availability_items.status` row still reads the stale `'available'` from
the last successful probe (`checked_at` long in the past, `next_check_at`
possibly also elapsed). Today's SQL cannot tell these two cases apart — "never
assessed" and "assessed, cached, then the cache emptied out" both look like
"no source_candidates row", so both stay visible. The second case is
functionally the SAME defect dial #2 fixed for the dead-swarm case (an
`'available'`-status item with nothing live to try stays visible) — just
reached via cache expiry instead of validation/dead-swarm marking. A cold
attempt against such an item goes straight to `no_candidate` (or
`availability_abstain` if the on-demand re-probe also misses), because the
availability row's own `checked_at`/candidate fields are stale and nothing
upstream forced a fresh probe before serving it to the user as playable.

## Goal

Distinguish, in the browse-visibility query (or a lookup it can cheaply join
against), "never probed" from "was probed, cached a candidate, and that cache
has since gone empty/stale" for an `'available'`-status row, and for the
latter case EITHER:

1. force (or schedule with the highest scheduler priority) an eager re-probe
   via the existing `AvailabilityProbeWorker` reconcile/TTL-re-probe path
   (dial #1's plumbing — reuse it, do not duplicate it) before the next
   browse read trusts the row, or
2. if no fresh probe can be forced synchronously in the browse-list path
   (list queries must stay cheap), exclude the row from default browse (same
   as the dead-swarm case) until a background re-probe restores a live
   candidate or definitively confirms unavailability — never leave it
   visible-but-empty indefinitely.

Movie AND episode/series parity is required (project rule): the fix must be
exercised for both `item_type=movie` and `item_type=episode` — a fix that
helps only movies is half a fix. Prefer no schema change (the existing
`availability_items.checked_at` / `next_check_at` / `attempt_count` columns
plus `source_candidates` timestamps should be enough to detect "stale
available, no live candidates").

## Where to implement

- `src/Jellyfin.Plugin.PhantomLibrary/State/PhantomDb.cs` —
  `ListVisibleMovieRowsAsync` / `ListVisibleSeriesRowsAsync`, the exact query
  dial #2 (`browse-prune-dead-swarm-001`) extended. Add the stale-detection
  predicate alongside the existing dead-swarm predicate (reuse
  `CandidateIsThresholdDeadSwarmSql` as the pattern to follow, not necessarily
  the same SQL fragment).
- `src/Jellyfin.Plugin.PhantomLibrary/Scheduled/AvailabilityProbeWorker.cs` —
  if the fix drives an eager re-probe, prioritise a stale-available-no-
  candidate row over the worker's normal batch order (it is a worse offender
  than an unprobed row — it is actively LYING that the item is playable).
- `src/Jellyfin.Plugin.PhantomLibrary/Sources/PhantomSourceManager.cs` — no
  change expected (the on-demand path already always calls the full-fan-out
  `MagnetSelector.ProbeAsync`, per day 4's analysis); confirm this remains true
  after the fix.

## Non-goals

- No change to the gostream register / first-byte handoff (still a secondary
  cause bucket per the series ranking).
- No relaxing of the existing dead-swarm / invalid-candidate prune dial #2
  shipped — this is an ADDITIONAL predicate, not a replacement.
- No new background loop if the existing `AvailabilityProbeWorker` sweep can
  be reprioritised instead.

## Definition of done (Check)

`Check:` = `./scripts/tests/availability-stale-candidate-reprobe.test.sh` — an
in-repo `script-test` harness (matches the `CHECKS.md` `script-test`
framework) that, against the `47-loadtime-flows.sh` synthetic fixture, asserts
for BOTH `item_type=movie` and `item_type=episode`:

- a genuinely never-probed item (no `availability_items` row, or a row with no
  `source_candidates` ever cached) STAYS visible in default browse (no
  regression on the existing "not yet assessed" behaviour);
- an `'available'`-status item whose `source_candidates` all expired out of
  cache (simulated by a `source_candidates` set older than
  `MagnetCacheTtlHours` / a cache with zero non-expired rows) is EITHER
  excluded from default browse OR triggers an eager re-probe that restores a
  live candidate before the next list read — never silently served as
  visible-with-zero-live-candidates;
- the existing dead-swarm / all-invalid-candidate exclusion (dial #2) is
  unaffected (no double-counting, no relaxation).

The harness FAILS before this task's stale-detection logic exists and PASSES
after. The `.test.sh` runner is committed executable (sandbox denies `bash` as
a check word; exec'd directly by its shebang path). A behavioural change also
requires a `dotnet test` regression on the C# query/prune path — the
implementer adds a unit test that FAILS without the change and PASSES with it,
alongside the rig harness.

## Series linkage

Enqueued by `playback-error-reduction-004` (day 4). Attacks the day 1-4
top-ranked cause bucket (`availability_abstain` + `no_candidate` on the cold
flow) by closing a residual gap in dial #2's own prune predicate. Its effect
is provable via the `phantom_playback_outcome_total{flow="materialise_then_play",cause=...}`
series once deployed and a day of live data exists.
