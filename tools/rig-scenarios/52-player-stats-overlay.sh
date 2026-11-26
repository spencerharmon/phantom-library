#!/bin/bash
# 52-player-stats-overlay.sh — p14-player-stats-overlay live-rig proof (ROI P14).
#
# Plays a phantom MOVIE (Phantom Rig Alpha, tmdb 99000001) AND a phantom EPISODE
# (Phantom Rig Delta S01E01, tmdb 99100001) on the reset rig (:18096, gostream
# mock :19080) and asserts, for each:
#   1. native-open AutoOpenLiveStream materialises it (35/36 parity);
#   2. a REAL Sessions/Playing report makes
#      GET /Plugins/PhantomLibrary/PlayerStats/Current (the overlay's endpoint,
#      user token) return status=ok, playing, isPhantom, the materialised
#      info-hash, source release/size/indexer, and gostream's live per-torrent
#      stats proxied from the mock's /api/library/stats (exact values);
#   3. the per-item form GET /Plugins/PhantomLibrary/PlayerStats/{itemId} works
#      for the playing user and is 403 for another user who is not playing it;
#   4. the payload carries no gostream token / URL / stub or FUSE path;
#   5. the REAL overlay script (served by the plugin, bootstrapped by badges.js)
#      runs against the live endpoint (phantom-player-stats-overlay-dom.mjs
#      --live): >=2 polls at a 2–5 s cadence, then Sessions/Playing/Stopped →
#      the overlay stops polling and the gostream mock receives ZERO further
#      stats requests;
#   6. after stop, /PlayerStats/Current reports not_playing.
# Plus: /Plugins/PhantomLibrary/playerstats.js is served as JavaScript and
# badges.js (already injected into jellyfin-web's index.html) bootstraps it;
# when the rig's web dir carries index.html, the badges/playerstats tags are
# asserted present (the fork image / install.sh inject them).
#
# Refuses the production port :8096. Tears the rig down on exit.
set -euo pipefail

ROOT=${PHANTOM_REPO_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
RIG=/tmp/jf-rig
API=http://localhost:18096
TOK=testtoken00000000000000000000000
PHDB=/var/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db
WEB_INDEX=${PHANTOM_RIG_WEB_INDEX:-/usr/share/jellyfin/web/index.html}
STATS_LOG=$RIG/logs/gostream-mock-stats.jsonl
LOG=$RIG/logs/scenario-player-stats-overlay.log
DEVICE=phantom-rig-stats-device
ALPHA=99000001
SERIES=99100001
SEASON=1
EPISODE=1
ALPHA_HASH=1111111111111111111111111111111111111111
EP_HASH=2222222222222222222222222222222222222222
USER_ID=
USER_AUTH_TOKEN=

if [[ "$API" == *:8096* ]]; then
  echo "REFUSING to run against production port :8096" >&2
  exit 1
fi

mkdir -p "$RIG/logs"
exec > >(tee "$LOG") 2>&1
cd "$ROOT"

cleanup() {
  local rc=$?
  if [ "${RIG_NO_RESET:-0}" != "1" ]; then
    bash tools/rig-scenarios/rig-down.sh >/dev/null 2>&1 || true
    systemctl --user stop rig-gostream-mock.service >/dev/null 2>&1 || true
  fi
  exit "$rc"
}
trap cleanup EXIT INT TERM

fail() { echo "FAIL: $*" >&2; exit 1; }
api() { curl -sS --fail -H "X-Emby-Token: $TOK" "$@"; }
api_post() { curl -sS --fail -X POST -H "X-Emby-Token: $TOK" "$@"; }
user_auth_header() { printf 'MediaBrowser Client="phantom-rig", Device="phantom-rig", DeviceId="%s", Version="1", Token="%s"' "$DEVICE" "$USER_AUTH_TOKEN"; }
user_api() { curl -sS --fail -H "X-Emby-Token: $USER_AUTH_TOKEN" -H "X-Emby-Authorization: $(user_auth_header)" "$@"; }
user_json_post() { curl -sS --fail -X POST -H "X-Emby-Token: $USER_AUTH_TOKEN" -H "X-Emby-Authorization: $(user_auth_header)" -H 'Content-Type: application/json' "$@"; }
hyphen() { python3 - "$1" <<'PY'
import sys
s=sys.argv[1].replace('-','')
print(f'{s[:8]}-{s[8:12]}-{s[12:16]}-{s[16:20]}-{s[20:]}')
PY
}
stats_requests() { [ -f "$STATS_LOG" ] && wc -l < "$STATS_LOG" | tr -d ' ' || echo 0; }

echo '[0] build plugin + start reset rig'
if [ "${RIG_NO_RESET:-0}" = "1" ]; then
  echo '  RIG_NO_RESET=1: reusing the already-running rig'
  [ -f "$PHDB" ] || fail "RIG_NO_RESET=1 but no phantom.db at $PHDB"
else
  read -r -a BUILD_ARGS <<< "${PHANTOM_DOTNET_BUILD_ARGS:-}"
  dotnet build -c Release "${BUILD_ARGS[@]}" >/tmp/phantom-player-stats-build.log
  bash tools/rig-scenarios/rig-up.sh --reset
  for _ in $(seq 1 60); do
    [ -f "$PHDB" ] && schema=$(sqlite3 "$PHDB" 'PRAGMA user_version;' 2>/dev/null || echo 0) || schema=0
    [ "${schema:-0}" -ge 14 ] && break
    sleep 1
  done
  [ "${schema:-0}" -ge 14 ] || fail "phantom schema not ready, got ${schema:-0}"
fi

echo '[1] overlay script served + injected'
code=$(curl -sS -o /tmp/pps-script.js -w '%{http_code}|%{content_type}' "$API/Plugins/PhantomLibrary/playerstats.js")
case "$code" in 200\|text/javascript*) : ;; *) fail "playerstats.js not served as JS: $code" ;; esac
grep -q 'Plugins/PhantomLibrary/PlayerStats/Current' /tmp/pps-script.js || fail 'served playerstats.js is not the overlay shim'
curl -sS --fail "$API/Plugins/PhantomLibrary/badges.js" -o /tmp/pps-badges.js || fail 'badges.js not served'
grep -q '/Plugins/PhantomLibrary/playerstats.js' /tmp/pps-badges.js || fail 'badges.js does not bootstrap playerstats.js'
if [ -f "$WEB_INDEX" ]; then
  if grep -q 'Plugins/PhantomLibrary/playerstats.js' "$WEB_INDEX"; then
    echo "  index.html carries the playerstats.js tag"
  elif grep -q 'Plugins/PhantomLibrary/badges.js' "$WEB_INDEX"; then
    echo "  index.html carries badges.js (which bootstraps playerstats.js)"
  else
    fail "rig web index.html ($WEB_INDEX) injects neither badges.js nor playerstats.js"
  fi
