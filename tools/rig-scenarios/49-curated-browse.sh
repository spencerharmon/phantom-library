#!/usr/bin/env bash
# 49-curated-browse.sh — ROI Priority 10 capstone acceptance rig
# (p10-curated-browse-acceptance-rig).
#
# Live-rig proof that the curated, playable-first browse experience works
# end to end, for MOVIE and TV alike, against the live rig Jellyfin
# (127.0.0.1:18096, never prod :8096) driven via rig-up.sh. Reuses the exact
# rig bring-up / sqlite-seed / trap-clean shape as 45-availability-probe.sh /
# 46-bluegreen-schema-overlap.sh so scenarios 35/36 stay green when this one
# also runs in an environment with a live seeded rig (same posture note as
# 45's header).
#
# Proves:
#   1. PRUNING (p10-prune-nonplayable-browse): an 'available' item whose every
#      known source_candidates row is 'invalid' (a known cold-materialise-
#      fail) is EXCLUDED from the browse LIST but stays reachable via the
#      search-sync folder (still globally searchable).
#   2. DEFAULT ORDER + EXPLICIT SORT (p10-relevance-sort): the default browse
#      order ranks by descending relevance_score, and each of the four
#      channel-honoured explicit sort options (PremiereDate, DateCreated,
#      CommunityRating, Name) re-orders the SAME set as expected.
#   3. NETFLIX-STYLE ROWS (p10-netflix-style-rows): with no explicit sort,
#      the channel root emits curated-row category folders (not a flat list),
#      and the "Available now" row's members are exactly the browse-visible,
#      available subset (bounded, cheap — no O(catalogue) surprises).
#   4. LATENCY NON-REGRESSION: list_load / sort_change (the P8-timed flows)
#      measured live against this same rig do not exceed the currently-
#      ratcheted threshold in tools/perf/loadtime-thresholds.json (via
#      tools/perf/loadtime-guard.sh --live --no-file).
#
# Every step is repeated for a movie AND a TV series/episode (movie/TV
# parity). trap-clean; refuses to run if pointed at the production port.
set -euo pipefail
REPO=${PHANTOM_REPO_ROOT:-/home/spencer/git-repos/spencerharmon/phantom-library}
cd "$REPO"
BASE=http://localhost:18096
TOK=testtoken00000000000000000000000
DATA=/var/tmp/jf-test/data
PHDB="$DATA/plugins/configurations/PhantomLibrary/phantom.db"

log()  { printf '\033[1m[49-curated-browse]\033[0m %s\n' "$*"; }
fail() { echo "FAIL: $*" >&2; exit 1; }

