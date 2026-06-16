#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# phantom-wipe.sh
#
# WHAT:  One-off operator-side "wipe all phantom-library state and start
#        fresh" script. Removes every BaseItem under the phantom stub root
#        from jellyfin.db (with cascade to all FK-related child tables),
#        renames phantom.db aside so the plugin recreates it on next start,
#        and deletes every on-disk stub under the stub root (preserving
#        `.phantom-library-keep` sentinels and `.splash.*` assets).
#
# WHY:   The v0.2.0.0 stub-layout migration left the operator's libraries
#        in a worse state than they started in (duplicate BaseItems,
#        orphaned phantom rows, partial recovery). Rather than continue
#        iterative repair, this script wipes phantom state cleanly. The
#        plugin's SuggestionsContributor will repopulate from TMDB
#        Trending + per-user Recommended on its next scheduled task tick
#        (or operator-triggered "Phantom Library - refresh suggestions").
#
# WHY NOT IN REPO: operator preference - migration / wipe scripts are
#        operator-side one-offs, not version-controlled artefacts.
#
# CONFIRMATION:  default is dry-run. `--commit` requires you to type
#        EXACTLY the four letters W I P E (case-sensitive, no quotes).
#        Anything else aborts.
#
# RECOVERY:  every `--commit` run takes a timestamped backup of both DBs:
#            <dbdir>/<dbname>.bak.wipe.<UTC-ISO-timestamp>
#        and renames phantom.db to phantom.db.wiped.<ts> (not deleted).
#        To roll back:
#          1. sudo systemctl stop jellyfin
#          2. mv /var/lib/jellyfin/.../phantom.db.wiped.<ts> \
#                /var/lib/jellyfin/.../phantom.db
#          3. cp -p /var/lib/jellyfin/data/jellyfin.db.bak.wipe.<ts> \
#                   /var/lib/jellyfin/data/jellyfin.db
#          4. (re-materialise stub files only if you had real content;
#               for a fresh wipe there is nothing to restore on disk)
#          5. sudo systemctl start jellyfin
#
# OVERRIDES (sandbox testing only - leave unset in prod):
#   JELLYFIN_DB         path to jellyfin.db   (default /var/lib/jellyfin/data/jellyfin.db)
#   PHANTOM_DB          path to phantom.db    (default /var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db)
#   STUB_ROOT           path to stub root     (default /var/lib/jellyfin/phantom-library)
#   JF_ROOT_DEFAULT     path to Jellyfin virtual-library root
#                                             (default /var/lib/jellyfin/root/default)
#   GOSTREAM_ROOT       path to gostream mount root (real files)
#                                             (default /var/gostream)
#   --skip-service-check  bypass the jellyfin-must-be-stopped check
#
# CHANNEL-ARCH NOTE (v0.3.0+): the wipe now also drops:
#   - the gostream-movies and gostream-shows CollectionFolders from jellyfin.db
#     (channel arch replaces them; if left in place the library scanner
#      would re-create them on next start)
#   - the on-disk CollectionFolder marker dirs under JF_ROOT_DEFAULT
#   - every BaseItem rooted under GOSTREAM_ROOT (real gostream content is
#     re-exposed via the channel; the scanner-derived BaseItems must go
#     so the channel owns the IDs)
# The real video files at GOSTREAM_ROOT itself are NOT touched - those
# belong to the gostream service, not to Jellyfin or to this plugin.
#
# Tested against a sandbox clone of the operator's live DBs at
# /tmp/wipe-test/ before being handed to the operator.
# ---------------------------------------------------------------------------

set -euo pipefail

# ---- config (overridable via env for sandbox testing) ---------------------
JELLYFIN_DB="${JELLYFIN_DB:-/var/lib/jellyfin/data/jellyfin.db}"
PHANTOM_DB="${PHANTOM_DB:-/var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db}"
STUB_ROOT="${STUB_ROOT:-/var/lib/jellyfin/phantom-library}"
JF_ROOT_DEFAULT="${JF_ROOT_DEFAULT:-/var/lib/jellyfin/root/default}"
GOSTREAM_ROOT="${GOSTREAM_ROOT:-/var/gostream}"

COMMIT=0
SKIP_SERVICE_CHECK=0

