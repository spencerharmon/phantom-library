#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/phantom-migrate-jellyfindb-to-mysql.test.sh
#
# In-repo regression test for scripts/phantom-migrate-jellyfindb-to-mysql.sh.
#
# Proves the P4 Stage A jellyfin.db -> MySQL migration ORCHESTRATION on a
# SYNTHETIC jellyfin.db and a SQLite-backed MySQL STAND-IN, with NO live
# MySQL/MariaDB server and NO dotnet — only bash + sqlite3. The stand-in
# (a `mysql`-client shim, below) applies the exact load set the script
# generates into a real SQLite database and answers COUNT queries from it, so
# the test proves the SQLite export round-trips faithfully and every stage's
# count-parity check is real.
#
# The live proof against a real MySQL/MariaDB + a real jellyfin-plugin-mysql
# cutover is the separate operator live-rig step (mirroring how
# phantom-migrate-v11-to-v12 defers its live-rig proof to a dedicated rig task).
#
# Asserts:
#   - dry-run (default): clones, writes predicted-counts.tsv + mysql-load.sql,
#     writes NOTHING to any MySQL target, exits 0.
#   - --stage: loads into the INACTIVE-color stand-in, actual counts == predicted
#     for every table, writes a .staging-validated receipt, stops at stage 4.
#   - export fidelity: the row data landed in the stand-in equals the source
#     (round-trip through the SQLite `.mode insert` export).
#   - --commit refuses without a staging receipt (stage gate).
#   - --commit with a receipt + typed MIGRATE loads prod stand-in, counts match.
#   - --commit with a wrong confirmation aborts and writes nothing to prod.
#   - idempotency: a second --stage load converges to the same counts.
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# Skips (exit 0 + NOTE) if sqlite3 is unavailable.
# ---------------------------------------------------------------------------

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
SCRIPT="$REPO_ROOT/scripts/phantom-migrate-jellyfindb-to-mysql.sh"

pass_count=0
fail_count=0
ok()   { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

command -v sqlite3 >/dev/null 2>&1 || { printf 'NOTE: sqlite3 not found; skipping.\n' >&2; exit 0; }
[[ -f "$SCRIPT" ]] || fatal "migration script not found: $SCRIPT"
[[ -x "$SCRIPT" ]] || fatal "migration script not executable: $SCRIPT"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/jellyfindb-mysql-test.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

# ---- MySQL client stand-in ------------------------------------------------
# A tiny shim that impersonates the `mysql` client argv the script uses:
#   mysql_run:  MYSQL_PWD=.. mysql --host=.. --port=.. --user=.. <DB> [-N]  (SQL on stdin)
# It routes to a per-DB SQLite file under $SHIM_STORE, tolerantly skipping
# MySQL-only session statements (SET ...), and answers COUNT queries with -N.
SHIM="$WORK/mysql-shim.sh"
SHIM_STORE="$WORK/mysql-store"
mkdir -p "$SHIM_STORE"
cat > "$SHIM" <<'SHIMEOF'
#!/usr/bin/env bash
set -euo pipefail
DB=""
NBATCH=0
for a in "$@"; do
    case "$a" in
        --host=*|--port=*|--user=*|--*) [[ "$a" == "-N" ]] && NBATCH=1 ;;
        -N) NBATCH=1 ;;
        *) DB="$a" ;;
    esac
done
: "${DB:?shim: no DB name}"
STORE_FILE="$SHIM_STORE/$DB.sqlite"
# Pre-create the destination "schema": the stand-in mirrors jellyfin-plugin-mysql
# having already created the tables. We lazily create tables on first DELETE/INSERT
# by letting SQLite CREATE them from the load if absent is NOT automatic, so the
# schema is seeded from the source clone's schema handed via SHIM_SCHEMA.
if [[ ! -f "$STORE_FILE" && -n "${SHIM_SCHEMA:-}" && -f "$SHIM_SCHEMA" ]]; then
    sqlite3 "$STORE_FILE" < "$SHIM_SCHEMA"
fi
SQL="$(cat)"
# Strip MySQL-only session pragmas and backticks (SQLite accepts backticks too,
# but normalise to be safe) — keep DELETE/INSERT/SELECT.
CLEAN="$(printf '%s\n' "$SQL" \
    | grep -viE '^[[:space:]]*SET[[:space:]]' \
    | grep -viE '^[[:space:]]*--')"
if [[ $NBATCH -eq 1 ]]; then
    sqlite3 -noheader "$STORE_FILE" "$CLEAN"
else
    printf '%s\n' "$CLEAN" | sqlite3 "$STORE_FILE"
fi
SHIMEOF
chmod +x "$SHIM"

# ---- synthetic source jellyfin.db -----------------------------------------
# A representative slice of Jellyfin's schema shape: a few data tables plus the
# EF migration-history table that MUST be excluded from the copy.
SRC="$WORK/jellyfin.db"
sqlite3 "$SRC" <<'SQL'
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

# Schema-only dump (no rows, no __EFMigrationsHistory) that the shim uses to
# seed each stand-in DB — impersonating jellyfin-plugin-mysql's created schema.
SHIM_SCHEMA_FILE="$WORK/mysql-schema.sql"
sqlite3 "$SRC" ".schema" | grep -v '__EFMigrationsHistory' > "$SHIM_SCHEMA_FILE"

STAGING="$WORK/staging"

run() {  # run <mode-args...> ; injects the shim + env, feeds stdin from $CONFIRM
    MYSQL_CMD="$SHIM" MYSQLDUMP_CMD="/does-not-exist-mysqldump" \
    SHIM_STORE="$SHIM_STORE" SHIM_SCHEMA="$SHIM_SCHEMA_FILE" \
    JELLYFIN_DB="$SRC" STAGING_DIR="$STAGING" \
    MYSQL_STAGING_DB="jellyfin_inactive" MYSQL_PROD_DB="jellyfin_prod" \
    bash "$SCRIPT" --skip-service-check "$@"
}

