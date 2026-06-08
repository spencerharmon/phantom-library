# M12 — Phantom item ID-collision recovery (plan)

> **⚠ Historical investigation. References deprecated naming
> scheme.** SQL recipes and design rationale below reference the
> legacy `__phantom_tmdb<id>` filename sentinel. That scheme is
> deprecated; the canonical scheme is Jellyfin-native
> `[tmdbid-<id>]`. For current stub identification, use
> `PhantomPathUtilities.IsPhantomStubPath` (recognises both forms)
> or the bracketed `[tmdbid-<id>]` token. See AGENTS.md §
> "Canonical phantom stub naming scheme." Content below is kept
> as the historical investigation record.

Date: 2026-06-07
Status: **diagnosis disputed by critic; investigation required before any code change**
Author: agent
Operator: spencerharmon

## Status update (after critic review)

The original draft (preserved below for history) was BLOCKED by
critic on three counts that invalidate the diagnosis itself:

1. **Diagnosis self-contradicts.** Plan claims
   `ItemPersistenceService.SaveItems` existing-row branch strips
   providers. But the plugin code calls `UpdateItemAsync(newItem)`
   immediately after `CreateItem` (`SuggestionsContributor.cs:447-449`).
   That second call hits the same branch on every fresh-DB run. Yet
   the rig reports 520/520 perfect on a fresh DB. Both facts cannot
   be true. Either the existing-row branch is not destructive
   (diagnosis wrong), or the 520/520 rig was not exercising the
   path I assumed, or the prod failure has a different root cause.

2. **Layer A is a no-op for the alleged bug.** Purge-then-create
   still leaves the `UpdateItemAsync(newItem)` call in place
   immediately after; per the diagnosis, that call would re-trigger
   the bug on the just-created row.

3. **5021/5021 prod rows are `IsLocked=0`, not just lacking
   providers.** Plugin sets `IsLocked=true` at creation. Universal
   `IsLocked=0` means the lock is being stripped on EVERY row, not
   just on prod-state-collision rows. That points at a different
   root cause than the existing-row-branch provider-strip
   hypothesis.

4. **The 108/5021 with providers split.** Plan attributes to
   bundled `TmdbProvider` fuzzy-match. Since all 5021 have
   `IsLocked=0`, the unlock-and-overwrite path runs universally;
   the 108/4913 split is then "did fuzzy-match succeed." That's
   orthogonal to the provider-strip claim.

Plus four MAJOR concerns about unit-test value (Mock-only, same
shape as M11's failed tests), missing rig scenarios (04/05/06
referenced but never written), provenance-blind repair-script SQL,
and DeleteItem side effects at 5021-row scale.

Full critic findings in `docs/plans/M12-collision-recovery.critic.md`
(or capture inline if no separate file).

## Decision (operator-approved 2026-06-07)

Do not implement M12 as drafted. **Investigate the diagnosis
first.** Architectural redesign ("let the scanner own ids") is
deferred but on the table after investigation results land.

## Investigation plan (BLOCKING any M12 code change)

Four scenarios in the persistent rig at `/tmp/jf-rig/`. Each must
be a single bash script under `/tmp/jf-rig/scenarios/` (checked in
to repo at `tools/rig-scenarios/` once stable), idempotent,
produces a labelled log artefact under `/tmp/jf-rig/logs/`.

The four investigations:

### I1 — Single-row mutation trace

**Question**: when a phantom row goes from correct-at-CreateItem
to broken (IsLocked=0, no providers), at which step does each
field change?

**Procedure**:
1. Wipe rig phantom state.
2. Start `db-observer.py` watching `BaseItems` (Id, Name, IsLocked,
   ForcedSortName) and `BaseItemProviders` for ONE specific
   phantom Id (derived from a fixed mock TMDB id 99000001).
3. Run Suggestions/Refresh (mock TMDB).
4. Trigger library scan.
5. Trigger `/Items/{id}/Refresh?MetadataRefreshMode=FullRefresh`.
6. Stop observer. Compare timeline.

**Answers**:
- Q1: After `CreateItem` returns (before `UpdateItemAsync`), is
  `IsLocked=1` in DB? Are providers present?
- Q2: After `UpdateItemAsync` returns, is anything stripped?
- Q3: After library scan, is anything stripped?
- Q4: After `/Items/{id}/Refresh`, is anything stripped?

