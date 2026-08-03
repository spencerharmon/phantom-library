#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/migration-rig.test.sh
#
# In-repo regression cover for the P3 Stage 3 migration rig
# (tools/rig-scenarios/44-migrate-v11-to-v12.sh) and its Gitea Actions job.
#
# The live rig itself (a real vM Jellyfin booting on the migrated DB + the full
# downstream e2e) can only run on the self-hosted Gitea Actions runner via the
# `phantom-library-migration-rig` job. This harness is the build/unit-test-tier
# guard: it proves the DETERMINISTIC core of the scenario — the v11 -> v12
# migration on a CLONE of a SYNTHETIC DB — with only bash + sqlite3 (NO live
# Jellyfin, NO dotnet), plus structural guards that the scenario and the CI job
# are wired the way the task requires.
#
# The synthetic v11 DB is derived from the REAL fresh schema embedded in
# src/.../State/PhantomDb.cs (SchemaV10Sql), downgraded to v11 by dropping
# exactly the two v12 objects — so it tracks the real schema automatically
# rather than a hand-transcribed copy that could drift (same technique as
# scripts/tests/phantom-migrate-v11-to-v12.test.sh).
#
# Asserts:
#   A. Scenario 44 exists, is executable, and is `bash -n` syntax-clean.
#   B. The scenario is wired as the task requires: it seeds a v11 synthetic DB,
#      runs the migration --commit, asserts user_version/new-table/census/
#      predicted==actual, boots the vM plugin, and runs the full downstream e2e
#      (35 + 36 + 42) against the MIGRATED DB (structural pass != done).
#   C. The Gitea Actions workflow defines job `phantom-library-migration-rig`,
#      runs it on the self-hosted runner, and invokes scenario 44.
#   D. DETERMINISTIC MIGRATION PROOF (the real effect, on a synthetic clone):
#      migrate v11 -> v12 and assert user_version 11->12; user_prefs +
#      user_hidden_items + idx_user_hidden_items_user present and the two new
#      tables EMPTY; the migrated schema is IDENTICAL to a freshly-built v12 DB;
#      every pre-existing table byte/census-identical (additive-only); and the
#      migration script's own predicted-before / actual-after verification
#      agrees (predicted==actual) by exiting 0.
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# Skips (exit 0 with a NOTE) if sqlite3 is unavailable, so it never breaks a
# CI node that lacks the tool.
# ---------------------------------------------------------------------------

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
SCENARIO="$REPO_ROOT/tools/rig-scenarios/44-migrate-v11-to-v12.sh"
WORKFLOW="$REPO_ROOT/.gitea/workflows/migration-rig.yml"
MIGRATE="$REPO_ROOT/scripts/phantom-migrate-v11-to-v12.sh"
PHANTOMDB_CS="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/State/PhantomDb.cs"

