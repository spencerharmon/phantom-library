# Eliminating the scanner-race GUID divergence (plan v2)

Date: 2026-06-09 (v2 after critic review)
Status: **DRAFT v2 — addresses critic findings; awaiting second-pass review**
Author: agent

## Problem

`SuggestionsContributor.MaterialiseHitsAsync` (and
`SeriesIngestor.EnsureSeriesAsync`) currently competes with
Jellyfin's library scanner for ownership of phantom-tree
`BaseItems`. The plugin computes a deterministic
`newItem.Id = GetNewItemId("phantom_<kind>_<tmdbId>", type)`,
calls `CreateItem(newItem, parent)`, then registers a
`phantom_items` row keyed on `newItem.Id`. Concurrently the
Jellyfin scanner walks the phantom-library directory tree,
runs `MovieResolver` / `SeriesResolver` against the stub
file, and creates its own `BaseItem` with a scanner-derived
GUID (computed internally from `(libraryId, path, type, ...)`).

The plugin and scanner do not agree on the GUID. Whichever
writer wins persists the surviving `BaseItem`; the loser's
GUID is orphaned. Today, on the operator's production box
after a clean wipe-and-reseed cycle:

| Metric | Movies | Series |
|---|---|---|
| `phantom_items` rows | 17062 | 16898 |
| Distinct `tmdb_id` values in `phantom_items` | 9520 | 9397 |
| TMDB ids with 2 pi rows (race + dedupe-rescue artefact) | 7542 | ≈7500 |
| Phantom-tree `BaseItems` on disk | 9372 | 9304 |
| BaseItems with matching pi row (badges work) | 7542 | 7594 |
| BaseItems with **no** matching pi row (badges broken) | 1830 | 1710 |
| Orphan pi rows (item_guid → no BaseItem at all) | 9375 | 9304 |
| pi rows : tmdb id ratio | 1.79 | 1.80 |

Mechanism, worked example (`tmdb_id=100`):

1. First Suggestions tick. Plugin computes
   `GetNewItemId("phantom_movie_100")` → `82cf9e4b…`.
   Creates stub file. Calls `CreateItem(newItem)`. Calls
   `UpsertPhantomRowAsync(82cf9e4b…)`. Scanner concurrently
   wins; live BaseItem ends up at `29381aef…`. pi row
   `82cf9e4b…` is immediately orphaned.
2. Second tick. Dedupe branch finds live BaseItem at
   `29381aef…`. Creates a second pi row. Badge now resolves.
3. Original `82cf9e4b…` pi row stays orphan forever.

7542 movie TMDB ids show this pattern. 1830 haven't had a
second tick yet, so badges are broken for them. Remaining
148 are edge cases.

## Failed alternatives (history)

The `IsLocked = true` + re-stamp + `HealBrokenPhantomAsync`
dance in `SuggestionsContributor` was an attempt to win the
race by patching it. The `[tmdbid-N]` naming spike retired
the *Name* fight; the *GUID* fight survives because Name and
Id derive independently inside Jellyfin's scanner.

The v0.2.0.0 in-plugin `StubLayoutMigration` IHostedService
was a different race (file moves vs scanner watcher) with
the same root cause: two writers contending. AGENTS.md
§ "Single-operator deployment" forbids reintroducing it.

## Diagnosis

The architecture has **two writers** of phantom-tree
BaseItems. Jellyfin's library model assumes exactly one
writer per (library, path) — the scanner. Every workaround
we've tried (locking, re-stamping, healing, deterministic
GUIDs) is downstream of the wrong choice to have the plugin
also write BaseItems.

Three architectural directions:

A. **Plugin-as-reactor.** Plugin stops calling `CreateItem`.
   Scanner is sole writer. Plugin observes via
   `ILibraryManager.ItemAdded` and writes `phantom_items`
   row reactively, keyed on scanner-assigned GUID.

B. **Separate unscanned directory.** Stubs outside any
   library; plugin owns BaseItem creation and injects items
   into library query results at runtime. Big refactor;
   bypasses M10 cull behaviour at substantial cost.

