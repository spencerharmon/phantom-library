#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/p10-curated-browse.test.sh
#
# In-repo regression harness for the ROI Priority 10 capstone acceptance rig
# (tools/rig-scenarios/49-curated-browse.sh, p10-curated-browse-acceptance-
# rig). The live rig itself (a real Jellyfin + plugin on :18096, driven end
# to end for movie AND TV) can only run on a host with the seeded rig
# (gitea-live-rig-job / in-cluster-acceptance-rig), exactly like every other
# tools/rig-scenarios/*.sh entry — see that script's own header note and
# docs/agents/testing.md. This harness is the in-sandbox, no-cluster gate:
#
#   A. Scenario 49 exists, is executable, and is `bash -n` syntax-clean;
#      every embedded python3 heredoc/`-c` snippet is `py_compile`-clean.
#   B. It is trap-clean, refuses the production port :8096, brings the rig
#      up via rig-up.sh --reset, and drives BOTH channels (Phantom Movies +
#      Phantom Shows) — movie/TV parity is structurally present, never just
#      claimed.
#   C. It actually exercises all four proof points the task requires:
#      pruning + search-sync reachability, default order, all four explicit
#      sort options (PremiereDate/DateCreated/CommunityRating/Name), the
#      curated-row folder presentation, and a live list_load/sort_change
#      latency check against the P8 ratchet (tools/perf/loadtime-guard.sh).
#   D. The underlying unit-level coverage for the same three behaviors the
#      rig asserts live — pruning (PhantomDbTests' ListVisibleMovieRows/
#      ListVisibleSeriesRows exclusion-on-all-invalid-candidates cases),
#      default ordering (PhantomDbTests' materialised/relevance/recency order
#      case), and curated-row derivation/pruning-inheritance (CuratedRowsTests)
#      — EXISTS and was not quietly deleted. (`dotnet test` itself is NOT
#      re-run here — that requires the built patched Jellyfin assemblies and
#      is the C# build/test gate's own job, mirroring
#      scripts/tests/recently-played.test.sh's convention; this harness only
#      guards that the coverage is present.)
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
SCENARIO="$REPO_ROOT/tools/rig-scenarios/49-curated-browse.sh"
RIG_UP="$REPO_ROOT/tools/rig-scenarios/rig-up.sh"
GUARD="$REPO_ROOT/tools/perf/loadtime-guard.sh"
DB_TESTS="$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/PhantomDbTests.cs"
CURATED_ROWS_TESTS="$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/CuratedRowsTests.cs"

