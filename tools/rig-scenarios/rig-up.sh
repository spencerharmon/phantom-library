#!/bin/bash
# rig-up.sh — start the Phantom Library test rig under user systemd.
# Idempotent. Re-running stops + re-starts the units.
set -euo pipefail
ROOT=/tmp/jf-rig
JF_DATA=/tmp/jf-test/data
JF_CFG=/tmp/jf-test/config
JF_CACHE=/tmp/jf-test/cache
JF_LOG=/tmp/jf-test/log
DLL=/home/spencer/git-repos/spencerharmon/phantom-library/src/Jellyfin.Plugin.PhantomLibrary/bin/Release/net9.0/Jellyfin.Plugin.PhantomLibrary.dll
PLUGIN_DIR=$JF_DATA/plugins/Jellyfin.Plugin.PhantomLibrary_0.1.0.0
PHANTOM_ROOT=$JF_DATA/phantom-library

# ---------------------------------------------------------------- helpers
log() { printf '\033[1m[rig-up]\033[0m %s\n' "$*"; }

stop_units() {
  for u in rig-jellyfin rig-tmdb-mock rig-observer; do
    if systemctl --user is-active --quiet "$u.scope" 2>/dev/null; then
      systemctl --user stop "$u.scope" || true
    fi
    if systemctl --user is-active --quiet "$u.service" 2>/dev/null; then
      systemctl --user stop "$u.service" || true
    fi
  done
  # belt + braces against pgroup-orphans
  pkill -u "$USER" -9 -f "dotnet.*jellyfin.dll.*jf-test" 2>/dev/null || true
  pkill -u "$USER" -9 -f "tmdb-mock.py" 2>/dev/null || true
  sleep 1
}

# ---------------------------------------------------------------- args
RESET=0
[ "${1:-}" = "--reset" ] && RESET=1

# ---------------------------------------------------------------- pre-flight
[ -f "$DLL" ] || { echo "DLL not built: $DLL" >&2; echo "Run: dotnet build -c Release" >&2; exit 1; }

log "stopping any existing rig units"
stop_units

# ---------------------------------------------------------------- rig dirs
mkdir -p $ROOT/{bin,scenarios,logs,fixtures/tmdb,state}

