#!/usr/bin/env bash
set -euo pipefail
DURATION=${DURATION:-120}
OUT=${OUT:-/tmp/phantom-perf-$(date +%Y%m%d-%H%M%S)}
PID=${PID:-}
mkdir -p "$OUT"
if [[ -z "$PID" ]]; then
  PID=$(pgrep -af 'dotnet .*jellyfin\.dll|Jellyfin\.Server|/usr/lib/jellyfin/bin/jellyfin' \
    | awk -v self="$$" '$1 != self { print $1; exit }' || true)
fi
if [[ -z "$PID" ]]; then
  echo "No Jellyfin process found. Set PID=<pid>." >&2
  exit 1
fi
echo "pid=$PID duration=${DURATION}s out=$OUT"
ps -o pid,ppid,pcpu,pmem,rss,vsz,comm,args -p "$PID" | tee "$OUT/ps-start.txt"
if command -v dotnet-counters >/dev/null 2>&1; then
  timeout "$DURATION" dotnet-counters monitor --process-id "$PID" --refresh-interval 5 System.Runtime Microsoft.AspNetCore.Hosting Microsoft-AspNetCore-Server-Kestrel >"$OUT/dotnet-counters.txt" 2>&1 || true
else
  echo "dotnet-counters not found" | tee "$OUT/dotnet-counters.txt"
  sleep "$DURATION"
fi
if command -v dotnet-trace >/dev/null 2>&1; then
  dotnet-trace collect --process-id "$PID" --duration "00:00:${DURATION}" --providers Microsoft-DotNETCore-SampleProfiler --output "$OUT/trace.nettrace" >"$OUT/dotnet-trace.log" 2>&1 || true
else
  echo "dotnet-trace not found" | tee "$OUT/dotnet-trace.log"
fi
ps -o pid,ppid,pcpu,pmem,rss,vsz,comm,args -p "$PID" | tee "$OUT/ps-end.txt"
echo "wrote $OUT"
