#!/bin/bash
# TV episode channel architecture end-to-end playback test.
# Verifies:
# - series tiles are navigation-only and do not receive badge state
# - episode rows carry Phantom badge state
# - episode PlaybackInfo exposes native RequiresOpening source, not splash
# - AutoOpenLiveStream materialises episode via gostream mock and returns real file
# - materialised episode stream opens
set -euo pipefail

ROOT=${PHANTOM_REPO_ROOT:-/home/spencer/git-repos/spencerharmon/phantom-library}
RIG=/tmp/jf-rig
API=http://localhost:18096
TOK=testtoken00000000000000000000000
PHDB=/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db
JDB=/tmp/jf-test/data/data/jellyfin.db
LOG=$RIG/logs/scenario-channel-episode-e2e-playback.log
SERIES=99100001
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

find_item_id() {
  local file=$1 external=$2
  python3 - "$file" "$external" <<'PY'
import json,sys
file,external=sys.argv[1],sys.argv[2]
j=json.load(open(file))
for x in j.get('Items', []):
    if x.get('ExternalId') == external or x.get('Id') == external or x.get('Name') == external:
        print(x['Id'])
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
if not (s.get('OpenToken') or '').endswith('_phantom:episode_99100001_s01e01'):
    raise SystemExit(f'{label}: expected Phantom episode open token, got {s.get("OpenToken")!r}')
if s.get('Path') not in (None, ''):
    raise SystemExit(f'{label}: expected no splash path, got {s.get("Path")!r}')
uuid.UUID(s.get('Id'))
PY
}

assert_auto_open_materialises() {
  local id=$1 label=$2
  local gid
  gid=$(hyphen "$id")
  echo "[auto-open-playback-info] $label guid=$gid"
  curl -sS --fail -X POST -H "X-Emby-Token: $TOK" -H 'Content-Type: application/json' \
    -d '{"AutoOpenLiveStream":true}' \
    "$API/Items/$gid/PlaybackInfo?AutoOpenLiveStream=true" -o /tmp/pb-auto-open.json \
    || fail "$label auto-open PlaybackInfo HTTP error"
  cp /tmp/pb-auto-open.json /tmp/pb.json
  python3 - "$label" <<'PY'
import json,sys,uuid
label=sys.argv[1]
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
if '/tmp/jf-rig/gostream/tv/' not in path:
    raise SystemExit(f'{label}: expected tv gostream path, got {path!r}')
if s.get('Protocol') != 'File':
    raise SystemExit(f'{label}: expected File protocol')
if len(s.get('MediaStreams') or []) < 1:
    raise SystemExit(f'{label}: expected probed MediaStreams')
uuid.UUID(s.get('Id'))
PY
}

assert_materialised_playback_info() {
  local id=$1 label=$2
  echo "[materialised-playback-info] $label id=$id"
  api "$API/Items/$id/PlaybackInfo" -o /tmp/pb-materialised.json || fail "$label materialised PlaybackInfo HTTP error"
  python3 - "$label" <<'PY'
import json,sys,uuid
label=sys.argv[1]
j=json.load(open('/tmp/pb-materialised.json'))
if j.get('ErrorCode'):
    raise SystemExit(f'{label}: PlaybackInfo ErrorCode={j.get("ErrorCode")}')
sources=j.get('MediaSources') or []
print('  source_count=', len(sources))
if len(sources) != 1:
    raise SystemExit(f'{label}: expected exactly one MediaSource, got {len(sources)}')
s=sources[0]
print('  source=', {k:s.get(k) for k in ['Id','Path','RequiresOpening','OpenToken','Protocol','Container']})
if s.get('RequiresOpening'):
    raise SystemExit(f'{label}: second PlaybackInfo should use materialised real source')
path=s.get('Path') or ''
if '/tmp/jf-rig/gostream/tv/' not in path:
    raise SystemExit(f'{label}: expected tv gostream path, got {path!r}')
if s.get('Protocol') != 'File':
    raise SystemExit(f'{label}: expected File protocol')
uuid.UUID(s.get('Id'))
PY
}

echo '[0] build plugin + start reset rig'
read -r -a BUILD_ARGS <<< "${PHANTOM_DOTNET_BUILD_ARGS:-}"
dotnet build -c Release "${BUILD_ARGS[@]}" >/tmp/phantom-episode-e2e-build.log
bash tools/rig-scenarios/rig-up.sh --reset

for _ in $(seq 1 60); do
  [ -f "$PHDB" ] && schema=$(sqlite3 "$PHDB" 'PRAGMA user_version;' 2>/dev/null || echo 0) || schema=0
  [ "$schema" = "11" ] && break
  sleep 1
done
[ "${schema:-0}" = "11" ] || fail "phantom schema not v11, got ${schema:-0}"