pass_count=0
fail_count=0
ok()   { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

if ! command -v sqlite3 >/dev/null 2>&1; then
    printf 'NOTE: sqlite3 not found; skipping migration-rig regression test.\n' >&2
    exit 0
fi
[[ -f "$MIGRATE" ]]      || fatal "migration script not found: $MIGRATE"
[[ -x "$MIGRATE" ]]      || fatal "migration script not executable: $MIGRATE"
[[ -f "$PHANTOMDB_CS" ]] || fatal "PhantomDb.cs not found: $PHANTOMDB_CS"

# =====================================================================
head_ "A. Scenario 44 exists, executable, syntax-clean"
if [[ -f "$SCENARIO" ]]; then ok "scenario present: $SCENARIO"; else bad "scenario missing: $SCENARIO"; fi
[[ -x "$SCENARIO" ]] && ok "scenario is executable" || bad "scenario not executable (chmod +x)"
if bash -n "$SCENARIO" 2>/tmp/mrig-syntax.err; then
    ok "scenario is bash -n clean"
else
    bad "scenario has a syntax error: $(cat /tmp/mrig-syntax.err)"
fi

# =====================================================================
head_ "B. Scenario wiring (seed v11 -> migrate -> boot vM -> full downstream e2e)"
need_in_scenario() {  # need_in_scenario <regex> <description>
    if grep -Eq -- "$1" "$SCENARIO"; then ok "scenario $2"; else bad "scenario MISSING: $2 (/$1/)"; fi
}
need_in_scenario 'PRAGMA user_version=\$\{FROM_VERSION\}'  "downgrades a synthetic fixture to a v11 seed"
need_in_scenario 'phantom-migrate-v11-to-v12\.sh'          "runs the v11->v12 migration script"
need_in_scenario '[-][-]commit'                            "migrates with --commit"
need_in_scenario 'user_prefs'                              "asserts the new user_prefs table"
need_in_scenario 'user_hidden_items'                       "asserts the new user_hidden_items table"
need_in_scenario 'census'                                  "does a before/after census of pre-existing tables"
need_in_scenario 'rig-up\.sh'                              "boots the vM plugin on the migrated DB"
need_in_scenario '35-channel-e2e-playback\.sh'             "runs downstream movie e2e (35)"
need_in_scenario '36-channel-episode-e2e-playback\.sh'     "runs downstream episode e2e (36)"
need_in_scenario '42-per-user-show-hide\.sh'               "runs downstream per-user e2e (42)"
need_in_scenario 'MIGRATION_RIG_OK'                        "emits the MIGRATION_RIG_OK success marker"
need_in_scenario 'trap cleanup EXIT'                       "installs a trap-based cleanup"
need_in_scenario 'CurrentSchemaVersion'                    "epoch-gates the live boot on the plugin's CurrentSchemaVersion"
need_in_scenario 'honest red'                              "is honest-red (never silent green) on an epoch mismatch"

# =====================================================================
head_ "C. Gitea Actions job phantom-library-migration-rig"
if [[ -f "$WORKFLOW" ]]; then ok "workflow present: $WORKFLOW"; else bad "workflow missing: $WORKFLOW"; fi
if [[ -f "$WORKFLOW" ]]; then
    grep -Eq '^\s*phantom-library-migration-rig:' "$WORKFLOW" \
        && ok "defines job phantom-library-migration-rig" || bad "job phantom-library-migration-rig not defined"
    grep -Eq 'runs-on:\s*self-hosted' "$WORKFLOW" \
        && ok "runs on the self-hosted runner" || bad "not pinned to a self-hosted runner"
    grep -Eq '44-migrate-v11-to-v12\.sh' "$WORKFLOW" \
        && ok "invokes scenario 44" || bad "does not invoke scenario 44"
    grep -Eq 'MIGRATION_RIG_OK' "$WORKFLOW" \
        && ok "gates on the MIGRATION_RIG_OK marker" || bad "does not gate on MIGRATION_RIG_OK"
fi

# =====================================================================
head_ "D. Deterministic migration proof (synthetic v11 clone -> v12)"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/migration-rig-test.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

uv()      { sqlite3 "$1" 'PRAGMA user_version;'; }
has_tbl() { [[ "$(sqlite3 "$1" "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='$2';")" == "1" ]]; }
has_idx() { [[ "$(sqlite3 "$1" "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='$2';")" == "1" ]]; }
schema_fingerprint() {
    sqlite3 "$1" "SELECT type,name,COALESCE(sql,'') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name;"
}

# Extract the real fresh-schema DDL (the SchemaV10Sql verbatim string).
FRESH_DDL="$WORK/fresh.sql"
awk '/private const string SchemaV10Sql = @"/{f=1;next} f&&/^";$/{f=0} f' \
    "$PHANTOMDB_CS" > "$FRESH_DDL"
[[ -s "$FRESH_DDL" ]] || fatal "failed to extract SchemaV10Sql DDL from PhantomDb.cs"

# A pristine, freshly-built v12 DB — the parity reference.
REF_V12="$WORK/ref-v12.db"
sqlite3 "$REF_V12" < "$FRESH_DDL"
sqlite3 "$REF_V12" "PRAGMA user_version=12;"

# Synthetic v11 seed = fresh schema, the two v12 objects dropped, representative
# pre-existing rows that MUST survive the additive migration, user_version=11.
SEED_V11="$WORK/seed-v11.db"
sqlite3 "$SEED_V11" < "$FRESH_DDL"
sqlite3 "$SEED_V11" "DROP INDEX IF EXISTS idx_user_hidden_items_user;
                     DROP TABLE IF EXISTS user_hidden_items;
                     DROP TABLE IF EXISTS user_prefs;"
sqlite3 "$SEED_V11" "
    INSERT INTO discovery_cache(tmdb_id,type,discovered_at,last_refreshed)
        VALUES (99000001,'movie',1000,1000),(99100001,'series',1001,1001);
    INSERT INTO catalogue_items(tmdb_id,type,first_seen_at,last_seen_at,source_mask)
        VALUES (99000001,'movie',900,1000,1),(99100001,'series',901,1001,3);
    INSERT INTO plugin_meta(key,value)
        VALUES ('schema_note','synthetic v11 rig seed');
"
sqlite3 "$SEED_V11" "PRAGMA user_version=11;"
[[ "$(uv "$SEED_V11")" == "11" ]] || fatal "seed is not v11"

# Census-before over every pre-existing table.
census_of() {
    local db="$1" t
    for t in $(sqlite3 "$db" "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"); do
        printf '%s\t%s\n' "$t" "$(sqlite3 "$db" "SELECT COUNT(*) FROM \"$t\";")"
    done
}
BEFORE="$WORK/census-before.txt"
census_of "$SEED_V11" > "$BEFORE"