C. **Match the scanner's GUID derivation.** Plugin
   precomputes the same GUID Jellyfin's scanner would.
   Brittle: depends on undocumented internal Jellyfin
   behaviour that may change per minor version.

**Recommended: A.** Smallest delta from current code, aligns
with Jellyfin's intended scanner contract, retires multiple
workaround layers.

## Proposed solution: ItemAdded-reactor (A)

This plan ships as **one combined PR** (not the v1
PR1/PR2 split), because:

- The cleanup work (removing `IsLocked` defaults,
  `HealBrokenPhantomAsync`, etc.) is not actually orthogonal
  to the reactor refactor — `HealBrokenPhantomAsync` is
  called from the dedupe branch the reactor keeps. Removing
  one without the other leaves an inconsistent code path.
- The schema column drop (`phantom_items.original_overview`)
  is itself a BREAKING-wipe change per AGENTS.md
  § "No database migrations until v1.0". Two back-to-back
  wipes would double the operator's repopulation cost for
  no benefit.

Single PR, one wipe cycle, one repopulation.

### Behaviour

1. **Stub creation on disk** — unchanged.
   `PhantomStubManager` continues to drop symlinks /
   directories under `<root>/{movies,shows}/`.

2. **`SuggestionsContributor.MaterialiseHitsAsync` and
   `SeriesIngestor.EnsureSeriesAsync`** drop their
   `CreateItem` + `UpdateItemAsync` re-stamp +
   `UpsertPhantomRowAsync` blocks. They write the stub
   to disk and return. The dedupe-hit branch
   (`FindExistingByTmdbId`) is also dropped because the
   reactor produces no orphan pi rows that would need
   subsequent recovery, and the listener (below) handles
   any rediscovery on its own.

3. **`PhantomItemAddedListener : IHostedService`** subscribes
   to `ILibraryManager.ItemAdded`. On each event:

   - Filter: `evt.Item.Path` matches
     `PhantomPathUtilities.IsPhantomStubPath(evt.Item.Path)`.
   - Filter: `evt.Item.Type` is `Movie` or `Series`
     (skip `Season`, `Episode`, `Folder` — those are
     scanner-managed children with no pi row).
   - Parse the TMDB id via
     `PhantomPathUtilities.TryParseTmdbId(evt.Item.Path)`.
     Skip if absent.
   - Determine `type` (`"movie"` / `"series"`) from
     `evt.Item.Type`.
   - **Defensive uniqueness check (preserves 1:1
     invariant):** query phantom_items by
     `(tmdb_id, type)`. If a row already exists with a
     *different* `item_guid`:
     - Resolve the old `item_guid` via
       `_libraryManager.GetItemById`. If the old BaseItem
       no longer exists (scanner culled it during a
       path-collision pass — see Demote race section
       below), `DeletePhantomItemAsync(old_guid)` then
       proceed with the insert.
     - If the old BaseItem still exists, log a `WARNING`
       with both Ids + tmdb_id and **skip** the insert.
       This is a real bug (genuine duplicate scanner-
       created BaseItems for the same TMDB id) and the
       1:1 invariant takes precedence over auto-write.
     - If the old `item_guid` equals the new `item_guid`,
       this is an idempotent re-fire of the same event —
       upsert is a no-op.
   - `UpsertPhantomItemAsync(evt.Item.Id, …, state=Virtual,
     first_seen=now, last_touched=now, …)`.
   - Transfer any pending hint from `_hintSink`
     (see EagerHintSink change below).

