#!/bin/bash
# Scenario 40: favourite -> TMDB similar/recommendations catalogue ingest
# (REQ-M14-RECOMMENDATIONS).
#
# Verifies the event-driven recommendation ingestor end-to-end against the
# live plugin, for BOTH a movie seed and a series seed (AGENTS.md "Movie/TV
# parity"):
#   - POST /Plugins/PhantomLibrary/Recommendations/Ingest?tmdbId=&type=
#     fans the seed out to TMDB similar + recommendations (tmdb-mock),
#     de-duplicates across the two lists, drops the seed itself, and folds
#     the result into the append-only catalogue under the
#     favourite-recommendation source bit (source_mask & 4).
#   - movie rec rows also seed availability_items (status='unknown').
#   - series rec rows also seed series_expansion_state.
#   - a second identical ingest is idempotent (no new catalogue rows).
#   - the seed id itself is never written as a favourite-recommendation row.
#   - invalid input is rejected with HTTP 400 without touching the catalogue.
#
# The tmdb-mock fan-out fixtures (tools/rig-scenarios/tmdb-mock.py):
#   movie seed 99000001 -> similar {99000101,99000102} + rec {99000102,99000103}
#                          => distinct {99000101,99000102,99000103}
#   series seed 99100001 -> similar {99100101,99100102} + rec {99100103}
#                          => distinct {99100101,99100102,99100103}
#
# *** OPERATOR-RUN-ONLY ***
#
# Like scenario 30, this requires the patched Jellyfin server (channel
# arch) built from scripts/jellyfin-patches/ AND a plugin DLL built from
# this branch (it exercises the Recommendations/Ingest endpoint, which
# only exists once this change ships). Bring the rig up with
# tools/rig-scenarios/rig-up.sh --reset first. The equivalent invariants
# are also covered by the in-memory unit tests:
#   FavouriteRecommendationIngestorTests (dedupe/drop-seed/cap/availability/
#     series-expansion/disabled/validation)
#   PhantomLibrarySourceControllerTests.IngestRecommendations_* (endpoint
#     validation + movie/series pass-through)
#   UserDataSavedListenerTests (favourite movie/series/episode -> ingest)

set -u
exec > /tmp/jf-rig/logs/scenario-favourite-recommendations.log 2>&1

BASE=${BASE:-http://localhost:18096}
TOK=${TOK:-testtoken00000000000000000000000}
PHDB=${PHDB:-/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db}

MOVIE_SEED=99000001
MOVIE_RECS="99000101 99000102 99000103"
SERIES_SEED=99100001
SERIES_RECS="99100101 99100102 99100103"

echo "=== Scenario: favourite-recommendations (REQ-M14-RECOMMENDATIONS) ==="
date
echo

fail() { echo "FAIL: $*"; exit 1; }

# JSON body of the last curl POST is captured to $BODY; lower-cases keys so
# the assertion is insensitive to camelCase/PascalCase serialization.
json_field() {
  # $1 = json string, $2 = lower-cased field name
  printf '%s' "$1" | python3 -c "
import json,sys
try:
    d=json.load(sys.stdin)
except Exception:
    print('<parse-error>'); sys.exit(0)
d={ (k.lower() if isinstance(k,str) else k):v for k,v in d.items() } if isinstance(d,dict) else {}
print(d.get('$2','<missing>'))
"
}

count() { sqlite3 "$PHDB" "$1"; }

in_list() {
  # build a SQL IN(...) list from space-separated ids
  local ids="$1" out=""
  for id in $ids; do out="$out,$id"; done
  echo "${out#,}"
}

# --- preflight --------------------------------------------------------------
if [ ! -f "$PHDB" ]; then
  fail "phantom.db missing at $PHDB — bring up the rig with tools/rig-scenarios/rig-up.sh --reset first."
fi

ver=$(count 'PRAGMA user_version;')
if [ "$ver" != "11" ]; then
  fail "phantom.db at user_version=$ver; expected 11. Pre-v1.0 ships no migrations — wipe and rebuild (scripts/phantom-wipe.sh)."
fi

# Endpoint must exist (this build) — a 404 here means the plugin DLL predates
# the Recommendations/Ingest endpoint.
probe=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "X-Emby-Token: $TOK" \
  "$BASE/Plugins/PhantomLibrary/Recommendations/Ingest?tmdbId=0&type=movie")
if [ "$probe" = "404" ]; then
  fail "Recommendations/Ingest returned 404 — plugin DLL predates this change. Rebuild + redeploy."
fi
# tmdbId=0 is invalid input -> must be 400, and must not write anything.
if [ "$probe" != "400" ]; then
  fail "invalid tmdbId=0 expected HTTP 400, got $probe"
fi
echo "preflight ok: schema v11, endpoint present, invalid-input rejected (400)"

MOVIE_IN=$(in_list "$MOVIE_RECS")
SERIES_IN=$(in_list "$SERIES_RECS")

# --- baseline ---------------------------------------------------------------
favbit_before=$(count "SELECT COUNT(*) FROM catalogue_items WHERE (source_mask & 4)=4;")
echo "baseline: favourite-recommendation catalogue rows=$favbit_before"

# --- 1. movie seed ----------------------------------------------------------
echo
echo "=== ingest movie seed $MOVIE_SEED ==="
BODY=$(curl -s -X POST -H "X-Emby-Token: $TOK" \
  "$BASE/Plugins/PhantomLibrary/Recommendations/Ingest?tmdbId=$MOVIE_SEED&type=movie")
