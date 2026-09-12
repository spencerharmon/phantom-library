# P10 relevance-blended browse ordering + explicit sort options

ROI Priority 10, item 2. Default browse ordering blends availability
confidence (more-available first), newness (recently-released/recently-
added), and user relevance (favourites-adjacent, recently-played-adjacent,
genre affinity); ties break toward the more-playable item. Also offer
explicit sort options: Most available, Newest, Trending/Popular (TMDB
popularity), Recently played, Recently added. Ranking inputs must be
cheap/precomputed (a stored score column refreshed by the availability
sweep), never an O(catalogue) scan per list load.

## The stored score: `tmdb_metadata.relevance_score`

Schema v20 adds `relevance_score REAL NOT NULL DEFAULT 0` to
`tmdb_metadata`, backed by `idx_tmdb_metadata_relevance(type,
relevance_score DESC)`. `PhantomDb.ComputeRelevanceScore` is the formula:

```
score = 0.6 * availabilityConfidence + 0.25 * newness + 0.15 * popularity
```

all three inputs and the result clamped to `0..1`:

- `availabilityConfidence` — `1.0` for a materialised/available movie,
  `0.0` otherwise; for a series, `min(availableEpisodeCount / 3, 1.0)`
  (three-or-more available episodes reads as "fully relevant"
  availability-wise).
- `newness` — a linear decay over a 90-day window off
  `tmdb_metadata.fetched_at` (`1.0` at fetch time, `0.0` at 90+ days old).
  This is the closest signal this layer has to "recently released/recently
  added" — TMDB's own release date isn't separately tracked at day
  granularity in `tmdb_metadata` (only `year`), and `fetched_at` is when the
  plugin's own catalogue first learned about the title.
- `popularity` — TMDB's community/vote-average rating (`community_rating`),
  normalised to `0..1`. This is a **proxy**: the plugin does not persist
  TMDB's own `popularity` field today (only `vote_average`); capturing that
  separately would need changes to the TMDB client response models and
  ingestion pipeline, out of scope here.

## Refresh: the availability sweep, not a bulk recompute

`PhantomDb.RefreshRelevanceScoreAsync` runs inside the SAME already-open
connection/write-lock hold as the two availability-sweep write paths:

- `CompleteAvailabilityProbeAsync` — the scheduled `AvailabilityProbeWorker`
  tick's probe-completion write.
- `MarkAvailabilityAvailableAsync` — the direct/user-triggered "mark
  available" path (e.g. a fresh magnet-cache hit).

Each call is O(1): one indexed SELECT of `community_rating`/`fetched_at`
(plus, for an episode touch, one indexed `COUNT(*)` of the parent series'
currently-available episodes) and one single-row `UPDATE`. It never scans
the catalogue, satisfying the ROI's "never an O(catalogue) scan per list
load" constraint by construction — the score is already sitting on the row
by the time any browse list reads it.

An episode-type touch (`tmdb_id` = the parent series' TMDB id, per how
`availability_items` already stores episode rows) recomputes the SERIES'
row, not a nonexistent per-episode `tmdb_metadata` row — so one
newly-resolved episode of a long-buried series nudges the whole series back
up the list without waiting for every episode to individually resolve.

## Default ordering

`ListVisibleMovieRowsAsync` / `ListVisibleSeriesRowsAsync` (the queries
backing both channels' browse LIST) order:

1. Materialised (or, for a series, has at least one materialised episode)
   ranks ahead of merely-available — the ROI's "ties break toward the more-
   playable item" rule, applied as the PRIMARY sort key rather than a mere
   tie-break, since a materialised item is strictly more playable than an
   available-only one regardless of relevance score.
2. `relevance_score DESC`.
3. The previous recency tie-breaker (`materialised_at`/`fetched_at` DESC).

## Explicit sort options

Jellyfin's native channel-browse sort control is a fixed enum
(`MediaBrowser.Model.Channels.ChannelItemSortField`: `Name`,
`CommunityRating`, `PremiereDate`, `DateCreated`, `Runtime`, `PlayCount`,
`CommunityPlayCount`) — a channel cannot add new field labels, only choose
which of these to support (`InternalChannelFeatures.DefaultSortFields`) and
honor when a caller requests one (`InternalChannelItemQuery.SortBy` /
`SortDescending`). `PhantomMoviesChannel` / `PhantomShowsChannel` now
declare `PremiereDate`, `DateCreated`, `CommunityRating`, `Name` and
`SupportsSortOrderToggle = true`, and both channels' top-level
`GetChannelItems` call the new `Channels/ChannelSortHelper.cs`'s
`ApplyExplicitSort` on the already browse-ordered item list when
`query.SortBy` is set.

Mapping from the ROI's five named options onto the fixed enum (documented
in `ChannelSortHelper`'s class doc, the authoritative source if this drifts):

| ROI option | Enum field | Note |
| --- | --- | --- |
| Most available | *(unset `SortBy`)* | the default order already ranks materialised/available first |
| Newest | `PremiereDate` | |
| Recently added | `DateCreated` | mapped to `tmdb_metadata.fetched_at` |
| Trending/Popular (TMDB popularity) | `CommunityRating` | proxy — see popularity note above |
| Recently played | *(not offered)* | Jellyfin only knows `PlayCount`/`CommunityPlayCount` from a real `BaseItem`'s persisted `UserData`, which doesn't exist until a phantom materialises; requesting either field is a documented no-op |

## Explicitly deferred (not silently dropped)

The ROI's default-blend wishlist also names "user relevance
(favourites-adjacent, recently-played-adjacent — depends on
`recently-played-fix` for that signal, genre affinity)". None of these are
in the formula above:

- Recently-played-adjacency: the ROI itself names `recently-played-fix` as
  this signal's dependency; that task has not landed, so there is no
  reliable recently-played source to blend in yet.
- Favourites-adjacency and genre affinity: per-user signals that live in
  Jellyfin's own `UserData`/`BaseItem` layer, not `PhantomDb` — the
  `ListVisibleMovieRowsAsync`/`ListVisibleSeriesRowsAsync` query layer this
  task touches has no such per-user signal plumbed into it today. Adding it
  would mean threading a `Guid userId` signal into the *ordering* (the
  existing per-user hidden-item filter is a post-hoc subtraction, not an
  ordering input) — a larger, separate change.

These are natural follow-ups once their prerequisites exist, not omissions.

## Tests

`tests/Jellyfin.Plugin.PhantomLibrary.Tests/PhantomDbTests.cs`:

- `ComputeRelevanceScore_BlendsAvailabilityNewnessPopularity`
- `ComputeRelevanceScore_NewnessDecaysLinearlyOverNinetyDays`
- `CompleteAvailabilityProbeAsync_RefreshesMovieRelevanceScoreOnAvailable`
- `MarkAvailabilityAvailableAsync_RefreshesMovieRelevanceScore`
- `MarkAvailabilityAvailableAsync_RefreshesSeriesRelevanceScoreFromEpisodeCount`
- `ListVisibleMovieRows_DefaultOrder_MaterialisedFirstThenRelevanceScore`

Run: `MSBUILDDISABLENODEREUSE=1 dotnet test -p:UseSharedCompilation=false`.
