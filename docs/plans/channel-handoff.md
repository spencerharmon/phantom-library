# Phantom Library — IChannel migration handoff (Path A: local patch)

Date: 2026-06-09 (v3 after critic round 3)
Status: **DRAFT v3 — addresses critic round 3 findings; awaiting final critic pass**
Path: A (local Jellyfin patch shipped via `install.sh`; upstream PR
deferred to Phase 8)

v3 changes from v2:
- Single external-id-per-logical-item scheme (was split `phantom_*` /
  `real_*`; broke dedup + nuked UserData per materialise — critic round
  3 BLOCKER 1)
- Materialiser try/finally restructured to protect in-flight row across
  the pre-flight `RefreshChannelItemAsync` (critic BLOCKER 2)
- Materialiser tuple-rewrite specified explicitly (helper inventory,
  `tmdb_cache` schema, SourcePicker tuple form, year/title/IMDB sourcing
  — critic BLOCKER 4)
- Install.sh binary-replacement scope DROPPED (operator: packaging not a
  priority; install.sh stays as dev-machine convenience only — critic
  BLOCKER 3)
- Wipe script confirmed present at `scripts/phantom-wipe.sh` (was
  flagged as missing in v2 — critic BLOCKER 5; operator copied from
  `/tmp/`)
- DI registration corrected: site is
  `src/Jellyfin.LiveTv/Extensions/LiveTvServiceCollectionExtensions.cs:33`;
  two-forwarder pattern (critic IMPORTANT 6)
- Style fixes: block-scoped namespaces; drop `ChannelItemResult.Empty`
  fabrication; commit to `forceUpdateParam` rename (critic IMPORTANT 7)
- Series-level materialise: explicit `Outcome.Error` reject branch in
  tuple Materialiser; kebab JS filters series items (critic IMPORTANT 8)
- New `SplashInitService` for plugin-startup splash extraction (critic
  IMPORTANT 9)

Supersedes (mark obsolete on this plan's merge):
- `docs/plans/channel-architecture.md` (v1, v2 — design context only)
- `docs/plans/scanner-race-reactor.md` (alternative architecture; not pursued)

References:
- `AGENTS.md` (four hard-rule sections at top)
- `https://jellyfin.org/docs/general/contributing/llm-policies/` (governs Phase 8 only; not Path A)
- `https://jellyfin.org/docs/general/contributing/development/` (governs Phase 8 only)
- `jellyfin/.github/workflows/ci-compat.yml` (ABI diff is informational, not blocking)

---

## Goal

Replace the operator's existing `gostream-movies` and `gostream-shows`
`CollectionFolder` libraries with custom `IChannel` implementations
that:

- Surface real gostream files (enumerated directly from
  `/var/gostream/gostream-mkv-virtual/{movies,tv}/`)
- Surface phantom discovery items (synthesised from TMDB)
- Unify both in a single browse view per channel
- Resolve materialise-on-play correctly across all clients (web,
  mobile, TV)
- Persist materialised state cleanly without the scanner race that
  plagued the file-on-disk architecture

…on top of a targeted, additive-only patch to Jellyfin's
`ChannelManager` that adds a per-item refresh primitive.

## Constraints

1. **No scanner race.** Plugin never calls `ILibraryManager.CreateItem`
   for phantom-tree items. ChannelManager owns BaseItem lifecycle.
2. **All-client support.** Architecture must work for web, mobile,
   and TV apps without client-side patches.
3. **Atomic, tight patch.** Jellyfin patch is the smallest possible
   set of additive-only changes. No interface mutation. No incidental
   refactors. Each commit single-purpose.
4. **Local-first.** Patch ships in this repo at
   `scripts/jellyfin-patches/`; `install.sh` applies it against the
   `jellyfin/` source clone before build. Upstream PR is Phase 8 and
   is operator-driven.
5. **Per AGENTS.md "No database migrations until v1.0":** schema
   evolution = wipe-and-rebuild. No migration tooling.
6. **Per AGENTS.md "Production database safety":** every destructive
   step (wipe, large UPDATE, etc.) must be tested end-to-end against
   a clone of the operator's actual DB shape before reaching prod.
7. **Per AGENTS.md "Single-operator deployment":** Jellyfin may be
   stopped freely for maintenance; offline bash scripts preferred
   over runtime data-mutation services.

---

## Pre-flight (Phase 0)

Before any code is written, verify the following. Each item is a
one-shot read-only check; total time ~30 minutes.

### 0.1 Source clone is current

```bash
cd jellyfin/
git fetch origin
git log -1 --oneline origin/master   # note the SHA
git status                            # working tree clean
```

The patch will be authored against this SHA. Record it in the patch
header for reproducibility.

### 0.2 Plugin builds clean against unpatched Jellyfin

```bash
cd /home/spencer/git-repos/spencerharmon/phantom-library
dotnet build -c Release
dotnet test
```

Both green. Records the baseline state we'll wipe.

### 0.3 Rig is functional

Per `docs/agents/testing.md`. Start the rig, drive a no-op scenario,
tear down. Confirms the rig is in a state where we can validate each
phase.

### 0.4 Operator-DB snapshot for sandbox testing

Copy the operator's current `phantom.db` and `jellyfin.db` to
`/tmp/operator-snapshot/` for use in Phase 7 sandbox validation.
Per AGENTS.md, the snapshot is the canonical "real data shape" we
test against before final deployment.

### 0.5 Gostream service health

```bash
systemctl status gostream.service
curl -fsS http://localhost:9080/health    # or whatever the existing probe is
ls /var/gostream/gostream-mkv-virtual/{movies,tv}/ | wc -l
```

Confirms gostream is running and serving its FUSE mount. Records
the file count we expect to see preserved across the migration.

### 0.6 Verify no plugin-side or web-shim references to legacy phantom-stub paths in unrelated code

```bash
cd /home/spencer/git-repos/spencerharmon/phantom-library
grep -rn 'phantom-library/movies\|phantom-library/shows' src/ --include='*.cs' \
  | grep -v 'PhantomStubManager\|PhantomCollectionFolderBinder\|PhantomPathUtilities'
```

If grep returns non-deletion-target hits, those code paths need
updating during the rewrite. Catch them in Phase 0 not Phase 7.

---

## Phase 1: Jellyfin patch (atomic, tight, simple)

### Design

Three additive components. **No existing public API is modified.**
No interface members are added to existing interfaces. The patch is
purely sibling-interface + new service + private-method-param
additions.

#### Component 1.A: `IChannelItemRefresh` (channel-side opt-in)

NEW file: `jellyfin/MediaBrowser.Controller/Channels/IChannelItemRefresh.cs`

Uses **block-scoped namespace** to match the rest of `MediaBrowser.Controller/Channels/`:

```csharp
#pragma warning disable CS1591
#nullable disable

using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Channels
{
    /// <summary>
    /// Optional capability for IChannel implementations. Channels that
    /// implement this advertise the ability to resolve a single
    /// <see cref="ChannelItemInfo"/> by its external id without paging
    /// through <c>GetChannelItems</c>. Used by
    /// <see cref="IChannelItemRefreshManager.RefreshChannelItemAsync"/>
    /// for efficient single-item refresh when a plugin-driven workflow
    /// (e.g. materialise-on-demand) needs to update a single item's
    /// persisted state.
    ///
    /// Channels that do not implement this interface fall back to the
    /// manager paging through GetChannelItems to locate the item.
    /// </summary>
    public interface IChannelItemRefresh
    {
        /// <summary>
        /// Look up the current <see cref="ChannelItemInfo"/> for a given
        /// external id. Return null if the channel no longer surfaces
        /// that item.
        /// </summary>
        Task<ChannelItemInfo> GetChannelItemAsync(
            string channelItemExternalId,
            CancellationToken cancellationToken);
    }
}
```

#### Component 1.B: `IChannelItemRefreshManager` (new service)

NEW file: `jellyfin/MediaBrowser.Controller/Channels/IChannelItemRefreshManager.cs`

Block-scoped namespace to match style:

```csharp
#pragma warning disable CS1591
#nullable disable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Channels
{
    /// <summary>
    /// Refreshes a single channel item's persisted state from its
    /// providing <see cref="IChannel"/>. Used by plugins that mutate a
    /// channel item's underlying media independently of the regular
    /// channel scan (e.g. materialise-on-demand pipelines where a
    /// placeholder MediaSource is replaced by a real file).
    ///
    /// Implemented by <c>ChannelManager</c>; registered as a sibling
    /// service to <see cref="IChannelManager"/>. Adding this as a new
    /// interface (rather than extending <see cref="IChannelManager"/>)
    /// preserves binary compatibility for existing plugins.
    /// </summary>
    public interface IChannelItemRefreshManager
    {
        Task RefreshChannelItemAsync(
            Guid channelId,
            string channelItemExternalId,
            ChannelItemRefreshOptions options = null,
            CancellationToken cancellationToken = default);
    }

    /// <summary>Flags controlling RefreshChannelItemAsync behaviour.</summary>
    public sealed class ChannelItemRefreshOptions
    {
        public bool ForceUpdate { get; set; } = true;
        public bool ForceProbe { get; set; } = true;
        public bool InvalidateMediaInfoCache { get; set; } = true;
    }
}
```

#### Component 1.C: `ChannelManager` modifications

EDITED file: `jellyfin/src/Jellyfin.LiveTv/Channels/ChannelManager.cs`

Three changes, surgical:

**1.C.1** — class declaration adds the interface:

```diff
- public class ChannelManager : IChannelManager
+ public class ChannelManager : IChannelManager, IChannelItemRefreshManager
```

**1.C.2** — extract `GetChannelItemEntityAsync` body into an inner
overload that takes the new params. Existing call site (one only,
inside `GetChannelItemsInternal`) calls the new overload with the
legacy-equivalent flags `(forceUpdate: false, forceProbe: false)`.

Current method signature (line 957):
```csharp
private async Task<BaseItem> GetChannelItemEntityAsync(
    ChannelItemInfo info,
    IChannel channelProvider,
    Guid internalChannelId,
    BaseItem parentFolder,
    CancellationToken cancellationToken)
```

**Naming decision (per critic IMPORTANT 7):** the existing local variable
`bool forceUpdate = false;` (declared at line 963 and referenced at ~15
use-sites inside the method) is named `forceUpdate`. To avoid renaming
~15 references, the new method parameter is named `forceUpdateParam`
and the local is initialised from it.

**Wrapper-vs-async decision:** the existing method is `async Task<BaseItem>`.
The legacy-shape wrapper is a non-async expression-bodied member that
returns the new overload's `Task` directly. Saves a state machine.

Replace with a thin wrapper plus a new overload:

```csharp
private Task<BaseItem> GetChannelItemEntityAsync(
    ChannelItemInfo info,
    IChannel channelProvider,
    Guid internalChannelId,
    BaseItem parentFolder,
    CancellationToken cancellationToken)
    => GetChannelItemEntityAsync(
        info, channelProvider, internalChannelId, parentFolder,
        forceUpdateParam: false, forceProbe: false, cancellationToken);

private async Task<BaseItem> GetChannelItemEntityAsync(
    ChannelItemInfo info,
    IChannel channelProvider,
    Guid internalChannelId,
    BaseItem parentFolder,
    bool forceUpdateParam,
    bool forceProbe,
    CancellationToken cancellationToken)
{
    // ... existing body, with three modifications below ...
}
```

Inside the new overload's body, three modifications:

a) Line ~963 (existing local-variable initialisation):
```diff
-   var forceUpdate = false;
+   var forceUpdate = forceUpdateParam;
```
The ~15 existing references to `forceUpdate` are unchanged.

b) Line ~1003 (probe-pin guard):
```diff
- else if (isNew || !enableMediaProbe)
+ else if (isNew || !enableMediaProbe || forceProbe)
```

c) Line ~1167 (`QueueRefresh` call):
```diff
-   if (isNew || forceUpdate || item.DateLastRefreshed == DateTime.MinValue)
+   if (isNew || forceUpdate || forceProbe || item.DateLastRefreshed == DateTime.MinValue)
    {
-       _providerManager.QueueRefresh(item.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.Normal);
+       var refreshOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem));
+       if (forceProbe)
+       {
+           refreshOptions.EnableRemoteContentProbe = true;
+           refreshOptions.MetadataRefreshMode = MetadataRefreshMode.FullRefresh;
+       }
+       _providerManager.QueueRefresh(item.Id, refreshOptions, RefreshPriority.Normal);
    }
```