4. **`PhantomItemBackfillSweeper : IHostedService`** runs
   ~30s after plugin startup. **Sources the truth from
   disk, not from the BaseItems table** (critic IMPORTANT 5
   fix):

   - Enumerate every stub file/dir under stub-root via
     `Directory.EnumerateFiles` / `EnumerateDirectories`.
     This is the authoritative source of "what should exist."
   - For each stub: derive `(tmdb_id, type)` from the
     `[tmdbid-N]` token in the path.
     - If `phantom_items` has a row for this
       `(tmdb_id, type)`: verify the row's `item_guid`
       still resolves to a live BaseItem. If yes, OK. If
       no, delete the stale pi row.
     - If no pi row exists: search for a BaseItem under
       stub-root with matching TMDB id via
       `ILibraryManager.GetItemList(InternalItemsQuery {
       HasAnyProviderId = {"Tmdb": tmdbIdStr}, AncestorIds
       = phantomFolderIds })`. If found, write pi row
       with `item_guid = baseItem.Id`. If not found, the
       scanner has not yet ingested this stub — invoke
       `_libraryManager.QueueLibraryScan` (or the
       `ValidateMediaLibrary` equivalent) **scoped to
       the stub-root physical folder** and log; the
       eventual `ItemAdded` will reach the listener.
   - Idempotent; logs (stubs_on_disk, pi_rows,
     scan_requeued) counts.

5. **`EagerResolver.OnItemAdded`** (critic BLOCKER 1) —
   today the resolver short-circuits on any non-empty
   `Path` (assumes path-less Virtual items are the
   eager target). Replace that filter with
   `PhantomPathUtilities.IsPhantomStubPath(item.Path)`.
   Post-refactor, every phantom carries a stub Path; the
   old filter would zero out all eager resolution. New
   filter correctly fires for phantom-stub-pathed items.
   `EagerHintSink` rekeys from `Guid` to `(int tmdbId,
   string type)` (Open Question 1 resolution: option (a),
   keyed by TMDB id, resolved to BaseItem.Id in the
   listener at consumption).

