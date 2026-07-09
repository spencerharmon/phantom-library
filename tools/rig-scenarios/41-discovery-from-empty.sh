#!/bin/bash
# Scenario 41: discovery-from-empty (synthetic provenance, P3 Stage 1).
#
# Proves the whole channel stack can be reconstructed from a *cold* phantom.db
# using ONLY the deterministic TMDB mock — no operator production state, no
# hand-seeded catalogue. This is the reusable synthetic-provenance fixture the
# migration-rig task builds on: the same from-empty discovery walk regenerates a
# known-shape catalogue that downstream scenarios can seed from without ever
# touching the operator's real ids.
#
# What it verifies, in order:
#   1. FROM-EMPTY precondition: after rig-up --reset the phantom schema is v11
#      and catalogue_items / availability_items / series_expansion_state /
#      materialised_state are ALL empty (nothing auto-populates on startup;
#      DiscoveryRefresh is a 6h interval task with no startup trigger).
#   2. Real discovery: trigger PhantomLibrary.DiscoveryRefresh and let it walk
#      TMDB trending + Discover against the mock.
#   3. CATALOGUE SHAPE (tmdb-keyed, synthetic): exactly the 3 movie + 2 series
#      mock fixtures land in catalogue_items, every id is synthetic (>=99000000
#      => zero operator PII), every row carries source_mask=3 (trending|discover),
#      movies seed availability_items(status='unknown') and series seed
#      series_expansion_state. tmdb-mock.log proves the data came from the mock.
#   4. CHANNEL SHAPE (tmdb-keyed) + downstream 35/36 parity, MOVIE and TV both
#      (AGENTS.md "Movie/TV parity"): flip one discovered movie and one
#      discovered episode to available (exactly what scenarios 35/36 seed), then
#      assert the channel surfaces them as tmdb-keyed phantom items
#      (ExternalId movie_<tmdb> / series_<tmdb> / episode_<tmdb>_sNNeNN,
#      ProviderIds.Tmdb, TMDB display name, phantom tag, native RequiresOpening
#      source) and that native-open playback still materialises through the
#      gostream mock into a real file that streams — i.e. the exact 35/36
#      native-open flow works when the catalogue was discovered, not seeded.
#   5. ZERO-PII gate: no discovery/materialise table references a sub-synthetic
#      tmdb id.
#
# *** OPERATOR / CI-RUN-ONLY ***
# Requires the patched Jellyfin server (channel arch) built from
# scripts/jellyfin-patches/ plus a plugin DLL built from this branch. The
# scenario is self-contained: it builds, brings the rig up with --reset, drives
# it, and (via an EXIT trap) tears the rig down again so it leaves no orphaned
# user-systemd units behind. See docs/tasks/discovery-from-empty-scenario.md.
set -euo pipefail

ROOT=${PHANTOM_REPO_ROOT:-/home/spencer/git-repos/spencerharmon/phantom-library}
RIG=/tmp/jf-rig
API=http://localhost:18096
TOK=testtoken00000000000000000000000
PHDB=/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db
JDB=/tmp/jf-test/data/data/jellyfin.db
TMDB_LOG=$RIG/logs/tmdb-mock.log
LOG=$RIG/logs/scenario-discovery-from-empty.log

# All rig data originates from the TMDB mock, whose fixtures are deliberately
# HIGH ids (>= this floor) so any sub-floor id in phantom.db would be operator
# PII leaking in from a real TMDB response.
SYNTH_MIN=99000000

# Fixtures we drive downstream playback through (both are in the discover walk).
ALPHA=99000001            # movie fixture -> external id movie_99000001
DELTA=99100001            # series fixture -> external id series_99100001
SEASON=1
EPISODE=1

mkdir -p "$RIG/logs"
exec > >(tee "$LOG") 2>&1
cd "$ROOT"

fail() { echo "FAIL: $*" >&2; exit 1; }
api() { curl -sS --fail -H "X-Emby-Token: $TOK" "$@"; }
api_post() { curl -sS --fail -X POST -H "X-Emby-Token: $TOK" "$@"; }
hyphen() { python3 - "$1" <<'PY'
import sys
s=sys.argv[1]
print(f'{s[:8]}-{s[8:12]}-{s[12:16]}-{s[16:20]}-{s[20:]}')
PY
}

