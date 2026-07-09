#!/bin/bash
# Discovery-from-empty end-to-end synthetic-provenance test.
#
# Proves that a *pristine empty* phantom.db, driven only by the deterministic
# TMDB + gostream mocks (zero operator PII), converges through REAL discovery
# into the browsable catalogue/channel shape the downstream channel scenarios
# (35 movie e2e, 36 TV episode e2e) build on, and exports the resulting
# phantom.db as the reusable synthetic fixture the migration methodology
# (P3 Stage 2/3: db-migration-script, migration-rig) seeds from.
#
# What it verifies, from an empty DB:
# - schema is created at the current version (v12) from scratch
# - the discovery task populates the deterministic catalogue core (3 movies +
#   2 series, all synthetic high tmdb ids), warms tmdb_metadata, enqueues movie
#   availability + series expansion  -- and NOTHING else (no PII, no legacy
#   __phantom_tmdb sentinel, no real tmdb ids leaked from the prod clone)
# - the background worker expands each series into its 8 episodes via the TMDB
#   mock (series_episode_catalogue / episode availability), deterministically
# - MOVIE + TV parity: after seeding synthetic availability (as 35/36 do),
#   a phantom movie (Alpha) and a phantom episode (Delta S01E01) both surface
#   in their channels with tmdb-keyed identity (movie_<tmdb> /
#   episode_<tmdb>_sNNeNN + ProviderIds.Tmdb) and native RequiresOpening
#   PlaybackInfo -- the exact precondition shape 35/36 consume
# - the discovered phantom.db is exported to a fixture dir + manifest
#
# The full live run rides the shared Nodepool-gated Zuul rig (it needs the
# patched Jellyfin server); locally the mocks + `bash -n` validate the
# deterministic discovery surface. Rig :18096 only, never prod. trap-clean.
set -euo pipefail

ROOT=${PHANTOM_REPO_ROOT:-/home/spencer/git-repos/spencerharmon/phantom-library}
RIG=/tmp/jf-rig
API=http://localhost:18096
TOK=testtoken00000000000000000000000
PHDB=/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db
JDB=/tmp/jf-test/data/data/jellyfin.db
LOG=$RIG/logs/scenario-discovery-from-empty.log
FIXTURE_DIR=$RIG/fixtures/discovery-from-empty
SCHEMA_EXPECT=12

# Synthetic mock fixture ids (see tmdb-mock.py). All >= 99000000 by design so
# they can never collide with a real tmdb id in the operator's prod clone.
ALPHA=99000001      # movie
BRAVO=99000002      # movie (also has real gostream files)
CHARLIE=99000003    # movie
DELTA=99100001      # series (1 season, 8 episodes)
ECHO=99100002       # series (1 season, 8 episodes)
SYNTHETIC_FLOOR=99000000
DELTA_EPISODES=8

mkdir -p "$RIG/logs"
exec > >(tee "$LOG") 2>&1
cd "$ROOT"

# ---------------------------------------------------------------- trap-clean
cleanup() {
  local rc=$?
  echo "[cleanup] tearing down rig (rc=$rc)"
  bash tools/rig-scenarios/rig-down.sh >/dev/null 2>&1 || true
  rm -f /tmp/discovery-from-empty.*.json /tmp/discovery-from-empty.*.tmp 2>/dev/null || true
  # NB: $FIXTURE_DIR is the deliverable and is deliberately preserved.
  exit $rc
}
trap cleanup EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }
api() { curl -sS --fail -H "X-Emby-Token: $TOK" "$@"; }
api_post() { curl -sS --fail -X POST -H "X-Emby-Token: $TOK" "$@"; }

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

find_channel_id() {
  local name=$1
  python3 - "$name" <<'PY'
import json,sys
name=sys.argv[1]
j=json.load(open('/tmp/channels.json'))
items=j.get('Items', j if isinstance(j,list) else [])
for x in items:
    if x.get('Name') == name:
        print(x['Id'])
        raise SystemExit(0)
raise SystemExit(1)
PY
}

