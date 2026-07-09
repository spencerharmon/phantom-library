#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# phantom-migrate-v11-to-v12.sh
#
# WHAT:  Out-of-band, operator-run, ADDITIVE-ONLY schema migration for
#        phantom.db from schema v11 to schema v12. v12 adds exactly the two
#        per-user tables `user_prefs` and `user_hidden_items` (+ the index
#        `idx_user_hidden_items_user`) — REQ-M14-PER-USER (branch B). It
#        touches NO existing table, deletes nothing, and rewrites no row.
#        The concrete instance of the `phantom-migrate-vN-to-vM.sh` pattern
#        with N=11, M=12 (see the naming note below).
#
# WHY:   Before this script, the ONLY pre-v1.0 upgrade path was wipe-and-
#        rebuild (`scripts/phantom-wipe.sh`), which discards all phantom
#        state and forces a full TMDB re-discovery. The v11->v12 delta is
#        purely additive (two brand-new, initially-empty tables), so a
#        narrowly-scoped, heavily-guarded, offline additive migration can
#        bring an existing v11 DB to v12 WITHOUT a wipe and without losing
#        any accumulated catalogue / availability / materialised state.
#        This is the "softened additive-migration rule" (AGENTS.md
#        § "No database migrations until v1.0"): it is NOT a general
#        migration framework — it is one specific, additive, tested v->v
#        step, run out-of-band with Jellyfin stopped.
#
# WHY THIS IS SAFE (and how it stays inside the rule):
#   - ADDITIVE-ONLY. The apply phase is exactly:
#         CREATE TABLE IF NOT EXISTS user_prefs (...);
#         CREATE TABLE IF NOT EXISTS user_hidden_items (...);
#         CREATE INDEX IF NOT EXISTS idx_user_hidden_items_user ON ...;
#         PRAGMA user_version = 12;
#     There is NO ALTER, NO DELETE, NO UPDATE, NO INSERT into any existing
#     table, and NO row rewrite. By construction it cannot mutate, drop, or
#     re-key any pre-existing table — so it is immune to the wrong-schema /
#     wrong-column / wrong-row-target failure modes that motivated the
#     no-migration rule (see AGENTS.md post-mortems).
#   - user_version-GUARDED. Runs ONLY when the DB is exactly at v11 (or is
#     already at v12, in which case it is a verified no-op). Any other
#     version — v10, v0/fresh, a future v13 — is HARD-REFUSED with a pointer
#     to wipe. It will never "guess" a migration path.
#   - DRY-RUN by default. It computes and prints the full plan and the
#     predicted-before / predicted-after counts and DOES NOTHING unless you
#     pass --commit (which additionally prompts for a typed MIGRATE
#     confirmation).
#   - BACKUP FIRST. Every --commit run takes a timestamped backup of
#     phantom.db (and its -wal/-shm sidecars if present) BEFORE touching it,
#     mirroring scripts/phantom-wipe.sh.
#   - ATOMIC. The DDL + the user_version bump run in a single SQLite
#     transaction under `.bail on`, so the DB is either fully v12 or wholly
#     unchanged at v11 — never a torn half-state.
#   - IDEMPOTENT + RESUMABLE. Re-running after a completed migration is a
#     verified no-op (guard sees v12). Re-running after an interrupted
#     attempt (tables partially created but the version not yet bumped, i.e.
#     still v11) completes cleanly because every DDL statement is
#     `IF NOT EXISTS` and the version bump is last inside the transaction.
#   - PREDICTED-BEFORE / ACTUAL-AFTER. It records every pre-existing table's
#     row count before, and asserts each is byte-for-byte identical after,
#     that the two new tables exist and are empty, and that user_version is
#     now 12 — refusing to declare success otherwise.
#
# TESTED: regression-tested in-repo against a CLONE of a synthetic v11 DB by
#         scripts/tests/phantom-migrate-v11-to-v12.test.sh (build + unit-test
#         only, no live Jellyfin). The live-rig proof on a real-shaped DB is
#         the separate `migration-rig` task.
#
# NAMING: the task names this `phantom-migrate-vN-to-vM.sh`; that is the
#         template form. This file is the concrete, honest instance for the
#         only migration that currently exists (N=11, M=12). A future schema
#         bump ships its own `phantom-migrate-v12-to-v13.sh` re-using this
#         exact shape — one specific additive step per file, never a
#         placeholder-named general migrator.
#
# RECOVERY: to roll back a committed migration:
#         1. sudo systemctl stop jellyfin
#         2. cp -p <phantom.db>.bak.migrate.<ts>  <phantom.db>
#            (and restore the .bak.migrate.<ts> -wal/-shm sidecars if any)
#         3. sudo systemctl start jellyfin
#         Because the migration is additive, "rolling back" is only needed
#         if you also downgrade the plugin below v12; a v12 plugin is happy
#         with the migrated DB.
#
# OVERRIDES (sandbox / rig testing only — leave unset in prod):
#   PHANTOM_DB            path to phantom.db
#                         (default /var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db)
#   --skip-service-check  bypass the jellyfin-must-be-stopped pre-flight
#                         (sandbox/rig only; NEVER on prod)
# ---------------------------------------------------------------------------

