# Changelog

All notable changes to this project are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Spike: Jellyfin-native stub-layout (`[tmdbid-<id>]` path tokens).**
  Phantom stubs now use the Jellyfin-native
  `<Title> (<Year>) [tmdbid-<id>]` filename / directory layout
  instead of the custom `__phantom_tmdb<id>` sentinel scheme.
  Newly-created stubs (movies and series) render with the real
  title in Jellyfin — no more `Word_Word__phantom_tmdb1234`
  scanner-derived names. Year segment is omitted when TMDB lacks
  it. Episode filenames under series stubs intentionally drop the
  bracketed token (the series directory carries it) so the
  `tvshows` resolver derives clean episode names. `PhantomStubManager`
  exposes year-aware overloads of `CreateAsync`,
  `DeriveFilename`, and `DeriveSeriesStubPaths`; old overloads
  forward with `year: null` for back-compat. A new
  `PhantomPathUtilities` helper centralises the dual-recognition
  logic (legacy sentinel OR new token) used by dedupe / heal /
  eviction / migration paths — no more scattered
  `Contains("__phantom_tmdb")` substring checks.

- **One-shot stub-layout migration.** A new `StubLayoutMigration`
  hosted service runs at plugin startup, renames every existing
  Virtual phantom stub from the legacy filename scheme to the new
  path-token scheme, and updates `BaseItems.Path` atomically.
  Records completion in a new `plugin_meta` table
  (`stub_layout_v1_complete` key) so subsequent startups no-op.
  Idempotent at the per-row level; refuses to overwrite an existing
  destination. A manual fallback bash script ships at
  `scripts/migrate-stub-layout-v1.sh` for cases where the in-plugin
  migration cannot complete (operator runs with Jellyfin stopped).

### Notes

- **This is an A/B spike.** The heal-on-rediscovery logic, the
  forced `IsLocked = true` re-stamp dance, `PhantomImageProvider`,
  and `PhantomStatusDecorator`'s Overview-prefix mutation are
  intentionally left in place pending operator validation that the
  new layout actually fixes the scanner-derived-name bug. Follow-up
  cleanup PR will remove the now-unnecessary cruft once validation
  confirms the spike works end-to-end.

- Plugin version bumped to `0.2.0.0` (on-disk layout change is
  unambiguously user-visible). Test rig + install docs updated
  accordingly.

## [0.1.x-pre]

### Added

- **M13 — Per-series subdir stub layout for TV phantoms.**
  Replaces the loose-file phantom-show layout from M10 (one
  symlink per series at the top of `phantom-library/shows/`)
  with a per-series subdirectory layout that Jellyfin's
  `tvshows` resolver can parse into a proper
  `Series → Season → Episode` tree. Each phantom series gets
  `phantom-library/shows/<SafeName>__phantom_tmdb<id>/Season 01/<stem> S01E01.<splashExt>`
  where the inner `S01E01.<ext>` is a symlink to the shared
  splash file; the series stub *directory* (with the
  `__phantom_tmdb<id>` sentinel still in its leaf name, so
  Suggestions' NameContains fallback in
  `FindExistingByTmdbId` keeps working) is what the Series
  BaseItem's `Path` points at. `EvictionSweeper.DemoteAsync`
  derives the inner episode file via the new
  `IPhantomStubManager.DeriveSeriesStubPaths(...)` helper and
  calls `gostream.RemoveAsync` against THAT path — gostream
  expects a file, not a directory. `PhantomStubManager.DeleteAsync`
  on a series stub recursively removes the tree only when the
  leaf carries the phantom sentinel; refuses any other dir.
  Movie stubs are byte-identical to M10 (loose-file symlinks
  under `movies/`). Operator must run
  `scripts/phantom-shows-cleanup.sh` once before triggering
  Suggestions on the new code — the loose-file phantom-show
  state from the M10 era leaves orphan Episode BaseItems that
  block the new Series rows from binding. See PLAN.md §M13 for
  the full design.

### Changed

- `install.sh` restart prompt now defaults to **yes** (`[Y/n]`).
  A new DLL has no effect until Jellyfin restarts; the previous
  `[y/N]` default caused operators hitting Enter to silently
  skip the restart and keep running the old in-memory code
  while a new DLL sat on disk.

### Added

