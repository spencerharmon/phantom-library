# Phantom Library — Implementation Plan

A Jellyfin plugin that makes the entire TMDB catalogue appear to exist inside a
Jellyfin library. Titles materialise on demand: a user plays an available
item or triggers a manual materialise action, and the plugin asks [gostream](https://github.com/MrRobotoGit/gostream) to
register the matching torrent and writes a virtual `.mkv` stub into gostream's
physical source directory. gostream's FUSE layer turns that stub into a
seekable file that Jellyfin can stream from. Phantom Library does not write
raw gostream stubs itself; gostream owns stub/FUSE creation via its API.

Mascot: *Stygiomedusa gigantea*, the giant phantom jelly.

---

> **⚠ DEPRECATED naming scheme.** Historical sections of this
> document (especially §§ M10, M11, M13, and the linked
> `docs/plans/M12-*.md` investigations) describe the legacy
> `__phantom_tmdb<id>` filename sentinel. That scheme is
> **deprecated** and retained only for back-compat parsing in the
> one-shot migration script. The canonical on-disk layout is
> Jellyfin-native `[tmdbid-<id>]` per the spike milestone below.
> See `AGENTS.md` § "Canonical phantom stub naming scheme" for
> the hard rule. Do not propagate `__phantom_tmdb` into new code,
> tests, or design docs.

---

## Status (as of 2026-06-19)

| Milestone | Status | Commit |
|---|---|---|
| M1 — gostream `POST /api/library/add`        | ✅ | `e4df693` on gostream `phantom-library/api-add` |
| M2 — Plugin skeleton + packaging             | ✅ | `bbe5dbf` |
| M3 — TMDB + remote search + image provider   | ✅ | `e837cd1` |
| M4 — Materialisation pipeline                | ✅ | `2451075` |
| M5 — Fake play button + splash hand-off      | ✅ | `8ca2f8b` |
| M6 — Suggestions integration                 | ✅ | `abe0fd1` |
| M6.5 — gostream Vault Mode                   | ✅ | `109c856` on gostream `phantom-library/vault-mode` |
| M7 — Eviction + favourite-driven persistence | ✅ | `3377add` |
| M8 — TV series + autopilot                   | ✅ | `60de538` |
| M9 — Packaging + release polish              | ✅ | M9 release commit (this change) |
| M10 — Phantom symlink library + visibility fix | ✅ | (unreleased, multiple commits) |
| M11 — Post-M10 phantom UX polish               | ✅ | (unreleased, multiple commits) |
| M12 — Dedupe-gap heal-on-rediscovery           | ✅ | (unreleased) |
| M13 — Per-series subdir stub layout for TV phantoms | ✅ | (unreleased) |
| Spike — Jellyfin-native `[tmdbid-<id>]` stub layout | ✅ | merged into main as `a931379` (file-on-disk architecture; deployed to operator v0.2.0.0; **slated for replacement by M14**) |
| M14 — IChannel migration + Jellyfin patch | 🚧 IN FLIGHT on main | Channel architecture implemented behind schema v11; remaining work is hardening, operator validation, and cleanup of stale design docs. |

### M14 operator requirements ledger

This ledger is the scope authority for M14 completion. Critic review may
identify missing code, but must not convert requirements to deferred work.
Only the operator may change `Disposition` from `IMPLEMENT` to `DEFER` or
`DROP`. Any PLAN text that conflicts with this ledger is stale and must be
fixed before handoff.

| ID | Requirement | Disposition | Acceptance evidence required |
|---|---|---|---|
| REQ-M14-SOURCE-API | Source-management backend APIs: list current source/candidates, reject current source, materialise selected candidate. | IMPLEMENT | API tests + file:line citations for `GET .../Sources`, `POST .../RejectCurrent`, `POST .../MaterialiseCandidate`. |
| REQ-M14-SOURCE-UI | Source-management web UI: details-panel "Phantom Source" section, candidate dropdown, and "Reject current source" action. | IMPLEMENT | UI/JS tests or DOM evidence showing controls for Phantom items and absence/disabled state for non-Phantom items. |
| REQ-M14-SOURCE-SAFETY | Rejecting a source skips that candidate, tries next ranked candidate, refreshes item state, and never removes a gostream hash still referenced by another item. | IMPLEMENT | Unit/API tests + rig proof for reject → next source → playback. |
| REQ-M14-MOBILE | Source-management UX is usable in mobile browser; native mobile limitations must have diagnostics/channel fallback or explicit operator-approved limitation. | IMPLEMENT | Mobile-browser DOM/API evidence or documented fallback with test coverage. |
| REQ-M14-FAV-MATERIALISE | Favourite-triggered materialisation/prewarming behavior must be implemented or explicitly re-approved by operator as not desired after channel refactor. | IMPLEMENT | UserData/favourite tests showing materialise/prewarm trigger, or operator-approved disposition change. |
| REQ-M14-PER-USER | Per-user preferences/favourite eviction protection/show-hide/source-probing controls must be implemented or re-evaluated with operator after channel refactor. | IMPLEMENT | API/UI/tests for per-user behavior, or operator-approved disposition change. |
| REQ-M14-RECOMMENDATIONS | Favourite-similar/recommendation ingestion must be re-evaluated after channel refactor; do not silently drop it. | EVALUATE | Written evaluation against current channel surfaces + operator disposition. |
| REQ-M14-RETENTION | Phantom/catalogue retention must be re-evaluated after schema v11 append-only catalogue design; config must not imply active pruning unless implemented. | EVALUATE | Written evaluation + either implementation/tests or operator-approved no-op/defer. |
| REQ-M14-VAULT | Vault Mode/prestage/favourite-driven persistence must be re-evaluated against current gostream and eviction model. | EVALUATE | Written evaluation + implementation/tests or operator-approved disposition. |
| REQ-M14-CONCURRENCY | Per-indexer concurrency cap must be implemented or removed/renamed so config does not overpromise. | EVALUATE | Concurrency tests or config/UI cleanup with operator-approved disposition. |
| REQ-M14-SEARCH-GATING | Native remote-search availability gating must be evaluated against channel-only availability gating. | EVALUATE | Written evaluation + operator disposition. |
| REQ-M14-SPLASH | Splash/fake-button/dynamic overlay remnants must be evaluated after native-open refactor and either removed, repurposed, or operator-approved as historical. | EVALUATE | Written evaluation + code/UI cleanup if still exposed. |

Evidence audit for the already-IMPLEMENT rows above (REQ-M14-SOURCE-API, REQ-M14-SOURCE-UI,
REQ-M14-FAV-MATERIALISE) with cited `file:line` tests/code lives in
`docs/plans/m14-ledger-evidence-audit.md`; see it before treating any of those three rows as done.

### M14 source-management implementation contract

Source-management work must use the current channel architecture and stable
`BaseItem.ExternalId` values (`movie_<tmdbId>` and
`episode_<seriesTmdbId>_sXXeYY`). Do not key operator actions on file paths,
Jellyfin item GUIDs, or current materialised state.

#### Backend API

Add API routes under the existing Phantom Library plugin API surface:

- `GET /Plugins/PhantomLibrary/Items/{externalId}/Sources`
  - Auth: same authenticated-user/admin policy as other Phantom Library item
    actions; return 404 for non-Phantom or unparseable `externalId`.
  - Response includes:
    - `externalId`, parsed type/TMDB/season/episode, current status
      (`unmaterialised`, `materialised`, `materialising`, `unavailable`).
    - `currentSource` when materialised: `magnet`, `infoHash`, `indexer`,
      `seeders`, `size`, `stubPath`, `fusePath`, `materialisedAt`. If old
      rows lack magnet metadata, return real stored fields and explicit nulls;
      do not invent values.
    - `candidates`: fresh ranked candidates from `MagnetSelector` plus cached
      winner where applicable, each with `magnet`, `infoHash`, `indexer`,
      `title`, `seeders`, `size`, `rank`, `isCurrent`, `isRejected`,
      `failureReason`, `retryAfter`.
    - `canRejectCurrent`, `canMaterialiseSelected`, and human-readable
      `message` for disabled actions.
- `POST /Plugins/PhantomLibrary/Items/{externalId}/Sources/RejectCurrent`
  - Requires current `materialised_state`; 409 if already in flight.
  - Records current source as rejected in `magnet_failure_cache` with reason
    `operator_rejected` and long retry window unless operator later chooses
    a shorter policy.
  - Removes only target `materialised_state`. Call gostream remove only when
    no other materialised row references the same `stub_path`/`infoHash`;
    shared hashes must not be removed.
  - Immediately attempts materialisation with next ranked non-rejected
    candidate. If none exists, leave item unmaterialised and return clear
    `no_alternate_source` state; do not silently reselect rejected source.
  - Refresh exact channel item after state changes.
- `POST /Plugins/PhantomLibrary/Items/{externalId}/Sources/MaterialiseCandidate`
  - Body requires exact `magnet` and may include `infoHash`, `indexer`,
    `title`, `size`, `seeders` from the candidate list.
  - Uses same atomic in-flight claim as normal materialisation.
  - Attempts that exact candidate first; if it is operator-selected, bypass
    stale ranking but not existing hard rejection unless request explicitly
    includes `overrideRejected=true`.
  - On success, writes real `materialised_state`, refreshes exact channel
    item, and returns final source details.

Persistence default: avoid schema bump if honest implementation is possible.
Use existing `materialised_state`, `magnet_cache`, and
`magnet_failure_cache`. If exact selected-candidate history cannot be
implemented honestly with those tables, propose schema v12 and the required
wipe/changelog/handoff before editing schema.

#### Web UI

Implement web controls in embedded Phantom Library web assets:

- Extend or replace `Configuration/phantomKebab.js` so item action sheets on
  Phantom movie/episode details show:
  - `Materialise (Phantom Library)` for unmaterialised materialisable items.
  - `Reject current source (Phantom Library)` for materialised Phantom items.
  - No source actions for non-Phantom items, series folders, or season
    folders.
- Add a details-panel injection (same JS file or a new embedded resource)
  that calls `GET .../Sources` for the current details item and renders a
  `Phantom Source` section with:
  - current source summary and rejection status,
  - candidate dropdown populated from API candidates,
  - `Materialise selected source` button,
  - `Reject current source` button when allowed,
  - loading, success, and error states that expose API messages.
- Mobile browser must use the same DOM flow with touch-sized controls. If
  native Jellyfin mobile apps cannot execute custom JS, implement a server-
  rendered diagnostics/channel fallback or bring the limitation back to the
  operator for disposition before claiming done.

#### Tests / rig evidence

Required coverage:

- API tests for all three routes, including non-Phantom 404, no-current-source,
  already-in-flight, no-alternate-source, and selected-candidate success.
- Safety tests proving RejectCurrent skips rejected candidate and does not call
  gostream remove when another materialised row references the same source.
- UI/markup/JS tests or DOM evidence proving `Phantom Source`, candidate
  dropdown, and reject action appear only where valid.
- Live rig scenario proving reject current source → next ranked source →
  playback through real Jellyfin channel/native-open flow.

### M14 remaining-deferral evaluation plan

Rows marked `EVALUATE` in the ledger are not approved deferrals. For each row,
inspect current channel-refactor code, write a short finding in PLAN.md or a
companion plan doc, and either implement the still-relevant behavior or obtain
operator disposition. Evaluation must cover user-visible behavior, current code
entry points, risks, and exact tests needed if implemented.

Current written evaluation lives in `docs/plans/m14-ledger-evaluation.md`.

Unavailable-title badge UX is no longer in this ledger because the operator
reported it was implemented in another session; future agents should verify it
by tests, not re-defer it.

### Documented partials

- **Custom `QualityPreset` falls back to `GostreamDefault`** with a
  warning log (M4 decision). Revisit when a real custom-scoring use
  case appears.
- **Per-user preferences are tracked by REQ-M14-PER-USER.** Earlier admin
  sub-page wiring was removed from the active API surface; this is not an
  approved deferral until the operator accepts the post-channel-refactor
  disposition.
- **Series-level `Materialise` returns `Error`** (M8). Correct
  behaviour: a Series is a container, not a streamable file.
  Materialise individual Episodes (the autopilot does this for the
  next unwatched episode automatically).
- **Splash overlay is static pixels** (M5, historical). M14 native-open
  playback supersedes splash-as-playback UX; splash assets remain only as
  legacy/support assets.
- **M10 binder vs. Jellyfin metadata-saver race (pre-M14 historical).** The CollectionFolder
  binder's `UpdateItemAsync` can race with Jellyfin's
  `FolderMetadataService.RunMetadataSavers` pipeline (in particular
  the `BaseDynamicImageProvider` that fires during folder metadata
  refresh), and the latter can save a stale snapshot AFTER ours
  that reverts `PhysicalLocationsList` / `PhysicalFolderIds` back
  to the pre-bind state. **Pre-M14 mitigation (removed by the M14 IChannel
  architecture):** the binder verifies persistence via
  `ILibraryManager.RetrieveItem` (the repository read, not the
  in-memory cache) and re-applies the patch up to 30 times across
  ~30 s; if persistence is still lost, an `ItemUpdated` event
  watchdog re-patches whenever a future save drops the phantom
  path; and `PhantomBootstrapService` re-runs `BindAsync` every 5
  minutes as belt-and-braces. End-to-end verification in the rig:
  both `gostream-movies` (was the failing case) and
  `gostream-shows` persist the binding across multiple
  metadata-saver cycles, and browse returns phantom items in both
  libraries. The underlying race is the same upstream bug
  documented under [§ Jellyfin upstream
  issue](#jellyfin-upstream-issue-deferred); the upstream patch
  (PR sketch in that section) removes the race because the
  controller itself runs `RefreshMetadata` synchronously after
  `AddMediaPath` instead of leaving the metadata-saver pipeline to
  finish asynchronously.

### Excluded from v0.1

See [§ Deferred features (post-v0.1)](#deferred-features-post-v01)
and [§ Out of scope (forever, not just v0.1)](#out-of-scope-forever-not-just-v01).

---

## Resolved Design Decisions

Previous open questions have been answered. Recorded here so future
contributors can see the rationale.

0. **Target Jellyfin version.** 10.11.x (initially planned for
   10.10.x; bumped during M4 verification when the operator's running
   instance turned out to be 10.11.9, which moved the plugin ABI to
   net9.0). Plugin compiles against the 10.11 plugin ABI; older
   servers are not supported.
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

1. **Quality selection.** Configurable in plugin settings. Current M14
   default is `ResolutionSeeders` (preferred 1080p/fallback order plus seeder
   weight), with `GostreamDefault` and `BiggestMostSeeded` presets available.
   Per-user/per-library overrides remain deferred.
2. **Indexer source.** Prowlarr and Torrentio are both registered indexer
   sources. Current M14 probes every enabled source, aggregates candidates,
   and ranks them together; Torrentio is not merely a fallback once Prowlarr
   returns results.
3. **TV series scope.** Series support is an MVP requirement. Current M14
   channel flow supports series → season → episode browse, episode
   materialise-on-play, and next-episode autopilot after completed playback.
   Movie sequel autopilot remains deferred; favourite-triggered materialisation/
   prewarming is implemented (see REQ-M14-FAV-MATERIALISE, evidence cited in
   `docs/plans/m14-ledger-evidence-audit.md`).
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
   without playback. Configurable through the admin config page. Current M14
   favourite protection is server-wide (`ProtectFavourites`), not per-user.
   Phantom/catalogue retention is configured but not enforced in M14; catalogue
   rows are append-only until a retention sweeper is implemented.
6. **Unavailable titles.** M14 availability-gated channels keep movie browse
   uncluttered by hiding unavailable unmaterialised movies. TV uses a series-
   scoped compromise: a series appears after `SeriesMinAvailableEpisodes`
   distinct available/materialised episodes (default `1`), then all known
   episodes in that series display. Unknown episodes display as normal
   phantoms; unavailable episodes display the red `Unavailable` badge. This
   badge UX was implemented in another session and removed from the open M14
   ledger; keep tests green and do not re-defer it.
7. **Play-press UX.** ⚠ Superseded by M14 native-open playback. Earlier
   milestones used a fake splash media source. Current M14 emits Jellyfin
   native `RequiresOpening` media sources for unmaterialised channel items;
   `OpenMediaSource` materialises, waits for FUSE readiness, then returns the
   real source to the client.
8. **Gostream integration.** The plugin talks to gostream through patched
   library-control endpoints: materialisation uses `POST /api/library/add`,
   eviction uses `POST /api/library/remove`, and Vault Mode/prestage endpoints
   are optional/deferred. No raw stub writes, no dependency on the JSON stub
   format, no four-step orchestration.
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
- **Availability-gated discovery + scheduled source probing.** TMDB
  discovery now feeds an append-only local catalogue (`catalogue_items`,
  plus `series_episode_catalogue` for episodes). Catalogue membership
  alone does **not** make an unmaterialised item visible. A bounded
  background availability scheduler leases due rows from
  `availability_items`, resolves IMDb as needed, probes Prowlarr/Torrentio
  through `MagnetSelector`, stores the best ranked candidate in
  `magnet_cache`, and marks the row `available`, `unavailable`, or
  transiently deferred. The worker uses configurable catch-up/steady-state
  cadences and TTLs in plugin settings (`AvailabilityProbe*` fields);
  this replaces the old eager pre-resolve model where every discovered
  phantom immediately cached one chosen magnet. Materialised items and
  already-real gostream files stay visible regardless of availability
  state.
- **Two-layer lazy loading is the architectural identity.** Layer 1 is
  Phantom → Virtual → Materialised inside the plugin. Layer 2 is
  gostream's normal FUSE-on-demand byte serving. Documentation, UI copy,
  and the README all lean on this framing.
- **Deferred feature: manual torrent picker / source rejection.** A future
  capability that lets a user view ranked candidate torrents from multiple
  indexers for a Virtual/Materialised item, reject the current candidate,
  and optionally pick a replacement explicitly (rather than relying only on
  the quality scorer). Useful when the user wants a specific release group,
  audio track, or smaller / larger file size than the default. The current
  backend now has candidate-level failure caching (`magnet_failure_cache`)
  and ranked candidate probing, which are prerequisites for this UI, but
  no operator-facing picker/reject endpoint is part of the current slice.

---

## Goals

- A Jellyfin user opens a Phantom channel and sees TMDB-backed channel items
  that are not yet materialised but have already passed source-availability
  probing. Native remote search remains raw TMDB-backed identify/search and is
  not availability-gated in the current M14 slice.
- The user presses ▶️ on an available Phantom movie/episode, or triggers a
  manual materialise action. The plugin resolves ranked torrent candidates,
  calls gostream to register the first working source, and refreshes the exact
  channel item. Within seconds
  the item is Materialised: the stable channel item now emits a real
  FUSE-backed media source and is playable from any Jellyfin client.
- The user presses ▶️ on an available Phantom item that has not yet been
  materialised. Jellyfin's native `RequiresOpening` media-source flow keeps
  the client in its normal loading state while materialisation runs. If the
  first candidate fails, candidate-level backoff advances to the next ranked
  candidate without hiding the item.
- Phantom Movies and Phantom Shows channels expose TMDB-backed discovery
  surfaces. Channel latest rows replace pre-M14 native Movies/TV Home-row
  integration; favourite-similar/recommendation ingestion is deferred.
- Works across every Jellyfin client (web, Android, iOS, Android TV, Apple
  TV) because all of the above is server-side and uses native Jellyfin
  primitives (library items, remote search, media source provider). No
  plugin-only web UI is required for the core flow.

## Non-goals (v0.1)

- A custom web "Discover" tab. Phantom Movies/Shows channels cover discovery
  without a separate per-client tab.
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

The diagram is conceptual: under M14, Phantom Movies/Shows are `IChannel`
surfaces. The plugin enumerates gostream FUSE paths directly for orphan files
and uses per-item channel refresh after materialisation; it does not rely on
Jellyfin library scanner/CollectionFolder binding for phantom channel items.

## Item lifecycle

| State | Storage | Playable | Metadata | Notes |
|-------|---------|----------|----------|-------|
| **Catalogued** | `catalogue_items` / `series_episode_catalogue` + TMDB metadata cache | No | Cached TMDB | Discovered from TMDB but not shown as an unmaterialised channel item until availability probing marks it available. |
| **Available Phantom** | `availability_items.status='available'` plus cached candidate; channel item is synthesised on browse | Yes via native-open materialisation | Cached TMDB | Channel emits `RequiresOpening` media source. Play/manual materialise triggers materialisation; no real Jellyfin file path exists yet. |
| **Materialising** | `materialise_in_flight` row | Pending | Cached TMDB | Channel/badge state can show in-progress while gostream registration and FUSE-path wait run. |
| **Materialised** | `materialised_state` row with gostream stub/FUSE paths; channel item keeps stable external id | Yes (real FUSE media source) | Same TMDB metadata | Channel emits a concrete file media source. If the FUSE path disappears, browse/playback falls back to phantom opener and re-materialises. |
| **Unavailable** | `availability_items.status='unavailable'` for visibility; `unavailable_marker` for materialise backoff | No | Cached TMDB | Availability status hides unmaterialised browse rows. `unavailable_marker` gates materialisation attempts only and is not currently joined into browse queries. |
| **Evicted** | `materialised_state` removed after gostream remove/unprestage; catalogue/metadata may remain | No until re-materialised | Cached TMDB retained | Eviction sweeper handles idle materialised rows. Favourites can be protected by server-wide config. |

Transitions are driven by:

- **User actions** — attempting to play via native-open source, manual
  materialise actions, watching, favouriting (triggers materialisation/prewarm
  per REQ-M14-FAV-MATERIALISE), and un-favouriting/eviction interactions.
- **Discovery / availability surfaces** — Discover and trending populate
  catalogue rows; the availability worker probes sources before making
  unmaterialised phantoms visible. Similar/recommendation ingestion remains
  deferred.
- **Series autopilot** — when a user completes an episode, the next episode
  can be queued/materialised according to server-wide autopilot settings.
  Movie sequel autopilot remains deferred; favourite-triggered materialisation
  is implemented (REQ-M14-FAV-MATERIALISE).
- **gostream sync engine** — continues to pre-populate trending content
  independently of the plugin. The plugin's eviction sweeper does not
  remove items it does not own; sync-engine stubs are gostream's
  responsibility.
- **Eviction sweeper** — background `IHostedService` runs on a schedule
  (default daily), removes idle materialised state via gostream, and leaves
  catalogue/availability rows for future re-materialisation. Favourite
  protection is currently server-wide.

---

## Components — Jellyfin plugin (`Jellyfin.Plugin.PhantomLibrary`)

### Project layout

Current M14/channel-architecture source layout (abridged):

```text
phantom-library/
├── AGENTS.md / PLAN.md / CHANGELOG.md / README.md
├── install.sh
├── scripts/jellyfin-patches/        (additive Jellyfin IChannel refresh patch)
├── src/Jellyfin.Plugin.PhantomLibrary/
│   ├── Plugin.cs
│   ├── PluginServiceRegistrator.cs
│   ├── Configuration/               (server-wide config + web shims)
│   ├── Api/                         (plugin REST: state/materialise/actions)
│   ├── Channels/                    (PhantomMoviesChannel, PhantomShowsChannel,
│   │                                  native-open media source provider, IDs,
│   │                                  gostream orphan enumeration)
│   ├── Scheduled/                   (DiscoveryRefreshTask, AvailabilityProbeWorker)
│   ├── Materialisation/             (Materialiser, queue, eviction, autopilot,
│   │                                  playback/user-data listeners)
│   ├── Sources/                     (MagnetSelector ranked probing)
│   ├── Clients/                     (TMDB, gostream, Prowlarr, Torrentio)
│   ├── State/PhantomDb.cs           (schema v11, catalogue/availability/materialise state)
│   └── Playback/                    (splash metadata/legacy support helpers)
├── tests/Jellyfin.Plugin.PhantomLibrary.Tests/
└── tools/rig-scenarios/             (live Jellyfin rig scenarios)
```

### Key Jellyfin extension points

- **`IRemoteSearchProvider<TItemType, TLookupInfoType>`** — registered for
  both `Movie` and `Series` (series is MVP). Surfaces TMDB hits inside
  Jellyfin's native search UI on every client.
- **Channel/search image URLs** — channel and search DTOs set `ImageUrl`
  from cached TMDB metadata; there is no active `IRemoteImageProvider`
  implementation in the M14 channel slice.
- **`IUserDataManager.UserDataSaved`** event — current M14 listener observes
  playback completion and forwards completed episodes to autopilot. Favourite
  materialisation and per-user preference handling are deferred.
- **`IMediaSourceProvider` / native open** —
  `PhantomMaterialisingMediaSourceProvider` emits native `RequiresOpening`
  sources for unmaterialised movies/episodes. `OpenMediaSource` performs
  materialisation, waits for FUSE readiness, and returns the final real file
  source to Jellyfin clients. The old splash fake-button flow is historical.
- **`IChannel` / `IChannelItemRefreshManager`** — Phantom Movies/Shows
  are channel surfaces. Targeted refresh swaps an item from native-open
  phantom source to real FUSE source after materialisation without relying
  on filesystem phantom stubs.
- **`IHostedService`** — for materialisation queue workers, availability
  probing, series expansion, playback/user-data listeners, and eviction.

### Configuration

Server-wide settings live in the Jellyfin admin dashboard
(`configPage.html`). Historical per-user preference UI is not part of the
current M14 slice; favourite protection and availability probing are
server-wide until native per-user integration is revisited.

Server-wide:

- TMDB API key
- gostream API base URL (`:9080` — the `/api/library/add` endpoint).
  `:8090` is talked to only for diagnostics.
- Prowlarr URL + API key (primary indexer)
- Torrentio URL (fallback indexer)
- Quality preset (current default: `ResolutionSeeders` with preferred
  1080p order) plus minimum seeders, size floors, resolution fallback
  order, and scorer weights
- Eviction defaults: enabled / disabled, idle days (default 7), GC schedule
- Materialisation concurrency caps (global worker count currently enforced;
  per-indexer cap is configured but not yet enforced in M14)
- Availability probing: enabled/disabled, catch-up and steady-state tick
  intervals, per-tick batch size, available/unavailable TTLs, transient
  retry delay, and probe lease duration
- Series expansion: TTL/delay for expanding catalogued series into episode
  availability rows
- Phantom DB retention (configuration field retained but labelled deferred/no-op in the
  admin UI; catalogue is currently append-only)
- Series autopilot: enabled / disabled, prefetch window in episodes
  (default 1)
- "Phantom badge" visibility (`AlwaysShow`, `HideForNonAdmins`, `Off`) is
  enforced server-side by the badge-state API.
- Splash content fields are legacy/no-op under M14 native-open playback
  unless a future UI reuses them

Deferred per-user preferences:

- Protect favourites from eviction per user (current implementation uses
  server-wide `ProtectFavourites`).
- Show/hide Phantom items per user.
- Allow background source probing based on this user's interactions.

The legacy user-preferences page and admin link are hidden because the
corresponding API endpoints were removed; per-user controls remain deferred
until a real per-user contract is implemented.

### Materialisation flow

`Materialiser.MaterialiseAsync(tmdbId, type, season, episode, trigger)`

1. Reject unsupported container types (series/season), short-circuit if
   the tuple already has `materialised_state`, then atomically claim
   `materialise_in_flight` with `INSERT OR IGNORE`. A loser returns
   `AlreadyInProgress` and never calls gostream; only the winner owns row
   cleanup.
2. Resolve TMDB metadata and IMDb. Episode requests use the parent series
   IMDb because gostream's episode API is keyed by `series_imdb`.
3. Build a ranked candidate list. The materialiser tries a fresh cached
   `magnet_cache` entry first when it has not been candidate-failed, then
   calls `MagnetSelector.ProbeAsync` to aggregate/rank all acceptable
   Prowlarr/Torrentio candidates. Candidates present in
   `magnet_failure_cache` are skipped until their `retry_after` expires.
   Availability-probe candidate fields are an advisory winner/cache seed;
   the materialiser is the final authority and re-filters against
   candidate-level failures before calling gostream.
4. If all enabled source probes return successful empty 2xx responses,
   write/update `unavailable_marker` and return an error. Transport
   failures, timeouts, 5xx, malformed upstream responses, unavailable
   indexers, or mixed empty+transient results are indeterminate/transient
   and must not write `unavailable_marker`, `availability_items.status='unavailable'`,
   or candidate failure cache entries.
5. For each candidate in preference order, `POST /api/library/add` to
   gostream `:9080` including TMDB/IMDb, title/year, magnet, and for TV
   episodes `season`, `episode`, `series_imdb`. The call blocks for
   torrent-metadata resolution (bounded server-side, default 45s, 504 on
   exceed).
6. Gostream replies with `{stub_path, fuse_path, hash, size}`. Filename
   conventions, JSON stub layout, and physical-path placement are
   gostream's responsibility. The plugin requires `File.Exists(fuse_path)`
   before writing `materialised_state`; a missing FUSE path marks only that
   candidate failed and advances to the next candidate.
7. Candidate-specific failures (`bad_request`, `no_valid_files`,
   `target_episode_not_found`, `metadata_timeout`, `fuse_path_missing`)
   write `magnet_failure_cache` and advance. Non-504 gostream 5xx errors
   are treated as transient service failures and do not candidate-poison.
8. On first candidate success, insert `materialised_state`, cache the
   successful magnet, and refresh the exact channel item via the patched
   `IChannelItemRefreshManager`. Materialised rows are emitted with real
   FUSE media sources; unmaterialised rows use Jellyfin's native
   `RequiresOpening` media-source flow.
9. Vault Mode endpoints exist in the client interface, but favourite-driven
   prestage/persist wiring is deferred in the current M14 slice. Eviction
   favourite protection is server-wide and prevents removal; it does not yet
   force full-file gostream persistence.

### State persistence

`PhantomDb` lives at `<dataPath>/plugins/configurations/PhantomLibrary/phantom.db`. Schema
sketch:

| Table | Purpose |
|-------|---------|
| `discovery_cache` | Legacy/channel-compatibility discovery table still present in v11; no longer the long-term visibility source of truth. |
| `catalogue_items` | Append-only TMDB movie/series catalogue membership discovered from Discover/trending surfaces. Later TMDB misses do not prune rows. |
| `series_expansion_state` | Due/lease/error state for expanding a catalogued series into seasons/episodes. |
| `series_episode_catalogue` | Per-(series, season, episode) episode catalogue rows derived from TMDB season payloads. |
| `availability_items` | Probe scheduler state and visibility gate for unmaterialised movies/episodes: status, due time, leases, probe policy hash, selected candidate metadata, transient errors. |
| `materialised_state` | One row per materialised movie/episode tuple with gostream stub/FUSE paths. Materialised rows are always visible regardless of availability status. |
| `materialise_in_flight` | Short-lived idempotency/coordination row while a materialise call is running; startup sweeper removes stale rows. |
| `magnet_cache` | Cached successful candidate per (tmdb/imdb/type/season/episode/preset), including magnet, info hash, size, seeders, indexer, TTL, and source (`availability` or user-triggered materialise). |
| `magnet_failure_cache` | Candidate-level negative cache keyed by item tuple + preset + magnet. Prevents one bad season pack or bad file from blocking later candidates. |
| `unavailable_marker` | Item-level backoff when no acceptable candidate exists, distinct from candidate failures. |
| `tmdb_metadata`, `tmdb_episode_cache`, `tmdb_external_ids`, `tmdb_cache` | TMDB-derived metadata, episode display cache, IMDb lookup cache, and raw endpoint cache used by channel synthesis and source probing. |
| `plugin_meta` | Small key/value metadata (channel data versions, one-shot markers). |

Kept separate from Jellyfin's DB to avoid schema-version coupling. Pre-v1.0
plugin DB schema changes still do **not** migrate in place: v11 hard-refuses
pre-v11 `phantom.db` and requires the operator to run the wipe/rebuild
procedure before installing the new DLL.

### Availability scheduler flow (schema v11)

Discovery and source search are now decoupled:

1. `DiscoveryRefreshTask` walks TMDB Discover plus trending pages up to
   configured caps and writes catalogue rows plus warmed metadata. It does
   **not** delete catalogue rows just because a later Discover walk omits
   them. Similar/recommendation feeds are still deferred and should be wired
   into this same catalogue ingestion path when implemented.
2. Series catalogue rows enqueue `series_expansion_state`; the availability
   worker can lease one due series at a time, fetch TMDB season payloads,
   write `series_episode_catalogue`/`tmdb_episode_cache`, and enqueue episode
   `availability_items`. In the current worker tick, due availability probes
   are attempted before series expansion; large availability backlogs can
   delay expansion until later ticks unless batch/cadence settings are raised.
3. The same worker leases due `availability_items`, resolves metadata/IMDb,
   probes indexers through `MagnetSelector.ProbeAsync`, and stores status:
   `available`, `unavailable`, or transient retry. Availability rows include
   the selected candidate fields so channel browse can cheaply gate visibility
   and later materialisation can start from a cached winner.
4. Channel browse shows unmaterialised Phantom movies/episodes when their
   `availability_items.status` is `available`; TTLs schedule re-probe rather
   than being enforced directly during browse. It always shows
   `materialised_state` rows. Movie-channel browse also surfaces real orphan
   gostream movie files; TV orphan enumeration is not yet wired into Phantom
   Shows.
5. The worker is bounded by plugin settings for enablement, min/max tick
   interval, batch size, probe lease, available/unavailable TTL, and transient
   retry delay. If a future change switches this to an actual cron-expression
   format, document whether five-field or six-field/seconds cron syntax is
   accepted and update tests accordingly.

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
  "magnet": "magnet:?xt=urn:btih:...",  // required for Phantom Library's
                                         // current materialiser; gostream-side
                                         // indexer resolution remains deferred
  "min_quality": "1080p"        // accepted by request shape; currently
                                  // ignored by gostream handler
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

Runtime requirement: Phantom Library's current materialiser requires the
patched gostream `/api/library/add` endpoint and always sends a selected
magnet. There is no raw-stub-write fallback against unpatched gostream in
M14. If eviction is enabled (default), the patched gostream must also expose
`/api/library/remove`. The current client treats a 404 from remove as
"already gone" and proceeds with plugin-state deletion, so unpatched/remove-
absent gostream is unsafe with eviction enabled.

### Secondary patch (optional, larger): Jellyfin watchlist source

A separate, smaller change replacing the hardcoded Plex watchlist source in
`internal/syncer/engines/watchlist_go.go` with a pluggable interface, plus a
Jellyfin Favourites adapter. Useful for users who do not want to run the
Phantom Library plugin but do run Jellyfin. Tracked as an independent PR.

### Tertiary patch (required when eviction is enabled): eviction API

`POST /api/library/remove` with `{stub_path}` — removes the torrent from
GoStorm, deletes the stub, and updates the inode map. Phantom Library's
eviction sweeper calls this; operators must disable eviction if deploying a
gostream build without this endpoint. Current client semantics swallow 404
as already-removed, so endpoint absence must not be represented as a generic
404 in production deployments.

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

### M10 — Phantom symlink library + visibility fix (≤ 3 days)

> **⚠ DEPRECATED naming scheme described below.** The
> `__phantom_tmdb<id>` filename sentinel introduced in this
> milestone has been replaced by Jellyfin-native `[tmdbid-<id>]`
> path tokens. The scanner-derived-name problem that motivated
> the `IsLocked = true` + re-stamp dance only existed because of
> the legacy scheme. See AGENTS.md § "Canonical phantom stub
> naming scheme" and the spike milestone at the bottom of this
> file. Content below is kept for historical context only.

v0.1 shipped with phantom / virtual items that have `Path = null`
and `IsVirtualItem = true`. **Jellyfin's library scanner culls
such items from user-facing browse**, so the Suggestions feature
created `BaseItem` rows that never appeared in the UI. The plan
below restores visibility without changing the architectural
contract with gostream.

Full diagnostic transcript is preserved in the testing session
logs; the short version of what we proved in the rig:

1. `Path = null` (Virtual) → row exists, browse omits it.
2. `Path = <file that doesn't exist>` → scanner culls the row on
   next pass.
3. `Path = <real shared file>` (N items, same Path) → scanner
   keeps **one** winner, culls the rest. Jellyfin's library
   scanner reconciles `<root>/<file>` ↔ `BaseItems.Path` 1:1.
4. `Path = <unique-per-item file>` → row appears in browse,
   survives rescans, plays via existing `PhantomMediaSourceProvider`.
5. gostream's FUSE mount (`/var/gostream/gostream-mkv-virtual/...`)
   is **read-only** to non-root processes (verified `EROFS` on
   `touch` as `spencer`; the plugin running as `jellyfin` has the
   same problem). The plugin cannot drop stubs into the FUSE dir.
6. gostream's underlying source dir
   (`/var/gostream/gostream-mkv-real/movies/`) is owned by `root`.
   Not writable by the `jellyfin` user. Same outcome.
7. **Symlinks satisfy the scanner** when each symlink path is
   unique, even if every symlink points at the same inode.
   Verified end-to-end: 5 symlinks → 5 BaseItem rows → all 5
   visible in browse → materialise of one item leaves the other 4
   intact.

Design choice: **one plugin-owned writable directory of
unique-named symlinks, all pointing at one shared splash file**.
Filenames carry a `__phantom_tmdb<id>` sentinel so cleanup,
gostream-collision-prevention, and human inspection are
first-class operations.

The operator additionally requires that phantoms appear **inside
the existing `gostream-movies` / `gostream-shows` libraries**
(not as separate `Phantom Movies` siblings). Side-by-side
browsing of phantom and materialised items is part of the v0.1
UX. This forces multi-path support on the existing
`CollectionFolder`, which exposes a Jellyfin bug — see
[§ Jellyfin upstream issue](#jellyfin-upstream-issue-deferred)
below.

#### Filesystem layout

Plugin-owned writable root, configurable via
`PluginConfiguration.PhantomStubRoot`, default
`/var/lib/jellyfin/phantom-library/`:

```
/var/lib/jellyfin/phantom-library/
├── movies/
│   ├── Backrooms__phantom_tmdb1100782.mp4        -> <splash>
│   ├── Toy_Story_5__phantom_tmdb748783.mp4       -> <splash>
│   └── ...
└── shows/
    ├── Severance__phantom_tmdb95396.mp4          -> <splash>
    └── ...
```

Each symlink targets the same `splash.mp4` extracted by
`SplashStream.GetLocalPathAsync` into
`<cache>/PhantomLibrary/splash.mp4`. Disk cost is one splash
file (~100 KB today; can be replaced with a 1-frame MKV stub of
~10 KB without UX change because `PhantomMediaSourceProvider`
overrides playback at the MediaSource layer).

The phantom directory is registered as an **additional path on
the existing `gostream-movies` / `gostream-shows`
CollectionFolders**, not as a separate library. Single browse
view shows phantoms intermixed with materialised items.

**Operator install step** (one-time, documented in README):

```bash
sudo mkdir -p /var/lib/jellyfin/phantom-library/{movies,shows}
sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/phantom-library
```

Plugin verifies writability on startup; refuses to bootstrap
phantoms if missing/unwritable, with a clear error pointing at
these exact paths.

#### Filename scheme

```
<safe_title>__phantom_tmdb<id>.<ext>
```

- `safe_title` = title with `[^A-Za-z0-9_]` → `_`, collapsed
  runs, trimmed.
- `__phantom_tmdb<id>` is the sentinel. Double underscore is
  deliberate — gostream's slugger uses single underscores, and
  the literal `phantom_tmdb<digits>` triple-marker is impossible
  to collide with a real release name.
- `<ext>` matches the splash file's extension (`.mp4` today).
  Must be a Jellyfin-recognised video extension or the scanner
  ignores the file.
- TV series use the same scheme at the Series level; episodes
  inherit the series directory and are episode-level symlinks
  with `__phantom_tmdb<series_id>_s<NN>e<NN>` naming.

Cleanup is a one-liner: `find /var/lib/jellyfin/phantom-library
-name '*__phantom_tmdb*' -delete`.

#### Plugin components (new and changed)

- **New: `Library/PhantomStubManager.cs`** — owns the
  filesystem side.
  - `Task<string> CreateAsync(BaseItem item, MediaKind kind, CancellationToken ct)`
    derives the filename, creates the symlink via
    `File.CreateSymbolicLink`, returns the absolute path.
    Idempotent: if symlink exists pointing at the splash, returns
    its path without creating a new one.
  - `Task DeleteAsync(string path, CancellationToken ct)` —
    swallows `FileNotFound`, throws on other I/O.
  - `Task BootstrapAsync(CancellationToken ct)` — verifies the
    root + per-kind subdirs exist and are writable, ensures the
    splash file is extracted, returns when ready or throws with a
    clear actionable message if the operator forgot the
    `mkdir`/`chown`.
  - `string DeriveFilename(string title, int tmdbId, MediaKind kind)` —
    deterministic, pure, unit-testable. Same inputs always
    produce the same name so re-runs of Suggestions don't
    duplicate symlinks.

- **New: `Library/PhantomCollectionFolderBinder.cs`** — owns the
  CollectionFolder side. Runs once at plugin startup (and again
  on config change):
  1. Look up the operator-configured target libraries
     (`PluginConfiguration.PhantomMoviesLibraryName` /
     `PhantomShowsLibraryName`, default `gostream-movies` /
     `gostream-shows`).
  2. For each, call `ILibraryManager.AddMediaPath(libraryName,
     new MediaPathInfo(phantomDir))` if our phantom dir is not
     already in the library's `PathInfos`. Wrap in `try/catch`;
     duplicate-add is a no-op.
  3. Trigger a top-level validation
     (`((LibraryManager)libraryManager).ValidateTopLibraryFolders(ct, removeRoot: false)`)
     so the new physical folder gets a `BaseItems` row.
  4. Apply the **upstream-bug workaround** documented below.
  5. Log the resulting `cf.PhysicalLocationsList` and
     `cf.PhysicalFolderIds` for operator visibility.

  The binder is idempotent. On every plugin startup it checks the
  current binding and only acts if our phantom dir is missing
  from `PhysicalLocationsList`. Once the upstream Jellyfin fix
  lands (see deferral note), this binder becomes a near-no-op:
  `AddMediaPath` will set the fields correctly and our check will
  match on the first restart.

- **Changed: `Library/SuggestionsContributor.cs`** — after
  `VirtualItemFactory.CreateVirtualMovie`/`Series`:
  ```csharp
  var stubPath = await _stubs.CreateAsync(item, MediaKind.Movie, ct);
  item.Path = stubPath;
  item.IsVirtualItem = true;          // visual badge only; scanner ignores when locked
  item.IsLocked = true;               // ← REQUIRED: prevents scanner-driven rename/refetch
  parent.AddChild(item);
  ```
  `IsLocked = true` is mandatory. Without it the scanner reads
  the filename stem, fuzzy-matches against TMDB, and renames the
  item — verified in the rig: `Backrooms__phantom_tmdb1100782`
  got renamed to `Backrooms.Enderman`. Locked items skip the
  metadata-provider pipeline.

- **Changed: `Library/SeriesIngestor.cs`** — same pattern for
  Series-level rows. Episode-level rows are emitted lazily by the
  autopilot on materialise, so episodes don't get phantom
  symlinks; only series do.

- **Changed: `Materialisation/Materialiser.cs`**
  (`PromoteItemAsync`) — after the existing `item.Path = fusePath`
  + `UpdateItemAsync`, call `_stubs.DeleteAsync(oldStubPath, ct)`.
  Order matters: update item first (UserData stays attached to
  the same BaseItem.Id), then delete the symlink. If we delete
  first and the in-process update fails mid-flight, a subsequent
  scan would re-resurrect the BaseItem from the now-missing
  symlink and create a duplicate. The `oldStubPath` is
  re-derivable from `(tmdbId, title, kind)` via
  `PhantomStubManager.DeriveFilename`, but for safety we also
  persist it in `phantom_items.stub_path` (already exists) so
  there is no ambiguity if title changed between create and
  promote.

- **Changed: `Materialisation/EvictionSweeper.cs`** — the
  reverse direction. On eviction:
  ```csharp
  var newStub = await _stubs.CreateAsync(item, kind, ct);
  item.Path = newStub;
  item.IsVirtualItem = true;
  item.IsLocked = true;
  await _libraryManager.UpdateItemAsync(
      item, item.GetParent(), ItemUpdateType.MetadataEdit, ct);
  // gostream Remove/Unprestage as today
  ```
  No reparent — same library throughout the lifecycle.

- **Changed: `Configuration/PluginConfiguration.cs`** — new
  settings:
  - `string PhantomStubRoot` (default
    `/var/lib/jellyfin/phantom-library`).
  - `string PhantomMoviesLibraryName` (default `gostream-movies`).
  - `string PhantomShowsLibraryName` (default `gostream-shows`).
  - All three exposed in `configPage.html`. Changing
    `PhantomStubRoot` after first use does not automatically
    migrate existing symlinks; documented as an admin-only knob
    set once at install.

- **Deleted: nothing.** `PhantomMediaSourceProvider` and the
  splash extraction stay as-is. The phantom items now have a
  `Path` set, but the provider still wins because items with
  `IsVirtualItem = true` route through the
  `IMediaSourceProvider` chain before the file path is opened
  directly. Verified in M5 and unchanged.

#### Jellyfin upstream issue (deferred)

`POST /Library/VirtualFolders/Paths` (controller method
`LibraryStructureController.AddMediaPath`,
`Jellyfin.Api/Controllers/LibraryStructureController.cs` lines
230-263) calls `_libraryManager.AddMediaPath(name, info)` and
then queues a `ValidateMediaLibrary` scan. **It does not call
`RefreshMetadata` on the affected `CollectionFolder`.** That is
asymmetric with the sibling `RenameVirtualFolder` endpoint (same
file, lines 195-198) which explicitly does
`await child.RefreshMetadata(...)` after the mutation.

Consequence: `CollectionFolder.PhysicalLocationsList` and
`PhysicalFolderIds` (defined in
`MediaBrowser.Controller/Entities/CollectionFolder.cs` lines
63-67) are not refreshed. Browse queries resolve via
`LibraryManager.GetTopParentIdsForQuery`
(`Emby.Server.Implementations/Library/LibraryManager.cs` line
~2068) which returns `collectionFolder.PhysicalFolderIds`
directly. Items whose `TopParentId` is not in that list are
invisible regardless of `ParentId`, mblinks, options.xml
PathInfos, scan completion, or restart count.

Reproducible on `master` (Jellyfin 12.0.0 in the in-tree clone
at `./jellyfin/`, HEAD `1a2db53710`) and on `10.11.9`. Cold-start
+ full `ValidateMediaLibrary` + 142s scan duration +
`/Items/{cf}/Refresh` with `MetadataRefreshMode=FullRefresh` all
fail to update the persisted state. Direct patch of
`BaseItems.Data` (the JSON blob of the CF's serialised state)
followed by restart **does** restore browse, proving the code
path works once the bytes are right — only the refresh-trigger
is missing from `AddMediaPath`.

**Plan: defer the upstream PR; ship the workaround now.**

Rationale:

- Even if upstream accepts in days, the fix lands in the next
  Jellyfin minor release. Operators on current 10.11.x do not
  benefit until they upgrade, which is 6–18 months typical lag.
  Phantom Library v0.1 targets 10.11.x and forward.
- The plugin workaround is small (~20 lines in
  `PhantomCollectionFolderBinder`) and *idempotent*. It reads the
  CollectionFolder's `PhysicalLocationsList`, appends our
  phantom dir + the new physical folder Id if missing, and calls
  `libraryManager.UpdateItemAsync(cf, cf.GetParent(),
  ItemUpdateType.MetadataEdit, ct)`. When upstream fixes
  `AddMediaPath`, our check sees the list already correct and
  no-ops. No removal required when the fix lands.
- Removing the workaround on a later Jellyfin version is its own
  risk. The cost of leaving it in place is the cost of one
  startup-time idempotent check.

**Upstream PR (deferred work item — track separately, do not
block v0.1.1)**:

- Repository: `jellyfin/jellyfin`.
- Branch base: `master` (12.0.0 in the in-tree clone).
- Minimal diff (sketch — to be verified against current master):

  ```csharp
  // LibraryStructureController.AddMediaPath, replace the existing
  // Task.Run finally block. Pattern is the same as the sibling
  // RenameVirtualFolder method 35 lines above:
  Task.Run(async () =>
  {
      if (refreshLibrary)
      {
          await _libraryManager.ValidateTopLibraryFolders(
              CancellationToken.None, removeRoot: false)
              .ConfigureAwait(false);
          var lib = _libraryManager.GetUserRootFolder()
              .Children.OfType<CollectionFolder>()
              .FirstOrDefault(f => string.Equals(
                  f.Name, mediaPathDto.Name,
                  StringComparison.OrdinalIgnoreCase));
          if (lib is not null)
          {
              _libraryManager.ClearIgnoreRuleCache();
              foreach (var child in lib.GetPhysicalFolders())
              {
                  await child.RefreshMetadata(CancellationToken.None)
                      .ConfigureAwait(false);
                  await child.ValidateChildren(
                      new Progress<double>(), CancellationToken.None)
                      .ConfigureAwait(false);
              }
              await lib.RefreshMetadata(CancellationToken.None)
                  .ConfigureAwait(false);   // ← the missing call
          }
          else
          {
              await _libraryManager.ValidateMediaLibrary(
                  new Progress<double>(), CancellationToken.None)
                  .ConfigureAwait(false);
          }
          _libraryManager.ClearIgnoreRuleCache();
      }
      else
      {
          await Task.Delay(1000).ConfigureAwait(false);
          _libraryMonitor.Start();
      }
  });
  ```

- Bug report content (to be filed at
  `github.com/jellyfin/jellyfin/issues`):
  - Title: *AddMediaPath does not refresh CollectionFolder
    binding; items in added path are invisible to browse*.
  - Repro: cold-start Jellyfin → existing library with one path
    → `POST /Library/VirtualFolders/Paths` adding a second path
    → drop files in second path → `POST /Library/Refresh` and
    wait → `GET /Users/{userId}/Items?ParentId=<libraryId>`
    returns only items from path #1.
  - Inspection: `SELECT Data FROM BaseItems WHERE Id =
    '<libraryId>';` shows `PhysicalLocationsList` and
    `PhysicalFolderIds` still reflect single-path state.
  - Expected: `AddMediaPath` should refresh the CollectionFolder
    the same way `RenameVirtualFolder` does (same controller, 35
    lines above the bug).
  - Workaround: described in this milestone.

  File the issue. Do not block on its triage. Track the upstream
  PR as a separate deferred item; revisit when the fix is in a
  released Jellyfin version, then evaluate whether the plugin
  workaround can be simplified or removed.

#### Materialise loop, scan-free

The materialise loop is **fully in-process**; no scan, no
progress polling, no scheduled-task wait. Pattern (already in
`Materialiser.PromoteItemAsync`, lines 416-432; M10 adds the
stub-delete tail):

```csharp
item.Path = fusePath;
isVirtualProp!.SetValue(item, false);
await _libraryManager.UpdateItemAsync(
    item, item.GetParent(), ItemUpdateType.MetadataImport, ct);
await _stubs.DeleteAsync(oldStubPath, ct);
```

`UpdateItemAsync` invalidates the in-memory `BaseItem` cache
and persists to SQLite in one transaction. Browse API reflects
the change in milliseconds. BaseItem.Id is unchanged so UserData
(favourites, watch progress) survives the transition
automatically.

Verified in the rig: SQL-update + immediate browse showed the
cached old value (because raw SQL bypasses the in-memory layer);
a single `/Items/{id}/Refresh` forced invalidation and browse
returned the new path. The real plugin path through
`UpdateItemAsync` does both atomically.

#### Tests (new and updated)

- **`PhantomStubManagerTests.cs`** (new) —
  `DeriveFilename` purity and collision-resistance properties
  (different tmdb_id → different filename; same inputs → same
  filename; sentinel always present), symlink create/delete with
  a tempdir, idempotency.
- **`PhantomCollectionFolderBinderTests.cs`** (new) — binder
  is idempotent (second call no-ops when binding already
  correct), patches missing paths into
  `cf.PhysicalLocationsList` / `PhysicalFolderIds`, throws a
  clear error if the configured library name does not exist.
  Uses a fake `ILibraryManager` and a fake `CollectionFolder`.
- **`MaterialiserTests.cs`** (updated) — promotion deletes the
  stub symlink after `UpdateItemAsync` succeeds, does not delete
  if `UpdateItemAsync` throws, deletes even if the symlink
  target file is missing.
- **`EvictionSweeperTests.cs`** (updated) — eviction creates a
  new stub and rebinds Path before any gostream removal call;
  Path mutation precedes filesystem changes.
- **Live integration**: full virtual-→-materialised-→-evicted-→-
  virtual round-trip in the rig using the `/tmp/jf-test/m2.sh`
  pattern (see `docs/agents/testing.md`). Confirms (a) phantom
  shows up in `gostream-movies` browse, (b) materialise swaps
  Path with no reparent, (c) UserData (favourite flag) survives,
  (d) eviction restores the symlink, (e) re-materialise works.

#### Operator-visible changes

- Phantoms are now visible in `gostream-movies` and
  `gostream-shows` libraries, intermixed with materialised
  items. No new top-level library.
- One new install step: create + chown the phantom-library dir.
  Documented in README and surfaced as a clear startup error if
  forgotten.
- Three new admin config knobs (root path, two library names).
  All have sane defaults; an operator running the standard
  install never touches them.
- Disk usage: ~few MB at most (one splash file + N symlink
  entries at ~few-hundred bytes of inode each). Negligible.

#### Done criteria

1. Cold-start plugin against a clean rig with the standard
   install steps → phantom-library dir exists, splash extracted,
   `gostream-movies` library lists the phantom dir in its
   `PhysicalLocationsList` after one startup cycle.
2. Suggestions refresh creates N phantom rows → all N visible in
   browse of `gostream-movies` within the same poll cycle (no
   user-initiated scan needed).
3. Phantom items survive a full library scan without renaming,
   metadata mutation, or culling.
4. Materialise one phantom → that item's row stays the same Id,
   gets the new fuse path + IsVirtualItem=false, the old symlink
   is gone from disk, browse reflects the change within ~1s, no
   scan triggered.
5. Evict one materialised item → row's Id unchanged, new stub
   symlink created, Path points at it, IsVirtualItem=true,
   gostream Remove/Unprestage called; browse reflects the
   demotion within ~1s.
6. Round-trip: virtual → materialise → favourite → evict-then-
   restore (per favourite protection) ends with the row in the
   correct state and UserData (favourite flag) intact.
7. Unit tests green; live integration tests pass via the rig.
8. Upstream bug report filed (referenced in CHANGELOG and PR
   description). Upstream PR is **not** a blocker for tagging
   v0.1.1.

### M11 — Post-M10 phantom UX polish (≤ 4 days)

> **⚠ References deprecated naming scheme.** The bug symptoms
> documented below (filename stems with underscores and the
> `__phantom_tmdb<id>` sentinel appearing as user-visible Names)
> are artifacts of the legacy on-disk scheme that has since been
> replaced by Jellyfin-native `[tmdbid-<id>]`. The healing logic
> the M11 work added is now redundant under the new scheme and
> scheduled for removal in the spike follow-up. See AGENTS.md
> § "Canonical phantom stub naming scheme."

M10 restored phantom **visibility**. Live operator testing on
2026-06-05 revealed six distinct UX problems that block usable
phantom browse + play. Each is small individually; together they
are the difference between "phantoms appear in the library" and
"phantoms are usable". Track and fix in this milestone.

Observed issues (with diagnosis where known):

1. **Phantom catalogue is far too small.** Operator expected the
   entire TMDB catalogue but saw ~10 movies. SuggestionsContributor
   currently fetches **Trending** (defaults to 40 movies + 40 series
   per refresh) plus per-user **Recommended** (which falls back to
   Trending when the user has no favourites). With two users + no
   favourites the actual surface is one cached Trending list, ~40
   titles, of which ~30 already exist as real gostream items so
   only ~10 are net-new phantoms.

   **Fix (one of, or both):**
   - **TMDB Discover / catalogue walk.** Add a `Discover`
     suggestion source that paginates `GET /discover/movie` and
     `GET /discover/tv` to back-fill the library with thousands of
     titles. Capped by config (`SuggestionsCatalogueMaxItems`,
     default e.g. 5000). Refreshes incrementally; respects TMDB
     rate limits (40 req / 10 s).
   - **TMDB Popular** as a higher-cardinality fallback than
     Trending. ~10 000 popular movies + ~10 000 popular shows
     across many pages.

   The architectural intent (PLAN §Goals) is "the entire TMDB
   catalogue appears to exist". Trending alone never delivered
   that even before M10; the symptom was masked while phantoms
   were invisible. M11 makes the catalogue source operator-tunable
   and ships a sensible default (Discover paginated to 5000
   movies + 5000 series).

2. **Display name shows filename stem with underscores and
   sentinel** (e.g. `Backrooms__phantom_tmdb1083381` instead of
   `Backrooms`).

   **Diagnosis:** `VirtualItemFactory.CreateVirtualMovieFromHit`
   sets `Name` to the TMDB title, then `SuggestionsContributor`
   stamps `Path = stubPath` and `IsLocked = true`. `IsLocked`
   should prevent any provider from overwriting Name, but on M10
   the on-disk filename is the *only* source the scanner can use
   when it encounters the symlink, and *something* downstream is
   either re-resolving the Name from the path stem or the lock is
   not being honoured. (Possibly: the scanner ran *before* the
   Name was persisted, given how Suggestions builds the item
   in-memory and then `AddChild`s it; the resolver picks the file
   first, derives Name from filename, then Save commits that
   over-mutated state.)

   **Fix:**
   - Verify `IsLocked = true` is persisted on the BaseItem before
     `parent.AddChild` triggers any scanner pass.
   - Set `ForcedSortName = title` and `SortName = title` so even
     if Name is wrong, sorting is correct.
   - Investigate whether the resolver derives Name from filename
     before our `UpdateItemAsync` lands. If so, the create flow
     needs an explicit `UpdateItemAsync` with the correct Name
     after `AddChild`, with `IsLocked=true`, to overwrite the
     stem-derived Name.
   - As a belt-and-braces measure, override `Name` in a metadata
     provider that runs at locked items too (TBD whether possible).

3. **Image displays as the plugin's splash thumbnail instead of
   the TMDB poster.**

   **Diagnosis:** `VirtualItemFactory` does not stamp
   `ImageInfos[Primary]` from TMDB at create time; the existing
   `TmdbImageProvider` populates images on metadata refresh. But
   M10 set `IsLocked = true`, which **skips** all metadata
   providers including image providers. The image falls back to
   whatever the scanner derives from the on-disk file — in our
   case the splash.mp4's embedded thumbnail (or a folder image
   composite).

   **Fix:** in SuggestionsContributor, after constructing the
   item with TMDB hit data, **eagerly fetch the TMDB primary
   image URL and stamp it on the BaseItem before AddChild**:
   ```csharp
   newItem.ImageInfos = new[]
   {
       new ItemImageInfo
       {
           Path = tmdbImageUrl,  // remote URL; Jellyfin caches on first fetch
           Type = ImageType.Primary,
       },
   };
   ```
   TMDB hits include `PosterPath`; the URL is
   `https://image.tmdb.org/t/p/original<poster_path>`.
   Same for backdrop (`BackdropPath` → ImageType.Backdrop).
   The image is fetched lazily by Jellyfin's image cache the
   first time a client requests it, so this does not add a TMDB
   round-trip during Suggestions.

   Alternative: leave `IsLocked = false` and let the existing
   TmdbImageProvider populate images. But then we lose the
   Name-protection from issue 2. The eager-stamp approach lets
   us keep `IsLocked = true` (Name protection) AND get correct
   images.

4. **No phantom TV series visible** despite log showing 5 series
   symlinks were created in `phantom-library/shows/`.

   **Diagnosis:** unclear. Candidates:
   - Series-level browse filter differs from movies (e.g. Jellyfin
     hides Series rows with no Season/Episode children).
   - SeriesIngestor's stub path is not landing in the bound
     phantom phys folder (different TopParentId from the series
     CollectionFolder).
   - `gostream-shows` library's PhysicalFolderIds was set
     correctly (verified earlier) but the BaseItems for our
     phantom Series may have a different `TopParentId` from the
     phantom phys folder.

   **Action:** dump DB to verify what `Type`, `Path`, `ParentId`,
   `TopParentId`, `IsVirtualItem`, `MediaType` the symlinked
   Series rows actually have. Compare with a real gostream Series
   row. Fix the discrepancy.

5. **Materialise never fires when user presses Play on a phantom.**
   The splash plays, then the splash ends, no real torrent is
   added, item stays Virtual.

   **Diagnosis:** M5's `PhantomMediaSourceProvider` returns the
   splash MediaSource on play-press but the **materialisation
   trigger** is `UserDataSavedListener` watching for
   `IsFavorite=true`, not for play. Per PLAN §M5 the play-press
   workflow was: "fake play button → splash hand-off". The
   trigger to actually materialise on play was implicit ("user
   marks favourite") and never wired to play events.

   **Fix:** subscribe to `ISessionManager.PlaybackStart` (or
   `IUserDataManager.UserDataSaved` with `PlaybackPositionTicks > 0`)
   and when a phantom's splash is played, enqueue the item for
   materialisation. Materialise runs in background while splash
   loops/ends; next play press hits the now-real fuse path.
   Per-user toggle (already exists in PluginConfiguration) gates
   the auto-materialise-on-play behaviour.

   Sub-decision: should the splash auto-restart while
   materialisation is in progress, so the user sees "loading"
   instead of "playback ended"? Probably yes — looping the splash
   for up to N seconds (e.g. 60) gives a UX of "it's working".
   After materialisation completes, the next MediaSource refresh
   returns the real source.

6. **Playing the splash marks the phantom as played**, polluting
   watch history with garbage "watched" state.

   **Diagnosis:** Jellyfin's playback reporting sees a media
   session for the BaseItem, increments PlayCount, sets
   PlayedDate. There is no signal to Jellyfin that the splash is
   not the real content.

   **Fix (probably best):** in the playback-completion event
   handler for any phantom item, reset its UserData
   (`PlayCount = 0`, `Played = false`, `PlaybackPositionTicks = 0`,
   `LastPlayedDate = null`). The user's intent on a phantom Play
   is "materialise + play", not "watch and mark watched". Real
   played-state only counts after materialisation, when the user
   plays the actual torrent-backed file.

   Alternative: customise `PhantomMediaSourceProvider` to return
   `RunTimeTicks = 0` or set `SupportsTranscoding = false` so
   Jellyfin treats the splash as a non-content session. Likely
   does not stop the played-mark on its own.

#### Tests

- Unit: `VirtualItemFactory` stamps ImageInfos[Primary] from TMDB
  `PosterPath` (when present).
- Unit: name-with-stem-and-sentinel round-trip —
  PhantomStubManager creates filename, plugin creates BaseItem
  with `Name=<title>`, after a simulated scan pass the Name
  still equals `<title>` and is not the filename stem.
- Integration: full play-press → splash → materialise loop in
  the rig. Verify (a) splash plays, (b) materialise fires
  in-process, (c) next /Users/{}/Items?Ids=<id> returns a
  MediaSource backed by a real fuse path, (d) PlayCount on the
  phantom remains 0 after splash playback completes.
- Integration: Series phantoms visible in `gostream-shows`
  browse with correct Name + image.
- Integration: catalogue walk (Discover) creates N phantoms
  bounded by the config cap, respects TMDB rate limits.

#### Done criteria

1. Operator sees thousands of phantom items in `gostream-movies`
   and `gostream-shows` after Discover-driven first refresh
   (bounded by `SuggestionsCatalogueMaxItems`).
2. Phantom item Names display as TMDB titles, not filename stems.
3. Phantom item primary images are TMDB posters (cached locally
   by Jellyfin's image system on first browse).
4. TV Series phantoms are visible and browseable.
5. Pressing Play on a phantom triggers materialisation; splash
   loops/transitions; next play hits the real fuse path.
6. Splash playback does not increment PlayCount or set
   PlayedDate. Real materialised playback does.
7. Unit + integration tests green.

---

### Spike — Jellyfin-native `[tmdbid-<id>]` stub layout (≤ 1 day)

Status: **IN PROGRESS — operator validating.** A/B spike that swaps
the on-disk phantom-stub filename / dirname scheme from the custom
`__phantom_tmdb<id>` sentinel to Jellyfin's native
`[tmdbid-<id>]` path-token form, so the scanner-derived
BaseItem.Name is the real title (e.g. `The Boys` instead of
`The_Boys__phantom_tmdb1234`).

#### What shipped (this PR)

- New `PhantomPathUtilities` helper recognises BOTH the legacy
  sentinel and the new bracketed token. All
  `Contains("__phantom_tmdb")` checks in feature code routed through
  it.
- `PhantomStubManager` gains year-aware overloads of `CreateAsync`,
  `DeriveFilename`, `DeriveSeriesStubPaths` emitting
  `<Title> (<Year>) [tmdbid-<id>]` layout. `DisplaySanitize`
  preserves spaces / parens / hyphens; strips only filesystem-
  hostile chars.
- Call sites threaded with `int? year` from `BaseItem.ProductionYear`:
  `SuggestionsContributor` (create + heal), `SeriesIngestor`,
  `EvictionSweeper`.
- `scripts/migrate-stub-layout-v1.sh` is the **canonical** offline
  migration (Jellyfin stopped). It handles legacy renames, recovers
  half-migrated rows, and collapses duplicate BaseItems from the
  failed in-plugin run. Records `plugin_meta.stub_layout_v1_complete`
  on a clean pass.
- *(2026-06-08 rollback)* The in-plugin `StubLayoutMigration`
  IHostedService that originally shipped with v0.2.0.0 was
  **deleted**: it raced the live library scanner and produced
  duplicate BaseItems. AGENTS.md gained a "Single-operator
  deployment" section codifying the rule: prefer offline bash
  scripts over in-plugin runtime data-mutation services. Do not
  reintroduce a runtime migration service.
- Plugin version bumped to `0.2.0.0`; rig + docs updated.

#### Intentionally left in place (spike scope)

- `HealBrokenPhantomAsync` + dedupe-hit healing branch.
- `IsLocked = true` re-stamp after `CreateItem` / `UpdateItemAsync`.
- `PhantomImageProvider`.
- `PhantomStatusDecorator` Overview-prefix mutation.
- `PhantomStubManager.Sentinel` legacy constant.

#### Follow-up cleanup PR (blocked on operator validation)

- Remove `HealBrokenPhantomAsync` + dedupe-hit healing branch.
- Drop default `IsLocked = true` for new phantom items.
- Remove (or scope down) `PhantomImageProvider`.
- Remove `PhantomStatusDecorator` Overview mutation + the
  `original_overview` round-trip column.
- Remove `Sentinel` constant + `PhantomPathUtilities` legacy branch.

### M13 — Per-series subdir stub layout for TV phantoms (≤ 2 days)

> **⚠ DEPRECATED naming scheme described below.** The directory
> structure documented here used `<SafeName>__phantom_tmdb<id>/`
> as the series-level subdir name. That has been replaced by
> `<DisplayTitle> (<Year>) [tmdbid-<id>]/` in the spike milestone
> below. The per-series subdir architectural decision (one dir
> per series rather than a flat shows root) is preserved; only
> the naming token changed. See AGENTS.md § "Canonical phantom
> stub naming scheme."

Shipped: PLAN §M13 design implemented in full. See `CHANGELOG.md`
entry under `[Unreleased] / Added` for the user-visible summary;
this section is left in place as the design record so future
milestones can refer back to the rationale.

#### What shipped

- `PhantomStubManager.CreateAsync(Series)` now creates
  `phantom-library/shows/<SafeName>__phantom_tmdb<id>/Season 01/<stem> S01E01.<splashExt>`
  and returns the **per-series directory** path. Movie stubs
  unchanged.
- New pure helper `DeriveSeriesStubPaths(title, tmdbId)` returns
  the `(SeriesDir, SeasonDir, EpisodeFile)` triple. Used by
  `EvictionSweeper.DemoteAsync` to point `gostream.RemoveAsync`
  at the inner episode file (not the dir).
- `PhantomStubManager.DeleteAsync` recursively removes a series
  dir only if its leaf carries the `__phantom_tmdb` sentinel;
  refuses any other directory. Movie symlink delete unchanged.
- `SuggestionsContributor.MaterialiseHitsAsync` /
  `HealBrokenPhantomAsync`: the series Path assignment line is
  unchanged — it now stores the series dir. The heal
  `alreadyMaterialised` substring check still correctly
  classifies a virtual series row (the dir leaf carries the
  sentinel).
- `Materialiser.MaterialiseCoreAsync`: the existing Series
  early-return keeps `PromoteItemAsync` / `ResolveHostPath` off
  the Series code path (their single-file Path assumption is
  preserved).
- Tests: new `PhantomStubManagerTests` for the per-series dir
  layout, S01E01 symlink, idempotency, recursive delete, and
  sentinel refusal. New `SuggestionsContributorTests` covering
  series Path assignment + `phantom_items` row write. New
  `EvictionSweeperTests` covering the Series demote branch
  (RemoveAsync against the inner episode file) and the rebind
  (stub_path replaced with a fresh series dir).

#### Tradeoffs

- Season number / episode number are hardcoded to `Season 01`
  / `S01E01`. Phantom series only ever expose a single
  placeholder; the autopilot creates the real Season NN / SNNENN
  paths under the gostream physical folder when the user plays.
- The series stub directory itself is a plain directory, not a
  symlink. Recursive delete on it is safe under the sentinel
  guard. We don't try to chase reparse points.

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
