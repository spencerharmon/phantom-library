#!/usr/bin/env bash
#
# migrate-stub-layout-v1.sh — manual fallback for the in-plugin
# StubLayoutMigration. The plugin runs the same migration automatically
# at startup; this script exists for cases where the in-plugin run
# cannot complete (e.g. operator wants to inspect / dry-run / fix
# permissions before letting the plugin loose).
#
# IMPORTANT:
#  - Jellyfin must be STOPPED before running. The script writes to
#    jellyfin.db; concurrent writes corrupt the WAL.
#  - Default paths assume the standard package install layout
#    (/var/lib/jellyfin/...). Override with flags if needed.
#  - Idempotent at the per-row level. Safe to re-run.
#
# Renames phantom stubs from the legacy `__phantom_tmdb<id>` filename
# scheme to the Jellyfin-native `[tmdbid-<id>]` path-token scheme and
# updates BaseItems.Path so Jellyfin keeps pointing at the new location.

set -euo pipefail

PHANTOM_DB="/var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db"
JELLYFIN_DB="/var/lib/jellyfin/data/jellyfin.db"
STUB_ROOT="/var/lib/jellyfin/phantom-library"
DRY_RUN=0

usage() {
    cat <<EOF
Usage: $0 [options]

Options:
  --phantom-db PATH    Path to phantom.db (default: $PHANTOM_DB)
  --jellyfin-db PATH   Path to jellyfin.db (default: $JELLYFIN_DB)
  --stub-root PATH     Phantom stub root (default: $STUB_ROOT)
  --dry-run            Print actions without performing any moves / writes.
  --help               Show this help.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --phantom-db) PHANTOM_DB="$2"; shift 2 ;;
        --jellyfin-db) JELLYFIN_DB="$2"; shift 2 ;;
        --stub-root) STUB_ROOT="$2"; shift 2 ;;
        --dry-run) DRY_RUN=1; shift ;;
        --help|-h) usage; exit 0 ;;
        *) echo "unknown arg: $1" >&2; usage; exit 2 ;;
    esac
done

# Refuse to run while Jellyfin is active.
if command -v systemctl >/dev/null 2>&1; then
    if systemctl is-active --quiet jellyfin.service 2>/dev/null; then
        echo "ERROR: jellyfin.service is active. Stop it first (sudo systemctl stop jellyfin)." >&2
        exit 1
    fi
fi
if pgrep -f 'jellyfin' >/dev/null 2>&1; then
    echo "ERROR: a jellyfin process is running (pgrep). Stop it before migrating." >&2
    exit 1
fi

for f in "$PHANTOM_DB" "$JELLYFIN_DB"; do
    if [[ ! -f "$f" ]]; then
        echo "ERROR: missing DB: $f" >&2
        exit 1
    fi
done

if [[ ! -d "$STUB_ROOT" ]]; then
    echo "ERROR: stub root does not exist: $STUB_ROOT" >&2
    exit 1
fi

echo "[migrate-stub-layout-v1] phantom_db=$PHANTOM_DB"
echo "[migrate-stub-layout-v1] jellyfin_db=$JELLYFIN_DB"
echo "[migrate-stub-layout-v1] stub_root=$STUB_ROOT"
echo "[migrate-stub-layout-v1] dry_run=$DRY_RUN"

# Detect BaseItems.Path column existence; jellyfin 10.11 has it.
HAS_PATH_COL=$(sqlite3 "$JELLYFIN_DB" "PRAGMA table_info(BaseItems);" \
    | awk -F'|' '$2=="Path"{print "1"; exit}')
if [[ -z "$HAS_PATH_COL" ]]; then
    echo "ERROR: BaseItems table has no Path column; jellyfin schema unexpected." >&2
    exit 1
fi

migrated=0
skipped_conflict=0
skipped_new=0
failed=0