else
  echo "  NOTE: $WEB_INDEX absent on this rig host; injection asserted via the served badges.js bootstrap"
fi

curl -sS --fail -X POST -H 'Content-Type: application/json' \
  -H "X-Emby-Authorization: MediaBrowser Client=\"phantom-rig\", Device=\"phantom-rig\", DeviceId=\"$DEVICE\", Version=\"1\"" \
  -d '{"Username":"a","Pw":"a"}' "$API/Users/AuthenticateByName" -o /tmp/pps-auth.json || fail 'test user login failed'
USER_ID=$(python3 -c "import json;print((json.load(open('/tmp/pps-auth.json')).get('User') or {}).get('Id') or '')")
USER_AUTH_TOKEN=$(python3 -c "import json;print(json.load(open('/tmp/pps-auth.json')).get('AccessToken') or '')")
[ -n "$USER_ID" ] && [ -n "$USER_AUTH_TOKEN" ] || fail 'test user id/token missing'

echo '[2] not playing yet → not_playing'
user_api "$API/Plugins/PhantomLibrary/PlayerStats/Current" -o /tmp/pps-current.json || fail 'PlayerStats/Current HTTP error'
python3 -c "
import json; j=json.load(open('/tmp/pps-current.json'))
assert j.get('status')=='not_playing' and j.get('playing') is False, j
" || fail 'expected not_playing before playback'

