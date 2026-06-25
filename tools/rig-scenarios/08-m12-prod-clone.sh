#!/bin/bash
# Scenario 08: M12 heal against prod-clone (5021 broken rows).
#
# Use an existing rig DB clone containing the historical broken phantom
# rows. Do not import production DBs during scenario execution. Run
# Suggestions. Assert: rows that get re-discovered by Catalogue get healed
# in place; no duplicates.
set -u
exec > /tmp/jf-rig/logs/scenario-08-m12-prod-clone.log 2>&1
BASE=http://localhost:18096
TOK=testtoken00000000000000000000000
JFDB=/tmp/jf-test/data/data/jellyfin.db
PHDB=/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db
REPO=${PHANTOM_REPO_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
source "$REPO/tools/rig-scenarios/rig-db.sh"

echo "=== Scenario 08: M12 heal against prod clone ==="
date

# Stop jellyfin so we can swap DBs.
systemctl --user stop rig-jellyfin
sleep 2

# Reuse existing cloned state.
ensure_existing_rig_jellyfin_db "$JFDB"
migrate_existing_rig_phantom_db "$PHDB" "$REPO"
sqlite3 $JFDB "
DELETE FROM ApiKeys WHERE Name='test-rig' OR AccessToken='testtoken00000000000000000000000';
INSERT INTO ApiKeys (DateCreated, DateLastActivity, Name, AccessToken)
VALUES ('2026-06-04','2026-06-04','test-rig','testtoken00000000000000000000000');"

echo "  pre-state:"
sqlite3 $JFDB "SELECT COUNT(*) AS total, SUM(CASE WHEN IsLocked=1 THEN 1 ELSE 0 END) AS locked FROM BaseItems WHERE Path LIKE '%__phantom_tmdb%' OR Name LIKE '%__phantom_tmdb%';" | sed 's/^/    base_phantom: /'
sqlite3 $JFDB "SELECT COUNT(DISTINCT b.Id) AS with_tmdb FROM BaseItems b JOIN BaseItemProviders p ON p.ItemId=b.Id AND p.ProviderId='Tmdb' WHERE b.Path LIKE '%__phantom_tmdb%' OR b.Name LIKE '%__phantom_tmdb%';" | sed 's/^/    with_tmdb: /'

# Use REAL TMDB (operator's key) so Catalogue creates real-titled rows
# matching the prod broken set.
TMDB_KEY=$(awk -F '"' '/tmdb_api_key/ {print $4}' /etc/gostream/config.json)
sed -i "s|<TmdbApiKey>.*</TmdbApiKey>|<TmdbApiKey>$TMDB_KEY</TmdbApiKey>|" \
  /tmp/jf-test/data/plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml
sed -i "s|<TmdbApiBaseUrl>.*</TmdbApiBaseUrl>|<TmdbApiBaseUrl></TmdbApiBaseUrl>|" \
  /tmp/jf-test/data/plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml
sed -i "s|<SuggestionsCatalogueMaxItems>.*</SuggestionsCatalogueMaxItems>|<SuggestionsCatalogueMaxItems>20</SuggestionsCatalogueMaxItems>|" \
  /tmp/jf-test/data/plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml

# Start jellyfin.
systemd-run --user --unit=rig-jellyfin --description='Phantom rig Jellyfin' \
  --working-directory=/tmp/jf-test/data \
  --setenv=TMPDIR=/tmp/jf-test/tmp \
  -- /usr/bin/dotnet /usr/lib/jellyfin/jellyfin.dll \
       --datadir /tmp/jf-test/data --configdir /tmp/jf-test/config \
       --cachedir /tmp/jf-test/cache --logdir /tmp/jf-test/log \
       --webdir /usr/share/jellyfin/web --ffmpeg /usr/lib/jellyfin-ffmpeg/ffmpeg >/dev/null

for i in {1..60}; do
  code=$(curl -s --max-time 2 -H "X-Emby-Token: $TOK" -o /dev/null -w '%{http_code}' http://localhost:18096/Library/VirtualFolders 2>/dev/null || echo 000)
  [ "$code" = "200" ] && { echo "  up ${i}s"; break; }
  sleep 1
done

echo ""
echo "=== Trigger Suggestions/Refresh (real TMDB, cap 20) ==="
curl -s --max-time 600 -X POST -H "X-Emby-Token: $TOK" \
  "$BASE/Plugins/PhantomLibrary/Suggestions/Refresh" -w "\n  HTTP=%{http_code}\n"
sleep 5

echo ""
echo "=== post-state ==="
sqlite3 $JFDB "SELECT COUNT(*) AS total, SUM(CASE WHEN IsLocked=1 THEN 1 ELSE 0 END) AS locked FROM BaseItems WHERE Path LIKE '%__phantom_tmdb%' OR Name LIKE '%__phantom_tmdb%';" | sed 's/^/  base_phantom: /'
sqlite3 $JFDB "SELECT COUNT(DISTINCT b.Id) AS with_tmdb FROM BaseItems b JOIN BaseItemProviders p ON p.ItemId=b.Id AND p.ProviderId='Tmdb' WHERE b.Path LIKE '%__phantom_tmdb%' OR b.Name LIKE '%__phantom_tmdb%';" | sed 's/^/  with_tmdb: /'

echo ""
echo "=== sample rows that should have been healed ==="
echo "Backrooms (tmdb=1083381):"
sqlite3 -separator '|' $JFDB "
SELECT b.Id, b.Name, b.IsLocked, p.ProviderValue
FROM BaseItems b LEFT JOIN BaseItemProviders p ON p.ItemId=b.Id AND p.ProviderId='Tmdb'
WHERE b.Name LIKE '%phantom_tmdb1083381%' OR b.Name='Backrooms' OR (b.Path LIKE '%phantom_tmdb1083381%');" | sed 's/^/    /'

echo ""
echo "=== heal logs ==="
journalctl --user -u rig-jellyfin --no-pager --since '2 min ago' 2>&1 | grep -i 'healed broken' | head -20
echo DONE