echo '[1] trigger discovery task'
api "$API/ScheduledTasks" -o /tmp/tasks.json
TASK_ID=$(find_task_id) || fail 'discovery task not found'
api_post "$API/ScheduledTasks/Running/$TASK_ID" -o /tmp/task-run.out || fail 'failed to start discovery task'
wait_task_idle "$TASK_ID"
series_count=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM catalogue_items WHERE type='series';")
[ "$series_count" -ge 2 ] || fail "expected >=2 catalogue series, got $series_count"

echo '[2] seed magnet cache for episode'
now=$(date +%s)
sqlite3 "$PHDB" <<SQL
INSERT OR REPLACE INTO magnet_cache
(tmdb_id, imdb_id, type, season, episode, preset, magnet, info_hash, size, seeders, indexer, cached_at, ttl_seconds, source)
VALUES
($SERIES, 'tt99100001', 'episode', $SEASON, $EPISODE, 'gostream-default',
 'magnet:?xt=urn:btih:2222222222222222222222222222222222222222&dn=Phantom+Rig+Delta+S01E01',
 '2222222222222222222222222222222222222222', 10485760, 100, 'rig-cache', $now, 86400, 'rig');
INSERT OR REPLACE INTO availability_items
(tmdb_id, type, season, episode, status, checked_at, next_check_at, candidate_magnet, candidate_info_hash, candidate_size, candidate_seeders, candidate_indexer, candidate_source)
VALUES
($SERIES, 'episode', $SEASON, $EPISODE, 'available', $now, $((now + 604800)),
 'magnet:?xt=urn:btih:2222222222222222222222222222222222222222&dn=Phantom+Rig+Delta+S01E01',
 '2222222222222222222222222222222222222222', 10485760, 100, 'rig-cache', 'rig');
INSERT OR REPLACE INTO plugin_meta(key,value) VALUES('channel_dataversion_shows', '$now-rig-seed');
SQL

echo '[3] browse shows channel series -> season -> episodes'
api "$API/Channels" -o /tmp/channels.json
SHOWS_CH=$(find_shows_channel_id) || fail 'Phantom Shows channel not found'
api "$API/Channels/$SHOWS_CH/Items?Fields=Tags,ProviderIds,MediaSources,Path,Overview,ProductionYear,ExternalId&Limit=50" -o /tmp/series.json
python3 - <<'PY'
import json
j=json.load(open('/tmp/series.json'))
for x in j.get('Items', []):
    if (x.get('ProviderIds') or {}).get('Tmdb') == '99100001' or x.get('ExternalId') == 'series_99100001':
        print('SERIES_ITEM', x.get('Name'), x.get('Id'), x.get('ExternalId'), x.get('Tags'), x.get('MediaSources'))
PY
SERIES_ID=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/series.json'))
for x in j.get('Items', []):
    if (x.get('ProviderIds') or {}).get('Tmdb') == '99100001' or x.get('ExternalId') == 'series_99100001':
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'Delta series not found'

