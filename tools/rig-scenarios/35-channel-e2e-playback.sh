#!/bin/bash
# Full channel architecture end-to-end playback test.
# Starts patched rig, uses TMDB + gostream mocks, then verifies:
# - discovery task warms channel metadata
# - existing gostream movie enriches and plays as real movie_<tmdb>
# - phantom movie PlaybackInfo exposes RequiresOpening native-open source
# - POST PlaybackInfo AutoOpenLiveStream materialises through gostream mock and returns real file
# - refreshed channel item points at real FUSE path
# - materialised movie PlaybackInfo + stream open succeed
set -euo pipefail

ROOT=${PHANTOM_REPO_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
RIG=/tmp/jf-rig
API=http://localhost:18096
TOK=testtoken00000000000000000000000
PHDB=/var/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db
JDB=/var/tmp/jf-test/data/data/jellyfin.db
LOG=$RIG/logs/scenario-channel-e2e-playback.log
ALPHA=99000001
BRAVO=99000002
USER_ID=
USER_AUTH_TOKEN=

mkdir -p "$RIG/logs"
exec > >(tee "$LOG") 2>&1
cd "$ROOT"

fail() { echo "FAIL: $*" >&2; exit 1; }
api() { curl -sS --fail -H "X-Emby-Token: $TOK" "$@"; }
api_post() { curl -sS --fail -X POST -H "X-Emby-Token: $TOK" "$@"; }
json_post() { curl -sS --fail -X POST -H "X-Emby-Token: $TOK" -H 'Content-Type: application/json' "$@"; }
user_api() { curl -sS --fail -H "X-Emby-Token: $USER_AUTH_TOKEN" "$@"; }
user_json_post() { curl -sS --fail -X POST -H "X-Emby-Token: $USER_AUTH_TOKEN" -H "X-Emby-Authorization: MediaBrowser Client=\"phantom-rig\", Device=\"phantom-rig\", DeviceId=\"phantom-rig-device\", Version=\"1\", Token=\"$USER_AUTH_TOKEN\"" -H 'Content-Type: application/json' "$@"; }
hyphen() { python3 - "$1" <<'PY'
import sys
s=sys.argv[1]
print(f'{s[:8]}-{s[8:12]}-{s[12:16]}-{s[16:20]}-{s[20:]}')
PY
}

assert_playback_info() {
  local id=$1 expect_path_sub=$2 label=$3 min_streams=${4:-0}
  echo "[playback-info] $label id=$id"
  for _ in $(seq 1 30); do
    api "$API/Items/$id/PlaybackInfo" -o /tmp/pb.json || fail "$label PlaybackInfo HTTP error"
    streams=$(python3 - <<'PY2'
import json
j=json.load(open('/tmp/pb.json'))
print(len((j.get('MediaSources') or [{}])[0].get('MediaStreams') or []))
PY2
)
    [ "$streams" -ge "$min_streams" ] && break
    sleep 1
  done
  python3 - "$expect_path_sub" "$label" "$min_streams" <<'PY'
import json,sys
expect=sys.argv[1]
label=sys.argv[2]
min_streams=int(sys.argv[3])
j=json.load(open('/tmp/pb.json'))
if j.get('ErrorCode'):
    raise SystemExit(f'{label}: PlaybackInfo ErrorCode={j.get("ErrorCode")}')
sources=j.get('MediaSources') or []
print('  source_count=', len(sources))
if len(sources) != 1:
    raise SystemExit(f'{label}: expected exactly one MediaSource, got {len(sources)}')
s=sources[0]
print('  source=', {k:s.get(k) for k in ['Id','Path','Protocol','Container','SupportsDirectPlay','SupportsDirectStream','SupportsTranscoding']})
import uuid
if not s.get('Id'):
    raise SystemExit(f'{label}: expected non-empty MediaSource.Id')
try:
    uuid.UUID(s.get('Id'))
except ValueError as e:
    raise SystemExit(f'{label}: expected Guid-shaped MediaSource.Id, got {s.get("Id")!r}') from e
path=s.get('Path') or ''
if expect not in path:
    raise SystemExit(f'{label}: expected path containing {expect!r}, got {path!r}')
if s.get('Protocol') != 'File':
    raise SystemExit(f'{label}: expected File protocol')
streams=s.get('MediaStreams') or []
if len(streams) < min_streams:
    raise SystemExit(f'{label}: expected at least {min_streams} MediaStreams, got {len(streams)}')
PY
}

assert_opening_playback_info() {
  local id=$1 label=$2
  echo "[opening-playback-info] $label id=$id"
  api "$API/Items/$id/PlaybackInfo" -o /tmp/pb-open.json || fail "$label PlaybackInfo HTTP error"
  python3 - "$label" <<'PY'
import json,sys,uuid
label=sys.argv[1]
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
if not (s.get('OpenToken') or '').endswith('_phantom:movie_99000001'):
    raise SystemExit(f'{label}: expected Phantom open token, got {s.get("OpenToken")!r}')
if s.get('Path') not in (None, ''):
    raise SystemExit(f'{label}: expected no splash path, got {s.get("Path")!r}')
try:
    uuid.UUID(s.get('Id'))
except ValueError as e:
    raise SystemExit(f'{label}: expected Guid-shaped MediaSource.Id, got {s.get("Id")!r}') from e
PY
}

assert_auto_open_materialises() {
  local id=$1 expect_path_sub=$2 label=$3 min_streams=${4:-1}
  local gid
  gid=$(hyphen "$id")
  echo "[auto-open-playback-info] $label guid=$gid"
  curl -sS --fail -X POST -H "X-Emby-Token: $TOK" -H 'Content-Type: application/json' \
    -d '{"AutoOpenLiveStream":true}' \
    "$API/Items/$gid/PlaybackInfo?AutoOpenLiveStream=true" -o /tmp/pb-auto-open.json \
    || fail "$label auto-open PlaybackInfo HTTP error"
  cp /tmp/pb-auto-open.json /tmp/pb.json
  python3 - "$expect_path_sub" "$label" "$min_streams" <<'PY'
import json,sys,uuid
expect=sys.argv[1]
label=sys.argv[2]
min_streams=int(sys.argv[3])
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
    raise SystemExit(f'{label}: auto-open should return final real source')
path=s.get('Path') or ''
if expect not in path:
    raise SystemExit(f'{label}: expected path containing {expect!r}, got {path!r}')
if s.get('Protocol') != 'File':
    raise SystemExit(f'{label}: expected File protocol')
try:
    uuid.UUID(s.get('Id'))
except ValueError as e:
    raise SystemExit(f'{label}: expected Guid-shaped MediaSource.Id, got {s.get("Id")!r}') from e
streams=s.get('MediaStreams') or []
if len(streams) < min_streams:
    raise SystemExit(f'{label}: expected at least {min_streams} MediaStreams, got {len(streams)}')
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

assert_runtime_ticks() {
  local file=$1 id=$2 min_ticks=$3 label=$4
  python3 - "$file" "$id" "$min_ticks" "$label" <<'PY'
import json,sys
file,id,min_ticks,label=sys.argv[1],sys.argv[2],int(sys.argv[3]),sys.argv[4]
j=json.load(open(file))
x=next((i for i in j.get('Items',[]) if i.get('Id')==id), None)
if x is None:
    raise SystemExit(f'{label}: item {id} missing')
rt=x.get('RunTimeTicks') or 0
print(f'  {label} runtime_ticks={rt}')
if rt < min_ticks:
    raise SystemExit(f'{label}: expected RunTimeTicks >= {min_ticks}, got {rt}')
PY
}

report_resume_progress_and_assert() {
  local id=$1 label=$2 position_ticks=$3
  local gid source_id play_session
  gid=$(hyphen "$id")
  play_session="phantom-rig-$label"
  source_id=$(python3 - <<'PY'
import json
try:
    j=json.load(open('/tmp/pb.json'))
    print(((j.get('MediaSources') or [{}])[0]).get('Id') or '')
except FileNotFoundError:
    print('')
PY
)
  echo "[resume-progress] $label guid=$gid position=$position_ticks source=$source_id"
  python3 - "$gid" "$source_id" "$play_session" "$position_ticks" > /tmp/playback-progress.json <<'PY'
import json,sys
item,source,session,pos=sys.argv[1],sys.argv[2],sys.argv[3],int(sys.argv[4])
print(json.dumps({
  'ItemId': item,
  'MediaSourceId': source,
  'PlaySessionId': session,
  'PositionTicks': pos,
  'CanSeek': True,
  'IsPaused': True,
  'PlayMethod': 'DirectPlay'
}))
PY
  user_json_post --data-binary @/tmp/playback-progress.json "$API/Sessions/Playing" -o /tmp/playback-start.out || true
  user_json_post --data-binary @/tmp/playback-progress.json "$API/Sessions/Playing/Progress" -o /tmp/playback-progress.out || fail "$label playback progress failed"
  local persisted_runtime resume_ticks
  persisted_runtime=$(sqlite3 "$JDB" "SELECT COALESCE(RunTimeTicks,0) FROM BaseItems WHERE Id=upper('$gid');")
  resume_ticks=$position_ticks
  if [ "${persisted_runtime:-0}" -gt 0 ] && [ "$resume_ticks" -ge "$persisted_runtime" ]; then
    resume_ticks=$((persisted_runtime / 2))
  fi
  python3 - "$resume_ticks" > /tmp/userdata-resume.json <<'PY'
import json,sys
pos=int(sys.argv[1])
print(json.dumps({'PlaybackPositionTicks': pos, 'Played': False, 'PlayCount': 0, 'IsFavorite': False}))
PY
  user_json_post --data-binary @/tmp/userdata-resume.json "$API/Users/$USER_ID/Items/$gid/UserData" -o /tmp/userdata-resume.out || fail "$label userdata resume update failed"
  for _ in $(seq 1 20); do
    user_api "$API/Users/$USER_ID/Items/Resume?Fields=UserData,ProviderIds,RunTimeTicks&MediaTypes=Video&EnableUserData=true&Limit=50" -o /tmp/resume.json || fail "$label resume query failed"
    if python3 - "$id" "$label" <<'PY'
import json,sys
wanted=sys.argv[1].lower()
j=json.load(open('/tmp/resume.json'))
for x in j.get('Items',[]):
    if (x.get('Id') or '').replace('-','').lower() == wanted:
        ud=x.get('UserData') or {}
        print('  resume_hit=', x.get('Name'), x.get('Id'), x.get('RunTimeTicks'), ud)
        if (ud.get('PlaybackPositionTicks') or 0) <= 0:
            raise SystemExit('resume hit missing PlaybackPositionTicks')
        raise SystemExit(0)
raise SystemExit(1)
PY
    then return 0; fi
    sleep 1
  done
  python3 - <<'PY'
import json
j=json.load(open('/tmp/resume.json'))
print('RESUME_ITEMS=', [(x.get('Name'), x.get('Id'), x.get('RunTimeTicks'), x.get('UserData')) for x in j.get('Items',[])])
PY
  fail "$label did not appear in Continue Watching"
}

echo '[0] build plugin + start reset rig'
read -r -a BUILD_ARGS <<< "${PHANTOM_DOTNET_BUILD_ARGS:-}"
dotnet build -c Release "${BUILD_ARGS[@]}" >/tmp/phantom-e2e-build.log
bash tools/rig-scenarios/rig-up.sh --reset

for _ in $(seq 1 60); do
  [ -f "$PHDB" ] && schema=$(sqlite3 "$PHDB" 'PRAGMA user_version;' 2>/dev/null || echo 0) || schema=0
  [ "$schema" = "14" ] && break
  sleep 1
done
[ "${schema:-0}" = "14" ] || fail "phantom schema not v14, got ${schema:-0}"
curl -sS --fail -X POST -H 'Content-Type: application/json' \
  -H 'X-Emby-Authorization: MediaBrowser Client="phantom-rig", Device="phantom-rig", DeviceId="phantom-rig-login", Version="1"' \
  -d '{"Username":"a","Pw":"a"}' "$API/Users/AuthenticateByName" -o /tmp/auth-user.json \
  || fail 'test user login failed'
USER_ID=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/auth-user.json'))
print((j.get('User') or {}).get('Id') or '')
PY
)
USER_AUTH_TOKEN=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/auth-user.json'))
print(j.get('AccessToken') or '')
PY
)
[ -n "$USER_ID" ] || fail 'test user id missing'
[ -n "$USER_AUTH_TOKEN" ] || fail 'test user token missing'