- **M12 — Dedupe-gap heal-on-rediscovery + IMDB enrichment + host-path translation.**
  Suggestions now finds legacy broken phantom rows (those that
  lost their providers / IsLocked / Name to an earlier
  persistence-layer or scanner interaction) via a NameContains
  fallback on the `__phantom_tmdb<id>` sentinel, and heals them
  in place via `UpdateItemAsync` instead of silently creating a
  duplicate. Same `BaseItem.Id` is preserved, so any UserData
  associations survive the heal. Self-healing: every Suggestions
  / Catalogue cycle repairs every broken row that re-appears in
  the TMDB feed. Materialiser now enriches missing IMDB id from
  TMDB before querying indexers — fixes Torrentio's "requires an
  IMDB id" rejection that was silently bailing the materialise
  path on phantom rows discovered via TMDB-only flows (Trending
  / Discover). Materialiser now also translates gostream's
  container-internal FUSE path (e.g. `/mnt/gostream-mkv-virtual/
  movies/X.mkv`) into the operator's host-visible path (e.g.
  `/var/gostream/gostream-mkv-virtual/movies/X.mkv`) before
  promoting the BaseItem — without this, Jellyfin stored a path
  that didn't exist on the host filesystem and the library
  scanner culled the BaseItem on the next sweep. Translation is
  zero-config: it reads the parent CollectionFolder's
  `PhysicalLocations`, excludes the plugin-owned phantom-stub
  dir, and concats the remaining (host) location with gostream's
  filename. Uses `LibraryManager.GetCollectionFolders` (not a
  ParentId walk) to find the CollectionFolder, since phantom
  items are parented at the phantom-library physical Folder, not
  the CollectionFolder directly. Operator action after install:
  trigger Suggestions/Refresh and then press Play on a phantom;
  no repair script required. Also adds a TMDB-base-URL config
  knob (`TmdbApiBaseUrl`) so test rigs can point at a local mock,
  and ships a persistent test rig (`tools/rig-scenarios/`) for
  scripted multi-step investigations under user-mode systemd.

- **M10 — Phantom symlink library + visibility fix.** Phantoms are
  now backed by per-item symlinks under a plugin-owned writable root
  (default `/var/lib/jellyfin/phantom-library/{movies,shows}`) and
  bound into the operator's existing `gostream-movies` /
  `gostream-shows` libraries via a new
  `PhantomCollectionFolderBinder`. Restores browse visibility for
  Suggestions / SeriesIngestor rows that were invisible in v0.1
  because `Path = null` caused the scanner to cull them. New config
  knobs: `PhantomStubRoot`, `PhantomMoviesLibraryName`,
  `PhantomShowsLibraryName`. New operator install step: `sudo mkdir
  -p /var/lib/jellyfin/phantom-library/{movies,shows} && sudo chown
  -R jellyfin:jellyfin /var/lib/jellyfin/phantom-library`. Includes
  a documented workaround for an upstream Jellyfin bug in
  `LibraryStructureController.AddMediaPath` (does not refresh the
  `CollectionFolder`'s `PhysicalLocationsList` /
  `PhysicalFolderIds`); upstream PR tracked separately. The
  workaround is hardened against the Jellyfin metadata-saver
  race via three layers (verify-from-repository loop,
  `ItemUpdated` event watchdog, periodic re-bind every 5 min);
  end-to-end verified that both `gostream-movies` and
  `gostream-shows` bindings persist across multiple
  metadata-refresh cycles.
- **M11 — Post-M10 phantom UX polish.** Six bugs surfaced by
  operator live testing:
  1. *Catalogue too small.* New `ITmdbClient.DiscoverMoviesAsync`
     / `DiscoverSeriesAsync` (paginated). New config field
     `SuggestionsCatalogueMaxItems` (default 5000). New
     `SuggestionsContributor.RefreshCatalogueAsync` walks
     Discover pages until the cap is hit or the API runs out.
     Cached per-page via `CachedTmdbReader`. Respects TMDB rate
     limits via inter-page delays.
  2. *Display name showed `__phantom_tmdb<id>` filename stem.*
     `VirtualItemFactory` now stamps `ForcedSortName` and
     `SuggestionsContributor` / `SeriesIngestor` re-stamp the
     item via `UpdateItemAsync` immediately after `CreateItem`
     so the scanner's filename-derived Name cannot win.
  3. *Phantom image was splash thumbnail instead of TMDB poster.*
     `VirtualItemFactory` now stamps `ImageInfos[Primary]` with
     `https://image.tmdb.org/t/p/original<PosterPath>` at create
     time, plus a Backdrop entry. Jellyfin's image cache fetches
     lazily on first client browse; no extra TMDB round-trip
     during Suggestions.
  4. *TV Series phantoms invisible in browse.* Series rows now
     get `PresentationUniqueKey` set (was empty, which caused
     dedupe-collapse in some browse queries) and the Series-
     from-hit path uses the same stub-symlink + lock + re-stamp
     pattern as Movies.
  5. *Pressing Play never triggered materialise.* New
     `PlaybackTriggerListener` subscribes to
     `ISessionManager.PlaybackStart` and enqueues
     `MaterialiseTrigger.Play` when the played item is a phantom
     (Movie / Episode; Series is a container, autopilot handles
     it). Additionally, `UserDataSavedListener.IsMaterialisable`
     now treats items whose Path matches the
     `PhantomStubManager.Sentinel` as phantoms (not
     already-materialised) so favouriting also re-enqueues.
     `Materialiser.ResolveProviderIdsAsync` falls back to the
     plugin DB when `BaseItem.ProviderIds` is empty post-scan;
     the operator-observed "item lacks TMDB/IMDB provider ids"
     error is now self-healing.
  6. *Splash playback marked the phantom as played.*
     `PlaybackTriggerListener.OnPlaybackStopped` resets UserData
     (`PlayCount=0`, `Played=false`, `PlaybackPositionTicks=0`,
     `LastPlayedDate=null`) for each session user on a phantom
     when playback stops, so the splash doesn't pollute watch
     history. Real materialised playback continues to count
     normally.
  Regression suite (11 tests) added in `M11BugsTests.cs`;
  full suite is 153/153 passing.

