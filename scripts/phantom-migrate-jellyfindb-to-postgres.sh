#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# phantom-migrate-jellyfindb-to-postgres.sh
#
# WHAT:  Out-of-band, operator-run migration of a Jellyfin-side authoritative
#        SQLite database onto a shared **PostgreSQL** instance. Two sources are
#        supported, EACH landing in its OWN PostgreSQL logical DB on the SAME
#        Postgres server:
#
#          --source jellyfin (default)
#              Jellyfin's AUTHORITATIVE library/user database, off the per-color
#              SQLite `jellyfin.db` onto PostgreSQL served through the external
#              `Jellyfin.Pgsql` (JPVenson) EF Core provider. This is P4 Stage A:
#              it lets N Jellyfin replicas (the blue/green "colors") share ONE
#              authoritative store instead of each carrying its own SQLite file.
#
#          --source phantom
#              phantom.db's own plugin state, off the per-color SQLite
#              `phantom.db` onto its own PostgreSQL logical DB (`phantom_prod` /
#              `phantom_dev`) on the SAME Postgres server as `jellyfin.db`. Per
#              `docs/tasks/p4-phantomdb-multiwriter-audit.md`, phantom.db is NOT
#              multi-writer safe as a shared SQLite file, so moving it to a real
#              multi-writer engine is the only correct way to share phantom state
#              across replicas.
#
#        This script is the `SQLite -> PostgreSQL` analogue of the existing
#        `scripts/phantom-migrate-v11-to-v12.sh` operator-migration contract:
#        offline, dry-run-by-default, backed-up, predicted-before /
#        actual-after count-verified, idempotent, and hard-guarded. It carries
#        NO data-mutating SQL against a live DB in the default path.
#
#        (Historical note: this deliverable previously targeted MySQL /
#        `jellyfin-plugin-mysql`. The 2026-07-31 ROI repointed Stage A to the
#        external PostgreSQL provider `Jellyfin.Pgsql`; the MySQL variant is
#        obsolete and replaced by this Postgres script.)
#
# WHY POSTGRES (not shared SQLite):
#        A single SQLite file shared read-write across multiple replica
#        processes/hosts is explicitly unsafe (broken advisory locking on network
#        filesystems, WAL shared-memory that does not span hosts, immediate
#        SQLITE_BUSY). The ONLY correct way to let real replicas share an
#        authoritative store is a genuine multi-writer engine. `Jellyfin.Pgsql`
#        points Jellyfin's EF Core context at PostgreSQL, which IS such an engine.
#        Migrating `jellyfin.db` (and, for shared phantom state, `phantom.db`)
#        onto that Postgres server is therefore the store-level prerequisite for
#        the multi-replica P4 topology.
#
# EXPAND/CONTRACT COMPATIBILITY (additive-only landing):
#        This migration lands ADDITIVELY and is compatible with the
#        expand/contract schema-change discipline
#        (flux `docs/phantom-library-schema-change-expand-contract.md`): it copies
#        row data into a FRESH logical DB / freshly-created tables on the Postgres
#        server. It performs NO destructive rename, NO in-place ALTER, and NO
#        rewrite of any existing table on either the source (SQLite is cloned,
#        never written) or the destination (a scoped DELETE + reload of the SAME
#        logical rows is idempotent, never a structural change). The destination
#        schema is owned elsewhere — `Jellyfin.Pgsql` runs the EF Core migrations
#        that create the `jellyfin` tables; the phantom Postgres schema is created
#        additively by the plugin's Postgres `EnsureSchema` path — so this script
#        only ever COPIES authoritative row data into an already-created schema and
#        proves the copy.
#
# THE P3 FIVE-STAGE STAGING-VALIDATION METHODOLOGY (never a bespoke shortcut):
#        This migration follows the SAME five-stage staging discipline P3 used,
#        so a real prod cutover is only ever reached after the exact same data
#        has been proven to load correctly out-of-band. The stages are:
#
#          1. CLONE            — snapshot the ACTIVE color's live source SQLite DB
#                                to an offline staging clone. The live file is
#                                never read destructively and never written.
#          2. PREDICTED COUNTS — enumerate every data table in the clone and
#                                record its exact row count. These predicted
#                                counts are the contract every later stage must
#                                reproduce byte-for-byte.
#          3. STAGING VALIDATION ON THE INACTIVE COLOR / DEV LOGICAL DB
#                              — load the clone's data into the INACTIVE color's
#                                Postgres schema (created by the provider/plugin)
#                                and assert Postgres's actual per-table counts
#                                equal the stage-2 predictions. The inactive color
#                                is the safe rehearsal target: nothing user-facing
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
#                                shared/prod Postgres logical DB and re-verify
#                                counts. The operator then flips the provider's
#                                connection string live (next steps printed).
#
# WHY THIS IS SAFE:
#   - NON-DESTRUCTIVE TO THE SOURCE. The active source SQLite DB is only ever
#     copied (stage 1), never altered. A failed migration leaves prod exactly
#     as it was; the operator just does not flip the connection string.
#   - DRY-RUN BY DEFAULT. With neither --stage nor --commit the script clones,
#     computes predicted counts, generates the Postgres load set, prints the
#     plan, and writes NOTHING to any Postgres DB.
#   - STAGE-GATED. A prod write (--commit) is REFUSED unless staging validation
#     (--stage) has been run and passed in the same staging dir (a
#     `.staging-validated` receipt is required), so you cannot skip the inactive-
#     color rehearsal.
#   - COUNT-VERIFIED AT EVERY LOAD. After every Postgres load the script queries
#     Postgres's actual per-table counts and refuses to proceed on ANY drift from
#     the stage-2 predictions.
#   - IDEMPOTENT. Each table's load set is prefixed with a scoped DELETE so a
#     re-run converges to the same rows; re-running staging or prod is safe.
#     The load runs inside a single transaction with referential triggers
#     suspended (`SET session_replication_role = replica`) so FK ordering never
#     forces a partial load.
#   - BACKS UP FIRST. A --commit prod write dumps the prod Postgres target's
#     current contents to a timestamped pg_dump artifact before loading.
#   - SCHEMA-VERSION GUARDED (--source phantom). Refuses to migrate a source
#     phantom.db whose `PRAGMA user_version` does not match
#     PhantomDb.CurrentSchemaVersion (default 16) — mirroring
#     phantom-migrate-v11-to-v12.sh's own user_version guard, so a stale or
#     wrong-version phantom.db is never silently copied into Postgres.
#
# NOT A GENERAL MIGRATOR. This is one specific, tested, offline store migration
# (SQLite -> PostgreSQL), run with every replica's Jellyfin stopped. It does not
# evolve schema; the destination schema is owned by the provider / plugin. This
# script only COPIES the authoritative row data into that schema and proves the
# copy.
#
# TESTED: regression-tested in-repo by
#         scripts/tests/phantom-migrate-jellyfindb-to-postgres.test.sh against a
#         synthetic source DB and a SQLite-backed Postgres stand-in (bash +
#         sqlite3 only, no live server). That test proves the clone, predicted
#         counts, load-set generation, count-parity validation, stage gating,
#         and confirmation gate, for BOTH the jellyfin and phantom source paths.
#         The live proof against a real PostgreSQL and a real Jellyfin.Pgsql
#         cutover is the separate operator live-rig step (mirroring how
#         phantom-migrate-v11-to-v12 defers its live-rig proof to a dedicated rig
#         task).
#
# HELD (2026-07-30 gate): Stage A stays HELD until the host->cluster import
# finishes and this environment flips to prod. Author + regression-test the
# script now, but do NOT deploy/run it against real operator data while the gate
# holds.
#
# CONNECTION CONFIG (env; the Postgres password is NEVER passed on the CLI):
#   Source SQLite DB (selected by --source):
#     JELLYFIN_DB              source SQLite path for --source jellyfin
#                              (default /var/lib/jellyfin/data/jellyfin.db)
#     PHANTOM_DB               source SQLite path for --source phantom
#                              (default /var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db)
#     PHANTOM_EXPECTED_SCHEMA_VERSION
#                              schema-version guard for --source phantom: the
#                              PRAGMA user_version the source phantom.db MUST be
#                              at (default 16, PhantomDb.CurrentSchemaVersion).
#                              A mismatch refuses to migrate (see "SCHEMA-VERSION
#                              GUARD" below).
#   STAGING_DIR                working dir for the clone + load set + receipts
#                              (default $TMPDIR/phantom-<source>-postgres.<ts>)
#   Inactive color / dev logical DB (stage 3 rehearsal target):
#     PG_STAGING_HOST     default 127.0.0.1
#     PG_STAGING_PORT     default 5432
#     PG_STAGING_USER     default jellyfin
#     PG_STAGING_PASSWORD (no default; read from env or PG_STAGING_PASSWORD_FILE)
#     PG_STAGING_DB       default: jellyfin_inactive (jellyfin) / phantom_dev (phantom)
#   Prod / shared logical DB (stage 5 cutover target):
#     PG_PROD_HOST        default 127.0.0.1
#     PG_PROD_PORT        default 5432
#     PG_PROD_USER        default jellyfin
#     PG_PROD_PASSWORD    (no default; read from env or PG_PROD_PASSWORD_FILE)
#     PG_PROD_DB          default: jellyfin_prod (jellyfin) / phantom_prod (phantom)
#   Tool overrides (sandbox/rig/testing only):
#     PSQL_CMD            psql client argv[0]  (default: psql)
#     PGDUMP_CMD          pg_dump argv[0]      (default: pg_dump)
#     SQLITE_CMD          sqlite3 argv[0]      (default: sqlite3)
#
# FLAGS:
#   --source jellyfin|phantom  which SQLite source to migrate (default jellyfin).
#   (default mode)          DRY-RUN: stages 1+2, generate load set, print plan.
#   --stage                 run stage 3 (load + validate on the INACTIVE color /
#                           dev logical DB), then stop at stage 4.
#   --commit                run stage 5 (prod write); requires a prior passing
#                           --stage in the same STAGING_DIR + a typed MIGRATE.
#   --tables a,b,c          restrict to these tables (default: all data tables).
#   --skip-service-check    bypass the jellyfin-must-be-stopped pre-flight
#                           (sandbox/rig only; NEVER on prod).
#   -h, --help              this help.
# ---------------------------------------------------------------------------

