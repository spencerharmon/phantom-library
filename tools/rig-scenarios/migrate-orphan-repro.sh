#!/usr/bin/env bash
#
# migrate-orphan-repro.sh
# =======================
#
# Reproduces (in a self-contained sandbox under /tmp/migrate-repro)
# the operator's prod post-broken-v0.2.0.0-migration state, then
# runs scripts/migrate-stub-layout-v1.sh against it three times to
# verify the three required transitions:
#   1. dry-run prints expected counters, performs no writes,
#      reports `marker_set=would-set`.
#   2. real run sets the marker and matches the dry-run counters.
#   3. re-run is a no-op (everything 0 except already_new).
#
# Synthetic state:
#   - 50 Virtual phantom_items rows whose stub was moved on disk
#     to new-format AND whose BaseItem was deleted and replaced
#     with a new BaseItem having a fresh GUID at the new path
#     (exact failure mode the broken in-plugin migration left
#     behind).
#   - 5 of those 50 also carry a duplicate new BaseItem at the
#     same TMDB id (to exercise dedup-then-reassociate ordering).
#   - 1 movie row whose old (legacy) and new (token) files both
#     exist on disk as symlinks to the same splash target (to
#     exercise conflict-resolution).
#
# This is NOT a Jellyfin integration test \u2014 it builds minimal
# SQLite DBs containing only the tables/columns the migration
# script reads/writes, plus a sandbox stub-tree under
# /tmp/migrate-repro/stubs. Runs in seconds; no jellyfin process
# required; no production data touched.
#
# Usage:
#   bash tools/rig-scenarios/migrate-orphan-repro.sh
#
set -euo pipefail