**1.C.3** — append `RefreshChannelItemAsync` implementation. New
method, placed after `GetChannelItemEntityAsync`:

```csharp
/// <inheritdoc />
public async Task RefreshChannelItemAsync(
    Guid channelId,
    string channelItemExternalId,
    ChannelItemRefreshOptions options = null,
    CancellationToken cancellationToken = default)
{
    ArgumentException.ThrowIfNullOrEmpty(channelItemExternalId);
    options ??= new ChannelItemRefreshOptions();

    var channelEntity = _libraryManager.GetItemById(channelId) as Channel
        ?? throw new InvalidOperationException(
            $"Channel BaseItem {channelId} not found or wrong type");

    var provider = GetChannelProvider(channelEntity)
        ?? throw new InvalidOperationException(
            $"No IChannel provider registered for channel {channelId}");

    // Resolve the fresh ChannelItemInfo. Prefer targeted lookup if
    // the channel implements IChannelItemRefresh; otherwise page
    // GetChannelItems at the root.
    ChannelItemInfo info = null;
    if (provider is IChannelItemRefresh targeted)
    {
        info = await targeted.GetChannelItemAsync(
            channelItemExternalId, cancellationToken).ConfigureAwait(false);
    }
    else
    {
        var query = new InternalChannelItemQuery
        {
            FolderId = null,
            StartIndex = 0,
            Limit = int.MaxValue,
        };
        var result = await provider.GetChannelItems(query, cancellationToken)
            .ConfigureAwait(false);
        if (result?.Items is { } items)
        {
            foreach (var candidate in items)
            {
                if (string.Equals(candidate.Id, channelItemExternalId,
                    StringComparison.Ordinal))
                {
                    info = candidate;
                    break;
                }
            }
        }
    }

    if (info is null)
    {
        _logger.LogDebug(
            "RefreshChannelItemAsync: channel {ChannelId} no longer surfaces item {ExternalId}; nothing to refresh",
            channelId, channelItemExternalId);
        return;
    }

    if (options.ForceUpdate || options.ForceProbe)
    {
        await GetChannelItemEntityAsync(
            info,
            provider,
            channelId,
            channelEntity,
            forceUpdateParam: options.ForceUpdate,
            forceProbe: options.ForceProbe,
            cancellationToken).ConfigureAwait(false);
    }

    if (options.InvalidateMediaInfoCache)
    {
        // GetChannelItemMediaSourcesInternal caches keyed on
        // item.ExternalId (see ChannelManager.cs:395, line 410-419).
        // ExternalId equals the ChannelItemInfo.Id we have in hand.
        _memoryCache.Remove(channelItemExternalId);
    }
}
```

#### Component 1.D: DI registration

EDITED file: `jellyfin/src/Jellyfin.LiveTv/Extensions/LiveTvServiceCollectionExtensions.cs`
(line 33 currently registers `IChannelManager`). Per critic IMPORTANT 6:
the v2 plan pointed at the wrong file (`Jellyfin.Server/Extensions/...`)
and used a cast-forwarder pattern that breaks under DI decoration.

The idiomatic pattern is two forwarders off a self-type registration:

```diff
-     services.AddSingleton<IChannelManager, ChannelManager>();
+     services.AddSingleton<ChannelManager>();
+     services.AddSingleton<IChannelManager>(
+         sp => sp.GetRequiredService<ChannelManager>());
+     services.AddSingleton<IChannelItemRefreshManager>(
+         sp => sp.GetRequiredService<ChannelManager>());
```

This preserves the single-instance lifetime (`ChannelManager` is a
singleton; both interfaces resolve to the same instance), avoids the
cast that would throw `InvalidCastException` if anyone decorates
`IChannelManager` (DI wrapper, test fake, profiling proxy), and is a
smaller / more obviously-correct diff for Phase 8 upstream review.

#### Component 1.E: Tests

NEW file:
`jellyfin/tests/Jellyfin.LiveTv.Tests/Channels/ChannelManagerRefreshTests.cs`
(or wherever the existing ChannelManager tests live — verify in
Phase 1.1)

Test cases:

1. `RefreshChannelItem_NonexistentChannel_Throws`
2. `RefreshChannelItem_ItemRemovedFromChannel_LogsAndReturns`
3. `RefreshChannelItem_TargetedLookup_PrefersIChannelItemRefresh`
4. `RefreshChannelItem_NoTargetedLookup_FallsBackToPaging`
5. `RefreshChannelItem_ForceUpdateTrue_PersistsPathChange` —
   asserts Path changes from splash to FUSE path through
   `RefreshChannelItem` even when the channel re-emits the same
   ExternalId. **Regression test for the v2 critic's BLOCKER 2.**
6. `RefreshChannelItem_ForceProbeTrue_TriggersFullRefreshWithRemoteProbe`
7. `RefreshChannelItem_InvalidatesMediaInfoCache_NextCallHitsProvider` —
   regression test for v2 critic's BLOCKER 5.
8. `RefreshChannelItem_OptionsAllFalse_NoOp`

Test fixtures use Moq for `ILibraryManager`, `IProviderManager`,
`IUserManager`, and a fake `IChannel + IChannelItemRefresh` for the
plugin side. ~150 lines.

### Commit granularity (Phase 8 prep)

Three commits, each independently reviewable:

1. **`Add IChannelItemRefresh opt-in interface for channels`** —
   Component 1.A alone. ~25 lines added, zero existing files
   touched.

2. **`Add IChannelItemRefreshManager service for per-item channel refresh`** —
   Components 1.B, 1.C, 1.D together. ~140 lines added, two existing
   files touched (`ChannelManager.cs`, the DI registration).

3. **`Add tests for ChannelManager per-item refresh`** —
   Component 1.E. ~150 lines added.

For Path A (this plan) these can land as a single commit on a local
branch; the granularity matters only for Phase 8.

### Phase 1 execution stages

**Stage 1.1 — Locate the DI registration site.**
Implementer-agent task: find `serviceCollection.AddSingleton<IChannelManager, ChannelManager>()`
in `jellyfin/`. Report exact file + line. Also find the test project
that contains existing `ChannelManager*` tests. Output: a short note
appended to this plan recording the paths.

**Stage 1.2 — Write the patch.**
Implementer task: produce Components 1.A through 1.E as a series of
edits to `jellyfin/`. Acceptance: `cd jellyfin && dotnet build`
clean; `dotnet test` green including the new test class.

**Stage 1.3 — Export as `.patch` files.**
```bash
cd jellyfin/
git add -A
git commit -m "Add IChannelItemRefresh opt-in interface for channels"      # commit 1
git add -A   # if any unstaged
git commit -m "Add IChannelItemRefreshManager service for per-item channel refresh"   # commit 2
git commit -m "Add tests for ChannelManager per-item refresh"             # commit 3
git format-patch -3 -o ../scripts/jellyfin-patches/
```

Result: three numbered .patch files in `scripts/jellyfin-patches/`.

**Stage 1.4 — Extend `install.sh` to apply patches.**

Before the existing `dotnet build` step for Jellyfin, add:

```bash
patches_dir="$(dirname "$0")/scripts/jellyfin-patches"
if [ -d "$patches_dir" ] && ls "$patches_dir"/*.patch >/dev/null 2>&1; then
    echo "==> applying Jellyfin patches"
    (
        cd jellyfin
        for patch in "$patches_dir"/*.patch; do
            # Idempotency: skip patches already applied (e.g. on rebuild
            # without an intervening 'git reset --hard' of jellyfin/).
            if git apply --check "$patch" 2>/dev/null; then
                git apply "$patch"
                echo "    applied: $(basename "$patch")"
            elif git apply --check -R "$patch" 2>/dev/null; then
                echo "    already applied: $(basename "$patch")"
            else
                echo "ERROR: patch $(basename "$patch") does not apply cleanly." >&2
                echo "       Likely cause: jellyfin/ source has drifted from the patch base." >&2
                echo "       Resolution: rebase the patch. See scripts/jellyfin-patches/REBASE.md" >&2
                exit 1
            fi
        done
    )
fi
```

Create `scripts/jellyfin-patches/REBASE.md` with concrete rebase
instructions (clone Jellyfin master, apply patches with `git am`,
resolve conflicts, re-export with `git format-patch`).

**Stage 1.5 — Rig validation.**

End-to-end test in the rig:

```bash
./install.sh --build     # applies patches, builds patched Jellyfin
# start rig Jellyfin
# call /System/Info; confirm version reports expected
# (no API to confirm patch is present; do it by behaviour test below)
```

Behaviour test: stand up a minimal stub `IChannel + IChannelItemRefresh`
implementation (live in `tools/rig-scenarios/`); register via a
test-only plugin; call `IChannelItemRefreshManager.RefreshChannelItemAsync`
via a test endpoint; assert Path changes from splash to a different
test path. If this works, the patch is functional.

Also: run Jellyfin's own test suite (`cd jellyfin && dotnet test`)
once more end-to-end to confirm no regressions in any other
component.

**Stage 1.6 — Commit Phase 1 to phantom-library main.**

```bash
cd /home/spencer/git-repos/spencerharmon/phantom-library
git add scripts/jellyfin-patches/ install.sh
git commit -m "build: apply Jellyfin patches for per-item channel refresh"
```

This commit's PR description summarises what the patches do, why,
and links to this plan.

---

## Phase 2: Plugin foundation

Sequenced to keep `dotnet build` and `dotnet test` green at every
commit boundary. Each stage is a separate commit on the main branch.

### Stage 2.1 — Delete components that go away under the new architecture

One commit. All files listed in `docs/plans/channel-architecture.md`
§"What goes away". Per that list:

DELETE:
- `Library/PhantomStubManager.cs`
- `Library/PhantomCollectionFolderBinder.cs`
- `Library/PhantomPathUtilities.cs`
- `Library/SeriesIngestor.cs`
- `Library/SuggestionsContributor.cs`
- `Library/VirtualItemFactory.cs`
- `Library/VirtualLibraryRoot.cs`
- `PhantomBootstrapService.cs`
- `Providers/PhantomImageProvider.cs`
- `Playback/PhantomMediaSourceProvider.cs`
- `Playback/PhantomStatusDecorator.cs`
- `Materialisation/EagerHintSink.cs`
- `Materialisation/EagerResolver.cs`
- `Scheduled/SuggestionsRefreshTask.cs`

Plus all their tests under
`tests/Jellyfin.Plugin.PhantomLibrary.Tests/`.

Plus deregistration from `PluginServiceRegistrator.cs`.

After this commit: plugin compiles but does very little (only the
SourcePicker controller, Materialiser core, gostream client, kebab
JS shim survive). Tests reduced significantly. CHANGELOG entry:
"refactor: remove file-on-disk phantom architecture (replaced by
IChannel-based design, see Phase 3+)."

### Stage 2.2 — New state schema

EDIT `State/PhantomDb.cs`:

Bump `CurrentSchemaVersion` to 7. The previous v5/v6 work is
irrelevant under the new architecture; the schema is rewritten from
scratch.

Replace `EnsureSchemaAsync` table-creation blocks with:

```sql
CREATE TABLE IF NOT EXISTS discovery_cache (
    tmdb_id        INTEGER NOT NULL,
    type           TEXT NOT NULL,        -- 'movie' or 'series'
    discovered_at  INTEGER NOT NULL,     -- unix ts
    last_refreshed INTEGER NOT NULL,
    PRIMARY KEY (tmdb_id, type)
);
CREATE INDEX IF NOT EXISTS idx_discovery_cache_last_refreshed
    ON discovery_cache(last_refreshed);

CREATE TABLE IF NOT EXISTS materialised_state (
    tmdb_id        INTEGER NOT NULL,
    type           TEXT NOT NULL,        -- 'movie' or 'episode'
    season         INTEGER NOT NULL DEFAULT -1,   -- -1 sentinel for movies; per critic v2 BLOCKER
    episode        INTEGER NOT NULL DEFAULT -1,   -- -1 sentinel for movies
    stub_path      TEXT NOT NULL,
    fuse_path      TEXT NOT NULL,
    materialised_at INTEGER NOT NULL,
    PRIMARY KEY (tmdb_id, type, season, episode)
);
CREATE INDEX IF NOT EXISTS idx_materialised_state_type ON materialised_state(type);
CREATE INDEX IF NOT EXISTS idx_materialised_state_materialised_at
    ON materialised_state(materialised_at);

CREATE TABLE IF NOT EXISTS materialise_in_flight (
    tmdb_id        INTEGER NOT NULL,
    type           TEXT NOT NULL,
    season         INTEGER NOT NULL DEFAULT -1,
    episode        INTEGER NOT NULL DEFAULT -1,
    started_at     INTEGER NOT NULL,
    PRIMARY KEY (tmdb_id, type, season, episode)
);

CREATE TABLE IF NOT EXISTS tmdb_external_ids (
    tmdb_id        INTEGER NOT NULL,
    type           TEXT NOT NULL,        -- 'movie' or 'series'
    imdb_id        TEXT,                 -- null = negative cache; entry_age TTL applies
    fetched_at     INTEGER NOT NULL,
    PRIMARY KEY (tmdb_id, type)
);

-- existing tables kept (verify still in schema script):
-- tmdb_cache         -- (tmdb_id, type) -> title/overview/year/etc; reused by channel item synthesis; explicit schema in Stage 4.2
-- magnet_cache       -- (tmdb, imdb, type, season, episode, preset) -> magnet+infohash+seeders+TTL; reused by Materialiser to avoid re-querying indexers
-- unavailable_marker -- (tmdb, imdb, type, season, episode) -> retry_after; written when MagnetSelector returns no candidate; gates re-fetch storms (critic round 3 BLOCKER 2)
-- plugin_meta        -- key/value, marker store
```

