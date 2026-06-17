#!/bin/bash
# Full channel architecture end-to-end playback test.
# Starts patched rig, uses TMDB + gostream mocks, then verifies:
# - discovery task warms channel metadata
# - existing gostream movie enriches and plays as real movie_<tmdb>
# - phantom movie PlaybackInfo + stream open succeed
# - manual materialise completes through gostream mock
# - refreshed channel item points at real FUSE path
# - materialised movie PlaybackInfo + stream open succeed
set -euo pipefail

ROOT=/home/spencer/git-repos/spencerharmon/phantom-library
RIG=/tmp/jf-rig
API=http://localhost:18096
TOK=testtoken00000000000000000000000
PHDB=/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db
JDB=/tmp/jf-test/data/data/jellyfin.db
LOG=$RIG/logs/scenario-channel-e2e-playback.log
ALPHA=99000001
BRAVO=99000002

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

assert_playback_info() {
  local id=$1 expect_path_sub=$2 label=$3
  echo "[playback-info] $label id=$id"
  api "$API/Items/$id/PlaybackInfo" -o /tmp/pb.json || fail "$label PlaybackInfo HTTP error"
  python3 - "$expect_path_sub" "$label" <<'PY'
import json,sys
expect=sys.argv[1]
label=sys.argv[2]
j=json.load(open('/tmp/pb.json'))
if j.get('ErrorCode'):
    raise SystemExit(f'{label}: PlaybackInfo ErrorCode={j.get("ErrorCode")}')
sources=j.get('MediaSources') or []
print('  source_count=', len(sources))
if len(sources) != 1:
    raise SystemExit(f'{label}: expected exactly one MediaSource, got {len(sources)}')
s=sources[0]
print('  source=', {k:s.get(k) for k in ['Id','Path','Protocol','Container','SupportsDirectPlay','SupportsDirectStream','SupportsTranscoding']})
if not s.get('Id'):
    raise SystemExit(f'{label}: expected non-empty MediaSource.Id')
path=s.get('Path') or ''
if expect not in path:
    raise SystemExit(f'{label}: expected path containing {expect!r}, got {path!r}')
if s.get('Protocol') != 'File':
    raise SystemExit(f'{label}: expected File protocol')
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

echo '[0] build plugin + start reset rig'
dotnet build -c Release >/tmp/phantom-e2e-build.log
bash tools/rig-scenarios/rig-up.sh --reset

for _ in $(seq 1 60); do
  [ -f "$PHDB" ] && schema=$(sqlite3 "$PHDB" 'PRAGMA user_version;' 2>/dev/null || echo 0) || schema=0
  [ "$schema" = "9" ] && break
  sleep 1
done
[ "${schema:-0}" = "9" ] || fail "phantom schema not v9, got ${schema:-0}"

echo '[1] trigger discovery task'
api "$API/ScheduledTasks" -o /tmp/tasks.json
TASK_ID=$(find_task_id) || fail 'discovery task not found'
api_post "$API/ScheduledTasks/Running/$TASK_ID" -o /tmp/task-run.out || fail 'failed to start discovery task'
wait_task_idle "$TASK_ID"
movies_count=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM discovery_cache WHERE type='movie';")
[ "$movies_count" -ge 3 ] || fail "expected >=3 discovery movies, got $movies_count"

echo '[2] seed magnet cache for Alpha materialise'
now=$(date +%s)
sqlite3 "$PHDB" <<SQL
INSERT OR REPLACE INTO magnet_cache
(tmdb_id, imdb_id, type, season, episode, preset, magnet, info_hash, size, seeders, indexer, cached_at, ttl_seconds, source)
VALUES
($ALPHA, 'tt99000001', 'movie', 0, 0, 'gostream-default',
 'magnet:?xt=urn:btih:1111111111111111111111111111111111111111&dn=Phantom+Rig+Alpha',
 '1111111111111111111111111111111111111111', 10485760, 100, 'rig-cache', $now, 86400, 'rig');
SQL

echo '[3] browse channels'
api "$API/Channels" -o /tmp/channels.json
MOVIES_CH=$(find_movies_channel_id) || fail 'Phantom Movies channel not found'
api "$API/Channels/$MOVIES_CH/Items?Fields=Tags,ProviderIds,MediaSources,Path,Overview,ProductionYear&Limit=50" -o /tmp/movies.json
python3 - <<'PY'
import json
j=json.load(open('/tmp/movies.json'))
for x in j.get('Items', []):
    if (x.get('ProviderIds') or {}).get('Tmdb') in ('99000001','99000002'):
        print('ITEM', x.get('Name'), x.get('Id'), x.get('ProviderIds'), x.get('Tags'), [(m.get('Path'),m.get('Container')) for m in x.get('MediaSources',[])])
PY
ALPHA_ID=$(find_movie_id "$ALPHA") || fail 'Alpha movie not found in channel'
BRAVO_ID=$(find_movie_id "$BRAVO") || fail 'Bravo movie not found in channel'

echo '[4] assert existing gostream Bravo enriches as real playable source'
python3 - "$BRAVO_ID" <<'PY'
import json,sys
id=sys.argv[1]
j=json.load(open('/tmp/movies.json'))
x=next(i for i in j['Items'] if i['Id']==id)
if 'orphan' in (x.get('Tags') or []) or 'phantom' in (x.get('Tags') or []):
    raise SystemExit(f'Bravo should be real gostream item, tags={x.get("Tags")}')
path=(x.get('MediaSources') or [{}])[0].get('Path') or ''
if '/tmp/jf-rig/gostream/movies/' not in path:
    raise SystemExit(f'Bravo path should be gostream mock, got {path}')
if x.get('Name') != 'Phantom Rig Bravo':
    raise SystemExit(f'Bravo should use TMDB name, got {x.get("Name")}')
PY
assert_playback_info "$BRAVO_ID" '/tmp/jf-rig/gostream/movies/' 'existing-gostream-bravo'
assert_stream_opens "$BRAVO_ID" 'mkv' 'existing-gostream-bravo'

echo '[5] assert Alpha starts as phantom and splash plays'
python3 - "$ALPHA_ID" <<'PY'
import json,sys
id=sys.argv[1]
j=json.load(open('/tmp/movies.json'))
x=next(i for i in j['Items'] if i['Id']==id)
if 'phantom' not in (x.get('Tags') or []):
    raise SystemExit(f'Alpha should start phantom, tags={x.get("Tags")}')
path=(x.get('MediaSources') or [{}])[0].get('Path') or ''
if 'splash.mp4' not in path:
    raise SystemExit(f'Alpha should start on splash, got {path}')
PY
assert_playback_info "$ALPHA_ID" 'splash.mp4' 'phantom-alpha'
assert_stream_opens "$ALPHA_ID" 'mp4' 'phantom-alpha'

echo '[6] materialise Alpha end-to-end'
ALPHA_GUID=$(hyphen "$ALPHA_ID")
api_post "$API/Plugins/PhantomLibrary/Materialise/$ALPHA_GUID?trigger=Manual" -o /tmp/materialise.json || fail 'materialise HTTP failed'
python3 - <<'PY'
import json
j=json.load(open('/tmp/materialise.json'))
print('MATERIALISE=', j)
if j.get('Status') not in ('Success','Duplicate') and j.get('status') not in ('Success','Duplicate'):
    raise SystemExit(f'materialise did not succeed: {j}')
fp=j.get('FusePath') or j.get('fusePath') or ''
if '/tmp/jf-rig/gostream/movies/' not in fp:
    raise SystemExit(f'materialise FusePath wrong: {fp}')
PY
state_count=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM materialised_state WHERE tmdb_id=$ALPHA AND type='movie';")
[ "$state_count" = "1" ] || fail "materialised_state missing for Alpha"

for _ in $(seq 1 20); do
  api "$API/Channels/$MOVIES_CH/Items?Fields=Tags,ProviderIds,MediaSources,Path,Overview,ProductionYear&Limit=50" -o /tmp/movies.json
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
assert_playback_info "$ALPHA_ID" '/tmp/jf-rig/gostream/movies/' 'materialised-alpha'
assert_stream_opens "$ALPHA_ID" 'mkv' 'materialised-alpha'

echo '[7] DB sanity: channel item persisted with movie external id + gostream path'
row=$(sqlite3 "$JDB" "SELECT ExternalId || '|' || Path FROM BaseItems WHERE Id=upper(substr('$ALPHA_ID',1,8)||'-'||substr('$ALPHA_ID',9,4)||'-'||substr('$ALPHA_ID',13,4)||'-'||substr('$ALPHA_ID',17,4)||'-'||substr('$ALPHA_ID',21));")
echo "BASEITEM_ALPHA=$row"
case "$row" in
  movie_$ALPHA\|/tmp/jf-rig/gostream/movies/*) : ;;
  *) fail "bad Alpha BaseItem row: $row" ;;
esac

echo 'CHANNEL_E2E_PLAYBACK_OK'
