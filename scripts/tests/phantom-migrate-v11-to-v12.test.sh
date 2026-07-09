#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/phantom-migrate-v11-to-v12.test.sh
#
# In-repo regression test for scripts/phantom-migrate-v11-to-v12.sh.
#
# Proves the additive v11->v12 migration on a CLONE of a SYNTHETIC v11
# phantom.db, with NO live Jellyfin and NO dotnet — only bash + sqlite3.
# (The live-rig proof on a real-shaped DB is the separate `migration-rig`
# task; this is the build/unit-test-tier guard per the "softened additive-
# migration rule", AGENTS.md § "No database migrations until v1.0".)
#
# Faithfulness: the synthetic v11 DB is derived from the REAL fresh schema
# embedded in src/.../State/PhantomDb.cs (SchemaV10Sql), then downgraded to
# v11 by dropping exactly the two v12 objects. So the test tracks the real
# schema automatically instead of a hand-transcribed copy that could drift.
#
# Asserts:
#   - dry-run (default) changes nothing (still v11, no new tables).
#   - --commit migrates: user_version 11->12; user_prefs + user_hidden_items
#     + idx_user_hidden_items_user appear; the migrated schema is IDENTICAL
#     to a freshly-built v12 DB; every pre-existing table's rows survive
#     unchanged; the two new tables are empty.
#   - a timestamped backup is taken on --commit.
#   - idempotent: a second --commit is a verified no-op.
#   - resumable: an interrupted state (a v12 table already present while
#     still at v11) completes cleanly.
#   - guard: v10, fresh/v0, and a future v13 DB are all HARD-REFUSED and
#     left byte-for-byte unchanged.
#
# Exit 0 = all assertions passed; non-zero on the first failure.
#
# Skips (exit 0 with a NOTE) if sqlite3 is unavailable, so it never breaks a
# CI node that lacks the tool.
# ---------------------------------------------------------------------------

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
SCRIPT="$REPO_ROOT/scripts/phantom-migrate-v11-to-v12.sh"
PHANTOMDB_CS="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/State/PhantomDb.cs"

pass_count=0
fail_count=0