6. **Schema** — add a UNIQUE index on `(tmdb_id, type)`
   in `phantom_items`. SQLite syntax:
   ```sql
   CREATE UNIQUE INDEX IF NOT EXISTS
     idx_phantom_items_tmdb_type
     ON phantom_items(tmdb_id, type)
     WHERE tmdb_id IS NOT NULL;
   ```
   Enforces the 1:1 invariant at the DB layer. Combined
   with the listener's defensive uniqueness check (step
   3), duplicate inserts become impossible. (Per the
   operator: keep this invariant; don't soften.)

7. **Drop `phantom_items.original_overview` column** as part
   of the cleanup. Was used by the now-deleted
   `PhantomStatusDecorator` Overview-prefix round-trip
   (also deleted).

### What this retires (in the same PR)

- `IsLocked = true` defaults on Virtual items.
- `UpdateItemAsync` re-stamp after `CreateItem`.
- `HealBrokenPhantomAsync` + dedupe-hit `nameIsStem` check.
- `PhantomImageProvider` (bundled TMDB image provider
  handles `ProviderIds[Tmdb]` from bracketed path tokens
  natively).
- `PhantomStatusDecorator` Overview-prefix mutation +
  `phantom_items.original_overview` column.
- `SuggestionsContributor.FindExistingByTmdbId`'s legacy
  `__phantom_tmdb` name-fallback branch (provider-id
  lookup is the only path post-wipe).

### EvictionSweeper demote race (critic IMPORTANT 4)

Demote repoints `BaseItem.Path` from the gostream FUSE path
to a fresh phantom stub path via `UpdateItemAsync`. Today's
behaviour depends on whether the demote-generated stub path
equals the original Virtual-state stub path:

- **Equal path** (the common case post-spike — stub paths
  are deterministic from `(title, year, tmdbId, kind)` and
  the materialiser preserves the original stub on disk):
  no path-collision; same `BaseItem.Id` survives; no
  scanner re-derivation; no race.
- **Different path** (rare: title/year enrichment between
  Virtual and Materialised states; manual operator
  intervention): scanner sees the new stub, computes a new
  Id via path-derived hashing, detects a path-collision
  with the existing BaseItem at the new Path; per
  `Folder.ValidateChildrenInternal2`, the old BaseItem is
  deleted and a new one created with the new Id. This
  destroys user data on the old BaseItem regardless of
  this plan, and is a pre-existing bug we are not fixing
  here.

The listener's defensive uniqueness check (step 3 above)
handles both cases: in the equal-path case, the existing
pi row's `item_guid` matches the post-demote BaseItem.Id
(no-op upsert). In the different-path case, the old
BaseItem is gone (scanner deleted), so the listener
deletes the stale pi row and writes a fresh one keyed on
the new BaseItem.Id.

**Net for the 1:1 invariant:** preserved by construction
regardless of demote path. No orphan pi rows after demote.

**Net for user data:** the different-path demote case can
still lose UserData rows (pre-existing). Out of scope for
this plan; tracked separately.

### Binder window race (critic IMPORTANT 5)

`PhantomCollectionFolderBinder.BindOneAsync` can take up
to ~60 s during cold-start (30 attempts × 1 s × 2
libraries). If `SuggestionsRefreshTask` fires within that
window, stubs land in an unbound directory; the scanner
never validates the folder; no `ItemAdded` fires; the
backfill sweeper's BaseItem query returns empty.

Mitigations in this PR:

a. **Backfill sweeper sources truth from disk** (step 4
   above), not from BaseItems. Discovers the orphaned
   stubs the scanner missed.
b. **Sweeper invokes scoped scan** for orphaned stubs:
   calls `_libraryManager.QueueLibraryScan` against the
   stub-root physical folder so the scanner picks them
   up; the listener fires when it does.
c. **`SuggestionsRefreshTask` checks `_binder.IsBound`
   before running.** New `IsBound` flag exposed by
   `PhantomCollectionFolderBinder`; defaults to `false`
   until both libraries have been successfully bound
   at least once. If `false`, the scheduled task logs a
   warning and skips this tick (next tick at the normal
   6h interval, or operator can re-trigger manually
   once binding completes).
d. **Sweeper also runs after binder reports `IsBound`
   changes from false to true**, not just at the 30s
   startup tick. Catches the cold-start case where
   binding takes longer than the sweeper delay.

### Diagnostic regression mitigation (critic IMPORTANT 7)

The reactor pattern's tradeoff is that
`phantom_items` no longer records "intent" — only
"items the scanner successfully ingested." For
diagnostics, the recovery is:

a. **Per-tick log line in `SuggestionsRefreshTask`**:
   `[Suggestions] tick complete: stubs_created=N
   stubs_existing=M tmdb_skipped=K` — records the
   intent count before the scanner runs.
b. **Sweeper logs `stubs_on_disk_without_pi_row=N`** in
   its per-run summary. If non-zero, the operator
   knows the scanner hasn't caught up; can wait or
   trigger a scan manually.
c. **New diagnostic API endpoint**: `GET
   /Plugins/PhantomLibrary/Diagnostics/StubAudit`
   returns `{stubs_on_disk, baseitems_under_stub_root,
   pi_rows, orphan_pi, orphan_stubs}`. One HTTP call
   surfaces drift; useful for operator and for
   automated monitoring.

### Wipe required (BREAKING)

The schema gains a UNIQUE index that the current
duplicated pi rows would violate. Plus `original_overview`
column is dropped. Plus all current pi rows are bloated
(1.79 ratio). Wipe is mandatory.

`scripts/phantom-wipe.sh` (NEW, committed to repo per
critic BLOCKER 3): inlines the wipe SQL + filesystem
commands, repo-tracked, idempotent. Per AGENTS.md
§ "No database migrations until v1.0", this is a wipe
*procedure*, not a migration — it's the documented
upgrade path. The script:

- Refuses to run if `jellyfin.service` is active.
- Backs up `phantom.db` and `jellyfin.db` to
  `<dir>/<name>.bak.wipe.<UTC-ts>` before any write.
- Schema-probes both DBs (uses Jellyfin 10.11
  `lower(replace(BaseItems.Id, '-', ''))` join form).
- `DELETE FROM BaseItems WHERE Path LIKE
  '/var/lib/jellyfin/phantom-library/%'` plus cascade
  cleanup of FK child tables (UserDatas,
  BaseItemProviders, MediaStreams, etc.) — table list
  is discovered from `PRAGMA foreign_key_list` rather
  than hardcoded.
- Sanity cap: refuses to delete more than 50% of
  BaseItems in one run.
- Renames `phantom.db` to `phantom.db.wiped.<ts>`
  (plugin recreates on next start via
  `PhantomDb.EnsureSchemaAsync` at the new schema
  version).
- Removes everything under stub-root EXCEPT
  `.phantom-library-keep` sentinels and `.splash.*`
  assets.
- Default mode is dry-run; `--commit` actually wipes;
  prompts for `WIPE` typed verbatim.
- Operator handoff steps inlined in
  `CHANGELOG.md` Unreleased BREAKING entry and the
  PR description.

The wipe script is **also documented inline in the
CHANGELOG** so operators can reproduce the procedure
manually from a fresh shell without depending on the
script (defence in depth per critic BLOCKER 3).

### Code touch points

| File | Change |
|---|---|
| `Library/SuggestionsContributor.cs` | gut `MaterialiseHitsAsync`; drop `CreateItem`, `UpdateItemAsync` re-stamp, `UpsertPhantomRowAsync`, `FindExistingByTmdbId`, `HealBrokenPhantomAsync`. Add `IsBound` check before tick. |
| `Library/SeriesIngestor.cs` | same simplification for `EnsureSeriesAsync`. |
| `Library/PhantomItemAddedListener.cs` (NEW) | hosted service per §3. |
| `Library/PhantomItemBackfillSweeper.cs` (NEW) | hosted service per §4. |
| `Library/PhantomCollectionFolderBinder.cs` | expose `IsBound` flag + binding-complete event. |
| `Materialisation/EagerResolver.cs` | replace path-empty filter with `PhantomPathUtilities.IsPhantomStubPath` (critic BLOCKER 1). |
| `Materialisation/EagerHintSink.cs` | rekey from `Guid` to `(int tmdbId, string type)`. |
| `State/PhantomDb.cs` | schema v6: add UNIQUE index on `(tmdb_id, type)`; drop `original_overview` column. Add `DeletePhantomItemAsync(Guid)` helper + `GetPhantomItemByTmdbAsync(int tmdb, string type)` helper. |
| `Playback/PhantomStatusDecorator.cs` | DELETE. |
| `Providers/PhantomImageProvider.cs` | DELETE. |
| `Api/PhantomLibraryDiagnosticsController.cs` (NEW) | StubAudit endpoint per §Diagnostic regression mitigation. |
| `PluginServiceRegistrator.cs` | register listener + sweeper; deregister status decorator + image provider. |
| `scripts/phantom-wipe.sh` (NEW) | committed wipe script per §Wipe required. |

### Tests

**Unit (`tests/Jellyfin.Plugin.PhantomLibrary.Tests/`):**

`PhantomItemAddedListenerTests`:
- ItemAdded for phantom-stub-pathed Movie/Series with parseable `[tmdbid-N]` → pi row inserted.
- Non-phantom Path → no insert.
- Season/Episode under series stub → no insert (filter test).
- Same event twice → second is no-op (idempotency).
- **1:1 invariant tests (operator-requested):**
  - Two ItemAdded events with different `item_guid` but same `(tmdb_id, type)`, old BaseItem culled → old pi row deleted, new pi row written, total pi row count for that tmdb stays at 1.
  - Two events as above but old BaseItem still alive → second event skipped with WARNING log, total pi rows stays at 1, old pi row's `item_guid` is preserved.
  - UNIQUE index rejection at the DB layer: forcibly attempt `UpsertPhantomItemAsync` with the same `(tmdb_id, type)` and different `item_guid` *bypassing* the listener guard; expect `SqliteException` with constraint violation.
- Hint transfer: `EagerHintSink` populated with hint for `(tmdb, type)`; listener fires; assert hint migrated to BaseItem.Id.

`PhantomItemBackfillSweeperTests`:
- Empty stub root → no inserts, no scan-requeue, zero counts.
- Stub on disk + BaseItem exists + pi row exists → no-op.
- Stub on disk + BaseItem exists + no pi row → pi row inserted.
- Stub on disk + no BaseItem → scoped library scan queued, logged.
- pi row exists + no BaseItem exists + no stub on disk → pi row deleted (cleanup).
- Idempotency: two consecutive runs produce identical state.

`SuggestionsContributorTests` — update existing assertions: `CreateItem` is NEVER called; stub file IS created; `IsBound = false` makes the tick a no-op with warning.

`EagerResolverTests` (NEW or updated):
- BaseItem with phantom-stub Path → enqueues.
- BaseItem with non-phantom Path → does not enqueue.
- BaseItem with null/empty Path → does not enqueue (the old behaviour preserved as a backstop).

`PhantomDbTests`:
- Migration from v5 (with `original_overview` column + no UNIQUE index) to v6: column dropped, index added.
- UNIQUE constraint violation behaviour.

**Live integration tests (rig under `docs/agents/testing.md`):**

`scenarios/10-reactor-no-race.sh`:
1. Stop & wipe rig phantom state via `scripts/phantom-wipe.sh --commit`.
2. Start rig Jellyfin with new plugin DLL.
3. Wait for `_binder.IsBound = true` (poll via diagnostics endpoint or log grep).
4. Trigger SuggestionsRefreshTask via REST.
5. Wait for n stubs to appear on disk.
6. Wait for scanner ingestion (poll BaseItems count under stub-root).
7. Assert: `COUNT(DISTINCT tmdb_id) == COUNT(*) FROM phantom_items WHERE type='movie'` (1:1 invariant).
8. Assert: every BaseItem under stub-root has a matching pi row.
9. Trigger SuggestionsRefreshTask second time. Re-assert (7).
10. Trigger SuggestionsRefreshTask THIRD time to stress-test the dedupe pathway (should produce zero new BaseItems since hits overlap).

`scenarios/11-binder-window-stub-drop.sh`:
1. Wipe state.
2. Start Jellyfin, immediately trigger SuggestionsRefreshTask before binder finishes.
3. Assert: task logs `binder not ready, skipping`. No stubs written.
4. Wait for binder. Trigger again. Stubs written, items appear.

`scenarios/12-eviction-demote-no-orphan.sh`:
1. Set up a Materialised item.
2. Run EvictionSweeper to demote it.
3. Assert: pi row count for that tmdb stays at 1, no orphan.
4. If a new BaseItem was created (different-path demote case), assert old pi row deleted.

`scenarios/13-stub-without-baseitem-recovery.sh`:
1. Write a stub directly to disk under stub-root, bypassing SuggestionsContributor.
2. Wait for backfill sweeper.
3. Assert: scan was queued; eventually BaseItem appears and pi row written.

### UX impact

User-visible changes:

1. **Latency between SuggestionsRefreshTask completion and
   items appearing in the UI** — new in this PR. Today:
   plugin's direct `CreateItem` writes the BaseItem
   synchronously, so items appear in the library as soon
   as the scheduled task finishes (typically under 30 s
   for a Trending fetch + 40-item batch). Post-refactor:
   the scheduled task writes stub files and returns;
   BaseItems appear when the scanner discovers them on
   its next pass.

   Concrete timing on a Jellyfin 10.11 install:
   - Stub files written: 0–5 s (network-bound on TMDB
     fetches).
   - Scheduled task reports success: immediately after
     last stub write.
   - Scanner's incremental file-system watcher (or
     scheduled library scan, whichever fires first):
     typically 0–30 s after a file appears, can be up
     to 5 min if the scanner is busy with another
     library or refresh.
   - `ItemAdded` event fires per item: synchronous with
     BaseItem persist.
   - Listener writes pi row: ~10 ms after `ItemAdded`.
   - Badge controller resolves: next time the UI polls
     `/Plugins/PhantomLibrary/States` (which the badge
     JS does on every list/card render — so immediately
     visible on next scroll or refresh).

   **Total: typically <60 s end-to-end from "click Run
   now" to "badge visible on a freshly-loaded list."
   Can be longer if scanner is busy.** Operator
   confirmed this latency is acceptable.

