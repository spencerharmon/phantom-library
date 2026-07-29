#!/bin/bash
# rig-up.sh — start the Phantom Library test rig under user systemd.
# Idempotent. Re-running stops + re-starts the units.
set -euo pipefail
ROOT=/tmp/jf-rig
JF_DATA=/var/tmp/jf-test/data
JF_CFG=/var/tmp/jf-test/config
JF_CACHE=/var/tmp/jf-test/cache
JF_LOG=/var/tmp/jf-test/log
REPO=${PHANTOM_REPO_ROOT:-/home/spencer/git-repos/spencerharmon/phantom-library}
DLL=$REPO/src/Jellyfin.Plugin.PhantomLibrary/bin/Release/net9.0/Jellyfin.Plugin.PhantomLibrary.dll
JF_DLL=$REPO/jellyfin/Jellyfin.Server/bin/Release/net9.0/jellyfin.dll
PLUGIN_VERSION=0.3.0.0
PLUGIN_DIR=$JF_DATA/plugins/Jellyfin.Plugin.PhantomLibrary_$PLUGIN_VERSION
PHANTOM_ROOT=$JF_DATA/phantom-library
GOSTREAM_ROOT=$ROOT/gostream
source "$REPO/tools/rig-scenarios/rig-db.sh"

# ---------------------------------------------------------------- helpers
log() { printf '\033[1m[rig-up]\033[0m %s\n' "$*"; }

stop_units() {
  for u in rig-jellyfin rig-tmdb-mock rig-gostream-mock rig-observer; do
    if systemctl --user is-active --quiet "$u.scope" 2>/dev/null; then
      systemctl --user stop "$u.scope" || true
    fi
    if systemctl --user is-active --quiet "$u.service" 2>/dev/null; then
      systemctl --user stop "$u.service" || true
    fi
  done
  # belt + braces against pgroup-orphans; match process names, not shell command text.
  ps -u "$USER" -o pid=,comm=,args= \
    | awk '$2 == "dotnet" && $0 ~ /jellyfin\.dll/ && $0 ~ /jf-test/ { print $1 }' \
    | xargs -r kill -9 >/dev/null 2>&1 || true
  ps -u "$USER" -o pid=,comm=,args= \
    | awk '$0 ~ /python/ && ($0 ~ /tmdb-mock\.py/ || $0 ~ /gostream-mock\.py/) { print $1 }' \
    | xargs -r kill -9 >/dev/null 2>&1 || true
  sleep 1
}

# ---------------------------------------------------------------- args
RESET=0
[ "${1:-}" = "--reset" ] && RESET=1

# ---------------------------------------------------------------- pre-flight
[ -f "$DLL" ] || { echo "DLL not built: $DLL" >&2; echo "Run: dotnet build -c Release" >&2; exit 1; }
[ -f "$JF_DLL" ] || { echo "patched Jellyfin not built: $JF_DLL" >&2; echo "Run: dotnet build jellyfin/Jellyfin.Server/Jellyfin.Server.csproj -c Release" >&2; exit 1; }

log "stopping any existing rig units"
stop_units

