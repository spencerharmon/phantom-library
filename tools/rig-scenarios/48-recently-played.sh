#!/bin/bash
# tools/rig-scenarios/48-recently-played.sh
#
# Recently-played fix (operator bug, 2026-09-12): drives a REAL Sessions/Playing
# → Sessions/Playing/Progress → Sessions/Playing/Stopped playback report to
# COMPLETION for a materialised phantom MOVIE and EPISODE (native
# RequiresOpening auto-open flow — the flow real clients use), then asserts
# both surface in the standard-Jellyfin recently-played / watched-history
# query (Filters=IsPlayed, SortBy=DatePlayed) with a correct DatePlayed and a
# reset resume position. Deliberately does NOT patch UserData directly via the
# REST endpoint (that would mask exactly the gap this scenario exists to
# catch — see RecentlyPlayedSyncListener's doc comment for the root cause).
#
# Also re-asserts 35/36 parity is unaffected: existing-gostream enrichment and
# native-open materialise-on-play both still resolve real playable sources.
#
# Efficiency guard: every query below is a targeted, bounded per-user
# Items?Filters=IsPlayed lookup (O(recent)) — never a full catalogue
# enumeration, and never a reintroduction of the deliberately removed
# ISupportsLatestMedia "Latest" (recently-added) row.
set -euo pipefail

ROOT=${PHANTOM_REPO_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
RIG=/tmp/jf-rig
API=http://localhost:18096
TOK=testtoken00000000000000000000000
PHDB=/var/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db
LOG=$RIG/logs/scenario-recently-played.log
ALPHA=99000021
SERIES=99000022
USER_ID=
USER_AUTH_TOKEN=

# Refuse the production port outright — this scenario must only ever run
# against the dev/test rig.
if [[ "$API" == *:8096* ]]; then
  echo "REFUSING to run against production port :8096" >&2
  exit 1
fi

mkdir -p "$RIG/logs"
exec > >(tee "$LOG") 2>&1
cd "$ROOT"

fail() { echo "FAIL: $*" >&2; exit 1; }
api() { curl -sS --fail -H "X-Emby-Token: $TOK" "$@"; }
api_post() { curl -sS --fail -X POST -H "X-Emby-Token: $TOK" "$@"; }
user_api() { curl -sS --fail -H "X-Emby-Token: $USER_AUTH_TOKEN" "$@"; }
user_json_post() { curl -sS --fail -X POST -H "X-Emby-Token: $USER_AUTH_TOKEN" -H "X-Emby-Authorization: MediaBrowser Client=\"phantom-rig\", Device=\"phantom-rig\", DeviceId=\"phantom-rig-device\", Version=\"1\", Token=\"$USER_AUTH_TOKEN\"" -H 'Content-Type: application/json' "$@"; }
hyphen() { python3 - "$1" <<'PY'
import sys
s=sys.argv[1]
print(f'{s[:8]}-{s[8:12]}-{s[12:16]}-{s[16:20]}-{s[20:]}')
PY
}

# Drive a full, REAL session-report playback lifecycle to completion for the
# given item — the same three calls a genuine client makes, never a direct
# UserData PATCH. This is the exact mechanism the operator-facing bug
# affects; a scenario that bypassed it would prove nothing.
play_to_completion() {
  local id=$1 label=$2
  local gid session_id
  gid=$(hyphen "$id")
  session_id="phantom-rig-recently-played-$label"

  api "$API/Items/$id/PlaybackInfo" -o /tmp/rp-pbinfo.json || fail "$label PlaybackInfo HTTP error"
  local source_id
  source_id=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/rp-pbinfo.json'))
print(((j.get('MediaSources') or [{}])[0]).get('Id') or '')
PY
)
  local runtime_ticks
  runtime_ticks=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/rp-pbinfo.json'))
print(((j.get('MediaSources') or [{}])[0]).get('RunTimeTicks') or 57000000000)
PY
)

  local start_payload
  start_payload=$(python3 - "$gid" "$source_id" "$session_id" <<'PY'
import json,sys
item,source,session=sys.argv[1],sys.argv[2],sys.argv[3]
print(json.dumps({
  'ItemId': item, 'MediaSourceId': source, 'PlaySessionId': session,
  'PositionTicks': 0, 'CanSeek': True, 'IsPaused': False, 'PlayMethod': 'DirectPlay'
}))
PY
)
  user_json_post --data-binary "$start_payload" "$API/Sessions/Playing" -o /tmp/rp-start.out \
    || fail "$label PlaybackStart report failed"

  # Report progress at 95% — well past MinResumePct/MaxResumePct so core
  # treats it as a completed watch — then a final Stopped report.
  local near_end=$((runtime_ticks * 95 / 100))
  local progress_payload
  progress_payload=$(python3 - "$gid" "$source_id" "$session_id" "$near_end" <<'PY'
import json,sys
item,source,session,pos=sys.argv[1],sys.argv[2],sys.argv[3],int(sys.argv[4])
print(json.dumps({
  'ItemId': item, 'MediaSourceId': source, 'PlaySessionId': session,
  'PositionTicks': pos, 'CanSeek': True, 'IsPaused': False, 'PlayMethod': 'DirectPlay'
}))
PY
)
  user_json_post --data-binary "$progress_payload" "$API/Sessions/Playing/Progress" -o /tmp/rp-progress.out \
    || fail "$label PlaybackProgress report failed"

  local stop_payload
  stop_payload=$(python3 - "$gid" "$source_id" "$session_id" "$near_end" <<'PY'
import json,sys
item,source,session,pos=sys.argv[1],sys.argv[2],sys.argv[3],int(sys.argv[4])
print(json.dumps({
  'ItemId': item, 'MediaSourceId': source, 'PlaySessionId': session,
  'PositionTicks': pos
}))
PY
)
  user_json_post --data-binary "$stop_payload" "$API/Sessions/Playing/Stopped" -o /tmp/rp-stop.out \
    || fail "$label PlaybackStopped report failed"
}