2. **Badges resolve correctly for every phantom from the
   moment they appear in the UI.** Today: ~20% of newly-
   created phantoms show no badge until a second
   Suggestions tick fixes them. Post-refactor: badges
   work on first appearance, every time.

3. **No `[🟡 materialising…]` / `[✅ Ready — press play]`
   Overview prefix.** `PhantomStatusDecorator` is
   removed. Status during materialisation will be
   visible via:
   - The kebab-menu "Materialise" action's UI feedback
     (already present, unaffected).
   - The new diagnostics endpoint
     (`/Plugins/PhantomLibrary/Diagnostics/StubAudit`)
     for operators/scripts.
   - Standard Jellyfin item-refresh visual cues (the
     spinner Jellyfin shows on items being scanned).

   For the operator's typical interaction (click
   Materialise → wait → play), the kebab JS already
   surfaces the outcome via toast. Overview-prefix was a
   v0.1 workaround; not needed under the new architecture.

4. **The phantom badge overlay (PR by operator, just
   merged) continues to work** — unchanged. The
   listener writes pi rows for every phantom the badge
   controller queries.

5. **Diagnostics for "did Suggestions actually populate?"
   moves from "count phantom_items rows immediately" to
   "wait for scanner + count" or "check stub audit
   endpoint."** Operator-visible only.

