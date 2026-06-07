#!/bin/bash
# I1: trace a single phantom row through every mutation point.
#
# Observer watches BaseItems (Id, Name, IsLocked, ForcedSortName,
# PresentationUniqueKey) + BaseItemProviders for tmdb=99000001
# (mock fixture "Phantom Rig Alpha"). After each step we snapshot
# DB ourselves and log the deltas.
set -u
exec > /tmp/jf-rig/logs/I1-trace.log 2>&1
BASE=http://localhost:18096
TOK=testtoken00000000000000000000000
JFDB=/tmp/jf-test/data/data/jellyfin.db
PHDB=/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db
SCAN_TASK=7738148ffcd07979c7ceb148e06b3aed

# ID we expect (derived by plugin from "phantom_movie_99000001"). We don't
# know what id Jellyfin will compute — we'll look it up by Path after
# Suggestions runs. For now, snapshot by Path pattern.

snapshot() {
  local label=$1
  echo "  [SNAPSHOT $label]  $(date '+%H:%M:%S.%3N')"
  echo "  BaseItems:"
  sqlite3 -separator '|' $JFDB "
    SELECT Id, Name, IsLocked, ForcedSortName, PresentationUniqueKey, IsVirtualItem, substr(Path,-50)
    FROM BaseItems WHERE Path LIKE '%phantom_tmdb99000001%' OR Name LIKE '%99000001%';" \
    | sed 's/^/    /'
  echo "  BaseItemProviders:"
  sqlite3 -separator '|' $JFDB "
    SELECT b.Name, p.ProviderId, p.ProviderValue
    FROM BaseItems b JOIN BaseItemProviders p ON p.ItemId=b.Id
    WHERE b.Path LIKE '%phantom_tmdb99000001%' OR b.Name LIKE '%99000001%';" \
    | sed 's/^/    /'
  echo "  BaseItemImageInfos:"
  sqlite3 -separator '|' $JFDB "
    SELECT b.Name, i.ImageType, substr(i.Path,1,80)
    FROM BaseItems b JOIN BaseItemImageInfos i ON i.ItemId=b.Id
    WHERE b.Path LIKE '%phantom_tmdb99000001%' OR b.Name LIKE '%99000001%';" \
    | sed 's/^/    /'
  echo "  phantom_items:"
  sqlite3 -separator '|' $PHDB "
    SELECT item_guid, tmdb_id, type, state, stub_path
    FROM phantom_items WHERE tmdb_id=99000001;" \
    | sed 's/^/    /'
}

echo "=== I1: single-row mutation trace ==="
date

# Wipe state. Fresh ground.
sqlite3 $JFDB <<'SQL'
DELETE FROM BaseItemProviders WHERE ItemId IN (SELECT Id FROM BaseItems WHERE Path LIKE '%phantom_tmdb%');
DELETE FROM BaseItemImageInfos WHERE ItemId IN (SELECT Id FROM BaseItems WHERE Path LIKE '%phantom_tmdb%');
DELETE FROM BaseItems WHERE Path LIKE '%phantom_tmdb%';
SQL
sqlite3 $PHDB "DELETE FROM phantom_items; DELETE FROM tmdb_cache;"
find /tmp/jf-test/data/phantom-library -type l -delete 2>/dev/null

snapshot "T0_initial_empty"

echo ""
echo "=== STEP A: Trigger Suggestions/Refresh (POST) ==="
RESP=$(curl -s --max-time 60 -X POST -H "X-Emby-Token: $TOK" \
  "$BASE/Plugins/PhantomLibrary/Suggestions/Refresh" -w "\nHTTP=%{http_code}\n")
echo "$RESP" | sed 's/^/  /'

snapshot "T1_after_Suggestions_returned"

echo ""
echo "=== STEP B: Wait 5s for any post-Suggestions async work ==="
sleep 5
snapshot "T2_5s_after_Suggestions"

echo ""
echo "=== STEP C: Trigger library scan (POST /Library/Refresh) ==="
curl -s -X POST -H "X-Emby-Token: $TOK" "$BASE/Library/Refresh" -w "  HTTP=%{http_code}\n"
for i in {1..60}; do
  STATE=$(curl -s -H "X-Emby-Token: $TOK" "$BASE/ScheduledTasks/$SCAN_TASK" \
    | python3 -c "import json,sys; print(json.load(sys.stdin).get('State'))" 2>/dev/null)
  [ "$STATE" = "Idle" ] && [ $i -gt 3 ] && { echo "  scan done in ${i}s"; break; }
  sleep 2
done
sleep 3
snapshot "T3_after_library_scan"

echo ""
echo "=== STEP D: Trigger per-item refresh on the phantom (FullRefresh+ReplaceAll) ==="
ITEM_ID=$(sqlite3 $JFDB "SELECT Id FROM BaseItems WHERE Path LIKE '%phantom_tmdb99000001%' LIMIT 1;" | tr A-Z a-z)
if [ -n "$ITEM_ID" ]; then
  echo "  refreshing item: $ITEM_ID"
  curl -s -X POST -H "X-Emby-Token: $TOK" \
    "$BASE/Items/$ITEM_ID/Refresh?MetadataRefreshMode=FullRefresh&ReplaceAllMetadata=true&ImageRefreshMode=FullRefresh&ReplaceAllImages=true" \
    -w "  HTTP=%{http_code}\n"
  sleep 8
  snapshot "T4_after_per_item_FullRefresh"
else
  echo "  no item found at tmdb=99000001 — Suggestions did not create it"
fi

echo ""
echo "=== STEP E: Wait 30s for any tail effects ==="
sleep 30
snapshot "T5_30s_idle"

echo ""
echo "=== summary deltas ==="
echo "Compare T1 -> T2 -> T3 -> T4 -> T5 above."
echo "If IsLocked or providers change between any pair, that step is the culprit."
echo DONE