echo '[3] discovery + seed movie and episode sources'
api "$API/ScheduledTasks" -o /tmp/pps-tasks.json
TASK_ID=$(python3 - <<'PY'
import json
for t in json.load(open('/tmp/pps-tasks.json')):
    if t.get('Key') == 'PhantomLibrary.DiscoveryRefresh' or t.get('Name') == 'Phantom Library — Refresh Discovery':
        print(t['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'discovery task not found'
api_post "$API/ScheduledTasks/Running/$TASK_ID" -o /dev/null || fail 'failed to start discovery task'
for _ in $(seq 1 120); do
  api "$API/ScheduledTasks" -o /tmp/pps-tasks.json
  state=$(python3 -c "
import json,sys
print(next((t.get('State') for t in json.load(open('/tmp/pps-tasks.json')) if t.get('Id')=='$TASK_ID'), ''))")
  [ "$state" = "Idle" ] && break
  sleep 1
done
[ "$state" = "Idle" ] || fail 'discovery task never went idle'

now=$(date +%s)
sqlite3 "$PHDB" <<SQL
INSERT OR REPLACE INTO magnet_cache
(tmdb_id, imdb_id, type, season, episode, preset, magnet, info_hash, size, seeders, indexer, cached_at, ttl_seconds, source)
VALUES
($ALPHA, 'tt99000001', 'movie', 0, 0, 'gostream-default',
 'magnet:?xt=urn:btih:$ALPHA_HASH&dn=Phantom+Rig+Alpha', '$ALPHA_HASH', 10485760, 100, 'rig-cache', $now, 86400, 'rig'),
($SERIES, 'tt99100001', 'episode', $SEASON, $EPISODE, 'gostream-default',
 'magnet:?xt=urn:btih:$EP_HASH&dn=Phantom+Rig+Delta+S01E01', '$EP_HASH', 10485760, 100, 'rig-cache', $now, 86400, 'rig');
INSERT OR REPLACE INTO source_candidates
(tmdb_id,type,season,episode,preset,magnet,info_hash,indexer,title,seeders,size,rank,source,fetched_at,expires_at)
VALUES
($ALPHA,'movie',-1,-1,'gostream-default','magnet:?xt=urn:btih:$ALPHA_HASH&dn=Phantom+Rig+Alpha','$ALPHA_HASH','rig-cache',
 'Phantom.Rig.Alpha.2024.1080p.WEB-DL.x264-RIG',100,10485760,1,'rig',$now,$((now + 86400))),
($SERIES,'episode',$SEASON,$EPISODE,'gostream-default','magnet:?xt=urn:btih:$EP_HASH&dn=Phantom+Rig+Delta+S01E01','$EP_HASH','rig-cache',
 'Phantom.Rig.Delta.S01E01.1080p.WEB-DL.x264-RIG',100,10485760,1,'rig',$now,$((now + 86400)));
INSERT OR REPLACE INTO availability_items
(tmdb_id, type, season, episode, status, checked_at, next_check_at, candidate_magnet, candidate_info_hash, candidate_size, candidate_seeders, candidate_indexer, candidate_source)
VALUES
($ALPHA, 'movie', -1, -1, 'available', $now, $((now + 604800)),
 'magnet:?xt=urn:btih:$ALPHA_HASH&dn=Phantom+Rig+Alpha', '$ALPHA_HASH', 10485760, 100, 'rig-cache', 'rig'),
($SERIES, 'episode', $SEASON, $EPISODE, 'available', $now, $((now + 604800)),
 'magnet:?xt=urn:btih:$EP_HASH&dn=Phantom+Rig+Delta+S01E01', '$EP_HASH', 10485760, 100, 'rig-cache', 'rig');
INSERT OR REPLACE INTO plugin_meta(key,value) VALUES('channel_dataversion_movies', '$now-rig-seed-pps');
INSERT OR REPLACE INTO plugin_meta(key,value) VALUES('channel_dataversion_shows', '$now-rig-seed-pps');
SQL

echo '[4] locate movie + episode in Phantom Movies / Phantom Shows'
api "$API/Channels" -o /tmp/pps-channels.json
chan() { python3 -c "
import json,sys
j=json.load(open('/tmp/pps-channels.json'))
for x in j.get('Items', j if isinstance(j,list) else []):
    if x.get('Name')=='$1': print(x['Id']); raise SystemExit(0)
raise SystemExit(1)"; }
MOVIES_CH=$(chan 'Phantom Movies') || fail 'Phantom Movies channel not found'
SHOWS_CH=$(chan 'Phantom Shows') || fail 'Phantom Shows channel not found'
pick() { python3 -c "
import json,sys
j=json.load(open('$1'))
for x in j.get('Items', []):
    if $2: print(x['Id']); raise SystemExit(0)
raise SystemExit(1)"; }
api "$API/Channels/$MOVIES_CH/Items?Fields=ProviderIds,ExternalId&Limit=50" -o /tmp/pps-movies.json
ALPHA_ID=$(pick /tmp/pps-movies.json "(x.get('ProviderIds') or {}).get('Tmdb')=='$ALPHA'") || fail 'Alpha not in Phantom Movies'
api "$API/Channels/$SHOWS_CH/Items?Fields=ProviderIds,ExternalId&Limit=50" -o /tmp/pps-series.json
SERIES_ID=$(pick /tmp/pps-series.json "(x.get('ProviderIds') or {}).get('Tmdb')=='$SERIES' or x.get('ExternalId')=='series_$SERIES'") || fail 'Delta not in Phantom Shows'
api "$API/Channels/$SHOWS_CH/Items?FolderId=$SERIES_ID&Limit=50" -o /tmp/pps-seasons.json
SEASON_ID=$(pick /tmp/pps-seasons.json "x.get('Name')=='Season $SEASON'") || fail 'Delta season not found'
api "$API/Channels/$SHOWS_CH/Items?FolderId=$SEASON_ID&Fields=ExternalId&Limit=50" -o /tmp/pps-episodes.json
EP_ID=$(pick /tmp/pps-episodes.json "x.get('Name')=='Phantom Rig Delta Episode $EPISODE'") || fail 'Delta S01E01 not found'
echo "  movie=$ALPHA_ID episode=$EP_ID"

play_and_assert() {
  local id=$1 label=$2 hash=$3 type=$4 release=$5
  local gid; gid=$(hyphen "$id")
  echo "[5:$label] native-open materialise"
  curl -sS --fail -X POST -H "X-Emby-Token: $TOK" -H 'Content-Type: application/json' -d '{"AutoOpenLiveStream":true}' \
    "$API/Items/$gid/PlaybackInfo?AutoOpenLiveStream=true" -o /tmp/pps-open.json || fail "$label auto-open PlaybackInfo failed"
  python3 -c "
import json; j=json.load(open('/tmp/pps-open.json'))
assert not j.get('ErrorCode'), j.get('ErrorCode')
s=(j.get('MediaSources') or [{}])[0]
assert not s.get('RequiresOpening') and s.get('Protocol')=='File', s
" || fail "$label did not materialise to a real file source"
  local where
  if [ "$type" = movie ]; then
    where="tmdb_id=$ALPHA AND type='movie' AND season=-1 AND episode=-1"
  else
    where="tmdb_id=$SERIES AND type='episode' AND season=$SEASON AND episode=$EPISODE"
  fi
  [ "$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM materialised_state WHERE $where;")" = 1 ] || fail "$label materialised_state missing"
  local source_id; source_id=$(python3 -c "import json;print(((json.load(open('/tmp/pps-open.json')).get('MediaSources') or [{}])[0]).get('Id') or '')")

  echo "[6:$label] report Sessions/Playing (as jellyfin-web does when the player opens)"
  python3 -c "
import json
print(json.dumps({'ItemId':'$gid','MediaSourceId':'$source_id','PlaySessionId':'pps-$label','PositionTicks':0,'CanSeek':True,'IsPaused':False,'PlayMethod':'DirectPlay'}))" > /tmp/pps-playing.json
  user_json_post --data-binary @/tmp/pps-playing.json "$API/Sessions/Playing" -o /dev/null || fail "$label Sessions/Playing failed"
  user_json_post --data-binary @/tmp/pps-playing.json "$API/Sessions/Playing/Progress" -o /dev/null || fail "$label progress failed"

  echo "[7:$label] GET PlayerStats/Current (overlay endpoint, user token)"
  local ok=0
  for _ in $(seq 1 20); do
    user_api "$API/Plugins/PhantomLibrary/PlayerStats/Current" -o /tmp/pps-current.json || fail "$label Current HTTP error"
    if python3 -c "import json,sys; sys.exit(0 if json.load(open('/tmp/pps-current.json')).get('status')=='ok' else 1)"; then ok=1; break; fi
    sleep 1
  done
  cat /tmp/pps-current.json; echo
  [ "$ok" = 1 ] || fail "$label PlayerStats/Current never reached status=ok"
  python3 - "$id" "$hash" "$type" "$release" "$TOK" <<'PY' || fail "$label stats payload assertion failed"
import json,sys
item,h,typ,release,admin_tok=sys.argv[1:]
raw=open('/tmp/pps-current.json').read()
j=json.loads(raw)
assert j['playing'] is True and j['isPhantom'] is True, j
assert j['itemId']==item.replace('-','').lower(), (j['itemId'], item)
assert j['type']==typ, j['type']
if typ=='episode':
    assert (j['season'], j['episode'])==(1,1), (j['season'], j['episode'])
else:
    assert j['season'] is None and j['episode'] is None
src=j['source']
assert src['infoHash']==h, src
assert src['release']==release and src['releaseSource']=='candidate', src
assert src['sizeBytes']==10485760 and src['indexer']=='rig-cache', src
st=j['stream']
seed=int(h[:6],16)
# exact values the gostream mock computes for this hash (proves real proxying)
assert st['peers']==12+seed%5 and st['seeders']==7+seed%3, st
assert st['downloadRateBytesPerSec']==1048576 and st['torrentSize']==10485760, st
assert st['bufferedAheadBytes']==10485760-4096 and st['readOffset']==4096, st
assert 'bufferedAheadSeconds' in st and st['bufferedAheadSeconds'] is None, 'explicit null must be preserved'
assert st['state']=='streaming', st
assert 2000<=j['pollIntervalMs']<=5000, j['pollIntervalMs']
low=raw.lower()
for bad in ['token','19080','127.0.0.1','/tmp/jf-rig','stub','fuse',admin_tok]:
    assert bad.lower() not in low, f'payload leaks {bad!r}'
print(f'  OK {typ}: hash={h} peers={st["peers"]} seeders={st["seeders"]} state={st["state"]}')
PY
  tail -n 1 "$STATS_LOG" | python3 -c "
import json,sys; e=json.loads(sys.stdin.read())
assert e['hash']=='$hash' and e['status']==200, e
" || fail "$label gostream mock did not serve the proxied stats request"

  echo "[8:$label] per-item endpoint: owner 200, other user 403"
  user_api "$API/Plugins/PhantomLibrary/PlayerStats/$id?playSessionId=pps-$label" -o /tmp/pps-item.json || fail "$label per-item HTTP error"
  python3 -c "
import json; j=json.load(open('/tmp/pps-item.json'))
assert j['status']=='ok' and j['playing'] and j['playSessionId']=='pps-$label' and j['source']['infoHash']=='$hash', j
" || fail "$label per-item payload wrong"
  local other; other=$(curl -sS -o /dev/null -w '%{http_code}' -H "X-Emby-Token: $OTHER_TOKEN" "$API/Plugins/PhantomLibrary/PlayerStats/$id")
  [ "$other" = 403 ] || fail "$label per-item for a non-playing user returned $other, expected 403"

  echo "[9:$label] REAL overlay script against the live endpoint; stop playback → polling stops"
  local before_overlay; before_overlay=$(stats_requests)
  node tools/rig-scenarios/phantom-player-stats-overlay-dom.mjs --live --api "$API" --token "$USER_AUTH_TOKEN" \
    --device "$DEVICE" --stop-item "$gid" --expect-hash "$hash" --label "$label" | tee /tmp/pps-live-$label.out \
    || fail "$label live overlay run failed"
  grep -q '^LIVE OVERLAY OK' /tmp/pps-live-$label.out || fail "$label live overlay did not complete"
  local after_overlay; after_overlay=$(stats_requests)
  [ "$after_overlay" -gt "$before_overlay" ] || fail "$label overlay polls never reached gostream"
  sleep 8
  [ "$(stats_requests)" = "$after_overlay" ] || fail "$label gostream still receiving stats requests after playback stopped"
  echo "  gostream stats requests during overlay: $((after_overlay - before_overlay)); after stop: 0"

  user_api "$API/Plugins/PhantomLibrary/PlayerStats/Current" -o /tmp/pps-current.json || fail "$label Current HTTP error after stop"
  python3 -c "
import json; j=json.load(open('/tmp/pps-current.json'))
assert j['status']=='not_playing' and j['playing'] is False, j
" || fail "$label Current still reports playing after Sessions/Playing/Stopped"
}

# Second, non-playing user for the per-item authorisation check.
api "$API/Users" -o /tmp/pps-users.json
if ! python3 -c "import json,sys; sys.exit(0 if any(u.get('Name')=='pps-other' for u in json.load(open('/tmp/pps-users.json'))) else 1)"; then
  curl -sS --fail -X POST -H "X-Emby-Token: $TOK" -H 'Content-Type: application/json' \
    -d '{"Name":"pps-other","Password":"pps-other"}' "$API/Users/New" -o /dev/null || fail 'could not create pps-other user'
fi
curl -sS --fail -X POST -H 'Content-Type: application/json' \
  -H 'X-Emby-Authorization: MediaBrowser Client="phantom-rig", Device="phantom-rig", DeviceId="pps-other-device", Version="1"' \
  -d '{"Username":"pps-other","Pw":"pps-other"}' "$API/Users/AuthenticateByName" -o /tmp/pps-other.json || fail 'pps-other login failed'
OTHER_TOKEN=$(python3 -c "import json;print(json.load(open('/tmp/pps-other.json')).get('AccessToken') or '')")
[ -n "$OTHER_TOKEN" ] || fail 'pps-other token missing'

play_and_assert "$ALPHA_ID" movie "$ALPHA_HASH" movie 'Phantom.Rig.Alpha.2024.1080p.WEB-DL.x264-RIG'
play_and_assert "$EP_ID" episode "$EP_HASH" episode 'Phantom.Rig.Delta.S01E01.1080p.WEB-DL.x264-RIG'

echo 'PLAYER_STATS_OVERLAY_OK'