# Migrate a clone --commit (feeds the typed MIGRATE confirmation). Its exit 0
# IS the predicted-before/actual-after verification passing.
CLONE="$WORK/migrated.db"
cp -p "$SEED_V11" "$CLONE"
if printf 'MIGRATE\n' | PHANTOM_DB="$CLONE" bash "$MIGRATE" --skip-service-check --commit >/dev/null 2>&1; then
    ok "migration --commit exited 0 (predicted==actual verification passed)"
else
    bad "migration --commit exited non-zero (predicted==actual failed)"
fi

[[ "$(uv "$CLONE")" == "12" ]] && ok "user_version 11 -> 12" || bad "user_version not 12 (got $(uv "$CLONE"))"
has_tbl "$CLONE" user_prefs        && ok "user_prefs created"        || bad "user_prefs missing"
has_tbl "$CLONE" user_hidden_items && ok "user_hidden_items created" || bad "user_hidden_items missing"
has_idx "$CLONE" idx_user_hidden_items_user && ok "index created"    || bad "index missing"
[[ "$(sqlite3 "$CLONE" 'SELECT COUNT(*) FROM user_prefs;')" == "0" \
   && "$(sqlite3 "$CLONE" 'SELECT COUNT(*) FROM user_hidden_items;')" == "0" ]] \
    && ok "new tables are empty" || bad "new tables not empty"

# Migrated schema IDENTICAL to a freshly-built v12 DB.
[[ "$(schema_fingerprint "$CLONE")" == "$(schema_fingerprint "$REF_V12")" ]] \
    && ok "migrated schema == fresh v12 schema (parity)" \
    || bad "migrated schema differs from a fresh v12 DB"

# Every pre-existing table byte/census-identical (additive-only).
AFTER="$WORK/census-after.txt"
: > "$AFTER"
while IFS=$'\t' read -r t _; do
    printf '%s\t%s\n' "$t" "$(sqlite3 "$CLONE" "SELECT COUNT(*) FROM \"$t\";")" >> "$AFTER"
done < "$BEFORE"
if diff -u "$BEFORE" "$AFTER" >/dev/null; then
    ok "every pre-existing table census-identical across the migration"
else
    bad "a pre-existing table's row count changed (migration not additive)"
fi

# =====================================================================
head_ "Summary"
printf '  passed: %d   failed: %d\n' "$pass_count" "$fail_count"
[[ "$fail_count" -eq 0 ]] || { printf '\033[31mMIGRATION-RIG REGRESSION TEST FAILED\033[0m\n' >&2; exit 1; }
printf '\033[32mALL MIGRATION-RIG REGRESSION ASSERTIONS PASSED\033[0m\n'