echo '[1] trigger discovery task'
api "$API/ScheduledTasks" -o /tmp/tasks.json
TASK_ID=$(find_task_id) || fail 'discovery task not found'
api_post "$API/ScheduledTasks/Running/$TASK_ID" -o /tmp/task-run.out || fail 'failed to start discovery task'
wait_task_idle "$TASK_ID"
movies_count=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM catalogue_items WHERE type='movie';")
[ "$movies_count" -ge 3 ] || fail "expected >=3 catalogue movies, got $movies_count"

echo '[2] seed magnet cache for Alpha materialise'
now=$(date +%s)
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

echo '[3] browse channels'
api "$API/Channels" -o /tmp/channels.json
MOVIES_CH=$(find_movies_channel_id) || fail 'Phantom Movies channel not found'
api "$API/Channels/$MOVIES_CH/Items?Fields=Tags,ProviderIds,MediaSources,Path,Overview,ProductionYear,RunTimeTicks&Limit=50" -o /tmp/movies.json
python3 - <<'PY'
import json
j=json.load(open('/tmp/movies.json'))
for x in j.get('Items', []):
    if (x.get('ProviderIds') or {}).get('Tmdb') in ('99000001','99000002'):
        print('ITEM', x.get('Name'), x.get('Id'), x.get('ProviderIds'), x.get('Tags'), [(m.get('Path'),m.get('Container')) for m in x.get('MediaSources',[])])
