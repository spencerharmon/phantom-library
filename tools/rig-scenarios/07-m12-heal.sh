#!/bin/bash
# Scenario 07: M12 dedupe-gap heal-on-rediscovery.
#
# Pre-populate a BROKEN-shape BaseItem row (no providers, IsLocked=0,
# Name = filename-stem) for tmdb=99000001 (mock fixture). Run
# Suggestions. Assert:
#   - The same BaseItem.Id is reused (no duplicate created).
#   - Name is healed to the TMDB title ("Phantom Rig Alpha").
#   - IsLocked=1.
#   - BaseItemProviders has Tmdb=99000001.
#
# This test FAILS on pre-M12 code (dedupe miss → duplicate created)
# and PASSES on M12 (NameContains-based fallback → heal in place).
set -u
exec > /tmp/jf-rig/logs/scenario-07-m12-heal.log 2>&1
BASE=http://localhost:18096
TOK=testtoken00000000000000000000000
JFDB=/var/tmp/jf-test/data/data/jellyfin.db
PHDB=/var/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db

echo "=== Scenario 07: M12 dedupe-gap heal ==="
date

# Wipe phantom state.
sqlite3 $JFDB <<'SQL'
DELETE FROM BaseItemProviders WHERE ItemId IN (SELECT Id FROM BaseItems WHERE Path LIKE '%__phantom_tmdb%' OR Name LIKE '%__phantom_tmdb%');
DELETE FROM BaseItemImageInfos WHERE ItemId IN (SELECT Id FROM BaseItems WHERE Path LIKE '%__phantom_tmdb%' OR Name LIKE '%__phantom_tmdb%');
DELETE FROM BaseItems WHERE Path LIKE '%__phantom_tmdb%' OR Name LIKE '%__phantom_tmdb%';
SQL
sqlite3 $PHDB "DELETE FROM phantom_items; DELETE FROM tmdb_cache;"
find /var/tmp/jf-test/data/phantom-library -type l -delete 2>/dev/null
echo "  state wiped"

# Plant a broken-shape row: filename-stem Name, IsLocked=0, no providers,
# Path NULL (simulating prod's M10-era leftovers).
BROKEN_ID='BAD00001-0000-0000-0000-000000000001'
sqlite3 $JFDB <<SQL
INSERT INTO BaseItems
  (Id, Type, Name, IsFolder, IsInMixedFolder, IsLocked, IsMovie, IsSeries, IsVirtualItem,
   IsRepeat, InheritedParentalRatingValue, InheritedParentalRatingSubValue, Width, Height, Audio,
   CleanName)
VALUES
  ('$BROKEN_ID',
   'MediaBrowser.Controller.Entities.Movies.Movie',
   'Phantom_Rig_Alpha__phantom_tmdb99000001',
   0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0,
   'phantom_rig_alpha__phantom_tmdb99000001');
SQL

echo "  pre-state for fixture tmdb=99000001:"
sqlite3 -separator '|' $JFDB "
SELECT Id, Name, IsLocked
FROM BaseItems WHERE Name LIKE '%__phantom_tmdb99000001%';" | sed 's/^/    /'
sqlite3 -separator '|' $JFDB "
SELECT COUNT(*) AS provider_count FROM BaseItemProviders WHERE ItemId='$BROKEN_ID';" | sed 's/^/    providers: /'

echo ""
echo "=== Trigger Suggestions/Refresh ==="
curl -s --max-time 60 -X POST -H "X-Emby-Token: $TOK" \
  "$BASE/Plugins/PhantomLibrary/Suggestions/Refresh" -w "\n  HTTP=%{http_code}\n"
sleep 5

echo ""
echo "=== POST-STATE for tmdb=99000001 ==="
echo "All BaseItems with sentinel in Name OR Path:"
sqlite3 -separator '|' $JFDB "
SELECT Id, Name, IsLocked, ForcedSortName, substr(Path, -50)
FROM BaseItems
WHERE Name LIKE '%__phantom_tmdb99000001%'
   OR Path LIKE '%__phantom_tmdb99000001%'
   OR Name='Phantom Rig Alpha';" | sed 's/^/  /'

echo ""
echo "Providers for the same items:"
sqlite3 -separator '|' $JFDB "
SELECT b.Id, b.Name, p.ProviderId, p.ProviderValue
FROM BaseItems b JOIN BaseItemProviders p ON p.ItemId=b.Id
WHERE b.Name LIKE '%__phantom_tmdb99000001%'
   OR b.Path LIKE '%__phantom_tmdb99000001%'
   OR b.Name='Phantom Rig Alpha';" | sed 's/^/  /'

echo ""
echo "phantom_items:"
sqlite3 -separator '|' $PHDB "
SELECT item_guid, tmdb_id, type, state
FROM phantom_items WHERE tmdb_id=99000001;" | sed 's/^/  /'

echo ""
echo "=== ASSERTIONS ==="

ROW_COUNT=$(sqlite3 $JFDB "
SELECT COUNT(*) FROM BaseItems
WHERE Name LIKE '%__phantom_tmdb99000001%' OR Name='Phantom Rig Alpha';")
echo "  total rows matching fixture (expect 1):     $ROW_COUNT"

SAME_ID_HEALED=$(sqlite3 $JFDB "
SELECT COUNT(*) FROM BaseItems
WHERE Id='$BROKEN_ID' AND Name='Phantom Rig Alpha' AND IsLocked=1;")
echo "  same-Id healed (expect 1):                  $SAME_ID_HEALED"

PROVIDER_OK=$(sqlite3 $JFDB "
SELECT COUNT(*) FROM BaseItemProviders
WHERE ItemId='$BROKEN_ID' AND ProviderId='Tmdb' AND ProviderValue='99000001';")
echo "  Tmdb provider on healed row (expect 1):     $PROVIDER_OK"

PHANTOM_ROW=$(sqlite3 $PHDB "
SELECT COUNT(*) FROM phantom_items WHERE tmdb_id=99000001;")
echo "  phantom_items row (expect 1):               $PHANTOM_ROW"

DUPLICATE_CHECK=$(sqlite3 $JFDB "
SELECT COUNT(*) FROM BaseItems
WHERE Id != '$BROKEN_ID' AND (Name='Phantom Rig Alpha' OR Name LIKE '%__phantom_tmdb99000001%');")
echo "  duplicate rows (expect 0):                  $DUPLICATE_CHECK"

echo ""
if [ "$ROW_COUNT" = "1" ] && [ "$SAME_ID_HEALED" = "1" ] && [ "$PROVIDER_OK" = "1" ] && [ "$DUPLICATE_CHECK" = "0" ]; then
  echo "PASS: dedupe matched the broken row, healed in place, no duplicate created."
else
  echo "FAIL: heal flow did not behave as expected."
fi
echo DONE
