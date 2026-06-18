# Changelog

All notable changes to this project are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- Phantom channel playback now uses Jellyfin's native `RequiresOpening`
  media-source flow instead of the finite splash video. TV/mobile/web
  clients that auto-open live media sources should show their native
  loading UI while Phantom materialises, then start the real gostream
  file as soon as it is available.
- Web badge overlays now poll visible Phantom/Materialising items and
  update/remove themselves when materialisation completes. The detail-page
  badge injection is idempotent to avoid a MutationObserver render loop;
  series/season folder thumbnails are omitted from badge state so only
  playable movies/episodes get Phantom badges.
- Added TV episode channel integration coverage for series → season →
  episode browse, native-open materialise, real gostream TV playback,
  and badge state separation between series folders and episodes.

### BREAKING — requires wipe + patched Jellyfin

Phantom Library v0.3.0 replaces the file-on-disk phantom
architecture with a Jellyfin `IChannel`-based design backed by a
small additive patch to Jellyfin's `ChannelManager`. Phantom items
are now exposed as `ChannelItemInfo` rows under two virtual
channels ("Phantom Movies" and "Phantom Shows"); the per-stub
file-on-disk tree under `/var/lib/jellyfin/phantom-library/` is
retired. Materialise-on-demand still works: a kebab → Materialise
click triggers gostream to register the underlying torrent, and a
per-item channel refresh primitive (added by the patch) re-binds
the ChannelItem to the now-real `BaseItem` produced by Jellyfin's
scan of the gostream-served file.

`phantom.db` schema bumped to v9 (was v5 in v0.2.0.0). The new
schema captures channel-item registrations, per-item materialise
state, and the additional bookkeeping the channel arch needs.
Per `AGENTS.md` § "No database migrations until v1.0", the
upgrade path is **wipe and rebuild**.

Operator steps (in order):

1. `sudo systemctl stop jellyfin`
2. `sudo bash scripts/phantom-wipe.sh`            # dry-run; inspect counts
3. `sudo bash scripts/phantom-wipe.sh --commit`   # type `WIPE` to proceed
4. `./install.sh --build`
   - applies the patches in `scripts/jellyfin-patches/` to
     `jellyfin/` (idempotent),
   - builds the patched Jellyfin assemblies
     (MediaBrowser.Controller + Jellyfin.LiveTv),
   - builds the plugin DLL,
   - installs the plugin DLL into the operator's Jellyfin plugins
     dir,
   - prints the exact `sudo cp` commands to deploy the patched
     Jellyfin DLLs into the runtime install dir.
5. Deploy the patched Jellyfin DLLs per the commands printed at
   the end of step 4. See `docs/operator-deploy.md` for context
   and the package-manager-clobber detection procedure.
6. `sudo systemctl start jellyfin`
7. Dashboard → Plugins → Phantom Library → Settings; confirm
   gostream paths; click **Save**.
8. Dashboard → Scheduled Tasks → **"Phantom Library: Discovery
   Refresh"** → **Run Now**.
9. Refresh the browser. "Phantom Movies" and "Phantom Shows"
   tiles appear in your library nav.
10. Smoke-test: click a phantom item, **Play**. Jellyfin should show
    its native loading UI while Phantom materialises the item, then
    start the real gostream file automatically.

