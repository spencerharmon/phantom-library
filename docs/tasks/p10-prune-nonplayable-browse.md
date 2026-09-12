# P10 prune non-playable phantoms from the main browse lists

ROI Priority 10, item 1. A phantom with no viable source — unavailable/
unknown, unreleased/future-aired, no capable indexer, or a known
cold-materialise-fail — must not clutter the default Movies/Shows browse
surface. It stays searchable (the existing p6-search-list-surface-split
already emits such items as badged BaseItems reachable via global search)
but is excluded from the top-level browse LIST.

## What was already in place (P6)

`PhantomDb.ListVisibleMovieRowsAsync` / `ListVisibleSeriesRowsAsync` — the
queries backing `PhantomMoviesChannel.GetChannelItems` (root folder) and
`PhantomShowsChannel.GetTopLevelSeriesAsync` — already filtered on
`availability_items.status='available'` (or a materialised row). That
alone already excludes:

- unavailable / never-probed ("unknown") items — `status` stays whatever
  it was; only a definitive `Available` probe outcome sets `'available'`.
- unreleased / future-aired items — `AvailabilityProbeWorker` pre-filters
  these to a long backoff without ever setting `status='available'`.
- no-capable-indexer items — same pre-filter, same effect on `status`.

## The gap this task closes: known cold-materialise-fail

An item can sit at `status='available'` (a real magnet candidate was
found and cached at `UpsertSourceCandidatesAsync` time) while every
candidate the plugin has since tried has been marked permanently invalid
in `source_candidates.validation_status` (via
`Materialiser.MarkCandidateFailedAsync` — hard validation failures,
`fuse_path_missing`, `bad_request`). `AvailabilityItemRow`'s long
`AvailabilityAvailableTtlDays` means the availability prober does not
revisit — and does not flip `status` back off `'available'` — for a long
time, so the item kept cluttering the main list even though every known
way to actually play it had already failed.

## The fix (no schema change, no new churn loop)

`ListVisibleMovieRowsAsync` / `ListVisibleSeriesRowsAsync` add one cheap,
index-backed correlated subquery against the already-collected
`source_candidates` rows for the item:

- an `'available'` item is included when it either has **no**
  `source_candidates` rows yet (never validated — absence of data is not
  proof of failure, so it stays visible), **or** has at least one row
  whose `validation_status <> 'invalid'` (still viable, including
  `'unknown'`/`'transient'`/`'valid'`).
- an `'available'` item with `source_candidates` rows where **every**
  row is `'invalid'` is excluded — a real known cold-materialise-fail.

Convergence reuses the existing pipeline exactly as designed: a fresh
re-probe or the magnet-cache background sweep inserting a new (non-
`'invalid'`) `source_candidates` row makes the item reappear on the very
next read — no re-promotion step, no new background loop. Materialised
items are unaffected (they bypass the candidate check entirely, same as
before). Season/episode detail views are unchanged: per
p6-search-list-surface-split they intentionally show the full known-
episode grid regardless of top-level list visibility, so a user who
reached a season via search/direct link still sees every known episode,
badged live.

## Movie/TV parity

Both `ListVisibleMovieRowsAsync` (movie, `season=-1, episode=-1`
candidates) and `ListVisibleSeriesRowsAsync` (per-episode candidates
feeding the series' min-available-episode display gate) got the
identical fix.

## Tests

`tests/Jellyfin.Plugin.PhantomLibrary.Tests/PhantomDbTests.cs`:

- `ListVisibleMovieRows_ExcludesAvailableMovieWhenAllCandidatesInvalid`
- `ListVisibleMovieRows_ReentersListWhenNewCandidateAppearsAfterColdFail`
- `ListVisibleSeriesRows_ExcludesSeriesWhenOnlyEpisodeCandidateInvalid`

Run: `MSBUILDDISABLENODEREUSE=1 dotnet test -p:UseSharedCompilation=false`.
