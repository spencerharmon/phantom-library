# Changelog

All notable changes to this project are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Opportunistic magnet-cache prefetch on user activity
  (p6-magnet-cache-opportunistic-prefetch).** ROI Priority 6, revised
  architecture item 2a. Every user-initiated caller site
  `p6-yield-to-user-callers` wired for the availability-priority bump +
  activity-marker stamp — `PhantomSourceManager`'s details/playback
  candidate-refresh view, and `Materialiser.MaterialiseAsync` (which
  autopilot prefetch and favourite ingest already route through) — now ALSO
  enqueues a HIGH-priority `magnet_cache_jobs` row
  (`PhantomDb.EnqueueOpportunisticMagnetCacheJobAsync`, priority
  `PhantomDb.OpportunisticMagnetCachePriority = 100`) for the touched
  movie/episode item, alongside (never replacing) the existing promote. This
  preempts any competing low-priority background-sweep job, reusing
  `p6-magnet-cache-store`'s max-never-lowered enqueue + priority-first claim
  ordering. Series/season-level views (no single touched item) are
  unaffected. Best-effort, movie AND episode parity. No schema change. See
  `tests/Jellyfin.Plugin.PhantomLibrary.Tests/OpportunisticMagnetCachePrefetchTests.cs`.

- **Availability convergence guarantee + TTL re-probe
  (p6-availability-convergence).** ROI Priority 6 item 5 (the acceptance
  capstone over the whole Priority 6 set). No availability item churns the
  short `AvailabilityTransientRetryMinutes` interval forever: once an item's
  consecutive-transient `attempt_count` (bumped on every claim, reset to 0 on
  any definitive `available`/`unavailable` completion) exceeds the new
  `AvailabilityTransientMaxAttempts` (default 8), `AvailabilityProbeWorker`
  escalates the retry cadence to the bounded `AvailabilityTransientEscalatedRetryHours`
  (default 24h) — the same long-backoff shape already used for the
  `no_capable_indexer`/unreleased pre-filters — for missing-metadata,
  indeterminate-transient, and probe-exception outcomes alike, movie and
  episode. `attempt_count` is now exposed on `AvailabilityItemRow` and
  threaded through every `availability_items` read/claim path in
  `PhantomDb`. Added
  `tools/rig-scenarios/45-availability-probe.sh`, the Priority 6 acceptance
  rig proving all five Priority 6 acceptance items (no-IMDB Prowlarr-shaped
  resolve, user-priority queue-jump ahead of a backlog, no-capable-indexer/
  future-aired deep-defer, search/browse-list unavailable-badge split with
  full hidden-series episode grid, and this task's convergence + TTL
  re-probe) for movie AND episode.

- **Availability-sweep pre-filter for unavailable titles
  (p6-prefilter-unavailable).** The background availability sweep now
  pre-classifies a claimed item BEFORE spending a probe cycle on it, so the
  sweep spends cycles where availability is plausible instead of churning on
  a permanent (or long-lived) no-op:
  - **No capable indexer.** `MagnetSelector.HasCapableIndexer` mirrors
    `ProbeAsync`'s abstention logic (an indexer that `RequiresImdb`, e.g.
    Torrentio, abstains without an imdb id) without invoking the indexer
    layer. If no enabled indexer can serve the query, the item deep-defers
    with the existing `AvailabilityNoIndexerRetryHours` long backoff (status
    stays `unknown`) instead of retrying at the 30-minute transient cadence.
    Once a title-based indexer (e.g. a configured Prowlarr) is enabled, it
    is correctly treated as capable even without an imdb id — this pre-
    filter depends on `p6-prowlarr-indexer-wiring` for that reason.
  - **Unreleased / not-yet-aired.** A movie whose TMDB release year is still
    in the future, or an episode whose catalogued TMDB air date has not yet
    passed, now deep-defers to the release boundary (Jan-1 of the release
    year for a movie, matching the existing UI synthetic date; the air date
    plus `EpisodeReleaseDelayHours` for an episode, mirroring the boundary
    series-expansion already computes) instead of being probed every cycle.
    `last_error_kind` is recorded as `unreleased`.
  - Both pre-filters reuse the existing `next_check_at`/backoff columns and
    `RescheduleAvailabilityTransientAsync` primitive — no schema change.