PY
ALPHA_ID=$(find_movie_id "$ALPHA") || fail 'Alpha movie not found in channel'
BRAVO_ID=$(find_movie_id "$BRAVO") || fail 'Bravo movie not found in channel'
assert_runtime_ticks /tmp/movies.json "$ALPHA_ID" 57000000000 'phantom-alpha-browse'
assert_runtime_ticks /tmp/movies.json "$BRAVO_ID" 57000000000 'existing-gostream-bravo-browse'

echo '[4] assert existing gostream Bravo enriches as real playable source'
python3 - "$BRAVO_ID" <<'PY'
import json,sys
id=sys.argv[1]
j=json.load(open('/tmp/movies.json'))
x=next(i for i in j['Items'] if i['Id']==id)
if 'orphan' in (x.get('Tags') or []) or 'phantom' in (x.get('Tags') or []):
    raise SystemExit(f'Bravo should be real gostream item, tags={x.get("Tags")}')
items=[i for i in j['Items'] if 'Phantom_Rig_Bravo' in (i.get('Name') or '')]
if items:
    raise SystemExit(f'Bravo variants should not surface as raw orphans: {items}')
path=(x.get('MediaSources') or [{}])[0].get('Path') or ''
if path != '/tmp/jf-rig/gostream/movies/Phantom_Rig_Bravo_2024_2160p_HDR_cafebabe.mkv':
    raise SystemExit(f'Bravo should select best variant, got {path}')