Resolves critic blocker #1 by attributing each mutation to a
specific step.

### I2 — Real-EF integration test for existing-row provider persistence

**Question**: does `ItemPersistenceService.SaveItems`
existing-row branch actually strip `BaseItemProviders`, or does
EF Core's `Attach(...).State = Modified` cascade-insert the
`entity.Provider` navigation collection?

**Procedure**: write a focused C# test (not in the plugin test
project; in a separate scratch project under `tools/` if needed)
that:
1. Spins up `JellyfinDbContext` against SQLite in-memory.
2. Inserts a `BaseItem` row with no providers.
3. Calls `ItemPersistenceService.SaveItems` (or a minimal harness
   that mirrors it) with a `BaseItemDto` that has the same Id and
   a populated `ProviderIds["Tmdb"]`.
4. `SELECT * FROM BaseItemProviders WHERE ItemId=<id>` and assert.

Alternatively (cheaper, less direct): write a rig scenario that
pre-inserts a path-less row with the derived Id of a fixture
TMDB id (via raw SQL), then runs Suggestions, then checks the row.
This tests through the full plugin → LibraryManager → EF stack.
Cannot prove or refute the EF semantics in isolation but does
prove whether real-stack behaviour matches my hypothesis.

Resolves critic blocker #1 + #3.

### I3 — Prod-clone re-import + Suggestions-twice

**Question**: when the rig has prod's exact broken state imported,
does Layer-A-style purge-then-create actually fix it? Does running
Suggestions a second time without changes drift the state?

**Procedure**: scenarios `04-collision-clean.sh`,
`05-collision-import-prod.sh`, `06-collision-rerun.sh`. Defined in
the draft but never written.

Resolves critic blocker #4 (missing scenarios) + tests Layer A's
actual effect when (if) implemented.

### I4 — IsLocked persistence trace

**Question**: per critic #8, all 5021 prod rows have `IsLocked=0`
despite plugin setting `IsLocked=true`. Where is the unlock
happening? Is it persistence-layer-driven or
metadata-refresh-driven?

**Procedure**: same as I1 but focused on the `IsLocked` column.
If the row is `IsLocked=1` after `CreateItem` and `IsLocked=0`
after `UpdateItemAsync`, the bug is `BaseItemMapper.Map` not
preserving `IsLocked` from the DTO (check the mapper source).
If the row is `IsLocked=1` after both and `IsLocked=0` only after
the library scan or metadata refresh, the bug is the metadata
pipeline overriding our lock (different fix).

Resolves critic blocker #1 partial + #8.

## Outcomes that drive next action

| Investigation result | Next action |
|---|---|
| I1+I2+I4 confirm `ItemPersistenceService` existing-row branch strips providers AND `IsLocked` | Implement Layer A as drafted, BUT also remove the post-`CreateItem` `UpdateItemAsync` call per critic #2; add real-EF integration test per critic #3 |
| I1+I4 show `UpdateItemAsync` is the culprit (not the existing-row branch but the second-write-on-fresh-row branch) | Remove the post-`CreateItem` `UpdateItemAsync` call; no Layer A needed |
| I1+I4 show library scan or metadata refresh is the culprit | Different fix entirely; investigate which Jellyfin code path overrides locked items; possibly a new pre-resolve hook to short-circuit |
| All investigations come back inconclusive | Defer M12, reopen architectural-redesign discussion ("let scanner own ids") |

## Investigation deliverables

- 4 scenario scripts under `/tmp/jf-rig/scenarios/` (copy to repo
  on stabilisation).
- Investigation report at `docs/plans/M12-investigation-results.md`
  with: one section per Q1–Q4 of I1; I2's raw SELECT output; I3's
  log artefacts; I4's IsLocked timeline. Each answer cited to
  log line or DB row.
- Decision section at the top of the report selecting one of the
  four outcome rows in the table above.

No plugin code change until the report exists.

## Architectural redesign (deferred, not abandoned)

Per critic #9: three rounds of "compute stable Id, call CreateItem,
hope it works" have not held up in production. The redesign
alternative is: stop using `_libraryManager.GetNewItemId` and
`CreateItem` for phantoms. Instead:

1. Plugin creates the phantom-stub symlinks on disk
   (`PhantomStubManager` already does this).
2. Plugin does NOT call `CreateItem`. Instead it triggers a
   library-validation scan (or relies on the periodic one) and lets
   Jellyfin's scanner discover the symlinks and assign Ids itself.
