# Phantom Library — Implementation Plan

A Jellyfin plugin that makes the entire TMDB catalogue appear to exist inside a
Jellyfin library. Titles materialise on demand: a user favourites or plays an
item, the plugin asks [gostream](https://github.com/MrRobotoGit/gostream) to
register the matching torrent and writes a virtual `.mkv` stub into gostream's
physical source directory. gostream's FUSE layer turns that stub into a
seekable file that Jellyfin can scan and stream from.

Mascot: *Stygiomedusa gigantea*, the giant phantom jelly.

---

## Resolved Design Decisions

Previous open questions have been answered. Recorded here so future
contributors can see the rationale.

0. **Target Jellyfin version.** 10.10.x. Plugin compiles against the
   10.10 plugin ABI; older servers are not supported.
0a. **gostream tree handling.** Not vendored. The upstream fork is
    checked out alongside this repo for development convenience and
    `.gitignore`d. Patches are committed to the fork and PR'd upstream;
    Phantom Library depends on the patched gostream at runtime, never
    by source inclusion.
0b. **Splash dynamism.** Splash is a `MediaSource` URL the plugin
    serves as opaque pixels to Jellyfin clients (video players, not
    browsers — no JS / HTML overlay possible client-side). Dynamic
    text-over-video would require an ffmpeg transcoder per request,
    which is out of scope. v0.1 ships a pre-baked looping splash with
    static branding; per-item materialisation status is surfaced via
    Jellyfin's native item fields (name suffix, overview text,
    progress indicators where available) rendered by the client UI,
    not burnt into the splash pixels. Dynamic overlay is a deferred
    feature.
0c. **`/api/library/add` blocking semantics.** No `wait_for_ready`
    flag. The endpoint inherently blocks for torrent-metadata
    resolution (gostream needs the real file size to write the stub).
    A bounded internal timeout (default 45s, server-config
    overridable) returns 504 on exceed. After return, the stub exists
    and the FUSE path is observable, but bytes are not guaranteed
    streamable yet — the plugin polls FUSE-path existence before
    calling `ILibraryManager.RefreshLibrary` and lets Jellyfin's
    normal scan + playback path tolerate the usual
    FUSE-pulls-on-demand latency. This keeps the contract simple
    (one bounded blocking call, no indefinite waits) and matches
    gostream's existing sync-engine behaviour.

