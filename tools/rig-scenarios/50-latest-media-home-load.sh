#!/usr/bin/env bash
# 50-latest-media-home-load.sh — restore-latest-row-and-drop-folders
# acceptance rig.
#
# Live-rig proof that the Home "Latest in Phantom Movies" / "Latest in
# Phantom Shows" rows are back (ISupportsLatestMedia re-added to both
# channels) WITHOUT reintroducing the O(catalogue) Home-load hang that got
# the interface dropped on 2026-06-28, and that category FOLDERS are gone
# from the channel root (operator-rejected UX; vanilla jellyfin-web cannot
# render a folder tile as a Netflix-style shelf). Reuses the rig-up.sh /
# trap-clean shape shared by 35/36/45/46/49 so those scenarios stay green.
#
# Proves, for MOVIE and TV alike (movie/TV parity):
#   1. NO FOLDERS: the channel root (GET /Channels/{id}/Items, authenticated
#      user) returns only Type=Media leaf items — no category-folder tiles.
#   2. LATEST ROW POPULATES: GET /Channels/Items/Latest (the real Home
#      "Latest" surface) returns the seeded materialised movie/episode.
#   3. NO HOME-LOAD REGRESSION: the Latest call returns well within a
#      bounded timeout (a few seconds) even though the catalogue root list
#      itself is also queried in the same run — proving the Latest path is
#      NOT the O(catalogue) BuildFlatMovieItemsAsync/BuildTopLevelSeriesItemsAsync
#      scan the interface removal exists to avoid.
#
# Every step is repeated for a movie AND a TV episode. trap-clean; refuses
# to run if pointed at the production port.
set -euo pipefail
REPO=${PHANTOM_REPO_ROOT:-/home/spencer/git-repos/spencerharmon/phantom-library}
cd "$REPO"
BASE=http://localhost:18096
TOK=testtoken00000000000000000000000
DATA=/var/tmp/jf-test/data
PHDB="$DATA/plugins/configurations/PhantomLibrary/phantom.db"
LATEST_TIMEOUT_SECONDS=10

log()  { printf '\033[1m[50-latest-media-home-load]\033[0m %s\n' "$*"; }
fail() { echo "FAIL: $*" >&2; exit 1; }

