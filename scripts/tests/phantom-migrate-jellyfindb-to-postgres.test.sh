#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/phantom-migrate-jellyfindb-to-postgres.test.sh
#
# In-repo regression test for scripts/phantom-migrate-jellyfindb-to-postgres.sh.
#
# Proves the P4 Stage A SQLite -> PostgreSQL migration ORCHESTRATION on a
# SYNTHETIC source DB and a SQLite-backed Postgres STAND-IN, with NO live
# PostgreSQL server and NO dotnet — only bash + sqlite3. The stand-in (a `psql`-
# client shim, below) applies the exact load set the script generates into a real
# SQLite database and answers COUNT queries from it, so the test proves the SQLite
# export round-trips faithfully and every stage's count-parity check is real.
#
# Runs the whole matrix for BOTH sources (--source jellyfin and --source phantom)
# to prove the phantom.db-to-its-own-logical-DB path as well as the jellyfin.db
# path.
#
# The live proof against a real PostgreSQL + a real Jellyfin.Pgsql cutover is the
# separate operator live-rig step (mirroring how phantom-migrate-v11-to-v12
# defers its live-rig proof to a dedicated rig task).
#
# Asserts (per source):
#   - dry-run (default): clones, writes predicted-counts.tsv + postgres-load.sql,
#     writes NOTHING to any Postgres target, exits 0.
#   - --stage: loads into the INACTIVE-color stand-in, actual counts == predicted
#     for every table, writes a .staging-validated receipt, stops at stage 4.
#   - export fidelity: the row data landed in the stand-in equals the source
#     (round-trip through the SQLite `.mode insert` export), quoted values intact.
#   - identifiers are double-quoted in the emitted load set (case preserved).
#   - --commit refuses without a staging receipt (stage gate).
#   - --commit with a receipt + typed MIGRATE loads prod stand-in, counts match.
#   - --commit with a wrong confirmation aborts and writes nothing to prod.
#   - idempotency: a second --stage load converges to the same counts.
# For --source jellyfin additionally: __EFMigrationsHistory is excluded.
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# Skips (exit 0 + NOTE) if sqlite3 is unavailable.
# ---------------------------------------------------------------------------

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
SCRIPT="$REPO_ROOT/scripts/phantom-migrate-jellyfindb-to-postgres.sh"

pass_count=0
fail_count=0
ok()   { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

command -v sqlite3 >/dev/null 2>&1 || { printf 'NOTE: sqlite3 not found; skipping.\n' >&2; exit 0; }
[[ -f "$SCRIPT" ]] || fatal "migration script not found: $SCRIPT"
[[ -x "$SCRIPT" ]] || fatal "migration script not executable: $SCRIPT"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/jellyfindb-postgres-test.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

# ---- psql client stand-in -------------------------------------------------
# A tiny shim that impersonates the `psql` client argv the script uses:
#   psql_run:  PGPASSWORD=.. psql --host=.. --port=.. --username=.. --dbname=DB
#              -v ON_ERROR_STOP=1 [-tA] [-c <sql> | -f <file>]
# It routes to a per-DB SQLite file under $SHIM_STORE, tolerantly skipping
# Postgres-only session statements (SET ..., BEGIN, COMMIT) and answering
# -tA/-c COUNT queries from it.
SHIM="$WORK/psql-shim.sh"
SHIM_STORE="$WORK/pg-store"
mkdir -p "$SHIM_STORE"
cat > "$SHIM" <<'SHIMEOF'
#!/usr/bin/env bash
set -euo pipefail
DB=""
TUPLES=0
CMD=""
FILE=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --dbname=*) DB="${1#--dbname=}" ;;
        -d)         shift; DB="${1:-}" ;;
        --host=*|--port=*|--username=*) : ;;
        -v)         shift ;;  # e.g. ON_ERROR_STOP=1
        -tA|-At|-A|-t) TUPLES=1 ;;
        -c)         shift; CMD="${1:-}" ;;
        -f)         shift; FILE="${1:-}" ;;
        *)          : ;;
    esac
    shift