echo "  response: $BODY"
m_enabled=$(json_field "$BODY" enabled)
m_type=$(json_field "$BODY" type)
m_inserted=$(json_field "$BODY" inserted)
echo "  enabled=$m_enabled type=$m_type inserted=$m_inserted"
[ "$m_type" = "movie" ] || fail "movie ingest: response type='$m_type', expected 'movie'"
[ "$m_enabled" = "True" ] || [ "$m_enabled" = "true" ] || fail "movie ingest: feature reported disabled (enabled=$m_enabled)"

# catalogue rows for the 3 distinct rec ids, all carrying the favourite bit.
mcat=$(count "SELECT COUNT(*) FROM catalogue_items WHERE type='movie' AND (source_mask & 4)=4 AND tmdb_id IN ($MOVIE_IN);")
[ "$mcat" = "3" ] || fail "expected 3 movie rec catalogue rows (source bit set), got $mcat"

# each movie rec row seeded availability_items with status='unknown'.
mav=$(count "SELECT COUNT(*) FROM availability_items WHERE type='movie' AND season=-1 AND episode=-1 AND status='unknown' AND tmdb_id IN ($MOVIE_IN);")
[ "$mav" = "3" ] || fail "expected 3 movie rec availability rows, got $mav"

# the seed itself was dropped from the fan-out (never carries the favourite bit).
mseed=$(count "SELECT COUNT(*) FROM catalogue_items WHERE tmdb_id=$MOVIE_SEED AND (source_mask & 4)=4;")
[ "$mseed" = "0" ] || fail "movie seed $MOVIE_SEED was written as a favourite-rec row (expected drop)"
echo "  movie: catalogue=3 availability=3 seed-dropped ok"

# --- 2. idempotency (movie) -------------------------------------------------
echo
echo "=== re-ingest movie seed $MOVIE_SEED (idempotent) ==="
BODY2=$(curl -s -X POST -H "X-Emby-Token: $TOK" \
  "$BASE/Plugins/PhantomLibrary/Recommendations/Ingest?tmdbId=$MOVIE_SEED&type=movie")
m2_inserted=$(json_field "$BODY2" inserted)
echo "  second-call inserted=$m2_inserted"
[ "$m2_inserted" = "0" ] || fail "second identical movie ingest inserted=$m2_inserted, expected 0 (append-only dedupe)"
mcat2=$(count "SELECT COUNT(*) FROM catalogue_items WHERE type='movie' AND (source_mask & 4)=4 AND tmdb_id IN ($MOVIE_IN);")
[ "$mcat2" = "3" ] || fail "movie rec catalogue rows changed after idempotent re-ingest: $mcat2 (expected 3)"
echo "  idempotent ok"

# --- 3. series seed (Movie/TV parity) ---------------------------------------
echo
echo "=== ingest series seed $SERIES_SEED ==="
BODY3=$(curl -s -X POST -H "X-Emby-Token: $TOK" \
  "$BASE/Plugins/PhantomLibrary/Recommendations/Ingest?tmdbId=$SERIES_SEED&type=series")
echo "  response: $BODY3"
s_type=$(json_field "$BODY3" type)
[ "$s_type" = "series" ] || fail "series ingest: response type='$s_type', expected 'series'"

scat=$(count "SELECT COUNT(*) FROM catalogue_items WHERE type='series' AND (source_mask & 4)=4 AND tmdb_id IN ($SERIES_IN);")
[ "$scat" = "3" ] || fail "expected 3 series rec catalogue rows (source bit set), got $scat"

# each series rec row seeded series_expansion_state (parity with movie
# availability seeding).
sexp=$(count "SELECT COUNT(*) FROM series_expansion_state WHERE series_tmdb_id IN ($SERIES_IN);")
[ "$sexp" = "3" ] || fail "expected 3 series_expansion_state rows for rec series, got $sexp"

# series rec ids must NOT create movie availability rows.
sbad=$(count "SELECT COUNT(*) FROM availability_items WHERE tmdb_id IN ($SERIES_IN);")
[ "$sbad" = "0" ] || fail "series rec ids created $sbad availability rows (expected 0; series expand, not probe)"

sseed=$(count "SELECT COUNT(*) FROM catalogue_items WHERE tmdb_id=$SERIES_SEED AND (source_mask & 4)=4;")
[ "$sseed" = "0" ] || fail "series seed $SERIES_SEED was written as a favourite-rec row (expected drop)"
echo "  series: catalogue=3 series_expansion=3 seed-dropped ok"

# --- 4. invalid type rejected -----------------------------------------------
echo
echo "=== reject invalid type ==="
bad=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "X-Emby-Token: $TOK" \
  "$BASE/Plugins/PhantomLibrary/Recommendations/Ingest?tmdbId=$MOVIE_SEED&type=person")
[ "$bad" = "400" ] || fail "invalid type=person expected HTTP 400, got $bad"
echo "  invalid type rejected (400) ok"

# --- summary ----------------------------------------------------------------
favbit_after=$(count "SELECT COUNT(*) FROM catalogue_items WHERE (source_mask & 4)=4;")
echo
echo "favourite-recommendation catalogue rows: before=$favbit_before after=$favbit_after (expected +6)"
[ "$((favbit_after - favbit_before))" = "6" ] || fail "net favourite-rec catalogue growth $((favbit_after-favbit_before)), expected 6"

echo
echo "=== PASS: REQ-M14-RECOMMENDATIONS favourite -> catalogue ingest (movie + series) ==="