Add `EnsureSchemaAsync` HARD-REFUSE branch: if existing DB reports
`user_version > 0 AND user_version < 7`, throw with the explicit
"wipe required, see scripts/phantom-wipe.sh" error. Plugin fails to
start; operator sees error immediately. **Per critic v2 BLOCKER 2
mitigation.**

Add new DB helpers:
- `Task UpsertDiscoveryCacheAsync(int tmdbId, string type, CancellationToken ct)`
- `Task<IReadOnlyList<DiscoveryCacheRow>> ListDiscoveryCacheAsync(string type, CancellationToken ct)`
- `Task PurgeStaleDiscoveryAsync(TimeSpan ttl, CancellationToken ct)`
- `Task UpsertMaterialiseInFlightAsync(int tmdbId, string type, int season, int episode, CancellationToken ct)` (sentinel `-1` for null season/episode at this boundary)
- `Task DeleteMaterialiseInFlightAsync(int tmdbId, string type, int season, int episode, CancellationToken ct)`
- `Task<bool> IsMaterialiseInFlightAsync(int tmdbId, string type, int season, int episode, CancellationToken ct)`
- `Task PurgeStaleMaterialiseInFlightAsync(TimeSpan threshold, CancellationToken ct)` — for the startup sweep
- `Task InsertMaterialisedStateAsync(int tmdbId, string type, int season, int episode, string stubPath, string fusePath, CancellationToken ct)`
- `Task<MaterialisedStateRow?> GetMaterialisedStateAsync(int tmdbId, string type, int season, int episode, CancellationToken ct)`
- `Task<IReadOnlyList<MaterialisedStateRow>> ListMaterialisedStateAsync(string type, CancellationToken ct)`
- `Task DeleteMaterialisedStateAsync(int tmdbId, string type, int season, int episode, CancellationToken ct)`
- `Task<string?> GetImdbIdAsync(int tmdbId, string type, CancellationToken ct)` — reads tmdb_external_ids; returns null if not cached or cached-negatively-and-expired
- `Task SetImdbIdAsync(int tmdbId, string type, string? imdbId, CancellationToken ct)` — caches; null means "negative cache" (entry recorded with null imdb_id; TTL controls re-fetch)

Per critic v2 BLOCKER 3 (UNIQUE NULL semantics): the `-1` sentinel
in the primary key columns means SQLite treats them as distinct
integer values, not NULL. Two movie inserts for `(tmdb=42, 'movie',
-1, -1)` correctly conflict on the PK. The sentinel is invisible
above the DB layer; `ChannelItemId` (Phase 2.3) translates between
`null` and `-1` at the boundary.

Tests: rewrite `tests/Jellyfin.Plugin.PhantomLibrary.Tests/PhantomDbTests.cs`
from scratch for the new schema. Cover: schema creation; PK
uniqueness for movies with sentinel; HARD-REFUSE on old version;
all helper round-trips.

### Stage 2.3 — `ChannelItemId` codec

NEW file: `Channels/ChannelItemId.cs`.

**Critical design decision (critic round 3 BLOCKER 1 fix):** the
external id for a logical item (movie / series / season / episode) is
**the same regardless of materialise state**. There is NO separate
`phantom_*` vs `real_*` namespace. The channel determines the
MediaSource at query time by consulting `materialised_state`; the
BaseItem.Id (derived by ChannelManager via stable hash of the external
id) is stable across the phantom → materialised transition, preserving
UserData (favourites, watched, playback position).

Format:

```
movie_<tmdb>                                  # movie (phantom OR materialised)
series_<tmdb>                                 # series top-level (folder)
season_<tmdb>_s<NN>                           # series → season folder
episode_<tmdb>_s<NN>e<NN>                     # series → season → episode
orphan_<hex>                                  # gostream file with no known tmdb id
```

`orphan_<hex>` uses a 16-char SHA1 prefix of the absolute file path;
stable as long as the file isn't renamed/moved. If renamed, treated
as a new orphan + UserData on the old id orphans. Acceptable; orphan
files are by definition things the plugin didn't put there.

API:
```csharp
public sealed record ChannelItemId(
    string Kind,           // "movie" | "series" | "season" | "episode" | "orphan"
    int? TmdbId,           // null for orphan
    int? Season,           // set for season + episode
    int? Episode,          // set for episode
    string? OrphanHash)    // set for orphan only
{
    public string Encode();
    public static ChannelItemId Parse(string s);
    public static bool TryParse(string s, out ChannelItemId id);

    // Sentinel conversion for DB primary keys (per AGENTS.md SQLite
    // NULL semantics fix in v2 schema): -1 means "not applicable".
    public static (int season, int episode) ToSentinels(int? season, int? episode);
    public static (int? season, int? episode) FromSentinels(int season, int episode);

    // Static factories for the common cases:
    public static ChannelItemId ForMovie(int tmdb);
    public static ChannelItemId ForSeries(int tmdb);
    public static ChannelItemId ForSeason(int seriesTmdb, int seasonNumber);
    public static ChannelItemId ForEpisode(int seriesTmdb, int seasonNumber, int episodeNumber);
    public static ChannelItemId ForOrphanPath(string absolutePath);
}
```

Tests under `Channels/ChannelItemIdTests.cs`:
- Round-trip for every shape
- Rejected inputs (malformed, wrong prefix)
- Sentinel ↔ null conversion symmetry
- `ForOrphanPath` produces stable hash for the same input across
  calls; different paths produce different hashes; rename produces a
  different id
- **Critical**: assert `ForMovie(42).Encode() == "movie_42"` regardless
  of whether tmdb=42 is in `materialised_state`. The id does not depend
  on materialise state.

### Stage 2.4 — Channel implementations: skeletons + DI registration