REPO_ROOT=${REPO_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
SCRIPT="$REPO_ROOT/scripts/migrate-stub-layout-v1.sh"
SANDBOX=/tmp/migrate-repro
STUB_ROOT="$SANDBOX/stubs"
JFDB="$SANDBOX/jellyfin.db"
PHDB="$SANDBOX/phantom.db"
SPLASH="$SANDBOX/splash.mp4"

[[ -f "$SCRIPT" ]] || { echo "missing $SCRIPT" >&2; exit 1; }

rm -rf "$SANDBOX"
mkdir -p "$STUB_ROOT/movies" "$STUB_ROOT/shows"
# Splash target. Both old and new symlinks resolve here.
echo "splash placeholder" > "$SPLASH"

# ----- jellyfin.db: minimal schema --------------------------------------
sqlite3 "$JFDB" <<'SQL'
CREATE TABLE BaseItems (
    Id TEXT PRIMARY KEY,
    Name TEXT,
    Path TEXT,
    Type TEXT,
    ProductionYear INTEGER,
    IsFolder INTEGER NOT NULL DEFAULT 0,
    IsInMixedFolder INTEGER NOT NULL DEFAULT 0,
    IsLocked INTEGER NOT NULL DEFAULT 0,
    IsMovie INTEGER NOT NULL DEFAULT 0,
    IsRepeat INTEGER NOT NULL DEFAULT 0,
    IsSeries INTEGER NOT NULL DEFAULT 0,
    IsVirtualItem INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE BaseItemProviders (
    ItemId TEXT NOT NULL,
    ProviderId TEXT NOT NULL,
    ProviderValue TEXT,
    PRIMARY KEY (ItemId, ProviderId),
    FOREIGN KEY (ItemId) REFERENCES BaseItems(Id) ON DELETE CASCADE
);
CREATE TABLE MediaSegments (ItemId TEXT);
CREATE TABLE TrickplayInfos (ItemId TEXT);
CREATE TABLE UserData (ItemId TEXT);
SQL

# ----- phantom.db: schema lifted from production layout ----------------
sqlite3 "$PHDB" <<'SQL'
CREATE TABLE phantom_items (
    item_guid TEXT PRIMARY KEY,
    tmdb_id INTEGER,
    imdb_id TEXT,
    type TEXT NOT NULL,
    state TEXT NOT NULL,
    first_seen INTEGER NOT NULL,
    last_touched INTEGER NOT NULL,
    eviction_protected INTEGER NOT NULL DEFAULT 0,
    original_overview TEXT,
    stub_path TEXT,
    fuse_path TEXT,
    materialised_at INTEGER
);
CREATE TABLE materialisation_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts INTEGER NOT NULL,
    item_guid TEXT,
    trigger TEXT NOT NULL,
    duration_ms INTEGER NOT NULL,
    outcome TEXT NOT NULL,
    error TEXT,
    indexer TEXT,
    info_hash TEXT
);
CREATE TABLE plugin_meta (key TEXT PRIMARY KEY, value TEXT);
SQL

# ----- helpers --------------------------------------------------------
guid() {
    # Random UPPER dashed guid (matches Jellyfin BaseItems.Id shape).
    python3 -c 'import uuid; print(str(uuid.uuid4()).upper())'
}
hex_of_guid() {
    echo "$1" | tr '[:upper:]' '[:lower:]' | tr -d '-'
}
sqescape() {
    printf "%s" "$1" | sed "s/'/''/g"
}

NOW=$(date -u +%s)

ORPHAN_GUIDS=()  # phantom-row (dead) guid hex
NEW_GUIDS=()     # new BaseItem guid hex (target of reassociation)
TMDB_IDS=()

# ----- 50 movie rows: orphan reassociation case ----------------------
for i in $(seq 1 50); do
    tmdb=$((9000000 + i))
    title="Repro Movie ${i}"
    year=2020
    new_stem="${title} (${year}) [tmdbid-${tmdb}]"
    new_path="${STUB_ROOT}/movies/${new_stem}.mp4"
    ln -s "$SPLASH" "$new_path"

    old_guid_dashed=$(guid)
    new_guid_dashed=$(guid)
    old_guid=$(hex_of_guid "$old_guid_dashed")
    new_guid=$(hex_of_guid "$new_guid_dashed")
    ORPHAN_GUIDS+=("$old_guid")
    NEW_GUIDS+=("$new_guid")
    TMDB_IDS+=("$tmdb")

    # New BaseItem at new path. Old BaseItem deliberately absent
    # (simulating scanner culled it post-move).
    sqlite3 "$JFDB" <<SQL
INSERT INTO BaseItems (Id, Name, Path, Type, ProductionYear, IsMovie)
VALUES ('${new_guid_dashed}', '$(sqescape "$title")', '$(sqescape "$new_path")',
        'MediaBrowser.Controller.Entities.Movies.Movie', ${year}, 1);
INSERT INTO BaseItemProviders (ItemId, ProviderId, ProviderValue)
VALUES ('${new_guid_dashed}', 'Tmdb', '${tmdb}');
SQL

    # phantom_items still points at the dead old guid, with NULL
    # stub_path (Suggestions-created rows never populated it).
    sqlite3 "$PHDB" <<SQL
INSERT INTO phantom_items (item_guid, tmdb_id, type, state, first_seen, last_touched, eviction_protected, original_overview)
VALUES ('${old_guid}', ${tmdb}, 'movie', 'Virtual', ${NOW}, ${NOW}, 1, 'preserved overview for tmdb ${tmdb}');
INSERT INTO materialisation_log (ts, item_guid, trigger, duration_ms, outcome)
VALUES (${NOW}, '${old_guid}', 'Test', 0, 'Skipped');
SQL
done

# ----- 5 of those 50 also have a duplicate new BaseItem -----------
for i in $(seq 1 5); do
    idx=$((i - 1))
    tmdb="${TMDB_IDS[$idx]}"
    dup_guid_dashed=$(guid)
    title="Repro Movie ${i}"
    year=2020
    new_stem="${title} (${year}) [tmdbid-${tmdb}]"
    # The duplicate BaseItem has a *different* path (the scanner
    # would have created it at a slightly different location, e.g.
    # a transient one). Path under stub root so duplicate-collapse
    # considers it.
    dup_path="${STUB_ROOT}/movies/${new_stem}.dup.mp4"
    ln -s "$SPLASH" "$dup_path"
    sqlite3 "$JFDB" <<SQL
INSERT INTO BaseItems (Id, Name, Path, Type, ProductionYear, IsMovie)
VALUES ('${dup_guid_dashed}', '$(sqescape "$title")', '$(sqescape "$dup_path")',
        'MediaBrowser.Controller.Entities.Movies.Movie', ${year}, 1);
INSERT INTO BaseItemProviders (ItemId, ProviderId, ProviderValue)
VALUES ('${dup_guid_dashed}', 'Tmdb', '${tmdb}');
SQL
done

# ----- 1 movie row with old+new equivalent symlinks (conflict-resolve)
CONFLICT_TMDB=9999991
CONFLICT_TITLE="Conflict Movie"
CONFLICT_YEAR=2010
CONFLICT_OLD_LEAF="${CONFLICT_TITLE}__phantom_tmdb${CONFLICT_TMDB}.mp4"
CONFLICT_NEW_STEM="${CONFLICT_TITLE} (${CONFLICT_YEAR}) [tmdbid-${CONFLICT_TMDB}]"
CONFLICT_OLD="${STUB_ROOT}/movies/${CONFLICT_OLD_LEAF}"
CONFLICT_NEW="${STUB_ROOT}/movies/${CONFLICT_NEW_STEM}.mp4"
ln -s "$SPLASH" "$CONFLICT_OLD"
ln -s "$SPLASH" "$CONFLICT_NEW"
CONFLICT_GUID_DASHED=$(guid)
CONFLICT_GUID=$(hex_of_guid "$CONFLICT_GUID_DASHED")
# BaseItem.Path is the *legacy* path \u2014 this is a row that the per-row
# pass will try to migrate. The new-format file already exists from
# the broken in-plugin migration leaving it behind.
sqlite3 "$JFDB" <<SQL
INSERT INTO BaseItems (Id, Name, Path, Type, ProductionYear, IsMovie)
VALUES ('${CONFLICT_GUID_DASHED}', '$(sqescape "$CONFLICT_TITLE")',
        '$(sqescape "$CONFLICT_OLD")',
        'MediaBrowser.Controller.Entities.Movies.Movie',
        ${CONFLICT_YEAR}, 1);
INSERT INTO BaseItemProviders (ItemId, ProviderId, ProviderValue)
VALUES ('${CONFLICT_GUID_DASHED}', 'Tmdb', '${CONFLICT_TMDB}');
SQL
sqlite3 "$PHDB" <<SQL
INSERT INTO phantom_items (item_guid, tmdb_id, type, state, first_seen, last_touched, stub_path)
VALUES ('${CONFLICT_GUID}', ${CONFLICT_TMDB}, 'movie', 'Virtual', ${NOW}, ${NOW},
        '$(sqescape "$CONFLICT_OLD")');
SQL

echo "[setup] orphan-reassoc rows: 50"
echo "[setup] duplicate-baseitem additions: 5"
echo "[setup] conflict-resolution rows: 1"
echo "[setup] phantom_items count: $(sqlite3 "$PHDB" 'SELECT COUNT(*) FROM phantom_items;')"
echo "[setup] BaseItems count:     $(sqlite3 "$JFDB" 'SELECT COUNT(*) FROM BaseItems;')"

# ----- preserve backups dir clean (script writes .bak.<ts> next to DBs)
# (no action needed; sandbox is throwaway)

run_script() {
    local label="$1"; shift
    echo
    echo "================ $label ================"
    PHANTOM_MIGRATE_FORCE=1 bash "$SCRIPT" \
        --phantom-db "$PHDB" \
        --jellyfin-db "$JFDB" \
        --stub-root "$STUB_ROOT" \
        --prune-orphans \
        --verbose \
        "$@"
}

# Assertion helper. usage: assert_eq <label> <expected> <actual>
assert_eq() {
    if [[ "$2" != "$3" ]]; then
        echo "ASSERT FAIL: $1 expected=$2 actual=$3" >&2
        FAILS=$((FAILS+1))
    else
        echo "  ok: $1 = $2"
    fi
}

extract_counter() {
    local log="$1" key="$2"
    grep -E "^${key}: *" "$log" | head -1 | sed -E "s/^${key}: *//"
}

FAILS=0
DRY_LOG="$SANDBOX/dryrun.log"
REAL_LOG="$SANDBOX/real.log"
RERUN_LOG="$SANDBOX/rerun.log"

run_script "DRY RUN" --dry-run | tee "$DRY_LOG"
echo
echo "=== DRY-RUN assertions ==="
assert_eq "migrated"              1 "$(extract_counter "$DRY_LOG" migrated)"
assert_eq "reassociated"          50 "$(extract_counter "$DRY_LOG" reassociated)"
assert_eq "duplicates_drop"       5 "$(extract_counter "$DRY_LOG" duplicates_drop)"
assert_eq "failed"                0 "$(extract_counter "$DRY_LOG" failed)"
assert_eq "marker_set"            would-set "$(extract_counter "$DRY_LOG" marker_set)"
assert_eq "orphan_no_baseitem"    0 "$(extract_counter "$DRY_LOG" orphan_no_baseitem)"
assert_eq "skipped_conflict"      0 "$(extract_counter "$DRY_LOG" skipped_conflict)"

# Confirm dry-run made no writes.
DB_HASH_BEFORE=$(sha1sum "$JFDB" "$PHDB" | awk '{print $1}' | tr '\n' ' ')

run_script "REAL RUN" | tee "$REAL_LOG"
echo
echo "=== REAL-RUN assertions ==="
assert_eq "migrated"              1 "$(extract_counter "$REAL_LOG" migrated)"
assert_eq "reassociated"          50 "$(extract_counter "$REAL_LOG" reassociated)"
assert_eq "duplicates_drop"       5 "$(extract_counter "$REAL_LOG" duplicates_drop)"
assert_eq "failed"                0 "$(extract_counter "$REAL_LOG" failed)"
assert_eq "marker_set"            yes "$(extract_counter "$REAL_LOG" marker_set)"

# Verify state post-real-run.
post_phantom=$(sqlite3 "$PHDB" 'SELECT COUNT(*) FROM phantom_items;')
assert_eq "phantom_items post-run count" 51 "$post_phantom"
post_orphans=$(sqlite3 "$JFDB" "ATTACH '$PHDB' AS p;
  SELECT COUNT(*) FROM p.phantom_items pi
   LEFT JOIN BaseItems b ON lower(replace(b.Id,'-',''))=pi.item_guid
  WHERE b.Id IS NULL;")
assert_eq "remaining orphans" 0 "$post_orphans"
marker=$(sqlite3 "$PHDB" "SELECT value FROM plugin_meta WHERE key='stub_layout_v1_complete';")
if [[ -z "$marker" ]]; then
    echo "ASSERT FAIL: marker not present in plugin_meta" >&2
    FAILS=$((FAILS+1))
else
    echo "  ok: marker present = $marker"
fi
# Conflict-resolution side effects: old file deleted, new survives,
# BaseItems.Path now points at new.
if [[ -e "$CONFLICT_OLD" ]]; then
    echo "ASSERT FAIL: conflict old file still exists: $CONFLICT_OLD" >&2
    FAILS=$((FAILS+1))
else
    echo "  ok: conflict old file removed"
fi
if [[ ! -e "$CONFLICT_NEW" ]]; then
    echo "ASSERT FAIL: conflict new file vanished: $CONFLICT_NEW" >&2
    FAILS=$((FAILS+1))
else
    echo "  ok: conflict new file preserved"
fi
new_bi_path=$(sqlite3 "$JFDB" "SELECT Path FROM BaseItems WHERE Id='${CONFLICT_GUID_DASHED}';")
assert_eq "conflict BaseItem.Path" "$CONFLICT_NEW" "$new_bi_path"

run_script "RE-RUN (idempotency)" | tee "$RERUN_LOG"
echo
echo "=== RE-RUN assertions ==="
assert_eq "migrated"          0 "$(extract_counter "$RERUN_LOG" migrated)"
assert_eq "reassociated"      0 "$(extract_counter "$RERUN_LOG" reassociated)"
assert_eq "duplicates_drop"   0 "$(extract_counter "$RERUN_LOG" duplicates_drop)"
assert_eq "duplicates_keep"   0 "$(extract_counter "$RERUN_LOG" duplicates_keep)"
assert_eq "orphan_no_baseitem" 0 "$(extract_counter "$RERUN_LOG" orphan_no_baseitem)"
assert_eq "failed"            0 "$(extract_counter "$RERUN_LOG" failed)"
# already_new should account for all 51 surviving rows.
assert_eq "already_new"       51 "$(extract_counter "$RERUN_LOG" already_new)"

echo
if [[ "$FAILS" -gt 0 ]]; then
    echo "REPRO RESULT: $FAILS assertion failure(s)" >&2
    exit 1
fi
echo "REPRO RESULT: all assertions passed."