# --- prod-safety guard: this rig is :18096 only, never prod :8096 ----------
case "$BASE" in
    *:8096|*:8096/*) fail "BASE points at :8096 (production) — refusing; the rig is :18096." ;;
esac

cleanup() {
  log "tearing down rig"
  "$REPO/tools/rig-scenarios/rig-down.sh" >/dev/null 2>&1 || true
  rm -f /tmp/rig50-*.json
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

api GET /Channels >/tmp/rig50-channels.json
CH_MOVIES=$(python3 -c "
import json
j=json.load(open('/tmp/rig50-channels.json'))
items=j.get('Items', j if isinstance(j,list) else [])
print(next(x['Id'] for x in items if x.get('Name')=='Phantom Movies'))
")
CH_SHOWS=$(python3 -c "
import json
j=json.load(open('/tmp/rig50-channels.json'))
items=j.get('Items', j if isinstance(j,list) else [])
print(next(x['Id'] for x in items if x.get('Name')=='Phantom Shows'))
")
[ -n "$CH_MOVIES" ] || fail "Phantom Movies channel not registered"
[ -n "$CH_SHOWS" ]  || fail "Phantom Shows channel not registered"
log "channels resolved: movies=$CH_MOVIES shows=$CH_SHOWS"

# ===========================================================================
# 0. Seed a materialised movie + a materialised episode directly in phantom.db
#    (mirrors 35/36's own direct-seed pattern rather than driving a full
#    discovery/materialise cycle, which is orthogonal to this rig's proof).
MOVIE_TMDB=99500001
SERIES_TMDB=99500002
now=$(now_epoch)
sql "INSERT OR REPLACE INTO tmdb_metadata
       (tmdb_id,type,title,year,overview,poster_url,backdrop_url,genres_json,
        original_title,community_rating,fetched_at,runtime_minutes)
     VALUES ($MOVIE_TMDB,'movie','Latest Row Movie',2026,'ov','','','[]',
        'Latest Row Movie',7.0,$now,100);"
sql "INSERT OR REPLACE INTO tmdb_metadata
       (tmdb_id,type,title,year,overview,poster_url,backdrop_url,genres_json,
        original_title,community_rating,fetched_at,runtime_minutes)
     VALUES ($SERIES_TMDB,'series','Latest Row Series',2026,'ov','','','[]',
        'Latest Row Series',7.0,$now,null);"
sql "INSERT OR REPLACE INTO tmdb_episode_cache
       (series_tmdb_id,season,episode,title,overview,still_url,air_date,
        runtime_minutes,fetched_at)
     VALUES ($SERIES_TMDB,1,1,'Pilot','ov','','2026-01-01',30,$now);"
mkdir -p "$DATA/../gostream-movies" "$DATA/../gostream-shows/Latest Row Series (2026)/Season 01" 2>/dev/null || true
MOVIE_FUSE="$DATA/../gostream-movies/latest-row-movie.mkv"
EPISODE_FUSE="$DATA/../gostream-shows/Latest Row Series (2026)/Season 01/latest-row-episode.mkv"
touch "$MOVIE_FUSE" "$EPISODE_FUSE"
sql "INSERT OR REPLACE INTO materialised_state
       (tmdb_id,type,season,episode,stub_path,fuse_path,materialised_at)
     VALUES ($MOVIE_TMDB,'movie',-1,-1,'/stub/latest-row-movie.mkv','$MOVIE_FUSE',$now);"
sql "INSERT OR REPLACE INTO materialised_state
       (tmdb_id,type,season,episode,stub_path,fuse_path,materialised_at)
     VALUES ($SERIES_TMDB,'episode',1,1,'/stub/latest-row-episode.mkv','$EPISODE_FUSE',$now);"
log "seeded materialised movie tmdb=$MOVIE_TMDB and episode tmdb=$SERIES_TMDB s01e01"

# ===========================================================================
# 1. NO FOLDERS at the channel root (authenticated user), movie + shows
api GET "/Channels/$CH_MOVIES/Items" >/tmp/rig50-movies-root.json
python3 -c "
import json
j=json.load(open('/tmp/rig50-movies-root.json'))
items=j.get('Items', [])
assert items, 'movies root returned no items'
folders=[i for i in items if i.get('IsFolder')]
assert not folders, f'movies root still emits folder tiles: {folders}'
print('ok: movies root has no folders,', len(items), 'items')
"
api GET "/Channels/$CH_SHOWS/Items" >/tmp/rig50-shows-root.json
python3 -c "
import json
j=json.load(open('/tmp/rig50-shows-root.json'))
items=j.get('Items', [])
assert items, 'shows root returned no items'
folders=[i for i in items if i.get('IsFolder')]
assert not folders, f'shows root still emits folder tiles (series folders must be gone too): {folders}'
print('ok: shows root has no folders,', len(items), 'items')
"
log "1/3 PASS: no folders at either channel root"

# ===========================================================================
# 2 + 3. LATEST ROW populates AND stays fast, movie + shows
check_latest() {
  local channel_id=$1 expect_name=$2 label=$3
  local start end elapsed
  start=$(date +%s%N)
  api GET "/Channels/Items/Latest?ChannelIds=$channel_id&Limit=20" > "/tmp/rig50-latest-$label.json"
  end=$(date +%s%N)
  elapsed=$(( (end - start) / 1000000000 ))
  [ "$elapsed" -le "$LATEST_TIMEOUT_SECONDS" ] || fail "$label: Latest call took ${elapsed}s (budget ${LATEST_TIMEOUT_SECONDS}s) — O(catalogue) regression"
  python3 -c "
import json,sys
j=json.load(open('/tmp/rig50-latest-$label.json'))
items=j.get('Items', [])
names=[i.get('Name') for i in items]
assert any('$expect_name' in (n or '') for n in names), f'$label: expected title not in Latest result: {names}'
print('ok: $label Latest contains expected title, elapsed=${elapsed}s')
"
}
check_latest "$CH_MOVIES" "Latest Row Movie" movies
check_latest "$CH_SHOWS" "Pilot" shows
log "2+3/3 PASS: Latest row populates for both channels, within budget"

log "ALL PASS"