3. Plugin then queries `_libraryManager.GetItemList` to find the
   scanner-created rows by Path, and stamps metadata
   (Name, ProviderIds, IsLocked, ImageInfos) onto them via the
   already-shipped `UpdateItemAsync` path.

Trade-offs:
- Pro: never fight the scanner's id-assignment. Path is the
  source of truth, scanner is the authority. Fits Jellyfin's model.
- Pro: works whether or not the operator pre-creates symlinks
  manually.
- Con: adds a latency window between symlink-create and
  metadata-stamp where the row is broken. Users browsing during
  that window see filename-stem Names.
- Con: requires triggering or waiting for a scan; may be slow.
- Con: post-stamp `UpdateItemAsync` STILL hits whatever code path
  caused the original bug (if I2 confirms it). So this design only
  helps if the bug is in `CreateItem`'s collision-handling, NOT
  in `UpdateItemAsync`'s persistence.

Revisit AFTER investigation results land. The investigation will
also tell us whether the redesign even helps.

---

## ORIGINAL DRAFT (BLOCKED — retained for history)

## Problem

Operator's prod Jellyfin has ~5021 phantom rows in `BaseItems` that
look like this:

- `Name` = filename stem (e.g. `Backrooms__phantom_tmdb1083381`)
- `IsLocked = 0`
- no `BaseItemProviders[Tmdb]`
- only ~108 of 5021 have providers (from Jellyfin's bundled
  `TmdbProvider` fuzzy-matching the filename stem against real TMDB)

This breaks materialise (no TMDB id → `Materialiser` bails before
calling gostream) and breaks the UI (filename stems instead of titles,
splash thumbnail instead of TMDB poster).

## Root cause (confirmed by rig 2026-06-07)

Sequence:

1. Plugin (`SuggestionsContributor`) constructs a `BaseItem` in memory
   with `Name=<title>`, `IsLocked=true`, `ProviderIds[Tmdb]=<id>`,
   `Path=<phantom-stub-symlink>`.
2. Plugin computes `newItem.Id = _libraryManager.GetNewItemId(stableKey, type)`
   where `stableKey = "phantom_movie_<tmdbId>"`. Deterministic.
3. Plugin calls `_libraryManager.CreateItem(newItem, parent)`.
4. `CreateItem` →  `_persistenceService.SaveItems([newItem], ...)`.
5. `ItemPersistenceService.SaveItems`
   (`jellyfin/Jellyfin.Server.Implementations/Item/ItemPersistenceService.cs`
   line 256–284):

   ```csharp
   var existingItems = context.BaseItems.Where(e => ids.Contains(e.Id))
       .Select(f => f.Id).ToArray();
   foreach (var item in tuples) {
       var entity = BaseItemMapper.Map(item.Item, _appHost);
       if (!existingItems.Any(e => e == entity.Id)) {
           context.BaseItems.Add(entity);          // ✓ cascades to Providers
       } else {
           context.BaseItemProviders.Where(...).ExecuteDelete();      // 🩸
           context.BaseItemImageInfos.Where(...).ExecuteDelete();
           context.BaseItemMetadataFields.Where(...).ExecuteDelete();
           if (entity.Images is { Count: > 0 })
               context.BaseItemImageInfos.AddRange(entity.Images);     // re-added
           if (entity.LockedFields is { Count: > 0 })
               context.BaseItemMetadataFields.AddRange(entity.LockedFields); // re-added
           context.BaseItems.Attach(entity).State = EntityState.Modified;
           // 🩸 BaseItemProviders NOT re-added. Entity.Provider is set
           //    on the EF entity but Attach+Modified doesn't cascade
           //    children inserts. Providers are lost.
       }
   }
   ```

   The existing-row branch deletes providers without re-adding. The
   new-row branch (`context.BaseItems.Add(entity)`) cascades correctly.

6. On prod, `BaseItems` already has a row with that `Id` from an
   earlier M10-era run. CreateItem hits the existing-row branch.
   Providers and Name get wiped relative to what we passed.

Rig evidence:

- Empty rig + `SuggestionsContributor.RefreshAllAsync` (real TMDB) →
  520 / 520 rows correct (Name, IsLocked, Tmdb provider, ImageInfos).
- Prod-DB-clone + same `RefreshAllAsync` → reproduces prod failure
  (existing rows untouched in BaseItems; new ones broken).