assert_recently_played() {
  local id=$1 label=$2 include_types=$3
  local gid
  gid=$(hyphen "$id")
  for _ in $(seq 1 20); do
    user_api "$API/Users/$USER_ID/Items?SortBy=DatePlayed&SortOrder=Descending&Filters=IsPlayed&IncludeItemTypes=$include_types&Recursive=true&Fields=UserData&Limit=50" \
      -o /tmp/rp-recently-played.json || fail "$label recently-played query failed"
    if python3 - "$id" "$label" <<'PY'
import json,sys
wanted=sys.argv[1].lower()
j=json.load(open('/tmp/rp-recently-played.json'))
for x in j.get('Items', []):
    if (x.get('Id') or '').replace('-','').lower() == wanted:
        ud = x.get('UserData') or {}
        print('  recently_played_hit=', x.get('Name'), x.get('Id'), ud)
        if not ud.get('Played'):
            raise SystemExit('recently-played hit missing Played=true')
        if not ud.get('LastPlayedDate'):
            raise SystemExit('recently-played hit missing LastPlayedDate')
        if (ud.get('PlaybackPositionTicks') or 0) != 0:
            raise SystemExit('recently-played hit should have reset resume position')
        raise SystemExit(0)
raise SystemExit(1)
PY
    then return 0; fi
    sleep 1
  done
  python3 - <<'PY'
import json
j=json.load(open('/tmp/rp-recently-played.json'))
print('RECENTLY_PLAYED_ITEMS=', [(x.get('Name'), x.get('Id'), x.get('UserData')) for x in j.get('Items', [])])
PY
  fail "$label did not appear in recently-played (Filters=IsPlayed, SortBy=DatePlayed)"
}

echo '[0] build plugin + start reset rig'
if [ "${RIG_NO_RESET:-0}" = "1" ]; then
  echo '  RIG_NO_RESET=1: reusing the already-running rig + its existing phantom.db (no reset)'
  [ -f "$PHDB" ] || fail "RIG_NO_RESET=1 but no phantom.db at $PHDB — the caller must seed + boot the rig first"
