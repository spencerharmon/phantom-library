#!/usr/bin/env bash
# Regression test: Phantom channel roots must return quickly on a production-shaped DB.
# Catches 2026-06-25 regression where browse-time FFprobe/audio probing made
# /Channels/{id}/Items?Limit=1 hang behind thousands of root media-source probes.
set -euo pipefail

ROOT=${PHANTOM_REPO_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
RIG=${PHANTOM_TTFB_RIG:-/var/tmp/jf-channel-ttfb}
USE_EXISTING_RIG=${PHANTOM_TTFB_USE_EXISTING_RIG:-0}
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
PROD_JDB=${PROD_JELLYFIN_DB:-/var/lib/jellyfin/data/jellyfin.db}
PROD_PHDB=${PROD_PHANTOM_DB:-/var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db}
PROD_CFG=${PROD_PHANTOM_CONFIG:-/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml}
MOVIES_MAX_SECONDS=${PHANTOM_TTFB_MOVIES_MAX_SECONDS:-30}
SHOWS_MAX_SECONDS=${PHANTOM_TTFB_SHOWS_MAX_SECONDS:-15}
LOG_FILE=/tmp/phantom-channel-root-ttfb.log

exec > >(tee "$LOG_FILE") 2>&1
cd "$ROOT"

fail() { echo "FAIL: $*" >&2; exit 1; }

stop_rig() {
  systemctl --user stop rig-channel-ttfb.service >/dev/null 2>&1 || true
  systemctl --user reset-failed rig-channel-ttfb.service >/dev/null 2>&1 || true
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

require_readable() {
  [ -r "$1" ] || fail "required readable file missing: $1"
}

find_channel_id() {
  local name=$1
  python3 - "$name" <<'PY'
import json,sys
name=sys.argv[1]
j=json.load(open('/tmp/ttfb-channels.json'))
for item in j.get('Items', []):
    if item.get('Name') == name:
        print(item['Id'])
        raise SystemExit(0)
raise SystemExit(f'missing channel {name}')
PY
}

measure_channel() {
  local label=$1 id=$2 max_seconds=$3 out=/tmp/ttfb-$1.json code start end elapsed count total
  start=$(python3 - <<'PY'
import time
print(time.monotonic())
PY
)
  code=$(curl -sS --max-time "$((max_seconds + 15))" -H "X-Emby-Token: $TOK" \
    "$API/Channels/$id/Items?Limit=1&Fields=ProviderIds,MediaSources,Tags" \
    -o "$out" -w '%{http_code}' || echo 000)
  end=$(python3 - <<'PY'
import time
print(time.monotonic())
PY
)
  elapsed=$(python3 - "$start" "$end" <<'PY'
import sys
print(f'{float(sys.argv[2]) - float(sys.argv[1]):.3f}')
PY
)
  echo "$label http=$code elapsed=${elapsed}s max=${max_seconds}s bytes=$(wc -c < "$out" 2>/dev/null || echo 0)"
  [ "$code" = 200 ] || fail "$label root browse HTTP $code"
  python3 - "$out" "$label" <<'PY'
import json,sys
path,label=sys.argv[1],sys.argv[2]
j=json.load(open(path))
items=j.get('Items') or []
total=j.get('TotalRecordCount')
print(f'{label} items={len(items)} total={total}')
if len(items) == 0:
    raise SystemExit(f'{label} returned no items')
if not isinstance(total, int) or total <= 0:
    raise SystemExit(f'{label} invalid TotalRecordCount={total!r}')
PY
  python3 - "$elapsed" "$max_seconds" "$label" <<'PY'
import sys
elapsed=float(sys.argv[1]); max_seconds=float(sys.argv[2]); label=sys.argv[3]
if elapsed > max_seconds:
    raise SystemExit(f'{label} root browse exceeded TTFB budget: {elapsed:.3f}s > {max_seconds:.3f}s')
PY
}

echo '[0] preflight'
require_readable "$PROD_JDB"
require_readable "$PROD_PHDB"
require_readable "$PROD_CFG"
[ -f "$JF_DLL" ] || fail "patched Jellyfin not built: $JF_DLL"

MSBUILDDISABLENODEREUSE=1 dotnet build -c Release -p:UseSharedCompilation=false --no-restore >/tmp/phantom-channel-root-ttfb-build.log
[ -f "$DLL" ] || fail "plugin DLL not built: $DLL"

echo '[1] prepare isolated rig DBs'
stop_rig
if [ "$USE_EXISTING_RIG" = "1" ]; then
  mkdir -p "$JF_DATA/data" "$JF_DATA/plugins/configurations/PhantomLibrary" "$JF_DATA/root/default" "$PLUGIN_DIR" "$JF_CFG" "$JF_CACHE" "$JF_LOG" "$RIG/tmp"
  [ -s "$JF_DATA/data/jellyfin.db" ] || fail "existing jellyfin DB missing/empty: $JF_DATA/data/jellyfin.db"
  [ -s "$JF_DATA/plugins/configurations/PhantomLibrary/phantom.db" ] || fail "existing phantom DB missing/empty: $JF_DATA/plugins/configurations/PhantomLibrary/phantom.db"
else
  rm -rf "$RIG"
  mkdir -p "$JF_DATA/data" "$JF_DATA/plugins/configurations/PhantomLibrary" "$JF_DATA/root/default" "$PLUGIN_DIR" "$JF_CFG" "$JF_CACHE" "$JF_LOG" "$RIG/tmp"
  sqlite3 "$PROD_JDB" ".backup '$JF_DATA/data/jellyfin.db'"
  sqlite3 "$PROD_PHDB" ".backup '$JF_DATA/plugins/configurations/PhantomLibrary/phantom.db'"
  [ -s "$JF_DATA/data/jellyfin.db" ] || fail "jellyfin DB clone is empty: $JF_DATA/data/jellyfin.db"
  [ -s "$JF_DATA/plugins/configurations/PhantomLibrary/phantom.db" ] || fail "phantom DB clone is empty: $JF_DATA/plugins/configurations/PhantomLibrary/phantom.db"
  cp -r /var/lib/jellyfin/root/default/* "$JF_DATA/root/default/" 2>/dev/null || true
fi
chmod u+rw "$JF_DATA/data/jellyfin.db" "$JF_DATA/plugins/configurations/PhantomLibrary/phantom.db"
cp "$PROD_CFG" "$JF_DATA/plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml"
scripts/migrate-source-candidates-v12.sh "$JF_DATA/plugins/configurations/PhantomLibrary/phantom.db" >/tmp/phantom-channel-root-ttfb-v12.log 2>&1 || true
scripts/migrate-source-validation-v14.sh "$JF_DATA/plugins/configurations/PhantomLibrary/phantom.db" >/tmp/phantom-channel-root-ttfb-v14.log
sqlite3 "$JF_DATA/data/jellyfin.db" \
  "DELETE FROM ApiKeys WHERE Name='channel-ttfb-rig' OR AccessToken='$TOK';
   INSERT INTO ApiKeys (DateCreated, DateLastActivity, Name, AccessToken)
   VALUES (datetime('now'), datetime('now'), 'channel-ttfb-rig', '$TOK');"
cp "$DLL" "$PLUGIN_DIR/Jellyfin.Plugin.PhantomLibrary.dll"
cat > "$PLUGIN_DIR/meta.json" <<META
{"category":"Metadata","changelog":"channel root TTFB","description":"channel root TTFB","guid":"9e7a1f4c-2b5d-4e8f-9a3b-7c1d2e5f6a8b","name":"Phantom Library","overview":"channel root TTFB","owner":"spencerharmon","targetAbi":"10.11.0.0","timestamp":"0001-01-01T00:00:00.0000000Z","version":"$PLUGIN_VERSION","status":"Active","autoUpdate":false,"assemblies":[]}
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
systemd-run --user --unit=rig-channel-ttfb \
  --description='Phantom channel root TTFB rig' \
  --working-directory="$JF_DATA" \
  --setenv=TMPDIR="$RIG/tmp" \
  -- /usr/bin/dotnet "$JF_DLL" \
       --datadir "$JF_DATA" --configdir "$JF_CFG" \
       --cachedir "$JF_CACHE" --logdir "$JF_LOG" \
       --webdir /usr/share/jellyfin/web \
       --ffmpeg /usr/lib/jellyfin-ffmpeg/ffmpeg >/dev/null

for i in {1..120}; do
  code=$(curl -s --max-time 2 -H "X-Emby-Token: $TOK" -o /dev/null -w '%{http_code}' "$API/Channels" 2>/dev/null || echo 000)
  if [ "$code" = 200 ]; then
    echo "  jellyfin up in ${i}s"
    break
  fi
  sleep 1
  [ "$i" = 120 ] && { journalctl --user -u rig-channel-ttfb -n 120 --no-pager || true; fail 'Jellyfin rig did not start'; }
done

echo '[3] resolve Phantom channel ids'
curl -sS --fail --max-time 10 -H "X-Emby-Token: $TOK" "$API/Channels" -o /tmp/ttfb-channels.json
MOVIES_ID=$(find_channel_id 'Phantom Movies')
SHOWS_ID=$(find_channel_id 'Phantom Shows')
echo "  movies=$MOVIES_ID shows=$SHOWS_ID"

echo '[4] measure channel root TTFB with Limit=1 on production-shaped data'
measure_channel movies "$MOVIES_ID" "$MOVIES_MAX_SECONDS"
measure_channel shows "$SHOWS_ID" "$SHOWS_MAX_SECONDS"

echo 'CHANNEL_ROOT_TTFB_OK'
