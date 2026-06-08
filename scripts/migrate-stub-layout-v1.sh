#!/usr/bin/env bash
#
# migrate-stub-layout-v1.sh
# =========================
#
# WHAT THIS DOES
#   Canonical one-shot migration of phantom stubs from the legacy
#   `__phantom_tmdb<id>` filename-sentinel scheme to the
#   Jellyfin-native `<Title> (<Year>) [tmdbid-<id>]` path-token
#   scheme. Renames files / directories on disk and updates
#   `BaseItems.Path` in jellyfin.db so Jellyfin keeps pointing at
#   the new location. Also collapses duplicate BaseItems (same Tmdb
#   provider id + same Type) that the broken v0.2.0.0 in-plugin
#   migration created when it raced the live library scanner.
#
# WHEN TO RUN
#   Once, after upgrading to (or past) plugin v0.2.0.0, with
#   `jellyfin.service` STOPPED. Re-runs are safe: per-row decisions
#   are idempotent, and a `plugin_meta` marker
#   (`stub_layout_v1_complete`) is written on a clean pass so
#   subsequent invocations short-circuit. This bash script is the
#   ONLY supported migration path for the v0.1.0 -> v0.2.0 layout
#   switch; the in-plugin StubLayoutMigration that briefly shipped
#   in v0.2.0.0 has been removed (it raced the live scanner; see
#   below).
#
# PASSES (in order)
#   1. Per-row rename: legacy `__phantom_tmdb<id>` -> new
#      `<Title> (<Year>) [tmdbid-<id>]` form, both on disk and in
#      `BaseItems.Path`. Idempotent (rows already in new form are
#      counted under `already_new`). If both the legacy and
#      new-form files exist on disk and are symlinks to the same
#      target, the legacy file is removed and the row is migrated
#      (counted under `migrated`).
#   2. Duplicate-BaseItem collapse: when the broken in-plugin
#      migration moved a file out from under the live scanner the
#      scanner created a fresh BaseItem with a new GUID for the
#      new path. This pass picks one survivor per (Tmdb id, Type)
#      and deletes the rest. Counters: `duplicates_keep`/`_drop`.
#   3. Orphan reassociation: every phantom_items row whose
#      `item_guid` no longer matches any BaseItem is looked up by
#      (Tmdb id + type) under `--stub-root`. If exactly one
#      BaseItem matches, the phantom row's `item_guid` (and any
#      other phantom.db table that references item_guid, e.g.
#      `materialisation_log`) is rewritten to point at the
#      surviving BaseItem so historical state (autopilot,
#      eviction_protected, original_overview, materialisation log)
#      is preserved across the rename. Counter: `reassociated`.
#      With `--prune-orphans` set, phantom rows that have NO
#      matching BaseItem (genuinely unrecoverable) are deleted
#      after the reassociation pass completes. Without the flag
#      they are left in place (harmless; counter is
#      `orphan_no_baseitem`).
#
# WHY IT EXISTS (the v0.2.0.0 story)
#   v0.2.0.0 shipped an in-plugin `StubLayoutMigration`
#   IHostedService that ran on plugin startup while Jellyfin was
#   live. It raced the library scanner: the watcher saw old paths
#   vanish before our `UpdateItemAsync` landed, the scanner saw
#   new-format paths appear and CREATED FRESH BaseItems for them,
#   and the UI kept rendering the ugly legacy scanner-derived
#   names because the old BaseItems were still present. That
#   service has been deleted. This script is now the only
#   supported way to migrate. See AGENTS.md ("Single-operator
#   deployment") for the rule that motivated the change.
#
# HOW TO DRY-RUN
#   sudo bash scripts/migrate-stub-layout-v1.sh --dry-run --verbose \
#       | tee /tmp/migrate-dryrun.log
#   Performs ZERO writes. Prints every intended rename, DB update,
#   and duplicate-collapse decision. Use to size the work and
#   detect surprises before committing.
#
# HOW TO COMMIT
#   sudo bash scripts/migrate-stub-layout-v1.sh --verbose \
#       | tee /tmp/migrate-run.log
#
# HOW TO RECOVER
#   Both DBs are backed up to `<dir>/<dbname>.bak.<timestamp>`
#   beside their originals before any write. To roll back:
#     sudo systemctl stop jellyfin
#     sudo cp /var/lib/jellyfin/data/jellyfin.db.bak.<ts> \
#             /var/lib/jellyfin/data/jellyfin.db
#     sudo cp /var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db.bak.<ts> \
#             /var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db
#   File-system moves are NOT backed up (would be huge). To undo
#   them, run the script with the old layout still in place, or
#   restore the affected stubs from the operator's next
#   `SuggestionsContributor` pass once Jellyfin restarts.
#
set -euo pipefail

PHANTOM_DB="/var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db"
JELLYFIN_DB="/var/lib/jellyfin/data/jellyfin.db"
STUB_ROOT="/var/lib/jellyfin/phantom-library"
DRY_RUN=0
VERBOSE=0
PRUNE_ORPHANS=0