done
: "${DB:?shim: no DB name}"
STORE_FILE="$SHIM_STORE/$DB.sqlite"
# Seed the destination "schema": the stand-in mirrors the provider/plugin having
# already created the tables. Seeded from the source clone's schema handed via
# SHIM_SCHEMA.
if [[ ! -f "$STORE_FILE" && -n "${SHIM_SCHEMA:-}" && -f "$SHIM_SCHEMA" ]]; then
    sqlite3 "$STORE_FILE" < "$SHIM_SCHEMA"
fi
if [[ -n "$CMD" ]]; then
    SQL="$CMD"
elif [[ -n "$FILE" ]]; then
    SQL="$(cat "$FILE")"
else
    SQL="$(cat)"
fi
# Strip Postgres-only session statements — keep DELETE/INSERT/SELECT.
# (SQLite accepts double-quoted identifiers, so the quoted INSERTs load as-is.)
CLEAN="$(printf '%s\n' "$SQL" \
    | grep -viE '^[[:space:]]*SET[[:space:]]' \
    | grep -viE '^[[:space:]]*(BEGIN|COMMIT)[[:space:]]*;?[[:space:]]*$' \
    | grep -viE '^[[:space:]]*--')"
if [[ $TUPLES -eq 1 ]]; then
    sqlite3 -noheader "$STORE_FILE" "$CLEAN"
else
    printf '%s\n' "$CLEAN" | sqlite3 "$STORE_FILE"
fi
SHIMEOF
chmod +x "$SHIM"

src_count() { sqlite3 "$1" "SELECT COUNT(*) FROM \"$2\";"; }
store_count() {
    [[ -f "$SHIM_STORE/$1.sqlite" ]] || { echo MISSING; return; }
    sqlite3 "$SHIM_STORE/$1.sqlite" "SELECT COUNT(*) FROM \"$2\";" 2>/dev/null || echo MISSING
}

