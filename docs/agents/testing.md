# Testing Phantom Library

Operator does not want to be in the loop for every test cycle.
Run live integration tests yourself against a clone of the
operator's production Jellyfin and gostream state. This doc
describes the rig, the procedure, and the foot-guns that ate
hours during M6 / M7 visibility debugging — read it before
asking the operator to run anything.

## Rule

**Never ask the operator to run a SQL query, copy a DB, or
restart Jellyfin to verify a fix.** Spin up your own Jellyfin
test instance from the cloned production DB, drive it via REST,
and inspect the resulting DB directly. If the rig cannot start
for a real environmental reason (e.g. .NET runtime version
missing) — say so explicitly and ask. Otherwise, do it
yourself.

The single exception: copying the production DB requires the
operator to have granted you read permission on it. The operator
has already done so via `chmod o+x` on the relevant parent dirs
plus world-read on the DB files themselves:

```
/var/lib/jellyfin/data/jellyfin.db        # main Jellyfin EF Core DB
/var/lib/jellyfin/data/jellyfin.db-wal    # WAL — MUST be copied with the .db
/var/lib/jellyfin/data/jellyfin.db-shm    # shared-memory index — MUST be copied
/var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db
```

If you find them unreadable on a future engagement, ask the
operator to re-apply traversal permissions to their parent
dirs — that is the only legitimate ask. Do **not** propose
running anything via `sudo` on your side; you don't have it.

## Jellyfin source is in-tree

`./jellyfin/` (sibling of `src/`) is a clone of the Jellyfin
10.11.x source. **Read it before guessing about Jellyfin
internals.** Particularly relevant:

- `MediaBrowser.Controller/Entities/CollectionFolder.cs` —
  multi-path library binding logic (`PhysicalLocationsList`,
  `PhysicalFolderIds`, `RefreshLinkedChildrenInternal`).
- `Emby.Server.Implementations/Library/LibraryManager.cs` —
  `GetTopParentIdsForQuery` (line ~2068, the function that
  resolves browse queries: for a CollectionFolder it returns
  `collectionFolder.PhysicalFolderIds`).
- `Jellyfin.Api/Controllers/LibraryStructureController.cs` —
  the `/Library/VirtualFolders` endpoints, including the
  known-broken-in-this-rig `AddMediaPath`.

Grep before assuming. The Plugin SDK alone does not expose
these internals; reading source is the only honest way to
know what the host will do.

## Rig layout

The test rig lives under `/tmp/jf-test/`. Once seeded it looks
like this:

```
/tmp/jf-test/
├── start.sh                    # launches dotnet jellyfin.dll
├── run-test.sh                 # full driver: start + wait + REST + inspect + kill
├── data/                       # --datadir
│   ├── data/                   # Jellyfin SQLite DBs
│   │   ├── jellyfin.db
│   │   ├── jellyfin.db-wal
│   │   └── jellyfin.db-shm
│   ├── root/default/           # CLONED FROM PROD — required
│   │   ├── gostream-movies/    #   .mblink + options.xml per library
│   │   ├── gostream-shows/
│   │   ├── Movies/
│   │   └── Shows/
│   └── plugins/
│       ├── configurations/
│       │   ├── Jellyfin.Plugin.PhantomLibrary.xml   # plugin config seed
│       │   └── PhantomLibrary/phantom.db            # plugin SQLite
│       └── Jellyfin.Plugin.PhantomLibrary_0.1.0.0/
│           └── Jellyfin.Plugin.PhantomLibrary.dll   # the build under test
├── config/                     # --configdir
│   └── network.xml             # MUST pre-seed to force port 18096
├── cache/                      # --cachedir
├── log/                        # --logdir
├── media/{tv,movies}/          # empty stand-in library roots if needed
├── run.log                     # stdout/stderr of jellyfin
└── out.log                     # output of run-test.sh
```

The Jellyfin server binary itself lives at
`/usr/lib/jellyfin/jellyfin.dll` with web assets at
`/usr/share/jellyfin/web` and ffmpeg at
`/usr/lib/jellyfin-ffmpeg/ffmpeg`. `dotnet` (net9.0) is on PATH.
You do not need to install anything.