usage() {
    cat <<EOF
Usage: $0 [options]

Options:
  --phantom-db PATH    Path to phantom.db
                       (default: $PHANTOM_DB)
  --jellyfin-db PATH   Path to jellyfin.db
                       (default: $JELLYFIN_DB)
  --stub-root PATH     Phantom stub root
                       (default: $STUB_ROOT)
  --dry-run            Print actions, perform no writes (no file
                       moves, no DB updates, no backups).
  --verbose            Per-row decision log.
  --prune-orphans      After reassociation, DELETE phantom_items
                       (and related materialisation_log) rows whose
                       BaseItem genuinely no longer exists in
                       jellyfin.db AND no surviving BaseItem could
                       be located by (Tmdb id, type) under
                       --stub-root. Default off.
  --help               Show this help.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --phantom-db)    PHANTOM_DB="$2"; shift 2 ;;
        --jellyfin-db)   JELLYFIN_DB="$2"; shift 2 ;;
        --stub-root)     STUB_ROOT="$2"; shift 2 ;;
        --dry-run)       DRY_RUN=1; shift ;;
        --verbose)       VERBOSE=1; shift ;;
        --prune-orphans) PRUNE_ORPHANS=1; shift ;;
        --help|-h)       usage; exit 0 ;;
        *) echo "unknown arg: $1" >&2; usage; exit 2 ;;
    esac
done

vlog() { [[ "$VERBOSE" -eq 1 ]] && echo "$@" || true; }
log()  { echo "$@"; }
err()  { echo "ERROR: $*" >&2; }

# ---------------------------------------------------------------------------
# 0. Refuse to run if Jellyfin is active.
#    Escape hatch for sandboxed test rigs that run an unrelated
#    Jellyfin instance pointed at different DBs:
#      PHANTOM_MIGRATE_FORCE=1 bash scripts/migrate-stub-layout-v1.sh ...
#    Operators on the prod box: do NOT set this. Stop Jellyfin first.
# ---------------------------------------------------------------------------
if [[ "${PHANTOM_MIGRATE_FORCE:-0}" != "1" ]]; then
    if command -v systemctl >/dev/null 2>&1; then
        for svc in jellyfin.service jellyfin; do
            if systemctl is-active --quiet "$svc" 2>/dev/null; then
                err "$svc is active. Stop it first (sudo systemctl stop jellyfin)."
                exit 1
            fi
        done
    fi
    # Fallback for systemd-less environments / container starts.
    if pgrep -fa 'jellyfin' >/dev/null 2>&1; then
        # Filter out our own grep / shell, and the rig's run-test.sh harness.
        matches=$(pgrep -fa 'jellyfin' | grep -Ev "$$|migrate-stub-layout|run-test\.sh" || true)
        if [[ -n "$matches" ]]; then
            err "a jellyfin-like process is running:"
            echo "$matches" >&2
            err "stop it before migrating (or set PHANTOM_MIGRATE_FORCE=1 for sandbox testing)."
            exit 1
        fi
    fi
fi

# ---------------------------------------------------------------------------
# 1. Sanity checks.
# ---------------------------------------------------------------------------
for f in "$PHANTOM_DB" "$JELLYFIN_DB"; do
    if [[ ! -f "$f" ]]; then err "missing DB: $f"; exit 1; fi
done
if [[ ! -d "$STUB_ROOT" ]]; then err "stub root does not exist: $STUB_ROOT"; exit 1; fi

# Schema probes.
if ! sqlite3 "$PHANTOM_DB" \
        "SELECT 1 FROM sqlite_master WHERE type='table' AND name='phantom_items';" \
        | grep -q 1; then
    err "phantom.db has no phantom_items table; wrong DB?"; exit 1
fi
if ! sqlite3 "$PHANTOM_DB" \
        "SELECT 1 FROM sqlite_master WHERE type='table' AND name='plugin_meta';" \
        | grep -q 1; then
    err "phantom.db has no plugin_meta table; plugin schema is older than v5."
    err "Start (and immediately stop) Jellyfin once with the v0.2.0.0+ plugin"
    err "installed to let it create the table, then re-run this script."
    exit 1
fi
if ! sqlite3 "$JELLYFIN_DB" \
        "SELECT 1 FROM sqlite_master WHERE type='table' AND name='BaseItems';" \
        | grep -q 1; then
    err "jellyfin.db has no BaseItems table; wrong DB?"; exit 1
fi

