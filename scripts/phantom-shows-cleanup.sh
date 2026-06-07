#!/usr/bin/env bash
# phantom-shows-cleanup.sh
#
# One-shot cleanup for the broken phantom-shows state described in
# PLAN.md § M13 (Per-series subdir stub layout for TV phantoms).
#
# Current state on a stock Jellyfin install with PhantomLibrary < M13:
#   - /var/lib/jellyfin/phantom-library/shows/ contains thousands of
#     loose <Name>__phantom_tmdbN.mp4 symlink stubs.
#   - Jellyfin's TV resolver scanned those loose files into orphan
#     Episode rows (SeriesId=0, SeasonId=0) which never display in
#     any browse surface.
#   - 36 stale Series BaseItem rows from earlier successful creates
#     still point at those same loose-file paths; they will never
#     get re-bound to working stubs because the new M13 layout uses
#     per-series subdirs, not flat files.
#   - phantom.db has zero `series`-typed phantom_items rows; the
#     dedupe-write path stopped firing once paths started colliding
#     with scanner-created Episodes.
#
# This script wipes that broken state so M13 can repopulate cleanly:
#   1. Stop jellyfin.service.
#   2. Back up jellyfin.db and phantom.db with a timestamped suffix.
#   3. Delete every BaseItem (Episode or Series) whose Path lives
#      under /var/lib/jellyfin/phantom-library/shows/. SQLite ON
#      DELETE CASCADE handles AncestorIds, BaseItemImageInfos,
#      BaseItemMetadataFields, BaseItemProviders, Chapters,
#      ItemValuesMap, UserData, PeopleBaseItemMap, etc.
#   4. Delete every `series`-typed row from phantom_items (zero
#      today; safe-defensive in case M13 has been partially run).
#   5. Remove every regular-file entry under phantom-library/shows/
#      EXCEPT the .phantom-library-keep sentinel. Symlinks and real
#      files alike are removed; the sentinel survives so the
#      CollectionFolder binder does not cull the folder before M13
#      writes the first per-series subdir.
#   6. Restart jellyfin.service. Operator must trigger a Suggestions
#      refresh from the dashboard once M13 ships to repopulate shows.
#
# Idempotent: re-running on a cleaned state is a no-op (DELETEs
# match zero rows; rm finds no entries).
#
# Requires: bash, sudo (script must run as root because jellyfin.db
# is owned by the jellyfin user and the install dir is mode 700),
# sqlite3, systemctl.

set -euo pipefail

JELLYFIN_USER="${JELLYFIN_USER:-jellyfin}"
JELLYFIN_DB="${JELLYFIN_DB:-/var/lib/jellyfin/data/jellyfin.db}"
PHANTOM_DB="${PHANTOM_DB:-/var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db}"
PHANTOM_SHOWS_DIR="${PHANTOM_SHOWS_DIR:-/var/lib/jellyfin/phantom-library/shows}"
SENTINEL_NAME=".phantom-library-keep"
TS="$(date -u +%Y%m%dT%H%M%SZ)"

log() { printf '[phantom-shows-cleanup] %s\n' "$*"; }
die() { printf '[phantom-shows-cleanup] ERROR: %s\n' "$*" >&2; exit 1; }

if [[ $EUID -ne 0 ]]; then
    die "must run as root (sudo). jellyfin.db is owned by ${JELLYFIN_USER}."
fi

command -v sqlite3   >/dev/null 2>&1 || die "sqlite3 not on PATH."
command -v systemctl >/dev/null 2>&1 || die "systemctl not on PATH."

[[ -f "$JELLYFIN_DB" ]] || die "jellyfin.db not found at $JELLYFIN_DB (override with JELLYFIN_DB=...)"
[[ -f "$PHANTOM_DB"  ]] || die "phantom.db not found at $PHANTOM_DB (override with PHANTOM_DB=...)"
[[ -d "$PHANTOM_SHOWS_DIR" ]] || die "phantom shows dir not found at $PHANTOM_SHOWS_DIR (override with PHANTOM_SHOWS_DIR=...)"

# ------------------------------------------------------------------
# 1. Stop jellyfin so the SQLite WAL is flushed and no concurrent
#    writers race our DELETE.
# ------------------------------------------------------------------
log "stopping jellyfin.service"
systemctl stop jellyfin.service
# Give the WAL checkpoint a moment to land before we touch the files.
sleep 2

# ------------------------------------------------------------------
# 2. Timestamped backups, owned by the jellyfin user so a future
#    restore is a plain `cp` away.
# ------------------------------------------------------------------
JF_BAK="${JELLYFIN_DB}.bak-${TS}"
PH_BAK="${PHANTOM_DB}.bak-${TS}"

log "backing up jellyfin.db -> $JF_BAK"
cp -a -- "$JELLYFIN_DB" "$JF_BAK"

log "backing up phantom.db -> $PH_BAK"
cp -a -- "$PHANTOM_DB" "$PH_BAK"

# ------------------------------------------------------------------
# 3. Wipe BaseItem rows for the broken phantom-shows entries.
#    Path-prefix match is anchored to the exact dir to avoid touching
#    unrelated rows.
# ------------------------------------------------------------------
SHOWS_PREFIX="${PHANTOM_SHOWS_DIR%/}/"