pass_count=0
fail_count=0
ok()    { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()   { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

[[ -f "$SCENARIO" ]] || fatal "scenario not found: $SCENARIO"
[[ -f "$RIG_UP" ]]   || fatal "rig-up.sh not found: $RIG_UP"
[[ -f "$GUARD" ]]    || fatal "tools/perf/loadtime-guard.sh not found: $GUARD"

# =====================================================================
head_ "A. Scenario 49 exists, executable, syntax-clean"
if [[ -x "$SCENARIO" ]]; then ok "scenario is executable"; else bad "scenario not executable (chmod +x): $SCENARIO"; fi
if bash -n "$SCENARIO" 2>/tmp/p10rig-syntax.err; then
    ok "scenario is bash -n clean"
else
    bad "scenario has a syntax error: $(cat /tmp/p10rig-syntax.err)"
fi
rm -f /tmp/p10rig-syntax.err

if command -v python3 >/dev/null 2>&1; then
    # Extract every `python3 -c "..."` / heredoc body and py_compile each, so
    # a broken embedded snippet fails fast in-sandbox rather than mid-live-run.
    workdir="$(mktemp -d "${TMPDIR:-/tmp}/p10rig-py.XXXXXX")"
    snippet_count="$(python3 - "$SCENARIO" "$workdir" <<'PY'
import re, sys
src = open(sys.argv[1]).read()
workdir = sys.argv[2]
# python3 -c "..." blocks (double-quoted, bash-embedded, possibly
# multi-line). The body may contain backslash-escaped quotes (\") from
# f-string literals inside the outer bash double-quoted string, so the
# closing quote must be matched with escape-awareness, not a naive
# non-greedy `.*?`.
pattern = re.compile(r'python3 -c "((?:[^"\\]|\\.)*)"', re.DOTALL)
n = 0
for m in pattern.finditer(src):
    body = m.group(1)
    # bash double-quote escaping inside the -c string: \" -> ", \$ -> $
    body = body.replace('\\"', '"').replace('\\$', '$')
    n += 1
    with open(f"{workdir}/snippet-{n}.py", "w") as f:
        f.write(body)
print(n)
PY
)"
    [[ "$snippet_count" -gt 0 ]] || fatal "found 0 embedded python3 -c snippets in the scenario — extraction regex may have rotted"
    heredoc_fail=0
    shopt -s nullglob
    for f in "$workdir"/snippet-*.py; do
        python3 -m py_compile "$f" 2>/tmp/p10rig-pyc.err || { heredoc_fail=1; cat /tmp/p10rig-pyc.err >&2; }
    done
    shopt -u nullglob
    if [[ "$heredoc_fail" -eq 0 ]]; then
        ok "every embedded python3 snippet in the scenario is py_compile-clean"
    else
        bad "an embedded python3 snippet in the scenario failed to compile"
    fi
    rm -rf "$workdir" /tmp/p10rig-pyc.err
else
    printf '  NOTE: python3 unavailable; skipping embedded-snippet compile check.\n'
fi

# =====================================================================
head_ "B. rig bring-up, trap-clean, prod-safety, movie/TV parity"
if grep -qE 'rig-up\.sh.*--reset' "$SCENARIO"; then
    ok "scenario brings up the rig via rig-up.sh --reset"
else
    bad "scenario does not bring up the rig via rig-up.sh --reset"
fi
if grep -qE 'trap cleanup EXIT INT TERM' "$SCENARIO" && grep -q 'rig-down.sh' "$SCENARIO"; then
    ok "scenario installs an EXIT/INT/TERM trap that tears the rig down"
else
    bad "scenario is missing a trap-clean rig teardown"
fi
if grep -qE ':8096' "$SCENARIO" && grep -qi 'refus' "$SCENARIO"; then
    ok "scenario refuses the production port :8096"
else
    bad "scenario does not explicitly refuse the production port :8096"
fi
if grep -q "Phantom Movies" "$SCENARIO" && grep -q "Phantom Shows" "$SCENARIO"; then
    ok "scenario resolves both the Phantom Movies AND Phantom Shows channels"
else
    bad "scenario does not resolve both channels (movie/TV parity)"
fi

# =====================================================================
head_ "C. the four required proof points are structurally present"
if grep -q "source_candidates" "$SCENARIO" && grep -q "'invalid'" "$SCENARIO" && grep -q "__search_sync_movies__" "$SCENARIO" && grep -q "__search_sync_shows__" "$SCENARIO"; then
    ok "scenario seeds an all-invalid-candidate item and checks both search-sync folders (pruning + still-searchable, movie+TV)"
else
    bad "scenario does not exercise the pruning + search-sync-reachability proof for both movie and TV"
fi
if grep -q 'SortBy=PremiereDate' "$SCENARIO" \
    && grep -q 'SortBy=DateCreated' "$SCENARIO" \
    && grep -q 'SortBy=CommunityRating' "$SCENARIO" \
    && grep -q 'SortBy=Name' "$SCENARIO"; then
    ok "scenario exercises all four explicit sort options"
else
    bad "scenario does not exercise all four explicit sort options (PremiereDate/DateCreated/CommunityRating/Name)"
fi
if grep -q 'relevance_score' "$SCENARIO" || grep -q 'default order' "$SCENARIO"; then
    ok "scenario exercises the default relevance-blended order"
else
    bad "scenario does not exercise the default order"
fi
if grep -q '__row_movies_genre_' "$SCENARIO" && grep -q '__row_shows_genre_' "$SCENARIO"; then
    ok "scenario exercises curated-row (Netflix-style rows) folder derivation for both channels"
else
    bad "scenario does not exercise curated-row folder derivation for both channels"
fi
if grep -q 'tools/perf/loadtime-guard.sh' "$SCENARIO" && grep -q "list_load" "$SCENARIO" && grep -q "sort_change" "$SCENARIO"; then
    ok "scenario checks list_load/sort_change latency against the P8 ratchet guard"
else
    bad "scenario does not check list_load/sort_change latency against the P8 ratchet guard"
fi

# =====================================================================
head_ "D. underlying unit-level coverage exists (pruning + order + rows)"
[[ -f "$DB_TESTS" ]] || fatal "PhantomDbTests.cs not found: $DB_TESTS"
[[ -f "$CURATED_ROWS_TESTS" ]] || fatal "CuratedRowsTests.cs not found: $CURATED_ROWS_TESTS"
if grep -q 'ListVisibleMovieRows_ExcludesAvailableMovieWhenAllCandidatesInvalid' "$DB_TESTS" \
    && grep -q 'ListVisibleSeriesRows_ExcludesSeriesWhenOnlyEpisodeCandidateInvalid' "$DB_TESTS"; then
    ok "pruning coverage (movie + series, all-invalid-candidates exclusion) present in PhantomDbTests.cs"
else
    bad "pruning coverage missing/renamed in PhantomDbTests.cs"
fi
if grep -q 'ListVisibleMovieRows_DefaultOrder_MaterialisedFirstThenRelevanceScore' "$DB_TESTS"; then
    ok "default-order coverage present in PhantomDbTests.cs"
else
    bad "default-order coverage missing/renamed in PhantomDbTests.cs"
fi
if grep -qi 'class CuratedRowsTests' "$CURATED_ROWS_TESTS" && grep -qi 'Build' "$CURATED_ROWS_TESTS"; then
    ok "curated-row derivation coverage present in CuratedRowsTests.cs"
else
    bad "curated-row derivation coverage missing/renamed in CuratedRowsTests.cs"
fi

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
