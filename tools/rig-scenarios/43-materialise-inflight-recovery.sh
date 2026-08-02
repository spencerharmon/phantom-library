#!/bin/bash
# Scenario 43: materialise_in_flight leaked-claim recovery, no restart required.
#
# ROI regression (2026-08-02 operator, materialise-inflight-recovery):
# a materialise hard-killed mid-flight (pod SIGKILL / crash) never runs its
# finally-block cleanup and leaks a materialise_in_flight claim row. Before
# this fix, the ONLY recovery path was MaterialiseInFlightSweeper, which
# purges stale rows once at startup — a claim younger than
# MaterialiseInFlightStaleMinutes at that one sweep survives indefinitely,
# wedging the item at AlreadyInProgress until a SECOND restart happens to
# land after it ages out.
#
# This scenario simulates the leak directly (no `finally` ever runs) by
# inserting a materialise_in_flight row and backdating started_at past the
# stale threshold, WITHOUT restarting the rig process — then asserts the
# very next materialise retry reclaims the row inline and succeeds, for
# BOTH movie and episode (Movie/TV parity per AGENTS.md).
#
# It also asserts the safety case: a FRESH claim (started_at now) still
# blocks a concurrent duplicate materialise even when using the same
# reclaim-aware code path — the reclaim only fires past the stale
# threshold, never for a still-running materialise.
#
# *** Nodepool-gated live rig ***
#
# Like scenarios 30/31, this requires the patched Jellyfin build (rig-up.sh
# builds jellyfin/Jellyfin.Server from the patched submodule + the plugin
# DLL, then runs both under user systemd units on :18096). That build is
# the shared Nodepool-gated rig path recorded by the other channel-arch rig
# tasks; the in-sandbox machine gate for this task is `dotnet test`
# (MaterialiserTests.LeakedInFlightRow_OlderThanStaleThreshold_ReclaimedWithoutRestart,
# .FreshInFlightRow_UnderStaleThreshold_StillBlocksConcurrentDuplicate, and
# their episode-parity counterparts; PhantomDbTests covers the same
# invariants at the SQL layer). This script is the operator/Zuun-rig-side
# deliverable authored and locally reproducible against that same rig
# bring-up path used by 35/36.
#
# Movie/TV parity checklist (AGENTS.md): both a movie (Alpha, tmdb=99000001)
# and an episode (Charlie S01E01, tmdb=99000003) are exercised below.

set -euo pipefail

ROOT=${PHANTOM_REPO_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
RIG=/tmp/jf-rig
API=http://localhost:18096
TOK=testtoken00000000000000000000000
PHDB=/var/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db
JDB=/var/tmp/jf-test/data/data/jellyfin.db
LOG=$RIG/logs/scenario-materialise-inflight-recovery.log
ALPHA=99000001      # movie (seeded by rig-up / scenario 35 magnet cache convention)
CHARLIE=99000003    # episode series tmdb, S01E01

mkdir -p "$RIG/logs"
exec > >(tee "$LOG") 2>&1
cd "$ROOT"

cleanup() {
  dotnet build-server shutdown >/dev/null 2>&1 || true
}
trap cleanup EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }
api() { curl -sS --fail -H "X-Emby-Token: $TOK" "$@"; }
api_post() { curl -sS --fail -X POST -H "X-Emby-Token: $TOK" "$@"; }
hyphen() { python3 - "$1" <<'PY'
import sys
s=sys.argv[1]
print(f'{s[:8]}-{s[8:12]}-{s[12:16]}-{s[16:20]}-{s[20:]}')
PY
}

wait_task_idle() {
  local task_id=$1
  for _ in $(seq 1 120); do
    api "$API/ScheduledTasks" -o /tmp/tasks-43.json
    state=$(python3 - "$task_id" <<'PY'
import json,sys
j=json.load(open('/tmp/tasks-43.json'))
for t in j:
    if t.get('Id') == sys.argv[1]:
        print(t.get('State'))
        break
PY
)
    [ "$state" = "Idle" ] && return 0
    sleep 1
  done
  fail "task $task_id did not become Idle"
}

