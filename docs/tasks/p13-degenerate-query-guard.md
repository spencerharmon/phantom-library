# p13-degenerate-query-guard

ROI P13. Production sent 43 indexer searches / 6 h as `Term: [. 2026]`: a
TMDB title of `"."` + the year. Such a term wastes indexer budget and never
matches. Invariant: **no indexer request (Prowlarr, per-indexer Torznab via
Prowlarr, Torrentio) carries an empty / whitespace / punctuation-only title
term** — movie and episode, interactive and background.

## Choke point
Every indexer request flows through `MagnetSelector.ProbeCoreAsync`
(`ProbeAsync` = materialise + details/source picker + availability reconcile;
`ProbeAvailabilityAsync` = availability sweep; `SelectRankedAsync` =
magnet-cache builder). No other code calls `IIndexerClient.SearchAsync`.
The guard runs there BEFORE any `SearchAsync`.

## Resolution order (`ResolveQueryTermAsync`)
1. `QueryTitleGuard.Normalize(title)`: trims, collapses whitespace, drops
   control/format chars (zero-width). Returns null when there is no Unicode
   letter or digit at all, which makes the title degenerate. Usable titles
   (`9`, `1917`, `M*A*S*H`, CJK) are kept. The query term never reaches the
   scorer, so scoring is unchanged.
2. Degenerate → `IQueryTitleRepairSource` (`TmdbQueryTitleRepairSource`):
   `tmdb_metadata.original_title` (no network), then TMDB
   `alternative_titles` + `translations`
   (`ITmdbClient.Get{Movie,Series}TitleVariantsAsync`, one call, en/US/GB
   first). Episodes repair from the parent series. A title is never fabricated.
3. Still none, IMDB id present → IMDB-id-only query to indexers whose
   `IIndexerClient.SupportsImdbOnlyQuery(type)` is true (Torrentio: always;
   Prowlarr: movie only, since episode search is text-only). Other indexers are
   not called.
4. Otherwise → `MagnetProbeOutcome.DegenerateQuerySkipped` (error kind
   `degenerate_query_skipped`). **Zero indexer requests.**

Counter `phantom_degenerate_queries_total{type,outcome}`, outcome ∈
`repaired_original_title | repaired_alternate_title | imdb_only |
degenerate_query_skipped`.

## Caller handling of the skip
- `SelectRankedAsync` throws `DegenerateQuerySkippedException`, so
  `MagnetCacheBuilder.BuildClaimedAsync` fails the job with
  `last_error='degenerate_query_skipped'`. The sync build returns 0.
- `AvailabilityProbeWorker`: long `AvailabilityNoIndexerRetryHours` backoff,
  `last_error_kind='degenerate_query_skipped'`, status unchanged, plus
  `phantom_availability_probes_total{outcome="degenerate_query_skipped"}`.
- Materialise and source picker: the outcome is not `Available`, so no candidates.
- Pre-filters no longer treat an empty title as missing metadata (the meta
  resolver, the builder, and the worker), so the guard can repair it.

## Backstop
`ProwlarrClient.BuildTextQuery` normalises the title and emits no text term
for a degenerate title. With no term left (an episode, or no IMDB id), it
throws `IndexerNotApplicableException` (an abstention) instead of sending
`query=`.

## Verify
`./scripts/tests/p13-degenerate-query-guard.test.sh` runs `dotnet test`
filtered to `DegenerateQueryGuardTests` (33 cases). The script initialises the
nested `jellyfin` submodule if it is missing.