- **Browse-LIST vs search/BaseItem surface split
  (p6-search-list-surface-split).** The channel-item emission the interactive
  browse LIST uses (Movies root, Shows root) is now separate from the
  emission path used to keep unavailable/unknown phantoms searchable:
  - `PhantomMoviesChannel.SearchSyncFolderId` / `PhantomShowsChannel.SearchSyncFolderId`
    are new, UI-unreachable `GetChannelItems` folder ids that emit the FULL
    catalogue (`PhantomDb.ListAllMovieRowsAsync` / `ListAllSeriesRowsAsync`),
    tagged so the existing badge overlay resolves Unavailable/Unknown
    correctly, instead of the browse-LIST-filtered set
    (`ListVisibleMovieRowsAsync` / `ListVisibleSeriesRowsAsync`).
  - `PhantomLibraryBadgesController`'s computed-channel-id fallback map (used
    to badge an item that is not yet a real BaseItem) now covers the full
    movie catalogue and the series-visibility-agnostic episode set, instead
    of being restricted to the browse-LIST's `SeriesMinAvailableEpisodes`
    filter.
  - **Fixed:** a series below `SeriesMinAvailableEpisodes` (list-hidden) now
    still emits its FULL season/episode grid when its season-detail is
    reached directly — previously `GetEpisodesForSeasonAsync` blanked the
    entire episode list for such a series, contradicting the "still
    reachable via search, full grid" contract. Only an explicit per-user
    hide still blanks season/episode detail.
  - **Scope note:** wiring a periodic sync that actually WALKS the new
    `SearchSyncFolderId` path through `IChannelManager` (so Jellyfin persists
    every catalogued item as a real, globally-searchable BaseItem even when
    it has never been interactively browsed) is not included in this change.
    `IChannelManager.GetChannelItemsInternal` derives the channel's
    `InternalChannelItemQuery.FolderId` from an existing library `ParentId`
    BaseItem's `ExternalId`, and the search-sync folder is deliberately never
    linked from any browsable parent — so persisting it requires either
    exposing a real (tag-hidden) parent folder or a dedicated follow-up that
    reimplements the BaseItem-materialisation ChannelManager already does
    privately. Tracked as a follow-up; this change's DB/channel-level split
    (LIST vs full-catalogue emission, badge coverage, season-detail full
    grid) is complete and covered by unit tests.

- **Breadth-first, priority-aware, user-yielding availability probe
  (probe-redesign-worker-queue).** The background source-availability probe no
  longer enqueues one row per TV episode and grinds through the whole catalogue
  blindly. Changes:
  - **Priority-aware claim ordering.** `availability_items` gains a
    `priority INTEGER NOT NULL DEFAULT 0` column; the claim SELECT (both the
    plain and the round-robin episode-cursor variants) now orders by
    `priority DESC` first, so user-initiated paths can promote specific items /
    series ahead of the background backlog via the new
    `PhantomDb.SetAvailabilityPriorityAsync` /
    `PhantomDb.BumpSeriesAvailabilityPriorityAsync`.
  - **Breadth-first series expansion.** On series expansion only a small set of
    representative episodes (earliest-aired, else lowest season/episode;
    `AvailabilityBackgroundEpisodesPerSeries`, default 1) is enqueued as due now;
    the rest are deferred `AvailabilityDeferredEpisodeDays` (default 30) into the
    future. Series visibility still keys off the representative. On-demand/user
    probes bypass this queue and can check any episode immediately.
  - **No-capable-indexer handling.** When a probe reports `NoCapableIndexer`
    (no enabled indexer can serve the query as-is, e.g. no resolvable imdb id +
    Prowlarr disabled) the row is deferred a long `AvailabilityNoIndexerRetryHours`
    (default 24h) with status left `unknown`, instead of churning on the 30-minute
    transient retry.
  - **Yield to user-initiated work.** A `availability.user_activity_at` plugin_meta
    marker (via `PhantomDb.TouchUserActivityAsync` / `GetUserActivityAtAsync`) lets
    the sweep back off for `AvailabilityYieldToUserSeconds` (default 20) while the
    user is actively driving on-demand probes.
  - **BREAKING: requires wipe.** Schema bumped 17 → 18 (adds
    `availability_items.priority` + `idx_availability_priority_due`). Per
    AGENTS.md "No database migrations until v1.0", an existing pre-v18 DB is
    hard-refused at startup. Stop Jellyfin, run
    `sudo bash scripts/phantom-wipe.sh --commit`, then restart. Both SQLite and
    Postgres build the new column from the shared schema DDL; no ALTER path.