# ===========================================================================
# run_matrix <source> <SRC-db> <staging-db> <prod-db> <data-tables...>
# Drives the full dry-run/stage/commit matrix for one source.
# ===========================================================================
run_matrix() {
    local SOURCE="$1" SRC="$2" STAGE_DB="$3" PROD_DB="$4"; shift 4
    local -a DATA_TABLES=("$@")

    local SHIM_SCHEMA_FILE="$WORK/${SOURCE}-schema.sql"
    # Schema-only dump the shim uses to seed each stand-in DB. Exclude EF table.
    sqlite3 "$SRC" ".schema" | grep -v '__EFMigrationsHistory' > "$SHIM_SCHEMA_FILE"

    local STAGING="$WORK/${SOURCE}-staging"

    run() {  # run <mode-args...>
        MYSQL_UNUSED=1 \
        PSQL_CMD="$SHIM" PGDUMP_CMD="/does-not-exist-pgdump" \
        SHIM_STORE="$SHIM_STORE" SHIM_SCHEMA="$SHIM_SCHEMA_FILE" \
        JELLYFIN_DB="$SRC" PHANTOM_DB="$SRC" STAGING_DIR="$STAGING" \
        PG_STAGING_DB="$STAGE_DB" PG_PROD_DB="$PROD_DB" \
        bash "$SCRIPT" --source "$SOURCE" --skip-service-check "$@"
    }

    # ---------------------------------------------------------------
    head_ "[$SOURCE] 1. Dry-run: clones + predicts, writes nothing to Postgres"
    rm -rf "$STAGING"
    run >/dev/null

    [[ -f "$STAGING/${SOURCE}.clone.db" ]] && ok "clone produced" || bad "no clone produced"
    [[ -f "$STAGING/predicted-counts.tsv" ]] && ok "predicted-counts.tsv written" || bad "no predicted-counts.tsv"
    [[ -f "$STAGING/postgres-load.sql" ]] && ok "postgres-load.sql generated" || bad "no load set"

    if [[ "$SOURCE" == "jellyfin" ]]; then
        if grep -q '__EFMigrationsHistory' "$STAGING/predicted-counts.tsv"; then
            bad "__EFMigrationsHistory was NOT excluded from the copy"
        else
            ok "__EFMigrationsHistory excluded from the migrated table set"
        fi
    fi

    # identifiers double-quoted in the emitted INSERTs (case preserved for Postgres)
    if grep -qE '^INSERT INTO "[^"]+" VALUES' "$STAGING/postgres-load.sql" \
       && ! grep -qE '^INSERT INTO [A-Za-z_][A-Za-z0-9_]* VALUES' "$STAGING/postgres-load.sql"; then
        ok "load set quotes every table identifier (case-preserving)"
    else
        bad "load set has an unquoted INSERT identifier"
    fi
    # transaction + trigger suspension present
    grep -q 'session_replication_role = replica' "$STAGING/postgres-load.sql" \
        && ok "load set suspends referential triggers" || bad "no session_replication_role in load set"

    # predicted counts equal the real source counts
    local predok=1 t n
    while IFS=$'\t' read -r t n; do
        [[ "$(src_count "$SRC" "$t")" == "$n" ]] || { predok=0; bad "predicted $t=$n != source $(src_count "$SRC" "$t")"; }
    done < "$STAGING/predicted-counts.tsv"
    [[ $predok -eq 1 ]] && ok "every predicted count equals the source count"

    # no Postgres stand-in DB was written in dry-run
    [[ "$(store_count "$STAGE_DB" "${DATA_TABLES[0]}")" == "MISSING" ]] \
        && ok "dry-run wrote nothing to the staging Postgres DB" \
        || bad "dry-run wrote to a Postgres stand-in"

    # ---------------------------------------------------------------
    head_ "[$SOURCE] 2. --stage: loads inactive color, counts match, receipt written"
    run --stage >/dev/null

    [[ -f "$STAGING/.staging-validated" ]] && ok "staging-validated receipt written" || bad "no staging receipt"
    local stageok=1
    for t in "${DATA_TABLES[@]}"; do
        [[ "$(store_count "$STAGE_DB" "$t")" == "$(src_count "$SRC" "$t")" ]] \
            || { stageok=0; bad "inactive $t=$(store_count "$STAGE_DB" "$t") != source $(src_count "$SRC" "$t")"; }
    done
    [[ $stageok -eq 1 ]] && ok "inactive-color counts == source for all data tables"
    [[ "$(store_count "$PROD_DB" "${DATA_TABLES[0]}")" == "MISSING" ]] \
        && ok "--stage did not write the prod color" || bad "--stage wrote prod"

    # ---------------------------------------------------------------
    head_ "[$SOURCE] 3. --stage is idempotent: a second load converges"
    run --stage >/dev/null
    [[ "$(store_count "$STAGE_DB" "${DATA_TABLES[0]}")" == "$(src_count "$SRC" "${DATA_TABLES[0]}")" ]] \
        && ok "second --stage converged (no row doubling)" || bad "second --stage drifted counts"

    # ---------------------------------------------------------------
    head_ "[$SOURCE] 4. --commit refuses without a staging receipt (stage gate)"
    local STAGING2="$WORK/${SOURCE}-no-receipt"
    if PSQL_CMD="$SHIM" SHIM_STORE="$SHIM_STORE" SHIM_SCHEMA="$SHIM_SCHEMA_FILE" \
       JELLYFIN_DB="$SRC" PHANTOM_DB="$SRC" STAGING_DIR="$STAGING2" \
       PG_STAGING_DB="$STAGE_DB" PG_PROD_DB="${PROD_DB}_norcpt" \
       bash "$SCRIPT" --source "$SOURCE" --skip-service-check --commit >/dev/null 2>&1 </dev/null; then
        bad "--commit ran without a staging receipt"
    else
        ok "--commit hard-refused without a staging-validation receipt"
    fi

    # ---------------------------------------------------------------
    head_ "[$SOURCE] 5. --commit wrong confirmation aborts; prod untouched"
    if printf 'nope\n' | run --commit >/dev/null 2>&1; then
        bad "--commit proceeded on a wrong confirmation"
    else
        [[ "$(store_count "$PROD_DB" "${DATA_TABLES[0]}")" == "MISSING" ]] \
            && ok "wrong confirmation aborted; prod color untouched" \
            || bad "wrong confirmation still wrote prod"
    fi

    # ---------------------------------------------------------------
    head_ "[$SOURCE] 6. --commit with receipt + MIGRATE loads prod, counts match"
    printf 'MIGRATE\n' | run --commit >/dev/null
    local commitok=1
    for t in "${DATA_TABLES[@]}"; do
        [[ "$(store_count "$PROD_DB" "$t")" == "$(src_count "$SRC" "$t")" ]] \
            || { commitok=0; bad "prod $t=$(store_count "$PROD_DB" "$t") != source $(src_count "$SRC" "$t")"; }
    done
    [[ $commitok -eq 1 ]] && ok "prod-color counts == source for all data tables"
}

