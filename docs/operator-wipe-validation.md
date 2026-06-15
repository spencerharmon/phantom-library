# Operator-wipe validation (Stage 7.4)

This document is the operator-runnable proof that
`scripts/phantom-wipe.sh` behaves correctly on the operator's
actual data shape. Per `AGENTS.md` \u00a7 "Production database
safety", every destructive script must be exercised end-to-end
against a clone of operator state before being handed to the
operator. This file is the record of that exercise for the
v0.3.0 channel-arch wipe.

## Source data

`/tmp/operator-snapshot/{jellyfin.db, phantom.db}` \u2014 a copy of the
operator's production DBs captured during Phase 0 of the
channel-handoff plan. These are the operator's real,
pre-v0.3.0-upgrade data shapes:

- `jellyfin.db` \u2014 245,581 BaseItems total.
- `phantom.db` \u2014 schema v5 (the v0.2.0.0 schema), 49,407 rows in
  `phantom_items`.

## Sandbox setup

The wipe script's on-disk cleanup phase removes directories. To
exercise that phase safely without touching the operator's real
`/var/lib/jellyfin/...` tree, the sandbox rewrites prod paths in
the cloned `jellyfin.db` to sandbox paths under
`/tmp/operator-sandbox/`. This is the bash recipe used:

```bash
rm -rf /tmp/operator-sandbox
mkdir -p /tmp/operator-sandbox
cp /tmp/operator-snapshot/jellyfin.db /tmp/operator-sandbox/
cp /tmp/operator-snapshot/phantom.db /tmp/operator-sandbox/
mkdir -p /tmp/operator-sandbox/stubs/movies /tmp/operator-sandbox/stubs/shows
mkdir -p /tmp/operator-sandbox/jfroot/gostream-movies \
         /tmp/operator-sandbox/jfroot/gostream-shows
mkdir -p /tmp/operator-sandbox/gostream
touch /tmp/operator-sandbox/gostream/sentinel-untouched.mkv

# Rewrite prod paths in BaseItems to sandbox roots so on-disk
# cleanup runs against /tmp/operator-sandbox, not /var/lib/jellyfin.
sqlite3 /tmp/operator-sandbox/jellyfin.db <<'SQL'
BEGIN;
UPDATE BaseItems
   SET Path = '/tmp/operator-sandbox/stubs/' ||
              substr(Path, length('/var/lib/jellyfin/phantom-library/')+1)
 WHERE Path LIKE '/var/lib/jellyfin/phantom-library/%';
UPDATE BaseItems
   SET Path = '/tmp/operator-sandbox/jfroot/' ||
              substr(Path, length('/var/lib/jellyfin/root/default/')+1)
 WHERE Path LIKE '/var/lib/jellyfin/root/default/gostream-%';
UPDATE BaseItems
   SET Path = '/tmp/operator-sandbox/gostream/' ||
              substr(Path, length('/var/gostream/')+1)
 WHERE Path LIKE '/var/gostream/%';
COMMIT;
SQL
```

## Wipe invocation

```bash
JELLYFIN_DB=/tmp/operator-sandbox/jellyfin.db \
PHANTOM_DB=/tmp/operator-sandbox/phantom.db \
STUB_ROOT=/tmp/operator-sandbox/stubs \
JF_ROOT_DEFAULT=/tmp/operator-sandbox/jfroot \
GOSTREAM_ROOT=/tmp/operator-sandbox/gostream \
bash -c 'echo WIPE | bash scripts/phantom-wipe.sh \
                          --commit --skip-service-check'
```

`--skip-service-check` is used because there is no `jellyfin.service`
to check in the sandbox. **DO NOT** use `--skip-service-check` in
prod; the script's default pre-flight refusal-while-jellyfin-is-up
is a safety property the operator wants.

## Pre-wipe / post-wipe counts

Pre-wipe and post-wipe row counts on the sandbox `jellyfin.db`:

| table              | pre-wipe | post-wipe | delta    | notes |
|--------------------|---------:|----------:|---------:|-------|
| BaseItems total    |  245,581 |   195,733 |  -49,848 | matches sum of three phantom-target buckets exactly |
| stub-root          |   48,521 |         0 |  -48,521 | legacy phantom-library tree |
| gostream CFs       |        2 |         0 |       -2 | two CollectionFolders dropped |
| gostream content   |    1,325 |         0 |   -1,325 | scanner-derived BaseItems for /var/gostream/% |
| UserData           |      162 |       129 |      -33 | favourites/watched on dropped items |
| BaseItemProviders  |  270,872 |   181,890 |  -88,982 | provider rows for dropped items |
| AncestorIds        |  187,527 |       686 | -186,841 | phantom items had many ancestors (TopParent chains) |
| MediaStreamInfos   |   72,497 |     1,443 |  -71,054 | most streams belonged to phantoms |
| PeopleBaseItemMap  |  640,782 |     2,260 | -638,522 | cast/crew rows for dropped items |
| BaseItemImageInfos |  210,522 |   120,774 |  -89,748 | per-item poster/backdrop refs |
| Chapters           |    4,132 |       333 |   -3,799 | |
| ItemValuesMap      |  320,863 |       609 | -320,254 | tag/genre rows for dropped items |

