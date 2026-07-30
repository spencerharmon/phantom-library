#!/usr/bin/env bash
# Regression test: the Home "Latest in Phantom <X>" rows must not deep-enumerate
# the phantom channels. Phantom channels intentionally do NOT implement
# ISupportsLatestMedia (operator decision 2026-06-28, Option 3), so
# GET /Users/{uid}/Items/Latest?ParentId=<phantomChannel> short-circuits in
# Jellyfin core's GetLatestChannelItemsInternal to an empty result instantly.
#
# Catches the 2026-06-28 regression where implementing ISupportsLatestMedia made
# RefreshLatestChannelItems call the channel's full GetChannelItems
# (series -> season -> build) on every Home load, hanging the Home screen on
# every client (web AND Xbox/native) for seconds-to-minutes on production data.
#
# Movie/TV parity: asserts both Phantom Movies and Phantom Shows channels.
set -euo pipefail

ROOT=${PHANTOM_REPO_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
source "$ROOT/tools/rig-scenarios/rig-db.sh"
RIG=${PHANTOM_LATEST_RIG:-${PHANTOM_TTFB_RIG:?set PHANTOM_LATEST_RIG (or PHANTOM_TTFB_RIG) to an existing Jellyfin rig DB clone; this scenario never copies production DBs}}
JF_DATA=$RIG/data; JF_CFG=$RIG/config; JF_CACHE=$RIG/cache; JF_LOG=$RIG/log
API=http://localhost:18096
TOK=testtoken00000000000000000000000
PLUGIN_VERSION=0.3.0.0
PLUGIN_DIR=$JF_DATA/plugins/Jellyfin.Plugin.PhantomLibrary_$PLUGIN_VERSION
DLL=$ROOT/src/Jellyfin.Plugin.PhantomLibrary/bin/Release/net9.0/Jellyfin.Plugin.PhantomLibrary.dll
JF_DLL=$ROOT/jellyfin/Jellyfin.Server/bin/Release/net9.0/jellyfin.dll
PLUGIN_CFG=${PHANTOM_PLUGIN_CONFIG:-/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml}
# Suppressed Latest returns instantly; pre-fix it hung for seconds-to-minutes.
LATEST_MAX_SECONDS=${PHANTOM_LATEST_MAX_SECONDS:-3}
LOG_FILE=/tmp/phantom-channel-latest-suppressed.log

exec > >(tee "$LOG_FILE") 2>&1
cd "$ROOT"
fail() { echo "FAIL: $*" >&2; exit 1; }

stop_rig() {
  systemctl --user stop rig-latest.service >/dev/null 2>&1 || true
  systemctl --user reset-failed rig-latest.service >/dev/null 2>&1 || true
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

echo '[0] preflight'
[ -f "$JF_DLL" ] || fail "patched Jellyfin not built: $JF_DLL"
[ -r "$PLUGIN_CFG" ] || fail "plugin config missing/read-protected: $PLUGIN_CFG"
MSBUILDDISABLENODEREUSE=1 dotnet build -c Release -p:UseSharedCompilation=false --no-restore >/tmp/phantom-channel-latest-build.log
[ -f "$DLL" ] || fail "plugin DLL not built: $DLL"

echo '[1] prepare existing isolated rig DBs'
stop_rig
mkdir -p "$JF_DATA/data" "$JF_DATA/plugins/configurations/PhantomLibrary" "$JF_DATA/root/default" "$PLUGIN_DIR" "$JF_CFG" "$JF_CACHE" "$JF_LOG" "$RIG/tmp"
ensure_existing_rig_jellyfin_db "$JF_DATA/data/jellyfin.db"
migrate_existing_rig_phantom_db "$JF_DATA/plugins/configurations/PhantomLibrary/phantom.db" "$ROOT"
cp "$PLUGIN_CFG" "$JF_DATA/plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml"
sqlite3 "$JF_DATA/data/jellyfin.db" \
  "DELETE FROM ApiKeys WHERE Name='latest-rig' OR AccessToken='$TOK';
   INSERT INTO ApiKeys (DateCreated, DateLastActivity, Name, AccessToken)
   VALUES (datetime('now'), datetime('now'), 'latest-rig', '$TOK');"
cp "$DLL" "$PLUGIN_DIR/Jellyfin.Plugin.PhantomLibrary.dll"
cat > "$PLUGIN_DIR/meta.json" <<META
{"category":"Metadata","changelog":"latest suppressed","description":"latest suppressed","guid":"9e7a1f4c-2b5d-4e8f-9a3b-7c1d2e5f6a8b","name":"Phantom Library","overview":"latest suppressed","owner":"spencerharmon","targetAbi":"10.11.0.0","timestamp":"0001-01-01T00:00:00.0000000Z","version":"$PLUGIN_VERSION","status":"Active","autoUpdate":false,"assemblies":[]}
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
systemd-run --user --unit=rig-latest \
  --description='Phantom Latest suppression rig' \
  --working-directory="$JF_DATA" --setenv=TMPDIR="$RIG/tmp" \
  -- /usr/bin/dotnet "$JF_DLL" \
       --datadir "$JF_DATA" --configdir "$JF_CFG" \
       --cachedir "$JF_CACHE" --logdir "$JF_LOG" \
       --webdir /usr/share/jellyfin/web \
       --ffmpeg /usr/lib/jellyfin-ffmpeg/ffmpeg >/dev/null

for i in {1..120}; do
  code=$(curl -s --max-time 2 -H "X-Emby-Token: $TOK" -o /dev/null -w '%{http_code}' "$API/System/Info" 2>/dev/null || echo 000)
  [ "$code" = 200 ] && { echo "  jellyfin up in ${i}s"; break; }
  sleep 1
  [ "$i" = 120 ] && { journalctl --user -u rig-latest -n 120 --no-pager || true; fail 'Jellyfin rig did not start'; }
done

echo '[3] resolve user + phantom channel ids'
JUID=$(curl -sS --fail --max-time 10 -H "X-Emby-Token: $TOK" "$API/Users" | python3 -c 'import json,sys;print(json.load(sys.stdin)[0]["Id"])')
curl -sS --fail --max-time 20 -H "X-Emby-Token: $TOK" "$API/Channels" -o /tmp/latest-channels.json
PHM=$(python3 -c 'import json;print(next(i["Id"] for i in json.load(open("/tmp/latest-channels.json"))["Items"] if i["Name"]=="Phantom Movies"))')
PHS=$(python3 -c 'import json;print(next(i["Id"] for i in json.load(open("/tmp/latest-channels.json"))["Items"] if i["Name"]=="Phantom Shows"))')
echo "  user=$JUID movies=$PHM shows=$PHS"

echo '[4] /Items/Latest per phantom channel must be fast + empty (suppressed)'
check_latest() {
  local label=$1 pid=$2 out=/tmp/latest-$1.json meta=/tmp/latest-$1.meta code time
  curl -sS --max-time "$((LATEST_MAX_SECONDS + 30))" -H "X-Emby-Token: $TOK" \
    "$API/Users/$JUID/Items/Latest?ParentId=$pid&Limit=16&Fields=PrimaryImageAspectRatio" \
    -o "$out" -w '%{http_code} %{time_total}' > "$meta" || fail "$label Latest request failed (likely still deep-enumerating -> ISupportsLatestMedia re-added?)"
  code=$(cut -d' ' -f1 "$meta"); time=$(cut -d' ' -f2 "$meta")
  echo "  $label http=$code time=${time}s budget=${LATEST_MAX_SECONDS}s bytes=$(wc -c < "$out" 2>/dev/null || echo 0)"
  [ "$code" = 200 ] || fail "$label Latest HTTP $code"
  python3 -c "import sys;t=$time;m=$LATEST_MAX_SECONDS;sys.exit(f'$label Latest exceeded budget: {t}s > {m}s (channel still being deep-enumerated -> ISupportsLatestMedia re-added?)' if t>m else 0)"
  python3 - "$out" "$label" <<'PY'
import json,sys
path,label=sys.argv[1],sys.argv[2]
d=json.load(open(path))
items=d if isinstance(d,list) else d.get('Items',[])
if items:
    sys.exit(f'{label} Latest must be empty (phantom channels suppressed from Latest) but returned {len(items)} items')
print(f'  {label} suppressed OK (0 items)')
PY
}
check_latest movies "$PHM"
check_latest shows "$PHS"

echo 'CHANNEL_LATEST_SUPPRESSED_OK'
