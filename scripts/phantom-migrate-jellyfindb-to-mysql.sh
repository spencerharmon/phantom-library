#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# phantom-migrate-jellyfindb-to-mysql.sh
#
# WHAT:  Out-of-band, operator-run migration of Jellyfin's AUTHORITATIVE
#        library/user database off the per-color SQLite `jellyfin.db` onto a
#        shared MySQL / MariaDB instance served through the
#        `jellyfin-plugin-mysql` EF Core provider. This is P4 Stage A: it
#        lets N Jellyfin replicas (the blue/green "colors") share ONE
#        authoritative store instead of each carrying its own SQLite file.
#
#        This script is the `jellyfin.db -> MySQL` analogue of the existing
#        `scripts/phantom-migrate-v11-to-v12.sh` operator-migration contract:
#        offline, dry-run-by-default, backed-up, predicted-before /
#        actual-after count-verified, idempotent, and hard-guarded. It carries
#        NO data-mutating SQL against a live DB in the default path.
#
# WHY MYSQL (not shared SQLite):
#        Per `docs/tasks/p4-phantomdb-multiwriter-audit.md`, a single SQLite
#        file shared read-write across multiple replica processes/hosts is
#        explicitly unsafe (broken advisory locking on network filesystems,
#        WAL shared-memory that does not span hosts, immediate SQLITE_BUSY).
#        The ONLY correct way to let real replicas share the authoritative
#        Jellyfin store is a genuine multi-writer engine. `jellyfin-plugin-mysql`
#        points Jellyfin's EF Core context at MySQL/MariaDB, which IS such an
#        engine. Migrating `jellyfin.db` onto MySQL is therefore the store-level
#        prerequisite for the multi-replica P4 topology.
#
# THE P3 FIVE-STAGE STAGING-VALIDATION METHODOLOGY (never a bespoke shortcut):
#        This migration follows the SAME five-stage staging discipline P3 used,
#        so a real prod cutover is only ever reached after the exact same data
#        has been proven to load correctly out-of-band. The stages are:
#
#          1. CLONE            — snapshot the ACTIVE color's live `jellyfin.db`
#                                to an offline staging clone. The live file is
#                                never read destructively and never written.
#          2. PREDICTED COUNTS — enumerate every data table in the clone and
#                                record its exact row count. These predicted
#                                counts are the contract every later stage must
#                                reproduce byte-for-byte.
#          3. STAGING VALIDATION ON THE INACTIVE COLOR
#                              — load the clone's data into the INACTIVE color's
#                                MySQL schema (created by jellyfin-plugin-mysql)
#                                and assert MySQL's actual per-table counts equal
#                                the stage-2 predictions. The inactive color is
#                                the safe rehearsal target: nothing user-facing
#                                points at it yet.
#          4. OPERATOR HAND-VALIDATION
#                              — print the full predicted-vs-actual report and
#                                STOP for the operator to hand-validate the
#                                inactive color (log in, browse the library,
#                                confirm users/watch-state) before any prod
#                                write. `--commit` additionally requires a typed
#                                MIGRATE confirmation.
#          5. PROD WRITE       — only after 1-4 pass and the operator confirms:
#                                load the SAME validated data set into the
#                                shared/prod MySQL DB and re-verify counts. The
#                                operator then flips jellyfin-plugin-mysql's
#                                connection string live (next steps printed).
#
# WHY THIS IS SAFE:
#   - NON-DESTRUCTIVE TO THE SOURCE. The active `jellyfin.db` is only ever
#     copied (stage 1), never altered. A failed migration leaves prod exactly
#     as it was; the operator just does not flip the connection string.
#   - DRY-RUN BY DEFAULT. With neither --stage nor --commit the script clones,
#     computes predicted counts, generates the MySQL load set, prints the plan,
#     and writes NOTHING to any MySQL DB.
#   - STAGE-GATED. A prod write (--commit) is REFUSED unless staging validation
#     (--stage) has been run and passed in the same staging dir (a
#     `.staging-validated` receipt is required), so you cannot skip the inactive-
#     color rehearsal.
#   - COUNT-VERIFIED AT EVERY LOAD. After every MySQL load the script queries
#     MySQL's actual per-table counts and refuses to proceed on ANY drift from
#     the stage-2 predictions.
#   - IDEMPOTENT. Each table's load set is prefixed with a scoped DELETE so a
#     re-run converges to the same rows; re-running staging or prod is safe.
#   - BACKS UP FIRST. A --commit prod write dumps the prod MySQL target's
#     current contents to a timestamped mysqldump artifact before loading.
#
# NOT A GENERAL MIGRATOR. This is one specific, tested, offline store migration
# (SQLite jellyfin.db -> MySQL), run with every replica's Jellyfin stopped. It
# does not evolve schema; jellyfin-plugin-mysql owns the MySQL schema (it runs
# the EF Core migrations that create the tables). This script only COPIES the
# authoritative row data into that schema and proves the copy.
#
# TESTED: regression-tested in-repo by
#         scripts/tests/phantom-migrate-jellyfindb-to-mysql.test.sh against a
#         synthetic jellyfin.db and a SQLite-backed MySQL stand-in (bash +
#         sqlite3 only, no live server). That test proves the clone, predicted
#         counts, load-set generation, count-parity validation, stage gating,
#         and confirmation gate. The live proof against a real MySQL/MariaDB and
#         a real jellyfin-plugin-mysql cutover is the separate operator live-rig
#         step (mirroring how phantom-migrate-v11-to-v12 defers its live-rig
#         proof to a dedicated rig task).
#
# CONNECTION CONFIG (env; the MySQL creds are NEVER passed on the CLI):
#   JELLYFIN_DB              source SQLite path
#                            (default /var/lib/jellyfin/data/jellyfin.db)
#   STAGING_DIR              working dir for the clone + load set + receipts
#                            (default $TMPDIR/phantom-jellyfindb-mysql.<ts>)
#   Inactive color (stage 3 rehearsal target):
#     MYSQL_STAGING_HOST     default 127.0.0.1
#     MYSQL_STAGING_PORT     default 3306
#     MYSQL_STAGING_USER     default jellyfin
#     MYSQL_STAGING_PASSWORD (no default; read from env or MYSQL_STAGING_PASSWORD_FILE)
#     MYSQL_STAGING_DB       default jellyfin_inactive
#   Prod / shared color (stage 5 cutover target):
#     MYSQL_PROD_HOST        default 127.0.0.1
#     MYSQL_PROD_PORT        default 3306
#     MYSQL_PROD_USER        default jellyfin
#     MYSQL_PROD_PASSWORD    (no default; read from env or MYSQL_PROD_PASSWORD_FILE)
#     MYSQL_PROD_DB          default jellyfin
#   Tool overrides (sandbox/rig/testing only):
#     MYSQL_CMD              mysql client argv[0] (default: mysql)
#     MYSQLDUMP_CMD          mysqldump argv[0]    (default: mysqldump)
#     SQLITE_CMD             sqlite3 argv[0]      (default: sqlite3)
#
# FLAGS:
#   (default)               DRY-RUN: stages 1+2, generate load set, print plan.
#   --stage                 run stage 3 (load + validate on the INACTIVE color),
#                           then stop at stage 4 (operator hand-validation).
#   --commit                run stage 5 (prod write); requires a prior passing
#                           --stage in the same STAGING_DIR + a typed MIGRATE.
#   --tables a,b,c          restrict to these tables (default: all data tables).
#   --skip-service-check    bypass the jellyfin-must-be-stopped pre-flight
#                           (sandbox/rig only; NEVER on prod).
#   -h, --help              this help.
# ---------------------------------------------------------------------------