set -euo pipefail

# ---- config ---------------------------------------------------------------
SOURCE="jellyfin"      # jellyfin | phantom
MODE="dryrun"          # dryrun | stage | commit
SKIP_SERVICE_CHECK=0
TABLES_ARG=""

PG_STAGING_HOST="${PG_STAGING_HOST:-127.0.0.1}"
PG_STAGING_PORT="${PG_STAGING_PORT:-5432}"
PG_STAGING_USER="${PG_STAGING_USER:-jellyfin}"

PG_PROD_HOST="${PG_PROD_HOST:-127.0.0.1}"
PG_PROD_PORT="${PG_PROD_PORT:-5432}"
PG_PROD_USER="${PG_PROD_USER:-jellyfin}"

PSQL_CMD="${PSQL_CMD:-psql}"
PGDUMP_CMD="${PGDUMP_CMD:-pg_dump}"
SQLITE_CMD="${SQLITE_CMD:-sqlite3}"

TS="$(date -u +%Y%m%dT%H%M%SZ)"

bold()  { printf '\033[1m%s\033[0m\n' "$*"; }
warn()  { printf '\033[33m%s\033[0m\n' "$*" >&2; }
die()   { printf '\033[31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }
info()  { printf '%s\n' "$*"; }

usage() {
    sed -n '2,190p' "$0" | sed 's/^# \{0,1\}//'
    exit 0
}

# ---- args -----------------------------------------------------------------
while [[ $# -gt 0 ]]; do
    case "$1" in
        --source)             shift; SOURCE="${1:-}"; [[ -n "$SOURCE" ]] || die "--source needs a value" ;;
        --source=*)           SOURCE="${1#--source=}" ;;
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

case "$SOURCE" in
    jellyfin)
        SOURCE_DB="${JELLYFIN_DB:-/var/lib/jellyfin/data/jellyfin.db}"
        # EF Core / provider-internal tables the provider owns and must NOT be
        # copied from the SQLite provider (migration history is provider-specific).
        SKIP_TABLES_DEFAULT="__EFMigrationsHistory __EFMigrationsLock"
        PG_STAGING_DB="${PG_STAGING_DB:-jellyfin_inactive}"
        PG_PROD_DB="${PG_PROD_DB:-jellyfin_prod}"
        ;;
    phantom)
        SOURCE_DB="${PHANTOM_DB:-/var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db}"
        # phantom.db has no EF bookkeeping table; sqlite_% tables are excluded
        # by the enumeration query below.
        SKIP_TABLES_DEFAULT=""
        PG_STAGING_DB="${PG_STAGING_DB:-phantom_dev}"
        PG_PROD_DB="${PG_PROD_DB:-phantom_prod}"
        ;;
    *) die "unknown --source: $SOURCE (want jellyfin|phantom)" ;;