# Native-open PlaybackInfo assertion: a visible-but-unmaterialised phantom must
# expose exactly one RequiresOpening source with the Phantom open token and NO
# splash path. Shared by the movie + episode parity checks.
assert_opening_playback_info() {
  local id=$1 label=$2 token_suffix=$3
  echo "[opening-playback-info] $label id=$id"
  api "$API/Items/$id/PlaybackInfo" -o /tmp/pb-open.json || fail "$label PlaybackInfo HTTP error"
  python3 - "$label" "$token_suffix" <<'PY'
import json,sys,uuid
label,token_suffix=sys.argv[1],sys.argv[2]
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
    raise SystemExit(f'{label}: expected RequiresOpening native-open source')
if not (s.get('OpenToken') or '').endswith(token_suffix):
    raise SystemExit(f'{label}: expected Phantom open token ending {token_suffix!r}, got {s.get("OpenToken")!r}')
if s.get('Path') not in (None, ''):
    raise SystemExit(f'{label}: expected no splash path, got {s.get("Path")!r}')
uuid.UUID(s.get('Id'))
PY
}

# =====================================================================
echo '[0] build plugin + start reset (EMPTY) rig'
read -r -a BUILD_ARGS <<< "${PHANTOM_DOTNET_BUILD_ARGS:-}"
dotnet build -c Release "${BUILD_ARGS[@]}" >/tmp/phantom-discovery-empty-build.log
bash tools/rig-scenarios/rig-up.sh --reset

for _ in $(seq 1 60); do
  [ -f "$PHDB" ] && schema=$(sqlite3 "$PHDB" 'PRAGMA user_version;' 2>/dev/null || echo 0) || schema=0
  [ "$schema" = "$SCHEMA_EXPECT" ] && break
  sleep 1
done
[ "${schema:-0}" = "$SCHEMA_EXPECT" ] || fail "phantom schema not v$SCHEMA_EXPECT, got ${schema:-0}"

# =====================================================================
echo '[1] precondition: phantom.db is pristine EMPTY'
# The discovery task has an interval trigger (not startup), and the probe
# worker has nothing to claim, so these are stable until we trigger discovery.
for tbl in catalogue_items tmdb_metadata availability_items series_expansion_state \
           series_episode_catalogue tmdb_episode_cache materialised_state; do
  n=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM $tbl;" 2>/dev/null || echo ERR)
  echo "  empty[$tbl]=$n"
  [ "$n" = "0" ] || fail "expected empty $tbl at start, got $n"
done

# =====================================================================
echo '[2] trigger REAL discovery + assert the deterministic catalogue core'
api "$API/ScheduledTasks" -o /tmp/tasks.json
TASK_ID=$(find_task_id) || fail 'discovery task not found'
api_post "$API/ScheduledTasks/Running/$TASK_ID" -o /tmp/task-run.out || fail 'failed to start discovery task'
wait_task_idle "$TASK_ID"

# These counts are owned by discovery and are NOT mutated by the background
# probe worker (it only flips availability status / grows episode rows), so
# they are race-free to assert immediately.
cat_total=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM catalogue_items;")
cat_movie=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM catalogue_items WHERE type='movie';")
cat_series=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM catalogue_items WHERE type='series';")
meta_total=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM tmdb_metadata;")
expand_total=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM series_expansion_state;")
mov_avail=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM availability_items WHERE type='movie' AND season=-1 AND episode=-1;")
echo "  catalogue total=$cat_total movie=$cat_movie series=$cat_series meta=$meta_total expand=$expand_total movie_avail=$mov_avail"
[ "$cat_total" = "5" ]  || fail "expected 5 catalogue items, got $cat_total"
[ "$cat_movie" = "3" ]  || fail "expected 3 catalogue movies, got $cat_movie"
[ "$cat_series" = "2" ] || fail "expected 2 catalogue series, got $cat_series"
[ "$meta_total" = "5" ] || fail "expected 5 tmdb_metadata rows, got $meta_total"
[ "$expand_total" = "2" ] || fail "expected 2 series_expansion_state rows, got $expand_total"
[ "$mov_avail" = "3" ]  || fail "expected 3 movie availability rows, got $mov_avail"