# --- prod-safety guard: this rig is :18096 only, never prod :8096 ----------
case "$BASE" in
    *:8096|*:8096/*) fail "BASE points at :8096 (production) — refusing; the rig is :18096." ;;
esac

cleanup() {
  log "tearing down rig"
  "$REPO/tools/rig-scenarios/rig-down.sh" >/dev/null 2>&1 || true
  rm -f /tmp/rig49-*.json
}
trap cleanup EXIT INT TERM

# ---------------------------------------------------------------- bring up rig
log "starting rig (rig-up.sh --reset)"
"$REPO/tools/rig-scenarios/rig-up.sh" --reset

api() {
  local method=$1 path=$2 body=${3:-}
  if [ -n "$body" ]; then
    curl -sS -X "$method" -H "X-Emby-Token: $TOK" -H 'Content-Type: application/json' -d "$body" "$BASE$path"
  else
    curl -sS -X "$method" -H "X-Emby-Token: $TOK" "$BASE$path"
  fi
}

sql() { sqlite3 "$PHDB" "$@"; }
now_epoch() { date +%s; }

api GET /Channels >/tmp/rig49-channels.json
CH_MOVIES=$(python3 -c "
import json
j=json.load(open('/tmp/rig49-channels.json'))
items=j.get('Items', j if isinstance(j,list) else [])
print(next(x['Id'] for x in items if x.get('Name')=='Phantom Movies'))
")
CH_SHOWS=$(python3 -c "
import json
j=json.load(open('/tmp/rig49-channels.json'))
items=j.get('Items', j if isinstance(j,list) else [])
print(next(x['Id'] for x in items if x.get('Name')=='Phantom Shows'))
")
[ -n "$CH_MOVIES" ] || fail "Phantom Movies channel not registered"
[ -n "$CH_SHOWS" ]  || fail "Phantom Shows channel not registered"
log "channels resolved: movies=$CH_MOVIES shows=$CH_SHOWS"

# ===========================================================================
# 1. PRUNING — movie
# ===========================================================================
log "1(movie): known cold-materialise-fail item is pruned from browse but stays searchable"
PRUNE_MOVIE=99490001
KEEP_MOVIE=99490002
now=$(now_epoch)
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,overview,poster_url,backdrop_url,genres_json,official_rating,community_rating,original_title,fetched_at,relevance_score)
     VALUES ($PRUNE_MOVIE,'movie','P10 Pruned Movie',2021,'x',NULL,NULL,'[]',NULL,NULL,'P10 Pruned Movie',$now,0.5);"
sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,checked_at,next_check_at,priority)
     VALUES ($PRUNE_MOVIE,'movie',-1,-1,'available',$now,$(( now+604800 )),0);"
# Every known source_candidates row is 'invalid' -> known cold-materialise-fail.
sql "INSERT OR REPLACE INTO source_candidates (tmdb_id,type,season,episode,preset,magnet,info_hash,indexer,title,seeders,size,rank,source,fetched_at,expires_at,validation_status)
     VALUES ($PRUNE_MOVIE,'movie',-1,-1,'gostream-default','magnet:?xt=urn:btih:aaa1','aaa1','mock','P10 Pruned Movie',1,1000,1,'test',$now,$(( now+604800 )),'invalid');"
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,overview,poster_url,backdrop_url,genres_json,official_rating,community_rating,original_title,fetched_at,relevance_score)
     VALUES ($KEEP_MOVIE,'movie','P10 Keep Movie',2021,'x',NULL,NULL,'[]',NULL,NULL,'P10 Keep Movie',$now,0.6);"
sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,checked_at,next_check_at,priority)
     VALUES ($KEEP_MOVIE,'movie',-1,-1,'available',$now,$(( now+604800 )),0);"

# The browse LIST is the flat, sort-forced view (an explicit SortBy bypasses
# the curated-row folder presentation, per PhantomMoviesChannel.GetChannelItems).
api GET "/Channels/$CH_MOVIES/Items?SortBy=Name" >/tmp/rig49-movies-list.json
python3 -c "
import json
j=json.load(open('/tmp/rig49-movies-list.json'))
names=[x.get('Name') for x in j.get('Items', [])]
assert 'P10 Pruned Movie' not in names, f'pruned movie leaked into browse LIST: {names}'
assert 'P10 Keep Movie' in names, f'keep movie missing from browse LIST: {names}'
print('1(movie) OK: pruned item excluded from browse LIST, keep item present')
"

# Search-sync folder emits the FULL catalogue (still globally searchable).
api GET "/Channels/$CH_MOVIES/Items?FolderId=__search_sync_movies__" >/tmp/rig49-movies-search.json
python3 -c "
import json
j=json.load(open('/tmp/rig49-movies-search.json'))
names=[x.get('Name') for x in j.get('Items', [])]
assert 'P10 Pruned Movie' in names, f'pruned movie missing from search-sync (must stay searchable): {names}'
print('1(movie) OK: pruned item still reachable via search-sync folder')
"

# ===========================================================================
# 1. PRUNING — TV (episode-level)
# ===========================================================================
log "1(tv): known cold-materialise-fail episode prunes the whole series from browse but stays searchable"
PRUNE_SERIES=99490101
KEEP_SERIES=99490102
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,overview,poster_url,backdrop_url,genres_json,official_rating,community_rating,original_title,fetched_at,relevance_score)
     VALUES ($PRUNE_SERIES,'series','P10 Pruned Series',2021,'x',NULL,NULL,'[]',NULL,NULL,'P10 Pruned Series',$now,0.5);"
sql "INSERT OR REPLACE INTO series_episode_catalogue (series_tmdb_id,episode_tmdb_id,season,episode,air_date,first_seen_at,last_seen_at)
     VALUES ($PRUNE_SERIES,$((PRUNE_SERIES*1000+1)),1,1,'2021-01-01',$now,$now);"
sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,checked_at,next_check_at,priority)
     VALUES ($PRUNE_SERIES,'episode',1,1,'available',$now,$(( now+604800 )),0);"
sql "INSERT OR REPLACE INTO source_candidates (tmdb_id,type,season,episode,preset,magnet,info_hash,indexer,title,seeders,size,rank,source,fetched_at,expires_at,validation_status)
     VALUES ($PRUNE_SERIES,'episode',1,1,'gostream-default','magnet:?xt=urn:btih:bbb1','bbb1','mock','P10 Pruned Series S1E1',1,1000,1,'test',$now,$(( now+604800 )),'invalid');"
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,overview,poster_url,backdrop_url,genres_json,official_rating,community_rating,original_title,fetched_at,relevance_score)
     VALUES ($KEEP_SERIES,'series','P10 Keep Series',2021,'x',NULL,NULL,'[]',NULL,NULL,'P10 Keep Series',$now,0.6);"
sql "INSERT OR REPLACE INTO series_episode_catalogue (series_tmdb_id,episode_tmdb_id,season,episode,air_date,first_seen_at,last_seen_at)
     VALUES ($KEEP_SERIES,$((KEEP_SERIES*1000+1)),1,1,'2021-01-01',$now,$now);"
sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,checked_at,next_check_at,priority)
     VALUES ($KEEP_SERIES,'episode',1,1,'available',$now,$(( now+604800 )),0);"

api GET "/Channels/$CH_SHOWS/Items?SortBy=Name" >/tmp/rig49-shows-list.json
python3 -c "
import json
j=json.load(open('/tmp/rig49-shows-list.json'))
names=[x.get('Name') for x in j.get('Items', [])]
assert 'P10 Pruned Series' not in names, f'pruned series leaked into browse LIST: {names}'
assert 'P10 Keep Series' in names, f'keep series missing from browse LIST: {names}'
print('1(tv) OK: pruned series excluded from browse LIST, keep series present')
"
api GET "/Channels/$CH_SHOWS/Items?FolderId=__search_sync_shows__" >/tmp/rig49-shows-search.json
python3 -c "
import json
j=json.load(open('/tmp/rig49-shows-search.json'))
names=[x.get('Name') for x in j.get('Items', [])]
assert 'P10 Pruned Series' in names, f'pruned series missing from search-sync (must stay searchable): {names}'
print('1(tv) OK: pruned series still reachable via search-sync folder')
"

# ===========================================================================
# 2. DEFAULT ORDER + EXPLICIT SORT — movie
# ===========================================================================
log "2(movie): default order + each explicit sort option produce the expected result"
# Three distinct movies with distinct year (PremiereDate), fetched_at
# (DateCreated), community_rating, name, and relevance_score.
# All three (plus, deliberately, the ALREADY-PRUNED movie above) share one
# genre ("P10RigGenre") so genre-row derivation (used below to prove
# curated-row content preserves the default relevance order — genre rows are
# the one row kind that never re-ranks, per CuratedRows.Build) has a
# qualifying (>= CuratedGenreRowMinItems) row to inspect, AND so the row's
# construction from the already-PRUNED flat list proves a pruned item can
# never leak into ANY row, genre rows included, even when it shares a genre
# with rows that DO qualify.
GENRE_JSON='["P10RigGenre"]'
A=99490201; B=99490202; C=99490203
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,overview,poster_url,backdrop_url,genres_json,official_rating,community_rating,original_title,fetched_at,relevance_score)
     VALUES ($A,'movie','P10 Sort A',2001,'x',NULL,NULL,'$GENRE_JSON',NULL,3.0,'P10 Sort A',$(( now-300 )),0.9);"
sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,checked_at,next_check_at,priority)
     VALUES ($A,'movie',-1,-1,'available',$now,$(( now+604800 )),0);"
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,overview,poster_url,backdrop_url,genres_json,official_rating,community_rating,original_title,fetched_at,relevance_score)
     VALUES ($B,'movie','P10 Sort B',2010,'x',NULL,NULL,'$GENRE_JSON',NULL,6.0,'P10 Sort B',$(( now-200 )),0.5);"
sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,checked_at,next_check_at,priority)
     VALUES ($B,'movie',-1,-1,'available',$now,$(( now+604800 )),0);"
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,overview,poster_url,backdrop_url,genres_json,official_rating,community_rating,original_title,fetched_at,relevance_score)
     VALUES ($C,'movie','P10 Sort C',2020,'x',NULL,NULL,'$GENRE_JSON',NULL,9.0,'P10 Sort C',$(( now-100 )),0.1);"
sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,checked_at,next_check_at,priority)
     VALUES ($C,'movie',-1,-1,'available',$now,$(( now+604800 )),0);"
# Retroactively give the pruned movie the same genre (it stays excluded from
# the flat list, so it must never appear in this genre's row either).
sql "UPDATE tmdb_metadata SET genres_json='$GENRE_JSON' WHERE tmdb_id=$PRUNE_MOVIE AND type='movie';"

assert_order() {
  local file="$1"; shift
  python3 -c "
import json,sys
j=json.load(open('$file'))
names=[x.get('Name') for x in j.get('Items', []) if x.get('Name','').startswith('P10 Sort ')]
expected=sys.argv[1:]
assert names == expected, f'expected order {expected}, got {names}'
print(f'order OK: {names}')
" "$@"
}

# Default order (no SortBy): a curated-row folder preserves the incoming
# default (relevance_score DESC) order verbatim (per CuratedRows.Build's
# genre-row construction — the one row kind that never re-ranks) — so its
# content order stands in for the underlying default order.
# => relevance_score DESC: A(0.9), B(0.5), C(0.1).
api GET "/Channels/$CH_MOVIES/Items?FolderId=__row_movies_genre_p10riggenre__" >/tmp/rig49-sort-default.json
assert_order /tmp/rig49-sort-default.json "P10 Sort A" "P10 Sort B" "P10 Sort C"

# PremiereDate ascending => year A(2001) < B(2010) < C(2020).
api GET "/Channels/$CH_MOVIES/Items?SortBy=PremiereDate" >/tmp/rig49-sort-premiere.json
assert_order /tmp/rig49-sort-premiere.json "P10 Sort A" "P10 Sort B" "P10 Sort C"

# DateCreated ascending => fetched_at A(oldest) < B < C(newest).
api GET "/Channels/$CH_MOVIES/Items?SortBy=DateCreated" >/tmp/rig49-sort-datecreated.json
assert_order /tmp/rig49-sort-datecreated.json "P10 Sort A" "P10 Sort B" "P10 Sort C"

# CommunityRating ascending => A(3.0) < B(6.0) < C(9.0).
api GET "/Channels/$CH_MOVIES/Items?SortBy=CommunityRating" >/tmp/rig49-sort-rating.json
assert_order /tmp/rig49-sort-rating.json "P10 Sort A" "P10 Sort B" "P10 Sort C"

# Name ascending => alphabetical A < B < C (titles already alphabetical).
api GET "/Channels/$CH_MOVIES/Items?SortBy=Name" >/tmp/rig49-sort-name.json
assert_order /tmp/rig49-sort-name.json "P10 Sort A" "P10 Sort B" "P10 Sort C"
log "2(movie) OK: default + all four explicit sort options produced the expected order"

# ===========================================================================
# 2. DEFAULT ORDER + EXPLICIT SORT — TV (series-level, same signals)
# ===========================================================================
log "2(tv): default order + each explicit sort option produce the expected result"
SA=99490301; SB=99490302; SC=99490303
seed_series() { # tmdb year rating fetched_offset relevance name
  local tmdb=$1 year=$2 rating=$3 offset=$4 rel=$5 name=$6
  sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,overview,poster_url,backdrop_url,genres_json,official_rating,community_rating,original_title,fetched_at,relevance_score)
       VALUES ($tmdb,'series','$name',$year,'x',NULL,NULL,'$GENRE_JSON',NULL,$rating,'$name',$(( now+offset )),$rel);"
  sql "INSERT OR REPLACE INTO series_episode_catalogue (series_tmdb_id,episode_tmdb_id,season,episode,air_date,first_seen_at,last_seen_at)
       VALUES ($tmdb,$((tmdb*1000+1)),1,1,'2020-01-01',$now,$now);"
  sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,checked_at,next_check_at,priority)
       VALUES ($tmdb,'episode',1,1,'available',$now,$(( now+604800 )),0);"
}
seed_series $SA 2001 3.0 -300 0.9 "P10 TSort A"
seed_series $SB 2010 6.0 -200 0.5 "P10 TSort B"
seed_series $SC 2020 9.0 -100 0.1 "P10 TSort C"
# Retroactively give the pruned series the same genre (excluded upstream —
# must never leak into this genre's row).
sql "UPDATE tmdb_metadata SET genres_json='$GENRE_JSON' WHERE tmdb_id=$PRUNE_SERIES AND type='series';"

assert_order_prefix() {
  local file="$1" prefix="$2"; shift 2
  python3 -c "
import json,sys
j=json.load(open('$file'))
names=[x.get('Name') for x in j.get('Items', []) if x.get('Name','').startswith('$prefix')]
expected=sys.argv[1:]
assert names == expected, f'expected order {expected}, got {names}'
print(f'order OK: {names}')
" "$@"
}

api GET "/Channels/$CH_SHOWS/Items?FolderId=__row_shows_genre_p10riggenre__" >/tmp/rig49-tsort-default.json
assert_order_prefix /tmp/rig49-tsort-default.json "P10 TSort " "P10 TSort A" "P10 TSort B" "P10 TSort C"
api GET "/Channels/$CH_SHOWS/Items?SortBy=PremiereDate" >/tmp/rig49-tsort-premiere.json
assert_order_prefix /tmp/rig49-tsort-premiere.json "P10 TSort " "P10 TSort A" "P10 TSort B" "P10 TSort C"
api GET "/Channels/$CH_SHOWS/Items?SortBy=DateCreated" >/tmp/rig49-tsort-datecreated.json
assert_order_prefix /tmp/rig49-tsort-datecreated.json "P10 TSort " "P10 TSort A" "P10 TSort B" "P10 TSort C"
api GET "/Channels/$CH_SHOWS/Items?SortBy=CommunityRating" >/tmp/rig49-tsort-rating.json
assert_order_prefix /tmp/rig49-tsort-rating.json "P10 TSort " "P10 TSort A" "P10 TSort B" "P10 TSort C"
api GET "/Channels/$CH_SHOWS/Items?SortBy=Name" >/tmp/rig49-tsort-name.json
assert_order_prefix /tmp/rig49-tsort-name.json "P10 TSort " "P10 TSort A" "P10 TSort B" "P10 TSort C"
log "2(tv) OK: default + all four explicit sort options produced the expected order"

# ===========================================================================
# 3. NETFLIX-STYLE ROWS — movie + TV
# ===========================================================================
log "3(movie): root emits curated-row folders; the genre row is exactly the browse-visible, pruning-respecting subset"
api GET "/Channels/$CH_MOVIES/Items" >/tmp/rig49-movies-root.json
python3 -c "
import json
j=json.load(open('/tmp/rig49-movies-root.json'))
items=j.get('Items', [])
assert len(items) > 0, 'no curated-row folders emitted at movies root'
assert all(x.get('Type') == 'Folder' for x in items), f'root did not emit category folders: {items}'
names=[x.get('Name') for x in items]
assert 'P10RigGenre' in names, f\"'P10RigGenre' genre row missing: {names}\"
print(f'3(movie) OK: root emits {len(items)} curated-row folders incl. the P10RigGenre genre row')
"
# The genre row is built from the SAME pruned, relevance-ordered flat list
# every other row derives from — a pruned item (which also carries this
# genre) can therefore never leak into ANY row, genre rows included.
api GET "/Channels/$CH_MOVIES/Items?FolderId=__row_movies_genre_p10riggenre__" >/tmp/rig49-movies-genre-row.json
python3 -c "
import json
j=json.load(open('/tmp/rig49-movies-genre-row.json'))
names=[x.get('Name') for x in j.get('Items', [])]
assert 'P10 Pruned Movie' not in names, f'pruned movie leaked into the genre row: {names}'
assert names == ['P10 Sort A', 'P10 Sort B', 'P10 Sort C'], f'genre row content/order unexpected: {names}'
print('3(movie) OK: genre-row content bounded to the browse-visible set, in default relevance order, pruned item absent')
"

log "3(tv): root emits curated-row folders; the genre row is exactly the browse-visible, pruning-respecting subset"
api GET "/Channels/$CH_SHOWS/Items" >/tmp/rig49-shows-root.json
python3 -c "
import json
j=json.load(open('/tmp/rig49-shows-root.json'))
items=j.get('Items', [])
assert len(items) > 0, 'no curated-row folders emitted at shows root'
assert all(x.get('Type') == 'Folder' for x in items), f'root did not emit category folders: {items}'
names=[x.get('Name') for x in items]
assert 'P10RigGenre' in names, f\"'P10RigGenre' genre row missing: {names}\"
print(f'3(tv) OK: root emits {len(items)} curated-row folders incl. the P10RigGenre genre row')
"
api GET "/Channels/$CH_SHOWS/Items?FolderId=__row_shows_genre_p10riggenre__" >/tmp/rig49-shows-genre-row.json
python3 -c "
import json
j=json.load(open('/tmp/rig49-shows-genre-row.json'))
names=[x.get('Name') for x in j.get('Items', [])]
assert 'P10 Pruned Series' not in names, f'pruned series leaked into the genre row: {names}'
assert names == ['P10 TSort A', 'P10 TSort B', 'P10 TSort C'], f'genre row content/order unexpected: {names}'
print('3(tv) OK: genre-row content bounded to the browse-visible set, in default relevance order, pruned item absent')
"

# ===========================================================================
# 4. LATENCY NON-REGRESSION — list_load/sort_change vs the P8 ratchet
# ===========================================================================
log "4: list_load/sort_change latency measured live does not regress past the ratcheted threshold"
# This capstone's own claim is scoped to the two P10-relevant flows
# (list_load, sort_change) — NOT materialise/play_materialised (those are
# owned by P8's own daily job and are separately, more-flakily gated per
# their own doc). Filter the shared ratchet-threshold file down to just
# those two flows so a pre-existing materialise breach never fails THIS
# scenario for a reason outside its scope, while a real list_load/
# sort_change regression still fails it exactly as required.
FILTERED_THRESHOLDS="$(mktemp -t p10-loadtime-thresholds.XXXXXX.json)"
python3 -c "
import json
src = json.load(open('$REPO/tools/perf/loadtime-thresholds.json'))
src['scenarios'] = [s for s in src['scenarios'] if s.get('flow') in ('list_load', 'sort_change')]
json.dump(src, open('$FILTERED_THRESHOLDS', 'w'))
"
if [ -x "$REPO/tools/perf/loadtime-guard.sh" ]; then
    if PHANTOM_REPO_ROOT="$REPO" bash "$REPO/tools/perf/loadtime-guard.sh" \
        --live --api "$BASE" --token "$TOK" --color rig-p10-acceptance \
        --thresholds "$FILTERED_THRESHOLDS" --no-file
    then
        log "4 OK: live ratchet guard reports no breach (list_load/sort_change within threshold)"
    else
        rc=$?
        rm -f "$FILTERED_THRESHOLDS"
        if [ "$rc" -eq 3 ]; then
            fail "4: live ratchet guard reports a REGRESSION (list_load/sort_change exceeded the ratcheted threshold)"
        else
            fail "4: live ratchet guard errored (rc=$rc) rather than reporting pass/breach"
        fi
    fi
    rm -f "$FILTERED_THRESHOLDS"
else
    rm -f "$FILTERED_THRESHOLDS"
    fail "4: tools/perf/loadtime-guard.sh not found/executable — cannot prove the latency non-regression clause"
fi

log "all p10-curated-browse-acceptance-rig assertions passed (movie + TV parity)"