else
  read -r -a BUILD_ARGS <<< "${PHANTOM_DOTNET_BUILD_ARGS:-}"
  dotnet build -c Release "${BUILD_ARGS[@]}" >/tmp/phantom-recently-played-build.log
  bash tools/rig-scenarios/rig-up.sh --reset
fi

curl -sS --fail -X POST -H 'Content-Type: application/json' \
  -H 'X-Emby-Authorization: MediaBrowser Client="phantom-rig", Device="phantom-rig", DeviceId="phantom-rig-login", Version="1"' \
  -d '{"Username":"a","Pw":"a"}' "$API/Users/AuthenticateByName" -o /tmp/rp-auth-user.json \
  || fail 'test user login failed'
USER_ID=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/rp-auth-user.json'))
print((j.get('User') or {}).get('Id') or '')
PY
)
USER_AUTH_TOKEN=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/rp-auth-user.json'))
print(j.get('AccessToken') or '')
PY
)
[ -n "$USER_ID" ] || fail 'test user id missing'
[ -n "$USER_AUTH_TOKEN" ] || fail 'test user token missing'

echo '[1] trigger discovery task'
api "$API/ScheduledTasks" -o /tmp/rp-tasks.json
TASK_ID=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/rp-tasks.json'))
for t in j:
    if t.get('Key') == 'PhantomLibrary.DiscoveryRefresh' or t.get('Name') == 'Phantom Library — Refresh Discovery':
        print(t['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'discovery task not found'
api_post "$API/ScheduledTasks/Running/$TASK_ID" -o /tmp/rp-task-run.out || fail 'failed to start discovery task'
for _ in $(seq 1 120); do
  api "$API/ScheduledTasks" -o /tmp/rp-tasks.json
  state=$(python3 - "$TASK_ID" <<'PY'
import json,sys
j=json.load(open('/tmp/rp-tasks.json'))
for t in j:
    if t.get('Id') == sys.argv[1]:
        print(t.get('State')); break
PY
)
  [ "$state" = "Idle" ] && break
  sleep 1
done

echo '[2] seed magnet cache for movie + episode materialise'
now=$(date +%s)
sqlite3 "$PHDB" <<SQL
INSERT OR REPLACE INTO magnet_cache
(tmdb_id, imdb_id, type, season, episode, preset, magnet, info_hash, size, seeders, indexer, cached_at, ttl_seconds, source)
VALUES
($ALPHA, 'tt99000021', 'movie', 0, 0, 'gostream-default',
 'magnet:?xt=urn:btih:3333333333333333333333333333333333333333&dn=Phantom+Rig+Recently+Played',
 '3333333333333333333333333333333333333333', 10485760, 100, 'rig-cache', $now, 86400, 'rig'),
($SERIES, 'tt99000022', 'episode', 1, 1, 'gostream-default',
 'magnet:?xt=urn:btih:4444444444444444444444444444444444444444&dn=Phantom+Rig+Recently+Played+Episode',
 '4444444444444444444444444444444444444444', 10485760, 100, 'rig-cache', $now, 86400, 'rig');
INSERT OR REPLACE INTO availability_items
(tmdb_id, type, season, episode, status, checked_at, next_check_at, candidate_magnet, candidate_info_hash, candidate_size, candidate_seeders, candidate_indexer, candidate_source)
VALUES
($ALPHA, 'movie', -1, -1, 'available', $now, $((now + 604800)),
 'magnet:?xt=urn:btih:3333333333333333333333333333333333333333&dn=Phantom+Rig+Recently+Played',
 '3333333333333333333333333333333333333333', 10485760, 100, 'rig-cache', 'rig'),
($SERIES, 'episode', 1, 1, 'available', $now, $((now + 604800)),
 'magnet:?xt=urn:btih:4444444444444444444444444444444444444444&dn=Phantom+Rig+Recently+Played+Episode',
 '4444444444444444444444444444444444444444', 10485760, 100, 'rig-cache', 'rig');
INSERT OR REPLACE INTO plugin_meta(key,value) VALUES('channel_dataversion_movies', '$now-rig-seed-rp');
INSERT OR REPLACE INTO plugin_meta(key,value) VALUES('channel_dataversion_shows', '$now-rig-seed-rp');
SQL

echo '[3] browse channels, locate movie + episode'
api "$API/Channels" -o /tmp/rp-channels.json
MOVIES_CH=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/rp-channels.json'))
items=j.get('Items', j if isinstance(j,list) else [])
for x in items:
    if x.get('Name') == 'Phantom Movies':
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'Phantom Movies channel not found'
SHOWS_CH=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/rp-channels.json'))
items=j.get('Items', j if isinstance(j,list) else [])
for x in items:
    if x.get('Name') == 'Phantom Shows':
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'Phantom Shows channel not found'

api "$API/Channels/$MOVIES_CH/Items?Fields=ProviderIds&Limit=50" -o /tmp/rp-movies.json
ALPHA_ID=$(python3 - "$ALPHA" <<'PY'
import json,sys
tmdb=sys.argv[1]
j=json.load(open('/tmp/rp-movies.json'))
for x in j.get('Items', []):
    if (x.get('ProviderIds') or {}).get('Tmdb') == tmdb:
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'recently-played movie not found in channel'

# Locate the seeded episode's series, then the s1e1 episode item.
api "$API/Channels/$SHOWS_CH/Items?Fields=ProviderIds&Limit=50" -o /tmp/rp-series.json
SERIES_ID=$(python3 - "$SERIES" <<'PY'
import json,sys
tmdb=sys.argv[1]
j=json.load(open('/tmp/rp-series.json'))
for x in j.get('Items', []):
    if (x.get('ProviderIds') or {}).get('Tmdb') == tmdb:
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'recently-played series not found in channel'
api "$API/Shows/$SERIES_ID/Episodes?SeasonId=&Fields=ProviderIds&Limit=50" -o /tmp/rp-episodes.json || true
EPISODE_ID=$(python3 - <<'PY'
import json
try:
    j=json.load(open('/tmp/rp-episodes.json'))
except Exception:
    raise SystemExit(1)
items=j.get('Items', [])
if items:
    print(items[0]['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'recently-played episode not found'

echo '[4] native-open auto-materialise BOTH (movie + episode) — 35/36 parity: real playable sources'
for id in "$ALPHA_ID" "$EPISODE_ID"; do
  gid=$(hyphen "$id")
  curl -sS --fail -X POST -H "X-Emby-Token: $TOK" -H 'Content-Type: application/json' \
    -d '{"AutoOpenLiveStream":true}' \
    "$API/Items/$gid/PlaybackInfo?AutoOpenLiveStream=true" -o /tmp/rp-auto-open.json \
    || fail "auto-open PlaybackInfo failed for $id"
  python3 - "$id" <<'PY'
import json,sys
j=json.load(open('/tmp/rp-auto-open.json'))
if j.get('ErrorCode'):
    raise SystemExit(f'auto-open ErrorCode={j.get("ErrorCode")} for {sys.argv[1]}')
src=(j.get('MediaSources') or [{}])[0]
if src.get('RequiresOpening'):
    raise SystemExit(f'auto-open should return the final real source for {sys.argv[1]}')
if src.get('Protocol') != 'File':
    raise SystemExit(f'expected File protocol for {sys.argv[1]}, got {src.get("Protocol")}')
PY
done

echo '[5] drive a REAL playback session to completion (movie + episode) — never a direct UserData PATCH'
play_to_completion "$ALPHA_ID" 'recently-played-movie'
play_to_completion "$EPISODE_ID" 'recently-played-episode'

echo '[6] assert BOTH surface in the standard recently-played query (Filters=IsPlayed, SortBy=DatePlayed)'
assert_recently_played "$ALPHA_ID" 'recently-played-movie' 'Movie'
assert_recently_played "$EPISODE_ID" 'recently-played-episode' 'Episode'

echo 'RECENTLY_PLAYED_OK'