# ---- teardown: stop the rig on exit (success OR failure), then checkpoint the
# phantom.db fixture so the file left on disk is a single consistent DB (no
# dangling -wal) for anyone that inspects the synthetic provenance afterwards.
cleanup() {
  local rc=$?
  bash "$ROOT/tools/rig-scenarios/rig-down.sh" >/dev/null 2>&1 || true
  [ -f "$PHDB" ] && sqlite3 "$PHDB" 'PRAGMA wal_checkpoint(TRUNCATE);' >/dev/null 2>&1 || true
  exit "$rc"
}
trap cleanup EXIT

find_task_id() {
  python3 - <<'PY'
import json
j=json.load(open('/tmp/tasks.json'))
for t in j:
    if t.get('Key') == 'PhantomLibrary.DiscoveryRefresh' or t.get('Name') == 'Phantom Library — Refresh Discovery':
        print(t['Id'])
        raise SystemExit(0)
raise SystemExit(1)
PY
}

wait_task_idle() {
  local task_id=$1
  for _ in $(seq 1 120); do
    api "$API/ScheduledTasks" -o /tmp/tasks.json
    state=$(python3 - "$task_id" <<'PY'
import json,sys
j=json.load(open('/tmp/tasks.json'))
for t in j:
    if t.get('Id') == sys.argv[1]:
        print(t.get('State'))
        break
PY
)
    echo "  task_state=$state"
    [ "$state" = "Idle" ] && return 0
    sleep 1
  done
  fail "task $task_id did not become Idle"
}

find_movies_channel_id() {
  python3 - <<'PY'
import json
j=json.load(open('/tmp/channels.json'))
items=j.get('Items', j if isinstance(j,list) else [])
for x in items:
    if x.get('Name') == 'Phantom Movies':
        print(x['Id'])
        raise SystemExit(0)
raise SystemExit(1)
PY
}

find_shows_channel_id() {
  python3 - <<'PY'
import json
j=json.load(open('/tmp/channels.json'))
items=j.get('Items', j if isinstance(j,list) else [])
for x in items:
    if x.get('Name') == 'Phantom Shows':
        print(x['Id'])
        raise SystemExit(0)
raise SystemExit(1)
PY
}

find_movie_id() {
  local tmdb=$1
  python3 - "$tmdb" <<'PY'
import json,sys
tmdb=sys.argv[1]
j=json.load(open('/tmp/movies.json'))
for x in j.get('Items', []):
    if (x.get('ProviderIds') or {}).get('Tmdb') == tmdb or x.get('ExternalId') == f'movie_{tmdb}':
        print(x['Id'])
        raise SystemExit(0)
raise SystemExit(1)
PY
}

# Native-open PlaybackInfo: phantom item must expose a single RequiresOpening
# source with a Phantom open token (ending $suffix) and no splash path.
assert_opening_playback_info() {
  local id=$1 label=$2 suffix=$3
  echo "[opening-playback-info] $label id=$id expect-token-suffix=$suffix"
  api "$API/Items/$id/PlaybackInfo" -o /tmp/pb-open.json || fail "$label PlaybackInfo HTTP error"
  python3 - "$label" "$suffix" <<'PY'
import json,sys,uuid
label,suffix=sys.argv[1],sys.argv[2]
j=json.load(open('/tmp/pb-open.json'))
if j.get('ErrorCode'):
    raise SystemExit(f'{label}: PlaybackInfo ErrorCode={j.get("ErrorCode")}')
sources=j.get('MediaSources') or []
print('  source_count=', len(sources))
if len(sources) != 1:
    raise SystemExit(f'{label}: expected exactly one MediaSource, got {len(sources)}')
s=sources[0]
print('  source=', {k:s.get(k) for k in ['Id','Path','RequiresOpening','OpenToken','Protocol','Container']})
if not s.get('RequiresOpening'):
    raise SystemExit(f'{label}: expected RequiresOpening source')
if not (s.get('OpenToken') or '').endswith(suffix):
    raise SystemExit(f'{label}: expected open token ending {suffix!r}, got {s.get("OpenToken")!r}')
if s.get('Path') not in (None, ''):
    raise SystemExit(f'{label}: expected no splash path, got {s.get("Path")!r}')
uuid.UUID(s.get('Id'))
PY
}