# Confirm the join shape: Jellyfin 10.11 stores BaseItems.Id as a
# dashed uppercase TEXT GUID. phantom.db stores item_guid as
# lowercase 32-char hex with no dashes. Probe one row to verify.
JOIN_PROBE=$(sqlite3 "$JELLYFIN_DB" "
    SELECT COUNT(*) FROM BaseItems
     WHERE lower(replace(Id,'-','')) IN (
        SELECT item_guid FROM (SELECT '0' AS item_guid)
     );" 2>/dev/null || echo "ERR")
if [[ "$JOIN_PROBE" == "ERR" ]]; then
    err "join probe failed; BaseItems.Id is not TEXT or the schema is unexpected."; exit 1
fi
# Real join probe: are there ANY phantom_items rows that resolve to a BaseItem?
JOIN_HITS=$(sqlite3 "$JELLYFIN_DB" "ATTACH DATABASE '$PHANTOM_DB' AS p;
    SELECT COUNT(*) FROM BaseItems b
      JOIN p.phantom_items pi
        ON lower(replace(b.Id,'-',''))=pi.item_guid;")
log "[migrate] join probe: $JOIN_HITS phantom_items rows resolve to a BaseItem"
if [[ "$JOIN_HITS" -eq 0 ]]; then
    err "no phantom_items rows join to any BaseItem; refusing to proceed."
    err "Either the DBs are mismatched, or phantom_items is empty."
    exit 1
fi

# Probe Tmdb provider rows.
TMDB_HITS=$(sqlite3 "$JELLYFIN_DB" \
    "SELECT COUNT(*) FROM BaseItemProviders WHERE ProviderId='Tmdb';" 2>/dev/null || echo 0)
log "[migrate] BaseItemProviders has $TMDB_HITS Tmdb rows"

log "[migrate] phantom_db   = $PHANTOM_DB"
log "[migrate] jellyfin_db  = $JELLYFIN_DB"
log "[migrate] stub_root    = $STUB_ROOT"
log "[migrate] dry_run      = $DRY_RUN"
log "[migrate] verbose      = $VERBOSE"
log "[migrate] prune_orphan = $PRUNE_ORPHANS"

# ---------------------------------------------------------------------------
# 2. Backups (skip on dry-run).
# ---------------------------------------------------------------------------
TS=$(date -u +%Y%m%dT%H%M%SZ)
JELLYFIN_BAK="${JELLYFIN_DB}.bak.${TS}"
PHANTOM_BAK="${PHANTOM_DB}.bak.${TS}"
if [[ "$DRY_RUN" -eq 0 ]]; then
    cp -p "$JELLYFIN_DB" "$JELLYFIN_BAK"
    cp -p "$PHANTOM_DB"  "$PHANTOM_BAK"
    log "[migrate] backups:"
    log "  $JELLYFIN_BAK"
    log "  $PHANTOM_BAK"
else
    log "[migrate] (dry-run; skipping backups)"
fi

# ---------------------------------------------------------------------------
# Helpers.
# ---------------------------------------------------------------------------
sql_phantom() { sqlite3 "$PHANTOM_DB"  "$@"; }
sql_phantom_t() { sqlite3 -separator $'\t' "$PHANTOM_DB" "$@"; }
sql_jellyfin() {
    # Enable FKs so DELETE FROM BaseItems cascades to child tables
    # (BaseItemProviders, AncestorIds, UserData, etc.). MediaSegments
    # and TrickplayInfos have no FK; we delete those manually below.
    sqlite3 "$JELLYFIN_DB" "PRAGMA foreign_keys=ON; $*"
}
sql_jellyfin_ro() { sqlite3 "$JELLYFIN_DB" "$@"; }
sql_jellyfin_t() { sqlite3 -separator $'\t' "$JELLYFIN_DB" "$@"; }

sql_escape() { printf "%s" "$1" | sed "s/'/''/g"; }

# Reverse-derive a clean display title from a legacy stem.
# In: leaf without extension (e.g. "The_Boys__phantom_tmdb1234")
# Out: "The Boys"
reverse_derive_title() {
    local leaf="$1"
    leaf="${leaf%%__phantom_tmdb*}"
    # underscores to spaces, collapse, trim.
    echo "$leaf" | tr '_' ' ' | tr -s ' ' | sed -E 's/^ +| +$//g'
}

# Sanitise for the new layout: replace filesystem-hostile chars with
# space, collapse whitespace, trim. Mirrors PhantomStubManager.DisplaySanitize.
# Hostile chars: / \ : * ? < > | " [ ]
display_sanitize() {
    tr '/\\:*?<>|"[]' '            ' | tr -s '[:space:]' ' ' | sed -E 's/^ +| +$//g'
}

# Compute new movie path: <parent>/<Title> (<Year>) [tmdbid-<id>].<ext>
new_movie_path() {
    local old="$1" title="$2" year="$3" tmdb="$4"
    local parent ext stem
    parent=$(dirname "$old")
    ext="${old##*.}"
    [[ "$ext" == "$old" ]] && ext="mp4"
    if [[ -n "$year" && "$year" -gt 0 ]]; then
        stem="${title} (${year})"
    else
        stem="${title}"
    fi
    echo "${parent}/${stem} [tmdbid-${tmdb}].${ext}"
}

# Compute new series dir (always under <stub-root>/shows/, regardless of where
# the legacy stub lived) AND the canonical inner episode path.
new_series_paths() {
    local title="$2" year="$3" tmdb="$4"
    local stem episode_stem ext
    if [[ -n "$year" && "$year" -gt 0 ]]; then
        stem="${title} (${year}) [tmdbid-${tmdb}]"
        episode_stem="${title} (${year})"
    else
        stem="${title} [tmdbid-${tmdb}]"
        episode_stem="${title}"
    fi
    # Episode ext defaults to mp4 (splash extension).
    ext="mp4"
    echo "${STUB_ROOT}/shows/${stem}"
    echo "${STUB_ROOT}/shows/${stem}/Season 01"
    echo "${STUB_ROOT}/shows/${stem}/Season 01/${episode_stem} S01E01.${ext}"
}

# Parse a tmdb id from a legacy `__phantom_tmdb<digits>` substring.
parse_legacy_tmdb() {
    local s="$1"
    echo "$s" | grep -oE '__phantom_tmdb[0-9]+' | head -1 | sed 's/__phantom_tmdb//'
}

# Tell if a path is in new format (has `[tmdbid-N]`).
is_new_format() {
    [[ "$1" == *"[tmdbid-"* ]]
}

# True iff both paths are symlinks AND `readlink -f` resolves both
# to the same non-empty target. Used to safely collapse the
# legacy-file / new-format-file pair that the broken v0.2.0.0
# in-plugin migration sometimes left side-by-side when the
# scanner indexed the new path before our move finished.
links_equivalent() {
    local a="$1" b="$2"
    [[ -L "$a" && -L "$b" ]] || return 1
    local ta tb
    ta=$(readlink -f "$a" 2>/dev/null) || return 1
    tb=$(readlink -f "$b" 2>/dev/null) || return 1
    [[ -n "$ta" && "$ta" == "$tb" ]]
}
is_legacy_format() {
    [[ "$1" == *"__phantom_tmdb"* ]]
}

# Update BaseItem.Path for a given guid (lowercase 32-hex). Returns 0 if
# exactly one row was updated; nonzero otherwise. UPDATE + changes() must
# share the same sqlite3 invocation (each call is a fresh connection;
# `changes()` resets across connections).
update_baseitem_path() {
    local guid="$1" newpath="$2"
    local np_esc out
    np_esc=$(sql_escape "$newpath")
    out=$(sqlite3 "$JELLYFIN_DB" "
        PRAGMA foreign_keys=ON;
        UPDATE BaseItems SET Path='$np_esc'
         WHERE lower(replace(Id,'-',''))='$guid';
        SELECT changes();")
    [[ "$out" == "1" ]]
}

# Update phantom_items.stub_path + last_touched for a given item_guid.
update_phantom_stub_path() {
    local guid="$1" newpath="$2"
    local np_esc now out
    np_esc=$(sql_escape "$newpath")
    now=$(date -u +%s)
    out=$(sqlite3 "$PHANTOM_DB" "
        UPDATE phantom_items
           SET stub_path='$np_esc', last_touched=$now
         WHERE item_guid='$guid';
        SELECT changes();")
    [[ "$out" == "1" ]]
}

# ---------------------------------------------------------------------------
# 3. Per-row pass.
# ---------------------------------------------------------------------------

# Counters.
scanned=0
migrated=0
already_new=0
recovered_baseitem_path=0
skipped_conflict=0
skipped_orphan=0
skipped_pathless=0
skipped_not_phantom=0
both_missing=0
new_format_missing_on_disk=0
failed=0

# Snapshot every Virtual phantom row up-front (we'll mutate phantom.db
# as we go; iterate from a stable snapshot). Tab-separated: title /
# path fields never contain literal tabs (display_sanitize strips them).
mapfile -t ROWS < <(sql_phantom_t "
    SELECT item_guid, COALESCE(tmdb_id,''), type, COALESCE(stub_path,'')
      FROM phantom_items
     WHERE state='Virtual';")

log "[migrate] virtual rows to inspect: ${#ROWS[@]}"

for row in "${ROWS[@]}"; do
    [[ -z "$row" ]] && continue
    scanned=$((scanned+1))

    IFS=$'\t' read -r guid tmdb_row type stub_path <<< "$row"

    # Resolve BaseItem.
    bi=$(sql_jellyfin_t "
        SELECT COALESCE(Path,''), COALESCE(Name,''), COALESCE(ProductionYear,0), COALESCE(Type,'')
          FROM BaseItems
         WHERE lower(replace(Id,'-',''))='$guid'
         LIMIT 1;")

    if [[ -z "$bi" ]]; then
        # Orphan phantom row. DO NOT prune here even with
        # --prune-orphans: the reassociation pass below may
        # rebind this row to a surviving BaseItem (the broken
        # v0.2.0.0 run created a fresh BaseItem with a new GUID
        # for the moved file, leaving the phantom row pointing
        # at the dead old GUID). Pruning here would destroy
        # autopilot / eviction_protected / original_overview /
        # materialisation_log state that the reassociation pass
        # would otherwise preserve.
        vlog "[orphan-phantom-row] guid=$guid type=$type stub_path=$stub_path (deferred to reassociation pass)"
        skipped_orphan=$((skipped_orphan+1))
        continue
    fi

    IFS=$'\t' read -r bi_path bi_name bi_year bi_type <<< "$bi"

    if [[ -z "$bi_path" ]]; then
        vlog "[pathless] guid=$guid name='$bi_name'"
        skipped_pathless=$((skipped_pathless+1))
        continue
    fi

    # Not under our stub root → not ours.
    case "$bi_path" in
        "$STUB_ROOT"/*) ;;
        *)
            vlog "[not-phantom] guid=$guid path=$bi_path"
            skipped_not_phantom=$((skipped_not_phantom+1))
            continue
            ;;
    esac

    # Already new-format?
    if is_new_format "$bi_path" && ! is_legacy_format "$bi_path"; then
        if [[ ! -e "$bi_path" ]]; then
            vlog "[new-format-missing-on-disk] guid=$guid path=$bi_path"
            new_format_missing_on_disk=$((new_format_missing_on_disk+1))
        fi
        # Sync phantom_items.stub_path if stale/null.
        if [[ "$stub_path" != "$bi_path" && "$DRY_RUN" -eq 0 ]]; then
            update_phantom_stub_path "$guid" "$bi_path" || true
        fi
        vlog "[already-new] guid=$guid path=$bi_path"
        already_new=$((already_new+1))
        continue
    fi

    # Legacy format from here.
    if ! is_legacy_format "$bi_path"; then
        vlog "[not-phantom] guid=$guid path=$bi_path (neither token nor sentinel)"
        skipped_not_phantom=$((skipped_not_phantom+1))
        continue
    fi

    # Derive title.
    title_raw="$bi_name"
    if [[ -z "$title_raw" || "$title_raw" == *"__phantom_tmdb"* ]]; then
        leaf=$(basename "$bi_path")
        leaf="${leaf%.*}"
        title_raw=$(reverse_derive_title "$leaf")
    fi
    title=$(echo "$title_raw" | display_sanitize)
    [[ -z "$title" ]] && title="Untitled"

    year=""
    if [[ "$bi_year" =~ ^[0-9]+$ && "$bi_year" -gt 0 ]]; then
        year="$bi_year"
    fi

    # TMDB id: prefer the one parsed from path (authoritative for
    # legacy stubs; ProviderIds-via-BaseItemProviders is more
    # reliable but we already have the substring).
    tmdb=$(parse_legacy_tmdb "$bi_path")
    if [[ -z "$tmdb" && -n "$tmdb_row" && "$tmdb_row" != "0" ]]; then
        tmdb="$tmdb_row"
    fi
    if [[ -z "$tmdb" ]]; then
        log "[FAIL] guid=$guid no tmdb id parsable from path=$bi_path"
        failed=$((failed+1))
        continue
    fi

    # Compute new path. Series: use the series-dir layout; movies: file.
    is_series=0
    case "$bi_type" in
        *Series*|*Episode*|*Season*) is_series=1 ;;
    esac
    # Fallback: phantom_items.type.
    if [[ "$type" == "series" ]]; then is_series=1; fi

    if [[ "$is_series" -eq 1 ]]; then
        mapfile -t paths < <(new_series_paths "" "$title" "$year" "$tmdb")
        new_dir="${paths[0]}"
        new_episode="${paths[2]}"
        # The legacy series stub lived as a loose-file `.mp4` under
        # shows/. The new layout is a directory containing
        # Season 01/<...>.mp4. So our move is: create the dir, move
        # (or symlink) the old file into Season 01/, then rename.
        # In v0.2.0.0's intended layout per PhantomStubManager,
        # the on-disk artefact is a single splash mp4 file.
        skip_mv=0
        if [[ -e "$new_dir" ]]; then
            if [[ -e "$new_episode" ]] && links_equivalent "$bi_path" "$new_episode"; then
                log "[migrate-series-conflict-resolved] guid=$guid (legacy file + new episode are equivalent symlinks)"
                log "    old: $bi_path"
                log "    new: $new_episode"
                if [[ "$DRY_RUN" -eq 0 ]]; then
                    rm -f "$bi_path"
                fi
                skip_mv=1
            else
                log "[conflict] series target dir exists: guid=$guid new=$new_dir old=$bi_path"
                skipped_conflict=$((skipped_conflict+1))
                continue
            fi
        fi
        if [[ "$skip_mv" -eq 0 ]]; then
            log "[migrate-series] guid=$guid"
            log "    old: $bi_path"
            log "    new: $new_episode"
            if [[ "$DRY_RUN" -eq 0 ]]; then
                mkdir -p "$(dirname "$new_episode")"
                if [[ -e "$bi_path" || -L "$bi_path" ]]; then
                    if ! mv -n "$bi_path" "$new_episode"; then
                        log "  [FAIL] mv -n returned nonzero"
                        failed=$((failed+1)); continue
                    fi
                else
                    # File was already moved (broken v0.2.0.0 run).
                    # If new path exists, we recover below.
                    if [[ ! -e "$new_episode" ]]; then
                        log "  [both-missing] guid=$guid old=$bi_path new=$new_episode"
                        both_missing=$((both_missing+1))
                        continue
                    fi
                fi
            fi
        fi
        new_path="$new_episode"
    else
        new_path=$(new_movie_path "$bi_path" "$title" "$year" "$tmdb")
        if [[ "$bi_path" == "$new_path" ]]; then
            vlog "[already-new-exact] guid=$guid"
            already_new=$((already_new+1))
            continue
        fi
        if [[ -e "$new_path" && ! ( -e "$bi_path" || -L "$bi_path" ) ]]; then
            # Likely a recovery case from the broken v0.2.0.0 run.
            log "[recover] guid=$guid new exists, old missing"
            log "    old: $bi_path"
            log "    new: $new_path"
            if [[ "$DRY_RUN" -eq 0 ]]; then
                if update_baseitem_path "$guid" "$new_path" \
                   && update_phantom_stub_path "$guid" "$new_path"; then
                    recovered_baseitem_path=$((recovered_baseitem_path+1))
                    continue
                else
                    log "  [FAIL] DB update during recovery"
                    failed=$((failed+1)); continue
                fi
            else
                recovered_baseitem_path=$((recovered_baseitem_path+1)); continue
            fi
        fi
        skip_mv=0
        if [[ -e "$new_path" ]]; then
            if links_equivalent "$bi_path" "$new_path"; then
                log "[migrate-movie-conflict-resolved] guid=$guid (legacy + new are equivalent symlinks)"
                log "    old: $bi_path"
                log "    new: $new_path"
                if [[ "$DRY_RUN" -eq 0 ]]; then
                    rm -f "$bi_path"
                fi
                skip_mv=1
            else
                log "[conflict] dest exists: guid=$guid new=$new_path"
                skipped_conflict=$((skipped_conflict+1))
                continue
            fi
        fi
        if [[ "$skip_mv" -eq 0 ]]; then
            log "[migrate-movie] guid=$guid"
            log "    old: $bi_path"
            log "    new: $new_path"
            if [[ "$DRY_RUN" -eq 0 ]]; then
                mkdir -p "$(dirname "$new_path")"
                if [[ -e "$bi_path" || -L "$bi_path" ]]; then
                    if ! mv -n "$bi_path" "$new_path"; then
                        log "  [FAIL] mv -n returned nonzero"
                        failed=$((failed+1)); continue
                    fi
                else
                    if [[ ! -e "$new_path" ]]; then
                        log "  [both-missing] guid=$guid"
                        both_missing=$((both_missing+1))
                        continue
                    fi
                fi
            fi
        fi
    fi

    # DB updates.
    if [[ "$DRY_RUN" -eq 0 ]]; then
        if ! update_baseitem_path "$guid" "$new_path"; then
            log "  [FAIL] BaseItems.Path update rowcount != 1"
            failed=$((failed+1)); continue
        fi
        if ! update_phantom_stub_path "$guid" "$new_path"; then
            log "  [FAIL] phantom_items.stub_path update rowcount != 1"
            failed=$((failed+1)); continue
        fi
    fi
    migrated=$((migrated+1))
done

# ---------------------------------------------------------------------------
# 4. Duplicate BaseItem collapse.
#    Two or more BaseItems with the same (Tmdb provider id, Type) — the
#    broken v0.2.0.0 run created one when it moved a file out from under
#    the live scanner.
# ---------------------------------------------------------------------------
log
log "[migrate] looking for duplicate BaseItems by (Tmdb id, Type)..."

# List (tmdb, type) keys that have >1 BaseItem, scoped to phantom-tree
# entries (we don't want to collapse real-media duplicates the operator
# may have intentionally).
DUP_KEYS=$(sql_jellyfin_t "
    SELECT bip.ProviderValue, b.Type
      FROM BaseItemProviders bip
      JOIN BaseItems b ON b.Id = bip.ItemId
     WHERE bip.ProviderId='Tmdb'
       AND b.Path LIKE '${STUB_ROOT}/%'
     GROUP BY bip.ProviderValue, b.Type
     HAVING COUNT(*) > 1;")

dup_keep=0
dup_drop=0
dup_failed=0
DROPPED_DUP_IDS=()  # BaseItem GUIDs the dup-collapse pass dropped
                    # (or, in --dry-run, *would* drop). Reassociation
                    # excludes these so dry-run counters match real run.

if [[ -z "$DUP_KEYS" ]]; then
    log "[migrate] no duplicates."
else
    while IFS=$'\t' read -r tmdb btype; do
        [[ -z "$tmdb" ]] && continue
        # List candidates (BaseItem.Id) for this dup key.
        cands=$(sql_jellyfin_t "
            SELECT b.Id, COALESCE(b.Path,''), b.rowid
              FROM BaseItemProviders bip
              JOIN BaseItems b ON b.Id = bip.ItemId
             WHERE bip.ProviderId='Tmdb'
               AND bip.ProviderValue='$(sql_escape "$tmdb")'
               AND b.Type='$(sql_escape "$btype")'
               AND b.Path LIKE '${STUB_ROOT}/%';")

        # Build arrays.
        ids=(); paths=(); rowids=()
        while IFS=$'\t' read -r id p rid; do
            [[ -z "$id" ]] && continue
            ids+=("$id"); paths+=("$p"); rowids+=("$rid")
        done <<< "$cands"

        if [[ "${#ids[@]}" -le 1 ]]; then continue; fi

        # Survivor selection:
        #   1. Path on disk AND new-format.
        #   2. Has UserData rows.
        #   3. Lowest rowid (oldest).
        keep_idx=-1
        for i in "${!ids[@]}"; do
            p="${paths[$i]}"
            if [[ -n "$p" && -e "$p" ]] && is_new_format "$p" && ! is_legacy_format "$p"; then
                keep_idx=$i; break
            fi
        done
        if [[ "$keep_idx" -lt 0 ]]; then
            for i in "${!ids[@]}"; do
                ud=$(sql_jellyfin_ro "SELECT COUNT(*) FROM UserData WHERE ItemId='$(sql_escape "${ids[$i]}")';")
                if [[ "$ud" -gt 0 ]]; then keep_idx=$i; break; fi
            done
        fi
        if [[ "$keep_idx" -lt 0 ]]; then
            # Lowest rowid.
            best=999999999; best_i=0
            for i in "${!ids[@]}"; do
                r="${rowids[$i]}"
                if [[ "$r" -lt "$best" ]]; then best="$r"; best_i=$i; fi
            done
            keep_idx=$best_i
        fi

        keep_id="${ids[$keep_idx]}"
        keep_guid=$(echo "$keep_id" | tr '[:upper:]' '[:lower:]' | tr -d '-')
        drop_ids=()
        for i in "${!ids[@]}"; do
            [[ "$i" == "$keep_idx" ]] && continue
            drop_ids+=("${ids[$i]}")
        done

        log "[duplicate-collapse] tmdb=$tmdb type=$btype keep=$keep_id drop=${drop_ids[*]}"
        dup_keep=$((dup_keep+1))

        for d in "${drop_ids[@]}"; do
            d_guid=$(echo "$d" | tr '[:upper:]' '[:lower:]' | tr -d '-')
            dup_drop=$((dup_drop+1))
            DROPPED_DUP_IDS+=("$d")
            if [[ "$DRY_RUN" -eq 1 ]]; then continue; fi
            # Repoint phantom.db rows that point at the dropped guid.
            sql_phantom "UPDATE phantom_items   SET item_guid='$keep_guid' WHERE item_guid='$d_guid';" || true
            sql_phantom "UPDATE materialisation_log SET item_guid='$keep_guid' WHERE item_guid='$d_guid';" || true
            # Manually delete from tables that don't have FK CASCADE to BaseItems.
            esc=$(sql_escape "$d")
            sql_jellyfin "DELETE FROM MediaSegments   WHERE ItemId='$esc';" || true
            sql_jellyfin "DELETE FROM TrickplayInfos  WHERE ItemId='$esc';" || true
            # Cascading delete from BaseItems (FKs ON via sql_jellyfin).
            if ! sql_jellyfin "DELETE FROM BaseItems WHERE Id='$esc';"; then
                log "  [FAIL duplicate-collapse delete] id=$d"
                dup_failed=$((dup_failed+1))
            fi
        done
    done <<< "$DUP_KEYS"
fi

failed=$((failed + dup_failed))

# ---------------------------------------------------------------------------
# 5. Orphan reassociation.
#    For every phantom_items row whose item_guid no longer matches
#    any BaseItem, look up the surviving BaseItem by (Tmdb id +
#    type) under --stub-root. If exactly one match: rewrite
#    phantom_items.item_guid (and any other phantom.db table that
#    references item_guid) to that GUID so historical state
#    (autopilot, eviction_protected, original_overview,
#    materialisation log) is preserved across the rename. Runs
#    AFTER duplicate-collapse so there is at most one BaseItem per
#    (Tmdb id, Type) when we query. With --prune-orphans, rows
#    with NO surviving BaseItem are deleted afterwards.
# ---------------------------------------------------------------------------
log
log "[migrate] reassociating orphan phantom_items rows..."

reassociated=0
orphan_no_baseitem=0
orphan_multi_match=0
orphan_collision=0
orphan_pruned=0

# Tables in phantom.db that reference item_guid (discovered
# dynamically so a future schema add isn't silently missed).
mapfile -t GUID_TABLES < <(sql_phantom "
    SELECT name FROM sqlite_master
     WHERE type='table' AND sql LIKE '%item_guid%'
     ORDER BY name;")
vlog "[reassoc] tables touched: ${GUID_TABLES[*]}"

# Snapshot orphan rows (phantom_items rows whose item_guid does not
# resolve to any BaseItem in jellyfin.db) up-front so we iterate
# from a stable list while mutating phantom.db.
mapfile -t ORPHANS < <(sqlite3 -separator $'\t' "$JELLYFIN_DB" "
    ATTACH DATABASE '$PHANTOM_DB' AS p;
    SELECT pi.item_guid, COALESCE(pi.tmdb_id,''), pi.type
      FROM p.phantom_items pi
      LEFT JOIN BaseItems b
        ON lower(replace(b.Id,'-',''))=pi.item_guid
     WHERE b.Id IS NULL;")

log "[reassoc] orphan candidates: ${#ORPHANS[@]}"

for row in "${ORPHANS[@]}"; do
    [[ -z "$row" ]] && continue
    IFS=$'\t' read -r old_guid tmdb_row ptype <<< "$row"

    # Decide BaseItem.Type to query for.
    case "$ptype" in
        movie)  type_str="MediaBrowser.Controller.Entities.Movies.Movie" ;;
        series) type_str="MediaBrowser.Controller.Entities.TV.Series" ;;
        *)
            vlog "[reassoc-skip] guid=$old_guid unknown type='$ptype'"
            orphan_no_baseitem=$((orphan_no_baseitem+1))
            if [[ "$PRUNE_ORPHANS" -eq 1 && "$DRY_RUN" -eq 0 ]]; then
                for tbl in "${GUID_TABLES[@]}"; do
                    sql_phantom "DELETE FROM \"$tbl\" WHERE item_guid='$old_guid';" || true
                done
                orphan_pruned=$((orphan_pruned+1))
            fi
            continue
            ;;
    esac

    if [[ -z "$tmdb_row" || "$tmdb_row" == "0" ]]; then
        vlog "[reassoc-no-tmdb] guid=$old_guid type=$ptype"
        orphan_no_baseitem=$((orphan_no_baseitem+1))
        if [[ "$PRUNE_ORPHANS" -eq 1 && "$DRY_RUN" -eq 0 ]]; then
            for tbl in "${GUID_TABLES[@]}"; do
                sql_phantom "DELETE FROM \"$tbl\" WHERE item_guid='$old_guid';" || true
            done
            orphan_pruned=$((orphan_pruned+1))
        fi
        continue
    fi

    cands=$(sql_jellyfin_t "
        SELECT b.Id
          FROM BaseItems b
          JOIN BaseItemProviders bip ON bip.ItemId = b.Id
         WHERE bip.ProviderId='Tmdb'
           AND bip.ProviderValue='$(sql_escape "$tmdb_row")'
           AND b.Type='$(sql_escape "$type_str")'
           AND b.Path LIKE '${STUB_ROOT}/%';")

    cand_ids=()
    while IFS= read -r line; do
        [[ -z "$line" ]] && continue
        # Exclude BaseItems that the dup-collapse pass dropped (or,
        # in --dry-run, *would* drop). Without this, dry-run
        # reassoc would see n>1 for every dup TMDB id and skip
        # them — mismatching the real-run counter.
        skip=0
        for d in "${DROPPED_DUP_IDS[@]}"; do
            if [[ "$line" == "$d" ]]; then skip=1; break; fi
        done
        [[ "$skip" -eq 1 ]] && continue
        cand_ids+=("$line")
    done <<< "$cands"
    n=${#cand_ids[@]}

    if [[ "$n" -eq 0 ]]; then
        vlog "[reassoc-no-match] guid=$old_guid tmdb=$tmdb_row type=$ptype"
        orphan_no_baseitem=$((orphan_no_baseitem+1))
        if [[ "$PRUNE_ORPHANS" -eq 1 && "$DRY_RUN" -eq 0 ]]; then
            for tbl in "${GUID_TABLES[@]}"; do
                sql_phantom "DELETE FROM \"$tbl\" WHERE item_guid='$old_guid';" || true
            done
            orphan_pruned=$((orphan_pruned+1))
        fi
        continue
    fi

    if [[ "$n" -gt 1 ]]; then
        log "[reassoc-multi] guid=$old_guid tmdb=$tmdb_row type=$ptype matches=${cand_ids[*]}; skipping (duplicate-collapse should have prevented this)"
        orphan_multi_match=$((orphan_multi_match+1))
        continue
    fi

    new_guid=$(echo "${cand_ids[0]}" | tr '[:upper:]' '[:lower:]' | tr -d '-')

    if [[ "$new_guid" == "$old_guid" ]]; then
        # Defensive: we LEFT JOINed so this should be unreachable.
        vlog "[reassoc-noop] guid=$old_guid resolves to itself"
        continue
    fi

    # phantom_items.item_guid is PRIMARY KEY — if another row
    # already owns the target GUID, the UPDATE would violate the
    # PK constraint. Skip with a warning; operator can investigate
    # and merge by hand.
    if [[ -n "$(sql_phantom "SELECT 1 FROM phantom_items WHERE item_guid='$new_guid';")" ]]; then
        log "[reassoc-collision] orphan=$old_guid would collide with existing phantom_items row guid=$new_guid; skipping"
        orphan_collision=$((orphan_collision+1))
        continue
    fi

    vlog "[reassoc] $old_guid -> $new_guid (tmdb=$tmdb_row type=$ptype)"
    if [[ "$DRY_RUN" -eq 0 ]]; then
        # phantom_items owns the PK — update it first. Then any
        # other tables that carry item_guid as a non-key FK-ish
        # column (currently materialisation_log; discovered
        # dynamically above).
        if ! sql_phantom "UPDATE phantom_items SET item_guid='$new_guid' WHERE item_guid='$old_guid';"; then
            log "  [FAIL] phantom_items rewrite $old_guid -> $new_guid"
            failed=$((failed+1)); continue
        fi
        for tbl in "${GUID_TABLES[@]}"; do
            [[ "$tbl" == "phantom_items" ]] && continue
            sql_phantom "UPDATE \"$tbl\" SET item_guid='$new_guid' WHERE item_guid='$old_guid';" || true
        done
    fi
    reassociated=$((reassociated+1))
done

log "[reassoc] reassociated=$reassociated orphan_no_baseitem=$orphan_no_baseitem orphan_multi_match=$orphan_multi_match orphan_collision=$orphan_collision orphan_pruned=$orphan_pruned"

# ---------------------------------------------------------------------------
# 6. Marker.
# ---------------------------------------------------------------------------
# Blockers (must be zero for marker to set): failed, skipped_conflict,
# both_missing. Non-blocking observations (per spec):
#   - new_format_missing_on_disk: file evicted/removed post-rename;
#     a re-bind on next Suggestions pass will recreate the stub.
#   - orphan_no_baseitem: unrecoverable phantom rows; leaving them
#     is harmless. Operator can re-run with --prune-orphans to GC.
#   - skipped_not_phantom: typically stale-Virtual rows whose
#     BaseItem.Path is a gostream FUSE path (real Materialised
#     items with a stale state column). Benign.
#   - orphan_multi_match / orphan_collision: rare; logged loudly.
marker_set="no"
if [[ "$failed" -eq 0 && "$skipped_conflict" -eq 0 && "$both_missing" -eq 0 ]]; then
    if [[ "$DRY_RUN" -eq 0 ]]; then
        now=$(date -u +%Y-%m-%dT%H:%M:%SZ)
        sql_phantom "
            INSERT INTO plugin_meta(key,value) VALUES('stub_layout_v1_complete','$now')
            ON CONFLICT(key) DO UPDATE SET value=excluded.value;"
        marker_set="yes"
    else
        marker_set="would-set"
    fi
fi

# ---------------------------------------------------------------------------
# 7. Summary.
# ---------------------------------------------------------------------------
cat <<EOF

==== Migration summary ====
scanned:                  $scanned
migrated:                 $migrated
already_new:              $already_new
recovered_baseitem_path:  $recovered_baseitem_path
skipped_conflict:         $skipped_conflict
skipped_orphan:           $skipped_orphan
skipped_pathless:         $skipped_pathless
skipped_not_phantom:      $skipped_not_phantom
both_missing:             $both_missing
new_format_missing_on_disk: $new_format_missing_on_disk
duplicates_keep:          $dup_keep
duplicates_drop:          $dup_drop
reassociated:             $reassociated
orphan_no_baseitem:       $orphan_no_baseitem
orphan_multi_match:       $orphan_multi_match
orphan_collision:         $orphan_collision
orphan_pruned:            $orphan_pruned
failed:                   $failed
marker_set:               $marker_set
dry_run:                  $DRY_RUN
EOF

if [[ "$DRY_RUN" -eq 0 ]]; then
    cat <<EOF
backups:
  $JELLYFIN_BAK
  $PHANTOM_BAK
EOF
fi

if [[ "$failed" -gt 0 ]]; then
    err "$failed failures; investigate before re-running."
    exit 1
fi
if [[ "$marker_set" == "no" ]]; then
    log "[migrate] marker NOT set (conflicts / both-missing present)."
    log "[migrate] address the items listed above and re-run."
fi
exit 0
