#!/usr/bin/env bash
# Regression test: POST /Plugins/PhantomLibrary/States must stay fast on a
# production-shaped DB when the requested batch contains ordinary (non-phantom)
# library card ids — exactly the shape the browser badge overlay (phantomBadges.js)
# sends on every Home-screen load and 3s poll.
#
# Catches 2026-06-28 regression where any unresolved id forced
# BuildComputedChannelIdMapAsync to enumerate the entire visible phantom
# catalogue (~540k movie+episode rows) and MD5-hash each into a BaseItem guid on
# every request. Continue Watching + library view tiles are real, non-channel
# library items; before the fix they all fell through to that full scan, keeping
# the web loading indicator lit and Continue Watching slow to render.
#
# Movie/TV parity: the assembled batch mixes movie and episode card ids (Resume
# spans both) and phantom movie + episode channel ids, so both paths are covered.
set -euo pipefail

ROOT=${PHANTOM_REPO_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
source "$ROOT/tools/rig-scenarios/rig-db.sh"
RIG=${PHANTOM_BADGE_RIG:-${PHANTOM_TTFB_RIG:?set PHANTOM_BADGE_RIG (or PHANTOM_TTFB_RIG) to an existing Jellyfin rig DB clone; this scenario never copies production DBs}}
JF_DATA=$RIG/data
JF_CFG=$RIG/config
JF_CACHE=$RIG/cache
JF_LOG=$RIG/log
API=http://localhost:18096
TOK=testtoken00000000000000000000000
PLUGIN_VERSION=0.3.0.0
PLUGIN_DIR=$JF_DATA/plugins/Jellyfin.Plugin.PhantomLibrary_$PLUGIN_VERSION
DLL=$ROOT/src/Jellyfin.Plugin.PhantomLibrary/bin/Release/net9.0/Jellyfin.Plugin.PhantomLibrary.dll
JF_DLL=$ROOT/jellyfin/Jellyfin.Server/bin/Release/net9.0/jellyfin.dll
PLUGIN_CFG=${PHANTOM_PLUGIN_CONFIG:-/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml}
# Generous budget: a fresh full-catalogue scan on the operator's data takes
# multiple seconds; the fixed fast path is sub-second. 4s catches the regression
# while tolerating cold-start jitter.
STATES_MAX_SECONDS=${PHANTOM_BADGE_STATES_MAX_SECONDS:-4}
# Steady-state poll budget. phantomBadges.js re-POSTs this batch every 3s for the
# life of the Home screen. Pre-fix, every poll rebuilt the ~540k-row computed-id
# map (no skip for real cards, no cross-request cache), so sustained polls each
# cost seconds and kept the web loading indicator lit. The fix must keep polls
# sub-second.
STATES_POLL_MAX_SECONDS=${PHANTOM_BADGE_STATES_POLL_MAX_SECONDS:-1.5}
LOG_FILE=/tmp/phantom-channel-badge-states-perf.log

exec > >(tee "$LOG_FILE") 2>&1
cd "$ROOT"

fail() { echo "FAIL: $*" >&2; exit 1; }

stop_rig() {
  systemctl --user stop rig-badge-states.service >/dev/null 2>&1 || true
  systemctl --user reset-failed rig-badge-states.service >/dev/null 2>&1 || true
  ps -u "$USER" -o pid=,comm=,args= \
    | awk '$2 == "dotnet" && $0 ~ /jellyfin\.dll/ && $0 ~ /jf-channel-ttfb/ { print $1 }' \
    | xargs -r kill >/dev/null 2>&1 || true
}

cleanup() {
  stop_rig
  timeout 10s dotnet build-server shutdown >/dev/null 2>&1 || true
  ps -u "$USER" -o pid=,comm=,args= \
    | awk '$2 == "VBCSCompiler" || $2 == "testhost" || ($2 == "dotnet" && $0 ~ /MSBuild\.dll \/noautoresponse/) { print $1 }' \
    | xargs -r kill >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

now_mono() { python3 -c 'import time;print(time.monotonic())'; }

echo '[0] preflight'
[ -f "$JF_DLL" ] || fail "patched Jellyfin not built: $JF_DLL"
[ -r "$PLUGIN_CFG" ] || fail "plugin config missing/read-protected: $PLUGIN_CFG"

MSBUILDDISABLENODEREUSE=1 dotnet build -c Release -p:UseSharedCompilation=false --no-restore >/tmp/phantom-channel-badge-states-build.log
[ -f "$DLL" ] || fail "plugin DLL not built: $DLL"

echo '[1] prepare existing isolated rig DBs'
stop_rig
mkdir -p "$JF_DATA/data" "$JF_DATA/plugins/configurations/PhantomLibrary" "$JF_DATA/root/default" "$PLUGIN_DIR" "$JF_CFG" "$JF_CACHE" "$JF_LOG" "$RIG/tmp"
ensure_existing_rig_jellyfin_db "$JF_DATA/data/jellyfin.db"
migrate_existing_rig_phantom_db "$JF_DATA/plugins/configurations/PhantomLibrary/phantom.db" "$ROOT"
cp "$PLUGIN_CFG" "$JF_DATA/plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml"
sqlite3 "$JF_DATA/data/jellyfin.db" \
  "DELETE FROM ApiKeys WHERE Name='badge-states-rig' OR AccessToken='$TOK';
   INSERT INTO ApiKeys (DateCreated, DateLastActivity, Name, AccessToken)
   VALUES (datetime('now'), datetime('now'), 'badge-states-rig', '$TOK');"
cp "$DLL" "$PLUGIN_DIR/Jellyfin.Plugin.PhantomLibrary.dll"
cat > "$PLUGIN_DIR/meta.json" <<META
{"category":"Metadata","changelog":"badge states perf","description":"badge states perf","guid":"9e7a1f4c-2b5d-4e8f-9a3b-7c1d2e5f6a8b","name":"Phantom Library","overview":"badge states perf","owner":"spencerharmon","targetAbi":"10.11.0.0","timestamp":"0001-01-01T00:00:00.0000000Z","version":"$PLUGIN_VERSION","status":"Active","autoUpdate":false,"assemblies":[]}
META
cat > "$JF_CFG/network.xml" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration>
  <PublicHttpPort>18096</PublicHttpPort>
  <InternalHttpPort>18096</InternalHttpPort>
  <AutoDiscovery>false</AutoDiscovery>
</NetworkConfiguration>
EOF

echo '[2] start isolated Jellyfin rig'
systemd-run --user --unit=rig-badge-states \
  --description='Phantom badge States perf rig' \
  --working-directory="$JF_DATA" \
  --setenv=TMPDIR="$RIG/tmp" \
  -- /usr/bin/dotnet "$JF_DLL" \
       --datadir "$JF_DATA" --configdir "$JF_CFG" \
       --cachedir "$JF_CACHE" --logdir "$JF_LOG" \
       --webdir /usr/share/jellyfin/web \
       --ffmpeg /usr/lib/jellyfin-ffmpeg/ffmpeg >/dev/null

for i in {1..120}; do
  code=$(curl -s --max-time 2 -H "X-Emby-Token: $TOK" -o /dev/null -w '%{http_code}' "$API/Channels" 2>/dev/null || echo 000)
  if [ "$code" = 200 ]; then echo "  jellyfin up in ${i}s"; break; fi
  sleep 1
  [ "$i" = 120 ] && { journalctl --user -u rig-badge-states -n 120 --no-pager || true; fail 'Jellyfin rig did not start'; }
done

echo '[3] assemble a realistic Home-screen card-id batch'
JUID=$(curl -sS --fail --max-time 10 -H "X-Emby-Token: $TOK" "$API/Users" | python3 -c 'import json,sys;print(json.load(sys.stdin)[0]["Id"])')
echo "  user=$JUID"
curl -sS --fail --max-time 20 -H "X-Emby-Token: $TOK" "$API/Users/$JUID/Views" -o /tmp/badge-views.json
curl -sS --fail --max-time 30 -H "X-Emby-Token: $TOK" "$API/Users/$JUID/Items/Resume?Limit=20&MediaTypes=Video" -o /tmp/badge-resume.json
curl -sS --fail --max-time 20 -H "X-Emby-Token: $TOK" "$API/Channels" -o /tmp/badge-channels.json
PHM=$(python3 -c 'import json;print(next(i["Id"] for i in json.load(open("/tmp/badge-channels.json"))["Items"] if i["Name"]=="Phantom Movies"))')
PHS=$(python3 -c 'import json;print(next(i["Id"] for i in json.load(open("/tmp/badge-channels.json"))["Items"] if i["Name"]=="Phantom Shows"))')
curl -sS --fail --max-time 90 -H "X-Emby-Token: $TOK" "$API/Channels/$PHM/Items?Limit=10&Fields=ExternalId" -o /tmp/badge-phmovies.json
curl -sS --fail --max-time 90 -H "X-Emby-Token: $TOK" "$API/Channels/$PHS/Items?Limit=10&Fields=ExternalId" -o /tmp/badge-phshows.json

python3 - <<'PY'
import json
ids=[]
def take(path,key='Items'):
    try: d=json.load(open(path))
    except Exception: return []
    items=d.get(key, d) if isinstance(d,dict) else d
    return [i['Id'] for i in items if isinstance(i,dict) and i.get('Id')]
views=take('/tmp/badge-views.json')
resume=take('/tmp/badge-resume.json')
phm=take('/tmp/badge-phmovies.json')
phs=take('/tmp/badge-phshows.json')
batch=views+resume+phm+phs
json.dump({'ids':batch,'views':views,'resume':resume,'phantom':phm+phs}, open('/tmp/badge-batch.json','w'))
print(f'  views={len(views)} resume={len(resume)} phantom_movies={len(phm)} phantom_shows={len(phs)} total={len(batch)}')
PY
[ "$(python3 -c 'import json;print(len(json.load(open("/tmp/badge-batch.json"))["ids"]))')" -gt 0 ] || fail 'assembled an empty card-id batch'

echo '[4] POST /States like phantomBadges.js (initial + two polls), measure latency'
post_states() {
  local label=$1 budget=$2 out=/tmp/badge-states-$1.json start end elapsed code
  start=$(now_mono)
  code=$(curl -sS --max-time "$((STATES_MAX_SECONDS + 20))" -H "X-Emby-Token: $TOK" \
    -H 'Content-Type: application/json' \
    --data @<(python3 -c 'import json;print(json.dumps({"ids":json.load(open("/tmp/badge-batch.json"))["ids"]}))') \
    "$API/Plugins/PhantomLibrary/States" -o "$out" -w '%{http_code}' || echo 000)
  end=$(now_mono)
  elapsed=$(python3 -c "print(f'{$end - $start:.3f}')")
  echo "  $label http=$code elapsed=${elapsed}s budget=${budget}s bytes=$(wc -c < "$out" 2>/dev/null || echo 0)"
  [ "$code" = 200 ] || fail "$label /States HTTP $code"
  python3 -c "import sys;e=$elapsed;m=$budget;sys.exit(f'$label /States exceeded budget: {e:.3f}s > {m}s' if e>m else 0)"
}
# Warm call (cold caches) tolerates a one-time build; the steady-state polls
# badges.js actually repeats must stay sub-second.
post_states warm "$STATES_MAX_SECONDS"
post_states poll1 "$STATES_POLL_MAX_SECONDS"
post_states poll2 "$STATES_POLL_MAX_SECONDS"

echo '[5] correctness: phantom cards get a state, library view folders are omitted'
python3 - <<'PY'
import json,sys
batch=json.load(open('/tmp/badge-batch.json'))
res=json.load(open('/tmp/badge-states-poll2.json'))
phantom=batch['phantom']; views=batch['views']
graded=[g for g in phantom if g in res]
if phantom and not graded:
    sys.exit(f'no phantom card received a badge state; phantom={phantom[:3]} response_keys={list(res)[:5]}')
leaked=[g for g in views if g in res]
if leaked:
    sys.exit(f'library view folders must not be badged but got states: {leaked}')
print(f'  phantom_graded={len(graded)}/{len(phantom)} states={sorted(set(res.values()))}')
PY

echo 'CHANNEL_BADGE_STATES_PERF_OK'