if x.get('Name') != 'Phantom Rig Bravo':
    raise SystemExit(f'Bravo should use TMDB name, got {x.get("Name")}')
PY
assert_playback_info "$BRAVO_ID" '/tmp/jf-rig/gostream/movies/' 'existing-gostream-bravo' 1
assert_stream_opens "$BRAVO_ID" 'mkv' 'existing-gostream-bravo'
report_resume_progress_and_assert "$BRAVO_ID" 'existing-gostream-bravo' 12000000000

echo '[5] assert Alpha starts as phantom with native opening source, then auto-open materialises'
python3 - "$ALPHA_ID" <<'PY'
import json,sys
id=sys.argv[1]
j=json.load(open('/tmp/movies.json'))
x=next(i for i in j['Items'] if i['Id']==id)
if 'phantom' not in (x.get('Tags') or []):
    raise SystemExit(f'Alpha should start phantom, tags={x.get("Tags")}')
src=(x.get('MediaSources') or [{}])[0]
if not src.get('RequiresOpening'):
    raise SystemExit(f'Alpha should start with RequiresOpening source, got {src}')
if src.get('Path') not in (None, ''):
    raise SystemExit(f'Alpha should not start on splash, got {src.get("Path")!r}')
PY
assert_opening_playback_info "$ALPHA_ID" 'phantom-alpha'
assert_auto_open_materialises "$ALPHA_ID" '/tmp/jf-rig/gostream/movies/' 'phantom-alpha-auto-open' 1

