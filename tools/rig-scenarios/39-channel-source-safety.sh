#!/bin/bash
# REQ-M14-SOURCE-SAFETY live rig proof.
#
# Drives the real Jellyfin channel/native-open flow end to end for BOTH a
# movie and a TV episode and proves the source-management safety contract:
#
#   1. materialise a phantom item via native auto-open,
#   2. reject the current source,
#   3. the next ranked availability candidate materialises,
#   4. the channel item refreshes to the new real file,
#   5. playback still succeeds on the new source,
#   6. the rejected source's backing file is removed ONLY when no other
#      materialised item still references it (shared-hash guard):
#        - unshared reject  -> old stub + backing file are REMOVED,
#        - shared reject     -> old stub + backing file are PRESERVED.
#
# The shared-hash guard is PhantomSourceManager.CountOtherMaterialisedReferences:
# a second materialised item whose magnet_cache row carries the same info_hash
# keeps the physical file alive even though the items have distinct stubs. This
# scenario seeds that second (ghost) reference directly, exactly like the unit
# tests RejectCurrent_SharedSource_DoesNotRemoveGostreamStub (movie) and
# RejectCurrent_Episode_SharedSource_DoesNotRemoveGostreamStub (episode).
#
# gostream-mock.py implements POST /api/library/remove so removal-vs-preservation
# is observable on disk; this scenario asserts both the on-disk state and the
# mock's removal log. Scenarios 35/36 stay green (35's unshared reject now
# actually deletes its old stub, which it never asserted the presence of; 36
# performs no reject).
#
# Reused by zuul-live-rig-job. Honours $PHANTOM_REPO_ROOT for CI portability.
set -euo pipefail

ROOT=${PHANTOM_REPO_ROOT:-/home/spencer/git-repos/spencerharmon/phantom-library}
RIG=/tmp/jf-rig
API=http://localhost:18096
TOK=testtoken00000000000000000000000
PHDB=/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db
JDB=/tmp/jf-test/data/data/jellyfin.db
LOG=$RIG/logs/scenario-channel-source-safety.log
REMOVE_LOG=$RIG/logs/gostream-mock.log
MOVIES_SUB=/tmp/jf-rig/gostream/movies/
TV_SUB=/tmp/jf-rig/gostream/tv/

# Real catalogue items under test.
ALPHA=99000001            # movie, UNSHARED reject -> old file removed
CHARLIE=99000003          # movie, SHARED reject   -> old file preserved
SERIES=99100001           # Delta series
EP1=1                     # Delta S01E01 episode, UNSHARED reject
EP2=2                     # Delta S01E02 episode, SHARED reject

# Synthetic ghost referencers (never browsed; DB rows only). They give the
# shared-hash items a peer reference so the guard fires.
GHOST_MOVIE=99000090
GHOST_SERIES=99100090

# Info hashes (40 hex). *_CUR = first/current source; *_ALT = next candidate.
A_CUR=1111111111111111111111111111111111111111
A_ALT=2222222222222222222222222222222222222222
C_CUR=3333333333333333333333333333333333333333   # SHARED with GHOST_MOVIE
C_ALT=5555555555555555555555555555555555555555
E1_CUR=6666666666666666666666666666666666666666
E1_ALT=7777777777777777777777777777777777777777
E2_CUR=8888888888888888888888888888888888888888   # SHARED with GHOST_SERIES
E2_ALT=9999999999999999999999999999999999999999

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
magnet() { echo "magnet:?xt=urn:btih:$1&dn=Phantom+Rig+Source+Safety"; }

find_task_id() {
  python3 - <<'PY'
import json
j=json.load(open('/tmp/tasks.json'))
for t in j:
    if t.get('Key') == 'PhantomLibrary.DiscoveryRefresh' or t.get('Name') == 'Phantom Library — Refresh Discovery':
        print(t['Id']); raise SystemExit(0)
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
        print(t.get('State')); break
PY
)
    echo "  task_state=$state"
    [ "$state" = "Idle" ] && return 0
    sleep 1
  done
  fail "task $task_id did not become Idle"
}

