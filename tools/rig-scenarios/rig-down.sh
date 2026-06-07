#!/bin/bash
# rig-down.sh — stop all rig units cleanly.
set -u
for u in rig-jellyfin rig-tmdb-mock rig-observer; do
  systemctl --user stop "$u.scope" 2>/dev/null || true
  systemctl --user stop "$u.service" 2>/dev/null || true
done
pkill -u "$USER" -9 -f "dotnet.*jellyfin.dll.*jf-test" 2>/dev/null || true
pkill -u "$USER" -9 -f "tmdb-mock.py" 2>/dev/null || true
pkill -u "$USER" -9 -f "db-observer.py" 2>/dev/null || true
sleep 1
echo "rig stopped"