Non-changes:

- Materialisation flow: unchanged (kebab → API → queue →
  gostream → promote). User clicks Materialise; backend
  works the same way.
- Eviction: unchanged.
- Autopilot: unchanged.
- Search results, library browse, metadata,
  posters/backdrops: unchanged (in fact: improved,
  because Jellyfin's bundled TMDB image provider takes
  over from `PhantomImageProvider` and uses Jellyfin's
  native image cache).
- gostream protocol: unchanged.

### Operator-visible install procedure

Single PR, single wipe. CHANGELOG entry:

```
BREAKING — requires wipe
========================

This release changes how phantom BaseItems are created.
Existing phantom_items rows are stale (1.79 average bloat,
half orphan-GUID) and the schema adds a UNIQUE index that
those rows would violate.

Operator steps:

1. Stop Jellyfin:
     sudo systemctl stop jellyfin

2. Run the committed wipe script:
     sudo bash scripts/phantom-wipe.sh             # dry-run
     sudo bash scripts/phantom-wipe.sh --commit    # commit

   When prompted, type WIPE in uppercase.

3. Install the new plugin:
     ./install.sh --build

4. Start Jellyfin:
     sudo systemctl start jellyfin

5. Wait for the binder to finish:
     journalctl -u jellyfin -f | grep -i Bound
   Expect "[PhantomBinder] both libraries bound"
   within ~60 s.

6. Trigger Suggestions:
     Dashboard → Scheduled Tasks → "Phantom Library —
     refresh suggestions" → Run Now

7. Wait ~60 s for scanner ingestion. Phantom items
   appear with bracketed-naming titles and badges on
   first appearance.

Manual fallback (if the wipe script is unavailable for
any reason):

    sudo systemctl stop jellyfin
    sudo sqlite3 /var/lib/jellyfin/data/jellyfin.db \
      "DELETE FROM BaseItems
       WHERE Path LIKE '/var/lib/jellyfin/phantom-library/%';"
    sudo rm /var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db*
    sudo find /var/lib/jellyfin/phantom-library/movies -mindepth 1 \
         ! -name '.phantom-library-keep' ! -name '.splash.*' -delete
    sudo find /var/lib/jellyfin/phantom-library/shows -mindepth 1 \
         ! -name '.phantom-library-keep' ! -name '.splash.*' -delete
    ./install.sh --build
    sudo systemctl start jellyfin
```