find_channel_id() {
  local name=$1
  python3 - "$name" <<'PY'
import json,sys
name=sys.argv[1]
j=json.load(open('/tmp/channels.json'))
items=j.get('Items', j if isinstance(j,list) else [])
for x in items:
    if x.get('Name') == name:
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
}

find_by_external() {
  local file=$1 external=$2
  python3 - "$file" "$external" <<'PY'
import json,sys
file,external=sys.argv[1],sys.argv[2]
j=json.load(open(file))
for x in j.get('Items', []):
    if x.get('ExternalId') == external or x.get('Name') == external:
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
}

find_by_tmdb() {
  local file=$1 tmdb=$2
  python3 - "$file" "$tmdb" <<'PY'
import json,sys
file,tmdb=sys.argv[1],sys.argv[2]
j=json.load(open(file))
for x in j.get('Items', []):
    if (x.get('ProviderIds') or {}).get('Tmdb') == tmdb or x.get('ExternalId') == f'movie_{tmdb}':
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
}

# GET PlaybackInfo asserts a native RequiresOpening source (no splash path).
assert_opening() {
  local id=$1 external=$2 label=$3
  echo "[opening] $label id=$id ext=$external"
  api "$API/Items/$id/PlaybackInfo" -o /tmp/pb-open.json || fail "$label opening PlaybackInfo HTTP error"
  python3 - "$external" "$label" <<'PY'
import json,sys,uuid
external,label=sys.argv[1],sys.argv[2]
j=json.load(open('/tmp/pb-open.json'))
if j.get('ErrorCode'):
    raise SystemExit(f'{label}: PlaybackInfo ErrorCode={j.get("ErrorCode")}')
sources=j.get('MediaSources') or []
if len(sources) != 1:
    raise SystemExit(f'{label}: expected exactly one MediaSource, got {len(sources)}')
s=sources[0]
if not s.get('RequiresOpening'):
    raise SystemExit(f'{label}: expected RequiresOpening source, got {s}')
tok=s.get('OpenToken') or ''
if not tok.endswith(f'_phantom:{external}'):
    raise SystemExit(f'{label}: expected open token _phantom:{external}, got {tok!r}')
if s.get('Path') not in (None, ''):
    raise SystemExit(f'{label}: expected no splash path, got {s.get("Path")!r}')
uuid.UUID(s.get('Id'))
PY
}

# POST AutoOpenLiveStream materialises through the gostream mock and returns the
# real backing file.
auto_open() {
  local id=$1 sub=$2 label=$3 gid
  gid=$(hyphen "$id")
  echo "[auto-open] $label guid=$gid"
  curl -sS --fail -X POST -H "X-Emby-Token: $TOK" -H 'Content-Type: application/json' \
    -d '{"AutoOpenLiveStream":true}' \
    "$API/Items/$gid/PlaybackInfo?AutoOpenLiveStream=true" -o /tmp/pb-auto.json \
    || fail "$label auto-open PlaybackInfo HTTP error"
  python3 - "$sub" "$label" <<'PY'
import json,sys,uuid
sub,label=sys.argv[1],sys.argv[2]
j=json.load(open('/tmp/pb-auto.json'))
if j.get('ErrorCode'):
    raise SystemExit(f'{label}: PlaybackInfo ErrorCode={j.get("ErrorCode")}')
sources=j.get('MediaSources') or []
if len(sources) != 1:
    raise SystemExit(f'{label}: expected exactly one MediaSource, got {len(sources)}')
s=sources[0]
if s.get('RequiresOpening'):
    raise SystemExit(f'{label}: auto-open should return the final real source, got {s}')
path=s.get('Path') or ''
if sub not in path:
    raise SystemExit(f'{label}: expected path containing {sub!r}, got {path!r}')
if s.get('Protocol') != 'File':
    raise SystemExit(f'{label}: expected File protocol, got {s.get("Protocol")!r}')
if len(s.get('MediaStreams') or []) < 1:
    raise SystemExit(f'{label}: expected probed MediaStreams')
uuid.UUID(s.get('Id'))
PY
}