# Exact synthetic id set present (movie + series parity), and the discover
# source bit is set on every row.
ids_movie=$(sqlite3 "$PHDB" "SELECT group_concat(tmdb_id) FROM (SELECT tmdb_id FROM catalogue_items WHERE type='movie' ORDER BY tmdb_id);")
ids_series=$(sqlite3 "$PHDB" "SELECT group_concat(tmdb_id) FROM (SELECT tmdb_id FROM catalogue_items WHERE type='series' ORDER BY tmdb_id);")
echo "  movie_ids=$ids_movie series_ids=$ids_series"
[ "$ids_movie" = "$ALPHA,$BRAVO,$CHARLIE" ] || fail "unexpected movie id set: $ids_movie"
[ "$ids_series" = "$DELTA,$ECHO" ] || fail "unexpected series id set: $ids_series"

# Zero-PII / synthetic provenance: every discovered id is a synthetic high id.
below_floor=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM catalogue_items WHERE tmdb_id < $SYNTHETIC_FLOOR;")
[ "$below_floor" = "0" ] || fail "non-synthetic (real) tmdb id leaked into catalogue: $below_floor rows < $SYNTHETIC_FLOOR"
meta_below_floor=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM tmdb_metadata WHERE tmdb_id < $SYNTHETIC_FLOOR;")
[ "$meta_below_floor" = "0" ] || fail "non-synthetic tmdb id leaked into tmdb_metadata: $meta_below_floor rows"

# The discover source bit (2) must be set on every catalogue row.
no_discover_bit=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM catalogue_items WHERE (source_mask & 2) = 0;")
[ "$no_discover_bit" = "0" ] || fail "catalogue rows missing discover source bit: $no_discover_bit"

# Metadata titles are the deterministic synthetic titles (no fuzzy-matchable
# real strings), proving the plugin's own stamp -- not a scanner rescue.
sqlite3 "$PHDB" "SELECT tmdb_id||'='||title FROM tmdb_metadata ORDER BY tmdb_id;" > /tmp/meta-titles.txt
python3 - <<PY
expected={
  "$ALPHA":"Phantom Rig Alpha","$BRAVO":"Phantom Rig Bravo","$CHARLIE":"Phantom Rig Charlie",
  "$DELTA":"Phantom Rig Delta","$ECHO":"Phantom Rig Echo",
}
got={}
for line in open('/tmp/meta-titles.txt'):
    line=line.strip()
    if not line: continue
    k,v=line.split('=',1)
    got[k]=v
for k,v in expected.items():
    if got.get(k)!=v:
        raise SystemExit(f'metadata title mismatch for {k}: expected {v!r}, got {got.get(k)!r}')
print('  metadata titles OK:', got)
PY

# =====================================================================
echo '[3] wait for deterministic series expansion (TMDB-mock driven, no network)'
# The background worker expands each enqueued series into its episodes using
# ONLY the TMDB mock (GetSeries/GetSeason) -- no Torrentio. Endpoint is fixed:
# 8 episodes per series. Poll to convergence.
for _ in $(seq 1 120); do
  delta_eps=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM series_episode_catalogue WHERE series_tmdb_id=$DELTA;" 2>/dev/null || echo 0)
  echo "  delta_episodes=$delta_eps"
  [ "$delta_eps" -ge "$DELTA_EPISODES" ] && break
  sleep 2
done
delta_eps=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM series_episode_catalogue WHERE series_tmdb_id=$DELTA;")
[ "$delta_eps" = "$DELTA_EPISODES" ] || fail "expected $DELTA_EPISODES Delta episodes, got $delta_eps"
delta_epcache=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM tmdb_episode_cache WHERE series_tmdb_id=$DELTA;")
[ "$delta_epcache" = "$DELTA_EPISODES" ] || fail "expected $DELTA_EPISODES Delta episode-cache rows, got $delta_epcache"
delta_ep_avail=$(sqlite3 "$PHDB" "SELECT COUNT(*) FROM availability_items WHERE tmdb_id=$DELTA AND type='episode';")
[ "$delta_ep_avail" = "$DELTA_EPISODES" ] || fail "expected $DELTA_EPISODES Delta episode availability rows, got $delta_ep_avail"