# ---------------------------------------------------------------- jellyfin rig (rebuild from prod)
if [ $RESET -eq 1 ] || [ ! -f "$JF_DATA/data/jellyfin.db" ]; then
  log "reseed jellyfin rig from prod"
  rm -rf /tmp/jf-test
  mkdir -p $JF_DATA/{data,plugins/configurations/PhantomLibrary,root/default} \
           $JF_DATA/plugins/Jellyfin.Plugin.PhantomLibrary_0.1.0.0 \
           $JF_CFG $JF_CACHE $JF_LOG
  cp /var/lib/jellyfin/data/jellyfin.db       $JF_DATA/data/jellyfin.db
  cp /var/lib/jellyfin/data/jellyfin.db-wal   $JF_DATA/data/jellyfin.db-wal 2>/dev/null || true
  cp /var/lib/jellyfin/data/jellyfin.db-shm   $JF_DATA/data/jellyfin.db-shm 2>/dev/null || true
  cp -r /var/lib/jellyfin/root/default/*      $JF_DATA/root/default/ 2>/dev/null || true
  cp /var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db \
     $JF_DATA/plugins/configurations/PhantomLibrary/phantom.db 2>/dev/null || true

  # Wipe all Phantom Virtual rows; we want deterministic from-scratch behaviour.
  sqlite3 $JF_DATA/data/jellyfin.db <<'SQL' || true
DELETE FROM BaseItemProviders WHERE ItemId IN (
  SELECT Id FROM BaseItems WHERE Path LIKE '%__phantom_tmdb%' OR (Path IS NULL OR Path='')
);
DELETE FROM BaseItemImageInfos WHERE ItemId IN (
  SELECT Id FROM BaseItems WHERE Path LIKE '%__phantom_tmdb%' OR (Path IS NULL OR Path='')
);
DELETE FROM BaseItemMetadataFields WHERE ItemId IN (
  SELECT Id FROM BaseItems WHERE Path LIKE '%__phantom_tmdb%' OR (Path IS NULL OR Path='')
);
DELETE FROM UserData WHERE ItemId IN (
  SELECT Id FROM BaseItems WHERE Path LIKE '%__phantom_tmdb%' OR (Path IS NULL OR Path='')
);
DELETE FROM BaseItems
WHERE Path LIKE '%__phantom_tmdb%'
   OR (Type IN ('MediaBrowser.Controller.Entities.Movies.Movie',
                'MediaBrowser.Controller.Entities.TV.Series',
                'MediaBrowser.Controller.Entities.TV.Episode')
       AND (Path IS NULL OR Path=''));
DELETE FROM BaseItemProviders WHERE ItemId NOT IN (SELECT Id FROM BaseItems);
SQL

  sqlite3 $JF_DATA/plugins/configurations/PhantomLibrary/phantom.db \
    "DELETE FROM phantom_items; DELETE FROM tmdb_cache;" || true

  # Rig-scoped phantom-stub dir
  rm -rf $PHANTOM_ROOT
  mkdir -p $PHANTOM_ROOT/movies $PHANTOM_ROOT/shows
fi

# Always drop the fresh DLL.
log "drop fresh DLL"
cp "$DLL" "$PLUGIN_DIR/Jellyfin.Plugin.PhantomLibrary.dll"
md5sum "$DLL" "$PLUGIN_DIR/Jellyfin.Plugin.PhantomLibrary.dll" | awk '{print "  "$0}'

# ---------------------------------------------------------------- plugin config
log "write plugin config (TMDB mock + GoStream mock would-go-here)"
cat > $JF_DATA/plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml <<EOF
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration>
  <TmdbApiKey>rig-test-key</TmdbApiKey>
  <TmdbApiBaseUrl>http://127.0.0.1:18099/3</TmdbApiBaseUrl>
  <GostreamBaseUrl>http://127.0.0.1:9080</GostreamBaseUrl>
  <GostreamDiagnosticsBaseUrl>http://127.0.0.1:8090</GostreamDiagnosticsBaseUrl>
  <ProwlarrBaseUrl></ProwlarrBaseUrl>
  <ProwlarrApiKey></ProwlarrApiKey>
  <TorrentioBaseUrl>https://torrentio.strem.fun</TorrentioBaseUrl>
  <QualityPreset>GostreamDefault</QualityPreset>
  <MinSeeders>5</MinSeeders>
  <MinSizeGb1080p>4</MinSizeGb1080p>
  <MinSizeGb4K>20</MinSizeGb4K>
  <EvictionEnabled>false</EvictionEnabled>
  <EvictionIdleDays>7</EvictionIdleDays>
  <EvictionScheduleCron>0 4 * * *</EvictionScheduleCron>
  <MaterialisationConcurrencyGlobal>2</MaterialisationConcurrencyGlobal>
  <MaterialisationConcurrencyPerIndexer>2</MaterialisationConcurrencyPerIndexer>
  <EagerResolveEnabled>false</EagerResolveEnabled>
  <EagerResolveMaxConcurrent>2</EagerResolveMaxConcurrent>
  <PhantomRetentionDays>7</PhantomRetentionDays>
  <SeriesAutopilotEnabled>false</SeriesAutopilotEnabled>
  <SeriesAutopilotPrefetchEpisodes>1</SeriesAutopilotPrefetchEpisodes>
  <PhantomBadgeVisibility>AlwaysShow</PhantomBadgeVisibility>
  <SplashLoopAssetPath></SplashLoopAssetPath>
  <PhantomTargetLibraryId></PhantomTargetLibraryId>
  <PhantomStubRoot>$PHANTOM_ROOT</PhantomStubRoot>
  <PhantomMoviesLibraryName>gostream-movies</PhantomMoviesLibraryName>
  <PhantomShowsLibraryName>gostream-shows</PhantomShowsLibraryName>
  <SuggestionsCatalogueMaxItems>10</SuggestionsCatalogueMaxItems>
</PluginConfiguration>
EOF

# network.xml: rig port 18096
cat > $JF_CFG/network.xml <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration>
  <PublicHttpPort>18096</PublicHttpPort>
  <InternalHttpPort>18096</InternalHttpPort>
  <AutoDiscovery>false</AutoDiscovery>
</NetworkConfiguration>
EOF

# API key for REST
sqlite3 $JF_DATA/data/jellyfin.db \
  "DELETE FROM ApiKeys WHERE Name='test-rig';
   INSERT INTO ApiKeys (DateCreated, DateLastActivity, Name, AccessToken)
   VALUES ('2026-06-04','2026-06-04','test-rig','testtoken00000000000000000000000');"

# ---------------------------------------------------------------- launch tmdb-mock under user systemd
log "start rig-tmdb-mock.service"
systemd-run --user --unit=rig-tmdb-mock \
  --description='Phantom rig TMDB mock' \
  --setenv=JF_RIG_ROOT=$ROOT \
  --setenv=TMDB_MOCK_PORT=18099 \
  -- /usr/bin/python3 $ROOT/bin/tmdb-mock.py >/dev/null

# Wait for it to bind.
for i in {1..20}; do
  if curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:18099/3/configuration | grep -q 200; then
    log "tmdb-mock up"
    break
  fi
  sleep 0.2
done

# ---------------------------------------------------------------- launch jellyfin under user systemd
log "start rig-jellyfin.service"
mkdir -p /tmp/jf-test/tmp
systemd-run --user --unit=rig-jellyfin \
  --description='Phantom rig Jellyfin' \
  --working-directory=$JF_DATA \
  --setenv=TMPDIR=/tmp/jf-test/tmp \
  -- /usr/bin/dotnet /usr/lib/jellyfin/jellyfin.dll \
       --datadir $JF_DATA --configdir $JF_CFG \
       --cachedir $JF_CACHE --logdir $JF_LOG \
       --webdir /usr/share/jellyfin/web \
       --ffmpeg /usr/lib/jellyfin-ffmpeg/ffmpeg >/dev/null

# Wait for auth health
for i in {1..120}; do
  code=$(curl -s --max-time 2 -H "X-Emby-Token: testtoken00000000000000000000000" -o /dev/null -w '%{http_code}' http://localhost:18096/Library/VirtualFolders 2>/dev/null || echo 000)
  if [ "$code" = "200" ]; then
    log "jellyfin up in ${i}s"
    break
  fi
  sleep 1
done

log "rig ready"
echo
echo "Status:"
systemctl --user list-units --no-pager 'rig-*' 2>&1 | grep rig- || true
echo
echo "Endpoints:"
echo "  jellyfin: http://localhost:18096 (token: testtoken00000000000000000000000)"
echo "  tmdb-mock: http://127.0.0.1:18099"
echo
echo "Logs:"
echo "  jellyfin: journalctl --user -u rig-jellyfin -f"
echo "  tmdb-mock: tail -f $ROOT/logs/tmdb-mock.log"