# GET PlaybackInfo asserts the current source is a real (non-opening) File under
# the expected gostream subtree. Retries to absorb post-reject channel refresh.
assert_real_source() {
  local id=$1 sub=$2 label=$3
  echo "[real-source] $label id=$id"
  for _ in $(seq 1 30); do
    api "$API/Items/$id/PlaybackInfo" -o /tmp/pb-real.json || fail "$label PlaybackInfo HTTP error"
    if python3 - "$sub" <<'PY'
import json,sys
sub=sys.argv[1]
j=json.load(open('/tmp/pb-real.json'))
s=(j.get('MediaSources') or [{}])[0]
raise SystemExit(0 if (not s.get('RequiresOpening') and sub in (s.get('Path') or '')) else 1)
PY
    then break; fi
    sleep 1
  done
  python3 - "$sub" "$label" <<'PY'
import json,sys,uuid
sub,label=sys.argv[1],sys.argv[2]
j=json.load(open('/tmp/pb-real.json'))
if j.get('ErrorCode'):
    raise SystemExit(f'{label}: PlaybackInfo ErrorCode={j.get("ErrorCode")}')
s=(j.get('MediaSources') or [{}])[0]
if s.get('RequiresOpening'):
    raise SystemExit(f'{label}: expected materialised real source, got {s}')
path=s.get('Path') or ''
if sub not in path:
    raise SystemExit(f'{label}: expected path containing {sub!r}, got {path!r}')
if s.get('Protocol') != 'File':
    raise SystemExit(f'{label}: expected File protocol, got {s.get("Protocol")!r}')
uuid.UUID(s.get('Id'))
print('  real path=', path)
PY
}

assert_stream_opens() {
  local id=$1 container=$2 label=$3 gid code bytes
  gid=$(hyphen "$id")
  echo "[stream-open] $label guid=$gid container=$container"
  code=$(curl -sS -L --max-time 20 -H "X-Emby-Token: $TOK" -H 'Range: bytes=0-4095' \
    -o /tmp/stream.bin -w '%{http_code}' \
    "$API/Videos/$gid/stream.$container?static=true" || true)
  bytes=$(wc -c < /tmp/stream.bin 2>/dev/null || echo 0)
  echo "  http=$code bytes=$bytes"
  case "$code" in 200|206) : ;; *) fail "$label stream returned HTTP $code" ;; esac
  [ "$bytes" -gt 0 ] || fail "$label stream returned zero bytes"
}

# POST RejectCurrent and assert the alternate materialised.
reject_current() {
  local external=$1 label=$2
  echo "[reject] $label ext=$external"
  api_post "$API/Plugins/PhantomLibrary/Items/$external/Sources/RejectCurrent" -o /tmp/reject.json \
    || fail "$label RejectCurrent API failed"
  python3 - "$label" <<'PY'
import json,sys
label=sys.argv[1]
j=json.load(open('/tmp/reject.json'))
print(f'  REJECT_RESULT[{label}]=', j)
code=j.get('Code') or j.get('code')
status=str(j.get('Status') or j.get('status'))
if code != 'materialised' and 'Success' not in status and status != '0':
    raise SystemExit(f'{label}: reject did not materialise alternate: code={code!r} status={status!r}')
PY
}

state_stub() {  # tmdb type season episode
  sqlite3 "$PHDB" "SELECT stub_path FROM materialised_state WHERE tmdb_id=$1 AND type='$2' AND season=$3 AND episode=$4;"
}

assert_present() { [ -f "$1" ] || fail "$2: expected file PRESENT but missing: $1"; echo "  PRESENT ok: $1"; }
assert_absent()  { [ ! -e "$1" ] || fail "$2: expected file ABSENT but present: $1"; echo "  ABSENT ok: $1"; }

removed_logged() { grep -Fq "remove stub=$1 " "$REMOVE_LOG"; }

