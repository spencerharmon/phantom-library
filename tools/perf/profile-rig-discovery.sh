#!/usr/bin/env bash
set -euo pipefail
ROOT=${ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}
LOG_DIR=${LOG_DIR:-/tmp/jf-rig/logs}
mkdir -p "$LOG_DIR"
cd "$ROOT"
start=$(date +%s)
dotnet build src/Jellyfin.Plugin.PhantomLibrary/Jellyfin.Plugin.PhantomLibrary.csproj -c Release
bash tools/rig-scenarios/rig-up.sh --reset
trap 'bash tools/rig-scenarios/rig-down.sh || true' EXIT
api_key=$(cat /tmp/jf-test/config/api-key.txt 2>/dev/null || true)
if [[ -z "$api_key" ]]; then
  echo "missing rig API key" >&2
  exit 1
fi
task_id="PhantomLibrary.DiscoveryRefresh"
curl -fsS -X POST "http://127.0.0.1:18096/ScheduledTasks/Running/${task_id}?api_key=${api_key}" >/dev/null
sleep 5
end=$(date +%s)
echo "profile-rig-discovery elapsed_seconds=$((end-start))"
grep -R "Perf\|Discovery refresh complete\|Discover movie\|Discover series" /tmp/jf-test/log* "$LOG_DIR" 2>/dev/null || true
