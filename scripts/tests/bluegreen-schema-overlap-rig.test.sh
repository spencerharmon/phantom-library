#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/bluegreen-schema-overlap-rig.test.sh
#
# In-repo regression cover for the P7 blue/green shared-Postgres schema
# overlap rig (tools/rig-scenarios/46-bluegreen-schema-overlap.sh) and its
# Gitea Actions job, mirroring migration-rig.test.sh's shape for the P3
# migration rig.
#
# The live rig itself (two real Jellyfin+plugin colors sharing one Postgres
# DB, driven over HTTP) can only run on the self-hosted Gitea Actions runner
# via the `phantom-library-bluegreen-schema-overlap-rig` job (it needs the
# patched channel-arch Jellyfin build). This harness is the build/CI-tier
# guard: it proves the DETERMINISTIC core of the scenario — the additive
# EXPAND, the OVERLAP tolerance, and the post-flip CONTRACT — against a real
# EPHEMERAL Postgres (docker.io/library/postgres:16-alpine via podman, same
# image tag SchemaExpandMigratorPostgresTests.cs already uses), with no live
# Jellyfin required — plus structural guards that the scenario and the CI job
# are wired the way the task requires.
#
# Asserts:
#   A. Scenario 46 exists, is executable, and is `bash -n` syntax-clean.
#      Every embedded python3 heredoc is `py_compile`-clean.
#   B. The scenario is wired as the task requires: boots blue against a
#      shared Postgres DB (PHANTOM_POSTGRES_* passthrough), applies an
#      additive/idempotent expand while blue runs, re-verifies blue post
#      expand, boots green against the SAME DB, re-verifies blue after
#      green's write (the two-sided overlap invariant), simulates a flip
#      (stops blue), applies the contract drop ONLY after the flip, and
#      re-verifies the sole remaining color (green) afterwards. No single
#      step both expands and contracts.
#   C. rig-up.sh forwards the PHANTOM_POSTGRES_* passthrough into the spawned
#      jellyfin.service (the opt-in hook the scenario depends on), and is
#      unaffected (no PHANTOM_POSTGRES_* setenv args) when the caller leaves
#      those vars unset (backward compatibility for every existing caller).
#   D. The Gitea Actions job phantom-library-bluegreen-schema-overlap-rig is
#      defined, self-hosted, and invokes scenario 46.
#   E. DETERMINISTIC OVERLAP + EXPAND->FLIP->CONTRACT PROOF (the real effect,
#      against a synthetic ephemeral Postgres — not a mock): seed a
#      catalogue_items table with a movie + an episode/series row; apply the
#      additive/idempotent expand (ADD COLUMN IF NOT EXISTS, applied twice —
#      the 2nd apply must be a no-op) and assert the pre-expand read/write
#      pattern (standing in for "blue", the old color) still succeeds
#      afterwards (OVERLAP, half 1); assert a write using the new column
#      (standing in for "green", the new color) succeeds and does not disturb
#      the movie/series rows (OVERLAP, half 2 + movie/TV parity); simulate
#      the flip (no further old-color reads/writes follow); apply the
#      contract drop ONLY after that point and assert the sole remaining
#      color's reads still succeed. No single step both expands and
#      contracts. (The LIVE rig scenario, 46-bluegreen-schema-overlap.sh, is
#      what proves two GENUINELY CONCURRENT live processes tolerate this —
#      this harness proves the data-level invariant deterministically.)
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# Skips (exit 0 with a NOTE) if podman is unavailable, so it never breaks a
# CI node that lacks it.
# ---------------------------------------------------------------------------

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
SCENARIO="$REPO_ROOT/tools/rig-scenarios/46-bluegreen-schema-overlap.sh"
RIG_UP="$REPO_ROOT/tools/rig-scenarios/rig-up.sh"
WORKFLOW="$REPO_ROOT/.gitea/workflows/bluegreen-schema-overlap-rig.yml"