- **Enforcing LRU reaper for the shared jellyfin-metadata cache (jellyfin-metadata-quota-reaper).**
  The shared `jellyfin-metadata` PVC reached 78G on `spray` (library 50G / People 18G / channels 9G)
  with no enforced upper bound (`persistence.jellyfinMetadata.size` was documentation only — local-path
  ignores it). Mirroring the existing gostream warmup pattern: `jellyfinMetadata.quotaGb` +
  `jellyfinMetadata.headroomGi` are now the single source of truth for the PVC's size (DERIVED, not
  hand-set); a new `jellyfin-metadata-reaper` CronJob (`jellyfinMetadataReaper.*`, hourly by default)
  measures usage and, once it exceeds quota, evicts the least-recently-accessed files first, restricted
  to the regenerable `library/`, `People/`, `channels/` subtrees (never touches anything a library scan
  cannot rebuild), and is a no-op below quota. It writes a Prometheus textfile-collector metrics file
  (`jellyfin_metadata_cache_usage_bytes` / `jellyfin_metadata_cache_quota_bytes` /
  `jellyfin_metadata_reaper_last_run_reclaimed_bytes`) so the existing monitoring stack can alert before
  the volume actually fills (e.g. at 80% of quota). New `jellyfin.metadataGeneration.*` chart values
  (`trickplayEnabled`/`trickplayIntervalSeconds`/`trickplayQualityPercent`/
  `personImageMaxDownloadsPerRefresh`/`artworkMaxResolutionPx`, wired to `PHANTOM_JELLYFIN_*` env vars
  on the workload container) cut generation at the source, which is cheaper than reaping after the
  fact. Reaper script: `scripts/jellyfin-metadata-reaper.sh` (embedded byte-identical in the chart at
  `deploy/helm/phantom-library/files/` since Helm's `.Files.Get` cannot read outside the chart
  directory); tested by `scripts/tests/jellyfin-metadata-quota-reaper.test.sh` against synthetic
  over-/under-quota fixtures plus a `helm template` render assertion. Chart bumped to 2.8.0
  (BREAKING: `persistence.jellyfinMetadata.size` removed in favor of the derived quota+headroom size).

- **Config-gated PostgreSQL backend for PhantomDb (p4-phantomdb-postgres-provider).**
  `PhantomDb`'s own state (discovery cache, catalogue, availability, materialised
  state, magnet caches, bulk-materialise queue, user prefs/hidden-items, TMDB
  metadata caches, `materialise_in_flight`, plugin meta) can now optionally run
  against a shared `phantom_<role>` PostgreSQL logical database instead of a
  per-color SQLite file, selected via the `PHANTOM_POSTGRES_HOST` env var (see
  `p4-chart-postgres-wiring`). SQLite stays the compiled-in default and remains
  off in prod unless explicitly configured; `EnsureSchema`'s hard-refuse on a
  schema-version mismatch is unchanged for both backends. New
  `Jellyfin.Plugin.PhantomLibrary.State.Db` provider abstraction
  (`IPhantomDbProvider`, `SqliteDbProvider`, `PostgresDbProvider`) plus
  `PhantomDb.CreatePostgres(connectionString)`. No data migration is included
  here (see `p4-phantomdb-sqlite-to-postgres-migration`).

### Fixed

- **Prowlarr no longer references the unused `gitea-oci-pull` imagePullSecret.**
  The standalone Prowlarr Deployment (`deploy/helm/phantom-library/templates/prowlarr.yaml`)
  pulls the PUBLIC `ghcr.io/linuxserver/prowlarr` image directly from upstream by
  digest, so it never needed the gitea OCI registry pull secret the
  gostream/jellyfin-phantom workload pod requires. Its `imagePullSecrets` block is
  now conditional on a new `prowlarr.imagePullSecret` value (empty by default);
  the workload Deployment's `imagePullSecret` reference is unchanged. Chart bumped
  to 2.6.1.

### Added