esac

STAGING_DIR="${STAGING_DIR:-${TMPDIR:-/tmp}/phantom-${SOURCE}-postgres.${TS}}"

# ---- resolve Postgres passwords from *_FILE if given ----------------------
if [[ -z "${PG_STAGING_PASSWORD:-}" && -n "${PG_STAGING_PASSWORD_FILE:-}" ]]; then
    PG_STAGING_PASSWORD="$(cat "$PG_STAGING_PASSWORD_FILE")"
fi
if [[ -z "${PG_PROD_PASSWORD:-}" && -n "${PG_PROD_PASSWORD_FILE:-}" ]]; then
    PG_PROD_PASSWORD="$(cat "$PG_PROD_PASSWORD_FILE")"
fi

# ---- psql invocation helpers ----------------------------------------------
# Build a psql client argv for a given color. Credentials come from env only,
# never the process CLI (defence against ps(1) credential leak) — the password
# is passed via PGPASSWORD in the child environment.
psql_run() {   # psql_run <color> [extra psql args...]  ; SQL on stdin if none
    local color="$1"; shift
    local host port user pw db
    if [[ "$color" == "staging" ]]; then
        host="$PG_STAGING_HOST"; port="$PG_STAGING_PORT"
        user="$PG_STAGING_USER"; pw="${PG_STAGING_PASSWORD:-}"; db="$PG_STAGING_DB"
    else
        host="$PG_PROD_HOST"; port="$PG_PROD_PORT"
        user="$PG_PROD_USER"; pw="${PG_PROD_PASSWORD:-}"; db="$PG_PROD_DB"
    fi
    PGPASSWORD="$pw" "$PSQL_CMD" \
        --host="$host" --port="$port" --username="$user" --dbname="$db" \
        -v ON_ERROR_STOP=1 "$@"
}