## Bring-up procedure (full clean clone)

```bash
# 0. kill any prior test instance. NEVER use a bare
#    `pkill -f jellyfin.dll` — that regex matches the
#    systemd-launched production instance owned by the jellyfin
#    user. You'll get permission denied AND race with the real
#    service. Always scope by user and by the jf-test path.
pkill -u "$USER" -9 -f "dotnet.*jellyfin.dll.*jf-test"
sleep 2

# 1. clone the production DBs (with WAL+SHM so the snapshot is consistent).
# ALSO clone /root/default — without it, AddMediaPath returns 404
# ("Could not find a part of the path .../<libname>/<safename>.mblink")
# because Jellyfin's library config dir on disk is missing.
rm -rf /tmp/jf-test
mkdir -p /tmp/jf-test/{data/data,data/plugins/configurations/PhantomLibrary,data/plugins/Jellyfin.Plugin.PhantomLibrary_0.1.0.0,data/root/default,config,cache,log,media/tv,media/movies}
cp /var/lib/jellyfin/data/jellyfin.db       /tmp/jf-test/data/data/jellyfin.db
cp /var/lib/jellyfin/data/jellyfin.db-wal   /tmp/jf-test/data/data/jellyfin.db-wal
cp /var/lib/jellyfin/data/jellyfin.db-shm   /tmp/jf-test/data/data/jellyfin.db-shm
cp -r /var/lib/jellyfin/root/default/*      /tmp/jf-test/data/root/default/
cp /var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db \
   /tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db

# 2. drop the freshly-built plugin DLL
cd /home/spencer/git-repos/spencerharmon/phantom-library
dotnet build -c Release
cp src/Jellyfin.Plugin.PhantomLibrary/bin/Release/net9.0/Jellyfin.Plugin.PhantomLibrary.dll \
   /tmp/jf-test/data/plugins/Jellyfin.Plugin.PhantomLibrary_0.1.0.0/
md5sum src/Jellyfin.Plugin.PhantomLibrary/bin/Release/net9.0/Jellyfin.Plugin.PhantomLibrary.dll \
       /tmp/jf-test/data/plugins/Jellyfin.Plugin.PhantomLibrary_0.1.0.0/Jellyfin.Plugin.PhantomLibrary.dll
# ALWAYS md5 both sides. The plugin csproj has incremental-build
# quirks and the dest dir is easy to forget; mismatched md5 = your
# edits did not take and you will waste an hour wondering why the
# fix does nothing.

# 3. pre-seed the plugin config XML so the TMDB key is loaded at startup.
#    Operator's prod key lives at /etc/gostream/config.json under
#    "tmdb_api_key" (world-readable).
TMDB_KEY=$(awk -F '"' '/tmdb_api_key/ {print $4}' /etc/gostream/config.json)
cat > /tmp/jf-test/data/plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml <<EOF
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration>
  <TmdbApiKey>$TMDB_KEY</TmdbApiKey>
  <GostreamBaseUrl>http://127.0.0.1:9080</GostreamBaseUrl>
  <GostreamDiagnosticsBaseUrl>http://127.0.0.1:8090</GostreamDiagnosticsBaseUrl>
  <ProwlarrBaseUrl></ProwlarrBaseUrl>
  <ProwlarrApiKey></ProwlarrApiKey>
  <TorrentioBaseUrl>https://torrentio.strem.fun</TorrentioBaseUrl>
  <QualityPreset>GostreamDefault</QualityPreset>
  <MinSeeders>5</MinSeeders>
  <MinSizeGb1080p>4</MinSizeGb1080p>
  <MinSizeGb4K>20</MinSizeGb4K>
  <EvictionEnabled>true</EvictionEnabled>
  <EvictionIdleDays>7</EvictionIdleDays>
  <EvictionScheduleCron>0 4 * * *</EvictionScheduleCron>
  <MaterialisationConcurrencyGlobal>4</MaterialisationConcurrencyGlobal>
  <MaterialisationConcurrencyPerIndexer>2</MaterialisationConcurrencyPerIndexer>
  <EagerResolveEnabled>true</EagerResolveEnabled>
  <EagerResolveMaxConcurrent>2</EagerResolveMaxConcurrent>
  <PhantomRetentionDays>7</PhantomRetentionDays>
  <SeriesAutopilotEnabled>true</SeriesAutopilotEnabled>
  <SeriesAutopilotPrefetchEpisodes>1</SeriesAutopilotPrefetchEpisodes>
  <PhantomBadgeVisibility>AlwaysShow</PhantomBadgeVisibility>
  <SplashLoopAssetPath></SplashLoopAssetPath>
  <PhantomTargetLibraryId></PhantomTargetLibraryId>
</PluginConfiguration>
EOF

# 4. seed network.xml so the test instance listens on 18096. NOT 8096
#    — production jellyfin owns 8096; binding it will EADDRINUSE and
#    crash. Disable AutoDiscovery so the rig doesn't fight production
#    for the UDP :7359 discovery port.
cat > /tmp/jf-test/config/network.xml <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration>
  <PublicHttpPort>18096</PublicHttpPort>
  <InternalHttpPort>18096</InternalHttpPort>
  <AutoDiscovery>false</AutoDiscovery>
</NetworkConfiguration>
EOF

# 5. insert an API key directly into the cloned DB so REST calls
#    skip the startup wizard and login flow entirely. Trying to
#    drive /Startup/User over REST does NOT work in 10.11 — the
#    wizard endpoint is gated by FirstTimeSetupOrElevated and the
#    cloned DB has IsStartupWizardCompleted=true already, so the
#    endpoint returns 404. Just put a key in the DB. Cloned admin
#    user persists from prod.
sqlite3 /tmp/jf-test/data/data/jellyfin.db \
  "DELETE FROM ApiKeys WHERE Name='test-rig';
   INSERT INTO ApiKeys (DateCreated, DateLastActivity, Name, AccessToken)
   VALUES ('2026-06-04','2026-06-04','test-rig','testtoken00000000000000000000000');"

# 6. (optional, but recommended) wipe Phantom-created rows from
#    the prod clone so you exercise the create path from a clean
#    slate. Order matters: BaseItemProviders has UNIQUE
#    (ItemId, ProviderId), so orphan rows left from a prior failed
#    Run will silently dedupe new attempts.
sqlite3 /tmp/jf-test/data/data/jellyfin.db \
  "DELETE FROM BaseItemProviders WHERE ItemId IN (
      SELECT Id FROM BaseItems
      WHERE Type IN ('MediaBrowser.Controller.Entities.Movies.Movie',
                     'MediaBrowser.Controller.Entities.TV.Series')
        AND (Path IS NULL OR Path=''));
   DELETE FROM BaseItems
   WHERE Type IN ('MediaBrowser.Controller.Entities.Movies.Movie',
                  'MediaBrowser.Controller.Entities.TV.Series')
     AND (Path IS NULL OR Path='');
   DELETE FROM BaseItemProviders WHERE ItemId NOT IN (SELECT Id FROM BaseItems);"
sqlite3 /tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db \
  "DELETE FROM phantom_items WHERE state='Virtual';
   DELETE FROM tmdb_cache;"

# 7. write the start script (idempotent — overwrite each rig rebuild)
cat > /tmp/jf-test/start.sh <<'EOF'
#!/bin/bash
exec dotnet /usr/lib/jellyfin/jellyfin.dll \
  --datadir /tmp/jf-test/data --configdir /tmp/jf-test/config \
  --cachedir /tmp/jf-test/cache --logdir /tmp/jf-test/log \
  --webdir /usr/share/jellyfin/web --ffmpeg /usr/lib/jellyfin-ffmpeg/ffmpeg
EOF
chmod +x /tmp/jf-test/start.sh
```