usage() {
    cat <<'EOF'
Usage: phantom-wipe.sh [--commit] [--skip-service-check] [-h|--help]

  (default)             dry-run; computes counts, performs nothing.
  --commit              actually wipe (prompts for "WIPE" confirmation).
  --skip-service-check  bypass jellyfin-must-be-stopped pre-flight
                        (sandbox testing only; NEVER use on prod).
  -h, --help            this help.

Environment overrides (sandbox only):
  JELLYFIN_DB   PHANTOM_DB   STUB_ROOT   JF_ROOT_DEFAULT   GOSTREAM_ROOT
EOF
}

for arg in "$@"; do
    case "$arg" in
        --commit) COMMIT=1 ;;
        --skip-service-check) SKIP_SERVICE_CHECK=1 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "unknown arg: $arg" >&2; usage >&2; exit 2 ;;
    esac
done

TS="$(date -u +%Y%m%dT%H%M%SZ)"

bold()  { printf '\033[1m%s\033[0m\n' "$*"; }
warn()  { printf '\033[33m%s\033[0m\n' "$*" >&2; }
die()   { printf '\033[31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }
info()  { printf '%s\n' "$*"; }

# ---- pre-flight -----------------------------------------------------------

bold "==> Pre-flight"

# 1. Jellyfin must not be running.
if [[ $SKIP_SERVICE_CHECK -eq 0 ]]; then
    if command -v systemctl >/dev/null 2>&1; then
        if systemctl is-active --quiet jellyfin.service 2>/dev/null; then
            die "jellyfin.service is active. Stop it first: sudo systemctl stop jellyfin"
        fi
    fi
    if pgrep -fa '[j]ellyfin' >/dev/null 2>&1; then
        warn "pgrep found a process matching 'jellyfin':"
        pgrep -fa '[j]ellyfin' >&2 || true
        die "Refusing to proceed while Jellyfin processes are alive."
    fi
    info "  jellyfin: stopped (ok)"
else
    warn "  --skip-service-check given; NOT verifying jellyfin is stopped (sandbox only)"
fi

# 2. Target paths exist.
[[ -f "$JELLYFIN_DB" ]] || die "jellyfin.db not found at: $JELLYFIN_DB"
[[ -f "$PHANTOM_DB"  ]] || warn "  phantom.db not at $PHANTOM_DB (already wiped? continuing)"
[[ -d "$STUB_ROOT"   ]] || die "stub root dir not found at: $STUB_ROOT"
info "  jellyfin.db : $JELLYFIN_DB"
info "  phantom.db  : $PHANTOM_DB"
info "  stub root   : $STUB_ROOT"

# 3. SQLite header check.
check_sqlite_header() {
    local f="$1"
    [[ -f "$f" ]] || return 0
    head -c 16 "$f" | grep -q 'SQLite format 3' \
        || die "not a SQLite database: $f"
}
check_sqlite_header "$JELLYFIN_DB"
check_sqlite_header "$PHANTOM_DB"
info "  sqlite header: ok"

# SQLite read helper for jellyfin.db pre-flight reads. Use a plain path,
# not a URI, because the operator host's sqlite3 may not support URI
# filenames or table-valued PRAGMA functions consistently.
JF_RO_URI="$JELLYFIN_DB"

sqlite_col_type() {
    local db="$1" table="$2" col="$3"
    sqlite3 "$db" "PRAGMA table_info('$table');" 2>/dev/null \
        | awk -F'|' -v c="$col" '$2 == c { print $3; exit }'
}

sqlite_cols_csv() {
    local db="$1" table="$2"
    sqlite3 "$db" "PRAGMA table_info('$table');" 2>/dev/null \
        | awk -F'|' '{ print $2 }' \
        | paste -sd, -
}

sqlite_has_col() {
    local db="$1" table="$2" col="$3"
    sqlite3 "$db" "PRAGMA table_info('$table');" 2>/dev/null \
        | awk -F'|' -v c="$col" '$2 == c { print 1; exit }'
}

# 4. Schema probe: BaseItems.Id must be TEXT, BaseItemProviders must
#    have (ItemId, ProviderId, ProviderValue).
ID_TYPE="$(sqlite_col_type "$JF_RO_URI" "BaseItems" "Id")"
[[ "$ID_TYPE" == "TEXT" ]] \
    || die "BaseItems.Id type is '$ID_TYPE', expected TEXT. Schema mismatch - refusing to continue."

BIP_COLS="$(sqlite_cols_csv "$JF_RO_URI" "BaseItemProviders")"
case ",$BIP_COLS," in
    *,ItemId,*) : ;;
    *) die "BaseItemProviders table missing or lacks ItemId column (got: $BIP_COLS)" ;;