# AutoOpenLiveStream must materialise the phantom source through the gostream
# mock and return the real File source under $expect (the gostream root).
assert_auto_open_materialises() {
  local id=$1 label=$2 expect=$3
  local gid
  gid=$(hyphen "$id")
  echo "[auto-open-playback-info] $label guid=$gid expect-path=$expect"
  curl -sS --fail -X POST -H "X-Emby-Token: $TOK" -H 'Content-Type: application/json' \
    -d '{"AutoOpenLiveStream":true}' \
    "$API/Items/$gid/PlaybackInfo?AutoOpenLiveStream=true" -o /tmp/pb-auto-open.json \
    || fail "$label auto-open PlaybackInfo HTTP error"
  python3 - "$label" "$expect" <<'PY'
import json,sys,uuid
label,expect=sys.argv[1],sys.argv[2]
j=json.load(open('/tmp/pb-auto-open.json'))
if j.get('ErrorCode'):
    raise SystemExit(f'{label}: PlaybackInfo ErrorCode={j.get("ErrorCode")}')
sources=j.get('MediaSources') or []
print('  source_count=', len(sources))
if len(sources) != 1:
    raise SystemExit(f'{label}: expected exactly one MediaSource, got {len(sources)}')
s=sources[0]
print('  source=', {k:s.get(k) for k in ['Id','Path','RequiresOpening','LiveStreamId','Protocol','Container']})
if s.get('RequiresOpening'):
    raise SystemExit(f'{label}: auto-open should return the final real source')
path=s.get('Path') or ''
if expect not in path:
    raise SystemExit(f'{label}: expected path containing {expect!r}, got {path!r}')
if s.get('Protocol') != 'File':
    raise SystemExit(f'{label}: expected File protocol')
if len(s.get('MediaStreams') or []) < 1:
    raise SystemExit(f'{label}: expected probed MediaStreams on materialised source')
uuid.UUID(s.get('Id'))
PY
}

assert_stream_opens() {
  local id=$1 container=$2 label=$3
  local gid
  gid=$(hyphen "$id")
  echo "[stream-open] $label guid=$gid container=$container"
  local code bytes
  code=$(curl -sS -L --max-time 20 -H "X-Emby-Token: $TOK" -H 'Range: bytes=0-4095' \
    -o /tmp/stream.bin -w '%{http_code}' \
    "$API/Videos/$gid/stream.$container?static=true" || true)
  bytes=$(wc -c < /tmp/stream.bin 2>/dev/null || echo 0)
  echo "  http=$code bytes=$bytes"
  case "$code" in 200|206) : ;; *) fail "$label stream returned HTTP $code" ;; esac
  [ "$bytes" -gt 0 ] || fail "$label stream returned zero bytes"
}

# ---------------------------------------------------------------------------
echo '[0] build plugin + start reset rig'
read -r -a BUILD_ARGS <<< "${PHANTOM_DOTNET_BUILD_ARGS:-}"
dotnet build -c Release "${BUILD_ARGS[@]}" >/tmp/phantom-discovery-from-empty-build.log
bash tools/rig-scenarios/rig-up.sh --reset

for _ in $(seq 1 60); do
  [ -f "$PHDB" ] && schema=$(sqlite3 "$PHDB" 'PRAGMA user_version;' 2>/dev/null || echo 0) || schema=0
  [ "$schema" = "11" ] && break
  sleep 1
done
[ "${schema:-0}" = "11" ] || fail "phantom schema not v11, got ${schema:-0}"

echo '[1] from-empty precondition: phantom state is cold before discovery'
for tbl in catalogue_items availability_items series_expansion_state materialised_state; do
  n=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM $tbl;")
  echo "  $tbl=$n"
  [ "$n" = "0" ] || fail "expected empty $tbl before discovery (from-empty precondition), got $n"
done

echo '[2] trigger real discovery walk (TMDB trending + Discover via mock)'
api "$API/ScheduledTasks" -o /tmp/tasks.json
TASK_ID=$(find_task_id) || fail 'discovery task not found'
api_post "$API/ScheduledTasks/Running/$TASK_ID" -o /tmp/task-run.out || fail 'failed to start discovery task'
wait_task_idle "$TASK_ID"

echo '[3] catalogue shape: exactly the synthetic mock fixtures, tmdb-keyed, zero PII'
movies_count=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM catalogue_items WHERE type='movie';")
series_count=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM catalogue_items WHERE type='series';")
echo "  catalogue movies=$movies_count series=$series_count"
[ "$movies_count" = "3" ] || fail "expected exactly 3 discovered catalogue movies, got $movies_count"
[ "$series_count" = "2" ] || fail "expected exactly 2 discovered catalogue series, got $series_count"