1. **Quality selection.** Configurable in plugin settings, defaulting to
   gostream's behaviour (the rules from `internal/syncer/quality/scorer.go`:
   4K DV > 4K HDR10+ > 4K HDR > 4K > 1080p REMUX > 1080p, with the same
   seeder and size floors). A simpler preset ("biggest `.mkv`, most
   seeders") and per-user / per-library overrides are surfaced through the
   admin config page.
2. **Indexer source.** Prowlarr is the primary indexer; Torrentio is the
   fallback. Mirrors gostream's own pattern. If Prowlarr is not configured,
   Torrentio is used directly.
3. **TV series scope.** Series support is an MVP requirement, not v0.2. A
   movies-only PoC is acceptable as an intermediate step but the v0.1
   release must include series. The plugin proactively materialises (and,
   where the Vault Mode patch is available, pre-warms) **next episodes**
   of any series a user is actively watching and **direct sequels** of
   movies a user has favourited or finished.
4. **Library scope and metadata ownership.** The plugin's library and the
   play / favourite system are explicitly decoupled. Virtual items appear
   inline with materialised items in normal Jellyfin views; users do not
   need to know which is which (a small "phantom" badge marks Virtual
   items, distinct from the "unavailable" badge). Favourites and watch
   state are per-user as Jellyfin normally handles them. The plugin
   *observes* user interactions to decide what to promote from Phantom to
   Virtual and from Virtual to Materialised. Metadata is held locally in
   the plugin's own SQLite DB. This is **two layers of lazy loading**:
   - **Layer 1**: Phantom → Virtual → Materialised, driven by user
     interactions in the plugin.
   - **Layer 2**: gostream's normal pull-bytes-on-demand FUSE behaviour.
5. **Eviction policy.** Default: evict Materialised items after **7 days**
   without playback. Configurable through the admin config page. Favourite
   items have a per-user-configurable toggle, **"Protect favourites from
   eviction"** (default on). When on, a favourited item is exempt from the
   timer. When the user un-favourites it, it re-enters the normal eviction
   schedule on the next garbage-collection round (and is evicted
   immediately if already past the timeout). Phantom items that never
   reach Virtual within the deadline are also evicted (see additional
   design notes below).
6. **Unavailable titles.** Surface as Virtual with an explicit
   "unavailable" badge in the UI. No silent hiding, no play-time-only
   failure.
7. **Play-press UX.** For Virtual items that have not yet been
   materialised, the play button is a **fake button** owned by the plugin
   (a custom `MediaSource` / playback-info shim that returns a Phantom
   Library status page or short looping animated splash instead of a real
   stream). It shows the phantom-jelly mascot and rotating playful status
   messages ("Reticulating splines…", facts about *Stygiomedusa
   gigantea*, materialisation step labels). As soon as the underlying
   `Materialiser` finishes, the overlay hands off to Jellyfin's real play
   button; the user can then press play normally and gostream + Jellyfin
   take it from there. No 30-second-Jellyfin-timeout risk because the
   plugin never claims a real `MediaSource` until the FUSE file is ready.
8. **Gostream integration.** The plugin talks to gostream exclusively
   through the new `POST /api/library/add` endpoint (the "primary patch"
   below). No raw stub writes, no dependency on the JSON stub format, no
   four-step orchestration.
9. **Auth between plugin and gostream.** Punt. Plugin and gostream are
   assumed reachable on a trusted network (loopback for the common
   single-host case, private LAN otherwise). README documents this; a
   reverse-proxy-with-auth deployment is left to the operator.
10. **Repository / release target.** Self-hosted repo, README-based
    install instructions. After v0.1 stabilises, evaluate submitting to
    the official Jellyfin plugin catalogue.

### Additional design notes (from the operator)

These refine the lifecycle model and shape several milestones.

- **Phantom promotion rule.** Phantom items only promote to Virtual when a
  user favourites them or attempts to play them. If a Phantom is never
  promoted within its eviction deadline (same configurable default as
  Materialised eviction — 7 days), it is dropped from the plugin's DB and
  will not be re-considered for materialisation unless TMDB / a
  suggestions surface re-introduces it. This prevents Phantom DB bloat
  from one-time browsing.
- **Eager indexer resolution (parallel pre-resolve).** As soon as a
  Phantom item is observed (e.g. surfaced in Suggestions, returned by
  search, mentioned as a "Similar to" of something the user is
  interacting with), the plugin enqueues a *background* indexer query
  against Prowlarr (with Torrentio fallback) keyed by the item's
  IMDB / TMDB ID. The chosen candidate magnet is cached in `magnet_cache`.
  When the user later presses play or favourites, the materialiser hits
  the cached candidate and skips straight to the gostream API call,
  cutting perceived latency by the indexer round-trip (typically 2–10 s
  for Prowlarr, more for Torrentio). Concurrency caps and per-indexer
  rate-limiting from `MaterialisationQueue` apply; pre-resolve is
  deprioritised relative to user-triggered materialisations.
- **Two-layer lazy loading is the architectural identity.** Layer 1 is
  Phantom → Virtual → Materialised inside the plugin. Layer 2 is
  gostream's normal FUSE-on-demand byte serving. Documentation, UI copy,
  and the README all lean on this framing.
- **Deferred feature: manual torrent picker.** A future capability that
  lets a user view candidate torrents from multiple indexers for a
  Virtual item and pick one explicitly (rather than relying on the
  quality scorer). Useful when the user wants a specific release group,
  audio track, or smaller / larger file size than the default. Tracked
  below in the Deferred Features section; not part of v0.1.

---

## Goals

- A Jellyfin user opens search, types a title, the plugin returns TMDB
  results that are not yet in the library. The user picks one; the plugin
  registers it as a Virtual library item with full TMDB metadata.
- The user presses ❤️ on any Virtual item. The plugin resolves the best
  available torrent, calls gostream to register it, writes the `.mkv` stub,
  triggers a targeted library refresh. Within seconds the item is
  Materialised: it has a real FUSE-backed path and is playable from any
  Jellyfin client.
- The user presses ▶️ on a Virtual item that has not yet been materialised.
  The plugin attempts synchronous materialisation within Jellyfin's
  PlaybackInfo budget. If it succeeds the stream starts; if it would exceed
  the budget, a friendly message is returned and a background
  materialisation is enqueued so the second press works.
- "Recommended for you", "Similar to X", and "Trending" surfaces in Jellyfin
  are populated by Virtual items pulled from TMDB, so housemates discover
  content without leaving Jellyfin.
- Works across every Jellyfin client (web, Android, iOS, Android TV, Apple
  TV) because all of the above is server-side and uses native Jellyfin
  primitives (library items, remote search, media source provider). No
  plugin-only web UI is required for the core flow.

## Non-goals (v0.1)

- A custom web "Discover" tab. Native Jellyfin search + Suggestions cover the
  same ground without per-client UI work.
- Pre-bulk-importing TMDB. Lazy materialisation is the design.
- Replacing gostream's own scheduled Movies / TV sync. They continue to run
  and pre-populate trending content; Phantom Library complements them.
- A Plex-compatible watchlist shim.

---

## High-level architecture

```
┌──────────────────────────────────────────────────────────────────────────┐
│                          Jellyfin Server                                 │
│                                                                          │
│  ┌─────────────┐    ┌────────────────────┐   ┌─────────────────────────┐ │
│  │  Web /      │    │  Library Manager   │   │  Phantom Library Plugin │ │
│  │  Mobile /   │◄──►│  (BaseItem,        │◄─►│                         │ │
│  │  TV clients │    │   IUserDataMgr,    │   │  • RemoteSearchProvider │ │
│  └─────────────┘    │   IMediaSourceMgr) │   │  • UserDataSaved hook   │ │
│                     └─────────┬──────────┘   │  • MediaSourceProvider  │ │
│                               │              │  • TMDB client          │ │
│                               │              │  • gostream client      │ │
│                               │              │  • Indexer client       │ │
│                               │              │  • Materialisation queue│ │
│                               │              │  • SQLite state         │ │
│                               │              └────────────┬────────────┘ │
└───────────────────────────────┼───────────────────────────┼──────────────┘
                                │                           │
                                │ scans                     │ HTTP
                                ▼                           ▼
                ┌───────────────────────────┐  ┌────────────────────────────┐
                │  gostream FUSE mount      │  │  gostream HTTP             │
                │  /…/gostream-mkv-virtual  │  │  :8090 torrents/settings   │
                │  (real .mkv to Jellyfin)  │  │  :9080 control/scheduler   │
                └─────────────┬─────────────┘  └─────────────┬──────────────┘
                              │                              │
                              │ writes stub                  │ resolve magnet
                              ▼                              ▼
                ┌───────────────────────────┐  ┌────────────────────────────┐
                │  gostream physical source │  │  Prowlarr / Torrentio       │
                │  /…/gostream-mkv-real     │  │  (indexer aggregation)      │
                │  (JSON stub files)        │  │                             │
                └───────────────────────────┘  └─────────────────────────────┘
```

## Item lifecycle

| State | Storage | Playable | Metadata | Notes |
|-------|---------|----------|----------|-------|
| **Phantom** | Plugin DB only, not in Jellyfin | No (fake play button shows splash) | Cached TMDB | Surfaces via remote search, suggestions, similar-to. Evicted from plugin DB if not promoted within the eviction deadline. Indexer query may be pre-resolved in the background. |
| **Virtual** | Plugin DB + Jellyfin DB row (`LocationType = Virtual`, no `Path`) | No (fake play button shows splash with materialisation status) | Full TMDB, locally persisted | Created when a user favourites a Phantom, attempts to play it, or it is surfaced as a "Similar to" of an active interaction |
| **Materialised** | Same Jellyfin DB row, `LocationType = FileSystem`, `Path` set to FUSE-backed `.mkv` | Yes (Jellyfin's real player) | Same TMDB metadata, no re-fetch | Stub exists in `gostream-mkv-real`, FUSE serves it from `gostream-mkv-virtual`. Real ▶ button takes over from the splash overlay |
| **Watched** | Same row + per-user data (watched, position, rating, favourite) | Yes | All preserved | Standard Jellyfin behaviour; per-user as normal |
| **Evicted** | Demoted back to Virtual (Jellyfin row preserved with no `Path`) or, for Phantoms that never promoted, removed from plugin DB | No (until re-materialised) | Preserved across Virtual eviction; lost on Phantom eviction | Triggered by eviction sweeper: default 7 days without playback. Favourites exempt when "Protect favourites" is on for that user. |

Transitions are driven by:

- **User actions** — favouriting, attempting to play (fake button),
  watching, un-favouriting.
- **Suggestions / similar-to surfaces** — populate Phantoms in the plugin
  DB so they can be indexer-pre-resolved before any user click.
- **Series autopilot** — when a user is actively watching a series, the
  next episode (and, where Vault Mode is available, a small prefetch
  window beyond it) materialises automatically. Same for direct sequels
  of favourited / completed films.
- **gostream sync engine** — continues to pre-populate trending content
  independently of the plugin. The plugin's eviction sweeper does not
  remove items it does not own; sync-engine stubs are gostream's
  responsibility.
- **Eviction sweeper** — background `IHostedService` runs on a schedule
  (default daily), demotes idle Materialised items to Virtual and prunes
  stale Phantoms from the plugin DB. Per-user favourite-protection toggle
  consulted before demoting any Materialised item.

---

## Components — Jellyfin plugin (`Jellyfin.Plugin.PhantomLibrary`)

### Project layout

```
phantom-library/
├── PLAN.md                              (this file)
├── README.md
├── LICENSE
├── build.yaml                           (jellyfin-plugin manifest source)
├── manifest.json                        (published plugin repo manifest)
├── src/
│   └── Jellyfin.Plugin.PhantomLibrary/
│       ├── Jellyfin.Plugin.PhantomLibrary.csproj
│       ├── Plugin.cs                    (entry point, GUID, name, version)
│       ├── PluginServiceRegistrator.cs  (DI registrations)
│       ├── Configuration/
│       │   ├── PluginConfiguration.cs   (TMDB key, gostream URLs, indexer cfg,
│       │   │                             quality knobs, eviction policy)
│       │   └── configPage.html          (admin config UI in Jellyfin dashboard)
│       ├── Api/
│       │   ├── PhantomLibraryController.cs   (REST: trigger refresh, manual
│       │   │                                  enqueue, status, debug)
│       │   └── Models/                       (DTOs)
│       ├── Search/
│       │   ├── TmdbMovieRemoteSearchProvider.cs   (IRemoteSearchProvider<Movie,
│       │   │                                       MovieInfo>)
│       │   ├── TmdbSeriesRemoteSearchProvider.cs  (Series is MVP, ships in v0.1)
│       │   └── PhantomImageProvider.cs            (IRemoteImageProvider, pulls
│       │                                           TMDB posters/backdrops)
│       ├── Library/
│       │   ├── VirtualItemFactory.cs    (creates Virtual BaseItem rows from
│       │   │                             TMDB payloads)
│       │   ├── SuggestionsContributor.cs (feeds Virtual items into "Trending",
│       │   │                              "Similar", "Recommended" rows)
│       │   ├── VirtualLibraryRoot.cs    (the synthetic library folder where
│       │   │                              Phantom Library owns items)
│       │   └── SeriesAutopilot.cs       (watches "Next Up" / playback-finished
│       │                                  events; materialises next episode and
│       │                                  sequels ahead of need)
│       ├── Playback/
│       │   ├── PhantomMediaSourceProvider.cs   (IMediaSourceProvider — owns the
│       │   │                                    fake play button for Virtual /
│       │   │                                    Phantom items; returns a splash
│       │   │                                    MediaSource that loops while
│       │   │                                    materialisation runs in
│       │   │                                    background; hands off to
│       │   │                                    Jellyfin's real player once the
│       │   │                                    FUSE path is ready)
│       │   └── SplashStream.cs                 (the looping splash payload:
│       │                                         phantom-jelly logo, status text,
│       │                                         rotating jelly trivia)
│       ├── Materialisation/
│       │   ├── MaterialisationQueue.cs  (Channel<T> + N workers, per-indexer
│       │   │                             concurrency caps for rate limits;
│       │   │                             separate lanes for user-triggered
│       │   │                             vs. eager-pre-resolve work)
│       │   ├── Materialiser.cs          (one item end-to-end: indexer →
│       │   │                             gostream API → library refresh →
│       │   │                             Jellyfin item promotion)
│       │   ├── EagerResolver.cs         (background pre-resolution of Phantom
│       │   │                              items so the magnet is cached before
│       │   │                              the user ever clicks)
│       │   ├── UserDataSavedListener.cs (subscribes to IUserDataManager
│       │   │                              .UserDataSaved; enqueues on
│       │   │                              ❤️ transitions and toggles persist
│       │   │                              flag when Vault Mode is present)
│       │   └── EvictionSweeper.cs       (hosted background service;
│       │                                  per-user favourite-protection;
│       │                                  prunes stale Phantoms; demotes idle
│       │                                  Materialised items to Virtual)
│       ├── Clients/
│       │   ├── ITmdbClient.cs / TmdbClient.cs
│       │   ├── IGostreamClient.cs / GostreamClient.cs   (talks exclusively to
│       │   │                                              `/api/library/add` on
│       │   │                                              :9080 — primary patch)
│       │   ├── IIndexerClient.cs / ProwlarrClient.cs / TorrentioClient.cs
│       │   └── QualityScorer.cs                            (configurable; default
│       │                                                    mirrors gostream's
│       │                                                    scorer.go)
│       ├── State/
│       │   ├── PhantomDb.cs            (SQLite, separate from Jellyfin's DB;
│       │   │                            phantom registry, magnet cache,
│       │   │                            indexer-query cache, materialisation
│       │   │                            history, eviction timestamps,
│       │   │                            per-user favourite-protection prefs)
│       │   └── Migrations/
│       └── Util/
│           └── ImdbTmdbMapper.cs
└── tests/
    └── Jellyfin.Plugin.PhantomLibrary.Tests/
        └── …                            (xUnit; mock Jellyfin host;
                                           integration tests against a local
                                           gostream)
```

### Key Jellyfin extension points

- **`IRemoteSearchProvider<TItemType, TLookupInfoType>`** — registered for
  both `Movie` and `Series` (series is MVP). Surfaces TMDB hits inside
  Jellyfin's native search UI on every client.
- **`IRemoteImageProvider`** — supplies TMDB posters/backdrops for items the
  plugin owns.
- **`IUserDataManager.UserDataSaved`** event — fires when favourite toggles,
  playback state changes, watched flag changes. `UserDataSavedListener`
  filters: favourite-to-true on a Phantom or Virtual item enqueues
  materialisation; playback-stopped-near-end on an episode triggers
  `SeriesAutopilot` for the next episode; favourite-to-false re-enters the
  item into normal eviction scheduling.
- **`IMediaSourceProvider`** —
  `PhantomMediaSourceProvider.GetMediaSources` is invoked during
  `/Items/{id}/PlaybackInfo`. For Phantom / Virtual items it returns the
  splash MediaSource (the fake play button) and enqueues materialisation;
  for Materialised items it returns nothing (Jellyfin uses its normal
  file-based MediaSource for the FUSE path).
- **`ILibraryManager`** — `CreateItem`, `UpdateItem`, `RefreshLibrary`
  (scoped to the single directory the gostream API returned).
- **`IServerEntryPoint` / `IHostedService`** — for the materialisation
  queue workers, the eager pre-resolver, the series autopilot, and the
  eviction sweeper.

### Configuration

Server-wide settings live in the Jellyfin admin dashboard
(`configPage.html`). Per-user toggles live in the user-preferences page
(the plugin contributes a section via the standard Jellyfin user-settings
extension point).

Server-wide:

- TMDB API key
- gostream API base URL (`:9080` — the `/api/library/add` endpoint).
  `:8090` is talked to only for diagnostics.
- Prowlarr URL + API key (primary indexer)
- Torrentio URL (fallback indexer)
- Quality preset (defaults to gostream-equivalent scoring) plus minimum
  seeders, size floors, and a free-form override for the scorer's tunable
  weights for advanced users
- Eviction defaults: enabled / disabled, idle days (default 7), GC schedule
- Materialisation concurrency caps (per-indexer, global)
- Eager pre-resolve enabled / disabled, max concurrent pre-resolves
- Phantom DB retention (default same as eviction window)
- Series autopilot: enabled / disabled, prefetch window in episodes
  (default 1)
- "Phantom badge" visibility (always show / hide for non-admins / off)
- Splash content (default loop, custom upload, jelly-trivia rotation
  enabled)

Per-user:

- Protect favourites from eviction (default on)
- Show Phantom items in browse views (default on)
- Allow eager pre-resolve based on this user's interactions (default on;
  off for read-only / guest accounts)

### Materialisation flow

`Materialiser.MaterialiseAsync(Item item, MaterialiseTrigger trigger)`

1. Look up cached indexer resolution in `PhantomDb.magnet_cache`. If
   present, fresh, and from an eager pre-resolve, skip to step 4.
2. Query Prowlarr by IMDB / TMDB ID. If no acceptable candidate, fall back
   to Torrentio. Apply the configured `QualityScorer`. Cache the best
   result.
3. If no acceptable candidate: persist "unavailable" state in `PhantomDb`
   and update the Jellyfin item to display the unavailable badge. Return.
4. `POST /api/library/add` to gostream `:9080` including the IMDB / TMDB
   ID, the title and year, and (optionally) the resolved magnet. For TV
   episodes also include `season`, `episode`, `series_imdb`. The call
   blocks for torrent-metadata resolution (bounded server-side, default
   45s, 504 on exceed).
5. Gostream replies with `{stub_path, fuse_path, hash, size}`. Filename
   conventions, JSON stub layout, and physical-path placement are
   entirely gostream's responsibility — the plugin does not need to
   know them. The plugin then polls `File.Exists(fuse_path)` with a
   short backoff (cap 5s) to confirm the stub has propagated through
   the FUSE layer.
6. Call `ILibraryManager.RefreshLibrary` scoped to
   `dirname(fuse_path)` so Jellyfin picks up the new file without a full
   scan.
7. Promote the Jellyfin item from Phantom or Virtual to Materialised:
   update its `Path` and `LocationType`; persist per-user data intact. If
   Jellyfin rejects in-place mutation (see Risks), create a new
   FileSystem-backed item and migrate user data across.
8. If Vault Mode is present and the item is favourited by any user with
   "Protect favourites" enabled, rewrite the stub via gostream to set
   `persist=true` and call `POST /api/library/prestage`.
9. Emit a `PhantomMaterialised` event for instrumentation.

Failure modes are recorded in `PhantomDb.materialisation_log` with a
backoff so that one bad title doesn't get retried in a tight loop.

### State persistence

`PhantomDb` lives at `<dataPath>/plugins/PhantomLibrary/phantom.db`. Schema
sketch:

| Table | Purpose |
|-------|---------|
| `phantom_items` | Mapping Jellyfin item GUID ↔ TMDB ID / IMDB ID, current state (Phantom / Virtual / Materialised), first-seen and last-touched timestamps, eviction-protection flags, type (movie / series / episode) |
| `magnet_cache` | Cached indexer results per (tmdb_id, quality_preset), with TTL, seeder snapshot, and a flag noting whether the result came from eager pre-resolve or user-triggered query |
| `materialisation_log` | Audit trail of each attempt: trigger (favourite / play / autopilot / pre-resolve), duration, outcome, error |
| `unavailable_marker` | TMDB IDs that returned no acceptable torrent, with retry-after timestamp |
| `user_prefs` | Per-Jellyfin-user toggles (protect-favourites, show-phantoms, allow-eager-pre-resolve) |
| `autopilot_state` | Per-(user, series) cursor: last episode played, next episode pre-materialised, prefetch-window cursor |

Kept separate from Jellyfin's DB to avoid schema-version coupling and to
survive plugin upgrades without database migrations against Jellyfin's
schema.

---

## Components — gostream patch

A small upstream change makes Phantom Library robust against gostream
internals. Scope intentionally minimal so the PR has a chance of being
accepted.

### Proposed change: `POST /api/library/add` on `:9080`

Request body:

```json
{
  "type": "movie" | "episode",
  "imdb": "tt1234567",
  "tmdb": 603,
  "title": "The Matrix",
  "year": 1999,
  "season": 1,                  // episodes only
  "episode": 4,                 // episodes only
  "series_imdb": "tt0903747",   // episodes only
  "magnet": "magnet:?xt=urn:btih:...",  // optional — if omitted, gostream
                                         // resolves via its own indexer chain
  "min_quality": "1080p"        // optional override of scheduler defaults
}
```

The endpoint blocks until torrent metadata is resolved and the stub is
written (gostream cannot produce a meaningful `size` without metadata).
A bounded server-side timeout (default 45s, configurable) returns 504
on exceed. It does **not** wait for first-byte readiness; the FUSE
layer serves bytes on demand once the stub exists.

```json
```

Response:

```text
{
  "stub_path": "/mnt/gostream-mkv-real/movies/The.Matrix.1999_abc12345.mkv",
  "fuse_path": "/mnt/gostream-mkv-virtual/movies/The.Matrix.1999_abc12345.mkv",
  "hash": "abc12345...",
  "size": 12345678901
}
```

This consolidates the four-step flow (add → poll → write stub → wait) into
one synchronous call inside gostream, where it can share code with the
movies / tv sync engines (`movie_go.go::createMKV`,
`quality/scorer.go`). It also removes the plugin's dependency on the exact
stub JSON format.

Files likely touched in gostream:

- `internal/syncer/engines/movie_go.go` — extract `createMKV` and the
  filename / quality logic into reusable functions.
- `internal/syncer/engines/tv_go.go` — same for episode path conventions.
- `internal/monitor/dashboard/handler.go` (or wherever `:9080` routes live)
  — add `/api/library/add` and an idempotent `/api/library/remove`.
- `internal/syncer/quality/scorer.go` — expose a public `Score` function for
  the new endpoint.

Backwards compatibility: the plugin should detect the endpoint at startup
(`GET /api/library/add` returns 405 if present, 404 if not) and fall back to
the four-step direct flow against `:8090` + raw stub writes if absent. This
keeps the plugin usable against unpatched gostream.

### Secondary patch (optional, larger): Jellyfin watchlist source

A separate, smaller change replacing the hardcoded Plex watchlist source in
`internal/syncer/engines/watchlist_go.go` with a pluggable interface, plus a
Jellyfin Favourites adapter. Useful for users who do not want to run the
Phantom Library plugin but do run Jellyfin. Tracked as an independent PR.

### Tertiary patch (nice-to-have): eviction API

`POST /api/library/remove` with `{stub_path}` — removes the torrent from
GoStorm, deletes the stub, and updates the inode map. Phantom Library's
eviction sweeper calls this.

### Quaternary patch (independent PR): persistent full-file SSD cache ("Vault Mode")

**Problem.** gostream's existing SSD warmup is hard-capped to the first 64 MB
(`warmup.FileSize`) and last 16 MB (`warmup.TailWarmupSize`) of every file.
GoStorm's piece cache (`UseDisk=true`, `RemoveCacheOnDrop=false`) preserves
pieces that were *actually streamed*, but middle-of-file bytes that nobody
has read are never on disk. For unpopular titles where the peer swarm dies
between plays, replay stalls or fails entirely — the swarm has to revive
from DHT and find seeds for byte ranges that aren't cached.

**Proposed change.** A per-stub opt-in flag that asks gostream to cache the
entire file to SSD on first play (or on a background pre-stage trigger), and
to keep those pieces resident until evicted by quota policy.

#### Stub format extension

```json
{
  "url": "http://127.0.0.1:8090/stream?link=<hash>&index=<id>&play",
  "size": 12345678901,
  "magnet": "magnet:?xt=urn:btih:...",
  "imdb": "tt0816692",
  "persist": true,
  "persist_priority": 50
}
```

- `persist` (bool, default false) — when true, gostream removes the
  `off > FileSize` gate in `warmup.WriteChunk` for this file's hash and writes
  every chunk that flows through the pump to SSD.
- `persist_priority` (int, optional) — eviction weight, higher = retained
  longer under quota pressure. Mirrors gostream's existing `disk_warmup_quota_gb`
  semantics; defaults to a middle value.

#### Code touch points in gostream

- `internal/warmup/warmup.go` — add a `sync.Map[hash → persistEntry]` and
  consult it inside `WriteChunk` / `processWrite` before the
  `off > FileSize` short-circuit; if the hash is marked persistent, skip the
  cap. Add `MarkPersistent(hash, priority)` / `UnmarkPersistent(hash)` API.
- `internal/warmup/warmup.go::enforceQuotaLocked` — extend LRU eviction to
  prefer non-persistent entries; persistent entries only get evicted when
  quota is exhausted AND no non-persistent entries remain, then in
  ascending priority order.
- `main.go` (FUSE open handler) — when opening a stub, read the JSON,
  detect `persist=true`, call `warmup.MarkPersistent(hash, priority)`.
  Inverse on close not required (state survives restart in stub file).
- `internal/config/config.go` — wire the existing-but-unused
  `WarmupHeadSizeMB` to `warmup.FileSize` at init while we're in the area
  (separate concern, but trivially in scope of this PR — call it a drive-by
  fix and mention in the PR description).
- New endpoint `POST /api/library/prestage` on `:9080` taking
  `{stub_path, priority?}` — triggers a background read of the entire FUSE
  file (effectively the in-process equivalent of
  `dd if=<fuse_path> of=/dev/null`), with throttling so it does not starve
  live playback. Returns immediately; progress queryable via
  `GET /api/library/prestage/status?stub_path=...`.

#### Why a separate PR from the API patch

The `/api/library/add` patch is mechanical refactoring + one new endpoint;
the persist patch touches the hot read path in `warmup.go` and the eviction
logic. Different review surface, different risk profile, different upstream
reviewers will care. Keeping them split:

1. Maximises the chance the simpler API patch lands quickly so Phantom
   Library has a clean integration point.
2. Lets the persist patch be debated on its own merits (quota semantics,
   eviction policy, performance impact on the warmup write channel) without
   blocking the API.
3. Phantom Library can ship a usable v0.1 against just the API patch; the
   persist patch becomes a v0.2 capability unlock.

#### How Phantom Library uses it

The `Materialiser` writes stubs with `persist=false` by default. The
`UserDataSavedListener` is extended:

- On favourite → true: if the item is Materialised, rewrite its stub with
  `persist=true` and call `POST /api/library/prestage` so the file fully
  caches in the background. The user's favourite is now "protected" against
  swarm rot.
- On favourite → false: rewrite stub with `persist=false`. Cached bytes
  become eligible for normal LRU eviction; user data preserved.
- Eviction sweeper (Q5): when demoting a Materialised item back to Virtual
  for being stale, first ensures it is unmarked persistent so its SSD
  footprint releases immediately.

This turns Jellyfin's ❤️ into a meaningful guarantee: favourited titles play
instantly forever, regardless of peer availability, until you unfavourite
them or the cache fills.

Milestone: slots after M6 (gostream `/api/library/add` PR) as **M6.5 —
gostream Vault Mode PR** (~3–4 days), with Phantom Library wiring in **M7**
alongside eviction.

#### Open questions specific to this patch

- Does `RemoveCacheOnDrop=false` on GoStorm's existing piece cache already
  cover enough of the use case that Vault Mode is redundant? Needs a
  before/after experiment on a low-seeded title with the network briefly
  disconnected during replay. If GoStorm pieces survive deactivation and
  serve replays from SSD without involving `warmup.go` at all, the patch
  collapses to "just expose a per-stub flag that forces a full pre-stream"
  and the persist quota becomes a GoStorm concern, not a `warmup` concern.
- Two coexisting on-disk caches (GoStorm pieces under `TorrentsSavePath` and
  warmup head/tail files under the same path) makes accounting messy. The
  patch needs to either unify them or clearly separate their quotas to avoid
  one starving the other.

---

## Milestones

### M1 — gostream patch (`/api/library/add`) (≤ 3 days)

Moved to the front: with the API patch in place, Phantom Library never
touches gostream's stub format, filename conventions, or the FUSE
propagation contract. Everything below this milestone speaks to gostream
exclusively through the new endpoint. Doing the patch first means no
throwaway code on the plugin side that targets the raw-stub path.

- Extract `createMKV`, the filename builder, and quality scorer into
  reusable functions in gostream.
- Add `POST /api/library/add` on `:9080` with the blocking-on-metadata
  semantics described in the gostream-patch section above (bounded
  internal timeout, no client-facing flag).
- Acceptance test (this replaces the old M0): from a script outside
  gostream, call the endpoint for a real TMDB title; receive a `fuse_path`;
  point Jellyfin at the containing directory; confirm Jellyfin scans the
  new file without restart, metadata populates, and playback works.
- Open PR upstream. Plugin work assumes this lands; until it does, develop
  against a local fork.

### M2 — Plugin skeleton (≤ 3 days)

- Initialise from
  [jellyfin-plugin-template](https://github.com/jellyfin/jellyfin-plugin-template).
- `Plugin.cs`, GUID, build / package pipeline (GitHub Actions producing
  `phantom-library_<version>.zip`).
- `PluginConfiguration` + admin page with stub fields.
- Side-loaded into a local Jellyfin to confirm load / config persistence.
- No functional behaviour yet.

### M3 — TMDB remote search and image provider (≤ 3 days)

- `TmdbMovieRemoteSearchProvider` returns TMDB hits inside Jellyfin's native
  "Identify" / search UI.
- Selecting a hit creates a Virtual item via `VirtualItemFactory`.
- `PhantomImageProvider` supplies posters / backdrops.
- Outcome: typing "Interstellar" in Jellyfin search returns a TMDB hit;
  selecting it surfaces a Virtual library item with full metadata but no
  play button.

### M4 — Materialisation pipeline against patched gostream (≤ 3 days)

- `GostreamClient` calls `/api/library/add` and receives the FUSE path,
  then polls `File.Exists(fuse_path)` (cap 5s) before refresh. No raw
  stub writes, no four-step orchestration.
- `Materialiser` end-to-end: cache lookup → Prowlarr (fallback Torrentio)
  → gostream API → library refresh scoped to the returned directory →
  Jellyfin item promotion.
- `MaterialisationQueue` with configurable concurrency caps and separate
  lanes for user-triggered vs. eager pre-resolve work.
- `EagerResolver` background service: subscribes to suggestions / similar
  / search-result surfacing events; pre-resolves magnets for Phantoms so
  the materialiser hits a warm cache.
- `UserDataSavedListener` triggers on favourite-to-true.
- Manual REST endpoint to force-materialise an item for testing.
- Outcome: ❤️ on a Virtual movie produces a playable Materialised item in
  under 30 s for a well-seeded magnet with pre-resolved cache, under 60 s
  cold.

### M5 — Fake play button and splash hand-off (≤ 4 days)

- `PhantomMediaSourceProvider` returns a splash `MediaSource` for Phantom
  and Virtual items: a pre-baked short looping MP4 served by a plugin
  HTTP handler, branded with the phantom-jelly logo and generic
  "materialising…" copy baked into the pixels. Splash is opaque to
  clients (no per-frame JS overlay possible — Jellyfin clients are
  video players, not browsers).
- Per-item status ("Resolving magnet…", "Downloading metadata…",
  "Ready — press play") is surfaced via Jellyfin's native item fields
  (overview text, name suffix, tags) that clients render in their
  own UI alongside the splash playback.
- Pressing ▶️ on an un-materialised item plays the splash and enqueues
  materialisation at user-trigger priority.
- When materialisation finishes, the item's `Path` is populated and
  Jellyfin's normal `IMediaSourceProvider` chain returns the real FUSE
  file on the next playback. The user backs out of the splash, presses
  play again, and gets the real stream — no Jellyfin client-side timeout
  risk, no fake error states.
- Investigate whether the splash itself can detect materialisation
  completion and prompt "Ready — play now?" inline, vs. requiring a
  back-and-press. Cross-client behaviour will dictate.
- Outcome: pressing ▶️ on any Phantom or Virtual item always does
  *something*, never errors, and seamlessly hands off to real playback
  when ready.

### M6 — Suggestions integration (≤ 3 days)

- `SuggestionsContributor` injects Virtual items from TMDB Trending /
  Similar / Recommended into Jellyfin's home screen rows.
- Cache TMDB responses aggressively to stay well under rate limits.
- Outcome: housemates see fresh Phantom content on the Jellyfin home page
  without searching.

### M6.5 — gostream Vault Mode PR (≤ 3–4 days)

Second gostream PR, independent of M1. Implements the persist patch
described under "Quaternary patch" above. Slotted here because Phantom
Library's eviction sweeper (M7) needs the persist semantics to be
meaningful, but the API patch (M1) does not depend on it.

### M7 — Eviction sweeper and favourite-driven persistence (≤ 2 days, optional)

- Per Q5: implement chosen eviction policy.
- Use the eviction endpoint from the tertiary gostream patch if it landed;
  otherwise call `/api/library/add` with a remove action equivalent.
- Wire the favourite ↔ `persist` flag flow described in the Vault Mode
  patch section, gated on Vault Mode being present.
- Outcome: long-term operation does not unbound gostream's state, and
  favourited titles are protected from swarm rot.

### M8 — TV series and autopilot (≤ 1–2 weeks, v0.1 MVP requirement)

- `TmdbSeriesRemoteSearchProvider`, episode-level Virtual items, season /
  episode handling routed through the gostream API.
- `SeriesAutopilot`: on playback-finished for an episode, pre-materialise
  next episode(s) per the configured prefetch window. On favourite of a
  movie that has a direct sequel, enqueue the sequel as Virtual and
  eager-pre-resolve its magnet.
- "Next Up" integration so the autopilot's pre-materialised next episode
  appears in Jellyfin's native Next Up row across all clients.
- Note: this is gated as MVP; v0.1 ships with movies + series + autopilot.
  A movies-only PoC may precede it during development but does not
  constitute v0.1.

### M9 — Packaging and release (≤ 2 days)

- `manifest.json` repo published via GitHub Pages.
- README, install instructions, troubleshooting, mascot artwork.
- Initial release tagged `v0.1.0`.

---

## Risks and unknowns

- **Jellyfin-over-FUSE contract has been validated in production usage**
  prior to plan adoption — the operator runs Jellyfin against gostream's
  FUSE mount today, with playback, scanning, and metadata working. No
  separate validation milestone needed; the API patch's acceptance test
  exercises the same path with a runtime-created file.
- **Jellyfin Virtual item playability.** The plan assumes a Virtual item
  can be promoted to Materialised by setting `Path` and updating
  `LocationType`. This needs verification in M2 / M3; if Jellyfin requires
  the item to be discovered via a scan rather than mutated in place, the
  plugin will need to delete the Virtual item and create a fresh
  FileSystem-backed one with the user data copied across.
- **`IMediaSourceProvider` deadline.** Web and mobile clients tolerate ~30 s
  before timing out; some TV apps may tolerate less. Real numbers come from
  M4 testing.
- **gostream stub-detection latency.** Whether gostream notices a new stub
  immediately or only on its own polling interval directly affects play-press
  latency. Worst case: the plugin needs the gostream patch (M6) to be
  practically usable on TV clients.
- **TMDB rate-limit headroom in Suggestions.** Caching is straightforward
  but the plugin must avoid cold-start storms when Jellyfin restarts.
- **Indexer rate limits dominating perceived latency.** Pre-warming magnets
  on favourite (background) is the main mitigation; if even that fails,
  consider a "favourite-to-pre-resolve" pass at idle times.
- **Plugin versus gostream sync engine collisions.** Both can independently
  decide to add the same TMDB title. Need idempotent stub creation (skip if
  a matching stub already exists; upgrade if the new candidate scores
  higher per Q1).

## Deferred features (post-v0.1)

- **Dynamic splash overlay.** Per-item status text or rotating trivia
  burnt into the splash video stream. Requires either a per-request
  ffmpeg transcoder or a client-side overlay extension point that
  Jellyfin does not currently expose. v0.1 ships static pixels +
  native-field status copy.
- **Manual torrent picker.** A UI for a user to browse candidate torrents
  returned by Prowlarr / Torrentio for a Virtual item and pick one
  explicitly, instead of relying on `QualityScorer`. Useful for choosing
  a specific release group, audio track, smaller / larger size, or
  manually-curated rip. Requires either a plugin web tab (limited to
  Jellyfin web client) or a server-side admin REST endpoint that
  housemates hit out-of-band. Designs to consider when prioritising.
- **Submission to the official Jellyfin plugin catalogue.** Self-hosted
  manifest is the v0.1 install path; revisit catalogue submission once
  the plugin is stable and the gostream PRs have landed.
- **Multi-user request quotas / approval flow.** If housemates start
  burning indexer quota or filling SSD via runaway favouriting, a quota /
  approval system may be worth adding. Out of scope until observed.
- **Cross-server federation.** See Out of scope.

## Out of scope (forever, not just v0.1)

- Hosting a custom streaming protocol. Jellyfin's existing playback path
  over the FUSE-backed `.mkv` is the contract.
- Replacing gostream's torrent engine. The plugin is a client of gostream;
  it never speaks BitTorrent directly.
- Cross-server federation. Single Jellyfin + single gostream is the target
  topology.