find_task_id() {
  python3 - <<'PY'
import json
j=json.load(open('/tmp/tasks-43.json'))
for t in j:
    if t.get('Key') == 'PhantomLibrary.DiscoveryRefresh' or t.get('Name') == 'Phantom Library — Refresh Discovery':
        print(t['Id'])
        raise SystemExit(0)
raise SystemExit(1)
PY
}

# Injects a leaked materialise_in_flight row for (tmdb,type,season,episode)
# that is OLDER than the configured stale threshold — simulating a process
# hard-killed mid-materialise whose `finally` cleanup never ran. No restart
# of the rig Jellyfin process happens anywhere in this scenario: the fix
# under test is precisely that no restart is required.
inject_leaked_claim() {
  local tmdb=$1 type=$2 season=${3:--1} episode=${4:--1}
  sqlite3 "$PHDB" "DELETE FROM materialise_in_flight WHERE tmdb_id=$tmdb AND type='$type' AND season=$season AND episode=$episode;"
  sqlite3 "$PHDB" "INSERT INTO materialise_in_flight (tmdb_id, type, season, episode, started_at)
    VALUES ($tmdb, '$type', $season, $episode, strftime('%s','now','-30 minutes'));"
}

inject_fresh_claim() {
  local tmdb=$1 type=$2 season=${3:--1} episode=${4:--1}
  sqlite3 "$PHDB" "DELETE FROM materialise_in_flight WHERE tmdb_id=$tmdb AND type='$type' AND season=$season AND episode=$episode;"
  sqlite3 "$PHDB" "INSERT INTO materialise_in_flight (tmdb_id, type, season, episode, started_at)
    VALUES ($tmdb, '$type', $season, $episode, strftime('%s','now'));"
}

assert_materialise_reclaims() {
  local item_id=$1 label=$2
  local gid
  gid=$(hyphen "$item_id")
  api_post "$API/Plugins/PhantomLibrary/Materialise/$gid" -o "/tmp/materialise-43-$label.json" \
    || fail "$label materialise call failed"
  python3 - "$label" <<PY
import json
j=json.load(open('/tmp/materialise-43-$label.json'))
print('MATERIALISE_RESULT[$label]=', j)
status=str(j.get('Status') or j.get('status'))
if status not in ('Success', '0'):
    raise SystemExit(f'$label: expected Success, got {status!r} ({j})')
PY
}

assert_materialise_already_in_progress() {
  local item_id=$1 label=$2
  local gid
  gid=$(hyphen "$item_id")
  api_post "$API/Plugins/PhantomLibrary/Materialise/$gid" -o "/tmp/materialise-43-$label.json" \
    || fail "$label materialise call failed"
  python3 - "$label" <<PY
import json
j=json.load(open('/tmp/materialise-43-$label.json'))
print('MATERIALISE_RESULT[$label]=', j)
status=str(j.get('Status') or j.get('status'))
if status not in ('AlreadyInProgress', '2'):
    raise SystemExit(f'$label: expected AlreadyInProgress (fresh claim must still block), got {status!r} ({j})')
PY
}

find_item_id() {
  local file=$1 tmdb=$2
  python3 - "$file" "$tmdb" <<'PY'
import json,sys
file,tmdb=sys.argv[1],sys.argv[2]
j=json.load(open(file))
for x in j.get('Items', []):
    if (x.get('ProviderIds') or {}).get('Tmdb') == tmdb or x.get('ExternalId') == f'movie_{tmdb}' or x.get('ExternalId') == f'episode_{tmdb}_1_1':
        print(x['Id'])
        raise SystemExit(0)
raise SystemExit(1)
PY
}

echo '[0] build plugin + start reset rig (mirrors scenarios 35/36 bring-up)'
read -r -a BUILD_ARGS <<< "${PHANTOM_DOTNET_BUILD_ARGS:-}"
dotnet build -c Release "${BUILD_ARGS[@]}" >/tmp/phantom-43-build.log
bash tools/rig-scenarios/rig-up.sh --reset

for _ in $(seq 1 60); do
  [ -f "$PHDB" ] && schema=$(sqlite3 "$PHDB" 'PRAGMA user_version;' 2>/dev/null || echo 0) || schema=0
  [ "$schema" != "0" ] && break
  sleep 1
