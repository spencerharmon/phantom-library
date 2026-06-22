#!/bin/bash
# External media parity test.
# Verifies gostream/external movie and TV files coexist in channels, play directly,
# and do not receive Phantom/materialised badges.
set -euo pipefail

ROOT=/home/spencer/git-repos/spencerharmon/phantom-library
RIG=/tmp/jf-rig
API=http://localhost:18096
TOK=testtoken00000000000000000000000
LOG=$RIG/logs/scenario-channel-external-media-parity.log
MOVIE_NAME="Rig External Movie (2026)"
SHOW_NAME="Rig External Show"

mkdir -p "$RIG/logs"
exec > >(tee "$LOG") 2>&1
cd "$ROOT"

fail() { echo "FAIL: $*" >&2; exit 1; }
api() { curl -sS --fail -H "X-Emby-Token: $TOK" "$@"; }
hyphen() { python3 - "$1" <<'PY'
import sys
s=sys.argv[1]
print(f'{s[:8]}-{s[8:12]}-{s[12:16]}-{s[16:20]}-{s[20:]}')
PY
}

channel_id() {
  local name=$1
  python3 - "$name" <<'PY'
import json,sys
name=sys.argv[1]
j=json.load(open('/tmp/channels.json'))
for item in j.get('Items', []):
    if item.get('Name') == name:
        print(item['Id'])
        raise SystemExit(0)
raise SystemExit(1)
PY
}

find_by_name() {
  local file=$1 name=$2
  python3 - "$file" "$name" <<'PY'
import json,sys
file,name=sys.argv[1:]
j=json.load(open(file))
for item in j.get('Items', []):
    if item.get('Name') == name:
        print(item['Id'])
        raise SystemExit(0)
raise SystemExit(1)
PY
}

assert_no_badge_state() {
  local id=$1 label=$2
  python3 - "$id" <<'PY' >/tmp/states-request.json
import json,sys
print(json.dumps({'ids':[sys.argv[1]]}))
PY
  curl -sS --fail -X POST -H "X-Emby-Token: $TOK" -H 'Content-Type: application/json' \
    -d @/tmp/states-request.json "$API/Plugins/PhantomLibrary/States" -o /tmp/states.json
  python3 - "$id" "$label" <<'PY'
import json,sys
id,label=sys.argv[1:]
j=json.load(open('/tmp/states.json'))
print('  badge_state=', j)
if id in j:
    raise SystemExit(f'{label}: external item should not receive Phantom badge state, got {j[id]!r}')
PY
}

assert_direct_playback_info() {
  local id=$1 label=$2
  echo "[playback-info] $label id=$id"
  api "$API/Items/$id/PlaybackInfo" -o /tmp/pb-external.json || fail "$label PlaybackInfo HTTP error"
  python3 - "$label" <<'PY'
import json,sys,uuid
label=sys.argv[1]
j=json.load(open('/tmp/pb-external.json'))
if j.get('ErrorCode'):
    raise SystemExit(f'{label}: PlaybackInfo ErrorCode={j.get("ErrorCode")}')
sources=j.get('MediaSources') or []
print('  sources=', [{k:s.get(k) for k in ['Id','Path','RequiresOpening','Protocol','Container']} for s in sources])
if len(sources) != 1:
    raise SystemExit(f'{label}: expected one source, got {len(sources)}')
s=sources[0]
if s.get('RequiresOpening'):
    raise SystemExit(f'{label}: external media should not require Phantom opening')
if s.get('Protocol') != 'File':
    raise SystemExit(f'{label}: expected File protocol, got {s.get("Protocol")!r}')
if not (s.get('Path') or '').startswith('/tmp/jf-rig/gostream/'):
    raise SystemExit(f'{label}: expected rig gostream path, got {s.get("Path")!r}')
uuid.UUID(s.get('Id'))
PY
}

assert_stream_opens() {
  local id=$1 label=$2
  local gid
  gid=$(hyphen "$id")
  local code bytes
  code=$(curl -sS -L --max-time 20 -H "X-Emby-Token: $TOK" -H 'Range: bytes=0-4095' \
    -o /tmp/stream-external.bin -w '%{http_code}' \
    "$API/Videos/$gid/stream.mp4?static=true" || true)
  bytes=$(wc -c < /tmp/stream-external.bin 2>/dev/null || echo 0)
  echo "  stream $label http=$code bytes=$bytes"
  case "$code" in 200|206) : ;; *) fail "$label stream returned HTTP $code" ;; esac
  [ "$bytes" -gt 0 ] || fail "$label stream returned zero bytes"
}

echo '[0] build plugin + start reset rig'
dotnet build -c Release >/tmp/phantom-external-parity-build.log
bash tools/rig-scenarios/rig-up.sh --reset

for _ in $(seq 1 60); do
  code=$(curl -s --max-time 2 -H "X-Emby-Token: $TOK" -o /dev/null -w '%{http_code}' "$API/System/Info" 2>/dev/null || true)
  [ "$code" = "200" ] && break
  sleep 1
done
[ "${code:-000}" = "200" ] || fail "rig API not ready"

echo '[1] create external movie + TV files'
mkdir -p "$RIG/gostream/movies" "$RIG/gostream/tv/Rig_External_Show (2026)/Season.01"
cp src/Jellyfin.Plugin.PhantomLibrary/Assets/splash.mp4 "$RIG/gostream/movies/Rig External Movie (2026).mp4"
cp src/Jellyfin.Plugin.PhantomLibrary/Assets/splash.mp4 "$RIG/gostream/tv/Rig_External_Show (2026)/Season.01/Rig_External_Show_S01E01_deadbeef.mp4"

echo '[2] browse channels'
api "$API/Channels" -o /tmp/channels.json
MOVIES_ID=$(channel_id 'Phantom Movies') || fail 'Phantom Movies channel missing'
SHOWS_ID=$(channel_id 'Phantom Shows') || fail 'Phantom Shows channel missing'
api "$API/Channels/$MOVIES_ID/Items?Limit=200" -o /tmp/movies-external.json
api "$API/Channels/$SHOWS_ID/Items?Limit=200" -o /tmp/shows-external.json
MOVIE_ID=$(find_by_name /tmp/movies-external.json "$MOVIE_NAME") || fail 'external movie missing'
SERIES_ID=$(find_by_name /tmp/shows-external.json "$SHOW_NAME") || fail 'external TV series missing'
echo "  movie_id=$MOVIE_ID series_id=$SERIES_ID"

api "$API/Channels/$SHOWS_ID/Items?FolderId=$SERIES_ID&Limit=50" -o /tmp/show-seasons-external.json
SEASON_ID=$(find_by_name /tmp/show-seasons-external.json 'Season 1') || fail 'external TV season missing'
api "$API/Channels/$SHOWS_ID/Items?FolderId=$SEASON_ID&Limit=50" -o /tmp/show-episodes-external.json
EPISODE_ID=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/show-episodes-external.json'))
items=j.get('Items', [])
if len(items) != 1:
    raise SystemExit(1)
print(items[0]['Id'])
PY
) || fail 'external TV episode missing'
echo "  season_id=$SEASON_ID episode_id=$EPISODE_ID"

assert_no_badge_state "$MOVIE_ID" 'external movie'
assert_no_badge_state "$EPISODE_ID" 'external TV episode'
assert_direct_playback_info "$MOVIE_ID" 'external movie'
assert_direct_playback_info "$EPISODE_ID" 'external TV episode'
assert_stream_opens "$MOVIE_ID" 'external movie'
assert_stream_opens "$EPISODE_ID" 'external TV episode'

echo '[OK] external media parity passed'
