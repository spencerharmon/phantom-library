#!/usr/bin/env bash
set -euo pipefail
ROOT=${ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}
cd "$ROOT"
dotnet build src/Jellyfin.Plugin.PhantomLibrary/Jellyfin.Plugin.PhantomLibrary.csproj -c Release
bash tools/rig-scenarios/rig-up.sh --reset
trap 'bash tools/rig-scenarios/rig-down.sh || true' EXIT
api_key=$(cat /tmp/jf-test/config/api-key.txt 2>/dev/null || true)
if [[ -z "$api_key" ]]; then
  echo "missing rig API key" >&2
  exit 1
fi
start=$(date +%s%3N)
curl -fsS "http://127.0.0.1:18096/Channels?api_key=${api_key}" >/tmp/phantom-channels.json
curl -fsS "http://127.0.0.1:18096/Items?api_key=${api_key}&Recursive=true&IncludeItemTypes=Movie,Series,Season,Episode" >/tmp/phantom-items.json
end=$(date +%s%3N)
echo "profile-rig-channel-browse elapsed_ms=$((end-start))"
grep -R "Perf\|Availability\|Discovery" /tmp/jf-test/log* 2>/dev/null || true