NEW files:
- `Channels/PhantomMoviesChannel.cs` — stub `IChannel + IRequiresMediaInfoCallback + ISupportsLatestMedia + IChannelItemRefresh` returning empty `ChannelItemResult` (`new ChannelItemResult { Items = Array.Empty<ChannelItemInfo>() }`) from `GetChannelItems`. `Name = "Phantom Movies"` (hardcoded).
- `Channels/PhantomShowsChannel.cs` — same, `Name = "Phantom Shows"`.
- `Channels/ChannelIds.cs` — constants for the two channel internal Guids derived via `GetNewItemId("Channel Phantom Movies", typeof(Channel))` (same algorithm `GetInternalChannelId` uses).
- `Channels/SplashInitService.cs` removed from this stage (critic round 3 IMPORTANT fix). Per critic, the hosted-service + TaskCompletionSource pattern has an unresolved race: IChannel.GetChannelItems has no "wait for plugin init" hook, and a browse hitting before extraction completes would either deadlock (sync-over-async) or persist a missing-file path against the BaseItem. Instead, splash extraction runs **synchronously inside `PluginServiceRegistrator.RegisterServices`** before the channel registrations execute. Splash is a small embedded resource; extraction is a sub-millisecond file copy; doing it synchronously at plugin-host construction time guarantees the splash path exists before any IChannel is resolved.

  Implementation in `PluginServiceRegistrator.cs`:
  ```csharp
  public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
  {
      // ... existing registrations ...

      // Extract splash synchronously before registering channels. The
      // channels' BuildMovieItemAsync / BuildEpisodeItemAsync return
      // ChannelItemInfos with MediaSources pointing at this splash file;
      // it MUST exist on disk before the first GetChannelItems call.
      var splashPath = SplashStream.GetLocalPath(_paths);   // synchronous variant; reads from embedded resources + writes to <plugin data dir>/splash.mp4
      services.AddSingleton<SplashSourceProvider>(
          sp => new SplashSourceProvider(splashPath));

      services.AddSingleton<IChannel, PhantomMoviesChannel>();
      services.AddSingleton<IChannel, PhantomShowsChannel>();
      // ... etc ...
  }
  ```

  Add a synchronous `SplashStream.GetLocalPath(IServerApplicationPaths paths)` companion to the existing async helper. The sync variant blocks on the async one (acceptable here — it's plugin init, not a request hot path) OR is implemented as a direct synchronous file-copy if the embedded-resource API allows.

  No hosted-service, no TaskCompletionSource, no ReadyAsync. The race goes away by construction.

Register in `PluginServiceRegistrator.cs`:

```csharp
// Splash extraction is synchronous at RegisterServices time (see above);
// SplashSourceProvider is constructed with the resulting path. No
// hosted service for splash.
serviceCollection.AddSingleton<IChannel, PhantomMoviesChannel>();
serviceCollection.AddSingleton<IChannel, PhantomShowsChannel>();
```

After this stage: `./install.sh --build`; restart rig Jellyfin; nav
shows two new tiles ("Phantom Movies", "Phantom Shows"); clicking
either shows an empty channel page. No errors in journal. Splash
file exists on disk at the expected path (verify before first browse).
Confirms the channel-registration + splash-init path works end-to-end
before we add the real logic.

Tests: `SplashSourceProviderTests` — returns expected MediaSource
pointing at the extracted splash path; idempotent across plugin-init
cycles.

---

## Phase 3: Discovery + movies channel

### Stage 3.1 — Discovery refresh task

NEW file: `Channels/DiscoveryRefreshTask.cs` (replaces deleted
`SuggestionsRefreshTask`).

`IScheduledTask` running every 6h (default; configurable per
existing pattern). Behaviour:

```csharp
async Task ExecuteAsync(...):
    var trendingMovies = await _tmdb.GetTrendingMoviesAsync(ct);
    var trendingSeries = await _tmdb.GetTrendingSeriesAsync(ct);

    foreach (var hit in trendingMovies)
        await _db.UpsertDiscoveryCacheAsync(hit.Id, "movie", ct);
    foreach (var hit in trendingSeries)
        await _db.UpsertDiscoveryCacheAsync(hit.Id, "series", ct);

    foreach (var user in _userManager.GetUsers())
    {
        var favMovieTmdbs = ReadFavouriteTmdbIds(user, ItemKind.Movie);
        var favSeriesTmdbs = ReadFavouriteTmdbIds(user, ItemKind.Series);
        foreach (var fav in favMovieTmdbs)
            foreach (var sim in await _tmdb.GetSimilarMoviesAsync(fav, ct))
                await _db.UpsertDiscoveryCacheAsync(sim.Id, "movie", ct);
        foreach (var fav in favSeriesTmdbs)
            foreach (var sim in await _tmdb.GetSimilarSeriesAsync(fav, ct))
                await _db.UpsertDiscoveryCacheAsync(sim.Id, "series", ct);
    }

    // TTL eviction: drop discovery_cache rows that are stale AND not
    // materialised AND not favourited (per critic v2 IMPORTANT).
    await _db.PurgeStaleDiscoveryAsync(
        TimeSpan.FromDays(_config.DiscoveryCacheTtlDays),
        protectFavourited: true,
        ct);

    // Bump channel DataVersion so the next browse re-fetches.
    _state.BumpDataVersion("movies");
    _state.BumpDataVersion("shows");
```

`PurgeStaleDiscoveryAsync(ttl, protectFavourited)` implementation:
DELETE rows where `last_refreshed < now - ttl` AND no
`materialised_state` row exists for the (tmdb, type) AND (if
protectFavourited) no UserData row exists with `IsFavorite=1` for
any channel item whose ExternalId encodes this tmdb. The favourite
check goes through `ILibraryManager.GetItemList` looking for a
BaseItem with the channel ExternalId — read-only Jellyfin DB query;
no race.

Register in `PluginServiceRegistrator.cs`. Default interval 6h.

**Critic round 3 IMPORTANT 4 fix:** the task also pre-populates
`tmdb_cache` for every discovered tmdb id during its run, so subsequent
channel browses don't pay sequential TMDB latency. Without this, the
first user browse after a refresh tick iterates every newly-discovered
row and BuildMovieItemAsync returns null for items whose tmdb_cache is
cold — fewer items emitted than discovery_cache suggests, and the
ChannelManager dead-id sweep at `ChannelManager.cs:735-754` may briefly
delete BaseItems whose tmdb fetch failed transiently.

```csharp
// After upserting discovery_cache + before BumpDataVersion:
foreach (var (tmdb, type) in discoveredTuples)
{
    try
    {
        if (type == "movie")
            await _tmdbCache.GetMovieAsync(tmdb, ct);   // warms tmdb_cache
        else
            await _tmdbCache.GetSeriesAsync(tmdb, ct);  // warms tmdb_cache
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex,
            "tmdb_cache warm failed for {Type}/{Tmdb}; channel browse will retry lazily",
            type, tmdb);
        // Don't block discovery refresh on individual TMDB failures.
    }
}
```

Tests: `DiscoveryRefreshTaskTests` — TMDB calls fired; rows
written; TTL eviction protects materialised + favourited; DataVersion
bumped; **tmdb_cache rows present for every discovered tmdb id after
task completes** (critic IMPORTANT 4 regression test).

### Stage 3.2 — Gostream filesystem enumerator

NEW file: `Channels/GostreamFilesystemEnumerator.cs`.

```csharp
public sealed class GostreamFilesystemEnumerator
{
    public async Task<IReadOnlyList<GostreamFileEntry>> EnumerateMoviesAsync(CancellationToken ct);
    public async Task<IReadOnlyList<GostreamSeriesEntry>> EnumerateSeriesAsync(CancellationToken ct);
}

public sealed record GostreamFileEntry(string Path, int? TmdbId);
public sealed record GostreamSeriesEntry(string DirectoryPath, int? TmdbId, /* season+episode listings */);
```

Behaviour:
- For movies: walk `/var/gostream/gostream-mkv-virtual/movies/`.
  Cross-reference with `materialised_state` table to recover TMDB id
  for known files. For unknown files: leave TmdbId null. Channel
  emits these as `orphan_<hash>` items with raw-filename Name.
  (Critic v2 R4: this is the day-1 regression for the operator's
  ~131 pre-existing un-materialised gostream files. Acknowledged;
  ship as-is. Operator can opt into TMDB title-search via
  `PluginConfiguration.EnrichOrphanGostreamItemsViaTmdbSearch =
  true` later.)
- For series: walk `/var/gostream/gostream-mkv-virtual/tv/`. Each
  subdirectory is a series; enumerate season subdirs and their
  episode files. Same TMDB-id recovery pattern.

No dependency on a new gostream API. Critic v2 IMPORTANT 8 fix —
authoritative source for `(tmdb, fuse_path)` mapping is our own
`materialised_state` table, not gostream.

Tests: enumerator returns expected `(path, tmdb_id)` tuples from a
temp directory + a mock `materialised_state`.

### Stage 3.3 — `PhantomMoviesChannel.GetChannelItems` full implementation

Replace the empty stub from Stage 2.4.

**Key design (critic round 3 BLOCKER 1 fix):** the channel returns ONE
ChannelItemInfo per (tmdb_id, type) tuple regardless of whether it's
materialised. The external id is stable across the materialise
transition; only the `MediaSources` field differs. The set of items
emitted is the **union** of (a) the materialised_state rows for movies
+ (b) the discovery_cache rows for movies + (c) orphan gostream files
with no known tmdb. For (a) ∩ (b) overlap (a movie was in discovery
and has now been materialised), the union is naturally dedup'd by
external id.

Implementation:

```csharp
async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
{
    // Movies channel is flat: only top-level browse (FolderId null/empty).
    if (!string.IsNullOrEmpty(query.FolderId))
        return new ChannelItemResult { Items = Array.Empty<ChannelItemInfo>() };

    var materialised = await _db.ListMaterialisedStateAsync("movie", ct);
    var phantoms = await _db.ListDiscoveryCacheAsync("movie", ct);
    var orphans = await _enumerator.EnumerateOrphanMoviesAsync(
        knownTmdbs: materialised.Select(m => m.TmdbId).ToHashSet(),
        ct);

    var emitted = new HashSet<string>(StringComparer.Ordinal);
    var items = new List<ChannelItemInfo>();

    // 1. Materialised movies (real FUSE-path MediaSource)
    foreach (var m in materialised)
    {
        var id = ChannelItemId.ForMovie(m.TmdbId).Encode();
        if (!emitted.Add(id)) continue;
        var info = await BuildMovieItemAsync(m.TmdbId, materialised: m, ct);
        if (info is not null) items.Add(info);
    }

    // 2. Phantoms (splash MediaSource); skip any whose tmdb is already materialised
    foreach (var p in phantoms)
    {
        var id = ChannelItemId.ForMovie(p.TmdbId).Encode();
        if (!emitted.Add(id)) continue;
        var info = await BuildMovieItemAsync(p.TmdbId, materialised: null, ct);
        if (info is not null) items.Add(info);
    }

    // 3. Orphan gostream files (no tmdb id known)
    foreach (var o in orphans)
    {
        var id = ChannelItemId.ForOrphanPath(o.Path).Encode();
        if (!emitted.Add(id)) continue;
        var info = BuildOrphanMovieItem(o);
        if (info is not null) items.Add(info);
    }

    return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
}
```

**`BuildMovieItemAsync(int tmdb, MaterialisedStateRow? materialised, CancellationToken ct)`** — unified builder for both phantom and materialised:

```csharp
async Task<ChannelItemInfo?> BuildMovieItemAsync(int tmdb, MaterialisedStateRow? materialised, CancellationToken ct)
{
    var metadata = await _tmdbCache.GetMovieAsync(tmdb, ct);
    if (metadata is null) return null;   // tmdb cache miss; skip this tick

    var mediaSource = materialised is not null
        ? new MediaSourceInfo
          {
              Path = materialised.FusePath,
              Container = Path.GetExtension(materialised.FusePath).TrimStart('.'),
              Protocol = MediaProtocol.File,
              SupportsDirectPlay = true,
              SupportsDirectStream = true,
              IsRemote = false,
          }
        : _splashSource.GetMediaSource();   // shared splash MediaSource

    var info = new ChannelItemInfo
    {
        Id = ChannelItemId.ForMovie(tmdb).Encode(),
        Name = metadata.Title,
        Type = ChannelItemType.Media,
        ContentType = ChannelMediaContentType.Movie,
        MediaType = ChannelMediaType.Video,
        ImageUrl = metadata.PosterUrl,
        ProductionYear = metadata.Year,
        Overview = metadata.Overview,
        Genres = metadata.Genres?.ToList() ?? new List<string>(),
        OfficialRating = metadata.OfficialRating,
        CommunityRating = metadata.Rating,
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = tmdb.ToString() },
        MediaSources = new List<MediaSourceInfo> { mediaSource },
        Tags = materialised is null ? new List<string> { "phantom" } : new List<string>(),
    };
    return info;
}
```

**`BuildOrphanMovieItem(GostreamFileEntry o)`** — raw-filename fallback for gostream content the plugin didn't put there:

```csharp
ChannelItemInfo BuildOrphanMovieItem(GostreamFileEntry o)
{
    return new ChannelItemInfo
    {
        Id = ChannelItemId.ForOrphanPath(o.Path).Encode(),
        Name = Path.GetFileNameWithoutExtension(o.Path),
        Type = ChannelItemType.Media,
        ContentType = ChannelMediaContentType.Movie,
        MediaType = ChannelMediaType.Video,
        MediaSources = new List<MediaSourceInfo>
        {
            new MediaSourceInfo
            {
                Path = o.Path,
                Container = Path.GetExtension(o.Path).TrimStart('.'),
                Protocol = MediaProtocol.File,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
            }
        },
        Tags = new List<string> { "orphan" },
    };
}
```

**`GetChannelItemMediaInfo(string id, CancellationToken ct)`** (IRequiresMediaInfoCallback):
```csharp
async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
{
    var parsed = ChannelItemId.Parse(id);
    switch (parsed.Kind)
    {
        case "movie":
            var m = await _db.GetMaterialisedStateAsync(parsed.TmdbId!.Value, "movie", -1, -1, ct);
            return m is not null
                ? new[] { BuildMaterialisedMediaSource(m.FusePath) }
                : new[] { _splashSource.GetMediaSource() };
        case "orphan":
            // Orphan files emit their path directly; this callback should
            // not normally be needed (ChannelManager caches the static
            // MediaSource), but return it again for safety.
            return new[] { BuildOrphanMediaSource(parsed.OrphanHash!) };
        default:
            return Array.Empty<MediaSourceInfo>();
    }
}
```

**`GetChannelItemAsync(string externalId, CancellationToken ct)`** (IChannelItemRefresh):
```csharp
async Task<ChannelItemInfo?> GetChannelItemAsync(string externalId, CancellationToken ct)
{
    var parsed = ChannelItemId.Parse(externalId);
    return parsed.Kind switch
    {
        "movie" => await BuildMovieItemAsync(
            parsed.TmdbId!.Value,
            await _db.GetMaterialisedStateAsync(parsed.TmdbId.Value, "movie", -1, -1, ct),
            ct),
        "orphan" => _enumerator.LookupOrphanByHashAsync(parsed.OrphanHash!, ct) is { } file
            ? BuildOrphanMovieItem(file) : null,
        _ => null,
    };
}
```

The patched `RefreshChannelItemAsync` calls `GetChannelItemAsync(externalId)`
after materialise; the channel returns the fresh ChannelItemInfo with the
FUSE-path MediaSource; ChannelManager (with `forceUpdate=true`) persists
the new Path + MediaSources against the **same BaseItem.Id** as before
(because the external id is unchanged). UserData on that BaseItem.Id is
preserved.

Per critic v2 BLOCKER 6 (dual MediaSources): phantom channels do NOT
declare `ISupportsMediaProbe`. Static MediaSources are persisted by
ChannelManager from whatever `GetChannelItems` returns. Combined with
the patched RefreshChannelItem invalidating both static + cache on
materialise, the user sees one consistent MediaSource per browse + play
cycle. The static MediaSource for phantoms is the splash; for
materialised items, the static is the real-file MediaSource. The
static + dynamic concat at `MediaSourceManager.cs:177-198` returns the
same MediaSource from both paths — visible as one entry in the play UI
because they share content/path/id. (Verify in rig.)

`DataVersion`: read from a small in-memory bumpable counter in
`Channels/ChannelStateProvider.cs` (NEW) backed by `plugin_meta`
row. Bumped by `DiscoveryRefreshTask` and `Materialiser` (on
successful materialise).

Tests: `PhantomMoviesChannelTests`:
- empty discovery + empty gostream → empty result;
- discovery + materialised dedup: same tmdb appears in both → ONE
  ChannelItemInfo emitted with the materialised MediaSource;
- orphan gostream files emit `orphan_<hash>` ids;
- FolderId not empty → empty result;
- **critical regression test**: materialise tmdb=42, re-call
  `GetChannelItems`, assert ChannelItemInfo.Id is
  `"movie_42"` (NOT `"phantom_movie_42"` or `"real_movie_42"`);
  assert there is exactly one item per (tmdb_id, type) tuple.

### Stage 3.4 — Validation

Rig scenario `scenarios/30-channel-discovery.sh`:
1. Wipe state.
2. Start rig Jellyfin.
3. Hand-seed `discovery_cache` with 3 movie TMDB ids.
4. Hand-seed `/var/gostream/gostream-mkv-virtual/movies/` with 2
   real files (one matching a discovery TMDB id, one orphan).
5. `GET /Channels/<phantomMoviesId>/Items` via REST.
6. Assert response contains 4 items: 1 real with matching tmdb (real
   wins), 1 orphan, 2 phantoms. Names correct; phantoms tagged
   `"phantom"`.
7. Trigger `DiscoveryRefreshTask` via Dashboard endpoint. Assert
   re-fetch happens, DataVersion bumped, channel re-queried by
   ChannelManager on next browse.

Commit Phase 3 to main.

---

## Phase 4: Materialise flow

### Stage 4.1 — `TmdbExternalIdResolver`

NEW file: `Channels/TmdbExternalIdResolver.cs`. Per critic v2
IMPORTANT.

```csharp
public sealed class TmdbExternalIdResolver
{
    private const int NegativeCacheTtlHours = 24;
    private const int PositiveCacheTtlDays = 30;

    public async Task<string?> GetImdbIdAsync(int tmdbId, string type, CancellationToken ct)
    {
        // Check cache
        var cached = await _db.GetImdbIdAsync(tmdbId, type, ct);
        if (cached.HasEntry)
        {
            if (cached.ImdbId != null) return cached.ImdbId;
            if (DateTimeOffset.UtcNow - cached.FetchedAt < TimeSpan.FromHours(NegativeCacheTtlHours))
                return null;   // still in negative-cache window
        }

        // Fetch from TMDB
        string? imdbId = null;
        try
        {
            imdbId = type == "movie"
                ? await _tmdb.GetImdbIdForMovieAsync(tmdbId, ct)
                : await _tmdb.GetImdbIdForSeriesAsync(tmdbId, ct);
        }
        catch (Exception ex)
        {
            // Transient: don't poison cache; return null but don't write
            _logger.LogWarning(ex, "TMDB external_ids fetch failed for {Type}/{Tmdb}; not caching", type, tmdbId);
            return null;
        }

        // Cache (positive or negative)
        await _db.SetImdbIdAsync(tmdbId, type, imdbId, ct);
        return imdbId;
    }
}
```

Tests: cache hit (positive); cache hit (negative, within TTL); cache
miss (positive fetch); cache miss (negative fetch); fetch failure
(no cache poison).

### Stage 4.2 — Materialiser refactor

EDIT `Materialisation/Materialiser.cs`.

**Critic round 3 BLOCKER 4 fix:** the tuple-rewrite must explicitly
source Title, Year, IMDB, and (for episodes) SeriesImdb from places
other than a BaseItem (because under channels the BaseItem is
scanner-managed and may not exist at materialise-call time). The
existing Materialiser does this via BaseItem properties; the tuple
version sources from `tmdb_cache` + `tmdb_external_ids`.

**Critic round 3 BLOCKER 2 fix:** the pre-flight `RefreshChannelItemAsync`
call MUST be inside the try/finally that protects the in-flight row.
If it throws, the in-flight row gets stuck.

#### Helper inventory (existing helpers + tuple-form changes)

| Helper today | Today's signature | Tuple-form change |
|---|---|---|
| `MaterialiseCoreAsync` | `(Guid jellyfinItemId, MaterialiseTrigger, CancellationToken)` | new entry: `(int tmdbId, string type, int? season, int? episode, MaterialiseTrigger, CancellationToken)` |
| `ResolveProviderIdsAsync(item, ct)` | reads from BaseItem.ProviderIds | replaced by direct lookup: tmdb passed in, imdb via `_externalIds.GetImdbIdAsync` |
| Year enrichment (Materialiser.cs:200-241) | reads from BaseItem.ProductionYear + writes back via UpdateItemAsync | replaced: read year from `_tmdbCache.GetMovieAsync(tmdb).Year` (or `.GetSeriesAsync(tmdb).Year` for episodes); no writeback (we don't own BaseItems) |
| `TryExtractIdentifiers(item, out ids)` | reads from BaseItem | inlined into MaterialiseCoreAsync; constructs identifiers tuple directly |
| `IsMarkedUnavailableAsync(ids, ct)` | takes identifiers tuple | unchanged — already tuple-based |
| `SourcePicker.Pick(...)` | **needs verification** — see Stage 4.2.0 below | likely needs a tuple variant: `Pick(int tmdbId, string? imdbId, string type, int? season, int? episode, string title, int? year, string presetName, ct)` |
| Magnet cache (`MagnetCacheKey`) | already tuple-keyed `(TmdbId?, ImdbId?, Type, Season, Episode, Preset)` | unchanged |
| `BuildGostreamRequest` (new helper) | n/a | constructs `GostreamAddRequest` from tuple + tmdb_cache lookups (see body below) |
| `WaitForFusePathAsync(path, ct)` | unchanged | unchanged — already path-based, BaseItem-free |
| `PromoteItemAsync(item, fusePath, ct)` | mutates BaseItem.Path + re-parents via `FindPhysicalFolderForPath` | **DELETED**; replaced by `_refreshManager.RefreshChannelItemAsync` |
| `LogAsync(sw, id, trigger, outcome, ...)` | takes Guid id | takes string `(externalId)` instead; logged for observability only |

#### Stage 4.2.0 — extract magnet-selection logic to a tuple-friendly form

The existing Materialiser at `Materialisation/Materialiser.cs:315-410` does
the magnet selection inline using:
- `_indexers` (an `IIndexerClient` chain, e.g. Prowlarr/Torrentio)
- `IndexerQuery` constructed from BaseItem properties
- `IndexerCandidate` list returned by each indexer
- `_scorer.PickBest(candidates, _config)` (`QualityScorer.PickBest`)
- magnet-cache lookup/write via `_db.GetMagnetCacheAsync` / `_db.UpsertMagnetCacheAsync`
- unavailable-marker plumbing via `_db.IsMarkedUnavailableAsync` /
  `_db.MarkUnavailableAsync` (see `Stage 4.2 critic-blocker-2 fix` below)

**There is no existing `SourcePicker` class**; the v3 spec's reference
to `_sourcePicker.PickAsync(SourcePickInputs)` was wrong. The implementer
has two viable shapes to pick from:

**Option A (recommended):** extract the existing inline logic into a new
`Sources/MagnetSelector.cs` class with tuple inputs:

```csharp
public sealed class MagnetSelector
{
    public async Task<MagnetCandidate?> SelectAsync(
        int tmdbId, string? imdbId,
        string type, int? season, int? episode,
        string title, int? year,
        string presetName,
        CancellationToken ct);
}
public sealed record MagnetCandidate(
    string Magnet, string InfoHash, long Size, int Seeders, string Indexer);
```

Inside `SelectAsync`: build `IndexerQuery` from the tuple inputs;
run the existing `_indexers` chain (each `IIndexerClient` returns
`IReadOnlyList<IndexerCandidate>`); aggregate; pass to existing
`QualityScorer.PickBest`; return chosen candidate.

**Option B:** keep the magnet selection inline inside
`BuildGostreamRequest` (do not introduce a new abstraction).
Larger BuildGostreamRequest body but no new class.

Recommend Option A: keeps Materialiser focused on the materialise
lifecycle; makes the magnet-selection independently testable.
MagnetSelector is registered as a singleton in
`PluginServiceRegistrator.cs`.

Whichever is chosen, update the `BuildGostreamRequest` body in the
next subsection to match.

#### `tmdb_cache` schema (explicit; this was hand-waved in v2)

```sql
CREATE TABLE IF NOT EXISTS tmdb_cache (
    tmdb_id        INTEGER NOT NULL,
    type           TEXT NOT NULL,    -- 'movie' or 'series'
    title          TEXT NOT NULL,
    year           INTEGER,
    overview       TEXT,
    poster_url     TEXT,
    backdrop_url   TEXT,
    genres         TEXT,             -- JSON array of strings
    official_rating TEXT,
    community_rating REAL,
    original_title TEXT,
    fetched_at     INTEGER NOT NULL,
    PRIMARY KEY (tmdb_id, type)
);
CREATE INDEX IF NOT EXISTS idx_tmdb_cache_fetched_at
    ON tmdb_cache(fetched_at);
```

Written by `_tmdbCache.GetMovieAsync` / `.GetSeriesAsync` (existing
`CachedTmdbReader` adapter; verify schema matches its writes).

For episodes: `tmdb_episode_cache` separately (similar shape; key
`(series_tmdb_id, season, episode)`; carries title, episode-level
overview, still_url). Spec'd in Stage 5.1 since the shows channel
needs it for episode display.

#### `BuildGostreamRequest` body

```csharp
async Task<GostreamAddRequest> BuildGostreamRequest(
    int tmdbId,
    string type,                     // "movie" or "episode"
    int? season,
    int? episode,
    string? imdb,                    // movie's own imdb (movies) or series-imdb (episodes)
    CancellationToken ct)
{
    // Source title + year from tmdb_cache (not BaseItem)
    var tmdbType = type == "movie" ? "movie" : "series";
    var cached = await _tmdbCache.GetByIdAsync(tmdbId, tmdbType, ct);
    if (cached is null)
        throw new InvalidOperationException(
            $"tmdb_cache miss for {tmdbType}/{tmdbId}; cannot build gostream request");
    if (string.IsNullOrEmpty(cached.Title))
        throw new InvalidOperationException(
            $"tmdb_cache row for {tmdbType}/{tmdbId} has empty Title");
    if (!cached.Year.HasValue)
        throw new InvalidOperationException(
            $"tmdb_cache row for {tmdbType}/{tmdbId} has null Year; gostream requires year");

    // Select magnet via SourcePicker (or pull from MagnetCache)
    var magnetKey = new MagnetCacheKey(
        TmdbId: tmdbId, ImdbId: imdb, Type: type,
        Season: season, Episode: episode,
        Preset: _config.SourcePickerPreset);
    var cachedMagnet = await _db.GetMagnetCacheAsync(magnetKey, ct);
    // Select magnet via MagnetSelector (Stage 4.2.0) or pull from MagnetCache
    var magnetKey = new MagnetCacheKey(
        TmdbId: tmdbId, ImdbId: imdb, Type: type,
        Season: season, Episode: episode,
        Preset: _config.SourcePickerPreset);
    var cachedMagnet = await _db.GetMagnetCacheAsync(magnetKey, ct);
    MagnetCandidate? magnet = cachedMagnet is not null
        ? new MagnetCandidate(cachedMagnet.Magnet, cachedMagnet.InfoHash, cachedMagnet.Size, cachedMagnet.Seeders, cachedMagnet.Indexer)
        : await _magnetSelector.SelectAsync(
            tmdbId, imdb, type, season, episode,
            cached.Title, cached.Year,
            _config.SourcePickerPreset, ct);

    // BLOCKER 2 fix (critic round 3): if no magnet found, write the
    // unavailable marker so future calls within UnavailableRetryAfter
    // (default 24h) short-circuit at the gate above and don't re-hit
    // the indexer chain. Mirrors existing Materialiser behaviour
    // (Materialiser.cs:382, 438). Without this, autopilot loops on
    // UserDataSaved trigger an indexer storm for episodes lacking torrents.
    if (string.IsNullOrEmpty(magnet?.Magnet))
    {
        var unavailKeyMiss = new UnavailableKey(
            TmdbId: tmdbId, ImdbId: imdb, Type: type,
            Season: season, Episode: episode);
        await _db.MarkUnavailableAsync(
            unavailKeyMiss,
            ttl: TimeSpan.FromHours(_config.UnavailableRetryAfterHours),
            ct);
        throw new InvalidOperationException(
            $"MagnetSelector returned no magnet for {tmdbType}/{tmdbId} (season={season} episode={episode}); marked unavailable for {_config.UnavailableRetryAfterHours}h");
    }

    // Cache the discovered magnet for the configured TTL.
    if (cachedMagnet is null)
    {
        await _db.UpsertMagnetCacheAsync(magnetKey, new MagnetCacheEntry
        {
            Magnet = magnet.Magnet,
            InfoHash = magnet.InfoHash,
            Size = magnet.Size,
            Seeders = magnet.Seeders,
            Indexer = magnet.Indexer,
            CachedAt = DateTimeOffset.UtcNow,
            Ttl = TimeSpan.FromHours(_config.MagnetCacheTtlHours),
            Source = "user",
        }, ct);
    }

    return new GostreamAddRequest
    {
        Type = type,
        Tmdb = tmdbId,
        Imdb = type == "movie" ? imdb : null,
        SeriesImdb = type == "episode" ? imdb : null,
        Title = cached.Title,
        Year = cached.Year,
        Season = season,
        Episode = episode,
        Magnet = magnet.Magnet,
        MinQuality = _config.GostreamMinQuality,
    };
}
```

#### Tuple-signature body (final, with restructured try/finally)

```csharp
async Task<MaterialisationOutcome> MaterialiseAsync(
    int tmdbId, string type, int? season, int? episode,
    MaterialiseTrigger trigger,
    CancellationToken ct)
{
    // Series-level reject (critic round 3 IMPORTANT 8). Users can
    // right-click a Series tile and hit Materialise; tuple receives
    // type="series". Episode-level materialise is the supported path.
    if (type == "series")
        return MaterialisationOutcome.Error(
            "Series-level materialise not supported; materialise individual episodes");

    if (type != "movie" && type != "episode")
        return MaterialisationOutcome.Error($"Unsupported type: {type}");

    var (s, e) = ChannelItemId.ToSentinels(season, episode);

    // Idempotency: skip if already materialised
    if (await _db.GetMaterialisedStateAsync(tmdbId, type, s, e, ct) is not null)
        return MaterialisationOutcome.Duplicate;

    // Idempotency: skip if already in flight
    if (await _db.IsMaterialiseInFlightAsync(tmdbId, type, s, e, ct))
        return MaterialisationOutcome.AlreadyInProgress;

    // Unavailable-marker gate (critic round 3 BLOCKER 2 fix). The
    // existing Materialiser writes a row into `unavailable_marker`
    // whenever the indexer chain returns no acceptable candidate; the
    // marker carries a TTL (UnavailableRetryAfter, default 24h) so
    // future attempts within that window short-circuit. Without this,
    // autopilot loops on UserDataSaved trigger an indexer storm for
    // every episode lacking a torrent. Keep the table; keep the
    // gate; keep the write.
    var unavailKey = new UnavailableKey(
        TmdbId: tmdbId, ImdbId: imdb, Type: type,
        Season: season, Episode: episode);
    if (await _db.IsMarkedUnavailableAsync(unavailKey, ct))
        return MaterialisationOutcome.Error(
            $"Marked unavailable (within retry window); skipping {type}/{tmdbId} s{season} e{episode}");

    // Resolve IMDB (movies: own imdb; episodes: series imdb)
    var imdbLookupType = type == "episode" ? "series" : "movie";
    var imdb = await _externalIds.GetImdbIdAsync(tmdbId, imdbLookupType, ct);
    if (type == "episode" && string.IsNullOrEmpty(imdb))
        return MaterialisationOutcome.Error(
            $"Could not resolve IMDB id for series tmdb={tmdbId}; gostream requires series_imdb for episodes");

    var channelId = ChannelIds.For(type == "movie" ? "movies" : "shows");
    var externalId = type == "movie"
        ? ChannelItemId.ForMovie(tmdbId).Encode()
        : ChannelItemId.ForEpisode(tmdbId, season!.Value, episode!.Value).Encode();

    // BLOCKER 2 fix: the pre-flight in-flight write + RefreshChannelItem
    // are INSIDE the try/finally so that a throw from either path
    // doesn't leak the in-flight row.
    await _db.UpsertMaterialiseInFlightAsync(tmdbId, type, s, e, ct);
    try
    {
        // Bump DataVersion + tell channel to surface "Materialising"
        // state. If RefreshChannelItem throws, the catch below logs and
        // returns Error; finally deletes the in-flight row cleanly.
        _state.BumpDataVersion(type == "movie" ? "movies" : "shows");
        try
        {
            await _refreshManager.RefreshChannelItemAsync(
                channelId, externalId,
                new ChannelItemRefreshOptions
                {
                    ForceUpdate = true,
                    ForceProbe = false,
                    InvalidateMediaInfoCache = true,
                },
                ct);
        }
        catch (Exception refreshEx)
        {
            // Non-fatal: badge won't show "Materialising" but the
            // gostream call below still proceeds.
            _logger.LogWarning(refreshEx,
                "Pre-flight RefreshChannelItem failed for {External}; badge may stay 'Phantom' during materialise",
                externalId);
        }

        // gostream flow (uses BuildGostreamRequest helper above)
        var addRequest = await BuildGostreamRequest(tmdbId, type, season, episode, imdb, ct);
        var addResult = await _gostream.AddAsync(addRequest, ct);
        await WaitForFusePathAsync(addResult.FusePath, ct);

        // Persist materialised state
        await _db.InsertMaterialisedStateAsync(
            tmdbId, type, s, e,
            stubPath: addResult.StubPath,
            fusePath: addResult.FusePath,
            ct);

        // Post-flight refresh: channel now emits real MediaSource;
        // probe gets re-run. THIS one is the load-bearing call; if it
        // throws, fall through to the catch (we still wrote
        // materialised_state, so the next browse will pick it up
        // even if this immediate refresh failed).
        await _refreshManager.RefreshChannelItemAsync(
            channelId, externalId,
            new ChannelItemRefreshOptions
            {
                ForceUpdate = true,
                ForceProbe = true,
                InvalidateMediaInfoCache = true,
            },
            ct);

        _state.BumpDataVersion(type == "movie" ? "movies" : "shows");
        return MaterialisationOutcome.Success(addResult.FusePath, addResult.StubPath);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        _logger.LogError(ex, "MaterialiseAsync failed for {Type}/{Tmdb} (s={Season} e={Episode})",
            type, tmdbId, season, episode);
        return MaterialisationOutcome.Error(ex.Message);
    }
    finally
    {
        // Always clean up the in-flight row, even on RefreshChannelItem
        // failure / gostream failure / process kill (the
        // MaterialiseInFlightSweeper handles process-kill below).
        try
        {
            await _db.DeleteMaterialiseInFlightAsync(tmdbId, type, s, e, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to delete in-flight row for {Type}/{Tmdb}; will be swept on next startup",
                type, tmdbId);
        }
    }
}
```

Legacy Guid signature becomes a thin wrapper that resolves Guid → channel
item id → tuple:

```csharp
public async Task<MaterialisationOutcome> MaterialiseAsync(
    Guid jellyfinItemId, MaterialiseTrigger trigger, CancellationToken ct)
{
    var item = _libraryManager.GetItemById(jellyfinItemId);
    if (item is null)
        return MaterialisationOutcome.Error($"BaseItem {jellyfinItemId} not found");
    if (item.SourceType != SourceType.Channel)
        return MaterialisationOutcome.Error("Item is not a channel item");
    if (!ChannelIds.IsPhantom(item.ChannelId))
        return MaterialisationOutcome.Error("Item is not in a phantom-library channel");

    if (!ChannelItemId.TryParse(item.ExternalId, out var parsed))
        return MaterialisationOutcome.Error(
            $"Unparseable channel external id: {item.ExternalId}");

    return parsed.Kind switch
    {
        "movie"   => await MaterialiseAsync(parsed.TmdbId!.Value, "movie", null, null, trigger, ct),
        "episode" => await MaterialiseAsync(parsed.TmdbId!.Value, "episode", parsed.Season, parsed.Episode, trigger, ct),
        "series"  => MaterialisationOutcome.Error(
            "Series-level materialise not supported; materialise individual episodes"),
        "season"  => MaterialisationOutcome.Error(
            "Season-level materialise not supported; materialise individual episodes"),
        "orphan"  => MaterialisationOutcome.Error(
            "Orphan gostream files are already materialised"),
        _         => MaterialisationOutcome.Error($"Unknown item kind: {parsed.Kind}"),
    };
}
```

Delete `PromoteItemAsync` (no longer needed — `RefreshChannelItemAsync`
does its job). Also delete `FindPhysicalFolderForPath` and any other
helpers that were exclusively used by `PromoteItemAsync`.

Kebab JS shim: per critic IMPORTANT 8, also filter at the JS layer so
the user can't even click Materialise on a Series/Season folder tile.
EDIT `Configuration/phantomKebab.js`: in the kebab-injection routine,
skip items whose detected ContentType is `Series` or `Season`. This
is defence-in-depth; the server-side reject above is authoritative.

Add **startup sweep** of stale `materialise_in_flight` rows (critic
v2 IMPORTANT). NEW hosted service `Materialisation/MaterialiseInFlightSweeper.cs`:

```csharp
public sealed class MaterialiseInFlightSweeper : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
            try
            {
                var purged = await _db.PurgeStaleMaterialiseInFlightAsync(
                    TimeSpan.FromMinutes(_config.MaterialiseInFlightStaleMinutes), ct);
                if (purged > 0)
                    _logger.LogInformation("Purged {N} stale materialise_in_flight rows on startup", purged);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Startup sweep failed"); }
        }, ct);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

Register in `PluginServiceRegistrator.cs`. Default
`MaterialiseInFlightStaleMinutes = 10` in `PluginConfiguration.cs`.

Tests: tuple signature happy path; episode without IMDB → error;
in-flight idempotency; already-materialised idempotency; gostream
failure → no materialised_state row; startup sweep deletes >10min
old rows; sweep skips fresh rows.

### Stage 4.3 — Badge controller rewrite

EDIT `Api/PhantomLibraryBadgesController.cs`:

```pseudocode
foreach guid in request.Ids:
    var baseItem = _libraryManager.GetItemById(guid)
    if (baseItem?.SourceType != SourceType.Channel) continue
    if (!ChannelIds.IsPhantom(baseItem.ChannelId)) continue

    var parsed = ChannelItemId.Parse(baseItem.ExternalId)
    var (s, e) = ChannelItemId.ToSentinels(parsed.Season, parsed.Episode)

    string state
    if (await _db.GetMaterialisedStateAsync(parsed.TmdbId, parsed.Type, s, e, ct) is not null)
        state = "Materialised"
    else if (await _db.IsMaterialiseInFlightAsync(parsed.TmdbId, parsed.Type, s, e, ct))
        state = "Materialising"
    else
        state = "Phantom"

    yield (guid, state)
```

`phantomBadges.js` JS shim unchanged (queries `/States` with GUIDs).

Tests: state derivation for each combination.

### Stage 4.4 — Validation

Rig scenario `scenarios/31-channel-materialise.sh`:
1. Wipe + start Jellyfin + DiscoveryRefresh + verify channel shows phantoms.
2. Pick one phantom movie (known TMDB id). Click Play in the rig
   client; assert splash plays.
3. Trigger materialise via `POST /Plugins/PhantomLibrary/Materialise`
   with the channel item id.
4. Wait for `materialise_in_flight` row to appear AND `materialised_state`
   to be written.
5. Re-browse the channel; assert the item's MediaSource Path is now
   the gostream FUSE path, NOT the splash.
6. Click Play again; assert real file plays.
7. Inspect `BaseItem.MediaStreams` — assert they reflect the real
   file's video/audio codecs, NOT the splash's. **Regression test
   for critic v2 BLOCKER 4 (probe pinning).**
8. Re-inspect after the 5-minute `_memoryCache` window has nominally
   expired — but actually we don't need to wait, because the patched
   RefreshChannelItem invalidated the cache. Confirm via a fresh
   `GET /Items/<guid>/PlaybackInfo` returning the real file
   immediately. **Regression test for critic v2 BLOCKER 5.**
9. Crash-simulation: between Stage 4 (in-flight row written) and
   Stage 5 (materialised_state row written), kill rig Jellyfin. Restart.
   Assert MaterialiseInFlightSweeper deletes the stale row after
   ~15s + 10min (or shorten the threshold for the test).

Commit Phase 4 to main.

---

## Phase 5: Shows channel

### Stage 5.1 — `PhantomShowsChannel` hierarchical implementation

Replace stub from Stage 2.4. Mirror `PhantomMoviesChannel` structure
plus folder navigation. Same single-id-per-logical-item discipline
(critic round 3 BLOCKER 1): `series_<tmdb>` for series folders,
`season_<tmdb>_s<NN>` for season folders, `episode_<tmdb>_s<NN>e<NN>`
for episode media items — stable across materialise.

```csharp
async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
{
    if (string.IsNullOrEmpty(query.FolderId))
        return await GetTopLevelSeriesAsync(ct);

    if (!ChannelItemId.TryParse(query.FolderId, out var parsed))
        return new ChannelItemResult { Items = Array.Empty<ChannelItemInfo>() };

    return parsed.Kind switch
    {
        "series" => await GetSeasonsForSeriesAsync(parsed.TmdbId!.Value, ct),
        "season" => await GetEpisodesForSeasonAsync(parsed.TmdbId!.Value, parsed.Season!.Value, ct),
        _        => new ChannelItemResult { Items = Array.Empty<ChannelItemInfo>() },
    };
}
```

Series-level items: ChannelItemType.Folder + ChannelFolderType.Series.
Per the explore agent's finding, `BaseItem.cs:1815` preserves
`Series` type for Channel-sourced items.

Season-level items: ChannelItemType.Folder + ChannelFolderType.Season.
Carry `ParentIndexNumber = null` and `IndexNumber = season_number`.
Per the explore agent: `Season` type also preserved.

Episode-level items: ChannelItemType.Media + ChannelMediaContentType.Episode.
Carry `SeriesName`, `ParentIndexNumber = season_number`, `IndexNumber = episode_number`.

Per-episode real-vs-phantom check uses `GostreamFilesystemEnumerator`
+ `materialised_state` lookup. Each episode's MediaSource is either
the real FUSE path or the splash.

**`GetChannelItemAsync(string externalId, CancellationToken ct)`** (IChannelItemRefresh) — critic round 3 IMPORTANT 5 fix. The patched `RefreshChannelItemAsync` (called by Materialiser post-flight for episodes) drives single-item refresh through this method. Without explicit handling for `series`/`season`/`episode` kinds, the patched manager falls back to paging the root — which returns series-level folders, not episodes — and the episode external id never matches → refresh silently no-ops → `forceUpdate=true` never fires → BaseItem.Path stays at splash → user plays splash post-materialise. The fix is to explicitly resolve every kind the shows channel emits.

```csharp
async Task<ChannelItemInfo?> GetChannelItemAsync(string externalId, CancellationToken ct)
{
    if (!ChannelItemId.TryParse(externalId, out var parsed))
        return null;

    switch (parsed.Kind)
    {
        case "series":
            // Rebuild the same ChannelItemInfo the top-level browse would emit
            return await BuildSeriesItemAsync(parsed.TmdbId!.Value, ct);

        case "season":
            return await BuildSeasonItemAsync(
                parsed.TmdbId!.Value, parsed.Season!.Value, ct);

        case "episode":
            // The Materialiser post-flight refresh hits this path. Build
            // the episode ChannelItemInfo with the current MediaSource
            // (real FUSE if materialised, splash if not).
            var materialised = await _db.GetMaterialisedStateAsync(
                parsed.TmdbId!.Value, "episode",
                parsed.Season!.Value, parsed.Episode!.Value, ct);
            return await BuildEpisodeItemAsync(
                parsed.TmdbId.Value, parsed.Season.Value, parsed.Episode.Value,
                materialised: materialised, ct);

        default:
            return null;
    }
}
```

`BuildSeriesItemAsync`, `BuildSeasonItemAsync`, `BuildEpisodeItemAsync`
are the same helpers used by `GetTopLevelSeriesAsync`,
`GetSeasonsForSeriesAsync`, `GetEpisodesForSeasonAsync`. Extract into
shared private methods on `PhantomShowsChannel` so the browse path
and the refresh path emit identical ChannelItemInfos.

Tests: `PhantomShowsChannelTests.GetChannelItemAsync_*` — verify each
kind round-trips correctly; verify episode refresh after materialise
returns ChannelItemInfo with FUSE-path MediaSource (the critical
regression scenario per critic IMPORTANT 5).

### Stage 5.2 — `SeriesAutopilot` rewrite

Replace `Materialisation/SeriesAutopilot.cs` body:

```pseudocode
async OnUserDataSavedAsync(UserDataSavedEventArgs evt):
    if (evt.UserData.PlayedPercentage < 80) return

    // SPLASH GUARD (critic v2 IMPORTANT 9 + critic round 3 IMPORTANT 5
    // refinement): refuse to count a play of an unmaterialised phantom.
    //
    // v3 of the plan used a 2-minute runtime threshold; v4 (this) uses
    // the channel's `phantom` tag instead. Reason: after materialise,
    // ChannelManager re-runs the probe (via our patched forceProbe path)
    // but persistence is async — BaseItem.RunTimeTicks may briefly be
    // null for the freshly-materialised episode, and a 2-min threshold
    // would suppress autopilot on the user's first real play of that
    // episode (the very session where prefetch matters most). The
    // `phantom` tag is set by `BuildEpisodeItemAsync` only when
    // materialised_state has no row; once materialised, the tag is gone
    // and the play counts toward autopilot trigger.
    var item = _libraryManager.GetItemById(evt.ItemId)
    if (item is null) return
    if (item.Tags is not null && item.Tags.Contains("phantom", StringComparer.OrdinalIgnoreCase))
        return   // unmaterialised; user was watching the splash

    if (item.SourceType != SourceType.Channel) return
    if (!ChannelIds.IsPhantom(item.ChannelId)) return

    var parsed = ChannelItemId.Parse(item.ExternalId)
    if (parsed.Kind != "phantom_episode") return

    // Prefetch the next N episodes
    var prefetch = _config.AutopilotPrefetch
    var nextEpisodes = await ComputeNextUpAsync(parsed.TmdbId, parsed.Season.Value, parsed.Episode.Value, prefetch, ct)
    foreach (var (nextSeason, nextEpisode) in nextEpisodes)
    {
        // Skip already-materialised / in-flight
        var (s, e) = ChannelItemId.ToSentinels(nextSeason, nextEpisode)
        if (await _db.GetMaterialisedStateAsync(parsed.TmdbId, "episode", s, e, ct) is not null) continue
        if (await _db.IsMaterialiseInFlightAsync(parsed.TmdbId, "episode", s, e, ct)) continue

        // Fire-and-forget materialise
        _ = _materialiser.MaterialiseAsync(parsed.TmdbId, "episode", nextSeason, nextEpisode, MaterialiseTrigger.Autopilot, CancellationToken.None)
    }
```

`ComputeNextUpAsync(seriesTmdb, currentSeason, currentEpisode, prefetch, ct)`:
queries TMDB for the series' season+episode list; walks forward
from `(currentSeason, currentEpisode + 1)` for up to `prefetch`
slots, crossing season boundaries naturally.

### Stage 5.3 — Validation

Rig scenario `scenarios/32-channel-shows.sh`:
1. Wipe + start Jellyfin + DiscoveryRefresh.
2. Browse "Phantom Shows" tile; assert list of series.
3. Click a series; assert list of seasons.
4. Click a season; assert list of episodes (all of them, per TMDB).
5. Click an episode; assert splash plays.
6. Materialise via kebab; wait; click play; assert real file plays.
7. Autopilot test: simulate `UserDataSaved` event with
   `PlayedPercentage = 85` on a Series → Episode whose
   RunTimeTicks is > 2 minutes. Assert MaterialiseAsync fires for
   next episode.
8. Splash-guard test: simulate same event with `RunTimeTicks =
   10 seconds`. Assert MaterialiseAsync does NOT fire (this is the
   v2 critic's storm scenario).

Commit Phase 5 to main.

---

## Phase 6: Eviction + lifecycle hygiene

### Stage 6.1 — `EvictionSweeper` rewrite

Replace `Materialisation/EvictionSweeper.cs`:

```pseudocode
async RunOnceAsync(ct):
    var rows = await _db.ListMaterialisedStateAsync("movie", ct).Concat(await _db.ListMaterialisedStateAsync("episode", ct))
    var users = _userManager.GetUsers()
    var idleCutoff = TimeSpan.FromDays(_config.EvictionIdleDays)

    foreach (var row in rows)
    {
        // Compute channel item id + look up BaseItem to get last-played-date
        var (kind, externalId) = (row.Type == "movie" ? "movies" : "shows", ChannelItemId.Encode(row))
        var baseItem = ResolveChannelBaseItem(externalId, kind)
        if (baseItem is null) { /* orphan; skip + log */ continue }

        var lastPlayed = users.Select(u => _userDataManager.GetUserData(u, baseItem)?.LastPlayedDate).Where(d => d.HasValue).Max()
        var protectedByFav = _config.ProtectFavourites && users.Any(u => _userDataManager.GetUserData(u, baseItem)?.IsFavorite ?? false)

        if (protectedByFav) continue
        if (lastPlayed.HasValue && (DateTimeOffset.UtcNow - lastPlayed.Value) < idleCutoff) continue
        if (!lastPlayed.HasValue && (DateTimeOffset.UtcNow - row.MaterialisedAt) < idleCutoff) continue

        // Evict: call gostream Remove with our stored stub_path (critic v2 IMPORTANT 8 fix)
        try
        {
            await _gostream.RemoveAsync(row.StubPath, ct)
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "gostream RemoveAsync failed for stub_path={Path}", row.StubPath)
            continue   // try again next tick
        }

        await _db.DeleteMaterialisedStateAsync(row.TmdbId, row.Type, row.Season, row.Episode, ct)
        await _refreshManager.RefreshChannelItemAsync(channelId, externalId,
            new ChannelItemRefreshOptions { ForceUpdate = true, ForceProbe = false, InvalidateMediaInfoCache = true }, ct)
        _state.BumpDataVersion(row.Type == "movie" ? "movies" : "shows")
    }
```

Run on cron (existing `EvictionScheduleCron` config; default daily).

### Stage 6.2 — Validation

Rig scenario `scenarios/33-channel-eviction.sh`:
1. Setup with one Materialised movie + simulated old-LastPlayedDate.
2. Trigger EvictionSweeper via Dashboard or test endpoint.
3. Assert `gostream.RemoveAsync(stub_path)` was called.
4. Assert `materialised_state` row deleted.
5. Re-browse channel; assert item now shows phantom MediaSource (splash).
6. Favourited-item test: setup with favourited materialised item;
   trigger sweep; assert NOT evicted.

Commit Phase 6 to main.

---

## Phase 7: Wipe + install + operator deployment

### Stage 7.1 — `scripts/phantom-wipe.sh`

Already in repo at `scripts/phantom-wipe.sh` (operator copied from
`/tmp/` during prior recovery work; previously sandbox-validated
against the operator's actual phantom.db + jellyfin.db data shapes).

Review the script's current behaviour against the new architecture's
wipe targets and EDIT only as needed:

- Verify it drops the operator's `gostream-movies` and `gostream-shows`
  CollectionFolders from jellyfin.db (channels take over; the
  CollectionFolders should not exist post-wipe).
- Cascade-cleans FK child tables (UserDatas, BaseItemProviders,
  MediaStreams, MediaAttachments, etc.) for the dropped BaseItems.
- Deletes/renames `phantom.db` (plugin recreates at schema v7 on
  next start).
- Removes phantom-library stub directory contents (the file-on-disk
  tree is no longer used by the channel architecture).

Existing safety properties to preserve (don't regress):
- Refuses to run while `jellyfin.service` is active.
- Backs up both DBs before any write (`<dbdir>/<dbname>.bak.wipe.<UTC-ts>`).
- Sanity cap: refuses to delete > 50% of `BaseItems` in one run.
- `--dry-run` default; `--commit` requires `WIPE` typed verbatim.

This stage's deliverable is a DIFF to the existing script (if needed),
not a fresh write. Test the modified script against a clone of the
operator's current DBs in `/tmp/operator-snapshot/` per the AGENTS.md
"Production database safety" rule.

### Stage 7.2 — `install.sh` (dev-machine convenience; no packaging scope)

Per operator: install.sh is a dev-machine convenience script, NOT
intended as a production deployment tool. Packaging (binary
distribution, package-manager integration, automatic Jellyfin-binary
replacement) is **out of scope** for this PR.

Minimal install.sh changes for this PR:

1. Apply Jellyfin patches from `scripts/jellyfin-patches/` against
   the `jellyfin/` source clone (Phase 1.4 logic, already specified).
2. Build patched Jellyfin from source (existing or new `dotnet build`
   step). Output: a built Jellyfin server tree under a known path
   (e.g. `./build/jellyfin/`).
3. Build plugin DLL from `src/` (existing step).
4. Copy plugin DLL into the operator's existing Jellyfin install's
   plugin dir (existing step; operator's Jellyfin location is whatever
   was true before).
5. Inject `phantomKebab.js` + `phantomBadges.js` shims into
   `jellyfin-web/index.html` (existing step).
6. Print clear operator instructions for how to actually USE the
   patched Jellyfin build (e.g. "to run with the patched server,
   `./build/jellyfin/jellyfin.Server`; production-style packaging
   not handled by this script").
7. Optional restart prompt.

**Explicit non-goals (deferred):**
- Detecting the operator's Jellyfin binary location automatically.
- Replacing operator's installed Jellyfin binaries.
- Detecting + warning on package-manager upgrades reverting the patch.
- Rollback procedure for binary replacement.

These all become real concerns when this work moves toward an actual
release; in the operator's current dev-machine workflow, they're
out of scope.
### Stage 7.3 — Operator-facing docs

EDIT `CHANGELOG.md` — Unreleased entry:

```
## [Unreleased]

### BREAKING — requires wipe + patched Jellyfin

Phantom Library v0.3.0 replaces the file-on-disk phantom architecture
with a Jellyfin `IChannel`-based design backed by a small additive
patch to Jellyfin's ChannelManager.

Operator steps:

1. sudo systemctl stop jellyfin
2. sudo bash scripts/phantom-wipe.sh                 # dry-run; inspect
3. sudo bash scripts/phantom-wipe.sh --commit        # type WIPE
4. ./install.sh --build
   # applies Jellyfin patches; builds patched Jellyfin; installs plugin
5. sudo systemctl start jellyfin
6. Dashboard → Plugins → Phantom Library → Settings; confirm gostream
   paths; click Save
7. Dashboard → Scheduled Tasks → "Phantom Library: Discovery Refresh"
   → Run Now
8. Refresh browser; "Phantom Movies" and "Phantom Shows" tiles
   appear in your library nav
9. Click a phantom; play (splash); kebab → Materialise; wait for
   toast; play again — real file plays

Manual fallback (if scripts/phantom-wipe.sh unavailable):
[inline SQL + shell commands]

Known regressions:
- Loss of `CollectionType.movies` Home rows ("Latest Movies",
  "Continue Watching Movies") for gostream content. Replaced by
  channel-specific "Latest in Phantom Movies" rows. Accepted.
- UserData (favourites, watched, playback position) on existing
  gostream-bound BaseItems is lost in the wipe. Accepted.
- Pre-existing gostream files that the plugin doesn't know about
  appear with raw filename Names until materialised through the
  plugin. Operator can opt into TMDB title-search fallback via
  `EnrichOrphanGostreamItemsViaTmdbSearch = true` in plugin config.
```

EDIT `README.md` — add a "Requires patched Jellyfin" callout near
the top:

```
> ⚠ This plugin requires a patched build of Jellyfin. The patches
> live at `scripts/jellyfin-patches/` and are applied automatically
> by `./install.sh --build`. The patches are additive (no API
> mutation) and add a per-item channel refresh primitive that the
> plugin uses for materialise-on-demand state updates. See
> `docs/plans/channel-handoff.md` for the architectural rationale
> and `scripts/jellyfin-patches/REBASE.md` for maintenance
> instructions.
```

EDIT `AGENTS.md` — add a "Jellyfin patch dependency" section:

```
## Jellyfin patch dependency

This plugin depends on patches applied to Jellyfin core, stored at
`scripts/jellyfin-patches/`. install.sh applies them at build time.
The patches add `IChannelItemRefresh` (opt-in channel-side interface)
and `IChannelItemRefreshManager` (new service sibling to
`IChannelManager`) — both purely additive. No existing API is
modified.

On Jellyfin upstream updates, the patches may need rebasing. The
install.sh script aborts with an actionable error if a patch fails
to apply. Rebase by applying via `git am`, resolving conflicts, and
re-exporting via `git format-patch`.

Phase 8 (deferred): upstream PR. Per Jellyfin's LLM/AI contribution
policy, this PR must be operator-authored with the operator
understanding and able to defend every line. See
docs/plans/channel-handoff.md § Phase 8 for the upstream procedure.
```

### Stage 7.4 — Sandbox test against operator's data shape

Per AGENTS.md "Production database safety":

```bash
mkdir -p /tmp/operator-sandbox
cp /tmp/operator-snapshot/* /tmp/operator-sandbox/
# Run the wipe script against the sandbox DBs
PHANTOM_DB=/tmp/operator-sandbox/phantom.db \
JELLYFIN_DB=/tmp/operator-sandbox/jellyfin.db \
STUB_ROOT=/tmp/operator-sandbox/phantom-library \
bash scripts/phantom-wipe.sh --commit
```

Verify counts match expectations. Verify no over-deletion. Verify
DBs are valid post-wipe.

### Stage 7.5 — Operator deployment

Follow the exact steps from the CHANGELOG entry. Verify each step.
Document any surprises in a post-deployment note appended to this
plan.

Commit Phase 7 to main.

---

## Phase 8 (DEFERRED — operator-driven): upstream PR

This phase happens only if and when the operator chooses to upstream
the Jellyfin patches. **Per Jellyfin's LLM/AI policy, this phase is
operator-driven, not agent-driven.** The agent's role is bounded
to:

- Providing the technical patch (already in `scripts/jellyfin-patches/`)
- Providing reference test code (already in `jellyfin/tests/`)
- Answering operator questions about the design

The agent does NOT:
- Author the Meta discussion thread (operator writes in their own words)
- Author the PR body (operator writes in their own words)
- Respond to review comments (operator engages personally)
- Decide design tradeoffs in review (operator owns)

### Stage 8.1 — Meta discussion

Operator opens a discussion at
https://github.com/jellyfin/jellyfin-meta/discussions describing:
- The use case: plugin-driven channel item refresh when underlying
  media changes outside the normal scan cycle.
- The proposed shape: additive sibling interface, no ABI break.
- The patches: link to commits / branch.
- Request: feedback on the design before opening a PR.

The agent provides a technical-summary draft for the operator to
read, understand, paraphrase, and post — but does not author the
final post.

### Stage 8.2 — Fork + branch

Operator forks `jellyfin/jellyfin` on GitHub. Clones the fork.
Creates a feature branch off `master`. Adds themselves to
`CONTRIBUTORS.md` per the dev guide.

### Stage 8.3 — Apply commits to upstream-track branch

Operator cherry-picks (or re-applies) the three commits from
`scripts/jellyfin-patches/` onto the fork's feature branch.

### Stage 8.4 — PR

Operator opens PR against `jellyfin/jellyfin:master`. PR body:
- Why the change is being made (operator's words, reference any
  related issues)
- What the change does (technical summary in operator's words)
- How it was tested (operator's experience, not the agent's)
- Note that it's three discrete commits per the LLM policy

Operator engages directly with reviewer feedback. The agent may
help interpret feedback in private but does not author replies.

### Stage 8.5 — Post-merge cleanup

If/when the PR merges:
- Delete `scripts/jellyfin-patches/`.
- Update `install.sh` to remove the patch-application step.
- Update `README.md` and `AGENTS.md` to remove the patched-Jellyfin
  warnings.
- Bump minimum supported Jellyfin version in `manifest.json` and
  `csproj`.
- Add a CHANGELOG entry: "Jellyfin patches accepted upstream as of
  vX.Y.Z; patches removed from repo."

---

## Acceptance gates per phase

Each phase has an acceptance criterion that MUST be met before
proceeding to the next:

| Phase | Acceptance gate |
|---|---|
| 0 | All pre-flight checks pass; baseline `dotnet build` + `dotnet test` green; rig functional; operator snapshot saved |
| 1 | Patched Jellyfin builds clean; Jellyfin's own tests pass; rig confirms `RefreshChannelItemAsync` works via behaviour test |
| 2 | Plugin builds clean post-deletion + new schema; new helper tests pass; channel tiles appear (empty) in rig nav |
| 3 | Movies channel renders mixed real + phantom items in rig; DiscoveryRefreshTask populates cache correctly |
| 4 | Rig materialise scenario passes including probe-poisoning regression + cache-invalidation regression + crash-sweep test |
| 5 | Shows channel browse Series → Season → Episode works in rig; episode materialise works; autopilot splash-guard verified |
| 6 | Eviction sweeper test passes; favourite protection works |
| 7 | Operator sandbox-tested wipe; operator deployment succeeds on prod box; UI verified per CHANGELOG check |
| 8 | Operator-driven; no agent acceptance criterion |

If any gate fails, stop and re-evaluate. Do not proceed to the next
phase. Critic review may be requested at any gate.

---

## Risks

R1. **Patch rebase burden on Jellyfin upgrades.** Patches target a
stable file (`ChannelManager.cs`); surrounding context around
`GetChannelItemEntityAsync` has been stable across 10.8-10.11
inspection. Mitigation: install.sh aborts with actionable error if
patches don't apply; rebase manually + re-export.

R2. **Mobile/TV client rendering verified server-side but not
client-side.** Server emits correct BaseItemDto.Type per source
investigation. Phase 5.3 rig scenario asserts via REST API
inspection. If actual mobile/TV apps render differently from REST
output, that's a Jellyfin client bug, not a plugin bug. Document
any divergence.

R3. **Pre-existing orphan gostream files (operator's ~131 files).**
On day 1 these surface with raw filenames. Operator-acceptable per
prior discussion. Mitigation knob:
`EnrichOrphanGostreamItemsViaTmdbSearch` setting (off by default).

R3b. **UserData loss on existing CollectionFolder-bound items at wipe.**
The wipe drops the operator's `gostream-movies` / `gostream-shows`
CollectionFolders; UserData rows for the old BaseItem Ids are
FK-cascaded away. Operator accepted this as one-time wipe cost.
**Critical: this is a ONE-TIME loss at wipe, not a per-materialise
loss.** Per critic round 3 BLOCKER 1 fix, the channel uses a single
external id per logical item (movie_<tmdb>, episode_<tmdb>_sNNeNN,
etc.) regardless of materialise state. The BaseItem.Id derived from
that external id is stable across the phantom → materialised
transition; ChannelManager updates Path + MediaSources against the
same row instead of deleting + recreating. UserData on the channel-
bound BaseItem persists across materialise.

R4. **`IndexNumber`/`ParentIndexNumber` not refreshed for
existing episode items (`ChannelManager.cs:1019-1020` only sets
these on `isNew`).** For phantom episodes this is fine; the values
don't change. If a TMDB metadata correction renumbers a season, the
plugin would need to delete + recreate the channel item. Out of
scope.

R5. **Channel display name change = data loss.** Plugin hardcodes
names (`"Phantom Movies"`, `"Phantom Shows"`). If a future PR
exposes them as settings, document the data-loss consequence
prominently.

R6. **`forceUpdate=true` triggers metadata refresh provider chain
on every materialise.** With `MetadataRefreshMode.FullRefresh +
EnableRemoteContentProbe = true`, all metadata providers run
including remote ones. For materialise of an autopilot prefetch
during a live user session, this may compete for TMDB rate limit
budget. Acceptable; monitor in production.

R7. **MaterialiseInFlightSweeper threshold too short / too long.**
Default 10 minutes. If a materialise legitimately takes longer than
10 min (large file, slow gostream), the in-flight row gets nuked
mid-flight; the materialise completes but the channel never gets
notified to refresh. Mitigation: monitor the sweep's purged-row
count; tune up if non-zero in steady state.

R8. **Dual MediaSources** — phantom channels don't declare
`ISupportsMediaProbe` (per critic v2 BLOCKER 6 mitigation). Verify
in rig that the static + dynamic MediaSource concat produces ONE
visible entry to the user, not two. If two are visible, declare
`ISupportsMediaProbe` and revisit (different breakage surfaces).

R9. **Search behaviour.** Per explore agent: search includes
channel Movies/Episodes (no `SourceType==Channel` filter in
`SearchEngine.cs`). But folder items
(ChannelFolderItem) are excluded by `excludeItemTypes`. Phantom
Series (which are Folder-shaped) may or may not appear in search.
Verify in rig; document.

---

## Rollback procedure

If Phase 7 deployment fails on the operator's box:

1. `sudo systemctl stop jellyfin`
2. Restore Jellyfin binary from package manager / previous backup.
3. Restore `phantom.db` from the `.bak.wipe.<ts>` file.
4. Restore `jellyfin.db` from its `.bak.wipe.<ts>` file.
5. `sudo systemctl start jellyfin`
6. Confirm UI returns to pre-Phase-7 state.

The patched Jellyfin and the new plugin DLL are isolated to the
operator's box; restoring the package manager's Jellyfin reverts
the patch entirely. Plugin restoration is by reverting to the
prior plugin DLL via the manifest.

---

## What this plan does NOT cover

- Migration of existing UserData (favourites, watched) from old
  CollectionFolder-bound BaseItems to new channel-bound BaseItems.
  Operator accepted the loss.
- New gostream protocol or API changes. The plan relies only on
  existing `IGostreamClient` surface.
- Custom client UI work beyond `phantomBadges.js` + `phantomKebab.js`
  (which already exist).
- Search-result customisation for channel items (Risk R9 may
  surface a need; out of scope for v0.3.0).
- Per-user discovery (current plan: global discovery).
- Eager pre-resolution (`EagerResolver` is deleted; no replacement).

These are all explicit deferrals from prior discussions, captured
here for completeness.

---

## Single source of truth

This document is the execution plan. Phase progress, surprises,
deviations from the plan, and post-mortem notes all append here
(under a new "Execution log" section that gets added as Phase 0
starts). The previous architecture plans
(`channel-architecture.md`, `scanner-race-reactor.md`) are kept for
historical context but are NOT updated.