set -euo pipefail

# ---- config ---------------------------------------------------------------
JELLYFIN_DB="${JELLYFIN_DB:-/var/lib/jellyfin/data/jellyfin.db}"

MYSQL_STAGING_HOST="${MYSQL_STAGING_HOST:-127.0.0.1}"
MYSQL_STAGING_PORT="${MYSQL_STAGING_PORT:-3306}"
MYSQL_STAGING_USER="${MYSQL_STAGING_USER:-jellyfin}"
MYSQL_STAGING_DB="${MYSQL_STAGING_DB:-jellyfin_inactive}"

MYSQL_PROD_HOST="${MYSQL_PROD_HOST:-127.0.0.1}"
MYSQL_PROD_PORT="${MYSQL_PROD_PORT:-3306}"
MYSQL_PROD_USER="${MYSQL_PROD_USER:-jellyfin}"
MYSQL_PROD_DB="${MYSQL_PROD_DB:-jellyfin}"

MYSQL_CMD="${MYSQL_CMD:-mysql}"
MYSQLDUMP_CMD="${MYSQLDUMP_CMD:-mysqldump}"
SQLITE_CMD="${SQLITE_CMD:-sqlite3}"

TS="$(date -u +%Y%m%dT%H%M%SZ)"
STAGING_DIR="${STAGING_DIR:-${TMPDIR:-/tmp}/phantom-jellyfindb-mysql.${TS}}"