`phantom.db` is renamed aside (`phantom.db.wiped.<UTC-ts>`) along
with its `-wal` / `-shm` sidecars. The plugin recreates an empty
schema-v9 `phantom.db` on next start via `PhantomDb.EnsureSchema`.

## Verification queries (reproducible)

```bash
sqlite3 "file:/tmp/operator-sandbox/jellyfin.db?mode=ro" <<'SQL'
SELECT 'BaseItems total', COUNT(*) FROM BaseItems
UNION ALL SELECT 'stub-root',
  COUNT(*) FROM BaseItems WHERE Path LIKE '/tmp/operator-sandbox/stubs/%'
UNION ALL SELECT 'gostream CFs',
  COUNT(*) FROM BaseItems WHERE Path LIKE '/tmp/operator-sandbox/jfroot/gostream-%'
UNION ALL SELECT 'gostream content',
  COUNT(*) FROM BaseItems WHERE Path LIKE '/tmp/operator-sandbox/gostream/%'
UNION ALL SELECT 'UserData', COUNT(*) FROM UserData
UNION ALL SELECT 'BaseItemProviders', COUNT(*) FROM BaseItemProviders
UNION ALL SELECT 'AncestorIds', COUNT(*) FROM AncestorIds
UNION ALL SELECT 'MediaStreamInfos', COUNT(*) FROM MediaStreamInfos
UNION ALL SELECT 'PeopleBaseItemMap', COUNT(*) FROM PeopleBaseItemMap
UNION ALL SELECT 'BaseItemImageInfos', COUNT(*) FROM BaseItemImageInfos
UNION ALL SELECT 'Chapters', COUNT(*) FROM Chapters
UNION ALL SELECT 'ItemValuesMap', COUNT(*) FROM ItemValuesMap;
SQL
```

## Sanity checks

- **`PRAGMA integrity_check`**: `ok` (pre-wipe and post-wipe).
- **`PRAGMA foreign_key_check`**: 6 violations pre-wipe \u2192 3
  post-wipe. The 3 remaining are **pre-existing orphans** in the
  operator's snapshot \u2014 unrelated `AncestorIds` rows whose
  `ItemId` is missing from `BaseItems`. They date from before the
  wipe and are not introduced by it. The wipe in fact *reduced*
  the orphan count by half because the missing-ItemId orphans
  whose ItemIds matched phantom items were cascade-deleted along
  with the phantoms.
- **50% sanity cap**: 49,848 / 245,581 = **20.3%** of total
  BaseItems. Well under the script's hard refusal threshold of
  50%.
- **Real gostream files preserved**: `sentinel-untouched.mkv`
  under `/tmp/operator-sandbox/gostream/` is still present
  post-wipe. The script's on-disk cleanup phase removes the
  CollectionFolder marker dirs at `JF_ROOT_DEFAULT/gostream-{movies,shows}`
  but does **not** touch `GOSTREAM_ROOT` itself \u2014 those files
  belong to the gostream service, not Jellyfin.
- **Idempotency**: a second run with the same env-var overrides
  hits the script's empty-state pre-flight short-circuit and
  exits 0 with no writes (`==> Nothing to do`).

## Backups

The script writes three rollback artefacts per `--commit` run:

- `/tmp/operator-sandbox/jellyfin.db.bak.wipe.<UTC-ts>`
- `/tmp/operator-sandbox/phantom.db.bak.wipe.<UTC-ts>`
- `/tmp/operator-sandbox/phantom.db.wiped.<UTC-ts>` (the rename-
  aside of the original DB; `-wal` and `-shm` sidecars get the
  same `.wiped.<ts>` suffix).

In prod these land under `/var/lib/jellyfin/data/` and
`/var/lib/jellyfin/plugins/configurations/PhantomLibrary/`. The
operator should keep them until at least one normal usage cycle
on the new channel arch has been confirmed.

## Verdict

The wipe behaves correctly on the operator's actual data shape.
Deletion targets the right three path namespaces in the right
proportion. Cascade rows drop in proportion. Real (non-phantom,
non-gostream) library data is untouched. Real gostream files
on disk are untouched. DB integrity is preserved. Idempotency
holds.

**The script is safe to hand to the operator for Stage 7.5**, on
the explicit condition that:

1. The operator runs the dry-run first (default behaviour) and
   confirms the printed `phantom-target BaseItems (to delete)`
   count matches their expectations (something close to the
   numbers in the table above, give or take movement since the
   Phase-0 snapshot was taken).
2. The operator runs `--commit` only after dry-run confirms.
3. The operator does **NOT** pass `--skip-service-check` in prod.
4. The operator keeps the `.bak.wipe.*` backups until they've
   confirmed at least one normal usage cycle on the new arch.

These four conditions are also documented in the script's own
help output (`bash scripts/phantom-wipe.sh --help`) and in the
v0.3.0 CHANGELOG entry under the operator-steps list.