done
[ "${schema:-0}" != "0" ] || fail "phantom schema never initialised"

echo '[1] trigger discovery task to populate catalogue + channel items'
api "$API/ScheduledTasks" -o /tmp/tasks-43.json
TASK_ID=$(find_task_id) || fail 'discovery task not found'
api_post "$API/ScheduledTasks/Running/$TASK_ID" -o /tmp/task-run-43.out || fail 'failed to start discovery task'
wait_task_idle "$TASK_ID"

echo '[2] seed magnet/availability cache for movie (Alpha) and episode (Charlie S01E01)'
now=$(date +%s)
sqlite3 "$PHDB" <<SQL
INSERT OR REPLACE INTO magnet_cache
(tmdb_id, imdb_id, type, season, episode, preset, magnet, info_hash, size, seeders, indexer, cached_at, ttl_seconds, source)
VALUES
($ALPHA, 'tt99000001', 'movie', 0, 0, 'gostream-default',
 'magnet:?xt=urn:btih:1111111111111111111111111111111111111111&dn=Phantom+Rig+Alpha',
 '1111111111111111111111111111111111111111', 10485760, 100, 'rig-cache', $now, 86400, 'rig'),
($CHARLIE, 'tt99000003', 'episode', 1, 1, 'gostream-default',
 'magnet:?xt=urn:btih:3333333333333333333333333333333333333333&dn=Phantom+Rig+Charlie+S01E01',
 '3333333333333333333333333333333333333333', 10485760, 100, 'rig-cache', $now, 86400, 'rig');
INSERT OR REPLACE INTO availability_items
(tmdb_id, type, season, episode, status, checked_at, next_check_at, candidate_magnet, candidate_info_hash, candidate_size, candidate_seeders, candidate_indexer, candidate_source)
VALUES
($ALPHA, 'movie', -1, -1, 'available', $now, $((now + 604800)),
 'magnet:?xt=urn:btih:1111111111111111111111111111111111111111&dn=Phantom+Rig+Alpha',
 '1111111111111111111111111111111111111111', 10485760, 100, 'rig-cache', 'rig'),
($CHARLIE, 'episode', 1, 1, 'available', $now, $((now + 604800)),
 'magnet:?xt=urn:btih:3333333333333333333333333333333333333333&dn=Phantom+Rig+Charlie+S01E01',
 '3333333333333333333333333333333333333333', 10485760, 100, 'rig-cache', 'rig');
SQL

echo '[3] browse channels to resolve item ids'
api "$API/Channels" -o /tmp/channels-43.json
MOVIES_CH=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/channels-43.json'))
items=j.get('Items', j if isinstance(j,list) else [])
for x in items:
    if x.get('Name') == 'Phantom Movies':
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'Phantom Movies channel not found'
SHOWS_CH=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/channels-43.json'))
items=j.get('Items', j if isinstance(j,list) else [])
for x in items:
    if x.get('Name') == 'Phantom Shows':
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'Phantom Shows channel not found'

api "$API/Channels/$MOVIES_CH/Items?Fields=ProviderIds&Limit=50" -o /tmp/movies-43.json
ALPHA_ID=$(find_item_id /tmp/movies-43.json "$ALPHA") || fail 'Alpha movie not found in channel'

# Resolve the Charlie episode item via series -> season -> episode browse.
api "$API/Channels/$SHOWS_CH/Items?Fields=ProviderIds&Limit=50" -o /tmp/series-43.json
SERIES_ID=$(find_item_id /tmp/series-43.json "$CHARLIE") || fail 'Charlie series not found in channel'
api "$API/Channels/$SHOWS_CH/Items?ParentId=$SERIES_ID&Limit=50" -o /tmp/seasons-43.json
SEASON_ID=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/seasons-43.json'))
items=j.get('Items', [])
print(items[0]['Id'] if items else '')
PY
)
[ -n "$SEASON_ID" ] || fail 'Charlie season not found'
api "$API/Channels/$SHOWS_CH/Items?ParentId=$SEASON_ID&Limit=50" -o /tmp/episodes-43.json
CHARLIE_EP_ID=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/episodes-43.json'))
items=j.get('Items', [])
print(items[0]['Id'] if items else '')
PY
)
[ -n "$CHARLIE_EP_ID" ] || fail 'Charlie S01E01 episode item not found'

