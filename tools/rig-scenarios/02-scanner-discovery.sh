#!/bin/bash
# Scenario 2: prove the scanner creates BaseItems from phantom
# symlinks if they exist independent of SuggestionsContributor's
# CreateItem path, and those scanner-created rows have:
#   - Name = filename stem
#   - IsLocked = 0
#   - no ProviderIds
#
# Procedure:
#   1. Wipe state.
#   2. Manually create phantom symlinks on disk (no plugin involvement).
#   3. Trigger a library scan.
#   4. Observe what scanner did to BaseItems.
set -u
exec > /tmp/jf-rig/logs/scenario-scanner-discovery.log 2>&1
BASE=http://localhost:18096
TOK=testtoken00000000000000000000000
JFDB=/tmp/jf-test/data/data/jellyfin.db
PHDB=/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db
SPLASH=/tmp/jf-test/cache/PhantomLibrary/splash.mp4

echo "=== Scenario: scanner discovers phantom symlinks ==="
date

# Wipe everything to clear prior scenario state.
sqlite3 $JFDB <<'SQL'
DELETE FROM BaseItemProviders WHERE ItemId IN (
  SELECT Id FROM BaseItems WHERE Path LIKE '%__phantom_tmdb%');
DELETE FROM BaseItemImageInfos WHERE ItemId IN (
  SELECT Id FROM BaseItems WHERE Path LIKE '%__phantom_tmdb%');
DELETE FROM BaseItemMetadataFields WHERE ItemId IN (
  SELECT Id FROM BaseItems WHERE Path LIKE '%__phantom_tmdb%');
DELETE FROM UserData WHERE ItemId IN (
  SELECT Id FROM BaseItems WHERE Path LIKE '%__phantom_tmdb%');
DELETE FROM BaseItems WHERE Path LIKE '%__phantom_tmdb%';
SQL
sqlite3 $PHDB "DELETE FROM phantom_items;"
find /tmp/jf-test/data/phantom-library -type l -delete 2>/dev/null || true
echo "  state wiped"

# Plant 3 symlinks DIRECTLY, bypassing the plugin.
# These look exactly like what the plugin produces but are not paired
# with any phantom_items row or in-memory BaseItem.
ln -s $SPLASH /tmp/jf-test/data/phantom-library/movies/Orphan_One__phantom_tmdb88800001.mp4
ln -s $SPLASH /tmp/jf-test/data/phantom-library/movies/Orphan_Two__phantom_tmdb88800002.mp4
ln -s $SPLASH /tmp/jf-test/data/phantom-library/shows/Orphan_Series__phantom_tmdb88810001.mp4
ls -la /tmp/jf-test/data/phantom-library/movies/ /tmp/jf-test/data/phantom-library/shows/ 2>&1 | head -10

echo
echo "=== trigger library scan ==="
curl -s -X POST -H "X-Emby-Token: $TOK" "$BASE/Library/Refresh" -w "HTTP=%{http_code}\n"

for i in {1..60}; do
  STATE=$(curl -s -H "X-Emby-Token: $TOK" "$BASE/ScheduledTasks/7738148ffcd07979c7ceb148e06b3aed" | python3 -c "import json,sys; print(json.load(sys.stdin).get('State'))" 2>/dev/null)
  echo "  ${i}s: $STATE"
  [ "$STATE" = "Idle" ] && [ $i -gt 3 ] && break
  sleep 2
done
sleep 3

echo
echo "=== POST-SCAN STATE ==="
echo "--- BaseItems for orphans ---"
sqlite3 $JFDB "
SELECT b.Id, b.Type, b.Name, b.IsLocked, b.ForcedSortName
FROM BaseItems b WHERE b.Path LIKE '%phantom_tmdb888%';"

echo "--- BaseItemProviders for orphans ---"
sqlite3 $JFDB "
SELECT b.Name, p.ProviderId, p.ProviderValue
FROM BaseItems b LEFT JOIN BaseItemProviders p ON p.ItemId=b.Id
WHERE b.Path LIKE '%phantom_tmdb888%';"

echo "--- phantom_items ---"
sqlite3 $PHDB "SELECT * FROM phantom_items WHERE tmdb_id IS NULL OR tmdb_id LIKE '888%';"

echo
echo "=== ASSERTIONS ==="
ORPHAN_COUNT=$(sqlite3 $JFDB "SELECT COUNT(*) FROM BaseItems WHERE Path LIKE '%phantom_tmdb888%';")
STEM_NAMED=$(sqlite3 $JFDB "SELECT COUNT(*) FROM BaseItems WHERE Path LIKE '%phantom_tmdb888%' AND Name LIKE '%__phantom_tmdb%';")
NO_PROV=$(sqlite3 $JFDB "SELECT COUNT(*) FROM BaseItems b WHERE b.Path LIKE '%phantom_tmdb888%' AND NOT EXISTS(SELECT 1 FROM BaseItemProviders p WHERE p.ItemId=b.Id AND p.ProviderId='Tmdb');")
NOT_LOCKED=$(sqlite3 $JFDB "SELECT COUNT(*) FROM BaseItems WHERE Path LIKE '%phantom_tmdb888%' AND IsLocked=0;")

echo "  orphan rows scanner created: $ORPHAN_COUNT (expect 3)"
echo "  stem-named (the bug):        $STEM_NAMED"
echo "  no Tmdb provider (the bug):  $NO_PROV"
echo "  IsLocked=0 (the bug):        $NOT_LOCKED"
echo
echo "If all three 'bug' counts > 0, the scanner is creating broken rows from"
echo "phantom symlinks on disk INDEPENDENT of our plugin's CreateItem path."
echo "Fix must prevent the scanner from creating rows, OR make the symlink"
echo "path produce the same BaseItem.Id that our plugin uses."
echo DONE