# Exact id set proves the catalogue was reconstructed from the mock fixtures and
# nothing else (no operator ids, no fuzzy scanner rescue).
movie_ids=$(sqlite3 "$PHDB" "SELECT group_concat(tmdb_id) FROM (SELECT tmdb_id FROM catalogue_items WHERE type='movie' ORDER BY tmdb_id);")
series_ids=$(sqlite3 "$PHDB" "SELECT group_concat(tmdb_id) FROM (SELECT tmdb_id FROM catalogue_items WHERE type='series' ORDER BY tmdb_id);")
echo "  movie_ids=$movie_ids series_ids=$series_ids"
[ "$movie_ids" = "99000001,99000002,99000003" ] || fail "unexpected movie id set: $movie_ids"
[ "$series_ids" = "99100001,99100002" ] || fail "unexpected series id set: $series_ids"

# Every catalogue row must carry BOTH source bits (trending=1 | discover=2 = 3):
# a from-empty run visits each fixture on both the trending and Discover phase.
badmask=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM catalogue_items WHERE source_mask <> 3;")
[ "$badmask" = "0" ] || fail "expected source_mask=3 (trending|discover) on all rows, $badmask row(s) differ"

# Discovery side-tables: movies seed availability(status='unknown'), series seed
# expansion state. These are what later gate channel visibility.
mav=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM availability_items WHERE type='movie' AND season=-1 AND episode=-1 AND status='unknown';")
[ "$mav" = "3" ] || fail "expected 3 movie availability(unknown) rows from discovery, got $mav"
sexp=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM series_expansion_state;")
[ "$sexp" = "2" ] || fail "expected 2 series_expansion_state rows from discovery, got $sexp"

# Provenance: discovery actually hit the mock (not a real TMDB / operator cache).
for ep in '/3/trending/movie/week' '/3/trending/tv/week' '/3/discover/movie' '/3/discover/tv'; do
  grep -Fq "$ep" "$TMDB_LOG" || fail "discovery did not hit tmdb-mock endpoint $ep (synthetic provenance unproven)"
done
echo "  tmdb-mock provenance ok (trending + discover, movie + tv)"

echo '[4] MOVIE channel shape (tmdb-keyed) + native-open 35-parity'
now=$(date +%s)
# Flip discovered Alpha to available + seed a magnet so it can materialise. This
# is exactly the seed scenario 35 performs after its own discovery step; here
# the catalogue row it points at was DISCOVERED, not hand-inserted.
sqlite3 "$PHDB" <<SQL
INSERT OR REPLACE INTO magnet_cache
(tmdb_id, imdb_id, type, season, episode, preset, magnet, info_hash, size, seeders, indexer, cached_at, ttl_seconds, source)
VALUES
($ALPHA, 'tt99000001', 'movie', 0, 0, 'gostream-default',
 'magnet:?xt=urn:btih:1111111111111111111111111111111111111111&dn=Phantom+Rig+Alpha',
 '1111111111111111111111111111111111111111', 10485760, 100, 'rig-cache', $now, 86400, 'rig');
INSERT OR REPLACE INTO availability_items
(tmdb_id, type, season, episode, status, checked_at, next_check_at, candidate_magnet, candidate_info_hash, candidate_size, candidate_seeders, candidate_indexer, candidate_source)
VALUES
($ALPHA, 'movie', -1, -1, 'available', $now, $((now + 604800)),
 'magnet:?xt=urn:btih:1111111111111111111111111111111111111111&dn=Phantom+Rig+Alpha',
 '1111111111111111111111111111111111111111', 10485760, 100, 'rig-cache', 'rig');
INSERT OR REPLACE INTO plugin_meta(key,value) VALUES('channel_dataversion_movies', '$now-rig-seed');
SQL

