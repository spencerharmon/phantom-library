# recently-played-fix — design & root-cause frame

Operator bug (2026-09-12): "Recently played" is not working for phantom items —
a played (materialised) phantom title does not appear in the standard Jellyfin
recently-played / Continue Watching / resume surfaces.

## Surfaces in scope

Jellyfin's Home screen shows a played item in two distinct rows, each a
DIFFERENT query, and the bug is about whether a played phantom lands in them:

1. **Recently played** — a normal recursive library query, roughly
   `GET /Users/{uid}/Items?SortBy=DatePlayed&SortOrder=Descending&Filters=IsPlayed
   &Recursive=true&MediaTypes=Video&IncludeItemTypes=Movie,Episode`.
   Surfacing requires the played item's persisted `BaseItem` to carry
   `UserData` with a `DatePlayed`/`Played` marker AND to be included by the
   query (not filtered out as a virtual/channel item).
2. **Continue Watching / resume** — `GET /Users/{uid}/Items/Resume`, requires a
   positive `PlaybackPositionTicks` on the same stable `BaseItem`.

Scenarios 35 (movie) and 36 (episode) already exercise the RESUME row; this task
adds the RECENTLY-PLAYED row and asserts both for a movie AND an episode.

## Candidate causes (confirm on the live rig, do not assume)

- **H1 — channel/virtual items excluded from those rows.** The phantom card is a
  virtual channel item (`SourceType.Channel`); the materialised item's persisted
  `BaseItem` deliberately stays `SourceType.Channel` (the stable materialise
  contract in `Materialisation/Materialiser.cs`). The badges controller already
  encodes that phantom cards are "definitively NOT Continue-Watching library
  content" (`Api/PhantomLibraryBadgesController.cs`). If the recently-played
  query filters out channel/virtual items, a played phantom never appears there.
- **H2 — playback never records DatePlayed/PlaybackPositionTicks.** Playback goes
  through the materialising media source; if reporting does not persist
  `DatePlayed`/`PlaybackPositionTicks` against the item, no row can show it.
  `Materialisation/UserDataSavedListener.cs` reads played state and has a splash
  guard — verify a real (non-splash) play records the marker.
- **H3 — no persisted BaseItem carrying UserData.** Verify `ChannelItemId` stays
  stable across the phantom → materialise transition (by design it does:
  `ForMovie(42)` → `"movie_42"` regardless of state; `Channels/ChannelItemId.cs`)
  so `UserData` survives and the recently-played query has something to hit.
- **H4 — disambiguate vs the removed `ISupportsLatestMedia` 'Latest' row.** The
  "Latest in Phantom X" Home row was deliberately dropped (operator decision
  2026-06-28) because implementing `ISupportsLatestMedia` made core deep-enumerate
  the whole channel on every Home load (`Channels/PhantomMoviesChannel.cs`,
  `PhantomShowsChannel.cs`). Any recently-played fix MUST stay **O(recent)**,
  never reintroduce that O(catalogue) enumeration, and never regress the badges /
  Continue-Watching fast-path.

## Fix requirement

A played (materialised) phantom must reliably land in recently-played AND resume
with correct DatePlayed + resume position, movie AND episode parity, WITHOUT an
O(catalogue) Home-load scan and WITHOUT regressing the badges / Continue-Watching
fast-path.

## Proof (mandatory)

- `tools/rig-scenarios/48-recently-played.sh` — live-rig (:18096, never prod
  :8096) end-to-end: reuses 35 (movie) + 36 (episode, `RIG_NO_RESET=1`) to
  materialise + play both, then asserts each surfaces in the recently-played row
  (with a DatePlayed marker) and the resume row (with a positive
  `PlaybackPositionTicks`), guards the recently-played query latency as O(recent),
  and re-asserts 35/36 parity via their terminal OK markers. Carries a
  `PHANTOM_CI_DRYRUN=1` deterministic fixture (same pattern as scenario 47).
- `scripts/tests/recently-played.test.sh` — the deterministic sandbox DoD gate
  (bash + python3 only) that drives the scenario's dry-run fixture and asserts
  the contract for a movie AND episode, with a negative control proving the
  harness catches a regression.

Live-rig confirmation of the SOURCE-side behaviour (a materialised BaseItem
actually surfacing in :18096's recent/resume rows, and thus which of H1–H4 is the
true root cause) runs on the self-hosted / in-cluster acceptance rig, where the
patched Jellyfin core (`jellyfin/` submodule) is present. The sandbox gate here
proves the CONTRACT the live scenario asserts, deterministically, without a
cluster.