esac
case ",$BIP_COLS," in
    *,ProviderId,*) : ;;
    *) die "BaseItemProviders lacks ProviderId column (got: $BIP_COLS)" ;;
esac
case ",$BIP_COLS," in
    *,ProviderValue,*) : ;;
    *) die "BaseItemProviders lacks ProviderValue column (got: $BIP_COLS)" ;;
esac
info "  schema       : BaseItems.Id=TEXT, BaseItemProviders ok"

# 5. Discover every table with an FK to BaseItems.Id (and which column).
#    Stored as "table:col1[,col2]" lines.
declare -a FK_TABLES=()
while IFS= read -r tbl; do
    [[ -z "$tbl" ]] && continue
    cols="$(sqlite3 "$JF_RO_URI" "PRAGMA foreign_key_list('$tbl');" 2>/dev/null \
            | awk -F'|' '$3=="BaseItems" && $5=="Id" {print $4}' \
            | sort -u | paste -sd, -)"
    [[ -z "$cols" ]] && continue
    # Skip BaseItems itself (self-FK on ParentId handled by cascade).
    [[ "$tbl" == "BaseItems" ]] && continue
    FK_TABLES+=("$tbl:$cols")
done < <(sqlite3 "$JF_RO_URI" "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;")

info "  FK-cascade tables (${#FK_TABLES[@]}):"
for entry in "${FK_TABLES[@]}"; do
    info "    - $entry"
done

# Tables that *should* be cleaned but have no declared FK (older Jellyfin
# tables that the migrate script also wipes manually). We only include them
# if they exist AND have an ItemId column.
declare -a EXTRA_TABLES=()
for t in MediaSegments TrickplayInfos; do
    has_col="$(sqlite_has_col "$JF_RO_URI" "$t" "ItemId")"
    if [[ "$has_col" == "1" ]]; then
        EXTRA_TABLES+=("$t:ItemId")
    fi