echo '[0] build plugin + start reset rig'
read -r -a BUILD_ARGS <<< "${PHANTOM_DOTNET_BUILD_ARGS:-}"
dotnet build -c Release "${BUILD_ARGS[@]}" >/tmp/phantom-source-safety-build.log
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
movies_count=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM catalogue_items WHERE type='movie';")
series_count=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM catalogue_items WHERE type='series';")
[ "$movies_count" -ge 3 ] || fail "expected >=3 catalogue movies, got $movies_count"
[ "$series_count" -ge 2 ] || fail "expected >=2 catalogue series, got $series_count"

echo '[2] seed current sources + availability candidates + ghost shared references'
now=$(date +%s)
future=$((now + 604800))
A_CUR_M=$(magnet "$A_CUR"); C_CUR_M=$(magnet "$C_CUR")
E1_CUR_M=$(magnet "$E1_CUR"); E2_CUR_M=$(magnet "$E2_CUR")
sqlite3 "$PHDB" <<SQL
-- Movie current sources (Alpha unshared, Charlie shared).
INSERT OR REPLACE INTO magnet_cache
 (tmdb_id, imdb_id, type, season, episode, preset, magnet, info_hash, size, seeders, indexer, cached_at, ttl_seconds, source)
VALUES
 ($ALPHA,  'tt99000001', 'movie', 0, 0, 'gostream-default', '$A_CUR_M', '$A_CUR', 10485760, 100, 'rig-cache', $now, 86400, 'rig'),
 ($CHARLIE,'tt99000003', 'movie', 0, 0, 'gostream-default', '$C_CUR_M', '$C_CUR', 10485760, 100, 'rig-cache', $now, 86400, 'rig');
INSERT OR REPLACE INTO availability_items
 (tmdb_id, type, season, episode, status, checked_at, next_check_at, candidate_magnet, candidate_info_hash, candidate_size, candidate_seeders, candidate_indexer, candidate_source)
VALUES
 ($ALPHA,  'movie', -1, -1, 'available', $now, $future, '$A_CUR_M', '$A_CUR', 10485760, 100, 'rig-cache', 'rig'),
 ($CHARLIE,'movie', -1, -1, 'available', $now, $future, '$C_CUR_M', '$C_CUR', 10485760, 100, 'rig-cache', 'rig');

-- Episode current sources (S01E01 unshared, S01E02 shared).
INSERT OR REPLACE INTO magnet_cache
 (tmdb_id, imdb_id, type, season, episode, preset, magnet, info_hash, size, seeders, indexer, cached_at, ttl_seconds, source)
VALUES
 ($SERIES, 'tt99100001', 'episode', 1, $EP1, 'gostream-default', '$E1_CUR_M', '$E1_CUR', 10485760, 100, 'rig-cache', $now, 86400, 'rig'),
 ($SERIES, 'tt99100001', 'episode', 1, $EP2, 'gostream-default', '$E2_CUR_M', '$E2_CUR', 10485760, 100, 'rig-cache', $now, 86400, 'rig');
INSERT OR REPLACE INTO availability_items
 (tmdb_id, type, season, episode, status, checked_at, next_check_at, candidate_magnet, candidate_info_hash, candidate_size, candidate_seeders, candidate_indexer, candidate_source)
VALUES
 ($SERIES, 'episode', 1, $EP1, 'available', $now, $future, '$E1_CUR_M', '$E1_CUR', 10485760, 100, 'rig-cache', 'rig'),
 ($SERIES, 'episode', 1, $EP2, 'available', $now, $future, '$E2_CUR_M', '$E2_CUR', 10485760, 100, 'rig-cache', 'rig');

-- Ghost peer references: a second materialised item carrying the SAME info_hash
-- keeps the shared source alive on reject (movie via season/episode 0 sentinel
-- mapping; episode via real season/episode). Distinct stub paths, matched hash.
INSERT OR REPLACE INTO materialised_state (tmdb_id, type, season, episode, stub_path, fuse_path, materialised_at)
VALUES
 ($GHOST_MOVIE,  'movie',   -1, -1, '/tmp/jf-rig/gostream/stubs/ghost_charlie_peer.mkv', '/tmp/jf-rig/gostream/movies/ghost_charlie_peer.mkv', $now),
 ($GHOST_SERIES, 'episode',  1,  1, '/tmp/jf-rig/gostream/stubs/ghost_delta_peer.mkv',   '/tmp/jf-rig/gostream/tv/ghost_delta_peer.mkv',       $now);