# EF Core / provider-internal tables that jellyfin-plugin-mysql owns and must
# NOT be copied from the SQLite provider (migration history is provider-specific).
SKIP_TABLES_DEFAULT="__EFMigrationsHistory"

MODE="dryrun"          # dryrun | stage | commit
SKIP_SERVICE_CHECK=0
TABLES_ARG=""

bold()  { printf '\033[1m%s\033[0m\n' "$*"; }
warn()  { printf '\033[33m%s\033[0m\n' "$*" >&2; }
die()   { printf '\033[31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }
info()  { printf '%s\n' "$*"; }

usage() {
    sed -n '2,140p' "$0" | sed 's/^# \{0,1\}//'
    exit 0
}

# ---- args -----------------------------------------------------------------
while [[ $# -gt 0 ]]; do
    case "$1" in
        --stage)              MODE="stage" ;;
        --commit)             MODE="commit" ;;
        --tables)             shift; TABLES_ARG="${1:-}"; [[ -n "$TABLES_ARG" ]] || die "--tables needs a value" ;;
        --tables=*)           TABLES_ARG="${1#--tables=}" ;;
        --skip-service-check) SKIP_SERVICE_CHECK=1 ;;
        -h|--help)            usage ;;
        *) die "unknown arg: $1 (see --help)" ;;
    esac
    shift
done

# ---- resolve MySQL passwords from *_FILE if given -------------------------
if [[ -z "${MYSQL_STAGING_PASSWORD:-}" && -n "${MYSQL_STAGING_PASSWORD_FILE:-}" ]]; then
    MYSQL_STAGING_PASSWORD="$(cat "$MYSQL_STAGING_PASSWORD_FILE")"
fi
if [[ -z "${MYSQL_PROD_PASSWORD:-}" && -n "${MYSQL_PROD_PASSWORD_FILE:-}" ]]; then
    MYSQL_PROD_PASSWORD="$(cat "$MYSQL_PROD_PASSWORD_FILE")"
fi

# ---- MySQL invocation helpers ---------------------------------------------
# Build a mysql client argv for a given color. Credentials come from env only,
# never the process CLI (defence against ps(1) credential leak) — password is
# passed via MYSQL_PWD in the child environment.
mysql_run() {   # mysql_run <color> [extra mysql args...]  ; SQL on stdin
    local color="$1"; shift
    local host port user pw db
    if [[ "$color" == "staging" ]]; then
        host="$MYSQL_STAGING_HOST"; port="$MYSQL_STAGING_PORT"
        user="$MYSQL_STAGING_USER"; pw="${MYSQL_STAGING_PASSWORD:-}"; db="$MYSQL_STAGING_DB"
    else
        host="$MYSQL_PROD_HOST"; port="$MYSQL_PROD_PORT"
        user="$MYSQL_PROD_USER"; pw="${MYSQL_PROD_PASSWORD:-}"; db="$MYSQL_PROD_DB"
    fi
    MYSQL_PWD="$pw" "$MYSQL_CMD" \
        --host="$host" --port="$port" --user="$user" "$db" "$@"
}