api "$API/Channels/$SHOWS_CH/Items?FolderId=$SERIES_ID&Fields=Tags,ProviderIds,MediaSources,Path,Overview,ExternalId,ProductionYear&Limit=50" -o /tmp/seasons.json
SEASON_ID=$(python3 - <<'PY'
import json, sys
j=json.load(open('/tmp/seasons.json'))
for x in j.get('Items', []):
    if x.get('Name') == 'Season 1':
        if x.get('Type') != 'Season':
            raise SystemExit('season item did not use native Season type: ' + str(x.get('Type')))
        overview = x.get('Overview') or ''
        if 'Season 1 overview for Phantom Rig Delta.' not in overview:
            raise SystemExit('season overview missing')
        if '8 episodes' not in overview or '1 available/materialised' not in overview or '7 unknown' not in overview:
            raise SystemExit('season availability summary missing: ' + overview)
        if x.get('ProductionYear') != 2024:
            raise SystemExit('season production year missing')
        print('SEASON_ITEM', x.get('Name'), x.get('Id'), overview.replace('\n', ' | '), x.get('ProductionYear'), file=sys.stderr)
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'Delta season 1 not found or not enriched'
api "$API/Channels/$SHOWS_CH/Items?FolderId=$SEASON_ID&Fields=Tags,ProviderIds,MediaSources,Path,Overview,ExternalId&Limit=50" -o /tmp/episodes.json
EP_ID=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/episodes.json'))
for x in j.get('Items', []):
    if x.get('Name') == 'Phantom Rig Delta Episode 1':
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'Delta S01E01 episode not found'
api "$API/Users/$(sqlite3 $JDB 'select Id from Users limit 1')/Items?ParentId=$SEASON_ID&Fields=Tags,IndexNumber,ParentIndexNumber,Overview&Limit=20" -o /tmp/season-parent-children.json
python3 - <<'PY'
import json
j=json.load(open('/tmp/season-parent-children.json'))
if j.get('TotalRecordCount') != 8 or len(j.get('Items', [])) != 8:
    raise SystemExit('native season child list not prehydrated')
print('SEASON_CHILDREN', j.get('TotalRecordCount'))
PY

python3 - "$EP_ID" <<'PY'
import json,sys
id=sys.argv[1]
j=json.load(open('/tmp/episodes.json'))
x=next(i for i in j['Items'] if i['Id']==id)
print('EPISODE_ITEM', x.get('Name'), x.get('Id'), x.get('ExternalId'), x.get('Tags'), x.get('MediaSources'))
if 'phantom' not in (x.get('Tags') or []):
    raise SystemExit(f'episode should start phantom, tags={x.get("Tags")}')
src=(x.get('MediaSources') or [{}])[0]
if not src.get('RequiresOpening'):
    raise SystemExit(f'episode should start with RequiresOpening source, got {src}')
if src.get('Path') not in (None, ''):
    raise SystemExit(f'episode should not start on splash, got {src.get("Path")!r}')
PY

echo '[4] badge state: series omitted, episode Phantom'
python3 - "$SERIES_ID" "$EP_ID" > /tmp/badge-request.json <<'PY'
import json,sys
print(json.dumps({'ids':[sys.argv[1], sys.argv[2]]}))
PY
curl -sS --fail -X POST -H "X-Emby-Token: $TOK" -H 'Content-Type: application/json' \
  --data-binary @/tmp/badge-request.json "$API/Plugins/PhantomLibrary/States" -o /tmp/badge-states.json
python3 - "$SERIES_ID" "$EP_ID" <<'PY'
import json,sys
series,ep=sys.argv[1],sys.argv[2]
j=json.load(open('/tmp/badge-states.json'))
print('BADGE_STATES=', j)
if series in j:
    raise SystemExit(f'series should be omitted from badge state, got {j.get(series)}')
if j.get(ep) != 'Phantom':
    raise SystemExit(f'episode should be Phantom badge state, got {j.get(ep)}')
PY

echo '[5] episode native-open materialises and plays'
assert_opening_playback_info "$EP_ID" 'phantom-episode'
assert_auto_open_materialises "$EP_ID" 'phantom-episode-auto-open'
state_count=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM materialised_state WHERE tmdb_id=$SERIES AND type='episode' AND season=$SEASON AND episode=$EPISODE;")
[ "$state_count" = "1" ] || fail 'materialised_state missing for episode'

for _ in $(seq 1 20); do
  api "$API/Channels/$SHOWS_CH/Items?FolderId=$SEASON_ID&Fields=Tags,ProviderIds,MediaSources,Path,Overview,ExternalId&Limit=50" -o /tmp/episodes.json
  if python3 - "$EP_ID" <<'PY'
import json,sys
id=sys.argv[1]
j=json.load(open('/tmp/episodes.json'))
x=next((i for i in j.get('Items',[]) if i.get('Id')==id), None)
if not x: raise SystemExit(1)
path=(x.get('MediaSources') or [{}])[0].get('Path') or ''
raise SystemExit(0 if '/tmp/jf-rig/gostream/tv/' in path else 1)
PY
  then break; fi
  sleep 1
done
python3 - "$EP_ID" <<'PY'
import json,sys
id=sys.argv[1]
j=json.load(open('/tmp/episodes.json'))
x=next(i for i in j['Items'] if i['Id']==id)
print('EPISODE_POST=', x.get('Name'), x.get('Tags'), [(m.get('Path'),m.get('Container')) for m in x.get('MediaSources',[])])
if 'phantom' in (x.get('Tags') or []):
    raise SystemExit(f'episode should no longer be phantom: {x.get("Tags")}')
path=(x.get('MediaSources') or [{}])[0].get('Path') or ''
if '/tmp/jf-rig/gostream/tv/' not in path:
    raise SystemExit(f'episode source not refreshed to gostream tv path: {path}')
PY
assert_stream_opens "$EP_ID" 'mkv' 'materialised-episode'
assert_materialised_playback_info "$EP_ID" 'materialised-episode-second-play'
assert_stream_opens "$EP_ID" 'mkv' 'materialised-episode-second-play'

echo '[6] DB sanity: episode BaseItem persisted with episode external id + tv path'
row=$(sqlite3 "$JDB" "SELECT ExternalId || '|' || Path FROM BaseItems WHERE Id=upper(substr('$EP_ID',1,8)||'-'||substr('$EP_ID',9,4)||'-'||substr('$EP_ID',13,4)||'-'||substr('$EP_ID',17,4)||'-'||substr('$EP_ID',21));")
echo "BASEITEM_EPISODE=$row"
case "$row" in
  episode_${SERIES}_s01e01\|/tmp/jf-rig/gostream/tv/*) : ;;
  *) fail "bad episode BaseItem row: $row" ;;
esac

echo 'CHANNEL_EPISODE_E2E_PLAYBACK_OK'