Manual fallback (if `scripts/phantom-wipe.sh` is unavailable for
any reason): inspect the script's source for the exact SQL
delete + cascade pattern. Do NOT hand-craft a substitute SQL
block; the script's CHECK-constraint verification + 50% sanity
cap exists for good reasons (see `AGENTS.md` § "Production
database safety").

#### Known regressions (operator-accepted)

- **Loss of `CollectionType.movies` Home rows** ("Latest Movies",
  "Continue Watching Movies", "Suggestions") for gostream content.
  Channels surface their own "Latest in Phantom Movies" /
  "Latest in Phantom Shows" rows instead; the Movies-typed library
  rows no longer include phantom content because phantom content
  is no longer a Movies-typed library.
- **UserData on the pre-v0.3.0 gostream-bound BaseItems is lost**
  in the wipe. Favourites, watched state, and playback position
  on gostream content created under v0.2.0.0 do not survive. Real
  (non-gostream) library UserData is untouched.
- **Pre-existing gostream files the plugin doesn't know about**
  appear with raw filename Names in the channel listing until
  materialised through the plugin. Operator can opt into the TMDB
  title-search fallback (per plugin config:
  `EnrichOrphanGostreamItemsViaTmdbSearch = true`) to back-fill
  metadata for these orphans on first channel sync.
- **Per-item channel refresh** requires the patched Jellyfin
  assemblies. The plugin DLL alone will load but materialise-on-
  demand will fail with a `TypeLoadException` for
  `IChannelItemRefreshManager`. See `docs/operator-deploy.md`
  for the patch deploy procedure.
- **Package-manager upgrades** of the `jellyfin-server` package
  silently clobber the deployed patched DLLs. `install.sh` and
  `docs/operator-deploy.md` document the detection + remediation.

### Added

- **Channel architecture (M-channel).** Phantom Movies + Phantom
  Shows IChannel implementations replacing the file-on-disk
  stub-symlink layout. Per-channel item discovery, per-item
  refresh, and materialise-on-demand wired through new
  `IChannelItemRefresh` opt-in interface and
  `IChannelItemRefreshManager` service (both purely additive to
  Jellyfin core; see `scripts/jellyfin-patches/`).
- **`scripts/jellyfin-patches/`** — three additive patches against
  Jellyfin exact tag v10.11.9 (base SHA `e83a7e62f2`):
  `0001-Add-IChannelItemRefresh-opt-in-interface...`,
  `0002-Add-IChannelItemRefreshManager-service...`,
  `0003-Add-tests-for-ChannelManager-per-item-refresh...`.
  Applied by `install.sh --build` idempotently. See
  `scripts/jellyfin-patches/REBASE.md` for rebase guidance on
  Jellyfin upstream version bumps.
- **`docs/operator-deploy.md`** — operator guide for deploying the
  patched Jellyfin DLLs (`MediaBrowser.Controller.dll`,
  `Jellyfin.LiveTv.dll`) alongside the plugin. Covers Model A
  (in-place DLL swap; recommended) and Model B (run Jellyfin from
  a self-built tree). Includes package-manager-clobber detection
  via md5 compare against `.pre-phantom-bak` sidecars.
- **`phantom.db` schema v9.** Channel-item registration + per-item
  materialise-state bookkeeping. Wipe-and-rebuild upgrade path
  per AGENTS.md.

### Removed

- **File-on-disk phantom stub tree** under `<jellyfin-data>/phantom-library/`.
  Replaced by IChannel ChannelItemInfo rows. The wipe script
  (`scripts/phantom-wipe.sh`) tears down the tree as part of the
  upgrade.
- **`gostream-movies` / `gostream-shows` CollectionFolders** in
  jellyfin.db. The IChannel implementation owns the BaseItem IDs
  for gostream-served content now. Wipe script drops them along
  with the BaseItems they collected.

### Notes

- Plugin version bumped to `0.3.0.0`. `manifest.json`, `build.yaml`,
  and the plugin csproj all match.
- `install.sh --build` is the documented upgrade path. The plugin
  DLL alone is insufficient; the patched Jellyfin DLLs must also
  be deployed before the next `jellyfin.service` restart.
- Phase 8 (upstream PR for the Jellyfin patches) is deferred and
  operator-driven per Jellyfin's LLM/AI contribution policy. See
  `docs/plans/channel-handoff.md` § Phase 8.

## [0.2.0.0]

### Added

- **Jellyfin-native stub-layout (`[tmdbid-<id>]` path tokens).**
  Phantom stubs use the Jellyfin-native
  `<Title> (<Year>) [tmdbid-<id>]` filename / directory layout
  instead of the custom `__phantom_tmdb<id>` sentinel scheme.
  Newly-created stubs (movies and series) render with the real
  title in Jellyfin — no more `Word_Word__phantom_tmdb1234`
  scanner-derived names. Year segment is omitted when TMDB lacks
  it. Episode filenames under series stubs intentionally drop the
  bracketed token (the series directory carries it) so the
  `tvshows` resolver derives clean episode names.
  `PhantomStubManager` exposes year-aware overloads of
  `CreateAsync`, `DeriveFilename`, and `DeriveSeriesStubPaths`;
  old overloads forward with `year: null` for back-compat. A new
  `PhantomPathUtilities` helper centralises the dual-recognition
  logic (legacy sentinel OR new token) used by dedupe / heal /
  eviction paths — no more scattered `Contains("__phantom_tmdb")`
  substring checks.

- `phantom.db` schema v5 adds a `plugin_meta` key/value table
  for cross-restart marker storage (see AGENTS.md § "Single-
  operator deployment" for the design rationale).

### BREAKING — requires wipe

This release changes the on-disk phantom stub layout AND bumps
the `phantom.db` schema. Per `AGENTS.md` § "No database migrations
  until v1.0", the project does not ship migration tooling pre-v1.0;
the upgrade path is **wipe and rebuild**.

**Operator steps to upgrade from any earlier version:**

1. `sudo systemctl stop jellyfin`
2. Delete `phantom.db` (plus `-wal` / `-shm` sidecars). Plugin
   recreates the schema on next start.
3. Delete every BaseItem in `jellyfin.db` whose `Path` begins with
   the phantom stub root (default
   `/var/lib/jellyfin/phantom-library/`). Cascade-clean FK rows
   in `UserDatas`, `BaseItemProviders`, `MediaStreams`,
   `MediaAttachments`, `Chapters2`, `AncestorIds`, etc.
3. `rm -rf` everything under the phantom stub root EXCEPT the
   `.phantom-library-keep` sentinel files and the `.splash.*`
   asset.
4. `./install.sh --build` to install the new plugin DLL.
5. `sudo systemctl start jellyfin`
6. Dashboard → Scheduled Tasks → trigger **"Phantom Library —
   refresh suggestions"** to repopulate with the new layout.

User-visible state in `jellyfin.db` outside the phantom tree
(favourites, watched, watch history on real-media items) is not
touched by this procedure.

### Removed

- **In-plugin `StubLayoutMigration` IHostedService.** Ran on
  plugin startup while Jellyfin was live, moved stub files on
  disk, and called `UpdateItemAsync` to repoint `BaseItem.Path`.
  It raced Jellyfin's live library scanner — the watcher saw old
  paths vanish, the scanner saw new-format paths appear and
  created **fresh BaseItems** for them — leaving the library
  with duplicate BaseItems and the UI still showing the legacy
  scanner-derived names. AGENTS.md gains "Single-operator
  deployment" + "No database migrations until v1.0" sections
  codifying the rule that motivated this rollback.

- Repo-shipped migration scripts and their rig harnesses.
  Pre-v1.0, schema evolution is wipe-and-rebuild per the
  AGENTS.md rule above.

### Notes

- **A/B spike scope.** The heal-on-rediscovery logic, the forced
  `IsLocked = true` re-stamp dance, `PhantomImageProvider`, and
  `PhantomStatusDecorator`'s Overview-prefix mutation are
  intentionally left in place pending operator validation that
  the new layout actually fixes the scanner-derived-name bug.
  Follow-up cleanup PR will remove the now-unnecessary cruft
  once validation confirms the spike works end-to-end.

- Plugin version bumped to `0.2.0.0`. Test rig + install docs
  updated accordingly.

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