## [0.1.0] - 2026-06-04

Initial release. Movies + series + materialisation + splash hand-off +
suggestions + eviction + series autopilot. Targets Jellyfin 10.11.x
(`targetAbi: 10.11.0.0`, `net9.0`).

### Added

- **M1 — gostream `POST /api/library/add`** (`e4df693` on gostream
  branch `phantom-library/api-add`). One-shot torrent-registration +
  FUSE-path-return endpoint; the contract Phantom Library calls into.
- **M2 — Plugin skeleton, configuration, packaging** (`bbe5dbf`).
  `Plugin` entry point, `PluginConfiguration`, JPRM-compatible
  `build.yaml`, manifest stub, GitHub Actions build + release
  workflows.
- **M3 — TMDB client, remote search providers, image provider,
  virtual item factory** (`e837cd1`). Movie + Series remote search
  feeding Jellyfin's "Identify" / search flow, with TMDB images
  surfaced through the standard `IRemoteImageProvider` path.
- **M4 — Materialisation pipeline** (`2451075`). Prowlarr (primary) +
  Torrentio (fallback) indexer clients, `QualityScorer` mirroring
  gostream's scorer, bounded materialisation queue, gostream client
  calling `/api/library/add` and polling for the FUSE path.
- **M5 — Fake play button + static splash hand-off** (`8ca2f8b`).
  Splash `MediaSource` served as opaque pixels; pressing play on a
  Virtual item enqueues materialisation and hands the client the
  splash; per-item status surfaced via overview-text prefix (see
  *Known limitations*).
- **M6 — Suggestions integration** (`abe0fd1`). TMDB Trending /
  Similar / Recommended folded into Jellyfin's suggestion rows;
  `tmdb_cache` with TTL tuning (default 6h trending, 24h
  similar+recs) to stay inside TMDB rate limits.
- **M6.5 — gostream Vault Mode** (`109c856` on gostream branch
  `phantom-library/vault-mode`). `persist=true` stub flag for
  per-stub full-file SSD cache; the plugin writes this opportunistically
  when the patch is detected at runtime.
- **M7 — Eviction sweeper + favourite-driven persistence** (`3377add`).
  Default 7-idle-days eviction; favourited items protected per-user
  via the admin-page user-prefs form (see *Known limitations*); Vault
  Mode persist flag set on favourites when the gostream patch is
  available.
- **M8 — TV series + autopilot** (`60de538`). Series remote search
  promoted to MVP; next-episode prefetch for series a user is
  actively watching; direct-sequel prefetch for finished movies.
- **M9 — Packaging + release polish** (this release). Troubleshooting
  README section drawn from real M2/M5 install failures, concrete
  Linux install worked example, mascot SVG/PNG, CHANGELOG, PLAN.md
  status table.

### Fixed (during M5 testing, pre-tag)

- `f6e70b7` — admin config page sent enum values as ints; Jellyfin's
  XML serializer expects enum *names*. Page would spin on save.
- `5595103` — `IApplicationPaths` resolved at registration time
  (`IServerApplicationHost.Resolve`) threw `ArgumentNullException`
  because the host isn't fully constructed yet; deferred resolution
  to a DI factory closure.
- `3a1e413` — retargeted to Jellyfin 10.11.9 / `net9.0` after the
  operator's running instance turned out to be 10.11.x rather than
  10.10.x.

### Companion gostream branches

- `phantom-library/api-add` (M1, `e4df693`) — required for any
  materialisation to work; v0.1 hard depends on it.
- `phantom-library/vault-mode` (M6.5, `109c856`) — optional;
  plugin detects at runtime and degrades gracefully if absent.

### Known limitations / partials

- **Custom QualityPreset falls back to GostreamDefault** with a
  warning log. The selector is wired through, but no custom-scorer
  use case has surfaced yet; revisit when one does.
- **Per-user preferences via admin sub-page form**, not native
  Jellyfin user-prefs integration. Functional but not idiomatic;
  proper integration is v0.2 polish.
- **Series-level `Materialise` returns `Error`** by design. A Series
  is a container, not a streamable file; materialise individual
  Episodes (autopilot does this for the next unwatched one
  automatically).
- **Splash overlay is static pixels.** Dynamic per-item status text
  would require a per-request ffmpeg transcoder; v0.1 surfaces
  status via Jellyfin's native item fields (overview-text prefix
  rendered by the client UI).