# ---------------------------------------------------------------- rig dirs
mkdir -p $ROOT/{bin,scenarios,logs,fixtures/tmdb,state,gostream/movies,gostream/stubs}
cp $REPO/tools/rig-scenarios/*.{py,sh} $ROOT/bin/ 2>/dev/null || true
chmod +x $ROOT/bin/*.py $ROOT/bin/*.sh 2>/dev/null || true

# ---------------------------------------------------------------- jellyfin rig (existing DB clone only)
log "verify existing rig DB clone"
mkdir -p $JF_DATA/{data,plugins/configurations/PhantomLibrary,root/default} \
         $PLUGIN_DIR \
         $JF_CFG $JF_CACHE $JF_LOG
ensure_existing_rig_jellyfin_db "$JF_DATA/data/jellyfin.db"
migrate_existing_rig_phantom_db_if_present "$JF_DATA/plugins/configurations/PhantomLibrary/phantom.db" "$REPO"
[ -d "$JF_DATA/root/default" ] || rig_fail "existing Jellyfin root/default clone missing: $JF_DATA/root/default"

if [ $RESET -eq 1 ]; then
  log "reset phantom state in existing rig clone"
  mkdir -p $JF_DATA/{data,plugins/configurations/PhantomLibrary,root/default} \
           $PLUGIN_DIR \
           $JF_CFG $JF_CACHE $JF_LOG
  rm -f $JF_DATA/plugins/configurations/PhantomLibrary/phantom.db

  # Wipe all Phantom channel rows from the CLONED rig DB; we want deterministic
  # from-scratch channel behaviour, not cached channel rows from prior rig runs.
  sqlite3 $JF_DATA/data/jellyfin.db <<'SQL' || true
CREATE TEMP TABLE phantom_delete_ids AS
SELECT Id FROM BaseItems
WHERE upper(ChannelId) IN ('80089D10-394F-B545-B5E4-D7D56A872393','40AB6E9A-F516-A84F-46DC-EA7140855D88')
   OR ExternalId LIKE 'movie_%'
   OR ExternalId LIKE 'series_%'
   OR ExternalId LIKE 'season_%'
   OR ExternalId LIKE 'episode_%'
   OR ExternalId LIKE 'orphan_%'
   OR Path LIKE '%__phantom_tmdb%'
   OR (Type IN ('MediaBrowser.Controller.Entities.Movies.Movie',
                'MediaBrowser.Controller.Entities.TV.Series',
                'MediaBrowser.Controller.Entities.TV.Episode')
       AND (Path IS NULL OR Path=''));
DELETE FROM BaseItemProviders WHERE ItemId IN (SELECT Id FROM phantom_delete_ids);
DELETE FROM BaseItemImageInfos WHERE ItemId IN (SELECT Id FROM phantom_delete_ids);
DELETE FROM BaseItemMetadataFields WHERE ItemId IN (SELECT Id FROM phantom_delete_ids);
DELETE FROM UserData WHERE ItemId IN (SELECT Id FROM phantom_delete_ids);
DELETE FROM BaseItems WHERE Id IN (SELECT Id FROM phantom_delete_ids);
DROP TABLE phantom_delete_ids;
DELETE FROM BaseItemProviders WHERE ItemId NOT IN (SELECT Id FROM BaseItems);
SQL

  rm -rf $JF_DATA/plugins/Jellyfin.Plugin.PhantomLibrary_0.1.0.0 \
         $JF_DATA/plugins/Jellyfin.Plugin.PhantomLibrary_0.2.0.0

  # Rig-scoped phantom-stub + gostream mock dirs.
  rm -rf $PHANTOM_ROOT $GOSTREAM_ROOT
  mkdir -p $PHANTOM_ROOT/movies $PHANTOM_ROOT/shows $GOSTREAM_ROOT/movies $GOSTREAM_ROOT/stubs $GOSTREAM_ROOT/tv
fi

# Always drop the fresh DLL.
log "drop fresh DLL"
mkdir -p "$PLUGIN_DIR"
cp "$DLL" "$PLUGIN_DIR/Jellyfin.Plugin.PhantomLibrary.dll"
cat > "$PLUGIN_DIR/meta.json" <<META
{"category":"Metadata","changelog":"integration","description":"integration","guid":"9e7a1f4c-2b5d-4e8f-9a3b-7c1d2e5f6a8b","name":"Phantom Library","overview":"integration","owner":"spencerharmon","targetAbi":"10.11.0.0","timestamp":"0001-01-01T00:00:00.0000000Z","version":"$PLUGIN_VERSION","status":"Active","autoUpdate":false,"assemblies":[]}
META
md5sum "$DLL" "$PLUGIN_DIR/Jellyfin.Plugin.PhantomLibrary.dll" | awk '{print "  "$0}'

# ---------------------------------------------------------------- plugin config
log "write plugin config (TMDB mock + GoStream mock would-go-here)"
cat > $JF_DATA/plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml <<EOF
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration>
  <TmdbApiKey>rig-test-key</TmdbApiKey>
  <TmdbApiBaseUrl>http://127.0.0.1:18099/3</TmdbApiBaseUrl>
  <GostreamBaseUrl>http://127.0.0.1:19080</GostreamBaseUrl>
  <GostreamDiagnosticsBaseUrl>http://127.0.0.1:19080</GostreamDiagnosticsBaseUrl>
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
  <GostreamMoviesRoot>$GOSTREAM_ROOT/movies</GostreamMoviesRoot>
  <GostreamShowsRoot>$GOSTREAM_ROOT/tv</GostreamShowsRoot>
  <SourcePickerPreset>gostream-default</SourcePickerPreset>
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
  "DELETE FROM ApiKeys WHERE Name='test-rig' OR AccessToken='testtoken00000000000000000000000';
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

# ---------------------------------------------------------------- launch gostream-mock under user systemd
log "start rig-gostream-mock.service"
systemd-run --user --unit=rig-gostream-mock \
  --description='Phantom rig gostream mock' \
  --setenv=JF_RIG_ROOT=$ROOT \
  --setenv=GOSTREAM_MOCK_PORT=19080 \
  --setenv=GOSTREAM_MOCK_RESPONSE_MOVIES_ROOT=/mnt/gostream-mkv-virtual/movies \
  -- /usr/bin/python3 $ROOT/bin/gostream-mock.py >/dev/null

for i in {1..20}; do
  if curl -s -o /dev/null -w '%{http_code}' -X OPTIONS http://127.0.0.1:19080/api/library/add | grep -q 405; then
    log "gostream-mock up"
    break
  fi
  sleep 0.2
done

# ---------------------------------------------------------------- launch jellyfin under user systemd
log "start rig-jellyfin.service"
mkdir -p /var/tmp/jf-test/tmp
systemd-run --user --unit=rig-jellyfin \
  --description='Phantom rig Jellyfin' \
  --working-directory=$JF_DATA \
  --setenv=TMPDIR=/var/tmp/jf-test/tmp \
  -- /usr/bin/dotnet $JF_DLL \
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
echo "  gostream-mock: http://127.0.0.1:19080"
echo
echo "Logs:"
echo "  jellyfin: journalctl --user -u rig-jellyfin -f"
echo "  tmdb-mock: tail -f $ROOT/logs/tmdb-mock.log"
echo "  gostream-mock: tail -f $ROOT/logs/gostream-mock.log"
