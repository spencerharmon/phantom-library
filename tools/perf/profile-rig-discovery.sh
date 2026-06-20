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
api_key=${API_KEY:-testtoken00000000000000000000000}
api='http://127.0.0.1:18096'
curl -fsS -H "X-Emby-Token: $api_key" "$api/ScheduledTasks" -o /tmp/phantom-perf-tasks.json
task_id=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/phantom-perf-tasks.json'))
for t in j:
    if t.get('Key') == 'PhantomLibrary.DiscoveryRefresh':
        print(t.get('Id'))
        raise SystemExit(0)
raise SystemExit(1)
PY
) || { echo 'discovery task not found' >&2; exit 1; }
curl -fsS -X POST -H "X-Emby-Token: $api_key" "$api/ScheduledTasks/Running/$task_id" >/dev/null
for _ in $(seq 1 180); do
  curl -fsS -H "X-Emby-Token: $api_key" "$api/ScheduledTasks" -o /tmp/phantom-perf-tasks.json
  state=$(python3 - "$task_id" <<'PY'
import json,sys
j=json.load(open('/tmp/phantom-perf-tasks.json'))
for t in j:
    if t.get('Id') == sys.argv[1]:
        print(t.get('State') or '')
        raise SystemExit(0)
print('missing')
PY
)
  [ "$state" = "Idle" ] && break
  sleep 1
done
end=$(date +%s)
echo "profile-rig-discovery elapsed_seconds=$((end-start)) task_state=${state:-unknown}"
grep -R "Perf\|Discovery refresh complete\|Discover movie\|Discover series" /tmp/jf-test/log* "$LOG_DIR" 2>/dev/null || true