api "$API/Channels" -o /tmp/channels.json
MOVIES_CH=$(find_movies_channel_id) || fail 'Phantom Movies channel not found'
api "$API/Channels/$MOVIES_CH/Items?Fields=Tags,ProviderIds,MediaSources,Path,Overview,ProductionYear&Limit=50" -o /tmp/movies.json
ALPHA_ID=$(find_movie_id "$ALPHA") || fail 'discovered Alpha movie did not surface in channel after availability flip'
python3 - "$ALPHA_ID" "$ALPHA" <<'PY'
import json,sys
id,tmdb=sys.argv[1],sys.argv[2]
j=json.load(open('/tmp/movies.json'))
x=next(i for i in j['Items'] if i['Id']==id)
print('MOVIE_ITEM=', x.get('Name'), x.get('ExternalId'), x.get('ProviderIds'), x.get('Tags'))
if x.get('ExternalId') != f'movie_{tmdb}':
    raise SystemExit(f'expected tmdb-keyed ExternalId movie_{tmdb}, got {x.get("ExternalId")!r}')
if (x.get('ProviderIds') or {}).get('Tmdb') != tmdb:
    raise SystemExit(f'expected ProviderIds.Tmdb={tmdb}, got {x.get("ProviderIds")}')
if x.get('Name') != 'Phantom Rig Alpha':
    raise SystemExit(f'expected TMDB display name, got {x.get("Name")!r}')
if 'phantom' not in (x.get('Tags') or []):
    raise SystemExit(f'discovered-not-materialised movie should be phantom-tagged, tags={x.get("Tags")}')
src=(x.get('MediaSources') or [{}])[0]
if not src.get('RequiresOpening'):
    raise SystemExit(f'phantom movie should expose native RequiresOpening source, got {src}')
if src.get('Path') not in (None, ''):
    raise SystemExit(f'phantom movie should not start on a splash path, got {src.get("Path")!r}')
PY
assert_opening_playback_info "$ALPHA_ID" 'discovered-movie' "_phantom:movie_$ALPHA"
assert_auto_open_materialises "$ALPHA_ID" 'discovered-movie-auto-open' '/tmp/jf-rig/gostream/movies/'
mstate=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM materialised_state WHERE tmdb_id=$ALPHA AND type='movie' AND season=-1 AND episode=-1;")
[ "$mstate" = "1" ] || fail "movie materialised_state row missing after native auto-open"
assert_stream_opens "$ALPHA_ID" 'mkv' 'discovered-movie'

echo '[5] TV channel shape (tmdb-keyed) + native-open 36-parity'
# Flip one discovered-series episode to available + seed its magnet (scenario 36
# seed). The series row + expansion state were DISCOVERED in step [2].
sqlite3 "$PHDB" <<SQL
INSERT OR REPLACE INTO magnet_cache
(tmdb_id, imdb_id, type, season, episode, preset, magnet, info_hash, size, seeders, indexer, cached_at, ttl_seconds, source)
VALUES
($DELTA, 'tt99100001', 'episode', $SEASON, $EPISODE, 'gostream-default',
 'magnet:?xt=urn:btih:2222222222222222222222222222222222222222&dn=Phantom+Rig+Delta+S01E01',
 '2222222222222222222222222222222222222222', 10485760, 100, 'rig-cache', $now, 86400, 'rig');
INSERT OR REPLACE INTO availability_items
(tmdb_id, type, season, episode, status, checked_at, next_check_at, candidate_magnet, candidate_info_hash, candidate_size, candidate_seeders, candidate_indexer, candidate_source)
VALUES
($DELTA, 'episode', $SEASON, $EPISODE, 'available', $now, $((now + 604800)),
 'magnet:?xt=urn:btih:2222222222222222222222222222222222222222&dn=Phantom+Rig+Delta+S01E01',
 '2222222222222222222222222222222222222222', 10485760, 100, 'rig-cache', 'rig');
INSERT OR REPLACE INTO plugin_meta(key,value) VALUES('channel_dataversion_shows', '$now-rig-seed');
SQL