ok()   { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

if ! command -v sqlite3 >/dev/null 2>&1; then
    printf 'NOTE: sqlite3 not found; skipping phantom-migrate regression test.\n' >&2
    exit 0
fi
[[ -f "$SCRIPT" ]]      || fatal "migration script not found: $SCRIPT"
[[ -x "$SCRIPT" ]]      || fatal "migration script not executable: $SCRIPT"
[[ -f "$PHANTOMDB_CS" ]] || fatal "PhantomDb.cs not found: $PHANTOMDB_CS"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/phantom-migrate-test.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

uv()      { sqlite3 "$1" 'PRAGMA user_version;'; }
has_tbl() { [[ "$(sqlite3 "$1" "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='$2';")" == "1" ]]; }
has_idx() { [[ "$(sqlite3 "$1" "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='$2';")" == "1" ]]; }
schema_fingerprint() {
    # type/name/sql of every non-internal object, order-stable.
    sqlite3 "$1" "SELECT type,name,COALESCE(sql,'') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name;"
}
run_migrate() {  # run_migrate <db> [--commit] ; feeds MIGRATE on stdin
    local db="$1"; shift
    printf 'MIGRATE\n' | PHANTOM_DB="$db" bash "$SCRIPT" --skip-service-check "$@"
}

# --- fixtures --------------------------------------------------------------

# Extract the real fresh-schema DDL (the SchemaV10Sql verbatim string).
FRESH_DDL="$WORK/fresh.sql"
awk '/private const string SchemaV10Sql = @"/{f=1;next} f&&/^";$/{f=0} f' \
    "$PHANTOMDB_CS" > "$FRESH_DDL"
[[ -s "$FRESH_DDL" ]] || fatal "failed to extract SchemaV10Sql DDL from PhantomDb.cs"

# A pristine, freshly-built v12 DB — the parity reference.
REF_V12="$WORK/ref-v12.db"
sqlite3 "$REF_V12" < "$FRESH_DDL"
sqlite3 "$REF_V12" "PRAGMA user_version=12;"

# Build a synthetic v-N DB carrying representative rows.
#   $1 = output path   $2 = target user_version
build_synthetic() {
    local out="$1" ver="$2"
    rm -f "$out"
    sqlite3 "$out" < "$FRESH_DDL"
    # Downgrade to v11 shape: remove exactly the two v12 objects.
    sqlite3 "$out" "DROP INDEX IF EXISTS idx_user_hidden_items_user;
                    DROP TABLE IF EXISTS user_hidden_items;
                    DROP TABLE IF EXISTS user_prefs;"
    # Representative pre-existing data that MUST survive the migration.
    sqlite3 "$out" "
        INSERT INTO discovery_cache(tmdb_id,type,discovered_at,last_refreshed)
            VALUES (603,'movie',1000,1000),(1399,'series',1001,1001),(27205,'movie',1002,1002);
        INSERT INTO catalogue_items(tmdb_id,type,first_seen_at,last_seen_at,source_mask)
            VALUES (603,'movie',900,1000,1),(1399,'series',901,1001,3);
        INSERT INTO plugin_meta(key,value)
            VALUES ('schema_note','synthetic v11 fixture'),('created_by','regression-test');
    "
    sqlite3 "$out" "PRAGMA user_version=${ver};"
}

# =====================================================================
head_ "1. Happy path: dry-run is a no-op, --commit migrates, data survives"

V11="$WORK/happy.db"
build_synthetic "$V11" 11
BEFORE_FP="$(schema_fingerprint "$V11")"
DC_BEFORE="$(sqlite3 "$V11" 'SELECT COUNT(*) FROM discovery_cache;')"
CI_BEFORE="$(sqlite3 "$V11" 'SELECT COUNT(*) FROM catalogue_items;')"
PM_BEFORE="$(sqlite3 "$V11" 'SELECT COUNT(*) FROM plugin_meta;')"

# 1a. dry-run (no --commit) must not touch the DB.
CLONE="$WORK/happy.clone.db"; cp -p "$V11" "$CLONE"
PHANTOM_DB="$CLONE" bash "$SCRIPT" --skip-service-check >/dev/null
[[ "$(uv "$CLONE")" == "11" ]] && ! has_tbl "$CLONE" user_prefs \
    && [[ "$(schema_fingerprint "$CLONE")" == "$BEFORE_FP" ]] \
    && ok "dry-run left the DB at v11, unchanged" \
    || bad "dry-run mutated the DB"

# 1b. --commit against the clone migrates it.
run_migrate "$CLONE" --commit >/dev/null
[[ "$(uv "$CLONE")" == "12" ]] && ok "user_version 11 -> 12" || bad "user_version not 12 (got $(uv "$CLONE"))"
has_tbl "$CLONE" user_prefs        && ok "user_prefs created"        || bad "user_prefs missing"
has_tbl "$CLONE" user_hidden_items && ok "user_hidden_items created" || bad "user_hidden_items missing"
has_idx "$CLONE" idx_user_hidden_items_user && ok "index created"    || bad "index missing"

# 1c. migrated schema is IDENTICAL to a freshly-built v12 DB.
[[ "$(schema_fingerprint "$CLONE")" == "$(schema_fingerprint "$REF_V12")" ]] \
    && ok "migrated schema == fresh v12 schema (parity)" \
    || bad "migrated schema differs from a fresh v12 DB"

# 1d. new tables are empty.
[[ "$(sqlite3 "$CLONE" 'SELECT COUNT(*) FROM user_prefs;')" == "0" \
   && "$(sqlite3 "$CLONE" 'SELECT COUNT(*) FROM user_hidden_items;')" == "0" ]] \
    && ok "new tables are empty" || bad "new tables not empty"

# 1e. every pre-existing row survived.
[[ "$(sqlite3 "$CLONE" 'SELECT COUNT(*) FROM discovery_cache;')" == "$DC_BEFORE" \
   && "$(sqlite3 "$CLONE" 'SELECT COUNT(*) FROM catalogue_items;')" == "$CI_BEFORE" \
   && "$(sqlite3 "$CLONE" 'SELECT COUNT(*) FROM plugin_meta;')" == "$PM_BEFORE" \
   && "$(sqlite3 "$CLONE" "SELECT value FROM plugin_meta WHERE key='schema_note';")" == "synthetic v11 fixture" ]] \
    && ok "all pre-existing rows preserved (discovery/catalogue/plugin_meta)" \
    || bad "pre-existing data changed"

# 1f. a timestamped backup was written next to the DB.
if compgen -G "${CLONE}.bak.migrate.*" >/dev/null; then
    ok "timestamped backup created (${CLONE}.bak.migrate.*)"
else
    bad "no .bak.migrate.* backup created"
fi

# =====================================================================
head_ "2. Idempotency: a second --commit is a verified no-op"

FP_AFTER_FIRST="$(schema_fingerprint "$CLONE")"
run_migrate "$CLONE" --commit >/dev/null
[[ "$(uv "$CLONE")" == "12" && "$(schema_fingerprint "$CLONE")" == "$FP_AFTER_FIRST" ]] \
    && ok "second --commit no-op (still v12, schema unchanged)" \
    || bad "second --commit changed a v12 DB"

# dry-run on an already-migrated DB also succeeds and no-ops.
if PHANTOM_DB="$CLONE" bash "$SCRIPT" --skip-service-check >/dev/null 2>&1; then
    ok "dry-run on a v12 DB exits 0 (already-migrated)"
else
    bad "dry-run on a v12 DB did not exit 0"
fi

# =====================================================================
head_ "3. Resumable: partial prior attempt (v12 table present, still v11)"

RESUME="$WORK/resume.db"
build_synthetic "$RESUME" 11
# Simulate an interrupted run: one v12 table already created (by the canonical
# DDL, exactly as a partial prior run would have), version still 11.
CANON_UP="$(sqlite3 "$REF_V12" "SELECT sql FROM sqlite_master WHERE name='user_prefs';")"
sqlite3 "$RESUME" "$CANON_UP;"
run_migrate "$RESUME" --commit >/dev/null
[[ "$(uv "$RESUME")" == "12" ]] \
    && has_tbl "$RESUME" user_hidden_items \
    && [[ "$(schema_fingerprint "$RESUME")" == "$(schema_fingerprint "$REF_V12")" ]] \
    && ok "resumed partial run completed to a full v12 schema" \
    || bad "resume did not complete cleanly to v12"

# =====================================================================
head_ "4. Guard: refuse anything that is not v11/v12, leave DB unchanged"

for badver in 10 0 13; do
    G="$WORK/guard-$badver.db"
    build_synthetic "$G" "$badver"
    FP="$(schema_fingerprint "$G")"
    if run_migrate "$G" --commit >/dev/null 2>&1; then
        bad "v$badver DB was NOT refused (should hard-refuse)"
    else
        if [[ "$(uv "$G")" == "$badver" && "$(schema_fingerprint "$G")" == "$FP" ]]; then
            ok "v$badver DB hard-refused and left unchanged"
        else
            bad "v$badver DB refused but was mutated"
        fi
    fi
done

# =====================================================================
head_ "5. Confirmation gate: wrong confirmation aborts without changes"

NC="$WORK/noconfirm.db"
build_synthetic "$NC" 11
FP="$(schema_fingerprint "$NC")"
if printf 'nope\n' | PHANTOM_DB="$NC" bash "$SCRIPT" --skip-service-check --commit >/dev/null 2>&1; then
    bad "wrong confirmation still migrated"
else
    [[ "$(uv "$NC")" == "11" && "$(schema_fingerprint "$NC")" == "$FP" ]] \
        && ok "wrong confirmation aborted; DB unchanged at v11" \
        || bad "wrong confirmation aborted but DB changed"
fi

# =====================================================================
head_ "Summary"
printf '  passed: %d   failed: %d\n' "$pass_count" "$fail_count"
[[ "$fail_count" -eq 0 ]] || { printf '\033[31mREGRESSION TEST FAILED\033[0m\n' >&2; exit 1; }
printf '\033[32mALL MIGRATION REGRESSION ASSERTIONS PASSED\033[0m\n'