# ===========================================================================
# Synthetic jellyfin.db — a representative slice plus the EF history table.
# ===========================================================================
JF_SRC="$WORK/jellyfin.db"
sqlite3 "$JF_SRC" <<'SQL'
CREATE TABLE "__EFMigrationsHistory" (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT);
INSERT INTO "__EFMigrationsHistory" VALUES ('20240101_Init','10.11.9');
CREATE TABLE "Users" (Id TEXT PRIMARY KEY, Username TEXT NOT NULL, LastLogin INTEGER);
INSERT INTO "Users" VALUES ('u-1','alice',1000),('u-2','bob',1001),('u-3','carol o''brien',1002);
CREATE TABLE "BaseItems" (Id TEXT PRIMARY KEY, Name TEXT, Type TEXT);
INSERT INTO "BaseItems" VALUES ('b-1','Blade Runner','Movie'),('b-2','Firefly','Series');
CREATE TABLE "UserData" (UserId TEXT, ItemId TEXT, Played INTEGER, PRIMARY KEY(UserId,ItemId));
INSERT INTO "UserData" VALUES ('u-1','b-1',1),('u-2','b-1',0),('u-1','b-2',1);
CREATE TABLE "EmptyTable" (Id INTEGER PRIMARY KEY, V TEXT);
SQL

# ===========================================================================
# Synthetic phantom.db — a representative slice of the plugin schema shape.
# ===========================================================================
PH_SRC="$WORK/phantom.db"
sqlite3 "$PH_SRC" <<'SQL'
CREATE TABLE "plugin_meta" (Key TEXT PRIMARY KEY, Value TEXT);
INSERT INTO "plugin_meta" VALUES ('schema_version','12'),('note','carol o''brien ran it');
CREATE TABLE "phantom_items" (item_guid TEXT PRIMARY KEY, tmdb_id INTEGER, stub_path TEXT);
INSERT INTO "phantom_items" VALUES ('g-1',603,'/m/a'),('g-2',1437,'/m/b'),('g-3',NULL,NULL);
CREATE TABLE "user_prefs" (user_id TEXT, pref TEXT, PRIMARY KEY(user_id,pref));
INSERT INTO "user_prefs" VALUES ('u-1','dark'),('u-2','light');
CREATE TABLE "user_hidden_items" (user_id TEXT, item_guid TEXT, PRIMARY KEY(user_id,item_guid));
PRAGMA user_version = 16;
SQL

run_matrix jellyfin "$JF_SRC" jellyfin_inactive jellyfin_prod Users BaseItems UserData EmptyTable

# export fidelity: a tricky value (embedded apostrophe) round-tripped (jellyfin)
head_ "[jellyfin] export fidelity"
got="$(sqlite3 "$SHIM_STORE/jellyfin_inactive.sqlite" "SELECT Username FROM Users WHERE Id='u-3';")"
[[ "$got" == "carol o'brien" ]] && ok "row data round-trips faithfully (quoted value intact)" \
    || bad "export corrupted data: got '$got'"