set -euo pipefail

# ---- migration identity ---------------------------------------------------
FROM_VERSION=11
TO_VERSION=12

# ---- config (overridable via env for sandbox testing) ---------------------
PHANTOM_DB="${PHANTOM_DB:-/var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db}"

COMMIT=0
SKIP_SERVICE_CHECK=0

usage() {
    cat <<EOF
Usage: phantom-migrate-v11-to-v12.sh [--commit] [--skip-service-check] [-h|--help]

  Additive-only offline migration of phantom.db from schema v${FROM_VERSION}
  to schema v${TO_VERSION} (adds the per-user tables user_prefs and
  user_hidden_items; touches no existing table).

  (default)             dry-run; computes + prints the plan and counts,
                        changes nothing.
  --commit              actually migrate (prompts for a typed MIGRATE
                        confirmation; takes a timestamped backup first).
  --skip-service-check  bypass the jellyfin-must-be-stopped pre-flight
                        (sandbox/rig testing only; NEVER use on prod).
  -h, --help            this help.

Environment overrides (sandbox only):
  PHANTOM_DB   path to phantom.db
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

# 1. Jellyfin must not be running (offline migration; no scanner/watcher race).
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

# 2. Target DB exists.
[[ -f "$PHANTOM_DB" ]] || die "phantom.db not found at: $PHANTOM_DB"
info "  phantom.db : $PHANTOM_DB"

# 3. SQLite header check.
head -c 16 "$PHANTOM_DB" | grep -q 'SQLite format 3' \
    || die "not a SQLite database: $PHANTOM_DB"
info "  sqlite header: ok"

# 4. user_version guard: ONLY v11 (migrate) or v12 (already-done no-op).
VERSION="$(sqlite3 "$PHANTOM_DB" 'PRAGMA user_version;' 2>/dev/null || echo '?')"
info "  user_version : $VERSION"

# Exact DDL identifiers we manage (kept in sync with
# src/Jellyfin.Plugin.PhantomLibrary/State/PhantomDb.cs SchemaV10Sql, the v12
# additive block). If PhantomDb.cs changes these, update this script AND its
# regression test together.
NEW_TABLES=(user_prefs user_hidden_items)
NEW_INDEX=idx_user_hidden_items_user

table_exists() {
    local t="$1"
    [[ "$(sqlite3 "$PHANTOM_DB" \
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='$t';")" == "1" ]]
}
index_exists() {
    local i="$1"
    [[ "$(sqlite3 "$PHANTOM_DB" \
        "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='$i';")" == "1" ]]
}

if [[ "$VERSION" == "$TO_VERSION" ]]; then
    # Already migrated. Verify the v12 objects are actually present, then
    # exit 0 as a no-op (idempotency: a completed migration re-runs clean).
    bold "==> Already at v${TO_VERSION}"
    missing=0
    for t in "${NEW_TABLES[@]}"; do
        if table_exists "$t"; then
            info "  table present: $t"
        else
            warn "  table MISSING at v${TO_VERSION}: $t"
            missing=1
        fi
    done
    if index_exists "$NEW_INDEX"; then
        info "  index present: $NEW_INDEX"
    else
        warn "  index MISSING at v${TO_VERSION}: $NEW_INDEX"
        missing=1
    fi
    if [[ $missing -ne 0 ]]; then
        die "DB reports v${TO_VERSION} but is missing v${TO_VERSION} objects. Do NOT hand-edit user_version. Restore from backup or wipe (scripts/phantom-wipe.sh)."
    fi
    info "  nothing to do; phantom.db is already at schema v${TO_VERSION}."
    exit 0
fi

if [[ "$VERSION" != "$FROM_VERSION" ]]; then
    die "refuse: user_version=$VERSION. This script migrates ONLY v${FROM_VERSION} -> v${TO_VERSION}.
       For any other version (fresh/v0, older, or newer) the supported pre-v1.0
       path is wipe-and-rebuild: stop Jellyfin, run
       'sudo bash scripts/phantom-wipe.sh --commit', then restart."
fi

# ---- prediction (before-counts) -------------------------------------------

# Enumerate every pre-existing table and record its row count. The additive
# migration must leave every one of these identical.
mapfile -t BEFORE_TABLES < <(sqlite3 "$PHANTOM_DB" \
    "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;")

declare -A BEFORE_COUNT=()
for t in "${BEFORE_TABLES[@]}"; do
    BEFORE_COUNT["$t"]="$(sqlite3 "$PHANTOM_DB" "SELECT COUNT(*) FROM \"$t\";")"
done

# Detect a resumed / partial prior attempt (still v11 but a v12 table already
# present). This is a legitimate resumable state, not an error.
PARTIAL=0
for t in "${NEW_TABLES[@]}"; do
    if table_exists "$t"; then
        PARTIAL=1
    fi
done

bold "==> Plan (v${FROM_VERSION} -> v${TO_VERSION}, additive-only)"
info "  will CREATE TABLE IF NOT EXISTS : ${NEW_TABLES[*]}"
info "  will CREATE INDEX IF NOT EXISTS : ${NEW_INDEX}"
info "  will bump user_version          : ${FROM_VERSION} -> ${TO_VERSION}"
info "  existing tables (preserved unchanged): ${#BEFORE_TABLES[@]}"
if [[ $PARTIAL -eq 1 ]]; then
    warn "  note: a v${TO_VERSION} table already exists at user_version=${FROM_VERSION}"
    warn "        (resuming an interrupted prior run; IF NOT EXISTS makes this safe)."
fi

bold "==> Predicted counts"
info "  per-table row counts (must be identical after):"
for t in "${BEFORE_TABLES[@]}"; do
    printf '    %-28s %s\n' "$t" "${BEFORE_COUNT[$t]}"
done
info "  after migration, additionally:"
for t in "${NEW_TABLES[@]}"; do
    printf '    %-28s %s\n' "$t" "0 (new, empty)"
done

if [[ $COMMIT -eq 0 ]]; then
    bold "==> Dry-run complete"
    info "  Re-run with --commit to actually migrate."
    exit 0
fi

# ---- commit phase ---------------------------------------------------------

bold "==> Confirmation"
printf '  Type EXACTLY  MIGRATE  to proceed: '
read -r confirm
[[ "$confirm" == "MIGRATE" ]] || die "confirmation mismatch ('$confirm' != 'MIGRATE'); aborted."

bold "==> Backup"
PH_BAK="${PHANTOM_DB}.bak.migrate.${TS}"
[[ -e "$PH_BAK" ]] && die "backup already exists: $PH_BAK"
cp -p "$PHANTOM_DB" "$PH_BAK"
info "  phantom.db backup: $PH_BAK"
# Preserve WAL/SHM sidecars alongside the backup so a restore is consistent.
for sidecar in "${PHANTOM_DB}-wal" "${PHANTOM_DB}-shm"; do
    if [[ -f "$sidecar" ]]; then
        cp -p "$sidecar" "${sidecar}.bak.migrate.${TS}"
        info "  sidecar backup   : ${sidecar}.bak.migrate.${TS}"
    fi
done

# ---- apply (single atomic transaction) ------------------------------------

bold "==> Applying additive migration (transactional)"

SQL_FILE="$(mktemp "${TMPDIR:-/tmp}/.phantom-migrate.${TS}.XXXXXX.sql")"
trap 'rm -f "$SQL_FILE"' EXIT

# The additive DDL below MUST match PhantomDb.cs SchemaV10Sql's v12 block
# verbatim so a migrated DB is schema-identical to a freshly-created v12 DB.
{
    echo ".bail on"
    echo "BEGIN TRANSACTION;"
    cat <<'DDL'
CREATE TABLE IF NOT EXISTS user_prefs (
    user_id            TEXT NOT NULL PRIMARY KEY,   -- Jellyfin user GUID (canonical string form)
    protect_favourites INTEGER NOT NULL DEFAULT 1 CHECK(protect_favourites IN (0,1)),
    show_phantoms      INTEGER NOT NULL DEFAULT 1 CHECK(show_phantoms IN (0,1)),
    allow_eager        INTEGER NOT NULL DEFAULT 1 CHECK(allow_eager IN (0,1)),
    updated_at         INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS user_hidden_items (
    user_id   TEXT NOT NULL,
    tmdb_id   INTEGER NOT NULL,
    type      TEXT NOT NULL CHECK(type IN ('movie','series')),
    hidden_at INTEGER NOT NULL,
    PRIMARY KEY (user_id, tmdb_id, type)
);
CREATE INDEX IF NOT EXISTS idx_user_hidden_items_user
    ON user_hidden_items(user_id);
DDL
    echo "PRAGMA user_version = ${TO_VERSION};"
    echo "COMMIT;"
    echo "SELECT 'USER_VERSION_AFTER:'||(SELECT user_version FROM pragma_user_version);"
} > "$SQL_FILE"

set +e
OUTPUT="$(sqlite3 "$PHANTOM_DB" < "$SQL_FILE" 2>&1)"
RC=$?
set -e
echo "$OUTPUT" | sed 's/^/    /'

if [[ $RC -ne 0 ]]; then
    die "sqlite3 migration failed (rc=$RC); transaction auto-rolled-back. DB unchanged at v${FROM_VERSION}. Backup at $PH_BAK"
fi

# ---- post-migration verification ------------------------------------------

bold "==> Verification (actual-after)"

VERSION_AFTER="$(sqlite3 "$PHANTOM_DB" 'PRAGMA user_version;')"
[[ "$VERSION_AFTER" == "$TO_VERSION" ]] \
    || die "post-migration user_version is $VERSION_AFTER, expected $TO_VERSION. Backup at $PH_BAK"
info "  user_version : ${FROM_VERSION} -> ${VERSION_AFTER}"

# New tables present, correct shape, and EMPTY.
for t in "${NEW_TABLES[@]}"; do
    table_exists "$t" || die "expected table '$t' missing after migration. Backup at $PH_BAK"
    n="$(sqlite3 "$PHANTOM_DB" "SELECT COUNT(*) FROM \"$t\";")"
    [[ "$n" == "0" ]] || die "new table '$t' has $n rows, expected 0. Backup at $PH_BAK"
    info "  new table    : $t (0 rows)"
done
index_exists "$NEW_INDEX" || die "expected index '$NEW_INDEX' missing after migration. Backup at $PH_BAK"
info "  new index    : $NEW_INDEX"

# Shape assertions for user_prefs: 5 columns, user_id sole PK.
UP_COLS="$(sqlite3 "$PHANTOM_DB" "SELECT COUNT(*) FROM pragma_table_info('user_prefs');")"
[[ "$UP_COLS" == "5" ]] || die "user_prefs has $UP_COLS columns, expected 5. Backup at $PH_BAK"
UP_PK="$(sqlite3 "$PHANTOM_DB" "SELECT name FROM pragma_table_info('user_prefs') WHERE pk>0 ORDER BY pk;" | paste -sd, -)"
[[ "$UP_PK" == "user_id" ]] || die "user_prefs PK is '$UP_PK', expected 'user_id'. Backup at $PH_BAK"

# Shape assertions for user_hidden_items: composite PK (user_id,tmdb_id,type).
UHI_PK="$(sqlite3 "$PHANTOM_DB" "SELECT name FROM pragma_table_info('user_hidden_items') WHERE pk>0 ORDER BY pk;" | paste -sd, -)"
[[ "$UHI_PK" == "user_id,tmdb_id,type" ]] \
    || die "user_hidden_items PK is '$UHI_PK', expected 'user_id,tmdb_id,type'. Backup at $PH_BAK"
info "  shapes       : user_prefs(PK user_id), user_hidden_items(PK user_id,tmdb_id,type) ok"

# Every pre-existing table's row count is byte-for-byte identical.
DRIFT=0
for t in "${BEFORE_TABLES[@]}"; do
    after="$(sqlite3 "$PHANTOM_DB" "SELECT COUNT(*) FROM \"$t\";")"
    if [[ "$after" != "${BEFORE_COUNT[$t]}" ]]; then
        warn "  DRIFT: $t was ${BEFORE_COUNT[$t]}, now $after"
        DRIFT=1
    fi
done
if [[ $DRIFT -ne 0 ]]; then
    die "an existing table's row count changed — additive migration must not. Restore from backup: $PH_BAK"
fi
info "  existing data: all ${#BEFORE_TABLES[@]} pre-existing tables unchanged"

bold "==> Migration complete"
info "  phantom.db is now at schema v${TO_VERSION}."
info "  Backup retained at: $PH_BAK"
info ""
bold "==> Next operator steps"
cat <<EOF
  1. sudo systemctl start jellyfin
  2. Confirm the plugin starts cleanly (no "schema is at version" refusal in
     the Jellyfin log). The v${TO_VERSION} plugin now reads the migrated DB
     in place with all prior phantom state intact.
  3. Keep the .bak.migrate.${TS} backup until you have confirmed at least one
     normal usage cycle; then it can be removed.
EOF
