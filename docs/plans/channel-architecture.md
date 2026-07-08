# Phantom Library as IChannel + targeted ChannelManager patch (architectural plan v2)

> **⚠ Historical planning document — SUPERSEDED. The M14 IChannel
> architecture has SHIPPED.** This is a design-context draft (v2); it is
> retained for history and is NOT the current state of the code. The
> authoritative record of what M14 implemented is
> `docs/plans/m14-ledger-evaluation.md` (the ledger). Two cautions when
> reading below: (1) the plugin-design snippets that emit a **splash
> MediaSource for phantoms as the normal playback path** are superseded —
> the shipped design uses native `RequiresOpening` / `OpenMediaSource`
> materialise-on-play, and splash is **legacy/support only** per the
> ledger (REQ-M14-SPLASH). The accurate, as-shipped contract is the
> "Native playback contract" under § "Post-implementation contracts (v0.3
> hardening)" in this same file. (2) Canonical stub naming is
> `[tmdbid-<id>]`; treat any `__phantom_tmdb<id>` reference as deprecated
> (AGENTS.md § "Canonical phantom stub naming scheme"). Do not implement
> from this document.

Date: 2026-06-09 (v2 after critic review of v1)
Status: **DRAFT v2 — awaiting critic review**
Author: agent

Replaces:
- `docs/plans/channel-architecture.md` (v1; left in repo for history,
  banner-flagged as obsolete on this plan's merge)
- `docs/plans/scanner-race-reactor.md` (also superseded)

## Problem (recap)

The plugin's current architecture has two writers competing for ownership
of phantom-tree `BaseItems`: the plugin's `CreateItem` calls and
Jellyfin's library scanner. The race produces orphan rows, missing
badges, and an accreting wall of workarounds (`IsLocked` re-stamp,
`HealBrokenPhantomAsync`, `PhantomImageProvider`,
`PhantomStatusDecorator`, custom collection folder binder).

`IChannel` is the only sanctioned Jellyfin extension point for
plugin-supplied items that don't sit on disk as files the scanner can
walk. However, the v1 plan's "channels solve everything cleanly"
framing was wrong: critic review identified five concrete BLOCKERs in
`ChannelManager` that prevent the materialise-via-MediaSource-swap UX
from working.

This plan accepts that **a small Jellyfin patch is required** to make
channels work for our use case, and specifies that patch concretely
alongside the plugin architecture. Operator has confirmed:

- Mobile + TV client support is required (so JS-shim-only is out)
- Web + native clients must all see phantom + real items unified
- Patching Jellyfin is acceptable; the operator already
  builds-and-installs Jellyfin from a source clone via `install.sh`

## Decision

Replace the operator's existing `gostream-movies` and `gostream-shows`
`CollectionFolder` libraries with `IChannel`-backed equivalents
(`PhantomMoviesChannel`, `PhantomShowsChannel`) implemented by this
plugin. Each channel:

- Lists **real gostream files** enumerated from
  `/var/gostream/gostream-mkv-virtual/{movies,tv}/` directly via
  filesystem walk
- Lists **phantom items** synthesised lazily from a plugin-internal
  discovery cache backed by TMDB
- Merges both into one unified channel browse view

Ship the plugin alongside a targeted patch to Jellyfin's
`ChannelManager` (~65 lines C# diff) that adds the
per-item refresh primitive the materialise flow requires. Patch is
shipped as a `.patch` file in `scripts/jellyfin-patches/` and applied
at install time against the source clone in `jellyfin/`.

The accepted UX consequences (from the v1 plan, unchanged):

- Loss of `CollectionType.movies` / `tvshows` Home rows
- Loss of Dashboard → Libraries management for gostream content
- Per-user visibility via `User.Policy.EnabledChannels`
- Channel-flavoured library icon

What this buys:

- Zero stub files on disk for phantoms (lazy synthesis)
- Zero scanner race (channel owns BaseItem lifecycle)
- Real + phantom items unified
- Materialisation as a MediaSource swap via the patched refresh API
- All-client support (mobile, TV apps) because the architecture is
  server-side standard
- ~50% net plugin code reduction (only this time honestly measured
  against the critic's mitigations)

## Post-implementation contracts (v0.3 hardening)

These contracts were established during the v0.3 production hardening
session after the basic channel design shipped. Keep them as design
constraints for future channel work.

### Native playback contract

Phantom playback must use Jellyfin's native media-source opening flow,
not a finite splash video as the normal path.

For an unmaterialised playable item (movie or episode), `PlaybackInfo`
should expose exactly one source with:

- `RequiresOpening = true`
- `OpenToken = <provider-prefix>_phantom:<ChannelItemId>`
- `Path = ""` (no splash file path)
- Guid-shaped `MediaSourceInfo.Id`

When a client posts `PlaybackInfo` with `AutoOpenLiveStream=true`, the
plugin's `IMediaSourceProvider.OpenMediaSource` implementation must:

1. Start materialisation with trigger `Play`.
2. Wait for `materialised_state` and the real FUSE file.
3. Return a real file `MediaSourceInfo` with `RequiresOpening=false`.
4. Preserve one-source semantics: no duplicate splash/static/dynamic
   sources beside the real file.
5. Ensure the returned stream opens with non-zero bytes.

Reason: native TV/mobile clients cannot be reliably forced to stop a
currently playing splash video and auto-switch to a newly materialised
file. The only server-side UX that maps to native clients is: do not
start playback until the real file is ready; let the client show its
native loading indicator during `OpenMediaSource`.

### Channel cache invalidation contract

Jellyfin caches channel provider output by `IChannel.DataVersion`.
Any change to channel output shape must invalidate that cache. This
includes changes to:

- `ChannelItemInfo.Id` / `ExternalId`
- `Tags`
- `ProviderIds`
- `MediaSources`
- path semantics
- phantom/materialised/orphan grouping
- real-vs-phantom item selection

If a code change alters any of those contracts, bump the channel
DataVersion salt or otherwise force DataVersion to change. Do not rely
on restart, install, or scheduled-task timing to flush stale channel
JSON. Stale channel cache has already surfaced as lingering orphan
items and splash media sources after the provider logic was fixed.

### Badge / client UI contract

Browser badges are advisory UI on top of server truth. The server state
endpoint must only return badge states for playable, materialise-capable
items:

- movies
- episodes

It must omit navigation/container rows:

- series folders
- season folders
- unknown/orphan containers unless explicitly designed otherwise

Client badge JS must be idempotent under Jellyfin's DOM churn:

- Do not remove/reinsert an unchanged badge on every MutationObserver
  callback.
- Ignore mutations caused solely by `.phantom-badge` elements.
- Poll only visible `Phantom` / `Virtual` / `Materialising` items.
- Stop polling when items leave the visible DOM or reach a terminal
  state.

Reason: the first polling implementation repeatedly removed and
reinserted the detail-page badge, triggering its own MutationObserver
forever; a single item page pinned CPU and exhausted browser memory.

### Gostream path contract

Gostream-returned paths must be normalized to host-visible configured
roots before they are persisted or emitted:

- movies: `PluginConfiguration.GostreamMoviesRoot`
- episodes: `PluginConfiguration.GostreamShowsRoot`

The plugin must handle gostream returning container-internal paths such
as `/mnt/gostream-mkv-virtual/...` when the host-visible path is under
`/var/gostream/gostream-mkv-virtual/...`. Use configured roots plus
filename/search fallback; do not persist unreachable container paths in
`materialised_state` or Jellyfin `BaseItems.Path`.

Existing gostream files that TMDB-match discovery rows are
materialised-equivalent for playback and badges, but they must not get
fake `materialised_state` rows: eviction/removal semantics depend on
`materialised_state.stub_path` referring to plugin-created gostream
registrations.

Movie and TV paths must both be covered. Movie-only tests are
insufficient; the TV episode path has distinct TMDB, IMDB, season,
episode, FUSE-root, and channel-folder behavior.

## Part 1: the Jellyfin patch

### File: `src/Jellyfin.LiveTv/Channels/ChannelManager.cs`

#### Patch §A — make `GetChannelItemEntityAsync` accept explicit `forceUpdate` and `forceProbe`

Current signature (line 957):
```csharp
private async Task<BaseItem> GetChannelItemEntityAsync(
    ChannelItemInfo info,
    IChannel channelProvider,
    Guid internalChannelId,
    BaseItem parentFolder,
    CancellationToken cancellationToken)
```

Modified signature:
```csharp
private async Task<BaseItem> GetChannelItemEntityAsync(
    ChannelItemInfo info,
    IChannel channelProvider,
    Guid internalChannelId,
    BaseItem parentFolder,
    bool forceUpdate,
    bool forceProbe,
    CancellationToken cancellationToken)
```

Existing call site (in `GetChannelItemsInternal`, line ~755) passes
`forceUpdate: false, forceProbe: false` — preserves legacy behaviour
for the normal channel-scan flow.

Inside the method, three behaviour changes:

1. **Probe-pinning bypass** (line 1001 today):
   ```csharp
   else if (isNew || !enableMediaProbe)
       item.RunTimeTicks = info.RunTimeTicks;
   ```
   Change to:
   ```csharp
   else if (isNew || !enableMediaProbe || forceProbe)
       item.RunTimeTicks = info.RunTimeTicks;
   ```

2. **forceUpdate propagation** — the local `forceUpdate` variable
   already exists (computed from DateModified / ChannelId mismatches).
   At its declaration site, OR with the new parameter:
   ```csharp
   var forceUpdate = forceUpdateParam;   // initialised to caller's value
   ```

3. **Trigger a metadata re-refresh when forceProbe is set**
   (line ~1175 today):
   ```csharp
   if (isNew || forceUpdate || item.DateLastRefreshed == DateTime.MinValue)
   {
       _providerManager.QueueRefresh(item.Id,
           new MetadataRefreshOptions(new DirectoryService(_fileSystem)),
           RefreshPriority.Normal);
   }
   ```
   Change to:
   ```csharp
   if (isNew || forceUpdate || forceProbe || item.DateLastRefreshed == DateTime.MinValue)
   {
       var refreshOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem));
       if (forceProbe)
       {
           refreshOptions.EnableRemoteContentProbe = true;
           refreshOptions.MetadataRefreshMode = MetadataRefreshMode.FullRefresh;
       }
       _providerManager.QueueRefresh(item.Id, refreshOptions, RefreshPriority.Normal);
   }
   ```

#### Patch §B — add `RefreshChannelItemAsync` public API

To `MediaBrowser.Controller/Channels/IChannelManager.cs`:

```csharp
/// <summary>
/// Forces a single channel item to be re-fetched from the channel
/// provider and its persisted BaseItem state (Path, MediaSources,
/// probe data) refreshed accordingly.
///
/// Used by plugin-side workflows that mutate a channel item's
/// underlying media source independently of the regular channel scan
/// (e.g. materialise-on-demand pipelines where a phantom placeholder
/// is replaced by a real file).
/// </summary>
/// <param name="channelId">The internal Channel BaseItem id.</param>
/// <param name="channelItemExternalId">
///   The `ChannelItemInfo.Id` (external id) of the item to refresh.
/// </param>
/// <param name="forceUpdate">Force persistence of Path/MediaSources
///   even if Jellyfin's normal forceUpdate heuristics didn't trigger.</param>
/// <param name="forceProbe">Force re-probe of streams + runtime by
///   bypassing the `isNew || !enableMediaProbe` guard.</param>
/// <param name="cancellationToken">CT.</param>
Task RefreshChannelItemAsync(
    Guid channelId,
    string channelItemExternalId,
    bool forceUpdate = true,
    bool forceProbe = true,
    CancellationToken cancellationToken = default);
```

Implementation in `ChannelManager.cs` (new method, ~50 lines):

```csharp
public async Task RefreshChannelItemAsync(
    Guid channelId,
    string channelItemExternalId,
    bool forceUpdate = true,
    bool forceProbe = true,
    CancellationToken cancellationToken = default)
{
    ArgumentException.ThrowIfNullOrEmpty(channelItemExternalId);

    var channelEntity = _libraryManager.GetItemById(channelId) as Channel
        ?? throw new InvalidOperationException(
            $"Channel BaseItem {channelId} not found or wrong type");

    var provider = GetChannelProvider(channelEntity)
        ?? throw new InvalidOperationException(
            $"No IChannel provider registered for channel {channelId}");

    // Resolve the fresh ChannelItemInfo. Prefer a targeted lookup if
    // the channel implements the optional IChannelItemRefresh interface
    // (see new optional interface, Patch §C). Otherwise walk
    // GetChannelItems pages to find it.
    ChannelItemInfo info = null;
    if (provider is IChannelItemRefresh targeted)
    {
        info = await targeted.GetChannelItemAsync(
            channelItemExternalId, cancellationToken)
            .ConfigureAwait(false);
    }
    else
    {
        // Fallback: page through GetChannelItems at the channel root.
        // Acceptable for small channels; expensive for large ones.
        var query = new InternalChannelItemQuery
        {
            ChannelId = channelId,
            FolderId = Guid.Empty,
            StartIndex = 0,
            Limit = int.MaxValue
        };
        var result = await provider.GetChannelItems(query, cancellationToken)
            .ConfigureAwait(false);
        info = result.Items?.FirstOrDefault(
            i => string.Equals(i.Id, channelItemExternalId,
                StringComparison.Ordinal));
    }

    if (info is null)
    {
        _logger.LogDebug(
            "RefreshChannelItem: channel {ChannelId} no longer owns item {ExternalId}; skipping",
            channelId, channelItemExternalId);
        return;
    }

    await GetChannelItemEntityAsync(
        info,
        provider,
        channelId,
        channelEntity,
        forceUpdate: forceUpdate,
        forceProbe: forceProbe,
        cancellationToken).ConfigureAwait(false);

    // Invalidate the 5-minute MediaSource cache so the next play
    // call invokes GetChannelItemMediaInfo fresh.
    var hashInput = GetIdToHash(channelItemExternalId, provider.Name);
    // GetChannelItemMediaSourcesInternal keys on the BaseItem.ExternalId,
    // which equals the ChannelItemInfo.Id we already have. The cache key
    // in _memoryCache.TryGetValue(id, ...) at line 410 is that external id.
    _memoryCache.Remove(channelItemExternalId);
}
```

#### Patch §C — optional `IChannelItemRefresh` interface

To `MediaBrowser.Controller/Channels/IChannelItemRefresh.cs` (NEW):

```csharp
namespace MediaBrowser.Controller.Channels;

/// <summary>
/// Optional contract a plugin's IChannel implementation can implement
/// to support efficient single-item lookup for `RefreshChannelItemAsync`.
/// Channels that do not implement this fall back to a full page walk.
/// </summary>
public interface IChannelItemRefresh
{
    /// <summary>
    /// Return the current <see cref="ChannelItemInfo"/> for a single
    /// external id, or null if the channel no longer surfaces that item.
    /// </summary>
    Task<ChannelItemInfo?> GetChannelItemAsync(
        string channelItemExternalId,
        CancellationToken cancellationToken);
}
```

PhantomMoviesChannel and PhantomShowsChannel implement this.

#### Patch §D — wire `RefreshChannelItemAsync` into DI

`MediaBrowser.Controller/Channels/IChannelManager.cs` already
registered as a service. Adding a method to an existing interface
requires no DI changes. Patches §A through §C land together.

#### Patch §E — tests in Jellyfin's own test project

In `tests/Jellyfin.Server.Implementations.Tests/Channels/`:

- `RefreshChannelItemAsync_ChannelNotFound_Throws`
- `RefreshChannelItemAsync_ItemRemovedFromChannel_NoOp`
- `RefreshChannelItemAsync_WithIChannelItemRefresh_UsesTargetedLookup`
- `RefreshChannelItemAsync_WithoutIChannelItemRefresh_FallsBackToPaging`
- `RefreshChannelItemAsync_ForceUpdate_PersistsPathChange` (this is
  the BLOCKER 2 regression test — without the patch, Path doesn't
  persist)
- `RefreshChannelItemAsync_ForceProbe_TriggersFullRefresh`
- `RefreshChannelItemAsync_InvalidatesMediaInfoCache` (BLOCKER 5)

#### Total patch size

| File | Lines added | Lines modified |
|---|---|---|
| `IChannelManager.cs` | +30 (interface method + xmldocs) | 0 |
| `IChannelItemRefresh.cs` (NEW) | +20 | 0 |
| `ChannelManager.cs` | +55 (RefreshChannelItemAsync) | +6 (GetChannelItemEntityAsync param + probe guard + refresh-options) |
| Tests | +200 | 0 |
| **Total** | **+305** | **+6** |

#### Patch deployment

Patches live in `scripts/jellyfin-patches/` as conventional `.patch`
files generated via `git format-patch`. `install.sh` extends to:

```bash
# After source-clone update, before build:
for patch in scripts/jellyfin-patches/*.patch; do
    (cd jellyfin && git apply --check "$patch") || die "patch $patch will not apply cleanly; rebase needed"
done
for patch in scripts/jellyfin-patches/*.patch; do
    (cd jellyfin && git am "$patch")
done

# Then build Jellyfin from source per existing install.sh flow.
```

If any patch fails to apply (Jellyfin upstream changed the patch
context), `install.sh` aborts with the operator-actionable error
"patch needs rebase against current Jellyfin source; see
`scripts/jellyfin-patches/REBASE.md`."

#### Maintenance burden + upstream plan

- Patch targets a stable file (`ChannelManager.cs` changes
  occasionally for new content types; surrounding context around
  `GetChannelItemEntityAsync` has been stable since Jellyfin
  10.8).
- On each Jellyfin minor version bump, run `install.sh --build` in
  the rig; if the patch fails to apply, rebase manually and
  re-export via `git format-patch`.
- **Upstream PR**: open against `jellyfin/jellyfin` immediately
  after merging here. PR includes Patch §A + §B + §C + §E. If/when
  upstream accepts, we delete `scripts/jellyfin-patches/` and
  bump the minimum supported Jellyfin version.
- Document the patch dependency prominently in `README.md` and
  `AGENTS.md`. The repo is no longer compatible with stock
  Jellyfin until the patch lands or is restored.

## Part 2: plugin architecture

### Channel implementations

#### `Channels/PhantomMoviesChannel.cs` (NEW)

```csharp
public sealed class PhantomMoviesChannel
    : IChannel,
      IRequiresMediaInfoCallback,
      ISupportsLatestMedia,
      IChannelItemRefresh   // from patched Jellyfin
{
    public string Name => "Phantom Movies";    // HARDCODED — never expose as setting
    public string Description => "...";
    public string DataVersion => _state.GetDataVersion("movies");
    public string HomePageUrl => "https://www.themoviedb.org/";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    public ChannelFeatures GetChannelFeatures() => new ChannelFeatures
    {
        ContentTypes = new[] { ChannelMediaContentType.Movie },
        MediaTypes = new[] { ChannelMediaType.Video },
        MaxPageSize = 500,
        SupportsSortOrderToggle = true,
        DefaultSortOrders = new[] { ChannelItemSortField.Name, ChannelItemSortField.DateCreated },
    };

    public Task<ChannelItemResult> GetChannelItems(
        InternalItemsQuery query, CancellationToken ct)
    {
        // Movies channel is flat: only top-level. No folder navigation.
        if (query.FolderId is not null && !query.FolderId.Equals(default))
        {
            return Task.FromResult(new ChannelItemResult { Items = Array.Empty<ChannelItemInfo>() });
        }

        return GetTopLevelMoviesAsync(query, ct);
    }

    public Task<ChannelItemInfo?> GetChannelItemAsync(
        string channelItemExternalId, CancellationToken ct)
        => _itemBuilder.BuildOneAsync(channelItemExternalId, ct);
        // _itemBuilder maintains an in-memory snapshot of "what should this channel return for this id"
        // populated lazily from the discovery cache + gostream filesystem state

    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(
        string id, CancellationToken ct)
        => _itemBuilder.GetMediaSourceForIdAsync(id, ct);
        // returns splash MediaSource for phantoms / fuse-path MediaSource for materialised
}
```

#### `Channels/PhantomShowsChannel.cs` (NEW)

Same shape, plus folder navigation for Series → Seasons → Episodes:

```csharp
public Task<ChannelItemResult> GetChannelItems(InternalItemsQuery query, CancellationToken ct)
{
    if (query.FolderId is null or default)
        return _itemBuilder.GetTopLevelSeriesAsync(query, ct);

    var parsed = ChannelItemId.Parse(query.FolderId.ToString());
    if (parsed.IsSeries)
        return _itemBuilder.GetSeasonsForSeriesAsync(parsed.SeriesTmdbId, ct);
    if (parsed.IsSeason)
        return _itemBuilder.GetEpisodesForSeasonAsync(
            parsed.SeriesTmdbId, parsed.SeasonNumber, ct);

    return Task.FromResult(ChannelItemResult.Empty);
}
```

### State schema

`State/PhantomDb.cs` — replace existing schema (BREAKING-wipe per
AGENTS.md):

```sql
CREATE TABLE discovery_cache (
    tmdb_id        INTEGER NOT NULL,
    type           TEXT NOT NULL,        -- 'movie' or 'series'
    discovered_at  INTEGER NOT NULL,     -- unix ts
    last_refreshed INTEGER NOT NULL,
    PRIMARY KEY (tmdb_id, type)
);
CREATE INDEX idx_discovery_cache_last_refreshed
    ON discovery_cache(last_refreshed);

CREATE TABLE materialised_state (
    tmdb_id        INTEGER NOT NULL,
    type           TEXT NOT NULL,        -- 'movie' or 'episode'
    season         INTEGER,              -- null for movies
    episode        INTEGER,              -- null for movies
    stub_path      TEXT NOT NULL,        -- gostream-returned path (for RemoveAsync)
    fuse_path      TEXT NOT NULL,        -- the FUSE mount path (BaseItem.Path source)
    materialised_at INTEGER NOT NULL,
    PRIMARY KEY (tmdb_id, type, season, episode)
);
CREATE INDEX idx_materialised_state_type ON materialised_state(type);
CREATE INDEX idx_materialised_state_materialised_at
    ON materialised_state(materialised_at);

CREATE TABLE materialise_in_flight (
    tmdb_id        INTEGER NOT NULL,
    type           TEXT NOT NULL,
    season         INTEGER,
    episode        INTEGER,
    started_at     INTEGER NOT NULL,
    PRIMARY KEY (tmdb_id, type, season, episode)
);

CREATE TABLE tmdb_external_ids (
    tmdb_id        INTEGER NOT NULL,
    type           TEXT NOT NULL,        -- 'movie' or 'series'
    imdb_id        TEXT,
    fetched_at     INTEGER NOT NULL,
    PRIMARY KEY (tmdb_id, type)
);

-- existing tables kept:
CREATE TABLE tmdb_cache (...);   -- title/overview/year/etc
CREATE TABLE plugin_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
```

Deleted (gone with the architecture):
- `phantom_items` (replaced by `discovery_cache` + `materialised_state`)
- `materialisation_log` (use logger; no DB row needed)
- `autopilot_state` (replaced by reading per-user UserData via Jellyfin API)

### Discovery refresh

`Channels/DiscoveryRefreshTask.cs` (NEW, replaces `SuggestionsRefreshTask`):

- Pulls TMDB Trending (movie + tv) and per-user Recommended
- INSERTs into `discovery_cache` with `last_refreshed = now`
- TTL eviction: `DELETE FROM discovery_cache WHERE last_refreshed < now - 30 days AND tmdb_id NOT IN (SELECT tmdb_id FROM materialised_state)`
- Bumps channel `DataVersion` via `_state.BumpDataVersion("movies")` / `("shows")`
- Default interval: 6h (same as today)

Discovery is global (not per-user). At channel-query time, the
listener can optionally filter by `query.UserId` if we want to keep
the per-user-Recommended distinction; current plan: surface all
discovered items to all enabled users.

### Materialisation flow

#### User-driven (kebab → API)

1. User clicks Materialise button on a phantom item (kebab UI unchanged)
2. `phantomKebab.js` sends `POST /Plugins/PhantomLibrary/Materialise` with the channel item Id (which encodes `(tmdb, type, season?, episode?)`)
3. API controller parses the id, looks up TMDB metadata + IMDB id (via `tmdb_external_ids` cache; fetches from TMDB on miss), calls `Materialiser.MaterialiseAsync(tmdbId, type, season, episode, ct)`
4. `Materialiser`:
   a. Inserts row into `materialise_in_flight`
   b. Calls `_gostream.AddAsync(GostreamAddRequest{...})` with TMDB id, IMDB id, season, episode, magnet (chosen via existing SourcePicker logic)
   c. Awaits FUSE path settlement
   d. INSERTs into `materialised_state` with `stub_path` (returned by gostream) + `fuse_path`
   e. DELETEs from `materialise_in_flight`
   f. Bumps channel `DataVersion`
   g. Calls **patched** `_channelManager.RefreshChannelItemAsync(channelId, channelItemExternalId, forceUpdate=true, forceProbe=true)`
5. ChannelManager re-invokes `channel.GetChannelItemAsync(externalId)`; channel returns fresh `ChannelItemInfo` whose `MediaSources` now points at the FUSE path
6. ChannelManager persists Path + MediaSources via `forceUpdate` path; triggers full re-refresh with remote-probe via `forceProbe` path
7. Probe runs against the real file; streams + RunTimeTicks update
8. 5-minute `_memoryCache` invalidated by the patch
9. User clicks Play → real file plays with correct streams

#### Autopilot-driven (continue playing → next-up)

Same flow as today, plus the splash-trip-protection:

```pseudocode
on UserDataSaved(itemId, userData):
    if userData.PlayedPercentage < 80: return
    if item.RunTimeTicks < TimeSpan.FromMinutes(2).Ticks: return   # SPLASH GUARD
    if item.Path equals splash file: return                         # belt + braces

    # Resolve channel item Id → (tmdb, season, episode)
    parsed = ChannelItemId.Parse(item.ExternalId)
    if parsed is not Episode: return

    nextEpisodes = await ComputeNextUpAsync(parsed, prefetch=2)
    foreach (next in nextEpisodes):
        if materialised_state.has(next): continue
        if materialise_in_flight.has(next): continue
        Materialiser.MaterialiseAsync(next.tmdb, "episode", next.season, next.episode, MaterialiseTrigger.Autopilot)
```

The splash guard kills the v1-critic-flagged "5-second splash trips 80%
threshold → materialise storm" issue.

### Materialiser refactor

`Materialisation/Materialiser.cs` — new signature added; old kept as a
thin wrapper:

```csharp
public async Task<MaterialisationOutcome> MaterialiseAsync(
    int tmdbId, string type,
    int? season, int? episode,
    MaterialiseTrigger trigger,
    CancellationToken ct)
{
    // Resolve IMDB id (required for gostream episode requests).
    var imdb = await _tmdbExternalIds.GetImdbIdAsync(
        tmdbId,
        type == "episode" ? "series" : type,
        ct).ConfigureAwait(false);

    if (type == "episode" && string.IsNullOrEmpty(imdb))
    {
        // Hard requirement; gostream rejects type=episode without series_imdb.
        return new MaterialisationOutcome
        {
            Status = MaterialisationStatus.Error,
            Error = $"Could not resolve IMDB id for series tmdb={tmdbId}"
        };
    }

    // Insert in-flight marker.
    await _db.UpsertMaterialiseInFlightAsync(tmdbId, type, season, episode, ct);
    try
    {
        // ... existing gostream + FUSE-wait logic, parameterised on the tuple ...
        var addResult = await _gostream.AddAsync(new GostreamAddRequest
        {
            Type = type,
            Tmdb = tmdbId,
            Imdb = imdb,
            Title = title,
            Year = year,
            Season = season,
            Episode = episode,
            SeriesImdb = type == "episode" ? imdb : null,
            Magnet = chosenMagnet,
            ...
        }, ct);

        await _db.InsertMaterialisedStateAsync(
            tmdbId, type, season, episode,
            stubPath: addResult.StubPath,
            fusePath: addResult.FusePath,
            ct);

        // Bump channel data version + trigger per-item refresh
        var channelId = ChannelIds.For(type == "movie" ? "movies" : "shows");
        var externalId = ChannelItemId.Encode(tmdbId, type, season, episode);
        await _channelManager.RefreshChannelItemAsync(
            channelId, externalId,
            forceUpdate: true, forceProbe: true,
            ct).ConfigureAwait(false);

        return MaterialisationOutcome.Success(addResult.FusePath, addResult.StubPath);
    }
    finally
    {
        await _db.DeleteMaterialiseInFlightAsync(tmdbId, type, season, episode, ct);
    }
}

// Legacy Guid-based signature (used by existing PlaybackTriggerListener):
public Task<MaterialisationOutcome> MaterialiseAsync(
    Guid jellyfinItemId, MaterialiseTrigger trigger, CancellationToken ct)
{
    var item = _libraryManager.GetItemById(jellyfinItemId);
    if (item is null) return Task.FromResult(MaterialisationOutcome.NotFound);

    var parsed = ChannelItemId.Parse(item.ExternalId);
    return MaterialiseAsync(parsed.TmdbId, parsed.Type, parsed.Season, parsed.Episode, trigger, ct);
}
```

`PromoteItemAsync` (today's in-place BaseItem.Path mutation + parent
re-parenting + UpdateItemAsync) is **deleted**. The patched
`RefreshChannelItemAsync` does its job.

### EvictionSweeper

`Materialisation/EvictionSweeper.cs` — keyed off `materialised_state`,
NOT off gostream filesystem walks (the v1 critic's IMPORTANT 8 fix):

```pseudocode
async RunOnceAsync(ct):
    rows = await _db.ListMaterialisedStateAsync(ct)
    foreach row in rows:
        if row.materialised_at < now - evictionIdleDays AND not favourited(row):
            await _gostream.RemoveAsync(row.stub_path, ct)  # uses the stub_path we stored
            await _db.DeleteMaterialisedStateAsync(row.tmdb_id, row.type, row.season, row.episode, ct)
            await _channelManager.RefreshChannelItemAsync(channelId, ChannelItemId.Encode(...), forceUpdate=true, forceProbe=false, ct)
        # Next channel browse will see the file gone + materialised_state gone → renders as phantom
```

`favourited(row)`: walks Jellyfin UserData for the channel item Id;
returns true if any user has marked it favourite. Reads-only against
Jellyfin DB; no race.

### Real-file enumeration (no gostream API change required)

`Channels/GostreamFilesystemEnumerator.cs` (NEW):

Walks `/var/gostream/gostream-mkv-virtual/{movies,tv}/` and emits one
`ChannelItemInfo` per file. **Does NOT depend on a new gostream API or
sidecar files** — uses materialised_state as the authoritative source
of "we materialised this":

```pseudocode
async EnumerateMoviesAsync(ct):
    # Source of truth: materialised_state rows we wrote at materialise time
    # already carry both fuse_path AND tmdb_id. Just read them.
    foreach row in await _db.ListMaterialisedStateAsync(type="movie", ct):
        if File.Exists(row.fuse_path):
            metadata = await _tmdbCache.GetMovieAsync(row.tmdb_id, ct)
            yield new ChannelItemInfo {
                Id = ChannelItemId.Encode(row.tmdb_id, "movie"),
                ...metadata...,
                MediaSources = [ new MediaSourceInfo {
                    Path = row.fuse_path, Container = "mkv", ...
                } ]
            }
        # If file doesn't exist but row does: orphan. Log + DELETE the row on sweep.

    # ALSO enumerate gostream files NOT in materialised_state
    # (manually-added or pre-existing gostream content).
    # For these we don't have a tmdb_id, but the operator may want them visible.
    # Use a best-effort filename → TMDB title-search fallback.
    foreach file in Directory.EnumerateFiles(gostreamMoviesPath):
        if NOT _db.IsInMaterialisedState(file):
            yield new ChannelItemInfo {
                Id = $"orphan_{Hash(file)}",                  # synthetic id, stable per path
                Name = Path.GetFileNameWithoutExtension(file),  # raw filename as best name
                ...
            }
```

The "raw filename" fallback is intentionally lo-fi: the operator can
opt into TMDB enrichment via plugin settings (off by default to avoid
TMDB rate-limit on enumeration). Manually-added gostream content is a
minor case; the operator's day-to-day workflow goes through phantom
materialisation, which writes the tmdb_id in `materialised_state`.

This sidesteps the v1 critic's Open Q §1 ("gostream sidecar
dependency") — we never needed it because we control the metadata
writes ourselves at materialise time.

### Badge UI (`phantomBadges.js`)

Today: queries `/States` with BaseItem GUIDs.

Under channels: BaseItem GUIDs are derived from the deterministic
channel item external ids. The controller's lookup becomes:

```pseudocode
foreach guid in request.Ids:
    baseItem = _libraryManager.GetItemById(guid)
    if baseItem.SourceType != SourceType.Channel: continue          # not ours
    if baseItem.ChannelId not in PhantomChannelIds: continue        # other channel
    parsed = ChannelItemId.Parse(baseItem.ExternalId)
    if materialised_state.has(parsed): state = "Materialised"
    elif materialise_in_flight.has(parsed): state = "Materialising"
    else: state = "Phantom"
    yield (guid, state)
```

The JS shim is unchanged. Same DOM decoration logic.

### Plugin config (`Configuration/PluginConfiguration.cs`)

```csharp
public class PluginConfiguration : BasePluginConfiguration
{
    // Gostream
    public string GostreamMoviesPath { get; set; } = "/var/gostream/gostream-mkv-virtual/movies";
    public string GostreamTvPath     { get; set; } = "/var/gostream/gostream-mkv-virtual/tv";
    public string GostreamApiBaseUrl { get; set; } = "http://localhost:9080";

    // TMDB
    public int SuggestionsRefreshIntervalHours { get; set; } = 6;
    public int DiscoveryCacheTtlDays           { get; set; } = 30;
    public bool EnrichOrphanGostreamItemsViaTmdbSearch { get; set; } = false;

    // Eviction
    public int EvictionIdleDays   { get; set; } = 90;
    public bool ProtectFavourites { get; set; } = true;

    // Channels
    public bool ShowMoviesChannel { get; set; } = true;
    public bool ShowShowsChannel  { get; set; } = true;

    // NOTE: no channel display name settings. Names hardcoded
    // ("Phantom Movies" / "Phantom Shows") because rename ⇒
    // BaseItem.Id derivation changes ⇒ UserData orphan. See
    // docs/plans/channel-architecture.md for rationale.
}
```

No `EnableChannelsForAllUsers` checkbox — Jellyfin's default
`UserPolicy.EnableAllChannels = true` already handles this for the
admin user (the v1 critic's drift note). For multi-user setups,
operator uses standard Dashboard → Users → Access page.

### `EagerResolver` deletion

The plugin's `EagerResolver` subscribed to `ItemAdded` to pre-resolve
magnets for phantom items the user was likely to play. Under
channels, `ItemAdded` doesn't fire for channel items in the same
way (the explore agent confirmed channel items are persisted but not
via the regular `CreateItem` path). Eager pre-resolution is dropped.

If pre-resolution becomes important later, the right hook is
subscribing to `UserDataSavedListener` for `IsFavorite` toggles —
when a user favourites a phantom, the plugin can opportunistically
fetch a magnet ahead of time. Out of scope for this PR.

## What goes away

Comprehensive deletion list.

| Component | Status |
|---|---|
| `Library/PhantomStubManager.cs` | DELETE |
| `Library/PhantomCollectionFolderBinder.cs` | DELETE |
| `Library/PhantomPathUtilities.cs` | DELETE |
| `Library/SeriesIngestor.cs` | DELETE |
| `Library/SuggestionsContributor.cs` | DELETE; replaced by `Channels/DiscoveryRefreshTask.cs` |
| `Library/VirtualItemFactory.cs` | DELETE |
| `Library/VirtualLibraryRoot.cs` | DELETE |
| `Library/CachedTmdbReader.cs` | KEEP |
| `PhantomBootstrapService.cs` | DELETE |
| `Providers/PhantomImageProvider.cs` | DELETE |
| `Playback/PhantomMediaSourceProvider.cs` | DELETE |
| `Playback/PhantomStatusDecorator.cs` | DELETE |
| `Playback/SplashStream.cs` | KEEP |
| `Materialisation/EagerHintSink.cs` | DELETE |
| `Materialisation/EagerResolver.cs` | DELETE |
| `Materialisation/UserDataSavedListener.cs` | KEEP, refactor for channel item ids |
| `Materialisation/PlaybackTriggerListener.cs` | KEEP, refactor for channel item ids + splash guard |
| `Materialisation/MaterialisationQueue.cs` | KEEP |
| `Materialisation/Materialiser.cs` | KEEP core; drop `PromoteItemAsync`; add tuple signature |
| `Materialisation/QualityScorer.cs` | KEEP |
| `Materialisation/EvictionSweeper.cs` | REWRITE per §EvictionSweeper |
| `Materialisation/SeriesAutopilot.cs` | REWRITE per §Materialisation/autopilot |
| `State/PhantomDb.cs` | REWRITE: new schema; drop old tables; add new tables |
| `Api/PhantomLibraryController.cs` | TRIM |
| `Api/PhantomLibraryBadgesController.cs` | REWRITE state lookup |
| `Api/SourcePickerController.cs` | KEEP |
| `Sources/SourcePickerService.cs` | KEEP |
| `Channels/PhantomMoviesChannel.cs` (NEW) | |
| `Channels/PhantomShowsChannel.cs` (NEW) | |
| `Channels/PhantomChannelItemBuilder.cs` (NEW) | shared item-construction logic |
| `Channels/ChannelItemId.cs` (NEW) | encode/decode `(tmdb, type, season?, episode?)` ↔ string |
| `Channels/ChannelIds.cs` (NEW) | constants for the two channel internal Guids |
| `Channels/DiscoveryRefreshTask.cs` (NEW) | replaces SuggestionsRefreshTask |
| `Channels/GostreamFilesystemEnumerator.cs` (NEW) | |
| `Channels/TmdbExternalIdResolver.cs` (NEW) | imdb lookup cache for episode materialise |
| `scripts/jellyfin-patches/0001-channelmanager-refresh-channel-item.patch` (NEW) | the Jellyfin patch |
| `scripts/jellyfin-patches/REBASE.md` (NEW) | rebase instructions |
| `scripts/phantom-wipe.sh` (NEW) | wipe script per AGENTS.md |
| `Scheduled/SuggestionsRefreshTask.cs` | DELETE (replaced) |
| `Configuration/PluginConfiguration.cs` | EXTEND per §Plugin config |
| `Configuration/configPage.html` | REWRITE for new settings |
| `Configuration/phantomKebab.js` | minor: channel item id format |
| `Configuration/phantomBadges.js` | minor: state value mapping |
| `PluginServiceRegistrator.cs` | REGISTER two channels + TmdbExternalIdResolver; deregister deleted services |
| `install.sh` | EXTEND to apply Jellyfin patches before build |
| `AGENTS.md` | ADD section: "Jellyfin patch dependency" |
| `README.md` | UPDATE: requires patched Jellyfin |

Estimated net: ~3500 lines deleted, ~1500 lines added. ~50% code
reduction, honestly measured this time.

## Migration / wipe

Per AGENTS.md, schema/format change ⇒ **BREAKING wipe**. Operator
already accepted UserData loss for existing gostream-bound BaseItems
(v1 plan R3); reaffirm here.

### `scripts/phantom-wipe.sh`

Same shape as the previously-tested wipe script:

1. Refuses to run while `jellyfin.service` is active
2. Backs up `phantom.db` and `jellyfin.db`
3. Removes operator's existing `gostream-movies` and `gostream-shows`
   `CollectionFolder` rows + FK-cascade children
4. Removes the existing phantom-library stub directory contents
5. Removes existing `phantom.db` (plugin recreates with new schema)
6. Sanity bound: refuses to delete > 50% of `BaseItems` in one run

Inline manual fallback in CHANGELOG.

### Operator install procedure

```
1. sudo systemctl stop jellyfin
2. cd phantom-library
3. git pull
4. sudo bash scripts/phantom-wipe.sh             # dry run, inspect
5. sudo bash scripts/phantom-wipe.sh --commit    # type WIPE
6. ./install.sh --build
   # install.sh applies scripts/jellyfin-patches/*.patch
   # against the source clone, then builds Jellyfin + plugin
7. sudo systemctl start jellyfin
8. Dashboard → Plugins → Phantom Library → Settings
   # confirm gostream paths; click Save
9. Dashboard → Scheduled Tasks → "Phantom Library: Discovery Refresh"
   → Run Now
   # populates discovery_cache; channels return non-empty results
10. Refresh browser; "Phantom Movies" and "Phantom Shows" tiles
    appear in nav
11. Click a phantom movie → splash plays → kebab → Materialise →
    toast → click play → real file plays with correct streams
```

Step 9 is the v1 critic's drift note fix (first install otherwise
shows empty tiles).

## Open questions (for v2 critic)

1. **Patch upstream-PR feasibility.** Has anyone in the Jellyfin
   maintainer space discussed per-item channel refresh? Search the
   `jellyfin/jellyfin` GitHub issues/PRs. If a similar PR was
   already rejected for reasons we should know about, the patch
   maintenance burden estimate changes.

2. **`InternalChannelItemQuery.ChannelId` field existence.** The
   plan's `RefreshChannelItemAsync` implementation uses
   `query.ChannelId` to scope the fallback `GetChannelItems` call.
   Confirm this field exists on the query type in Jellyfin 10.11.

3. **`MediaBrowser.Controller/Channels/IChannelManager.cs`
   versioning.** Adding a method to a public interface is a breaking
   API change. Jellyfin's plugin API stability story for
   minor-version bumps: do they allow it? If not, the patch needs
   to add a new optional interface (`IChannelManagerExtended` or
   similar) rather than mutating `IChannelManager`.

4. **`_memoryCache.Remove(externalId)` correctness.** The
   `GetChannelItemMediaSourcesInternal` caller passes
   `item.ExternalId` as the cache key (line 395 in ChannelManager.cs).
   Confirm `item.ExternalId == channelItemExternalId` (i.e. the
   `ChannelItemInfo.Id` we have in hand matches the BaseItem's
   `ExternalId` column).

5. **TMDB external_ids API rate limits.** Episode materialise now
   triggers a TMDB external_ids API call (cached). For autopilot
   prefetch of N upcoming episodes, that's N+1 TMDB calls (1
   external_ids + N magnet lookups). At default rate limit
   (50/sec) and a 5-episode prefetch, ~120ms latency before
   gostream calls start. Acceptable but worth noting.

6. **What happens if `_channelManager.RefreshChannelItemAsync`
   fails or hangs?** Plan calls it inside Materialiser after
   gostream Add returns. If the patched API throws, the materialise
   "succeeded" (gostream has the file) but the channel doesn't
   know. User sees the splash forever until next discovery refresh
   or manual `RefreshChannels` from Dashboard. Mitigation:
   try/catch in Materialiser; on failure, log + queue a delayed
   retry; fallback to bumping channel DataVersion which forces
   a full re-list on next browse (slower but it works).

7. **Channel item with `IRequiresMediaInfoCallback` cached for 5
   minutes** — the patch invalidates `_memoryCache` for the
   refreshed item. But what if a user is mid-playback (paused) on
   the splash when materialisation completes? The MediaSource
   they're playing from is the splash, cached; even after our
   invalidate, their playback session continues from the old
   source. Subsequent play sessions get the real file. Acceptable;
   not a regression vs. today's UX.

8. **Two channel items merging in `MergeAndDeduplicate` when a
   phantom is also materialised.** After materialise:
   `materialised_state.has(tmdb)` → real item emitted.
   `discovery_cache.has(tmdb)` → phantom item emitted... but
   `MergeAndDeduplicate` filters by tmdb_id. Real wins. Phantom
   row stays in discovery_cache forever (until TTL eviction) but
   doesn't surface. Acceptable; could also delete from
   discovery_cache at materialise time for tidiness.

## Risks

R1. **Jellyfin patch maintenance.** Every Jellyfin minor version
upgrade may require patch rebase. Surrounding `ChannelManager.cs`
context is stable across 10.8-10.11 per `git log` inspection but
could change. Mitigation: explicit `install.sh` failure if patch
won't apply, with operator-actionable error.

R2. **Upstream rejection.** Patch may not merge upstream. Indefinite
maintenance burden. Mitigation: PR with full test coverage; if
rejected, evaluate forking Jellyfin properly.

R3. **`UserData` loss on existing gostream content.** Wipe procedure
removes existing CollectionFolders → cascades UserData. Operator
accepted this. Confirm one more time before merge.

R4. **`tmdb_id` extraction reliability for orphan gostream files.**
The plan's `EnrichOrphanGostreamItemsViaTmdbSearch = false` default
means orphan gostream items appear with raw filename names. If the
operator has substantial pre-materialised gostream content with no
phantom_items lineage, the UI degrades for those items. Mitigation:
TMDB title-search fallback can be enabled via setting; operator
trades enumeration speed for nicer names.

R5. **`MergeAndDeduplicate` performance.** With ~17k phantoms +
~150 real files, the merge happens on every `GetChannelItems` call.
Should be sub-second but unverified. Mitigation: cache the merged
list in-memory per `DataVersion`; invalidate on
`RefreshChannelItemAsync`.

R6. **First-install empty-channel UX.** Step 9 in operator procedure
exists specifically because empty channels show empty tiles and
look broken. If operator skips it, they panic. Mitigation:
prominent install procedure; plugin emits a startup log warning if
`discovery_cache` is empty + `last_refreshed > 1 hour ago`.

R7. **Episode-grid rendering verification.** The v1 critic flagged
that `Series.cs:1815` was mis-cited and UI rendering of channel-
backed series was unverified. **HARD-GATED before merge**: rig
test must confirm the episode-grid UI actually renders correctly
in the web client and in the mobile/TV clients the operator uses.

R8. **`forceUpdate` interaction with `OnMetadataChanged()`.** The
patch sets `forceUpdate=true` for the materialise refresh. The
existing `OnMetadataChanged()` call (line 1167) fires; it dispatches
metadata-change events to subscribers. Any custom subscribers
(metadata indexers, search index updaters) will fire on every
materialise. Today's load: zero (no channel-driven materialise).
Post-merge load: one event per materialise. Verify no
subscriber-side flood concerns.

## Out of scope (for this PR)

- TMDB title-search default-on for orphan gostream items (R4
  workaround; operator-opt-in)
- Search integration for channel items (verify in implementation; if
  broken, follow-up)
- Per-user discovery filtering (current plan: global discovery)

## Acceptance

PR is "done" when:

1. `dotnet build -c Release` clean (both Jellyfin patched and plugin)
2. `dotnet test` green: Jellyfin's tests + plugin's tests
3. Jellyfin patch tests (Patch §E above) green
4. All five rig scenarios pass:
   - `20-channel-fresh-install.sh`
   - `21-channel-materialise-flow.sh` (verifies probe + streams correct
     post-materialise)
   - `22-channel-eviction.sh`
   - `23-real-gostream-coexistence.sh`
   - `24-mobile-tv-client-rendering.sh` (R7 gate)
5. Operator-side verified: install procedure produces working channels
   with correct UX on web, mobile, and TV clients
6. CHANGELOG entry with **BREAKING — requires wipe + patched Jellyfin**
   prefix + inline wipe procedure + install steps
7. `scripts/phantom-wipe.sh` committed + shellcheck clean +
   sandbox-validated against operator's actual data shape per
   AGENTS.md
8. `scripts/jellyfin-patches/0001-channelmanager-refresh-channel-item.patch`
   committed; `install.sh` applies it cleanly against current
   Jellyfin source clone
9. Upstream Jellyfin PR opened with the patch + tests