pg_count() {  # pg_count <color> <table> -> row count (integer)
    local color="$1" table="$2"
    psql_run "$color" -tA -c "SELECT COUNT(*) FROM \"$table\";" 2>/dev/null | tr -d '[:space:]'
}

# ---- pre-flight (stage 0) -------------------------------------------------
bold "==> Pre-flight (source: $SOURCE)"

command -v "$SQLITE_CMD" >/dev/null 2>&1 || die "sqlite3 not found ($SQLITE_CMD)"
if [[ "$MODE" != "dryrun" ]]; then
    command -v "$PSQL_CMD" >/dev/null 2>&1 \
        || die "psql client not found ($PSQL_CMD); required for --stage/--commit"
fi

# Jellyfin (every replica/color) must be stopped: this is an offline store
# migration; no live writer may touch the source SQLite DB or Postgres mid-copy.
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

[[ -f "$SOURCE_DB" ]] || die "source SQLite DB not found at: $SOURCE_DB"
head -c 16 "$SOURCE_DB" | grep -q 'SQLite format 3' || die "not a SQLite database: $SOURCE_DB"
info "  source SQLite DB   : $SOURCE_DB"
info "  mode               : $MODE"
info "  staging (dev) DB   : $PG_STAGING_DB"
info "  prod DB            : $PG_PROD_DB"
info "  staging dir        : $STAGING_DIR"