- Wipe ALL phantom rows from prod-DB-clone + re-run → 27 / 27 correct
  (a few `created=X but Y in DB` losses are dedupe against real
  gostream-movies rows from prod, not the bug).

Mechanism #2 ("scanner clobbers in-flight after CreateItem") was the
other suspect. Rig shows it does NOT happen — scans + per-item
`/Items/{id}/Refresh?MetadataRefreshMode=FullRefresh&ReplaceAllMetadata=true`
both leave correct rows untouched.

## Fix surface

Three layers, in priority order:

### Layer A — plugin: collision detection + recovery (BLOCKING)

In `SuggestionsContributor.MaterialiseHitsAsync`, right after
`newItem.Id = _libraryManager.GetNewItemId(...)` and BEFORE
`_libraryManager.CreateItem(newItem, parent)`:

```csharp
// Defensive: if a BaseItem with our derived Id already exists, it is
// either (a) one of our own from a prior run, or (b) something else
// claiming the same Id. Hitting CreateItem here would trigger
// ItemPersistenceService.SaveItems' existing-row branch, which is
// known to strip BaseItemProviders. Delete first to guarantee we
// go through the new-row Add path (which DOES cascade providers).
var existing = _libraryManager.GetItemById(newItem.Id);
if (existing is not null)
{
    try
    {
        _libraryManager.DeleteItem(existing, new DeleteOptions
        {
            DeleteFileLocation = false,
            DeleteFromExternalProvider = false,
        }, parent, false);
        _logger.LogDebug(
            "[Suggestions] purged existing BaseItem {Id} ({Name}) to avoid persistence-layer provider-strip bug",
            existing.Id, existing.Name);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex,
            "[Suggestions] could not purge existing BaseItem {Id}; proceeding (may produce a broken row)",
            existing.Id);
    }
}
```

Notes on the call:

- `DeleteFileLocation = false` — the symlink on disk may be valid;
  we'll re-create it via `_stubs.CreateAsync` in the very next
  statements anyway. We do NOT want to delete and recreate the
  splash.mp4 inode (`DeleteFileLocation=true` could cascade unwanted
  deletes).
- `DeleteFromExternalProvider = false` — never round-trip to TMDB
  for an item we're about to recreate.
- The `parent` is the same `Folder` we'd pass to `CreateItem`.

Same fix in `SeriesIngestor.CreateOrTouchSeriesAsync` (if it also
uses GetNewItemId + CreateItem for Series rows).

### Layer B — plugin: id-derivation change (FOLLOW-UP, lower priority)

Replace the current `GetNewItemId` input
`"phantom_movie_<tmdbId>"` with a tuple that includes the plugin's
GUID:

```csharp
$"phantom_{plugin_guid}_{kind}_{tmdbId}"
// e.g. phantom_9e7a1f4c2b5d4e8f9a3b7c1d2e5f6a8b_movie_1083381
```

The plugin GUID is fixed (`Plugin.PluginId`). Including it in the
key makes our derived ids:

- Globally unique to phantoms (no chance of colliding with another
  source even theoretically).