mysql_count() {  # mysql_count <color> <table> -> row count (integer)
    local color="$1" table="$2"
    printf 'SELECT COUNT(*) FROM `%s`;\n' "$table" \
        | mysql_run "$color" -N 2>/dev/null | tr -d '[:space:]'
}

# ---- pre-flight (stage 0) -------------------------------------------------
bold "==> Pre-flight"

command -v "$SQLITE_CMD" >/dev/null 2>&1 || die "sqlite3 not found ($SQLITE_CMD)"
if [[ "$MODE" != "dryrun" ]]; then
    command -v "$MYSQL_CMD" >/dev/null 2>&1 \
        || die "mysql client not found ($MYSQL_CMD); required for --stage/--commit"
fi

# Jellyfin (every replica/color) must be stopped: this is an offline store
# migration; no live EF Core writer may touch jellyfin.db or MySQL mid-copy.
if [[ $SKIP_SERVICE_CHECK -eq 0 ]]; then
    if command -v systemctl >/dev/null 2>&1 && systemctl is-active --quiet jellyfin.service 2>/dev/null; then
        die "jellyfin.service is active. Stop EVERY color's Jellyfin first: sudo systemctl stop jellyfin"
    fi
    if pgrep -fa '[j]ellyfin' >/dev/null 2>&1; then
        warn "pgrep found a live jellyfin process:"; pgrep -fa '[j]ellyfin' >&2 || true
        die "Refusing to migrate while any Jellyfin process is alive."
    fi
    info "  jellyfin: stopped (ok)"
else
    warn "  --skip-service-check given; NOT verifying jellyfin is stopped (sandbox only)"
fi

[[ -f "$JELLYFIN_DB" ]] || die "jellyfin.db not found at: $JELLYFIN_DB"
head -c 16 "$JELLYFIN_DB" | grep -q 'SQLite format 3' || die "not a SQLite database: $JELLYFIN_DB"
info "  source jellyfin.db : $JELLYFIN_DB"
info "  mode               : $MODE"
info "  staging dir        : $STAGING_DIR"

mkdir -p "$STAGING_DIR"

# =====================================================================
# STAGE 1 — CLONE the active color's live jellyfin.db to an offline snapshot.
# =====================================================================
bold "==> Stage 1: clone active jellyfin.db"
CLONE="$STAGING_DIR/jellyfin.clone.db"
# Prefer a consistent snapshot via the SQLite backup API over a raw cp so any
# WAL frames are folded in and we never read a torn page.
"$SQLITE_CMD" "$JELLYFIN_DB" ".backup '$CLONE'"
[[ -f "$CLONE" ]] || die "clone failed: $CLONE not produced"
info "  clone: $CLONE"

# Resolve the table set to migrate.
mapfile -t ALL_TABLES < <("$SQLITE_CMD" "$CLONE" \
    "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;")

declare -a TABLES=()
if [[ -n "$TABLES_ARG" ]]; then
    IFS=',' read -r -a want <<< "$TABLES_ARG"
    for t in "${want[@]}"; do
        t="$(printf '%s' "$t" | tr -d '[:space:]')"
        [[ -n "$t" ]] || continue
        printf '%s\n' "${ALL_TABLES[@]}" | grep -qx "$t" || die "requested table not in source: $t"
        TABLES+=("$t")
    done
else
    for t in "${ALL_TABLES[@]}"; do
        skip=0
        for s in $SKIP_TABLES_DEFAULT; do [[ "$t" == "$s" ]] && skip=1; done
        [[ $skip -eq 0 ]] && TABLES+=("$t")
    done
