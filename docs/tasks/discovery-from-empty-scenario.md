# discovery-from-empty-scenario — cold-start synthetic-provenance rig coverage

Task: `discovery-from-empty-scenario` (P3 Stage 1, "synthetic provenance").
Deliverable: `tools/rig-scenarios/41-discovery-from-empty.sh`. This doc records
the design/intent and the load-bearing facts the scenario depends on; it does
not change plugin behavior.

## Intent

Prove the whole phantom channel stack can be reconstructed from a **cold**
`phantom.db` using only the deterministic TMDB mock — no operator production
state, no hand-seeded catalogue. Two things fall out of that:

1. **Zero operator PII.** Every id that ends up in the rebuilt catalogue/
   availability/materialise state must come from the mock fixtures (all
   `>= 99000000`). A sub-synthetic id would mean a real operator TMDB id leaked
   into the test corpus.
2. **A reusable synthetic fixture.** The dependent `migration-rig` task seeds
   from a known-shape synthetic catalogue. This scenario is the canonical way to
   regenerate that shape from scratch, so `migration-rig` never has to embed
   operator ids to get a realistic pre-migration DB.

It also doubles as regression coverage that the scenario-35 (movie) and
scenario-36 (episode) native-open playback flow works when the catalogue was
**discovered**, not directly inserted by the test.

## What the scenario asserts (in order)

- **[1] From-empty precondition.** After `rig-up.sh --reset` (which `rm`s
  `phantom.db`), schema is `user_version=11` and `catalogue_items`,
  `availability_items`, `series_expansion_state`, `materialised_state` are ALL
  empty. Nothing auto-populates: `DiscoveryRefresh` is a 6h `IntervalTrigger`
  task with no startup trigger, and the only other `catalogue_items` writer is
  the event-driven favourite ingestor.
- **[2] Real discovery.** Trigger `PhantomLibrary.DiscoveryRefresh` and wait for
  Idle.
- **[3] Catalogue shape (tmdb-keyed, synthetic).** Exactly the 3 movie +
  2 series mock fixtures land in `catalogue_items` with the exact id sets
  `{99000001,99000002,99000003}` / `{99100001,99100002}`; every row carries
  `source_mask=3` (trending `1` | discover `2`, because a from-empty run visits
  each fixture on both phases); movies seed 3 `availability_items(status=
  'unknown')` rows and series seed 2 `series_expansion_state` rows; and
  `tmdb-mock.log` shows the `/3/trending/{movie,tv}/week` + `/3/discover/{movie,
  tv}` hits (provenance = mock, not real TMDB).
- **[4]/[5] Channel shape (tmdb-keyed) + native-open parity, movie AND TV.**
  Discovery alone does **not** surface channel items (see gating below), so the
  scenario flips one discovered movie (`99000001`) and one discovered episode
  (`99100001 S01E01`) to `available` and seeds a magnet — exactly the seed
  scenarios 35/36 perform after their own discovery step. It then asserts the
  channel emits them as tmdb-keyed phantom items (`ExternalId`
  `movie_<tmdb>` / `series_<tmdb>` / `episode_<tmdb>_s01e01`, `ProviderIds.Tmdb`,
  TMDB display name, `phantom` tag, native `RequiresOpening` source with no
  splash path), and that the two-step native-open contract still materialises
  through the gostream mock into a real file that streams
  (`PlaybackInfo` RequiresOpening → `AutoOpenLiveStream` real File source under
  `/tmp/jf-rig/gostream/{movies,tv}/` → `materialised_state` row → stream bytes).
- **[6] Zero-PII gate.** No row in `catalogue_items` / `availability_items` /
  `materialised_state` / `series_expansion_state` references a tmdb id
  `< 99000000`.

## Load-bearing facts (why the scenario is shaped this way)

- **Discovery writes** `catalogue_items` for both `type='movie'` and
  `type='series'` (`catalogue_items.type` CHECK is `('movie','series')`), via
  `PhantomDb.UpsertCatalogueHitsAsync`. Source-mask bits: trending `1`
  (`DiscoveryRefreshTask.SourceTrending`), discover `2` (`SourceDiscover`),
  favourite-recommendation `4`. Bits are OR-accumulated, so a fixture seen on
  both phases ends at `3`.
- **The catalogue cap** (`SuggestionsCatalogueMaxItems`, rig=10 → 5 movies /
  5 series) never truncates the 3+2 fixtures, and it gates only the Discover
  phase — trending is always admitted.
- **Channel visibility gates on availability, not discovery.**
  `ListVisibleMovieRowsAsync` requires `materialised_state` OR
  `availability_items.status='available'`; `ListVisibleSeriesRowsAsync` requires
  `>= 1` available/materialised episode. A discovery-only movie
  (`status='unknown'`, no magnet, no materialise) is therefore invisible until
  something flips it available — which is precisely the minimal seed the
  scenario adds (and which 35/36 already do). Episode display metadata for the
  season browse is served live by the TMDB mock's `/3/tv/<id>/season/1`
  endpoint, so no `tmdb_episode_cache` seeding is required.
- The `PhantomBadgeVisibility` / `EagerResolveEnabled` / `SeriesAutopilotEnabled`
  config flags do not gate this path (badge visibility only affects overlay
  strings on already-emitted items; `EagerResolveEnabled` has no runtime reader;
  autopilot only fires on playback progress of a real item).

## Running it

```bash
# Prereq: patched Jellyfin built (rig-up checks for the patched jellyfin.dll).
bash tools/rig-scenarios/41-discovery-from-empty.sh
# → builds the plugin, rig-up --reset, drives discovery + playback,
#   tears the rig down on exit, and prints DISCOVERY_FROM_EMPTY_OK on success.
# Log: /tmp/jf-rig/logs/scenario-discovery-from-empty.log
```

Movie/TV parity (AGENTS.md) is satisfied in-scenario: both a movie and an
episode are asserted for catalogue shape, channel tmdb-keying, and native-open
materialise/stream. There is no intentionally-scoped parity exception.

## Relationship to other work

- **Scenarios 35/36** own the exhaustive movie/episode playback + source-
  management assertions; scenario 41 reuses their native-open contract but adds
  the from-empty/provenance/zero-PII dimension they do not cover.
- **`migration-rig`** (depends on this task) reuses the from-empty discovery walk
  to regenerate a synthetic pre-migration catalogue without operator ids.