# ---- schema-version guard (phantom source only) ----------------------------
# phantom.db's schema is versioned via `PRAGMA user_version` (see
# PhantomDb.CurrentSchemaVersion / EnsureSchemaAsync). Per AGENTS.md "No database
# migrations until v1.0", this migration is a pure row-data COPY into an
# already-created Postgres schema — it must NEVER run against a source phantom.db
# whose on-disk schema does not match the version the running plugin build
# expects, exactly like phantom-migrate-v11-to-v12.sh's own user_version guard.
# A mismatch means the operator is migrating with the wrong plugin build staged,
# or a stale/corrupt phantom.db — either way, guessing at the shape is unsafe;
# refuse and tell the operator to align the plugin build/DB first.
# jellyfin.db has no equivalent single-file gate here: its own schema is owned
# and versioned by the Jellyfin.Pgsql provider's EF Core migrations, applied
# independently on the Postgres side, not by this script.
if [[ "$SOURCE" == "phantom" ]]; then
    PHANTOM_EXPECTED_SCHEMA_VERSION="${PHANTOM_EXPECTED_SCHEMA_VERSION:-16}"
    PHANTOM_ACTUAL_SCHEMA_VERSION="$("$SQLITE_CMD" "$SOURCE_DB" 'PRAGMA user_version;' 2>/dev/null || echo '?')"
    [[ "$PHANTOM_ACTUAL_SCHEMA_VERSION" == "$PHANTOM_EXPECTED_SCHEMA_VERSION" ]] \
        || die "phantom.db schema-version guard: source is at user_version=$PHANTOM_ACTUAL_SCHEMA_VERSION, expected $PHANTOM_EXPECTED_SCHEMA_VERSION (PhantomDb.CurrentSchemaVersion). Refusing to migrate a schema-mismatched phantom.db — align the plugin build (PhantomDb.CurrentSchemaVersion) and the source DB (or wipe/rebuild per AGENTS.md) before retrying. Override only for a deliberately pinned rehearsal via PHANTOM_EXPECTED_SCHEMA_VERSION=$PHANTOM_ACTUAL_SCHEMA_VERSION."
    info "  phantom schema guard: user_version=$PHANTOM_ACTUAL_SCHEMA_VERSION (expected $PHANTOM_EXPECTED_SCHEMA_VERSION) ok"
fi

mkdir -p "$STAGING_DIR"

# =====================================================================
# STAGE 1 — CLONE the active color's live source SQLite DB to an offline snapshot.
# =====================================================================
bold "==> Stage 1: clone active ${SOURCE} SQLite DB"
CLONE="$STAGING_DIR/${SOURCE}.clone.db"
# Prefer a consistent snapshot via the SQLite backup API over a raw cp so any
# WAL frames are folded in and we never read a torn page.
"$SQLITE_CMD" "$SOURCE_DB" ".backup '$CLONE'"
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