## Open questions (round 2 for critic)

1. **`SuggestionsContributor.FindExistingByTmdbId`
   deletion.** Plan v2 drops this method entirely
   (dedupe-hit branch no longer exists; the listener
   handles dedup via the UNIQUE index + uniqueness
   check). Is there any other caller? Should the helper
   stay for diagnostic / API use?

2. **`InternalItemsQuery.AncestorIds` scoping for the
   backfill sweeper.** What's the correct way to get
   the phantom-root physical folder IDs at sweeper
   time? `PhantomCollectionFolderBinder` knows them
   (caches in `_bindings`). Expose via a new method on
   the binder, or duplicate the lookup in the sweeper?

3. **Scanner-scoped scan invocation.** Jellyfin 10.11
   exposes `ILibraryManager.QueueLibraryScan` and
   `ValidateMediaLibrary`. Which one fits "scan this
   specific physical folder right now"? Need to
   confirm from the Jellyfin source clone; the API
   surface has changed between 10.x versions.

4. **EagerHintSink rekey collision.** If two hints
   arrive for the same `(tmdb, type)` before either
   resolves to a BaseItem, the second clobbers the
   first. Is this a problem? Today (GUID-keyed),
   collisions are impossible because each
   `CreateItem` produces a unique BaseItem.Id.
   Post-refactor, hints are TMDB-keyed; collisions
   become possible. Probably benign (eager-resolve is
   best-effort) but worth confirming.