api "$API/Channels" -o /tmp/channels.json
SHOWS_CH=$(find_shows_channel_id) || fail 'Phantom Shows channel not found'
api "$API/Channels/$SHOWS_CH/Items?Fields=Tags,ProviderIds,MediaSources,Path,Overview,ProductionYear,ExternalId&Limit=50" -o /tmp/series.json
SERIES_ID=$(python3 - "$DELTA" <<'PY'
import json,sys
tmdb=sys.argv[1]
j=json.load(open('/tmp/series.json'))
for x in j.get('Items', []):
    if (x.get('ProviderIds') or {}).get('Tmdb') == tmdb or x.get('ExternalId') == f'series_{tmdb}':
        if x.get('ExternalId') != f'series_{tmdb}':
            raise SystemExit(f'expected tmdb-keyed ExternalId series_{tmdb}, got {x.get("ExternalId")!r}')
        if x.get('Name') != 'Phantom Rig Delta':
            raise SystemExit(f'expected TMDB series display name, got {x.get("Name")!r}')
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'discovered Delta series did not surface in channel after episode availability flip'

api "$API/Channels/$SHOWS_CH/Items?FolderId=$SERIES_ID&Fields=Tags,ProviderIds,MediaSources,Path,Overview,ExternalId,ProductionYear&Limit=50" -o /tmp/seasons.json
SEASON_ID=$(python3 - <<'PY'
import json,sys
j=json.load(open('/tmp/seasons.json'))
for x in j.get('Items', []):
    if x.get('Name') == 'Season 1':
        if x.get('Type') != 'Season':
            raise SystemExit('season item did not use native Season type: ' + str(x.get('Type')))
        print('SEASON_ITEM', x.get('Name'), x.get('Id'), x.get('ExternalId'), file=sys.stderr)
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'Delta season 1 not found'

api "$API/Channels/$SHOWS_CH/Items?FolderId=$SEASON_ID&Fields=Tags,ProviderIds,MediaSources,Path,Overview,ExternalId&Limit=50" -o /tmp/episodes.json
EP_ID=$(python3 - "$DELTA" <<'PY'
import json,sys
tmdb=sys.argv[1]
j=json.load(open('/tmp/episodes.json'))
for x in j.get('Items', []):
    if x.get('Name') == 'Phantom Rig Delta Episode 1':
        if x.get('ExternalId') != f'episode_{tmdb}_s01e01':
            raise SystemExit(f'expected tmdb-keyed ExternalId episode_{tmdb}_s01e01, got {x.get("ExternalId")!r}')
        if 'phantom' not in (x.get('Tags') or []):
            raise SystemExit(f'discovered-not-materialised episode should be phantom-tagged, tags={x.get("Tags")}')
        src=(x.get('MediaSources') or [{}])[0]
        if not src.get('RequiresOpening'):
            raise SystemExit(f'phantom episode should expose native RequiresOpening source, got {src}')
        if src.get('Path') not in (None, ''):
            raise SystemExit(f'phantom episode should not start on a splash path, got {src.get("Path")!r}')
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'Delta S01E01 episode not found or not tmdb-keyed'

assert_opening_playback_info "$EP_ID" 'discovered-episode' "_phantom:episode_${DELTA}_s01e01"
assert_auto_open_materialises "$EP_ID" 'discovered-episode-auto-open' '/tmp/jf-rig/gostream/tv/'
estate=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM materialised_state WHERE tmdb_id=$DELTA AND type='episode' AND season=$SEASON AND episode=$EPISODE;")
[ "$estate" = "1" ] || fail "episode materialised_state row missing after native auto-open"
assert_stream_opens "$EP_ID" 'mkv' 'discovered-episode'

echo '[6] zero-PII gate: no discovery/materialise state references a sub-synthetic id'
pii=$(sqlite3 "$PHDB" <<SQL
SELECT
  (SELECT COUNT(*) FROM catalogue_items      WHERE tmdb_id        < $SYNTH_MIN) +
  (SELECT COUNT(*) FROM availability_items   WHERE tmdb_id        < $SYNTH_MIN) +
  (SELECT COUNT(*) FROM materialised_state   WHERE tmdb_id        < $SYNTH_MIN) +
  (SELECT COUNT(*) FROM series_expansion_state WHERE series_tmdb_id < $SYNTH_MIN);
SQL
)
[ "$pii" = "0" ] || fail "found $pii row(s) with sub-synthetic tmdb id (<$SYNTH_MIN): operator PII leak"
echo "  zero-PII ok (catalogue/availability/materialised/expansion all >= $SYNTH_MIN)"

echo '[7] synthetic-provenance fixture summary'
echo "  schema=$(sqlite3 "$PHDB" 'PRAGMA user_version;')"
echo "  catalogue movies=$movies_count series=$series_count ids=[$movie_ids][$series_ids]"
echo "  materialised movie($ALPHA)=$mstate episode(${DELTA}_s0${SEASON}e0${EPISODE})=$estate"

echo 'DISCOVERY_FROM_EMPTY_OK'