- Stable across plugin restarts and reinstalls (same GUID always).
- Different from any historical M10-era id, which forces every
  phantom to take the new-row path on a one-time basis. (Combined
  with Layer C's reconcile.)

Cost: changes every existing phantom item's Id. Operator must run
a reconcile to repoint plugin DB rows (`phantom_items.item_guid`)
at the new ids. This is acceptable as a one-time migration but is
why Layer A is the immediate fix.

### Layer C — operator-side: thorough repair script

Existing `phantom-m11-repair.sh` only deletes rows matching
`Path LIKE '%__phantom_tmdb%'`. Misses:

- Rows with `Path=NULL` but derived from `phantom_movie_<id>` (the
  pre-Layer-A future state if Suggestions ran with bootstrap failure).
- Rows in `phantom.db` with `tmdb_id=NULL, type='unknown'` written
  by `PhantomDb.PreserveOriginalOverviewAsync` placeholder inserts.

New `phantom-m12-repair.sh`:

```bash
sudo systemctl stop jellyfin

# Backup
TS=$(date +%Y%m%d-%H%M%S)
cp -a /var/lib/jellyfin/data/jellyfin.db{,.bak-$TS}
cp -a /var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db{,.bak-$TS}

# Wipe phantom-related state in jellyfin.db.
sqlite3 /var/lib/jellyfin/data/jellyfin.db <<'SQL'
CREATE TEMP TABLE _purge AS
SELECT Id FROM BaseItems
WHERE
  -- Items with phantom-stub paths.
  Path LIKE '%__phantom_tmdb%'
  -- Items with paths under the phantom-library root (defensive).
  OR Path LIKE '/var/lib/jellyfin/phantom-library/%'
  -- Items with no providers AND a phantom-shaped Name.
  OR (Name LIKE '%__phantom_tmdb%' AND Id NOT IN (SELECT ItemId FROM BaseItemProviders))
  -- Path-less Movies/Series with no providers (orphan pre-M10 wreckage).
  OR (Type IN ('MediaBrowser.Controller.Entities.Movies.Movie',
               'MediaBrowser.Controller.Entities.TV.Series',
               'MediaBrowser.Controller.Entities.TV.Episode')
      AND (Path IS NULL OR Path='')
      AND Id NOT IN (SELECT ItemId FROM BaseItemProviders));

DELETE FROM BaseItemProviders WHERE ItemId IN (SELECT Id FROM _purge);
DELETE FROM BaseItemImageInfos WHERE ItemId IN (SELECT Id FROM _purge);
DELETE FROM BaseItemMetadataFields WHERE ItemId IN (SELECT Id FROM _purge);
DELETE FROM BaseItemTrailerTypes WHERE ItemId IN (SELECT Id FROM _purge);
DELETE FROM ItemValuesMap WHERE ItemId IN (SELECT Id FROM _purge);
DELETE FROM PeopleBaseItemMap WHERE ItemId IN (SELECT Id FROM _purge);
DELETE FROM MediaStreamInfos WHERE ItemId IN (SELECT Id FROM _purge);
DELETE FROM UserData WHERE ItemId IN (SELECT Id FROM _purge);
DELETE FROM BaseItems WHERE Id IN (SELECT Id FROM _purge);

-- Tidy orphans.
DELETE FROM BaseItemProviders WHERE ItemId NOT IN (SELECT Id FROM BaseItems);
SQL

# Wipe plugin DB entirely; Catalogue will repopulate.
sqlite3 /var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db \
  "DELETE FROM phantom_items; DELETE FROM tmdb_cache;"

# Remove stale symlinks.
find /var/lib/jellyfin/phantom-library -type l -name '*__phantom_tmdb*' -delete

sudo systemctl start jellyfin
```

Operator runs once. Subsequent Suggestions runs hit the Layer-A code
path and produce clean state from scratch.

### Layer D — upstream Jellyfin PR (DEFERRED, tracked separately)

The actual bug is in upstream `ItemPersistenceService.SaveItems`. The
existing-row branch should call `context.BaseItemProviders.AddRange(entity.Provider)`
after the delete, mirroring how it handles `Images` and `LockedFields`
on lines 272 and 278.

Track as a separate issue against `jellyfin/jellyfin`. Plugin works
around regardless. Already a deferred upstream item per PLAN §M10
§Jellyfin upstream issue; M12 expands the scope of what that PR
should fix.

## Tests

### Unit tests (NEW)

`M12CollisionRecoveryTests.cs`:

1. `Suggestions_WithExistingBaseItem_PurgesBeforeCreate`:
   pre-populate `Mock<ILibraryManager>.GetItemById` to return a fake
   existing item. Run Suggestions for a hit whose derived id matches.
   Assert: `DeleteItem` called once for the existing item BEFORE
   `CreateItem` is called. Argument shape: `DeleteFileLocation=false`,
   `DeleteFromExternalProvider=false`.

2. `Suggestions_WithoutExistingBaseItem_DoesNotCallDelete`:
   `GetItemById` returns null. Run Suggestions. Assert: `DeleteItem`
   is NEVER called; `CreateItem` is called normally.

3. `Suggestions_DeleteThrows_StillAttemptsCreate`:
   `DeleteItem` throws. Run Suggestions. Assert: warning logged;
   `CreateItem` is still called. (Layer A is best-effort; even if
   delete fails, the existing row makes things no worse than today.)

### Integration tests (rig)

Scenarios under `/tmp/jf-rig/scenarios/`:

1. `04-collision-clean.sh` — clean rig + real TMDB + scale 50 →
   all rows correct.
2. `05-collision-import-prod.sh` — clone prod DB into rig (with
   broken state) + run Suggestions → expect Layer A to detect and
   purge → all rows correct after.
3. `06-collision-rerun.sh` — run Suggestions twice back-to-back →
   first run creates rows, second run hits Layer A purge path on
   every item → final state identical to first run (no drift).

All three scenarios must finish with:
- `BaseItems.IsLocked = 1` for every phantom.
- `BaseItemProviders[Tmdb]` set for every phantom.
- `BaseItemImageInfos[Primary].Path` set for every phantom.
- `phantom_items.tmdb_id` set (no NULLs) for every phantom.

### M11 regression suite

All 11 existing `M11BugsTests.cs` tests must continue passing.

### Live integration (operator-driven)

After plugin fix lands + operator runs the repair script + triggers
Suggestions, operator presses Play on any phantom. Agent pulls
`/System/Logs/Log?name=jellyfin<date>.log` and confirms:

- `[PlaybackTrigger] phantom Play pressed: <Name> (<Id>); enqueueing materialise`
- `Materialise <Id>: ...` followed by gostream POST request log line
  (success or content-specific failure — the absence of "lacks TMDB/IMDB
  provider ids" is the win condition).

## Operator actions required

After plugin fix is built and installed:

1. `sudo /tmp/phantom-m12-repair.sh` (the new repair script above).
2. Dashboard → Scheduled Tasks → "Phantom Library — refresh suggestions" → Run.
3. Wait for completion (Catalogue walk: ~5 min per 1000 items).
4. Confirm: pick any phantom in `gostream-movies`; verify Name is the
   TMDB title, image is the TMDB poster.
5. Press Play. Splash plays. Watch the log for the materialise
   activity above.

## Risks

1. **DeleteItem semantics on a row that's a child of multiple
   structures.** If the existing row is reachable via the user's
   favourites or watch history, deleting it loses that user state.
   Mitigation: rare — phantoms haven't been favouriteable in prod
   because they've never been playable. UserData on a phantom is
   always 0/empty. Verified by SQL inspection of prod
   `UserData WHERE ItemId IN (phantom Ids)`.

2. **DeleteItem cascade.** If our derived id ever accidentally
   collides with a non-phantom row (Movies in gostream-movies, real
   media), we'd delete real media. Mitigation: our key
   `"phantom_movie_<tmdbId>"` is a fixed string under the
   `MediaBrowser.Controller.Entities.Movies.Movie` type prefix. Real
   gostream items have path-derived keys. MD5 collision risk is
   cryptographic, not realistic. Layer B reduces this to zero by
   including the plugin GUID.

3. **Race with PhantomCollectionFolderBinder.** If the Layer-A
   delete-then-create runs while the binder's `ItemUpdated`
   watchdog is also touching the CollectionFolder, EF context
   conflicts possible. Mitigation: Suggestions doesn't touch
   CollectionFolders, only Movie/Series rows; binder doesn't touch
   Movie/Series rows. Disjoint. Verified by reading binder source.

4. **Plugin DB tmdb_cache and phantom_items survive the wipe**
   (in Layer C). Acceptable: tmdb_cache speeds up next Catalogue;
   phantom_items rows get repointed at new BaseItem Ids when
   Suggestions creates them with the same tmdb_id.

5. **Operator forgets to run the repair script.** Same as M11 #5
   experience. Layer A makes the plugin self-heal even without the
   script: every Suggestions run purges colliding rows it
   encounters. The script accelerates the cleanup but is no longer
   strictly required after Layer A ships.

## Out of scope for M12

- Upstream PR (Layer D) — track separately.
- Layer B id-derivation change — defer to a v0.2 cleanup unless
  Layer A surfaces an issue.
- New tests for phantoms.db's `PreserveOriginalOverviewAsync`
  insert-with-NULL-tmdb_id bug — that's M11 #4 territory; M12 is
  scoped to the collision-recovery path.

## Commit plan

| commit | scope |
|---|---|
| `feat(M12): purge colliding BaseItem before CreateItem` | Layer A in SuggestionsContributor + SeriesIngestor |
| `test(M12): collision-recovery unit + rig scenarios` | new tests |
| `script(M12): phantom-m12-repair.sh` | Layer C |
| `docs(M12): mark milestone DONE + operator steps + risks` | PLAN.md + CHANGELOG.md + AGENTS.md operator hand-off |