INSERT OR REPLACE INTO magnet_cache
 (tmdb_id, imdb_id, type, season, episode, preset, magnet, info_hash, size, seeders, indexer, cached_at, ttl_seconds, source)
VALUES
 ($GHOST_MOVIE,  '', 'movie',   0, 0, 'ghost', 'magnet:?xt=urn:btih:$C_CUR', '$C_CUR', 10485760, 10, 'ghost', $now, 86400, 'ghost'),
 ($GHOST_SERIES, '', 'episode', 1, 1, 'ghost', 'magnet:?xt=urn:btih:$E2_CUR', '$E2_CUR', 10485760, 10, 'ghost', $now, 86400, 'ghost');

INSERT OR REPLACE INTO plugin_meta(key,value) VALUES('channel_dataversion_movies', '$now-safety-seed');
INSERT OR REPLACE INTO plugin_meta(key,value) VALUES('channel_dataversion_shows',  '$now-safety-seed');
SQL

echo '[3] browse movie channel -> Alpha + Charlie'
api "$API/Channels" -o /tmp/channels.json
MOVIES_CH=$(find_channel_id 'Phantom Movies') || fail 'Phantom Movies channel not found'
api "$API/Channels/$MOVIES_CH/Items?Fields=Tags,ProviderIds,MediaSources,Path,Overview,ProductionYear&Limit=50" -o /tmp/movies.json
ALPHA_ID=$(find_by_tmdb /tmp/movies.json "$ALPHA") || fail 'Alpha movie not found in channel'
CHARLIE_ID=$(find_by_tmdb /tmp/movies.json "$CHARLIE") || fail 'Charlie movie not found in channel'
echo "  ALPHA_ID=$ALPHA_ID CHARLIE_ID=$CHARLIE_ID"

echo '[4] browse shows channel -> Delta S01E01 + S01E02'
SHOWS_CH=$(find_channel_id 'Phantom Shows') || fail 'Phantom Shows channel not found'
api "$API/Channels/$SHOWS_CH/Items?Fields=ExternalId,ProviderIds&Limit=50" -o /tmp/series.json
SERIES_ID=$(find_by_external /tmp/series.json "series_$SERIES") || fail 'Delta series not found'
api "$API/Channels/$SHOWS_CH/Items?FolderId=$SERIES_ID&Fields=ExternalId&Limit=50" -o /tmp/seasons.json
SEASON_ID=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/seasons.json'))
for x in j.get('Items', []):
    if x.get('Name') == 'Season 1':
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'Delta Season 1 not found'
api "$API/Channels/$SHOWS_CH/Items?FolderId=$SEASON_ID&Fields=Tags,MediaSources,Path,ExternalId&Limit=50" -o /tmp/episodes.json
EP1_ID=$(find_by_external /tmp/episodes.json 'Phantom Rig Delta Episode 1') || fail 'Delta S01E01 not found'
EP2_ID=$(find_by_external /tmp/episodes.json 'Phantom Rig Delta Episode 2') || fail 'Delta S01E02 not found'
echo "  EP1_ID=$EP1_ID EP2_ID=$EP2_ID"

echo '[5] materialise all four items via native auto-open'
assert_opening "$ALPHA_ID"   "movie_$ALPHA"            'alpha-open'
auto_open      "$ALPHA_ID"   "$MOVIES_SUB"             'alpha-auto-open'
assert_opening "$CHARLIE_ID" "movie_$CHARLIE"          'charlie-open'
auto_open      "$CHARLIE_ID" "$MOVIES_SUB"             'charlie-auto-open'
assert_opening "$EP1_ID"     "episode_${SERIES}_s01e01" 'ep1-open'
auto_open      "$EP1_ID"     "$TV_SUB"                 'ep1-auto-open'
assert_opening "$EP2_ID"     "episode_${SERIES}_s01e02" 'ep2-open'
auto_open      "$EP2_ID"     "$TV_SUB"                 'ep2-auto-open'