echo '[6] assert Alpha materialised after native auto-open'
state_count=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM materialised_state WHERE tmdb_id=$ALPHA AND type='movie';")
[ "$state_count" = "1" ] || fail "materialised_state missing for Alpha"

for _ in $(seq 1 20); do
  api "$API/Channels/$MOVIES_CH/Items?Fields=Tags,ProviderIds,MediaSources,Path,Overview,ProductionYear,RunTimeTicks&Limit=50" -o /tmp/movies.json
  if python3 - "$ALPHA_ID" <<'PY'
import json,sys
id=sys.argv[1]
j=json.load(open('/tmp/movies.json'))
x=next((i for i in j.get('Items',[]) if i.get('Id')==id), None)
if not x: raise SystemExit(1)
path=(x.get('MediaSources') or [{}])[0].get('Path') or ''
raise SystemExit(0 if '/tmp/jf-rig/gostream/movies/' in path else 1)
PY
  then break; fi
  sleep 1
done
python3 - "$ALPHA_ID" <<'PY'
import json,sys
id=sys.argv[1]
j=json.load(open('/tmp/movies.json'))
x=next(i for i in j['Items'] if i['Id']==id)
print('ALPHA_POST=', x.get('Name'), x.get('Tags'), [(m.get('Path'),m.get('Container')) for m in x.get('MediaSources',[])])
if 'phantom' in (x.get('Tags') or []):
    raise SystemExit(f'Alpha should no longer be phantom: {x.get("Tags")}')
path=(x.get('MediaSources') or [{}])[0].get('Path') or ''
if '/tmp/jf-rig/gostream/movies/' not in path:
    raise SystemExit(f'Alpha source not refreshed to gostream: {path}')
PY
assert_runtime_ticks /tmp/movies.json "$ALPHA_ID" 1 'materialised-alpha-browse'
assert_playback_info "$ALPHA_ID" '/tmp/jf-rig/gostream/movies/' 'materialised-alpha' 1
assert_stream_opens "$ALPHA_ID" 'mkv' 'materialised-alpha'
report_resume_progress_and_assert "$ALPHA_ID" 'materialised-alpha' 12000000000

