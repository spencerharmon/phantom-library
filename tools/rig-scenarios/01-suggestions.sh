#!/bin/bash
# Scenario 1: confirm M11 "post-CreateItem UpdateItemAsync re-stamp"
# is the mechanism that strips BaseItemProviders.
#
# Procedure:
#   1. Trigger Suggestions/Refresh.
#   2. Observer captures BaseItems + BaseItemProviders mutations.
#   3. Assert post-state: do our 5 fixture rows have ProviderIds[Tmdb] set?
#
# Pass criterion: yes (means mechanism 1 is the only bug; CreateItem
# alone persists providers).
# Fail criterion: no (means scanner or other path also strips them).
set -u
exec > /tmp/jf-rig/logs/scenario-suggestions.log 2>&1
BASE=http://localhost:18096
TOK=testtoken00000000000000000000000
JFDB=/var/tmp/jf-test/data/data/jellyfin.db
PHDB=/var/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db

echo "=== Scenario: suggestions (baseline current code) ==="
date

# Wipe phantom state, fresh start.
sqlite3 $JFDB <<'SQL'
DELETE FROM BaseItemProviders WHERE ItemId IN (
  SELECT Id FROM BaseItems WHERE Path LIKE '%__phantom_tmdb%');
DELETE FROM BaseItemImageInfos WHERE ItemId IN (
  SELECT Id FROM BaseItems WHERE Path LIKE '%__phantom_tmdb%');
DELETE FROM BaseItems WHERE Path LIKE '%__phantom_tmdb%';
SQL
sqlite3 $PHDB "DELETE FROM phantom_items; DELETE FROM tmdb_cache;"
find /var/tmp/jf-test/data/phantom-library -type l -delete 2>/dev/null || true
echo "  state wiped"

# Start observer in background (managed via systemd transient)
systemctl --user stop rig-observer.service 2>/dev/null || true
systemd-run --user --unit=rig-observer --description='rig observer' \
  --setenv=JF_RIG_ROOT=/tmp/jf-rig \
  -- /usr/bin/python3 /tmp/jf-rig/bin/db-observer.py \
       "$JFDB:BaseItems:Path LIKE '%phantom_tmdb99000001%'" \
       "$JFDB:BaseItemProviders:ItemId IN (SELECT Id FROM BaseItems WHERE Path LIKE '%phantom_tmdb99000001%')" \
       "$PHDB:phantom_items:tmdb_id=99000001" \
  >/dev/null
sleep 1
echo "  observer started"

# Trigger Suggestions
echo "=== trigger Suggestions/Refresh ==="
curl -s --max-time 120 -X POST -H "X-Emby-Token: $TOK" \
  "$BASE/Plugins/PhantomLibrary/Suggestions/Refresh" -w "\nHTTP=%{http_code}\n"

# Wait for catalogue task to finish
for i in {1..60}; do
  cnt=$(sqlite3 $PHDB "SELECT COUNT(*) FROM phantom_items WHERE tmdb_id IN (99000001,99000002,99000003,99100001,99100002);")
  echo "  ${i}s: phantom_items fixture rows = $cnt"
  [ "$cnt" -ge 3 ] && { echo "  reached"; break; }
  sleep 1
done

# Wait an additional 10s for any post-create scanner/refresh mutations
echo "  waiting 10s for post-create activity"
sleep 10

# Stop observer
systemctl --user stop rig-observer.service 2>/dev/null || true

echo
echo "=== FINAL STATE ==="
echo "--- BaseItems for fixture movies ---"
sqlite3 $JFDB "
SELECT b.Id, b.Name, b.IsLocked, b.ForcedSortName, b.Path
FROM BaseItems b WHERE b.Path LIKE '%phantom_tmdb990000%';"

echo "--- BaseItemProviders for fixture movies ---"
sqlite3 $JFDB "
SELECT b.Name, p.ProviderId, p.ProviderValue
FROM BaseItems b LEFT JOIN BaseItemProviders p ON p.ItemId=b.Id
WHERE b.Path LIKE '%phantom_tmdb990000%';"

echo "--- BaseItemImageInfos for fixture movies ---"
sqlite3 $JFDB "
SELECT b.Name, i.ImageType, substr(i.Path,1,80) AS path
FROM BaseItems b LEFT JOIN BaseItemImageInfos i ON i.ItemId=b.Id
WHERE b.Path LIKE '%phantom_tmdb990000%';"

echo "--- phantom_items for fixture ids ---"
sqlite3 $PHDB "
SELECT item_guid, tmdb_id, type, state, stub_path
FROM phantom_items WHERE tmdb_id IN (99000001,99000002,99000003,99100001,99100002);"

echo "--- symlinks on disk ---"
ls -la /var/tmp/jf-test/data/phantom-library/movies/ 2>&1 | head -10
ls -la /var/tmp/jf-test/data/phantom-library/shows/ 2>&1 | head -10

echo
echo "=== ASSERTIONS ==="
PROV_COUNT=$(sqlite3 $JFDB "SELECT COUNT(DISTINCT b.Id) FROM BaseItems b JOIN BaseItemProviders p ON p.ItemId=b.Id AND p.ProviderId='Tmdb' WHERE b.Path LIKE '%phantom_tmdb990000%';")
ITEM_COUNT=$(sqlite3 $JFDB "SELECT COUNT(*) FROM BaseItems WHERE Path LIKE '%phantom_tmdb990000%';")
LOCKED_COUNT=$(sqlite3 $JFDB "SELECT COUNT(*) FROM BaseItems WHERE Path LIKE '%phantom_tmdb990000%' AND IsLocked=1;")
CLEAN_NAME_COUNT=$(sqlite3 $JFDB "SELECT COUNT(*) FROM BaseItems WHERE Path LIKE '%phantom_tmdb990000%' AND Name NOT LIKE '%__phantom_tmdb%';")

echo "  items created:               $ITEM_COUNT (expect 3)"
echo "  items with Tmdb provider:    $PROV_COUNT (expect 3 = PASS)"
echo "  items with IsLocked=1:       $LOCKED_COUNT (expect 3 = PASS)"
echo "  items with clean Name:       $CLEAN_NAME_COUNT (expect 3 = PASS)"
echo
echo "Observer log: ls /tmp/jf-rig/logs/observer-*.log"
echo "DONE"