# =====================================================================
echo '[4] seed synthetic availability -> make Alpha (movie) + Delta S01E01 (episode) visible'
# Mirrors 35/36: INSERT a synthetic magnet + availability=available with a
# far-future next_check_at so the probe worker never re-claims/flips these
# rows (deterministic visibility). All values synthetic -> zero PII.
now=$(date +%s)
sqlite3 "$PHDB" <<SQL
INSERT OR REPLACE INTO magnet_cache
(tmdb_id, imdb_id, type, season, episode, preset, magnet, info_hash, size, seeders, indexer, cached_at, ttl_seconds, source)
VALUES
($ALPHA, 'tt99000001', 'movie', 0, 0, 'gostream-default',
 'magnet:?xt=urn:btih:4141414141414141414141414141414141414141&dn=Phantom+Rig+Alpha',
 '4141414141414141414141414141414141414141', 10485760, 100, 'rig-cache', $now, 86400, 'rig'),
($DELTA, 'tt99100001', 'episode', 1, 1, 'gostream-default',
 'magnet:?xt=urn:btih:4242424242424242424242424242424242424242&dn=Phantom+Rig+Delta+S01E01',
 '4242424242424242424242424242424242424242', 10485760, 100, 'rig-cache', $now, 86400, 'rig');
INSERT OR REPLACE INTO availability_items
(tmdb_id, type, season, episode, status, checked_at, next_check_at, candidate_magnet, candidate_info_hash, candidate_size, candidate_seeders, candidate_indexer, candidate_source)
VALUES
($ALPHA, 'movie', -1, -1, 'available', $now, $((now + 604800)),
 'magnet:?xt=urn:btih:4141414141414141414141414141414141414141&dn=Phantom+Rig+Alpha',
 '4141414141414141414141414141414141414141', 10485760, 100, 'rig-cache', 'rig'),
($DELTA, 'episode', 1, 1, 'available', $now, $((now + 604800)),
 'magnet:?xt=urn:btih:4242424242424242424242424242424242424242&dn=Phantom+Rig+Delta+S01E01',
 '4242424242424242424242424242424242424242', 10485760, 100, 'rig-cache', 'rig');
INSERT OR REPLACE INTO plugin_meta(key,value) VALUES('channel_dataversion_movies', '$now-rig-seed');
INSERT OR REPLACE INTO plugin_meta(key,value) VALUES('channel_dataversion_shows', '$now-rig-seed');
SQL

# =====================================================================
echo '[5] MOVIE parity: Alpha surfaces in Phantom Movies as tmdb-keyed phantom'
api "$API/Channels" -o /tmp/channels.json
MOVIES_CH=$(find_channel_id 'Phantom Movies') || fail 'Phantom Movies channel not found'
SHOWS_CH=$(find_channel_id 'Phantom Shows') || fail 'Phantom Shows channel not found'

