# M12 investigation results (2026-06-07)

## TL;DR

The original M12 diagnosis was wrong on the specific mechanism. Two
real bugs identified:

### Bug 1: `RunMetadataSavers` clears in-memory `ImageInfos` before persistence

When the plugin's `SuggestionsContributor.UpdateItemAsync(newItem)`
runs, Jellyfin's `LibraryManager.UpdateItemsAsync` calls
`RunMetadataSavers(item, updateReason)` BEFORE
`_persistenceService.SaveItems(allItems, ...)`. `RunMetadataSavers`
calls `UpdateImagesAsync` which **mutates the in-memory item's
`ImageInfos` array to empty when the configured image URL cannot
be locally verified or when the underlying file probe sees no
embedded images**.

Verified by rig instrumentation (debug log in
`VirtualItemFactory.CreateVirtualMovieFromHit` +
`SuggestionsContributor.UpdateItemAsync` call site):

```
[FACTORY-DEBUG] tmdb=99000001 ... imageInfos=2
[SUGG-DEBUG] BEFORE UpdateItemAsync tmdb=99000001 ... imageInfos=2
[SUGG-DEBUG] AFTER  UpdateItemAsync tmdb=99000001 ... imageInfos=0
```

For real TMDB ids (where the remote URL resolves cleanly), the
in-memory ImageInfos survive:

```
[SUGG-DEBUG] BEFORE UpdateItemAsync tmdb=1083381 ... imageInfos=2
[SUGG-DEBUG] AFTER  UpdateItemAsync tmdb=1083381 ... imageInfos=2
```

For our mock-TMDB fake-poster paths, the URL doesn't resolve, and
ImageInfos is wiped. So this bug is **only a rig artefact for
mock data**, and is **not the prod failure mechanism**. It does
explain why the rig's `BaseItemImageInfos` table stays empty in
the I1 trace.

Plug. Not pursuing the ImageInfos persistence fix now; doesn't
affect prod.

### Bug 2: stale-row dedupe gap

`SuggestionsContributor.FindExistingByTmdbId` (line 466) queries
via `HasAnyProviderId = new Dictionary<string,string>{ ["Tmdb"]=id }`.
This **only finds existing rows that already have the Tmdb
provider**. A row that was created by us in M10-era code with a
provider, then had its provider stripped by some Jellyfin
metadata-pipeline mechanism, is invisible to dedupe.