done
if [[ ${#EXTRA_TABLES[@]} -gt 0 ]]; then
    info "  extra (no-FK) tables to clean:"
    for entry in "${EXTRA_TABLES[@]}"; do
        info "    - $entry"
    done
fi

# 6. Counts.
#
# Channel-arch wipe targets THREE path namespaces in BaseItems:
#   1. STUB_ROOT/%               legacy phantom stub tree (file-on-disk arch)
#   2. JF_ROOT_DEFAULT/gostream-% the two CollectionFolders backing the
#                                old gostream-movies / gostream-shows libraries
#   3. GOSTREAM_ROOT/%           real gostream content scanned via those
#                                CollectionFolders (must clear so the new
#                                IChannel implementation owns the IDs)
# All three are joined as `Path LIKE p1 OR Path LIKE p2 OR Path LIKE p3`
# in every count + delete query below.
STUB_PATH_LIKE="${STUB_ROOT%/}/%"
CF_PATH_LIKE="${JF_ROOT_DEFAULT%/}/gostream-%"
GS_PATH_LIKE="${GOSTREAM_ROOT%/}/%"

# Composable WHERE clause used by all phantom-target queries. We also
# match BaseItems whose Path is EXACTLY the gostream CollectionFolder
# dir (no trailing slash); the `/gostream-%` LIKE already covers them.
PHANTOM_WHERE="Path LIKE '$STUB_PATH_LIKE' OR Path LIKE '$CF_PATH_LIKE' OR Path LIKE '$GS_PATH_LIKE'"

TOTAL_BI="$(sqlite3 "$JF_RO_URI" "SELECT COUNT(*) FROM BaseItems;")"
N_PHANTOM_BI="$(sqlite3 "$JF_RO_URI" "SELECT COUNT(*) FROM BaseItems WHERE $PHANTOM_WHERE;")"
N_STUB_BI="$(sqlite3 "$JF_RO_URI" "SELECT COUNT(*) FROM BaseItems WHERE Path LIKE '$STUB_PATH_LIKE';")"
N_CF_BI="$(sqlite3 "$JF_RO_URI" "SELECT COUNT(*) FROM BaseItems WHERE Path LIKE '$CF_PATH_LIKE';")"
N_GS_BI="$(sqlite3 "$JF_RO_URI" "SELECT COUNT(*) FROM BaseItems WHERE Path LIKE '$GS_PATH_LIKE';")"

if [[ -f "$PHANTOM_DB" ]]; then
    PH_RO_URI="file:${PHANTOM_DB}?mode=ro"
    N_PHANTOM_ROWS="$(sqlite3 "$PH_RO_URI" "SELECT COUNT(*) FROM phantom_items;" 2>/dev/null || echo 0)"
else
    N_PHANTOM_ROWS=0
fi

# Count on-disk stub entries, excluding sentinels and splash files.
count_stubs() {
    local sub="$1"
    [[ -d "$STUB_ROOT/$sub" ]] || { echo 0; return; }
    find "$STUB_ROOT/$sub" -mindepth 1 -maxdepth 1 \
         ! -name '.phantom-library-keep' \
         ! -name '.splash.*' \
         -print 2>/dev/null | wc -l
}
N_FILES_MOVIES="$(count_stubs movies)"
N_FILES_SHOWS="$(count_stubs shows)"
N_FILES=$((N_FILES_MOVIES + N_FILES_SHOWS))

bold "==> Counts"
info "  total BaseItems                       : $TOTAL_BI"
info "  phantom-target BaseItems (to delete)  : $N_PHANTOM_BI"
info "    via STUB_ROOT ($STUB_ROOT/%)        : $N_STUB_BI"
info "    via JF_ROOT_DEFAULT (gostream CFs)  : $N_CF_BI"
info "    via GOSTREAM_ROOT ($GOSTREAM_ROOT/%): $N_GS_BI"
info "  phantom_items rows                    : $N_PHANTOM_ROWS"
info "  stub entries under movies/            : $N_FILES_MOVIES"
info "  stub entries under shows/             : $N_FILES_SHOWS"
info "  stub entries total                    : $N_FILES"

# 7. Sanity bound: phantom BaseItems must be <= 50% of total.
if [[ $TOTAL_BI -gt 0 ]]; then
    # 2 * phantom > total  =>  phantom > total/2
    if (( 2 * N_PHANTOM_BI > TOTAL_BI )); then
        die "SANITY: phantom BaseItems ($N_PHANTOM_BI) exceeds 50% of total ($TOTAL_BI). Refusing."
    fi
fi

# 8. Already-clean detection -> no-op exit.
if [[ $N_PHANTOM_BI -eq 0 && $N_PHANTOM_ROWS -eq 0 && $N_FILES -eq 0 ]]; then
    bold "==> Nothing to do"
    info "  phantom state is already empty (0 BaseItems, 0 phantom_items, 0 stubs)."
    if [[ ! -f "$PHANTOM_DB" ]]; then
        info "  phantom.db not present; plugin will recreate on next start."
    fi
    exit 0
fi

if [[ $COMMIT -eq 0 ]]; then
    bold "==> Dry-run complete"
    info "  Re-run with --commit to actually wipe."
    exit 0
fi

# ---- commit phase ---------------------------------------------------------

bold "==> Confirmation"
printf '  Type EXACTLY  WIPE  to proceed: '
read -r confirm
[[ "$confirm" == "WIPE" ]] || die "confirmation mismatch ('$confirm' != 'WIPE'); aborted."

bold "==> Backups"
JF_BAK="${JELLYFIN_DB}.bak.wipe.${TS}"
PH_BAK="${PHANTOM_DB}.bak.wipe.${TS}"

[[ -e "$JF_BAK" ]] && die "backup already exists: $JF_BAK"
[[ -f "$PHANTOM_DB" && -e "$PH_BAK" ]] && die "backup already exists: $PH_BAK"

cp -p "$JELLYFIN_DB" "$JF_BAK"
info "  jellyfin.db backup: $JF_BAK"
if [[ -f "$PHANTOM_DB" ]]; then
    cp -p "$PHANTOM_DB" "$PH_BAK"
    info "  phantom.db  backup: $PH_BAK"
fi

# ---- jellyfin.db wipe (single transaction) --------------------------------

bold "==> Wiping jellyfin.db (transactional)"

EXPECTED=$((TOTAL_BI - N_PHANTOM_BI))
SQL_FILE="/tmp/.phantom-wipe.${TS}.sql"

# Build SQL: deletes + CHECK-constraint verification + COMMIT, all in one
# sqlite3 invocation under `.bail on` so any error aborts and the open
# transaction auto-rolls-back on process exit. The CHECK constraints on
# the _verify temp table fail the INSERT (and therefore the whole batch)
# if either of:
#   - any phantom BaseItem remains after deletes
#   - the total row count didn't drop by exactly N_PHANTOM_BI
# is true. That guarantees we never COMMIT a wrong-shape result.
{
    echo ".bail on"
    echo "PRAGMA foreign_keys = ON;"
    echo "BEGIN TRANSACTION;"
    echo "CREATE TEMP TABLE _phantom_ids AS"
    echo "  SELECT Id FROM BaseItems WHERE $PHANTOM_WHERE;"
    for entry in "${FK_TABLES[@]}" "${EXTRA_TABLES[@]}"; do
        tbl="${entry%%:*}"
        cols="${entry#*:}"
        IFS=',' read -ra colarr <<< "$cols"
        for c in "${colarr[@]}"; do
            echo "DELETE FROM \"$tbl\" WHERE \"$c\" IN (SELECT Id FROM _phantom_ids);"
        done
    done
    echo "DELETE FROM BaseItems WHERE Id IN (SELECT Id FROM _phantom_ids);"
    # CHECK-constraint verification. Two rows, both must be 0.
    echo "CREATE TEMP TABLE _verify (x INTEGER NOT NULL CHECK (x=0));"
    echo "INSERT INTO _verify VALUES ((SELECT COUNT(*) FROM BaseItems WHERE $PHANTOM_WHERE));"
    echo "INSERT INTO _verify VALUES ((SELECT COUNT(*) FROM BaseItems) - $EXPECTED);"
    echo "DROP TABLE _verify;"
    echo "DROP TABLE _phantom_ids;"
    echo "COMMIT;"
    echo "SELECT 'TOTAL_AFTER:'||(SELECT COUNT(*) FROM BaseItems);"
    echo "SELECT 'PHANTOM_AFTER:'||(SELECT COUNT(*) FROM BaseItems WHERE $PHANTOM_WHERE);"
} > "$SQL_FILE"

set +e
OUTPUT="$(sqlite3 "$JELLYFIN_DB" < "$SQL_FILE" 2>&1)"
RC=$?
set -e
echo "$OUTPUT" | sed 's/^/    /'

if [[ $RC -ne 0 ]]; then
    die "sqlite3 wipe failed (rc=$RC); transaction auto-rolled-back. DB unchanged. SQL saved at $SQL_FILE"
fi

PHANTOM_AFTER_LINE="$(echo "$OUTPUT" | grep -E '^PHANTOM_AFTER:' || true)"
TOTAL_AFTER_LINE="$(echo "$OUTPUT" | grep -E '^TOTAL_AFTER:' || true)"
PHANTOM_AFTER="${PHANTOM_AFTER_LINE#PHANTOM_AFTER:}"
TOTAL_AFTER="${TOTAL_AFTER_LINE#TOTAL_AFTER:}"

[[ "$PHANTOM_AFTER" == "0" ]] \
    || die "post-commit re-check: still $PHANTOM_AFTER phantom BaseItems. Backup at $JF_BAK"
[[ "$TOTAL_AFTER" == "$EXPECTED" ]] \
    || die "post-commit total mismatch ($TOTAL_AFTER != $EXPECTED). Backup at $JF_BAK"

info "  jellyfin.db: committed. BaseItems $TOTAL_BI -> $TOTAL_AFTER (-$N_PHANTOM_BI)"
rm -f "$SQL_FILE"

# ---- phantom.db rename ----------------------------------------------------

bold "==> Wiping phantom.db (rename-aside)"
if [[ -f "$PHANTOM_DB" ]]; then
    PH_WIPED="${PHANTOM_DB}.wiped.${TS}"
    [[ -e "$PH_WIPED" ]] && die "wiped-aside path already exists: $PH_WIPED"
    # Also move the WAL/SHM sidecars if present so the renamed DB stays
    # internally consistent (and so the plugin doesn't re-attach to them).
    mv "$PHANTOM_DB" "$PH_WIPED"
    for sidecar in "${PHANTOM_DB}-wal" "${PHANTOM_DB}-shm"; do
        [[ -f "$sidecar" ]] && mv "$sidecar" "${sidecar}.wiped.${TS}"
    done
    info "  phantom.db moved -> $PH_WIPED"
    info "  (plugin will recreate the schema on next start via PhantomDb.EnsureSchema)"
else
    info "  phantom.db not present; nothing to rename."
fi

# ---- stub directories on disk --------------------------------------------

bold "==> Wiping stub directories on disk"

wipe_subdir() {
    local sub="$1"
    local dir="$STUB_ROOT/$sub"
    [[ -d "$dir" ]] || { info "  $sub: (no such dir, skipping)"; return; }
    local before
    before="$(find "$dir" -mindepth 1 -maxdepth 1 \
              ! -name '.phantom-library-keep' \
              ! -name '.splash.*' \
              -print 2>/dev/null | wc -l)"
    # -depth ensures children are removed before parents.
    find "$dir" -mindepth 1 -maxdepth 1 \
        ! -name '.phantom-library-keep' \
        ! -name '.splash.*' \
        -exec rm -rf -- {} +
    local after
    after="$(find "$dir" -mindepth 1 -maxdepth 1 \
             ! -name '.phantom-library-keep' \
             ! -name '.splash.*' \
             -print 2>/dev/null | wc -l)"
    if [[ "$after" != "0" ]]; then
        die "stub dir $dir still has $after non-sentinel entries after wipe."
    fi
    info "  $sub: removed $before entries (now empty except sentinels)"
}

wipe_subdir movies
wipe_subdir shows

# Channel-arch additional cleanup: remove the on-disk CollectionFolder
# marker dirs at JF_ROOT_DEFAULT/gostream-{movies,shows}. Jellyfin's
# library scanner re-creates a CollectionFolder BaseItem on next start
# for any subdir of /var/lib/jellyfin/root/default/, so leaving these
# behind would undo the BaseItems delete on the very next scan tick.
# We do NOT touch GOSTREAM_ROOT itself - those are real video files
# owned by the gostream service.
bold "==> Removing on-disk gostream CollectionFolder marker dirs"
for sub in gostream-movies gostream-shows; do
    cfdir="$JF_ROOT_DEFAULT/$sub"
    if [[ -d "$cfdir" ]]; then
        # Defensive: refuse if it contains anything that looks like a
        # real video file. CollectionFolder marker dirs normally hold
        # only .collection metadata + options.xml.
        bad="$(find "$cfdir" -type f \
               \( -iname '*.mkv' -o -iname '*.mp4' -o -iname '*.m4v' \
               -o -iname '*.avi' -o -iname '*.mov' -o -iname '*.ts' \
               -o -iname '*.webm' \) -print -quit 2>/dev/null || true)"
        if [[ -n "$bad" ]]; then
            die "unexpected video file under CollectionFolder dir $cfdir ($bad); refusing to rm -rf. Inspect manually."
        fi
        rm -rf -- "$cfdir"
        info "  removed: $cfdir"
    else
        info "  (skip) $cfdir does not exist"
    fi
done

# ---- post-wipe verification ----------------------------------------------

bold "==> Post-wipe verification"
info "  phantom-target BaseItems       : $(sqlite3 "$JF_RO_URI" "SELECT COUNT(*) FROM BaseItems WHERE $PHANTOM_WHERE;")"
if [[ -f "$PHANTOM_DB" ]]; then
    info "  phantom_items rows           : $(sqlite3 "file:${PHANTOM_DB}?mode=ro" "SELECT COUNT(*) FROM phantom_items;" 2>/dev/null || echo '(unreadable)')"
else
    info "  phantom_items rows           : 0 (phantom.db moved aside; plugin will recreate)"
fi
info "  stub entries under movies/   : $(count_stubs movies)"
info "  stub entries under shows/    : $(count_stubs shows)"
info ""
bold "  Backups:"
info "    $JF_BAK"
[[ -f "$PH_BAK" ]] && info "    $PH_BAK"
[[ -n "${PH_WIPED:-}" ]] && info "    phantom.db moved aside at: $PH_WIPED"

bold "==> Next operator steps"
cat <<'EOF'
  1. Deploy the patched Jellyfin assemblies if you haven't already
     (see docs/operator-deploy.md). The plugin DLL alone is not
     enough - it references types added by the patches.
  2. sudo systemctl start jellyfin
  3. Wait for Jellyfin to come up (web UI responsive).
  4. Dashboard -> Plugins -> Phantom Library -> Settings; confirm
     gostream paths; click Save.
  5. Dashboard -> Scheduled Tasks -> run
       "Phantom Library: Discovery Refresh"
     (populates the channel item lists from TMDB Trending +
      per-user Recommended).
  6. Refresh the browser; "Phantom Movies" and "Phantom Shows"
     tiles should appear in your library nav.
  7. Smoke-test: click a phantom item, play (splash plays),
     kebab -> Materialise, wait for toast, play again -> real
     file streams.
  8. Keep the .bak.wipe.* backups until you've confirmed at
     least one normal usage cycle. Then they can be removed.
EOF