api "$API/Channels/$MOVIES_CH/Items?Fields=Tags,ProviderIds,MediaSources,Path,Overview,ProductionYear,ExternalId&Limit=50" -o /tmp/movies.json
ALPHA_ID=$(python3 - "$ALPHA" <<'PY'
import json,sys
tmdb=sys.argv[1]
j=json.load(open('/tmp/movies.json'))
for x in j.get('Items', []):
    if (x.get('ProviderIds') or {}).get('Tmdb')==tmdb or x.get('ExternalId')==f'movie_{tmdb}':
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'Alpha movie not visible in Phantom Movies channel'
python3 - "$ALPHA_ID" "$ALPHA" <<'PY'
import json,sys
id,tmdb=sys.argv[1],sys.argv[2]
j=json.load(open('/tmp/movies.json'))
x=next(i for i in j['Items'] if i['Id']==id)
print('  ALPHA=', x.get('Name'), x.get('ExternalId'), x.get('ProviderIds'), x.get('Tags'))
if x.get('ExternalId') != f'movie_{tmdb}':
    raise SystemExit(f'Alpha ExternalId expected movie_{tmdb}, got {x.get("ExternalId")!r}')
if (x.get('ProviderIds') or {}).get('Tmdb') != tmdb:
    raise SystemExit(f'Alpha ProviderIds.Tmdb expected {tmdb}, got {x.get("ProviderIds")}')
if 'phantom' not in (x.get('Tags') or []):
    raise SystemExit(f'Alpha should be a phantom, tags={x.get("Tags")}')
src=(x.get('MediaSources') or [{}])[0]
if not src.get('RequiresOpening'):
    raise SystemExit(f'Alpha should start RequiresOpening, got {src}')
if src.get('Path') not in (None, ''):
    raise SystemExit(f'Alpha should not start on splash, got {src.get("Path")!r}')
PY
assert_opening_playback_info "$ALPHA_ID" 'phantom-alpha' '_phantom:movie_99000001'

# =====================================================================
echo '[6] TV parity: Delta series -> Season 1 -> S01E01 surfaces as tmdb-keyed phantom'
api "$API/Channels/$SHOWS_CH/Items?Fields=Tags,ProviderIds,MediaSources,Path,Overview,ProductionYear,ExternalId&Limit=50" -o /tmp/series.json
SERIES_ID=$(python3 - "$DELTA" <<'PY'
import json,sys
tmdb=sys.argv[1]
j=json.load(open('/tmp/series.json'))
for x in j.get('Items', []):
    if (x.get('ProviderIds') or {}).get('Tmdb')==tmdb or x.get('ExternalId')==f'series_{tmdb}':
        if x.get('Type') not in ('Series','Folder'):
            raise SystemExit(f'series tile should be navigation folder, got Type={x.get("Type")}')
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'Delta series not visible in Phantom Shows channel'

api "$API/Channels/$SHOWS_CH/Items?FolderId=$SERIES_ID&Fields=Tags,ProviderIds,MediaSources,Path,Overview,ExternalId,ProductionYear&Limit=50" -o /tmp/seasons.json
SEASON_ID=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/seasons.json'))
for x in j.get('Items', []):
    if x.get('Name') == 'Season 1':
        if x.get('Type') != 'Season':
            raise SystemExit(f'season should use native Season type, got {x.get("Type")}')
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'Delta Season 1 not found'

api "$API/Channels/$SHOWS_CH/Items?FolderId=$SEASON_ID&Fields=Tags,ProviderIds,MediaSources,Path,Overview,ExternalId&Limit=50" -o /tmp/episodes.json
EP_ID=$(python3 - "$DELTA" <<'PY'
import json,sys
tmdb=sys.argv[1]
j=json.load(open('/tmp/episodes.json'))
for x in j.get('Items', []):
    if x.get('ExternalId')==f'episode_{tmdb}_s01e01':
        print(x['Id']); raise SystemExit(0)
raise SystemExit(1)
PY
) || fail 'Delta S01E01 not found by tmdb-keyed external id'
python3 - "$EP_ID" "$DELTA" <<'PY'
import json,sys
id,tmdb=sys.argv[1],sys.argv[2]
j=json.load(open('/tmp/episodes.json'))
x=next(i for i in j['Items'] if i['Id']==id)
print('  EPISODE=', x.get('Name'), x.get('ExternalId'), x.get('Tags'))
if x.get('ExternalId') != f'episode_{tmdb}_s01e01':
    raise SystemExit(f'episode ExternalId expected episode_{tmdb}_s01e01, got {x.get("ExternalId")!r}')
if 'phantom' not in (x.get('Tags') or []):
    raise SystemExit(f'episode should be a phantom, tags={x.get("Tags")}')
src=(x.get('MediaSources') or [{}])[0]
if not src.get('RequiresOpening'):
    raise SystemExit(f'episode should start RequiresOpening, got {src}')
if src.get('Path') not in (None, ''):
    raise SystemExit(f'episode should not start on splash, got {src.get("Path")!r}')
PY
assert_opening_playback_info "$EP_ID" 'phantom-episode' '_phantom:episode_99100001_s01e01'

# =====================================================================
echo '[7] canonical-naming guard: no legacy __phantom_tmdb sentinel anywhere'
legacy=$(sqlite3 "$JDB" "SELECT COUNT(*) FROM BaseItems WHERE Path LIKE '%__phantom_tmdb%';")
[ "$legacy" = "0" ] || fail "legacy __phantom_tmdb sentinel present in $legacy BaseItems paths"
# Every phantom channel BaseItem must carry a synthetic tmdb-keyed external id
# (movie_/series_/season_/episode_ + a >= 99000000 id) -- no real prod ids.
python3 - <<PY
import sqlite3
floor=$SYNTHETIC_FLOOR
db=sqlite3.connect("$JDB")
rows=db.execute(
  "SELECT ExternalId FROM BaseItems "
  "WHERE ExternalId LIKE 'movie_%' OR ExternalId LIKE 'series_%' "
  "   OR ExternalId LIKE 'season_%' OR ExternalId LIKE 'episode_%'").fetchall()
import re
bad=[]
for (ext,) in rows:
    m=re.match(r'^(movie|series|season|episode)_(\d+)', ext or '')
    if not m or int(m.group(2)) < floor:
        bad.append(ext)
if bad:
    raise SystemExit(f'non-synthetic phantom channel external ids leaked: {bad[:10]}')
print(f'  phantom channel external ids all synthetic ({len(rows)} rows)')
PY

# =====================================================================
echo '[8] export reusable synthetic fixture (phantom.db + manifest)'
rm -rf "$FIXTURE_DIR"
mkdir -p "$FIXTURE_DIR"
sqlite3 "$PHDB" ".backup '$FIXTURE_DIR/phantom.db'"
[ -s "$FIXTURE_DIR/phantom.db" ] || fail 'fixture phantom.db export is empty'
fx_schema=$(sqlite3 "$FIXTURE_DIR/phantom.db" 'PRAGMA user_version;')
[ "$fx_schema" = "$SCHEMA_EXPECT" ] || fail "exported fixture schema v$fx_schema != v$SCHEMA_EXPECT"
python3 - <<PY
import json,sqlite3,time
db=sqlite3.connect("$FIXTURE_DIR/phantom.db")
def one(sql): return db.execute(sql).fetchone()[0]
manifest={
  "scenario":"41-discovery-from-empty",
  "generated_at":time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
  "provenance":"synthetic-mocks-only (tmdb-mock.py + gostream-mock.py); zero operator PII",
  "schema_version":int(one("PRAGMA user_version")),
  "synthetic_id_floor":$SYNTHETIC_FLOOR,
  "counts":{
    "catalogue_items":one("SELECT COUNT(*) FROM catalogue_items"),
    "catalogue_movies":one("SELECT COUNT(*) FROM catalogue_items WHERE type='movie'"),
    "catalogue_series":one("SELECT COUNT(*) FROM catalogue_items WHERE type='series'"),
    "tmdb_metadata":one("SELECT COUNT(*) FROM tmdb_metadata"),
    "series_episode_catalogue":one("SELECT COUNT(*) FROM series_episode_catalogue"),
    "availability_items":one("SELECT COUNT(*) FROM availability_items"),
    "materialised_state":one("SELECT COUNT(*) FROM materialised_state"),
  },
  "consumed_by":["35-channel-e2e-playback","36-channel-episode-e2e-playback",
                 "db-migration-script","migration-rig"],
}
json.dump(manifest, open("$FIXTURE_DIR/manifest.json","w"), indent=2, sort_keys=True)
print("  fixture:", json.dumps(manifest["counts"]))
PY
echo "  fixture written to $FIXTURE_DIR"

echo 'DISCOVERY_FROM_EMPTY_OK'