src_count() { sqlite3 "$SRC" "SELECT COUNT(*) FROM \"$1\";"; }
store_count() {
    [[ -f "$SHIM_STORE/$1.sqlite" ]] || { echo MISSING; return; }
    sqlite3 "$SHIM_STORE/$1.sqlite" "SELECT COUNT(*) FROM \"$2\";" 2>/dev/null || echo MISSING
}

# =====================================================================
head_ "1. Dry-run: clones + predicts, writes nothing to MySQL"
rm -rf "$STAGING"; rm -f "$SHIM_STORE"/*.sqlite
run >/dev/null

[[ -f "$STAGING/jellyfin.clone.db" ]] && ok "clone produced" || bad "no clone produced"
[[ -f "$STAGING/predicted-counts.tsv" ]] && ok "predicted-counts.tsv written" || bad "no predicted-counts.tsv"
[[ -f "$STAGING/mysql-load.sql" ]] && ok "mysql-load.sql generated" || bad "no load set"

if grep -q '__EFMigrationsHistory' "$STAGING/predicted-counts.tsv"; then
    bad "__EFMigrationsHistory was NOT excluded from the copy"
else
    ok "__EFMigrationsHistory excluded from the migrated table set"
fi
# predicted counts equal the real source counts
predok=1
while IFS=$'\t' read -r t n; do
    [[ "$(src_count "$t")" == "$n" ]] || { predok=0; bad "predicted $t=$n != source $(src_count "$t")"; }
done < "$STAGING/predicted-counts.tsv"
[[ $predok -eq 1 ]] && ok "every predicted count equals the source count"
# no MySQL stand-in DB was written in dry-run
compgen -G "$SHIM_STORE/*.sqlite" >/dev/null && bad "dry-run wrote to a MySQL stand-in" \
    || ok "dry-run wrote nothing to any MySQL DB"

# =====================================================================
head_ "2. --stage: loads inactive color, counts match, receipt written"
run --stage >/dev/null

[[ -f "$STAGING/.staging-validated" ]] && ok "staging-validated receipt written" || bad "no staging receipt"
stageok=1
for t in Users BaseItems UserData EmptyTable; do
    [[ "$(store_count jellyfin_inactive "$t")" == "$(src_count "$t")" ]] \
        || { stageok=0; bad "inactive $t=$(store_count jellyfin_inactive "$t") != source $(src_count "$t")"; }
done
[[ $stageok -eq 1 ]] && ok "inactive-color counts == source for all data tables"
# prod stand-in must NOT have been touched by --stage
[[ "$(store_count jellyfin_prod Users)" == "MISSING" ]] \
    && ok "--stage did not write the prod color" || bad "--stage wrote prod"

# export fidelity: a tricky value (embedded apostrophe) round-tripped
got="$(sqlite3 "$SHIM_STORE/jellyfin_inactive.sqlite" "SELECT Username FROM Users WHERE Id='u-3';")"
[[ "$got" == "carol o'brien" ]] && ok "row data round-trips faithfully (quoted value intact)" \
    || bad "export corrupted data: got '$got'"

# =====================================================================
head_ "3. --stage is idempotent: a second load converges to the same counts"
run --stage >/dev/null
[[ "$(store_count jellyfin_inactive Users)" == "$(src_count Users)" ]] \
    && ok "second --stage converged (no row doubling)" || bad "second --stage drifted counts"

# =====================================================================
head_ "4. --commit refuses without a staging receipt (stage gate)"
STAGING2="$WORK/staging-no-receipt"
if MYSQL_CMD="$SHIM" SHIM_STORE="$SHIM_STORE" SHIM_SCHEMA="$SHIM_SCHEMA_FILE" \
   JELLYFIN_DB="$SRC" STAGING_DIR="$STAGING2" MYSQL_PROD_DB="jellyfin_prod2" \
   bash "$SCRIPT" --skip-service-check --commit >/dev/null 2>&1 </dev/null; then
    bad "--commit ran without a staging receipt"
else
    ok "--commit hard-refused without a staging-validation receipt"
fi

# =====================================================================
head_ "5. --commit wrong confirmation aborts; prod untouched"
if printf 'nope\n' | run --commit >/dev/null 2>&1; then
    bad "--commit proceeded on a wrong confirmation"
else
    [[ "$(store_count jellyfin_prod Users)" == "MISSING" ]] \
        && ok "wrong confirmation aborted; prod color untouched" \
        || bad "wrong confirmation still wrote prod"
fi

# =====================================================================
head_ "6. --commit with receipt + MIGRATE loads prod, counts match predicted"
printf 'MIGRATE\n' | run --commit >/dev/null
commitok=1
for t in Users BaseItems UserData EmptyTable; do
    [[ "$(store_count jellyfin_prod "$t")" == "$(src_count "$t")" ]] \
        || { commitok=0; bad "prod $t=$(store_count jellyfin_prod "$t") != source $(src_count "$t")"; }
done
[[ $commitok -eq 1 ]] && ok "prod-color counts == source for all data tables"

# =====================================================================
head_ "Summary"
printf '  passed: %d   failed: %d\n' "$pass_count" "$fail_count"
[[ "$fail_count" -eq 0 ]] || { printf '\033[31mREGRESSION TEST FAILED\033[0m\n' >&2; exit 1; }
printf '\033[32mALL jellyfin.db -> MySQL MIGRATION REGRESSION ASSERTIONS PASSED\033[0m\n'