5. **Schema migration from v5 to v6.** Per the
   no-migrations rule, the schema bump is allowed
   inside `EnsureSchemaAsync` because the plugin
   recreates from scratch on wipe. But what does
   `EnsureSchemaAsync` actually do for an existing v5
   database that hasn't been wiped? Should it refuse to
   start, or apply ALTER TABLE? Per AGENTS.md, the
   plugin should not silently migrate. Probably:
   detect v5 → log loud error → start anyway with v5
   schema → backfill sweeper hits constraint
   violations → operator sees errors → runs wipe →
   restarts → ensure-schema creates v6 cleanly. Worth
   the critic's call.

6. **Listener idempotency under metadata-refresh `ItemAdded` fires.**
   Per critic note (now Risk R1), `ItemAdded` may fire
   multiple times for the same BaseItem (e.g. during
   refresh). The defensive uniqueness check + UPSERT
   should handle this, but worth a test that the same
   event firing 100x produces a single pi row.

## Risks

R1. `ItemAdded` event semantics in Jellyfin 10.11 may
fire multiple times for the same BaseItem (refresh,
re-scan). Listener is idempotent via UPSERT and the
uniqueness check.

R2. Backfill sweeper enumerating disk on a 9000-item
library: bounded I/O. Single `find` equivalent
in C# — should be sub-second. Tracking via summary
log.

R3. Scanner refusal of filenames with unusual
characters. Unactionable without a known failing
input; sweeper will surface as
`stubs_on_disk_without_pi_row > 0`.

R4. EvictionSweeper different-path demote can lose
UserData. Pre-existing; out of scope for this plan;
tracked separately.

R5. Binder takes >60 s during cold start; `IsBound`
guard prevents premature Suggestions ticks.

R6. Scoped scan-requeue may not exist as a clean API
in 10.11; might need full library scan as fallback.
Investigate during implementation.

## Out of scope (for this PR)

- EvictionSweeper user-data loss in different-path
  demote (pre-existing, tracked separately).
- Series-episode-level phantom tracking (children
  remain scanner-managed with no pi row).
- Materialiser, Autopilot, gostream client —
  unchanged.

## Acceptance

PR is "done" when:

1. `dotnet build -c Release` clean.
2. `dotnet test` green, including:
   - 1:1 invariant tests (operator-requested, DB-level
     UNIQUE rejection + listener defensive check).
   - All new
     `PhantomItemAddedListenerTests`,
     `PhantomItemBackfillSweeperTests`,
     `EagerResolverTests` (path-filter case).
3. All four rig scenarios (10–13) pass.
4. CHANGELOG entry with **BREAKING — requires wipe**
   prefix + inline wipe procedure + script reference.
5. PR description includes operator steps per
   AGENTS.md hand-off rule.
6. `scripts/phantom-wipe.sh` committed; `shellcheck`
   clean; sandbox-validated against the operator's
   actual data shape per AGENTS.md
   § "Production database safety."