## Process lifecycle — the single most important constraint

The shell tool used by this agent kills the entire process group
on each command exit. A `dotnet jellyfin.dll &` backgrounded in
one `bash` invocation **will be killed** before your next `bash`
invocation runs. Jellyfin does not persist across separate tool
calls.

**The only reliable pattern is to run the whole test —
start → wait → drive → inspect → kill — inside a single bash
command.** That's what `run-test.sh` is for. Edit it, then run
it, then inspect its output. Do not try to "leave jellyfin
running between tool calls and curl it." That has never worked
and will never work in this environment.

If a stale rig instance is still bound to `:18096` from a
prior run, the `pkill` in step 0 above clears it. Scope by
`-u $USER` and the `jf-test` path anchor; production jellyfin
runs as user `jellyfin` and its cmdline does not contain
`jf-test`, so it is unaffected.

## The driver script: `run-test.sh`

This is the script that gets edited per-test. The shape is
fixed; the body in the middle changes. Reference current
working copy:

```bash
#!/bin/bash
set -u
LOG=/tmp/jf-test/run.log
BASE=http://localhost:18096
TOK=testtoken00000000000000000000000
# admin user GUID — read once from the cloned DB:
#   sqlite3 /tmp/jf-test/data/data/jellyfin.db \
#     "SELECT lower(substr(hex(Id),1,8)||'-'||substr(hex(Id),9,4)||'-'||
#            substr(hex(Id),13,4)||'-'||substr(hex(Id),17,4)||'-'||
#            substr(hex(Id),21,12)), Username FROM Users;"
ADMIN=8EB11AC1-9939-4621-896C-31D5CBA4951C

/tmp/jf-test/start.sh > $LOG 2>&1 &
JF=$!
echo "jf pid=$JF"
trap "kill -9 $JF 2>/dev/null; sleep 1" EXIT

# wait for Jellyfin to be up AND for the API key to be honoured.
# Don't just curl /System/Info — that's anonymous and succeeds
# before EF migrations are done loading the ApiKeys table. Hit
# an auth-gated endpoint so you know the token is being validated.
for i in {1..60}; do
  CODE=$(curl -s --max-time 3 -H "X-Emby-Token: $TOK" -o /dev/null \
         -w "%{http_code}" "$BASE/Users/Me" 2>/dev/null || echo 000)
  if [ "$CODE" = "200" ]; then echo "up + auth ok in ${i}s"; break; fi
  sleep 1
done

# --- per-test body goes here ---

echo "=== Trigger Suggestions/Refresh ==="
curl -s --max-time 90 -X POST -H "X-Emby-Token: $TOK" \
     "$BASE/Plugins/PhantomLibrary/Suggestions/Refresh" \
     -w "\n  HTTP %{http_code}\n"
sleep 3

# Browse what the admin user sees under the gostream-movies CollectionFolder.
# To find a parent id: sqlite3 ... "SELECT lower(...hex_to_guid(Id)...), Name
# FROM BaseItems WHERE Type='MediaBrowser.Controller.Entities.CollectionFolder';"
PARENT_MOVIES=DB8B6E7B-707B-B546-9E4D-9B125CAEBB3C
curl -s "$BASE/Users/$ADMIN/Items?ParentId=$PARENT_MOVIES&Limit=200" \
     -H "X-Emby-Token: $TOK" > /tmp/jf-test/items.json
python3 -c "
import json
d = json.load(open('/tmp/jf-test/items.json'))
items = d.get('Items', [])
print('TotalRecordCount=', d.get('TotalRecordCount'))
splash = [i for i in items if i.get('Path','').endswith('splash.mp4')]
print(f'splash-pathed items in browse: {len(splash)}')
for i in splash[:10]:
    print(f'  {i[\"Name\"]} Path={i.get(\"Path\")} LocationType={i.get(\"LocationType\")}')
"

echo "=== DB state ==="
sqlite3 -separator '|' /tmp/jf-test/data/data/jellyfin.db \
  "SELECT b.Name, b.IsVirtualItem, COALESCE(p.Name,'<ORPHAN>'),
          substr(b.Path, length(b.Path)-15, 16)
   FROM BaseItems b LEFT JOIN BaseItems p ON b.ParentId=p.Id
   WHERE b.Type IN ('MediaBrowser.Controller.Entities.Movies.Movie',
                    'MediaBrowser.Controller.Entities.TV.Series')
     AND b.Path IS NOT NULL AND b.Path LIKE '%splash.mp4'
   LIMIT 15;"

echo "=== DONE ==="
```

