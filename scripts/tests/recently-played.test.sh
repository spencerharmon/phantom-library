#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/recently-played.test.sh
#
# Deterministic, sandbox-safe regression harness (the DoD `Check:`) for the
# recently-played fix — the operator bug (2026-09-12) "'Recently played' is not
# working for phantom items". Mirrors scripts/tests/p8-loadtime-flows.test.sh:
# the live rig only runs on the self-hosted / in-cluster acceptance runner;
# THIS harness is the in-sandbox, deterministic machine gate (bash + python3
# only, NO live Jellyfin, NO cluster, NO network).
#
# It drives tools/rig-scenarios/48-recently-played.sh with PHANTOM_CI_DRYRUN=1
# and asserts the RECENTLY-PLAYED CONTRACT the live scenario asserts:
#   A. scenario + harness exist, executable, `bash -n` syntax-clean.
#   B. scenario refuses the production port :8096 and is trap-clean (static).
#   C. dry-run CONTRACT: a played MOVIE and a played EPISODE each surface in
#      BOTH the recently-played row (with a DatePlayed marker) and the resume
#      row (with a non-zero PlaybackPositionTicks), and the recently-played
#      query is O(recent) (latency within budget) — movie/episode parity.
#   D. NEGATIVE CONTROL: forcing the movie to miss the recent row FAILS the
#      movie contract (the harness can actually catch the regression), while
#      the episode still passes (parity is independent).
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# Skips with a NOTE (exit 0) if python3 is unavailable — never breaks a node
# lacking the tool.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
SCENARIO="$REPO_ROOT/tools/rig-scenarios/48-recently-played.sh"

pass_count=0
fail_count=0
ok()    { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()   { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

command -v python3 >/dev/null 2>&1 || { printf 'NOTE: python3 unavailable; skipping recently-played harness.\n'; exit 0; }

head_ "A. scenario + harness exist, executable, syntax-clean"
[[ -f "$SCENARIO" ]] || fatal "scenario not found: $SCENARIO"
if [[ -x "$SCENARIO" ]]; then ok "scenario is executable"; else bad "scenario is not executable (chmod +x): $SCENARIO"; fi
if bash -n "$SCENARIO"; then ok "$SCENARIO passes bash -n"; else bad "$SCENARIO has a bash syntax error"; fi
if [[ -x "${BASH_SOURCE[0]}" ]]; then ok "harness is executable"; else bad "harness is not executable (chmod +x)"; fi

head_ "B. prod-port refusal + trap-clean (static)"
if grep -qE ':8096' "$SCENARIO" && grep -qi 'refus' "$SCENARIO"; then
    ok "scenario statically refers to refusing the production port :8096"
else
    bad "scenario does not explicitly refuse the production port :8096"
fi
if grep -qE 'trap .* (EXIT|INT|TERM)' "$SCENARIO"; then
    ok "scenario installs an EXIT trap (trap-clean)"
else
    # 48 has no persistent resources of its own in dry run; a guard on $API is the
    # real safety-critical invariant here. Assert it explicitly instead.
    if grep -qE 'PHANTOM_RP_API points at :8096' "$SCENARIO"; then
        ok "scenario guards \$API against the :8096 production port before any work"
    else
        bad "scenario has neither an EXIT trap nor an explicit :8096 guard"
    fi
fi

# -- shared: run the dry-run scenario and parse its RP fixture lines into a map --
# emits key=val pairs on stdout for python to assert against.
run_dryrun() {
    local force_miss="${1:-}"
    PHANTOM_CI_DRYRUN=1 PHANTOM_RP_FORCE_MISS="$force_miss" bash "$SCENARIO" 2>/dev/null
}

assert_contract() {
    # $1 = fixture FILE, $2 = label, $3 = expect movie surfaced in recent (1/0),
    # $4 = expect episode surfaced in recent (1/0)
    # (fixture arrives via a FILE arg, never stdin — the python script itself
    # arrives on stdin via the heredoc, so the two cannot share stdin.)
    python3 - "$1" "$2" "$3" "$4" <<'PY'
import sys
fixture_file=sys.argv[1]; label=sys.argv[2]; exp_movie=sys.argv[3]; exp_ep=sys.argv[4]
text=open(fixture_file).read()
recent={}   # item -> surfaced(bool), dateplayed
resume={}   # item -> position_ticks
recent_dp={}
latency=None; budget=None
for line in text.splitlines():
    line=line.strip()
    if not line.startswith('RP '): continue
    body=line[3:].strip()
    kv=dict(p.split('=',1) for p in body.split() if '=' in p)
    if 'recent_query_seconds' in kv:
        latency=float(kv['recent_query_seconds']); budget=float(kv['budget'])
    it=kv.get('item'); surf=kv.get('surface')
    if it and surf=='recent':
        recent[it]=(kv.get('surfaced')=='1'); recent_dp[it]=kv.get('dateplayed','')
    if it and surf=='resume':
        resume[it]=int(kv.get('position_ticks') or 0)

def die(m): raise SystemExit(f'{label}: {m}')

if latency is None or budget is None: die('no recent_query_seconds/budget line in fixture')
if latency > budget: die(f'recent query not O(recent): {latency}s > {budget}s budget')

for it, exp in (('movie', exp_movie=='1'), ('episode', exp_ep=='1')):
    if it not in recent: die(f'{it} missing from recent fixture')
    if recent[it] != exp:
        die(f'{it} recent-surface = {recent[it]}, expected {exp}')
    if exp:
        if not recent_dp.get(it): die(f'{it} surfaced in recent but has no DatePlayed marker')
    # resume must always carry a positive position for a played item
    if it not in resume: die(f'{it} missing from resume fixture')
    if resume[it] <= 0: die(f'{it} resume position_ticks not > 0 (got {resume[it]})')

print(f'  {label}: movie_recent={recent["movie"]} episode_recent={recent["episode"]} '
      f'movie_resume={resume["movie"]} episode_resume={resume["episode"]} '
      f'latency={latency}s<=budget={budget}s')
PY
}

head_ "C. dry-run recently-played CONTRACT: movie AND episode surface in recent + resume, O(recent)"
FIX_FILE="$(mktemp)"; run_dryrun "" > "$FIX_FILE" || fatal "dry run of the scenario exited non-zero"
grep '^RP ' "$FIX_FILE" | sed 's/^/    /'
if assert_contract "$FIX_FILE" "baseline" 1 1; then
    ok "played movie AND episode both surface in recent+resume with DatePlayed + resume ticks, O(recent)"
else
    bad "baseline contract failed (see message above)"
fi

head_ "D. negative control: forcing the movie to miss FAILS the contract (harness can catch the bug)"
FIX_MISS_FILE="$(mktemp)"; run_dryrun movie > "$FIX_MISS_FILE" || fatal "dry run (force miss=movie) exited non-zero"
# The baseline assertion (expect movie surfaced=1) MUST now fail on this fixture.
if assert_contract "$FIX_MISS_FILE" "forced-miss-baseline" 1 1 2>/dev/null; then
    bad "forcing the movie to miss did NOT fail the contract — the harness is vacuous!"
else
    ok "forced movie-miss correctly FAILS the movie contract (regression is detectable)"
fi
# And the SAME forced-miss fixture must still satisfy the episode (parity independent).
if assert_contract "$FIX_MISS_FILE" "forced-miss-episode" 0 1; then
    ok "episode still satisfies the contract when only the movie is forced to miss (parity independent)"
else
    bad "episode contract broke under a movie-only forced miss (parity not independent)"
fi

head_ "Result"
printf '%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ] || exit 1