# List Virtual rows from phantom_items with legacy stub paths.
# Fields: item_guid|tmdb_id|type|stub_path
rows=$(sqlite3 -separator $'\t' "$PHANTOM_DB" \
    "SELECT item_guid, COALESCE(tmdb_id, 0), type, stub_path
       FROM phantom_items
       WHERE state='Virtual'
         AND stub_path IS NOT NULL
         AND stub_path LIKE '%__phantom_tmdb%';")

if [[ -z "$rows" ]]; then
    echo "[migrate-stub-layout-v1] no legacy rows to migrate."
    exit 0
fi

# Reverse-derive title from BaseItem.Name (if not the ugly stem) else
# from the old filename / dirname stem (underscores -> spaces, strip
# __phantom_tmdb<N> suffix).
sanitize_title() {
    # Replace filesystem-hostile chars with spaces, collapse whitespace, trim.
    sed -E 's#[/\\\[\]:*?<>|"]+# #g' | tr -s '[:space:]' ' ' | sed -E 's/^ +| +$//g'
}

while IFS=$'\t' read -r guid tmdb type old_path; do
    [[ -z "$guid" ]] && continue

    # Look up BaseItem name + production year. BaseItems.Id is a BLOB GUID;
    # the plugin stores GUID as 32-char hex with no dashes. Try both forms.
    # The DB stores GUID as raw 16 bytes in jellyfin; the phantom plugin
    # stores as 32-char hex. Convert: e.g. abcdef...
    formatted_guid="${guid:0:8}-${guid:8:4}-${guid:12:4}-${guid:16:4}-${guid:20:12}"
    name=$(sqlite3 "$JELLYFIN_DB" \
        "SELECT COALESCE(Name,'') FROM BaseItems WHERE lower(hex(Id))='$(echo -n $guid | tr 'A-Z' 'a-z')' LIMIT 1;" 2>/dev/null || true)
    year=$(sqlite3 "$JELLYFIN_DB" \
        "SELECT COALESCE(ProductionYear,0) FROM BaseItems WHERE lower(hex(Id))='$(echo -n $guid | tr 'A-Z' 'a-z')' LIMIT 1;" 2>/dev/null || true)

    if [[ -z "$name" || "$name" == *"__phantom_tmdb"* ]]; then
        # Reverse-derive from filename / dirname leaf.
        leaf=$(basename "$old_path")
        leaf="${leaf%.*}"
        # Strip __phantom_tmdbN... suffix.
        stem="${leaf%%__phantom_tmdb*}"
        # Underscores back to spaces.
        name=$(echo "$stem" | tr '_' ' ' | sed -E 's/^ +| +$//g')
    fi

    safe_title=$(echo "$name" | sanitize_title)
    [[ -z "$safe_title" ]] && safe_title="Untitled"

    if [[ "$year" -gt 0 ]]; then
        display="${safe_title} (${year})"
    else
        display="${safe_title}"
    fi

    # Compute new path.
    if [[ "$type" == "series" ]]; then
        old_dir="$old_path"
        parent=$(dirname "$old_dir")
        new_dir="${parent}/${display} [tmdbid-${tmdb}]"
        new_path="$new_dir"

        if [[ "$old_dir" == "$new_dir" ]]; then
            ((skipped_new++)); continue
        fi
        if [[ -e "$new_dir" ]]; then
            echo "[CONFLICT] $old_dir -> $new_dir (destination exists, skipping)"
            ((skipped_conflict++)); continue
        fi

        echo "[MOVE-DIR] $old_dir -> $new_dir"
        if [[ "$DRY_RUN" -eq 0 ]]; then
            if [[ -d "$old_dir" ]]; then
                mv "$old_dir" "$new_dir" || { echo "  [FAIL]"; ((failed++)); continue; }
            else
                echo "  (warning: source dir missing; DB will still be updated)"
            fi
        fi
    else
        # movie
        parent=$(dirname "$old_path")
        ext="${old_path##*.}"
        new_path="${parent}/${display} [tmdbid-${tmdb}].${ext}"

        if [[ "$old_path" == "$new_path" ]]; then
            ((skipped_new++)); continue
        fi
        if [[ -e "$new_path" ]]; then
            echo "[CONFLICT] $old_path -> $new_path (destination exists, skipping)"
            ((skipped_conflict++)); continue
        fi

        echo "[MOVE]     $old_path -> $new_path"
        if [[ "$DRY_RUN" -eq 0 ]]; then
            if [[ -e "$old_path" || -L "$old_path" ]]; then
                mv "$old_path" "$new_path" || { echo "  [FAIL]"; ((failed++)); continue; }
            else
                echo "  (warning: source missing; DB will still be updated)"
            fi
        fi
    fi

    # Update DBs.
    if [[ "$DRY_RUN" -eq 0 ]]; then
        # Escape single quotes in path.
        np_esc=$(printf "%s" "$new_path" | sed "s/'/''/g")
        sqlite3 "$PHANTOM_DB" \
            "UPDATE phantom_items SET stub_path='$np_esc' WHERE item_guid='$guid';" \
            || { echo "  [FAIL phantom_items update]"; ((failed++)); continue; }
        sqlite3 "$JELLYFIN_DB" \
            "UPDATE BaseItems SET Path='$np_esc' WHERE lower(hex(Id))='$(echo -n $guid | tr 'A-Z' 'a-z')';" \
            || { echo "  [FAIL jellyfin.db update]"; ((failed++)); continue; }
    fi
    ((migrated++))
done <<< "$rows"

echo
echo "[migrate-stub-layout-v1] done: migrated=$migrated skipped_new=$skipped_new skipped_conflict=$skipped_conflict failed=$failed"

if [[ "$failed" -gt 0 ]]; then exit 1; fi
exit 0