# ---- Generate the Postgres load set from the clone ------------------------
# One idempotent load file wrapped in a single transaction with referential
# triggers suspended, and for each table a scoped DELETE followed by
# SQLite-emitted INSERT statements (identifiers double-quoted to preserve the
# case-sensitive names the EF/plugin schema created). The provider/plugin already
# created the destination schema, so we only load data.
bold "==> Generating Postgres load set"
LOAD_FILE="$STAGING_DIR/postgres-load.sql"
{
    echo "-- phantom-migrate-jellyfindb-to-postgres load set (source=$SOURCE) generated $TS"
    echo "BEGIN;"
    echo "SET session_replication_role = replica;"
} > "$LOAD_FILE"
# Fetch the destination column TYPE maps (booleans + arrays) so the generator emits
# correctly-typed PostgreSQL literals. SQLite is dynamically typed and quote()/`.mode
# insert` emit SQLite-flavoured literals PostgreSQL rejects for three column classes:
#   * boolean  — SQLite stores 0/1 ints; PG has no int->bool assignment cast.
#   * array    — SQLite stores a JSON '[..]' string; PG array input needs '{..}'.
#   * bytea    — SQLite emits X'..'; PG parses that as a BIT STRING (handled by the
#                post-pass below, since quote() still emits the blob as X'..').
# We read the destination types from the target DB and transform per column. Column
# NAMES bind the values (never position), so the EF schema ordering a table's columns
# differently from the historical SQLite schema is irrelevant. Requires target
# connectivity (always set for --stage/--commit); dry-run skips the typing (preview only).
TYPE_SRC_COLOR="staging"; [[ "$MODE" == "commit" ]] && TYPE_SRC_COLOR="prod"
# Strict canonical GUID shape (8-4-4-4-12 hex) used to gate uuid columns.
_H='[0-9A-Fa-f]'
GUIDGLOB="${_H}${_H}${_H}${_H}${_H}${_H}${_H}${_H}-${_H}${_H}${_H}${_H}-${_H}${_H}${_H}${_H}-${_H}${_H}${_H}${_H}-${_H}${_H}${_H}${_H}${_H}${_H}${_H}${_H}${_H}${_H}${_H}${_H}"
declare -A BOOLCOL=() ARRCOL=() TSCOL=() BYTEACOL=() UUIDCOL=()
if [[ "$MODE" != "dryrun" ]]; then
    while IFS=$'\t' read -r _tbl _col; do [[ -n "$_tbl" ]] && BOOLCOL["$_tbl.$_col"]=1; done < <(
        psql_run "$TYPE_SRC_COLOR" -tAF $'\t' -c \
            "SELECT table_name,column_name FROM information_schema.columns WHERE table_schema='public' AND data_type='boolean'")
    while IFS=$'\t' read -r _tbl _col; do [[ -n "$_tbl" ]] && ARRCOL["$_tbl.$_col"]=1; done < <(
        psql_run "$TYPE_SRC_COLOR" -tAF $'\t' -c \
            "SELECT table_name,column_name FROM information_schema.columns WHERE table_schema='public' AND data_type='ARRAY'")
    while IFS=$'\t' read -r _tbl _col; do [[ -n "$_tbl" ]] && TSCOL["$_tbl.$_col"]=1; done < <(
        psql_run "$TYPE_SRC_COLOR" -tAF $'\t' -c \
            "SELECT table_name,column_name FROM information_schema.columns WHERE table_schema='public' AND data_type='timestamp with time zone'")
    while IFS=$'\t' read -r _tbl _col; do [[ -n "$_tbl" ]] && BYTEACOL["$_tbl.$_col"]=1; done < <(
        psql_run "$TYPE_SRC_COLOR" -tAF $'\t' -c \
            "SELECT table_name,column_name FROM information_schema.columns WHERE table_schema='public' AND data_type='bytea'")
    while IFS=$'\t' read -r _tbl _col; do [[ -n "$_tbl" ]] && UUIDCOL["$_tbl.$_col"]=1; done < <(
        psql_run "$TYPE_SRC_COLOR" -tAF $'\t' -c \
            "SELECT table_name,column_name FROM information_schema.columns WHERE table_schema='public' AND data_type='uuid'")
    info "  destination types: ${#BOOLCOL[@]} boolean, ${#ARRCOL[@]} array, ${#TSCOL[@]} timestamp, ${#BYTEACOL[@]} bytea, ${#UUIDCOL[@]} uuid column(s)"
fi