for spec in "$ALPHA:movie:-1:-1" "$CHARLIE:movie:-1:-1" "$SERIES:episode:1:$EP1" "$SERIES:episode:1:$EP2"; do
  IFS=: read -r t ty s e <<< "$spec"
  c=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM materialised_state WHERE tmdb_id=$t AND type='$ty' AND season=$s AND episode=$e;")
  [ "$c" = "1" ] || fail "materialised_state missing for $spec (got $c)"
done

# Capture pre-reject stubs.
ALPHA_OLD=$(state_stub "$ALPHA" movie -1 -1)
CHARLIE_OLD=$(state_stub "$CHARLIE" movie -1 -1)
EP1_OLD=$(state_stub "$SERIES" episode 1 "$EP1")
EP2_OLD=$(state_stub "$SERIES" episode 1 "$EP2")
echo "  ALPHA_OLD=$ALPHA_OLD"
echo "  CHARLIE_OLD=$CHARLIE_OLD"
echo "  EP1_OLD=$EP1_OLD"
echo "  EP2_OLD=$EP2_OLD"
for f in "$ALPHA_OLD" "$CHARLIE_OLD" "$EP1_OLD" "$EP2_OLD"; do
  [ -n "$f" ] || fail 'a materialised stub path came back empty'
  assert_present "$f" 'pre-reject stub'
done

echo '[6] point availability at the next candidate for each item'
sqlite3 "$PHDB" <<SQL
UPDATE availability_items SET candidate_magnet='$(magnet "$A_ALT")',  candidate_info_hash='$A_ALT',  candidate_size=20971520, candidate_seeders=55, candidate_indexer='rig-alt', candidate_source='rig-alt', status='available', checked_at=$now, next_check_at=$future WHERE tmdb_id=$ALPHA   AND type='movie'   AND season=-1 AND episode=-1;
UPDATE availability_items SET candidate_magnet='$(magnet "$C_ALT")',  candidate_info_hash='$C_ALT',  candidate_size=20971520, candidate_seeders=55, candidate_indexer='rig-alt', candidate_source='rig-alt', status='available', checked_at=$now, next_check_at=$future WHERE tmdb_id=$CHARLIE AND type='movie'   AND season=-1 AND episode=-1;
UPDATE availability_items SET candidate_magnet='$(magnet "$E1_ALT")', candidate_info_hash='$E1_ALT', candidate_size=20971520, candidate_seeders=55, candidate_indexer='rig-alt', candidate_source='rig-alt', status='available', checked_at=$now, next_check_at=$future WHERE tmdb_id=$SERIES  AND type='episode' AND season=1  AND episode=$EP1;
UPDATE availability_items SET candidate_magnet='$(magnet "$E2_ALT")', candidate_info_hash='$E2_ALT', candidate_size=20971520, candidate_seeders=55, candidate_indexer='rig-alt', candidate_source='rig-alt', status='available', checked_at=$now, next_check_at=$future WHERE tmdb_id=$SERIES  AND type='episode' AND season=1  AND episode=$EP2;
SQL

echo '[7] MOVIE unshared reject (Alpha): alternate materialises, old file REMOVED'
reject_current "movie_$ALPHA" 'alpha-reject'
ALPHA_NEW=$(state_stub "$ALPHA" movie -1 -1)
[ -n "$ALPHA_NEW" ] || fail 'Alpha missing materialised_state after reject'
[ "$ALPHA_NEW" != "$ALPHA_OLD" ] || fail "Alpha reject did not switch stub (still $ALPHA_NEW)"
reason=$(sqlite3 "$PHDB" "SELECT reason FROM magnet_failure_cache WHERE tmdb_id=$ALPHA AND type='movie' AND info_hash='$A_CUR' LIMIT 1;")
[ "$reason" = "operator_rejected" ] || fail "Alpha: expected operator_rejected failure row, got '$reason'"
assert_absent "$ALPHA_OLD" 'alpha-unshared-old-stub'
removed_logged "$ALPHA_OLD" || fail "Alpha: mock did not log removal of $ALPHA_OLD"
assert_real_source "$ALPHA_ID" "$MOVIES_SUB" 'alpha-after-reject'
assert_stream_opens "$ALPHA_ID" mkv 'alpha-after-reject'