log "counting BaseItem rows to delete (Path LIKE '${SHOWS_PREFIX}%')"
PRE_EP=$(sqlite3 "$JELLYFIN_DB" "SELECT COUNT(*) FROM BaseItems WHERE Type='MediaBrowser.Controller.Entities.TV.Episode' AND Path LIKE '${SHOWS_PREFIX}%';")
PRE_SR=$(sqlite3 "$JELLYFIN_DB" "SELECT COUNT(*) FROM BaseItems WHERE Type='MediaBrowser.Controller.Entities.TV.Series'  AND Path LIKE '${SHOWS_PREFIX}%';")
PRE_SE=$(sqlite3 "$JELLYFIN_DB" "SELECT COUNT(*) FROM BaseItems WHERE Type='MediaBrowser.Controller.Entities.TV.Season'   AND Path LIKE '${SHOWS_PREFIX}%';")
log "  Episodes: $PRE_EP    Seasons: $PRE_SE    Series: $PRE_SR"

log "deleting BaseItem rows (Episode + Season + Series under ${PHANTOM_SHOWS_DIR})"
sqlite3 "$JELLYFIN_DB" <<SQL
PRAGMA foreign_keys = ON;
BEGIN;
DELETE FROM BaseItems
 WHERE Path LIKE '${SHOWS_PREFIX}%'
   AND Type IN (
       'MediaBrowser.Controller.Entities.TV.Episode',
       'MediaBrowser.Controller.Entities.TV.Season',
       'MediaBrowser.Controller.Entities.TV.Series'
   );
COMMIT;
VACUUM;
SQL

POST_EP=$(sqlite3 "$JELLYFIN_DB" "SELECT COUNT(*) FROM BaseItems WHERE Type='MediaBrowser.Controller.Entities.TV.Episode' AND Path LIKE '${SHOWS_PREFIX}%';")
POST_SR=$(sqlite3 "$JELLYFIN_DB" "SELECT COUNT(*) FROM BaseItems WHERE Type='MediaBrowser.Controller.Entities.TV.Series'  AND Path LIKE '${SHOWS_PREFIX}%';")
POST_SE=$(sqlite3 "$JELLYFIN_DB" "SELECT COUNT(*) FROM BaseItems WHERE Type='MediaBrowser.Controller.Entities.TV.Season'   AND Path LIKE '${SHOWS_PREFIX}%';")
log "  after: Episodes=$POST_EP Seasons=$POST_SE Series=$POST_SR (all should be 0)"

if [[ "$POST_EP" != "0" || "$POST_SE" != "0" || "$POST_SR" != "0" ]]; then
    die "post-delete row counts are non-zero; aborting before touching disk. Backups are at $JF_BAK / $PH_BAK."
fi

# ------------------------------------------------------------------
# 4. Drop any phantom_items rows of type 'series'. Today there are
#    none, but a partial M13 rollout could have written some.
# ------------------------------------------------------------------
PRE_PSR=$(sqlite3 "$PHANTOM_DB" "SELECT COUNT(*) FROM phantom_items WHERE type='series';")
log "deleting phantom_items rows where type='series' (pre-count: $PRE_PSR)"
sqlite3 "$PHANTOM_DB" "DELETE FROM phantom_items WHERE type='series';"
POST_PSR=$(sqlite3 "$PHANTOM_DB" "SELECT COUNT(*) FROM phantom_items WHERE type='series';")
log "  after: $POST_PSR (should be 0)"

# ------------------------------------------------------------------
# 5. Wipe loose stub files under phantom-library/shows/. Keeps the
#    sentinel so PhantomCollectionFolderBinder + Jellyfin's empty-
#    folder skip do not drop the bound physical folder before M13's
#    first per-series subdir gets created.
# ------------------------------------------------------------------
log "counting files under $PHANTOM_SHOWS_DIR (excluding $SENTINEL_NAME)"
PRE_FILES=$(find "$PHANTOM_SHOWS_DIR" -mindepth 1 -maxdepth 1 ! -name "$SENTINEL_NAME" | wc -l)
log "  files to remove: $PRE_FILES"

# Use -delete with predicates so we never recurse past the top dir
# and never touch the sentinel. -mindepth 1 -maxdepth 1 keeps the
# parent dir itself in place.
find "$PHANTOM_SHOWS_DIR" -mindepth 1 -maxdepth 1 ! -name "$SENTINEL_NAME" -delete

POST_FILES=$(find "$PHANTOM_SHOWS_DIR" -mindepth 1 -maxdepth 1 ! -name "$SENTINEL_NAME" | wc -l)
log "  after: $POST_FILES (should be 0)"

if [[ "$POST_FILES" != "0" ]]; then
    die "post-cleanup file count is non-zero; manual inspection needed. Backups at $JF_BAK / $PH_BAK."
fi

# Make sure the sentinel is still present; recreate if a stray
# previous cleanup nuked it.
SENTINEL_PATH="${PHANTOM_SHOWS_DIR}/${SENTINEL_NAME}"
if [[ ! -f "$SENTINEL_PATH" ]]; then
    log "re-creating sentinel at $SENTINEL_PATH"
    printf 'Phantom Library sentinel; do not delete. See PLAN §M10.\n' > "$SENTINEL_PATH"
    chown "$JELLYFIN_USER:$JELLYFIN_USER" "$SENTINEL_PATH"
fi

# ------------------------------------------------------------------
# 6. Restart Jellyfin.
# ------------------------------------------------------------------
log "starting jellyfin.service"
systemctl start jellyfin.service

log "done."
log "summary:"
log "  jellyfin.db backup : $JF_BAK"
log "  phantom.db backup  : $PH_BAK"
log "  Episode rows wiped : $PRE_EP"
log "  Season  rows wiped : $PRE_SE"
log "  Series  rows wiped : $PRE_SR"
log "  series phantom rows: $PRE_PSR"
log "  stub files wiped   : $PRE_FILES"
log ""
log "next: install plugin with M13 stub-layout fix, then trigger"
log "the Suggestions / Refresh task from the Jellyfin dashboard."