for t in "${TABLES[@]}"; do
    printf 'DELETE FROM "%s";\n' "$t" >> "$LOAD_FILE"
    # Column names in SQLite's own (cid) order.
    mapfile -t _cols < <("$SQLITE_CMD" "$CLONE" "SELECT name FROM pragma_table_info('$t') ORDER BY cid;")
    [[ ${#_cols[@]} -gt 0 ]] || die "could not resolve columns for table $t from the SQLite clone"
    _collist=""; _exprlist=""
    for c in "${_cols[@]}"; do
        _collist+="${_collist:+,}\"$c\""
        if [[ -n "${BOOLCOL[$t.$c]:-}" ]]; then
            _e="CASE WHEN \"$c\" IS NULL THEN 'NULL' WHEN \"$c\"=0 THEN 'false' ELSE 'true' END"
        elif [[ -n "${ARRCOL[$t.$c]:-}" ]]; then
            # SQLite JSON '[a,b,c]' -> PostgreSQL array literal '{a,b,c}'::bigint[].
            _e="CASE WHEN \"$c\" IS NULL THEN 'NULL' ELSE '''{' || substr(\"$c\",2,length(\"$c\")-2) || '}''::bigint[]' END"
        elif [[ -n "${TSCOL[$t.$c]:-}" ]]; then
            # PostgreSQL timestamptz is strict; SQLite (dynamically typed) can hold a
            # non-timestamp value in a DATETIME column (observed: BaseItems.EndDate='101').
            # Pass through anything shaped like a 'YYYY-MM-DD...' timestamp; coerce any
            # other non-NULL value to NULL (it is not representable as a timestamp).
            _e="CASE WHEN \"$c\" IS NULL THEN 'NULL' WHEN \"$c\" GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]*' THEN quote(\"$c\") ELSE 'NULL' END"
        elif [[ -n "${BYTEACOL[$t.$c]:-}" ]]; then
            # SQLite BLOB -> PostgreSQL bytea hex literal '\xHEX'::bytea, built from
            # SQLite hex(). Done HERE (per column) rather than by a post-pass regex so a
            # text value that merely CONTAINS an X'..' substring is never mistaken for a
            # blob literal and corrupted (that shifts every following column).
            _e="CASE WHEN \"$c\" IS NULL THEN 'NULL' ELSE '''\x' || hex(\"$c\") || '''::bytea' END"
        elif [[ -n "${UUIDCOL[$t.$c]:-}" ]]; then
            # PostgreSQL uuid is strict; SQLite may hold a byte-shifted/corrupt GUID
            # (observed: one BaseItems row with non-hex trailing chars in ParentId/
            # SeasonId/SeriesId/TopParentId). Pass through canonical 8-4-4-4-12 GUIDs;
            # coerce any other non-NULL value to NULL (unrepresentable as a uuid). All
            # primary-key Id columns were verified clean, so this only NULLs bad FKs.
            _e="CASE WHEN \"$c\" IS NULL THEN 'NULL' WHEN \"$c\" GLOB '$GUIDGLOB' THEN quote(\"$c\") ELSE 'NULL' END"
        else
            _e="quote(\"$c\")"
        fi
        _exprlist+="${_exprlist:+ || ',' || }$_e"
    done
    # One fully-typed, column-named INSERT per row.
    "$SQLITE_CMD" "$CLONE" >> "$LOAD_FILE" <<SQL
SELECT 'INSERT INTO "$t" ($_collist) VALUES(' || $_exprlist || ');' FROM "$t";
SQL
done
echo "SET session_replication_role = DEFAULT;" >> "$LOAD_FILE"
echo "COMMIT;" >> "$LOAD_FILE"
info "  load set: $LOAD_FILE"

# ---- load + verify a color against the predicted counts -------------------
load_and_verify() {  # load_and_verify <color>
    local color="$1"
    bold "==> Loading validated data set into the ${color} Postgres DB"
    psql_run "$color" -f "$LOAD_FILE" >/dev/null \
        || die "Postgres load into ${color} failed. Prod is untouched unless this WAS prod; re-check connection/schema."

    bold "==> Verifying ${color} actual-after counts == predicted"
    local drift=0 t actual
    for t in "${TABLES[@]}"; do
        actual="$(pg_count "$color" "$t")"
        if [[ "$actual" != "${PRED[$t]}" ]]; then
            warn "  DRIFT: $t predicted=${PRED[$t]} actual=$actual"
            drift=1
        fi
    done
    if [[ $drift -ne 0 ]]; then
        die "count drift on ${color} — the load did not reproduce the clone. Refusing to proceed."
    fi
    info "  ${color}: all ${#TABLES[@]} tables match predicted counts (total $TOTAL rows)"

    # Advance identity/serial sequences past the bulk-loaded explicit Ids. A pure
    # row-data COPY inserts explicit primary keys without advancing the owning
    # sequence, so the application's next INSERT would reuse a low value and collide
    # with an existing key (observed on jellyfin: ApiKeys/Devices/Permissions/... use
    # GENERATED-AS-IDENTITY int PKs). Reset each public sequence to MAX(owning col)+1.
    # PostgreSQL-specific; a no-op when the destination has no owned sequences.
    bold "==> Resetting ${color} identity sequences to MAX(id)+1"
    psql_run "$color" <<'RESETSQL' >/dev/null \
        || die "sequence reset on ${color} failed"
DO $$
DECLARE r RECORD; n bigint;
BEGIN
  FOR r IN SELECT s.relname AS seq, t.relname AS tbl, a.attname AS col
           FROM pg_class s
           JOIN pg_depend d ON d.objid=s.oid AND d.deptype IN ('a','i')
           JOIN pg_class t ON t.oid=d.refobjid
           JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum=d.refobjsubid
           WHERE s.relkind='S' AND t.relnamespace='public'::regnamespace
  LOOP
    EXECUTE format('SELECT COALESCE(MAX(%I),0)+1 FROM %I', r.col, r.tbl) INTO n;
    EXECUTE format('SELECT setval(%L, %s, false)', quote_ident(r.seq), n);
  END LOOP;
END $$;
RESETSQL
    info "  ${color}: identity sequences reset"
}

STAGE_RECEIPT="$STAGING_DIR/.staging-validated"

# =====================================================================
# DRY-RUN: stages 1+2 + load-set generation only. No Postgres writes.
# =====================================================================
if [[ "$MODE" == "dryrun" ]]; then
    bold "==> Dry-run complete (stages 1-2)"
    info "  Nothing was written to any Postgres DB."
    info "  Next: re-run with --stage to load + validate on the INACTIVE color / dev DB:"
    info "        PG_STAGING_* env set, STAGING_DIR=$STAGING_DIR $0 --source $SOURCE --stage"
    exit 0
fi

# =====================================================================
# STAGE 3 + 4 — staging validation on the inactive color, then hand-validation.
# =====================================================================
if [[ "$MODE" == "stage" ]]; then
    bold "==> Stage 3: staging validation on the INACTIVE color (${PG_STAGING_DB}@${PG_STAGING_HOST})"
    load_and_verify staging
    {
        echo "source=$SOURCE"
        echo "staged_at=$TS"
        echo "clone=$CLONE"
        echo "predicted=$PRED_FILE"
        echo "load=$LOAD_FILE"
        echo "total_rows=$TOTAL"
    } > "$STAGE_RECEIPT"
    bold "==> Stage 4: OPERATOR HAND-VALIDATION required"
    cat <<EOF
  The inactive color's Postgres DB now holds the migrated data and matches the
  predicted counts exactly. Before any prod write:
    1. Point a Jellyfin.Pgsql-configured Jellyfin (or the phantom plugin's
       Postgres connection) at ${PG_STAGING_DB} (the inactive color) and start
       ONLY that replica.
    2. Hand-validate: log in, browse the library, confirm users, watch-state,
       favourites, and playlists are intact and correct.
    3. Stop that replica again.
  When satisfied, run the prod cutover (stage 5) from THIS SAME staging dir:
       STAGING_DIR=$STAGING_DIR PG_PROD_* env set  $0 --source $SOURCE --commit
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
    printf '  Type EXACTLY  MIGRATE  to write the validated data into the PROD Postgres DB (%s@%s): ' \
        "$PG_PROD_DB" "$PG_PROD_HOST"
    read -r confirm
    [[ "$confirm" == "MIGRATE" ]] || die "confirmation mismatch ('$confirm' != 'MIGRATE'); aborted. Prod untouched."

    bold "==> Backing up the prod Postgres target first"
    PROD_BAK="$STAGING_DIR/prod-postgres-backup.${TS}.sql"
    if command -v "$PGDUMP_CMD" >/dev/null 2>&1; then
        if PGPASSWORD="${PG_PROD_PASSWORD:-}" "$PGDUMP_CMD" \
                --host="$PG_PROD_HOST" --port="$PG_PROD_PORT" \
                --username="$PG_PROD_USER" "$PG_PROD_DB" > "$PROD_BAK" 2>/dev/null; then
            info "  prod backup: $PROD_BAK"
        else
            warn "  pg_dump backup failed (empty/new prod DB?). Continuing — the source SQLite DB is itself the ultimate rollback."
        fi
    else
        warn "  pg_dump not found ($PGDUMP_CMD); skipping prod backup. Source SQLite DB remains the rollback."
    fi

    load_and_verify prod

    bold "==> Prod cutover data load complete"
    cat <<EOF
  The shared/prod Postgres DB (${PG_PROD_DB}@${PG_PROD_HOST}) now holds the
  migrated authoritative data and matches the predicted counts.

  Next operator steps (nothing else touched this — this script only loaded data):
    1. Set the ${SOURCE} provider connection string to the shared prod Postgres
       DB (${PG_PROD_DB}@${PG_PROD_HOST}:${PG_PROD_PORT}). For jellyfin this is
       Jellyfin.Pgsql's connection string on every color; for phantom it is the
       plugin's Postgres connection.
    2. Start the Jellyfin replicas. They now share ONE authoritative Postgres store.
    3. Confirm the library, users, and watch-state on each color.
    4. Keep the per-color SQLite DB files and the staging dir
       ($STAGING_DIR) until you have confirmed a normal usage cycle; the SQLite
       files remain a full rollback (just revert the connection string).
EOF
    exit 0
fi