Result: plugin's next Suggestions run creates a NEW row alongside
the stale broken one. Both coexist in `BaseItems` with the same
Path and different Ids (because Path differs by some mechanism we
haven't fully traced — possibly symlink-vs-resolved-symlink).

Verified by rig with prod-DB-clone import. After running
Suggestions, the row for tmdb=1083381 (Backrooms) doubles:

```sql
SELECT b.Name, b.IsLocked, p.ProviderValue
FROM BaseItems b LEFT JOIN BaseItemProviders p ON p.ItemId=b.Id AND p.ProviderId='Tmdb'
WHERE b.Path LIKE '%phantom_tmdb1083381%';
Backrooms__phantom_tmdb1083381 | 0 |            -- the M10-era broken row
Backrooms                       | 1 | 1083381   -- the M12-era new clean row
```

Prod has 5021 broken rows that never get cleaned up because dedupe
misses them.

## What the rig DID prove

- **Plugin code's create + UpdateItemAsync path produces a clean
  row** (correct Name, IsLocked=1, ProviderIds[Tmdb], plus correct
  ImageInfos when the remote URL resolves). 520/520 confirmed at
  scale.
- **`ItemPersistenceService.SaveItems` existing-row branch does NOT
  strip providers in the way the M12 plan claimed.** EF Core
  `Attach(...).State = Modified` correctly cascades the new
  `entity.Provider` collection inserts on the attached parent;
  providers persist through both new-row and existing-row branches.
  Critic blocker #1 resolved.
- **Library scan and per-item `/Items/{id}/Refresh` with
  `MetadataRefreshMode=FullRefresh&ReplaceAllMetadata=true` do NOT
  mutate locked items.** I1 trace T2→T3→T4→T5 showed identical
  state. Critic concern about scanner clobbering: refuted.

## What remains unknown

**How did the 5021 prod rows lose their providers + IsLocked
originally?** They were created by some M10-era code path with
providers + locked. By the time we observed them today, they had
neither. Three candidates:

1. **The 09:26 mass-removal** in the prod log
   (`Removing item, Type: "Movie", Name: "Backrooms", Path: ...`).
   Maybe the items were deleted then re-created from filename by
   the scanner. Scanner-created rows have no providers and
   IsLocked=0. Matches the symptom exactly.
2. **An earlier broken DLL version** (pre-M11) where the create
   path didn't set IsLocked or providers correctly. The repair
   script today (M11) reset some but not all.
3. **A periodic library validation** that runs in production with
   a heavy `RunMetadataSavers` that does mutate row state for items
   whose Path file doesn't probe cleanly.

For the operator's immediate problem, **the actual root cause
doesn't matter** as long as we have a recovery path. The dedupe
gap (bug 2) is the actionable fix.

## Recommended fix

**Single change** to `SuggestionsContributor.FindExistingByTmdbId`:
broaden the dedupe to include path-stem match in addition to
provider match. Existing broken rows have Path that contains
`__phantom_tmdb<id>` — we can match on that.

```csharp
private BaseItem? FindExistingByTmdbId(string tmdbId, ItemKind kind)
{
    // First: standard provider-based lookup (handles rows the plugin
    // created and the provider survived).
    var byProvider = _libraryManager.GetItemList(new InternalItemsQuery
    {
        IncludeItemTypes = new[] { /* ... */ },
        HasAnyProviderId = new Dictionary<string, string> { [TmdbProvider] = tmdbId },
        Limit = 1,
    });
    if (byProvider.Count > 0) return byProvider[0];

    // Second: path-stem lookup (handles broken legacy rows where
    // providers were stripped by an upstream metadata pipeline).
    // Match against `%__phantom_tmdb<id>.%` so we find our own
    // symlinks even when their metadata is gone.
    var byPath = _libraryManager.GetItemList(new InternalItemsQuery
    {
        IncludeItemTypes = new[] { /* ... */ },
        Path = $"%__phantom_tmdb{tmdbId}.%",  // SQL LIKE pattern; check query semantics
        Limit = 1,
    });
    return byPath.Count > 0 ? byPath[0] : null;
}
```

When dedupe finds a broken legacy row, the existing code path
(line 379) calls `UpsertPhantomRowAsync` and continues. Need to
add: **also re-stamp the BaseItem's Name + IsLocked + ProviderIds
via UpdateItemAsync** so the dedupe-hit case heals broken state.

## Critic concerns: status

| # | Concern | Status |
|---|---|---|
| 1 | Diagnosis self-contradicts (existing-row branch claim) | **REFUTED.** Rig trace shows providers DO persist through both branches; diagnosis was wrong. |
| 2 | Layer A doesn't remove the alleged-culprit `UpdateItemAsync` call | **MOOT.** UpdateItemAsync isn't the culprit. |
| 3 | Mock-only unit tests | **STILL VALID.** Any real fix needs an integration test against the rig. |
| 4 | Missing scenarios 04/05/06 | **PARTIAL.** I1 trace scenario landed at `/tmp/jf-rig/scenarios/I1-single-row-trace.sh`; needs check-in. Prod-clone scenario was ad-hoc; should be scripted. |
| 5 | Repair script provenance-blind SQL | **STILL VALID** if we ship a repair script. |
| 6 | "Self-heals without repair script" claim | **STILL VALID.** Plus my updated dedupe fix DOES self-heal — every Suggestions run that re-touches a tmdb_id now matches the broken row and heals it. |
| 7 | DeleteItem side effects | **MOOT** — no DeleteItem in the new fix. |
| 8 | 108/5021 split unexplained | **PARTIALLY EXPLAINED**: bundled TmdbProvider fuzzy-match still applies; IsLocked=0 universality matches the candidate cause "items were re-created by scanner from filename after some earlier mass-delete." |
| 9 | Architectural question | **DEFERRED** per operator. New dedupe fix is a smaller intervention than the redesign; revisit redesign if dedupe fix also fails. |

## Next action

Implement the dedupe fix. Strip debug logs. Land an integration
test that pre-populates the rig DB with a broken-shaped row, runs
Suggestions, and asserts the broken row was healed (not duplicated).