fi
[[ ${#TABLES[@]} -gt 0 ]] || die "no tables selected to migrate"

# =====================================================================
# STAGE 2 — PREDICTED COUNTS: the contract every load must reproduce.
# =====================================================================
bold "==> Stage 2: predicted counts (source of truth for every later stage)"
PRED_FILE="$STAGING_DIR/predicted-counts.tsv"
: > "$PRED_FILE"
declare -A PRED=()
TOTAL=0
for t in "${TABLES[@]}"; do
    n="$("$SQLITE_CMD" "$CLONE" "SELECT COUNT(*) FROM \"$t\";")"
    PRED["$t"]="$n"
    printf '%s\t%s\n' "$t" "$n" >> "$PRED_FILE"
    TOTAL=$((TOTAL + n))
    printf '    %-40s %s\n' "$t" "$n"
done
info "  tables: ${#TABLES[@]}   total rows: $TOTAL"
info "  predicted counts written: $PRED_FILE"

# ---- Generate the MySQL load set from the clone ---------------------------
# One idempotent load file: FK checks off, and for each table a scoped DELETE
# followed by SQLite-emitted INSERT statements. jellyfin-plugin-mysql already
# created the destination schema (via EF Core migrations), so we only load data.
bold "==> Generating MySQL load set"
LOAD_FILE="$STAGING_DIR/mysql-load.sql"
{
    echo "-- phantom-migrate-jellyfindb-to-mysql load set generated $TS"
    echo "SET FOREIGN_KEY_CHECKS=0;"
    echo "SET UNIQUE_CHECKS=0;"
} > "$LOAD_FILE"
for t in "${TABLES[@]}"; do
    printf 'DELETE FROM `%s`;\n' "$t" >> "$LOAD_FILE"
    # SQLite `.mode insert <t>` emits `INSERT INTO <t> VALUES(...);` per row,
    # value-quoting compatible with MySQL. The table name is emitted verbatim,
    # so it MUST be the bare identifier (a backtick-quoted arg would be emitted
    # literally, corrupting the target name). Jellyfin table names are plain
    # identifiers; MySQL accepts an unquoted identifier here.
    "$SQLITE_CMD" "$CLONE" <<SQL >> "$LOAD_FILE"
.mode insert $t
SELECT * FROM "$t";
SQL
done
echo "SET FOREIGN_KEY_CHECKS=1;" >> "$LOAD_FILE"
echo "SET UNIQUE_CHECKS=1;" >> "$LOAD_FILE"
info "  load set: $LOAD_FILE"

# ---- load + verify a color against the predicted counts -------------------
load_and_verify() {  # load_and_verify <color>
    local color="$1"
    bold "==> Loading validated data set into the ${color} MySQL DB"
    mysql_run "$color" < "$LOAD_FILE" \
        || die "MySQL load into ${color} failed. Prod is untouched unless this WAS prod; re-check connection/schema."

    bold "==> Verifying ${color} actual-after counts == predicted"
    local drift=0 t actual
    for t in "${TABLES[@]}"; do
        actual="$(mysql_count "$color" "$t")"
        if [[ "$actual" != "${PRED[$t]}" ]]; then
            warn "  DRIFT: $t predicted=${PRED[$t]} actual=$actual"
            drift=1
        fi
    done
    if [[ $drift -ne 0 ]]; then
        die "count drift on ${color} — the load did not reproduce the clone. Refusing to proceed."
    fi
    info "  ${color}: all ${#TABLES[@]} tables match predicted counts (total $TOTAL rows)"
}

STAGE_RECEIPT="$STAGING_DIR/.staging-validated"

# =====================================================================
# DRY-RUN: stages 1+2 + load-set generation only. No MySQL writes.
# =====================================================================
if [[ "$MODE" == "dryrun" ]]; then
    bold "==> Dry-run complete (stages 1-2)"
    info "  Nothing was written to any MySQL DB."
    info "  Next: re-run with --stage to load + validate on the INACTIVE color:"
    info "        MYSQL_STAGING_* env set, STAGING_DIR=$STAGING_DIR $0 --stage"
    exit 0
fi

# =====================================================================
# STAGE 3 + 4 — staging validation on the inactive color, then hand-validation.
# =====================================================================
if [[ "$MODE" == "stage" ]]; then
    bold "==> Stage 3: staging validation on the INACTIVE color (${MYSQL_STAGING_DB}@${MYSQL_STAGING_HOST})"
    load_and_verify staging
    {
        echo "staged_at=$TS"
        echo "clone=$CLONE"
        echo "predicted=$PRED_FILE"
        echo "load=$LOAD_FILE"
        echo "total_rows=$TOTAL"
    } > "$STAGE_RECEIPT"
    bold "==> Stage 4: OPERATOR HAND-VALIDATION required"
    cat <<EOF
  The inactive color's MySQL DB now holds the migrated data and matches the
  predicted counts exactly. Before any prod write:
    1. Point a jellyfin-plugin-mysql-configured Jellyfin at ${MYSQL_STAGING_DB}
       (the inactive color) and start ONLY that replica.
    2. Hand-validate: log in, browse the library, confirm users, watch-state,
       favourites, and playlists are intact and correct.
    3. Stop that replica again.
  When satisfied, run the prod cutover (stage 5) from THIS SAME staging dir:
       STAGING_DIR=$STAGING_DIR MYSQL_PROD_* env set  $0 --commit
  A --commit refuses to run unless this staging validation receipt exists:
       $STAGE_RECEIPT
EOF
    exit 0
fi

# =====================================================================
# STAGE 5 — PROD WRITE (cutover). Gated on a passing stage-3 receipt + typed
# confirmation; backs up the prod target first.
# =====================================================================
if [[ "$MODE" == "commit" ]]; then
    [[ -f "$STAGE_RECEIPT" ]] || die "no staging-validation receipt in $STAGING_DIR (run --stage first, same STAGING_DIR). Refusing prod write."
    info "  staging receipt: $STAGE_RECEIPT"

    bold "==> Stage 5: prod cutover confirmation"
    printf '  Type EXACTLY  MIGRATE  to write the validated data into the PROD MySQL DB (%s@%s): ' \
        "$MYSQL_PROD_DB" "$MYSQL_PROD_HOST"
    read -r confirm
    [[ "$confirm" == "MIGRATE" ]] || die "confirmation mismatch ('$confirm' != 'MIGRATE'); aborted. Prod untouched."

    bold "==> Backing up the prod MySQL target first"
    PROD_BAK="$STAGING_DIR/prod-mysql-backup.${TS}.sql"
    if command -v "$MYSQLDUMP_CMD" >/dev/null 2>&1; then
        if MYSQL_PWD="${MYSQL_PROD_PASSWORD:-}" "$MYSQLDUMP_CMD" \
                --host="$MYSQL_PROD_HOST" --port="$MYSQL_PROD_PORT" \
                --user="$MYSQL_PROD_USER" "$MYSQL_PROD_DB" > "$PROD_BAK" 2>/dev/null; then
            info "  prod backup: $PROD_BAK"
        else
            warn "  mysqldump backup failed (empty/new prod DB?). Continuing — the source jellyfin.db is itself the ultimate rollback."
        fi
    else
        warn "  mysqldump not found ($MYSQLDUMP_CMD); skipping prod backup. Source jellyfin.db remains the rollback."
    fi

    load_and_verify prod

    bold "==> Prod cutover data load complete"
    cat <<EOF
  The shared/prod MySQL DB (${MYSQL_PROD_DB}@${MYSQL_PROD_HOST}) now holds the
  migrated authoritative data and matches the predicted counts.

  Next operator steps (nothing else touched this — this script only loaded data):
    1. Set every color's jellyfin-plugin-mysql connection string to the shared
       prod MySQL DB (${MYSQL_PROD_DB}@${MYSQL_PROD_HOST}:${MYSQL_PROD_PORT}).
    2. Start the Jellyfin replicas. They now share ONE authoritative MySQL store.
    3. Confirm the library, users, and watch-state on each color.
    4. Keep the per-color SQLite jellyfin.db files and the staging dir
       ($STAGING_DIR) until you have confirmed a normal usage cycle; the SQLite
       files remain a full rollback (just revert the connection string).
EOF
    exit 0
fi