run_matrix phantom "$PH_SRC" phantom_dev phantom_prod plugin_meta phantom_items user_prefs user_hidden_items

# export fidelity for phantom too
head_ "[phantom] export fidelity"
got="$(sqlite3 "$SHIM_STORE/phantom_dev.sqlite" "SELECT Value FROM plugin_meta WHERE Key='note';")"
[[ "$got" == "carol o'brien ran it" ]] && ok "phantom row data round-trips faithfully" \
    || bad "phantom export corrupted data: got '$got'"
# NULLs preserved
gotnull="$(sqlite3 "$SHIM_STORE/phantom_dev.sqlite" "SELECT COUNT(*) FROM phantom_items WHERE stub_path IS NULL;")"
[[ "$gotnull" == "1" ]] && ok "phantom NULLs preserved through the copy" \
    || bad "phantom NULL not preserved (got $gotnull)"

# ===========================================================================
# Schema-version guard (--source phantom only): refuses a mismatched
# PRAGMA user_version, and an explicit override lets a pinned rehearsal
# proceed anyway. Mirrors phantom-migrate-v11-to-v12.sh's own user_version gate.
# ===========================================================================
head_ "[phantom] schema-version guard"

PH_SRC_OLD="$WORK/phantom-v11.db"
sqlite3 "$PH_SRC_OLD" <<'SQL'
CREATE TABLE "plugin_meta" (Key TEXT PRIMARY KEY, Value TEXT);
INSERT INTO "plugin_meta" VALUES ('note','stale');
PRAGMA user_version = 11;
SQL

STAGING_GUARD="$WORK/phantom-guard-mismatch"
if PSQL_CMD="$SHIM" SHIM_STORE="$SHIM_STORE" \
   PHANTOM_DB="$PH_SRC_OLD" STAGING_DIR="$STAGING_GUARD" \
   PG_STAGING_DB="phantom_guard_dev" PG_PROD_DB="phantom_guard_prod" \
   bash "$SCRIPT" --source phantom --skip-service-check >/dev/null 2>"$WORK/guard-mismatch.err"; then
    bad "migration ran against a v11 phantom.db (expected v16) — schema-version guard did not refuse"
else
    grep -q 'schema-version guard' "$WORK/guard-mismatch.err" \
        && ok "schema-version guard hard-refused a v11 source (expected v16)" \
        || bad "refused for the wrong reason: $(cat "$WORK/guard-mismatch.err")"
fi
[[ -d "$STAGING_GUARD" ]] && ls "$STAGING_GUARD"/*.clone.db >/dev/null 2>&1 \
    && bad "guard refusal still produced a clone (should refuse before stage 1)" \
    || ok "guard refusal ran before any clone was produced"

STAGING_GUARD_OK="$WORK/phantom-guard-override"
if PSQL_CMD="$SHIM" SHIM_STORE="$SHIM_STORE" \
   PHANTOM_DB="$PH_SRC_OLD" STAGING_DIR="$STAGING_GUARD_OK" \
   PG_STAGING_DB="phantom_guard_dev2" PG_PROD_DB="phantom_guard_prod2" \
   PHANTOM_EXPECTED_SCHEMA_VERSION=11 \
   bash "$SCRIPT" --source phantom --skip-service-check >/dev/null 2>"$WORK/guard-override.err"; then
    ok "PHANTOM_EXPECTED_SCHEMA_VERSION override lets a pinned-version rehearsal proceed"
else
    bad "override run unexpectedly refused: $(cat "$WORK/guard-override.err")"
fi

# ===========================================================================
head_ "Summary"
printf '  passed: %d   failed: %d\n' "$pass_count" "$fail_count"
[[ "$fail_count" -eq 0 ]] || { printf '\033[31mREGRESSION TEST FAILED\033[0m\n' >&2; exit 1; }
printf '\033[32mALL SQLite -> POSTGRES MIGRATION REGRESSION ASSERTIONS PASSED\033[0m\n'