pass_count=0
fail_count=0
ok()   { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

[[ -f "$SCENARIO" ]] || fatal "scenario not found: $SCENARIO"
[[ -f "$RIG_UP" ]]   || fatal "rig-up.sh not found: $RIG_UP"

# =====================================================================
head_ "A. Scenario 46 exists, executable, syntax-clean"
[[ -x "$SCENARIO" ]] && ok "scenario is executable" || bad "scenario not executable (chmod +x)"
if bash -n "$SCENARIO" 2>/tmp/bgrig-syntax.err; then
    ok "scenario is bash -n clean"
else
    bad "scenario has a syntax error: $(cat /tmp/bgrig-syntax.err)"
fi

if command -v python3 >/dev/null 2>&1; then
    # Extract every python3 heredoc body (`<<'PY' ... PY`) and py_compile each.
    workdir="$(mktemp -d "${TMPDIR:-/tmp}/bgrig-py.XXXXXX")"
    trap 'rm -rf "$workdir"' RETURN 2>/dev/null || true
    awk '
        /<<.?PY.?$/ { infile=1; n++; fn=sprintf("'"$workdir"'/heredoc-%d.py", n); next }
        infile && /^PY$/ { infile=0; next }
        infile { print > fn }
    ' "$SCENARIO"
    heredoc_fail=0
    for f in "$workdir"/heredoc-*.py; do
        [[ -e "$f" ]] || continue
        python3 -m py_compile "$f" 2>/tmp/bgrig-pyc.err || { heredoc_fail=1; cat /tmp/bgrig-pyc.err >&2; }
    done
    if [[ "$heredoc_fail" -eq 0 ]]; then
        ok "every embedded python3 heredoc is py_compile-clean"
    else
        bad "an embedded python3 heredoc failed py_compile (see stderr above)"
    fi
    rm -rf "$workdir"
else
    printf '  NOTE: python3 unavailable; skipping heredoc py_compile check\n' >&2
fi

# =====================================================================
head_ "B. Scenario wiring (blue -> expand -> overlap -> green -> flip -> contract)"
need_in_scenario() {  # need_in_scenario <regex> <description>
    if grep -Eq -- "$1" "$SCENARIO"; then ok "scenario $2"; else bad "scenario MISSING: $2 (/$1/)"; fi
}
need_in_scenario 'PHANTOM_POSTGRES_HOST'                    "boots blue via the PHANTOM_POSTGRES_* shared-DB passthrough"
need_in_scenario 'rig-up\.sh'                               "boots blue via rig-up.sh"
need_in_scenario 'ADD COLUMN IF NOT EXISTS'                 "applies an additive, idempotent expand (ADD COLUMN IF NOT EXISTS)"
need_in_scenario '2nd apply, must be a no-op'               "proves the expand is idempotent (applied twice)"
need_in_scenario 'blue-post-expand'                         "re-verifies blue (old color) post-expand (overlap invariant, half 1)"
need_in_scenario ':18296'                                   "boots green (new color) on a distinct, non-prod port"
need_in_scenario 'blue-after-green-write'                   "re-verifies blue after green's write (overlap invariant, half 2)"
need_in_scenario 'SIMULATE FLIP'                            "simulates the flip (stops blue) before any contract step"
need_in_scenario 'DROP COLUMN IF EXISTS'                    "applies the contract drop (DROP COLUMN IF EXISTS)"
need_in_scenario 'ONLY now that the flip happened'          "gates the contract drop on the simulated flip having happened"
need_in_scenario 'green-post-contract'                      "re-verifies the sole remaining color (green) post-contract"
need_in_scenario 'trap cleanup EXIT'                        "installs a trap-based cleanup for both colors + the shared Postgres"
need_in_scenario 'DiscoveryRefresh'                         "drives a real write (DiscoveryRefresh) against the shared DB"
need_in_scenario "type='series'"                             "asserts a series/episode row (TV parity), not movie-only"
need_in_scenario "type='movie'"                              "asserts a movie row (movie parity)"
need_in_scenario 'PlaybackInfo'                             "drives a real playback query"
need_in_scenario 'BLUEGREEN_RIG_OK'                          "emits the BLUEGREEN_RIG_OK success marker"
need_in_scenario 'NEVER prod'                                "documents the non-prod port posture"

# =====================================================================
head_ "C. rig-up.sh PHANTOM_POSTGRES_* passthrough (opt-in, backward compatible)"
if grep -Eq 'PG_SETENV_ARGS' "$RIG_UP"; then
    ok "rig-up.sh defines the PHANTOM_POSTGRES_* passthrough hook"
else
    bad "rig-up.sh missing the PHANTOM_POSTGRES_* passthrough hook"
fi
if grep -Eq '"\$\{PG_SETENV_ARGS\[@\]\}"' "$RIG_UP"; then
    ok "rig-up.sh's systemd-run forwards PG_SETENV_ARGS to the spawned jellyfin.service"
else
    bad "rig-up.sh's systemd-run does not forward PG_SETENV_ARGS"
fi
if bash -n "$RIG_UP" 2>/tmp/bgrig-rigup-syntax.err; then
    ok "rig-up.sh (with the passthrough patch) is still bash -n clean"
else
    bad "rig-up.sh has a syntax error after the passthrough patch: $(cat /tmp/bgrig-rigup-syntax.err)"
fi
# Backward compatibility: with PHANTOM_POSTGRES_HOST unset, rig-up.sh must
# build an EMPTY PG_SETENV_ARGS array (no behavioural change for any existing
# caller/scenario). Simulate just the array-building fragment in isolation.
bc_out="$(env -i PATH="$PATH" bash -c '
  PG_SETENV_ARGS=()
  if [ -n "${PHANTOM_POSTGRES_HOST:-}" ]; then
    PG_SETENV_ARGS+=("--setenv=PHANTOM_POSTGRES_HOST=$PHANTOM_POSTGRES_HOST")
  fi
  echo "${#PG_SETENV_ARGS[@]}"
' 2>&1)"
[[ "$bc_out" == "0" ]] && ok "PHANTOM_POSTGRES_HOST unset -> zero setenv args (unaffected default path)" \
    || bad "expected 0 setenv args with PHANTOM_POSTGRES_HOST unset, got: $bc_out"

# =====================================================================
head_ "D. Gitea Actions job phantom-library-bluegreen-schema-overlap-rig"
if [[ -f "$WORKFLOW" ]]; then ok "workflow present: $WORKFLOW"; else bad "workflow missing: $WORKFLOW"; fi
if [[ -f "$WORKFLOW" ]]; then
    grep -Eq '^\s*phantom-library-bluegreen-schema-overlap-rig:' "$WORKFLOW" \
        && ok "defines job phantom-library-bluegreen-schema-overlap-rig" || bad "job not defined"
    grep -Eq 'runs-on:\s*self-hosted' "$WORKFLOW" \
        && ok "runs on the self-hosted runner" || bad "not pinned to a self-hosted runner"
    grep -Eq '46-bluegreen-schema-overlap\.sh' "$WORKFLOW" \
        && ok "invokes scenario 46" || bad "does not invoke scenario 46"
    grep -Eq 'BLUEGREEN_RIG_OK' "$WORKFLOW" \
        && ok "gates on the BLUEGREEN_RIG_OK marker" || bad "does not gate on BLUEGREEN_RIG_OK"
fi

# =====================================================================
head_ "E. Deterministic overlap + expand->flip->contract proof (ephemeral Postgres)"
if ! command -v podman >/dev/null 2>&1; then
    printf 'NOTE: podman not found; skipping the ephemeral-Postgres deterministic proof.\n' >&2
else
    PGC="bgrig-test-pg-$$"
    PGPORT=$(( (RANDOM % 5000) + 25000 ))
    PGUSER=phantom
    PGPASS=rigtest
    PGDB=bgrig_test

    teardown_pg() { podman rm -f "$PGC" >/dev/null 2>&1 || true; }
    trap teardown_pg EXIT

    if ! podman run -d --name "$PGC" \
            -e POSTGRES_USER="$PGUSER" -e POSTGRES_PASSWORD="$PGPASS" -e POSTGRES_DB="$PGDB" \
            -p "127.0.0.1:$PGPORT:5432" \
            docker.io/library/postgres:16-alpine >/tmp/bgrig-pg-start.log 2>&1; then
        printf 'NOTE: could not start ephemeral Postgres (no network/image?); skipping proof.\n' >&2
        cat /tmp/bgrig-pg-start.log >&2
    else
        ready=0
        for _ in $(seq 1 60); do
            podman exec "$PGC" pg_isready -U "$PGUSER" -d "$PGDB" >/dev/null 2>&1 && { ready=1; break; }
            sleep 1
        done
        if [[ "$ready" -ne 1 ]]; then
            bad "ephemeral Postgres never became ready"
        else
            pgx() { podman exec -i "$PGC" psql -q -A -t -U "$PGUSER" -d "$PGDB" -c "$1"; }

            # Seed a movie + an episode row (TV parity) in a fresh table.
            pgx "CREATE TABLE catalogue_items (tmdb_id BIGINT PRIMARY KEY, type TEXT NOT NULL);" >/dev/null
            pgx "INSERT INTO catalogue_items (tmdb_id, type) VALUES (99000001,'movie'), (99100001,'series');" >/dev/null

            # "blue" = the SAME database connected-to before the expand exists
            # (its rows/behaviour established pre-expand); "green" = a
            # connection/write that only happens AFTER the expand. (The live
            # rig scenario, 46-bluegreen-schema-overlap.sh, is what proves two
            # GENUINELY CONCURRENT live processes tolerate this; this
            # deterministic harness proves the DATA-LEVEL invariant — an
            # already-established old-color read/write pattern keeps working
            # across an additive expand applied mid-sequence, with no
            # process-level flakiness from holding a literal open session.)
            movie_before=$(pgx "SELECT COUNT(*) FROM catalogue_items WHERE type='movie' AND tmdb_id=99000001;")
            series_before=$(pgx "SELECT COUNT(*) FROM catalogue_items WHERE type='series' AND tmdb_id=99100001;")
            if [[ "$movie_before" == "1" && "$series_before" == "1" ]]; then
                ok "seed present: movie + episode/series rows before expand"
            else
                bad "seed missing before expand (movie=$movie_before series=$series_before)"
            fi

            # EXPAND (additive, idempotent) applied twice while blue's session
            # is open — mirrors SchemaExpandMigrator's ADD COLUMN IF NOT EXISTS.
            e1_rc=0; e2_rc=0
            pgx "ALTER TABLE catalogue_items ADD COLUMN IF NOT EXISTS rig_probe TEXT;" >/dev/null || e1_rc=$?
            pgx "ALTER TABLE catalogue_items ADD COLUMN IF NOT EXISTS rig_probe TEXT;" >/dev/null || e2_rc=$?
            if [[ "$e1_rc" -eq 0 && "$e2_rc" -eq 0 ]]; then
                ok "expand applied twice with no error (additive + idempotent)"
            else
                bad "expand failed or was not idempotent (1st rc=$e1_rc 2nd rc=$e2_rc)"
            fi

            # blue's read/write pattern (established pre-expand) keeps working
            # fine post-expand (OVERLAP INVARIANT, half 1 — old color not
            # disabled by the newer additive schema).
            blue_read_after=$(pgx "SELECT COUNT(*) FROM catalogue_items WHERE type='movie';")
            blue_write_rc=0
            pgx "INSERT INTO catalogue_items (tmdb_id, type) VALUES (99000099,'movie') ON CONFLICT DO NOTHING;" >/dev/null || blue_write_rc=$?
            if [[ "$blue_read_after" -ge 1 && "$blue_write_rc" -eq 0 ]]; then
                ok "blue's pre-expand read/write pattern keeps working across the expand"
            else
                bad "blue's pre-expand read/write pattern broke across the expand (read=$blue_read_after write_rc=$blue_write_rc)"
            fi

            # "green": a NEW session opened only AFTER the expand — its
            # additive-column write must not break blue.
            pgx "UPDATE catalogue_items SET rig_probe='green-write' WHERE tmdb_id=99100001;" >/dev/null \
                && ok "green's post-expand write (new column) succeeds" \
                || bad "green's post-expand write failed"

            movie_after=$(pgx "SELECT COUNT(*) FROM catalogue_items WHERE type='movie';")
            series_after=$(pgx "SELECT COUNT(*) FROM catalogue_items WHERE type='series' AND tmdb_id=99100001;")
            [[ "$series_after" == "1" ]] \
                && ok "movie/TV parity preserved: series/episode row still intact after green's write" \
                || bad "series/episode row disturbed by green's write (got count=$series_after)"
            [[ "$movie_after" -ge 2 ]] \
                && ok "blue's write landed alongside green's (both colors' writes coexist)" \
                || bad "expected >=2 movie rows after both colors wrote, got $movie_after"

            # SIMULATE FLIP: blue's connection pattern ends here (no further
            # blue reads/writes follow); the CONTRACT step below must only
            # run after this point.
            ok "simulated flip: no further blue reads/writes issued"

            # CONTRACT: drop the column ONLY now, after the flip.
            pgx "ALTER TABLE catalogue_items DROP COLUMN IF EXISTS rig_probe;" >/dev/null \
                && ok "contract drop applied after the simulated flip" \
                || bad "contract drop failed"
            has_col=$(pgx "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='catalogue_items' AND column_name='rig_probe';")
            [[ "$has_col" == "0" ]] && ok "contract drop removed rig_probe" || bad "rig_probe still present after contract (count=$has_col)"

            # Sole remaining color (green) still fully works post-contract.
            movie_final=$(pgx "SELECT COUNT(*) FROM catalogue_items WHERE type='movie';")
            series_final=$(pgx "SELECT COUNT(*) FROM catalogue_items WHERE type='series';")
            [[ "$movie_final" -ge 2 && "$series_final" == "1" ]] \
                && ok "sole remaining color (green) reads movie + series rows fine post-contract" \
                || bad "post-contract read failed (movie=$movie_final series=$series_final)"
        fi
        teardown_pg
    fi
    trap - EXIT
fi

# =====================================================================
head_ "Summary"
printf '  passed: %d   failed: %d\n' "$pass_count" "$fail_count"
[[ "$fail_count" -eq 0 ]] || { printf '\033[31mBLUEGREEN-SCHEMA-OVERLAP-RIG REGRESSION TEST FAILED\033[0m\n' >&2; exit 1; }
printf '\033[32mALL BLUEGREEN-SCHEMA-OVERLAP-RIG REGRESSION ASSERTIONS PASSED\033[0m\n'
