#!/bin/bash
# Scenario 3: re-run library scan AFTER Suggestions has populated
# items correctly. Verify the scan does or doesn't clobber.
set -u
exec > /tmp/jf-rig/logs/scenario-rescan.log 2>&1
BASE=http://localhost:18096
TOK=testtoken00000000000000000000000
JFDB=/tmp/jf-test/data/data/jellyfin.db

echo "=== Scenario: rescan after suggestions ==="
date

echo "--- pre-rescan: current phantom rows ---"
sqlite3 $JFDB "
SELECT b.Name, b.IsLocked, p.ProviderValue, b.ForcedSortName
FROM BaseItems b LEFT JOIN BaseItemProviders p ON p.ItemId=b.Id AND p.ProviderId='Tmdb'
WHERE b.Path LIKE '%phantom_tmdb990000%';"

# Trigger full scan
echo "--- trigger library scan ---"
curl -s -X POST -H "X-Emby-Token: $TOK" "$BASE/Library/Refresh" -w "HTTP=%{http_code}\n"
for i in {1..60}; do
  STATE=$(curl -s -H "X-Emby-Token: $TOK" "$BASE/ScheduledTasks/7738148ffcd07979c7ceb148e06b3aed" | python3 -c "import json,sys; print(json.load(sys.stdin).get('State'))" 2>/dev/null)
  [ "$STATE" = "Idle" ] && [ $i -gt 3 ] && { echo "  scan done in ${i}s"; break; }
  sleep 2
done
sleep 3

echo "--- post-rescan: same query ---"
sqlite3 $JFDB "
SELECT b.Name, b.IsLocked, p.ProviderValue, b.ForcedSortName
FROM BaseItems b LEFT JOIN BaseItemProviders p ON p.ItemId=b.Id AND p.ProviderId='Tmdb'
WHERE b.Path LIKE '%phantom_tmdb990000%';"

# Trigger a SECOND scan
echo "--- trigger second scan ---"
curl -s -X POST -H "X-Emby-Token: $TOK" "$BASE/Library/Refresh" -w "HTTP=%{http_code}\n"
for i in {1..60}; do
  STATE=$(curl -s -H "X-Emby-Token: $TOK" "$BASE/ScheduledTasks/7738148ffcd07979c7ceb148e06b3aed" | python3 -c "import json,sys; print(json.load(sys.stdin).get('State'))" 2>/dev/null)
  [ "$STATE" = "Idle" ] && [ $i -gt 3 ] && { echo "  scan done in ${i}s"; break; }
  sleep 2
done
sleep 3

echo "--- post-second-rescan ---"
sqlite3 $JFDB "
SELECT b.Name, b.IsLocked, p.ProviderValue, b.ForcedSortName
FROM BaseItems b LEFT JOIN BaseItemProviders p ON p.ItemId=b.Id AND p.ProviderId='Tmdb'
WHERE b.Path LIKE '%phantom_tmdb990000%';"

# Trigger metadata refresh on one item via REST (simulates what
# Jellyfin's TmdbProvider might do)
echo "--- trigger /Items/{id}/Refresh on one phantom ---"
PHANTOM_ID=$(sqlite3 $JFDB "SELECT lower(replace(Id,'-','')) FROM BaseItems WHERE Path LIKE '%99000001%' LIMIT 1;")
PHANTOM_ID_DASHED=$(echo $PHANTOM_ID | sed 's/\(........\)\(....\)\(....\)\(....\)\(............\)/\1-\2-\3-\4-\5/')
echo "  refreshing $PHANTOM_ID_DASHED"
curl -s -X POST -H "X-Emby-Token: $TOK" \
  "$BASE/Items/$PHANTOM_ID_DASHED/Refresh?MetadataRefreshMode=FullRefresh&ReplaceAllMetadata=true" \
  -w "HTTP=%{http_code}\n"
sleep 10

echo "--- post-FullRefresh on phantom 99000001 ---"
sqlite3 $JFDB "
SELECT b.Name, b.IsLocked, p.ProviderValue, b.ForcedSortName
FROM BaseItems b LEFT JOIN BaseItemProviders p ON p.ItemId=b.Id AND p.ProviderId='Tmdb'
WHERE b.Path LIKE '%phantom_tmdb99000001%';"

echo DONE