- **Migration + integration live rig (P3 Stage 3).** A new operator/CI rig
  scenario `tools/rig-scenarios/44-migrate-v11-to-v12.sh` that seeds a rig from
  a **v11 synthetic** phantom.db (derived from scenario 41's zero-PII discovery
  fixture), runs `scripts/phantom-migrate-v11-to-v12.sh --commit`, and asserts
  the additive migration is correct (user_version 11→12; the two per-user tables
  + index present and empty; every pre-existing table census-identical; schema
  parity with a fresh v12 DB; the script's own predicted==actual verification) —
  then boots the vM plugin on the migrated DB and runs the full downstream e2e
  (35 + 36 + per-user 42) **against the migrated DB** (scenarios 35/36 gained a
  default-preserving `RIG_NO_RESET=1` mode so they can drive an already-seeded
  rig). The deterministic core is regression-covered in-repo by
  `scripts/tests/migration-rig.test.sh` (bash + sqlite3 only, no live Jellyfin).
  A **Gitea Actions** job `phantom-library-migration-rig`
  (`.gitea/workflows/migration-rig.yml`) runs the rig on the already-live
  self-hosted runner (no Zuul/Nodepool cross-dependency). The live boot + e2e
  half is **honest-red** (never a silent green) while the in-repo additive
  migration's target (v12) is behind the live plugin's `CurrentSchemaVersion`.

- **Per-scenario ratcheting performance-regression guard.** A self-contained
  .NET tool (`tools/perf/ratchet-guard/`, unit-tested via `dotnet test`) plus a
  runner/auto-filer (`tools/perf/ratchet-guard.sh`) that guards the five browse
  flows against per-scenario latency ceilings recorded in
  `tools/perf/ratchet-thresholds.json`. A measurement over its ceiling is a
  **breach** (fails the guard, and files a `beehive` performance-review task
  blocking the guard rather than silently accepting the regression); a
  measurement faster than the ceiling by the improvement margin **tightens** the
  ceiling downward — the ratchet only ever tightens, never loosens (a breach
  leaves the ceiling exactly where it was). Feeds off the `phantom_flow_duration_ms`
  baseline instrumentation.

- **OTLP flow-latency metrics for the five browse flows (pre-Postgres
  baseline instrumentation).** A new `Phantom.Flows` meter
  (`src/.../Diagnostics/PhantomFlowMetrics.cs`) records a
  `phantom_flow_duration_ms` histogram and a `phantom_flow_items` counter,
  tagged `flow` and `backend` (`sqlite`/`postgres`), around the five channel
  browse flows: list view, sort/filter (badge States endpoint), season
  listing, episode listing, and materialised listing. An opt-in OTLP exporter
  (`PhantomMetricsExporter`, an `IHostedService`) ships these over
  OTLP/gRPC or OTLP/HTTP. The exporter target is **configuration/env driven,
  never a baked-in host**: `MetricsOtlpEndpoint` / `MetricsOtlpProtocol`
  plugin config, falling back to the standard `OTEL_EXPORTER_OTLP_ENDPOINT` /
  `OTEL_EXPORTER_OTLP_PROTOCOL` env vars; disabled by default
  (`MetricsOtlpEnabled=false`). This establishes the SQLite baseline the
  Postgres load-time comparison and the ratcheting regression guard measure
  against.

### Fixed

- **Materialise in-flight leak: deterministic inline reclaim, no restart
  required.** A materialise hard-killed mid-flight (pod SIGKILL / warmup
  restart) never runs its `finally` cleanup and leaks a
  `materialise_in_flight` claim row; previously the only recovery was
  `MaterialiseInFlightSweeper`, a startup-only sweep, so a claim younger than
  `MaterialiseInFlightStaleMinutes` (default 10) at that single sweep survived
  indefinitely — wedging the item at `AlreadyInProgress` until a *second*
  restart happened to land after it aged out (observed on Rick and Morty
  S08E02, 2026-08-02). `PhantomDb.TryInsertMaterialiseInFlightAsync` now
  accepts an optional stale threshold and steals/reclaims an existing row
  inline — atomically, via `INSERT ... ON CONFLICT ... DO UPDATE ... WHERE
  started_at < cutoff` — the moment it is older than the threshold, with no
  dependency on a startup event. A claim younger than the threshold still
  blocks a concurrent duplicate exactly as before (safety case unchanged).
  `Materialiser.MaterialiseAsync` passes `MaterialiseInFlightStaleMinutes` as
  the reclaim threshold on every retry. `MaterialiseInFlightSweeper` is kept
  unchanged as a startup belt-and-braces sweep.

### Changed

- **Helm chart 2.1.1 — rename the gostream/GoStorm control-panel host default
  label `gostorm.` -> `tiramisu.`** (`templates/_helpers.tpl`
  `phantom-library.gostormHost`), matching the GoStream -> Tiramisu rebrand. A
  plain chart consumer now reaches the dashboard at `tiramisu.<hostname>` (still
  the `:9080` metrics `/dashboard` UI); `.Values.gostream.hostname` override and
  the `ingress.extraGostormHosts` / `gostorm` value-key/port names are unchanged
  (host label only, no values-schema break). Kept in lockstep with the flux
  blue/green role CNAMEs + `extraGostormHosts` (submodule `flux`).

### Added

- **P4 Stage A: `scripts/phantom-migrate-jellyfindb-to-postgres.sh`** — offline,
  operator-run migration of Jellyfin's authoritative library/user database off
  the per-color SQLite `jellyfin.db` onto a shared **PostgreSQL** instance served
  through the external [`Jellyfin.Pgsql`](https://github.com/JPVenson/Jellyfin.Pgsql)
  provider, enabling N replicas to share one authoritative store. The same script
  also moves phantom.db's own state to its own Postgres logical DB on the same
  server (`--source phantom`), per the multi-writer audit. Follows the P3
  five-stage staging-validation methodology (clone → predicted counts → staging
  validation on the inactive color → operator hand-validation → prod write);
  dry-run by default, stage-gated, count-verified, idempotent, additive
  (expand/contract-compatible), backs up the prod target first. Regression-tested
  by `scripts/tests/phantom-migrate-jellyfindb-to-postgres.test.sh`. See
  `docs/tasks/p4-mysql-migration-impl.md`. No operator action until the operator
  chooses to run the migration. (Replaces the earlier MySQL-targeted variant,
  now removed, after the 2026-07-31 ROI repointed Stage A to PostgreSQL.)

### Changed

- **BREAKING: requires wipe.** Phantom DB schema bumped v14→v15: adds
  `gostream_path_tmdb` (path→tmdb resolution cache). Pre-v1.0 has no
  migrations — stop Jellyfin, run `sudo bash scripts/phantom-wipe.sh
  --commit`, then restart. The cache repopulates on next browse.

### Fixed

- Phantom channel cold-cache browse is fast again. The gostream FUSE
  path→tmdb resolution map is now persisted to `phantom.db` instead of
  living only in memory, so a Jellyfin restart no longer forces a fresh
  TMDB search per orphan movie (~40s) and a TMDB title/year full-scan
  per orphan series (~5.3s). FUSE tree walks are also deduped via a 30s
  single-flight cache keyed on the gostream movies/shows version.

- Home screen no longer hangs with a perpetual loading indicator on
  native clients (Xbox, mobile) and is faster on web. Phantom channels
  no longer implement `ISupportsLatestMedia`, so Jellyfin core's
  `RefreshLatestChannelItems` no longer deep-enumerates the whole
  channel (series → season → build) to populate the "Latest in Phantom
  Movies/Shows" Home rows on every load — an enumeration that ran for
  seconds-to-minutes on production-shaped data and affected every
  client. Tradeoff: the "Latest in Phantom Movies/Shows" Home rows are
  removed for now; a cheap O(latest) replacement is deferred (see
  PLAN.md "Documented partials"). Guarded by
  `tools/rig-scenarios/40-channel-latest-suppressed.sh`.
- Home-screen badge state lookups (`POST /Plugins/PhantomLibrary/States`) no
  longer enumerate and MD5-hash the entire visible phantom catalogue
  (~540k movie+episode rows on the operator's data) on every request. Real
  (non-channel) library cards — Continue Watching items, library view tiles —
  now short-circuit before the catalogue scan, and the residual virtual-card
  fallback map is cached across requests (60s TTL, single-flight). This
  removes the sustained per-poll latency that kept the web loading indicator
  lit and slowed the Continue Watching section. Covered by
  `tools/rig-scenarios/39-channel-badge-states-perf.sh`.

### Documentation

- Added durable design/testing/deploy protocols for native phantom
  playback, channel cache invalidation, badge/UI scope, gostream path
  normalization, rig scenario authoring, and patched Jellyfin runtime
  alignment.

### Changed

- Added an allowed video-container materialisation setting; Phantom defaults gostream selection/validation to MKV files for client compatibility while allowing operators to opt into other containers.
- `install.sh --build` now builds and saves the gostream `docker.io/mrrobotogit/gostream:testing` image from the in-repo `gostream/` checkout before loading it into root podman storage, preventing stale `/tmp/gostream-testing.tar` deployments.
- BREAKING: requires offline migration. Phantom DB schema is now v14 to persist source-candidate validation state, magnet-failure validation policy versions, and durable bulk materialise queue tables; run `scripts/migrate-source-validation-v14.sh` with Jellyfin stopped before starting this build.
- Added an optional gostream library-control shared token setting; Phantom now sends `X-Gostream-Token` on protected gostream add/remove calls when configured.
- Patched Jellyfin now exposes server-advertised item actions through `GET/POST /Items/{itemId}/Actions`, and Phantom registers Materialise, Reset Phantom, and Reject current source actions for Phantom movie/episode items.
- BREAKING: patched Jellyfin deploy now requires replacing `MediaBrowser.Model.dll` and `Jellyfin.Api.dll` in addition to `MediaBrowser.Controller.dll` and `Jellyfin.LiveTv.dll`, because native item actions add model DTOs and an API controller.
- BREAKING: requires wipe. Phantom DB schema is now v13 to persist TMDB movie runtime minutes; this lets Phantom movie channel items expose `RunTimeTicks` so Jellyfin can save playback progress and list movies in Continue Watching.
- Added rig coverage for Phantom movie and episode `RunTimeTicks`, playback-progress reporting, and `/Users/{userId}/Items/Resume` Continue Watching results.
- Added `scripts/prestage-materialised.sh`, an operator dry-run/commit helper that reads `materialised_state` and asks gostream Vault Mode to prestage existing materialised movies and episodes.
- BREAKING: requires offline migration. Phantom DB schema is now v12 to persist ranked source candidates in `source_candidates`; run `scripts/migrate-source-candidates-v12.sh` with Jellyfin stopped before starting this build.
- Added Phantom source-management APIs and web controls for listing candidates, rejecting the current source, and materialising a selected candidate from stable channel external IDs.
- Made the Phantom source-management controls usable in a mobile browser: the same detail-page section and kebab (…) action-sheet entries run the identical custom-JS shim, now with >=44px touch targets, `touch-action:manipulation`, a full-width stacked layout under a `max-width:600px` media query, a dropped desktop min-width so the candidate `<select>` never overflows a narrow phone, and a 16px `<select>` font so iOS Safari does not focus-zoom. Backed by executable mobile-viewport DOM/API evidence in `tools/rig-scenarios/phantom-kebab-mobile-dom.mjs` (movie + TV episode).
- Favourite saves on Phantom movie/episode channel items now trigger materialisation/prewarm using the existing materialiser pipeline; favouriting a Phantom season or series now materialises every episode in that season or series.
- Favouriting a Phantom movie or series now also grows the catalogue toward the
  user's taste: the title is fanned out to its TMDB "similar" + "recommendations"
  (24h cached), de-duplicated, capped, and folded into the append-only catalogue
  under a distinct favourite-recommendation source, so new movies enqueue
  availability probing and new series enqueue expansion. Episode favourites seed
  from the parent series. Configurable via **Enable favourite recommendations**
  (default on) and **Favourite recommendations max per favourite** (default 40)
  in the Suggestions settings; an admin `POST
  /Plugins/PhantomLibrary/Recommendations/Ingest?tmdbId=&type=` endpoint triggers
  the same ingest manually (REQ-M14-RECOMMENDATIONS).
- Episode source selection now ranks exact `SxxEyy` releases ahead of season/series packs, reducing long materialisation loops through bad pack candidates.
- Phantom DB retention remains deferred/no-op and is now labelled that way in the admin UI instead of presenting an active retention policy.
- Added `scripts/phantom-migrate-v11-to-v12.sh`, an offline operator script that
  performs the v11 → v12 schema bump in place instead of wiping. Because the v12
  delta is purely additive (it only creates `user_prefs`, `user_hidden_items`,
  and `idx_user_hidden_items_user` and touches no existing table), the migrated
  DB is byte-identical to a fresh v12 build. The script is dry-run by default,
  requires `--commit` plus a typed `MIGRATE` confirmation, backs the DB up first
  (with `-wal`/`-shm` sidecars), is `PRAGMA user_version`-guarded (migrates only
  v11, treats v12 as a verified no-op, hard-refuses any other version and directs
  the operator to wipe), applies the DDL + version bump in one atomic
  transaction, verifies the result, and is idempotent/resumable. It mirrors
  `scripts/phantom-wipe.sh` and must be run with `jellyfin.service` stopped; wipe
  remains a valid alternative. Regression-tested by
  `scripts/tests/phantom-migrate-v11-to-v12.test.sh`, wired into the non-rig CI
  gate; permitted by the additive-only carve-out documented in `AGENTS.md`
  § "No database migrations until v1.0".
- Per-user show/hide (REQ-M14-PER-USER 3/4): each Jellyfin user can now hide or
  unhide an individual Phantom title from their own library view — via a **Hide
  from my library** / **Unhide from my library** entry on the detail-page Phantom
  section and the kebab (…) action sheet, for movies and TV series alike
  (title-level; hiding a series also hides its episodes). A hidden title drops out
  of that user's Phantom channel browse only; other users and global/admin state
  are unaffected. Backed by new authenticated `/Plugins/PhantomLibrary/User/*`
  endpoints — the caller's own preferences (`protect_favourites` / `show_phantoms`
  / `allow_eager`) via `GET`/`POST User/Prefs`, and the hidden set via `GET
  User/Hidden` and `GET`/`POST`/`DELETE User/Hidden/{type}/{tmdbId}`. The admin
  per-user preferences page is also restored (it had been temporarily hidden
  pending this API) and now lists and edits every user's toggles over
  `GET`/`POST Plugins/PhantomLibrary/UserPrefs`.
- BREAKING: requires wipe. Phantom DB schema is now v12, adding two additive
  per-user tables — `user_prefs` (one row per Jellyfin user holding the
  `protect_favourites` / `show_phantoms` / `allow_eager` toggles, all defaulting
  on) and `user_hidden_items` (a user's per-title hidden set, keyed
  `(user_id, tmdb_id, type)`). This is schema-only groundwork for
  REQ-M14-PER-USER (branch B); favourite state is not stored here (it stays in
  Jellyfin's own `UserData`), and the read/write accessors land with the
  per-user backend change. Although the delta only adds tables, the plugin still
  ships no runtime migration pre-v1.0: databases at any older schema version are
  hard-refused at startup. Before restarting into this build, either wipe and
  rebuild with `scripts/phantom-wipe.sh` (see `docs/operator-wipe-validation.md`)
  or, when upgrading specifically from v11, run the offline
  `scripts/phantom-migrate-v11-to-v12.sh` (above) to add the tables in place.
- BREAKING: requires wipe. Phantom DB schema is now v11 to split append-only
  TMDB catalogue discovery from source availability. Discovery no longer prunes
  rows simply because TMDB stops returning them, and channel visibility is gated
  by bounded availability probes instead of raw discovery membership.
- Added configurable availability probing cadence/TTLs, mandatory probe leases,
  stale-available visibility, transient-error preservation, and series expansion
  scheduling so large catalogues can be refreshed incrementally without full
  Jellyfin channel cache churn.
- Added perf profiling scripts under `tools/perf/` for discovery, channel browse,
  and materialise flows.
- Added cold-start discovery throttles (`DiscoverPagesPerRun`,
  `DiscoverPageDelayMilliseconds`) with persisted per-kind cursors so post-wipe
  TMDB discovery can fill incrementally instead of stampeding Jellyfin.
- Added Phantom Prometheus metrics on Jellyfin's normal `/metrics` endpoint when
  Jellyfin metrics are enabled.
- Added `SeriesMinAvailableEpisodes` (default `1`) so TV series appear after a
  configurable number of distinct available/materialised episodes; once visible,
  all known episodes in the series are shown.
- Phantom Shows season folders now use TMDB season details when browsing a
  series, adding season poster/overview/air year plus episode availability
  counts, and now emit Jellyfin `Season` channel items so web opens the rich
  native season details view. The web shim prehydrates channel season children
  on season detail pages so the native episode list is populated without
  broadening the Jellyfin patch surface.

### Fixed

- Fixed reject-current re-materialisation so rejection blocks the rejected source by info-hash, not only by exact magnet URL, preventing the same torrent from being immediately selected again through a duplicate tracker/magnet row.
- Fixed reject-current re-materialisation so rejection no longer tries only the first unvalidated alternate candidate; it now rejects the current magnet, clears current state, and runs the normal materialisation pipeline so later valid alternates can be selected.
- Renamed the Phantom kebab rejection item to "Reject current source" and start detail-page polling immediately when reject/materialise actions are submitted so the UI can transition into materialising while the action is still running.
- Fixed Phantom Source and kebab action loading latency by making source/action lookups use cached DB state by default; fresh indexer discovery now runs on materialise or explicit "Refresh sources" instead of blocking every detail-page/poll/action lookup.
- Fixed episode materialisation post-refresh ordering so Jellyfin invalidates stale dynamic media-source cache before probing the new FUSE file, preventing a newly materialised episode from inheriting runtime/size/media-info from a different episode in the same pack.
- Fixed manual materialisation source discovery so materialise always launches a fresh indexer probe while cached candidates validate, adds newly discovered candidates to the queue, and treats transient validation cancellations as short-retry failures instead of poisoning the item for 24 hours.
- Fixed Prowlarr movie discovery to query both IMDb ID and title/year, deduplicating by info hash so title-only tracker results are not missed.
- Added light Phantom detail-page polling after materialise/reset/reject actions to refresh source controls, kebab actions, visible item containers, and trigger one reload when native Jellyfin detail state must be rebuilt.
- Fixed gostream episode validation treating `EpisodeMinBytes` as 1 GiB instead of 1 byte, which incorrectly rejected short animated episodes with `target_episode_not_found` before matching the requested episode file.
- Fixed Phantom Movies/Shows root browse regression introduced by audio stream selection: channel browse now emits unprobed file media sources and reserves FFprobe/audio-stream extraction for playback/media-info paths.
- Patched Jellyfin audio stream selection now honors the user's preferred audio language ahead of the container default track, matching Phantom's selector and preventing non-preferred default tracks from winning at play time.
- Phantom web now lets Jellyfin's native item-page kebab open for Phantom channel items by caching channel DTOs for Jellyfin's `/Users/{userId}/Items/{channelItemId}` refetch (including media-source ids selected by the detail page), then injects only Reset Phantom and Reject current Phantom source into the default command menu.
- Fixed server-advertised item-action routes to accept Jellyfin web's dashless item ids, restoring Phantom kebab actions in the browser shim.
- Phantom materialised/native-open movie and episode media sources now carry probed audio streams from the current gostream/FUSE file so Jellyfin can apply normal audio-language and remembered-track selection.
- Phantom materialisation now validates source candidates through gostream before `/api/library/add`, persists valid/invalid/transient validation state, bounds gostream-heavy validation/add calls, and disables failed candidates in source controls and item actions.
- Added a Reset Phantom operation that clears materialised state and unavailable markers without rejecting the current source, allowing a bad/stale materialisation to be retried through the normal Phantom materialiser after gostream-side picker fixes.
- Fixed the admin settings "Enable availability probing" checkbox markup so
  Jellyfin's `emby-checkbox` initializer can attach and the setting can be toggled.
- TV season browse now displays unknown and unavailable sibling episodes for any
  series that meets the availability threshold; unknown siblings stay normal
  phantoms and unavailable siblings report the `Unavailable` badge state
  (REQ-M14-UNAVAILABLE-UX).
- Indexer probing now distinguishes successful empty 2xx source results from transient failures (transport errors, timeouts, 5xx, malformed upstream responses, and unavailable indexers), preventing transient Prowlarr/Torrentio outages from poisoning availability, unavailable markers, or magnet failure cache state.
- Concurrent materialise/open requests for the same movie or episode now use an atomic in-flight claim, so only one request calls gostream and losers return already-in-progress without deleting the winner's in-flight row.
- Phantom badge visibility settings are enforced server-side: `Off` returns no badge states, `HideForNonAdmins` hides badges from non-admin users, and `AlwaysShow` preserves existing badge behavior.
- Discovery refresh now walks paginated TMDB Discover results up to
  `SuggestionsCatalogueMaxItems`, so Phantom Movies/Shows get thousands
  of catalogue rows instead of only the tiny weekly-trending surface.
- Discovery refresh now stops at TMDB's page-500 Discover limit and writes
  TMDB hit metadata before exposing discovery rows, avoiding cold-row channel
  sweeps during long catalogue refreshes.
- TV episode materialisation now tries ranked magnet candidates in order
  instead of failing after the first gostream rejection. Candidate-specific
  failures such as `no_valid_files`, `target_episode_not_found`, metadata
  timeouts, and missing FUSE paths are negative-cached so later attempts
  skip known-bad magnets and can advance to the next preferred source.
  Transient gostream 5xx errors no longer poison magnet candidates.
- Gostream `/api/library/add` episode handling now selects the largest
  valid video file whose filename/path matches the requested season/episode,
  including `S01E02`, `1x02`, and episode-only names inside `Season 01/`
  pack folders. It returns `422 target_episode_not_found` instead of
  pointing an episode stub at the largest unrelated file in a season/series
  pack. Existing stubs are detected before touching the torrent engine, and
  rejected candidates only remove unreferenced torrent hashes.
- Show season browse now falls back to cached episode rows when TMDB season
  refresh fails or rate-limits, so known episodes do not disappear from a
  season folder during transient TMDB outages. Channel season folders now use
  Jellyfin `Season` items plus web-side child prehydration, so client
  navigation can use the native rich season details view without adding another
  Jellyfin core patch.
- Materialisation no longer persists `materialised_state` for a gostream
  result whose FUSE path never appears; that candidate is marked failed and
  the next ranked magnet is tried instead.
- Phantom movie/episode playback now treats a `materialised_state` row whose
  FUSE file has disappeared as stale: browse/playback falls back to the native
  materialise opener and the next play re-materialises instead of trying to
  stream a dead file path.
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
  episode browse, native-open materialise, immediate second playback from
  the materialised source, real gostream TV playback, and badge state
  separation between series folders and episodes.
- Post-materialise refresh failures now force a media-info cache invalidation
  fallback, so a second play cannot reuse the stale pre-materialise opener.
- `scripts/phantom-wipe.sh` now removes Phantom Movies/Shows channel-cache
  `BaseItems` by channel id, including child rows reached through Jellyfin's
  `ParentId` hierarchy. Prior wipes only targeted path-owned stub/gostream
  rows, so stale channel cache could survive and leave Jellyfin with hundreds
  of thousands of obsolete Phantom episode rows.
- Availability probing now arms its background timer even when disabled at
  startup, so enabling it later in plugin settings starts filling visible
  phantom rows without requiring a Jellyfin restart.
- Phantom Shows now surfaces gostream-only TV files as external
  series/season/episode channel items, including `Season.01` folder parsing,
  instead of only showing episodes already present in `materialised_state`.
- Gostream/external files are tagged as external playable media rather than
  Phantom state-machine items, so badge responses omit them instead of labeling
  them as materialised phantoms.
- Availability scheduling now interleaves series expansion with source probing
  so a large movie backlog cannot starve TV phantom episode creation.
- Phantom channel data-version salt now changes for external-media support,
  forcing Jellyfin to rebuild stale channel cache after install.
- External TV series now use TMDB search/details when available, giving
  gostream-only TV folders normal titles, posters, overviews, genres, and
  TMDB provider IDs instead of raw folder-only metadata.
- External TV episode browse now groups duplicate files for the same SxxExx
  and chooses the best-quality variant, while still showing playable files
  that lack an episode token instead of silently dropping them.
- External TV series now retain Series folder semantics and bump the channel
  data-version salt again so stale raw external folders are rebuilt with TMDB
  metadata on install.
- Availability probing now explicitly alternates episode and movie claims, and
  episode claims advance through a persisted series cursor before wrapping, so
  due TV probes add more shows to the UI without running a full-table grouped
  claim query over million-row episode catalogues every tick.
- Gostream-backed TMDB series now use canonical series/season/episode ids for
  children; existing files direct-play while missing TMDB episodes remain
  visible as phantoms.
- External TV metadata lookup now uses already-cached TMDB metadata instead of
  performing live TMDB searches during channel browse, avoiding slow/spinning
  Phantom Shows loads.

### BREAKING — requires wipe + patched Jellyfin

- BREAKING: requires wipe. Phantom DB schema is now v11 to add append-only
  `catalogue_items`, `availability_items`, `series_expansion_state`, and
  `series_episode_catalogue` tables used by the bounded availability scheduler;
  follow `docs/operator-wipe-validation.md` / `scripts/phantom-wipe.sh`
  before installing this build.

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

`phantom.db` schema bumped to v11 (was v5 in v0.2.0.0). The new
schema captures channel-item registrations, per-item materialise
state, candidate-level magnet failure caching, append-only TMDB catalogue
membership, series expansion state, and bounded source availability
bookkeeping.
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
- **`phantom.db` schema v10.** Channel-item registration + per-item
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