echo '[8] MOVIE shared reject (Charlie): alternate materialises, old file PRESERVED'
reject_current "movie_$CHARLIE" 'charlie-reject'
CHARLIE_NEW=$(state_stub "$CHARLIE" movie -1 -1)
[ -n "$CHARLIE_NEW" ] || fail 'Charlie missing materialised_state after reject'
[ "$CHARLIE_NEW" != "$CHARLIE_OLD" ] || fail "Charlie reject did not switch stub (still $CHARLIE_NEW)"
assert_present "$CHARLIE_OLD" 'charlie-shared-old-stub'
if removed_logged "$CHARLIE_OLD"; then fail "Charlie: shared stub $CHARLIE_OLD was removed (guard failed)"; fi
ghost_alive=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM materialised_state WHERE tmdb_id=$GHOST_MOVIE AND type='movie';")
[ "$ghost_alive" = "1" ] || fail 'Charlie: ghost movie reference vanished'
assert_real_source "$CHARLIE_ID" "$MOVIES_SUB" 'charlie-after-reject'
assert_stream_opens "$CHARLIE_ID" mkv 'charlie-after-reject'

echo '[9] EPISODE unshared reject (Delta S01E01): alternate materialises, old file REMOVED'
reject_current "episode_${SERIES}_s01e01" 'ep1-reject'
EP1_NEW=$(state_stub "$SERIES" episode 1 "$EP1")
[ -n "$EP1_NEW" ] || fail 'S01E01 missing materialised_state after reject'
[ "$EP1_NEW" != "$EP1_OLD" ] || fail "S01E01 reject did not switch stub (still $EP1_NEW)"
reason=$(sqlite3 "$PHDB" "SELECT reason FROM magnet_failure_cache WHERE tmdb_id=$SERIES AND type='episode' AND season=1 AND episode=$EP1 AND info_hash='$E1_CUR' LIMIT 1;")
[ "$reason" = "operator_rejected" ] || fail "S01E01: expected operator_rejected failure row, got '$reason'"
assert_absent "$EP1_OLD" 'ep1-unshared-old-stub'
removed_logged "$EP1_OLD" || fail "S01E01: mock did not log removal of $EP1_OLD"
assert_real_source "$EP1_ID" "$TV_SUB" 'ep1-after-reject'
assert_stream_opens "$EP1_ID" mkv 'ep1-after-reject'

echo '[10] EPISODE shared reject (Delta S01E02): alternate materialises, old file PRESERVED'
reject_current "episode_${SERIES}_s01e02" 'ep2-reject'
EP2_NEW=$(state_stub "$SERIES" episode 1 "$EP2")
[ -n "$EP2_NEW" ] || fail 'S01E02 missing materialised_state after reject'
[ "$EP2_NEW" != "$EP2_OLD" ] || fail "S01E02 reject did not switch stub (still $EP2_NEW)"
assert_present "$EP2_OLD" 'ep2-shared-old-stub'
if removed_logged "$EP2_OLD"; then fail "S01E02: shared stub $EP2_OLD was removed (guard failed)"; fi
ghost_alive=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM materialised_state WHERE tmdb_id=$GHOST_SERIES AND type='episode';")
[ "$ghost_alive" = "1" ] || fail 'S01E02: ghost episode reference vanished'
assert_real_source "$EP2_ID" "$TV_SUB" 'ep2-after-reject'
assert_stream_opens "$EP2_ID" mkv 'ep2-after-reject'

echo 'CHANNEL_SOURCE_SAFETY_OK'