echo '[4] MOVIE: leaked claim older than stale threshold reclaimed without restart'
inject_leaked_claim "$ALPHA" movie
row_before=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM materialise_in_flight WHERE tmdb_id=$ALPHA AND type='movie';")
[ "$row_before" = "1" ] || fail 'leaked movie claim not seeded'
assert_materialise_reclaims "$ALPHA_ID" 'movie-reclaim'
state_count=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM materialised_state WHERE tmdb_id=$ALPHA AND type='movie';")
[ "$state_count" = "1" ] || fail 'movie materialised_state missing after reclaim'
inflight_after=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM materialise_in_flight WHERE tmdb_id=$ALPHA AND type='movie';")
[ "$inflight_after" = "0" ] || fail 'movie in-flight row not cleared after successful reclaim+materialise'
echo '[4b] MOVIE: playback succeeds post-reclaim'
gid=$(hyphen "$ALPHA_ID")
code=$(curl -sS -L --max-time 20 -H "X-Emby-Token: $TOK" -H 'Range: bytes=0-4095' \
  -o /tmp/stream-43-movie.bin -w '%{http_code}' \
  "$API/Videos/$gid/stream.mkv?static=true" || true)
case "$code" in 200|206) : ;; *) fail "movie stream returned HTTP $code post-reclaim" ;; esac

echo '[5] MOVIE safety case: fresh claim still blocks a concurrent duplicate'
sqlite3 "$PHDB" "DELETE FROM materialised_state WHERE tmdb_id=$ALPHA AND type='movie';"
inject_fresh_claim "$ALPHA" movie
assert_materialise_already_in_progress "$ALPHA_ID" 'movie-fresh-blocks'
sqlite3 "$PHDB" "DELETE FROM materialise_in_flight WHERE tmdb_id=$ALPHA AND type='movie';"

echo '[6] EPISODE: leaked claim older than stale threshold reclaimed without restart (TV parity)'
inject_leaked_claim "$CHARLIE" episode 1 1
row_before=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM materialise_in_flight WHERE tmdb_id=$CHARLIE AND type='episode' AND season=1 AND episode=1;")
[ "$row_before" = "1" ] || fail 'leaked episode claim not seeded'
assert_materialise_reclaims "$CHARLIE_EP_ID" 'episode-reclaim'
state_count=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM materialised_state WHERE tmdb_id=$CHARLIE AND type='episode' AND season=1 AND episode=1;")
[ "$state_count" = "1" ] || fail 'episode materialised_state missing after reclaim'
inflight_after=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM materialise_in_flight WHERE tmdb_id=$CHARLIE AND type='episode' AND season=1 AND episode=1;")
[ "$inflight_after" = "0" ] || fail 'episode in-flight row not cleared after successful reclaim+materialise'
echo '[6b] EPISODE: playback succeeds post-reclaim'
gid=$(hyphen "$CHARLIE_EP_ID")
code=$(curl -sS -L --max-time 20 -H "X-Emby-Token: $TOK" -H 'Range: bytes=0-4095' \
  -o /tmp/stream-43-episode.bin -w '%{http_code}' \
  "$API/Videos/$gid/stream.mkv?static=true" || true)
case "$code" in 200|206) : ;; *) fail "episode stream returned HTTP $code post-reclaim" ;; esac

echo '[7] EPISODE safety case: fresh claim still blocks a concurrent duplicate (TV parity)'
sqlite3 "$PHDB" "DELETE FROM materialised_state WHERE tmdb_id=$CHARLIE AND type='episode' AND season=1 AND episode=1;"
inject_fresh_claim "$CHARLIE" episode 1 1
assert_materialise_already_in_progress "$CHARLIE_EP_ID" 'episode-fresh-blocks'
sqlite3 "$PHDB" "DELETE FROM materialise_in_flight WHERE tmdb_id=$CHARLIE AND type='episode' AND season=1 AND episode=1;"

echo 'MATERIALISE_INFLIGHT_RECOVERY_OK'