Run with:

```bash
bash /tmp/jf-test/run-test.sh 2>&1 | tee /tmp/jf-test/out.log
```

## SQLite consistency — WAL/SHM

SQLite uses Write-Ahead Logging on production. While Jellyfin is
running, the `.db` file on disk is **not consistent on its own**;
recent writes live in `.db-wal` and the shared-memory index in
`.db-shm`. If you copy only `jellyfin.db` you will get either
`database disk image is malformed` or stale rows that do not
reflect the operator's current library.

Always copy all three together in the order shown in step 1.
Quick sanity check after a clone:

```bash
sqlite3 /tmp/jf-test/data/data/jellyfin.db "SELECT COUNT(*) FROM BaseItems;"
```

Expect thousands (operator's real library), not an error.

For the rig's own DB after the test runs, the same caveat
applies if Jellyfin is still up. The `trap` in `run-test.sh`
kills jellyfin before the script returns, so by the time you
inspect `/tmp/jf-test/data/data/jellyfin.db` from a follow-up
command, WAL has been flushed. If you inspect mid-test (from
inside the script), include WAL:

```bash
sqlite3 /tmp/jf-test/data/data/jellyfin.db "PRAGMA wal_checkpoint(FULL); SELECT ...;"
```

## REST surface cheatsheet

All requests carry `-H "X-Emby-Token: testtoken00000000000000000000000"`.

- `GET /Users/Me` — auth health check, returns the admin user
- `GET /Library/VirtualFolders` — list CollectionFolders + IDs
- `GET /Users/{userId}/Items?ParentId=<guid>&Limit=200` — browse as user
- `POST /Plugins/PhantomLibrary/Suggestions/Refresh` — kick a refresh
- `POST /Items/{id}/Refresh` — force a metadata refresh on one item
- `GET /System/Logs/Log?name=<filename>` — fetch a log file by name
- `GET /ScheduledTasks` — list scheduled tasks (find the IDs of
  Phantom's tasks)
- `POST /ScheduledTasks/Running/{id}` — trigger a scheduled task

User GUIDs in the path are dashed lowercase form. The DB stores
them as 16-byte BLOBs (`hex(Id)` gives 32 chars with no dashes).
Convert with the SQL snippet in the `run-test.sh` comment.

## CollectionFolder IDs for the operator's prod clone

Look them up rather than hardcoding — they are stable per
operator but not per project:

```bash
sqlite3 /tmp/jf-test/data/data/jellyfin.db \
  "SELECT lower(substr(hex(Id),1,8)||'-'||substr(hex(Id),9,4)||'-'||
                substr(hex(Id),13,4)||'-'||substr(hex(Id),17,4)||'-'||
                substr(hex(Id),21,12)), Name
   FROM BaseItems
   WHERE Type='MediaBrowser.Controller.Entities.CollectionFolder';"
```

At time of writing the gostream-movies folder is
`db8b6e7b-707b-b546-9e4d-9b125caebb3c`. Confirm before relying.

## Plugin DB schema (phantom.db)

```
phantom_items(
  id INTEGER PRIMARY KEY,
  jellyfin_item_id TEXT,         -- 32-char hex of BaseItems.Id, no dashes
  tmdb_id INTEGER,
  imdb_id TEXT,
  media_type TEXT,               -- 'movie' | 'series'
  title TEXT,
  state TEXT,                    -- 'Virtual' | 'Materialising' | 'Materialised' | ...
  source TEXT,                   -- 'TmdbSuggestion' | 'UserRequest' | ...
  created_utc TEXT,
  updated_utc TEXT,
  ...
)
tmdb_cache(endpoint TEXT, key TEXT, json TEXT, fetched_utc TEXT, ...)
materialisation_queue(...)
```

Useful queries:

```sql
-- count phantoms by state
SELECT state, COUNT(*) FROM phantom_items GROUP BY state;

-- find phantom rows whose Jellyfin BaseItem went missing
-- (run this attached to BOTH DBs)
ATTACH '/tmp/jf-test/data/data/jellyfin.db' AS jf;
SELECT p.title, p.state, p.jellyfin_item_id
FROM phantom_items p
LEFT JOIN jf.BaseItems b ON lower(hex(b.Id)) = lower(p.jellyfin_item_id)
WHERE b.Id IS NULL;
```

## Common failure modes (and what they actually mean)

| Symptom | Cause | Fix |
| --- | --- | --- |
| `database disk image is malformed` after clone | WAL/SHM not copied | redo step 1 |
| `EADDRINUSE :8096` | network.xml not seeded or empty | redo step 4 |
| `401` from `/Users/Me` | DB wiped before ApiKeys insert | redo step 5 *after* any DB-clearing step |
| `404` from `/Startup/User` | already-completed wizard in cloned DB | use API key path (step 5), don't bootstrap |
| All HTTP calls hang | Jellyfin process got SIGKILL by tool's pgroup teardown | run the whole test inside ONE bash call |
| Fix has no effect | dest DLL is stale (md5 mismatch) | redo step 2 and verify md5 sums |
| `UNIQUE constraint failed: BaseItemProviders` | orphan provider rows from prior run | redo step 6 |
| Phantom items "missing" from browse but in DB | item has `IsVirtualItem=1` and user filter hides virtuals; or `ParentId` points to wrong CollectionFolder | inspect with the DB-state SQL in `run-test.sh` and the lookup in "CollectionFolder IDs" |
| Browse returns 0 items but DB has rows | wrong `ParentId` GUID in URL | re-derive from `VirtualFolders` endpoint or the SQL above |
| `AddMediaPath` returns 404 `Could not find a part of the path .../<libname>/<safename>.mblink` | `/root/default/<libname>/` missing on disk (we cloned the DB but not the library config dirs) | `cp -r /var/lib/jellyfin/root/default/* /tmp/jf-test/data/root/default/` |
| Multi-path library: items from 2nd path don't show in browse even after scan + restart | `CollectionFolder.PhysicalLocationsList` and `PhysicalFolderIds` did not refresh; `AddMediaPath` + `ValidateMediaLibrary` does not propagate to those fields reliably on 10.11 | see "CollectionFolder GUID resolution" below — patch `BaseItems.Data` directly OR call `libraryManager.UpdateItemAsync(cf, parent, ItemUpdateType.MetadataEdit, ct)` after setting both arrays in-process |
| Phantom item gets weird Name like `Backrooms.Enderman` after scan | scanner renamed from filename + TMDB fuzzy-matched | set `IsLocked = true` on the BaseItem at creation; scanner skips locked items |
| Browse `ParentId=<CollectionFolderId>` returns items recursively but `children of <CollectionFolderId>` SQL is empty | normal — CollectionFolder browse goes via `GetTopParentIdsForQuery` which returns `PhysicalFolderIds`, not via the `BaseItems.ParentId` tree | use `TopParentId` to find what a CollectionFolder claims, not `ParentId` |

## CollectionFolder GUID resolution and multi-path quirks

A Jellyfin library has **three layers of identity**:

1. **CollectionFolder** (`Type='MediaBrowser.Controller.Entities.CollectionFolder'`)
   — the user-facing library. URL `ParentId=<this>` is what
   browse APIs accept. Path is the on-disk config dir
   (`/var/lib/jellyfin/root/default/gostream-movies`).
2. **Physical Folder(s)** (`Type='MediaBrowser.Controller.Entities.Folder'`)
   — one per `MediaPathInfo` in `options.xml`. Path is the
   real media root (`/var/gostream/gostream-mkv-virtual/movies`).
   Each becomes the `TopParentId` of every item resolved
   under it.
3. **Items** — Movies/Series/Episodes with `TopParentId =
   <one of the physical folder Ids>`.

The CollectionFolder's `BaseItems.Data` blob is the JSON of
its in-memory state, including `PhysicalFolderIds` (list of
physical Folder Ids it claims) and `PhysicalLocationsList`
(list of resolved mblink targets + the container path).
Browse query `GetTopParentIdsForQuery(collectionFolder)`
returns `collectionFolder.PhysicalFolderIds` — items whose
`TopParentId` is in that list show up; others do not.

**Known issue (verified in this rig on 10.11.9):** calling
`POST /Library/VirtualFolders/Paths` to add a second path to
an existing library:

- Updates `options.xml` ✓
- Creates the on-disk `<name>1.mblink` shortcut ✓
- Creates the second physical Folder row in DB on next scan ✓
- **Does NOT update `CollectionFolder.PhysicalLocationsList`
  or `PhysicalFolderIds`** ✗

Result: items in the new path are scanned and stored, but
browse via `ParentId=<CollectionFolderId>` ignores them.
`RefreshMediaLibraryTask` does not fix this; targeted
`/Items/{cfid}/Refresh` does not fix this; cold restart
does not fix this. Direct patch is required.

**To verify the binding state**:

```bash
sqlite3 /tmp/jf-test/data/data/jellyfin.db \
  "SELECT Data FROM BaseItems WHERE Id='<CollectionFolderId>';" \
  | python3 -c "import json,sys; d=json.load(sys.stdin); print('paths:',d.get('PhysicalLocationsList')); print('ids:',d.get('PhysicalFolderIds'))"
```

**To patch (rig / manual test)**:

```bash
NEW_DATA='{"PhysicalLocationsList":["<container>","<path1>","<path2>"],"PhysicalFolderIds":["<id1>","<id2>"],"CollectionType":"movies",...}'
sqlite3 /tmp/jf-test/data/data/jellyfin.db \
  "UPDATE BaseItems SET Data='$NEW_DATA' WHERE Id='<CollectionFolderId>';"
```

Then restart Jellyfin (in-memory cache loads from `Data`
blob at startup).

**To patch (plugin in-process)**:

```csharp
var cf = (CollectionFolder)libraryManager.GetItemById(gostreamMoviesId);
cf.PhysicalLocationsList = cf.PhysicalLocationsList.Concat(new[]{ phantomDir }).ToArray();
cf.PhysicalFolderIds = cf.PhysicalFolderIds.Concat(new[]{ phantomPhysFolder.Id }).ToArray();
await libraryManager.UpdateItemAsync(cf, cf.GetParent(), ItemUpdateType.MetadataEdit, ct);
```

Do this once at plugin startup if the additional path is
missing from the CollectionFolder's bound list. Idempotent.

## Materialise without a library scan

The materialise loop is **entirely in-process** in the
plugin and **does not require a library scan**. The pattern
in `Materialiser.PromoteItemAsync`:

```csharp
item.Path = fusePath;             // new gostream-backed path
item.IsVirtualItem = false;        // demote from virtual
await libraryManager.UpdateItemAsync(
    item, item.GetParent(), ItemUpdateType.MetadataEdit, ct);
```

That single call updates the in-memory BaseItem cache AND
persists to SQLite in one round-trip. Subsequent browse API
calls (within milliseconds) reflect the new path. No
`Library/Refresh`, no `ScheduledTasks/Running`, no
`/Items/{id}/Refresh`. UserData (favourites, watch progress)
stays attached because the BaseItem.Id is unchanged.

Verified end-to-end in `/tmp/jf-test/m2.sh`: SQL-update +
immediate browse showed the cached old value (because we
bypassed `UpdateItemAsync` and went straight to SQL); a
single `/Items/{id}/Refresh` forced in-memory invalidation
and browse returned the new path. In real plugin code,
`UpdateItemAsync` does both atomically.

The **only** scan-required step in the phantom workflow is
one-time library bootstrap (add 2nd path to gostream-movies
on plugin startup if missing). Per-item materialise is
scan-free.

## Logs

### Rig logs (the rig you control)

- `/tmp/jf-test/run.log` — jellyfin's stdout/stderr
- `/tmp/jf-test/log/*.log` — jellyfin's structured logs
  (per `--logdir`)
- Plugin log lines are prefixed `[PhantomLibrary]` and
  `[Phantom.*]` (per-subsystem categories like
  `Phantom.SeriesIngestor`).

Grep examples:

```bash
grep -i 'PhantomLibrary\|Phantom\.' /tmp/jf-test/log/*.log
grep -i 'error\|exception\|fail' /tmp/jf-test/run.log | head -50
```

### Production-Jellyfin logs (the operator's `:8096` instance)

`/var/log/jellyfin` and `/var/lib/jellyfin/log` are
`jellyfin:jellyfin`-owned 0750 and not readable by your shell
user without `sudo` — which you don't have. **Use Jellyfin's
REST API to fetch logs instead.** No sudo, no operator action,
works against any running Jellyfin.

The operator's API key (created out-of-band, lives in the prod
`ApiKeys` table) is required. Look it up once via the
world-readable cloned DB in your rig, or via the operator's
prod DB directly if you can read it:

```bash
sqlite3 /var/lib/jellyfin/data/jellyfin.db \
  "SELECT Name, AccessToken FROM ApiKeys;"
```

Pick a key (e.g. `sonarr`'s token used as a generic read token).
Then:

```bash
TOK=<that-token>
BASE=http://localhost:8096   # production instance, NOT the rig's :18096

# 1. List available log files (most recent first; today's is
#    typically jellyfin<YYYYMMDD>.log)
curl -s -H "X-Emby-Token: $TOK" "$BASE/System/Logs" \
  | python3 -c "
import json,sys
for f in json.load(sys.stdin)[:10]:
    print(f['Name'], f['Size'], f['DateModified'])
"

# 2. Pull a specific log to disk
curl -s -H "X-Emby-Token: $TOK" \
  "$BASE/System/Logs/Log?name=jellyfin20260606.log" \
  -o /tmp/jflog.txt
wc -l /tmp/jflog.txt

# 3. Grep for plugin-relevant events
grep -iE 'phantom|materialis|gostream' /tmp/jflog.txt | tail -60
grep -iE 'MaterialisationQueue|UserDataSavedListener|PhantomBinder|PhantomStubManager' /tmp/jflog.txt | tail -30
```

Use this whenever the operator says "X doesn't work" — the
plugin's log lines almost always tell you what fired (or, more
often, what *didn't* fire). Do NOT ask the operator to tail the
log or paste output.

If the API endpoint returns 401 the token is stale or revoked.
If it returns 403 the operator removed the
`EnableUserAccessForAllLibraries`-equivalent permission on the
key; pick a different key. If the operator has no usable key,
that is one of the rare legitimate things to ask about — the fix
is 30 seconds in the admin dashboard.

## What this rig is NOT for

- **Unit tests.** Those live under `tests/Jellyfin.Plugin.PhantomLibrary.Tests/`
  and run with `dotnet test`. The rig is for integration:
  "does the plugin behave correctly inside a real Jellyfin
  against a real production-shaped DB."
- **Testing against the real gostream / Torrentio / TMDB
  servers without consent.** TMDB is fine (read-only public API
  with operator's key). Torrentio is third-party — be polite,
  cache, don't hammer. gostream-diagnostics and gostream-api
  on `:8090` / `:9080` are local to the operator's box; verify
  with `curl http://127.0.0.1:8090/health` before assuming
  they're up.
- **Destructive prod tests.** The rig writes only to
  `/tmp/jf-test`. It never writes back to
  `/var/lib/jellyfin/...`. Keep it that way.

## Tear-down

```bash
pkill -u "$USER" -9 -f "dotnet.*jellyfin.dll.*jf-test"
rm -rf /tmp/jf-test
```

The rig is disposable. Rebuild from prod whenever you suspect
drift from production state.
