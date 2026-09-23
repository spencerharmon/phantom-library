# availability-deadswarm-eager-reprobe-001

ROI Priority 12 fix, enqueued by playback-error-reduction-011 (day 11). It is the
ACTION complement to day-10's SIGNAL fix (badge-deadswarm-episode-parity-001,
DONE): that fix made the badge layer WARN the user about a `status='available'`
item whose only surviving candidate is a confirmed threshold dead swarm; this fix
makes the eager re-probe worker ACTUALLY REFRESH that dead candidate before the
next playback attempt, attacking the real episode `magnet_dead_stale` failure rate
day 10 and day 11 observed in the real per-attempt Mimir counter (episode
`materialise_then_play` = 2/2 `magnet_dead_stale`).

## Root cause

`AvailabilityProbeWorker` (`src/.../Scheduled/AvailabilityProbeWorker.cs:189`)
promotes stale-available rows to the front of the availability claim queue each
tick via `PhantomDb.MarkStaleAvailableItemsDueAsync`
(`src/.../State/PhantomDb.cs:3008`, availability-stale-candidate-reprobe-001). Its
current `WHERE` clause only matches a row that is `status='available'`,
`candidate_magnet IS NOT NULL`, unleased, AND has **zero** surviving
`source_candidates` rows (`NOT EXISTS (SELECT 1 FROM source_candidates …)`) — i.e.
the cached candidate EXPIRED OUT of the magnet cache.

It MISSES the day-10 failure shape: a `status='available'` row whose candidate row
STILL EXISTS in `source_candidates` but is a **confirmed threshold dead swarm**
(`dead_swarm_confirmations.confirmations >= DeadSwarmBrowsePruneThreshold`). Such a
row: (a) is excluded from default browse by dial #2, (b) now badges
stale-reprobe-pending (day-10 fix — the badge layer already KNOWS this state via
`CandidateIsThresholdDeadSwarmSql`), yet (c) is NEVER promoted to eager re-probe —
so the dead candidate lingers until the slow background sweep incidentally
re-probes it, and every playback attempt in the interim fails `magnet_dead_stale`.

## Fix

Extend `MarkStaleAvailableItemsDueAsync`'s promotion predicate so it ALSO promotes
a `status='available'`, unleased row whose EVERY surviving `source_candidates` row
is a threshold-exceeded dead swarm. Concretely, widen the `WHERE` to promote a row
that is EITHER:

- (existing) has zero surviving `source_candidates`, OR
- (new) has ≥1 surviving `source_candidates` row AND **no** surviving
  `source_candidates` row that is NOT a threshold dead swarm — i.e.
  `EXISTS (SELECT 1 FROM source_candidates sc WHERE <item-match>)`
  `AND NOT EXISTS (SELECT 1 FROM source_candidates sc WHERE <item-match>`
  `AND NOT ({CandidateIsThresholdDeadSwarmSql("sc","@deadSwarmThreshold")}))`.

REUSE the existing private `CandidateIsThresholdDeadSwarmSql(scAlias,
thresholdParam)` helper (`src/.../State/PhantomDb.cs:1629`) — the SAME predicate
browse-prune (`ListVisibleMovieRowsAsync`/`ListVisibleSeriesRowsAsync`) and the
day-10 badge fix already use. Do NOT hand-duplicate the dead-swarm SQL. Bind a
`@deadSwarmThreshold` param defaulting to
`DefaultDeadSwarmBrowsePruneThreshold` (2), so a wired config value can override it
exactly as the browse overloads do; keep the parameterless public overload if one
is expected by callers. Continue to touch ONLY `next_check_at`/`priority` (never
`candidate_magnet`/`status`) so the browse-exclusion distinguisher survives until a
real re-probe resolves the row, and continue to skip leased rows
(`lease_until IS NULL OR lease_until < @now`).

## Movie/TV parity (REQUIRED)

The predicate keys on `availability_items.tmdb_id/type/season/episode`, which
matches both movie (`type='movie'`, season/episode = -1) and episode
(`type='episode'`) rows identically — same as the existing stale-no-candidate
clause and the day-10 badge predicate. No item_type branch; promotion is uniform.
The regression test MUST cover BOTH item_type=movie and item_type=episode.

## Non-goals

- Do NOT change `ListEpisodesForSeasonAsync`'s full-grid listing (intentional).
- Do NOT modify dial #1/#2 browse-prune SQL or `CandidateIsThresholdDeadSwarmSql`
  itself — REUSE it.
- Do NOT change the badge layer (day-10 fix already truthful).
- Do NOT add a new background worker; extend the EXISTING promotion the worker
  already calls each tick.
- No schema change (`dead_swarm_confirmations` table already exists, v22).

## Test (definition of done)

Add/extend a dotnet unit test (the `PhantomDb` test class covering
`MarkStaleAvailableItemsDueAsync`, or a new focused test) that seeds:

1. an `availability_items` row `status='available'`, `candidate_magnet` non-null,
   unleased, with a `next_check_at` far in the future and a low `priority`;
2. a matching `source_candidates` row (candidate STILL present); and
3. a `dead_swarm_confirmations` row for that candidate with
   `confirmations >= DefaultDeadSwarmBrowsePruneThreshold`,

for BOTH `type='movie'` and `type='episode'`, then calls
`MarkStaleAvailableItemsDueAsync` and asserts BOTH rows were promoted
(`next_check_at==0`, `priority` raised to the passed value). Include a NEGATIVE
control: a `status='available'` row whose surviving candidate is NOT a threshold
dead swarm (confirmations below threshold) is NOT promoted. The test must FAIL
before the change (the dead-swarm rows are not promoted today) and PASS after. Run
the full suite to confirm no regression to the existing stale-no-candidate
promotion behavior.

Check: `dotnet test` (matches the `dotnet-test` stub in
`submodules/phantom-library/CHECKS.md`).