echo '[7] DB sanity: channel item persisted with movie external id + gostream path'
row=$(sqlite3 "$JDB" "SELECT ExternalId || '|' || Path || '|' || COALESCE(RunTimeTicks,0) FROM BaseItems WHERE Id=upper(substr('$ALPHA_ID',1,8)||'-'||substr('$ALPHA_ID',9,4)||'-'||substr('$ALPHA_ID',13,4)||'-'||substr('$ALPHA_ID',17,4)||'-'||substr('$ALPHA_ID',21));")
echo "BASEITEM_ALPHA=$row"
case "$row" in
  movie_$ALPHA\|/tmp/jf-rig/gostream/movies/*\|[1-9]*) : ;;
  *) fail "bad Alpha BaseItem row: $row" ;;
esac

echo '[8] source-management reject current source chooses next ranked availability candidate and playback still works'
old_stub=$(sqlite3 "$PHDB" "SELECT stub_path FROM materialised_state WHERE tmdb_id=$ALPHA AND type='movie' AND season=-1 AND episode=-1;")
alt_magnet='magnet:?xt=urn:btih:2222222222222222222222222222222222222222&dn=Phantom+Rig+Alpha+Alternate'
sqlite3 "$PHDB" <<SQL
UPDATE availability_items
SET candidate_magnet='$alt_magnet',
    candidate_info_hash='2222222222222222222222222222222222222222',
    candidate_size=20971520,
    candidate_seeders=55,
    candidate_indexer='rig-alt',
    candidate_source='rig-alt',
    status='available',
    checked_at=$now,
    next_check_at=$((now + 604800))
WHERE tmdb_id=$ALPHA AND type='movie' AND season=-1 AND episode=-1;
INSERT OR REPLACE INTO source_candidates
(tmdb_id,type,season,episode,preset,magnet,info_hash,indexer,title,seeders,size,rank,source,fetched_at,expires_at,validation_status,validation_policy_version)
VALUES
($ALPHA,'movie',-1,-1,'gostream-default','$alt_magnet','2222222222222222222222222222222222222222','rig-alt','Phantom Rig Alpha Alternate',55,20971520,2,'rig-alt',$now,$((now + 604800)),'unknown','unknown');
SQL
api "$API/Plugins/PhantomLibrary/Items/movie_$ALPHA/Sources" -o /tmp/sources-before-reject.json
python3 - <<'PY'
import json
j=json.load(open('/tmp/sources-before-reject.json'))
print('SOURCES_BEFORE=', j.get('Status') or j.get('status'), j.get('CurrentSource') or j.get('currentSource'), j.get('Candidates') or j.get('candidates'))
cands=j.get('Candidates') or j.get('candidates') or []
if not any((c.get('Magnet') or c.get('magnet')) == 'magnet:?xt=urn:btih:2222222222222222222222222222222222222222&dn=Phantom+Rig+Alpha+Alternate' for c in cands):
    raise SystemExit('alternate source candidate not exposed before reject')
PY
api_post "$API/Plugins/PhantomLibrary/Items/movie_$ALPHA/Sources/RejectCurrent" -o /tmp/reject-source.json || fail 'RejectCurrent source API failed'
python3 - <<'PY'
import json
j=json.load(open('/tmp/reject-source.json'))
print('REJECT_RESULT=', j)
code=j.get('Code') or j.get('code')
status=str(j.get('Status') or j.get('status'))
if code != 'materialised' and 'Success' not in status and status != '0':
    raise SystemExit(f'reject did not materialise alternate: code={code!r} status={status!r}')
PY
new_stub=$(sqlite3 "$PHDB" "SELECT stub_path FROM materialised_state WHERE tmdb_id=$ALPHA AND type='movie' AND season=-1 AND episode=-1;")
[ -n "$new_stub" ] || fail 'Alpha missing materialised_state after RejectCurrent'
[ "$new_stub" != "$old_stub" ] || fail "RejectCurrent did not switch stub path (still $new_stub)"
reject_reason=$(sqlite3 "$PHDB" "SELECT reason FROM magnet_failure_cache WHERE tmdb_id=$ALPHA AND type='movie' AND magnet LIKE 'magnet:%1111111111111111111111111111111111111111%' LIMIT 1;")
[ "$reject_reason" = "operator_rejected" ] || fail "expected operator_rejected failure row, got $reject_reason"
api "$API/Channels/$MOVIES_CH/Items?Fields=Tags,ProviderIds,MediaSources,Path,Overview,ProductionYear,RunTimeTicks&Limit=50" -o /tmp/movies.json
assert_playback_info "$ALPHA_ID" '/tmp/jf-rig/gostream/movies/' 'materialised-alpha-after-source-reject' 1
assert_stream_opens "$ALPHA_ID" 'mkv' 'materialised-alpha-after-source-reject'

echo 'CHANNEL_E2E_PLAYBACK_OK'
