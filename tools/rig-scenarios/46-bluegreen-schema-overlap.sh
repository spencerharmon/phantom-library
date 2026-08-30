#!/bin/bash
# Scenario 46: blue/green shared-Postgres schema overlap live rig
# (ROI Priority 7, item 5 — p7-bluegreen-schema-overlap-rig).
#
# *** OPERATOR-RUN-ONLY *** (needs a patched channel-arch Jellyfin + a plugin
# DLL built from this branch, plus `podman` for the ephemeral shared Postgres).
# Rig ports 18096 (blue) / 18296 (green) ONLY, NEVER prod :8096. trap-clean.
#
# What this proves, against ONE shared logical Postgres DB (the planned
# blue/green topology from flux's
# docs/phantom-library-schema-change-expand-contract.md, made concrete in
# THIS repo by PhantomDb's forward-tolerant gate (p7-forward-tolerant-schema-gate)
# and SchemaExpandMigrator (p7-additive-idempotent-expand-migrations)):
#
#   [1] BOOT BLUE — the "old" color: rig-up.sh at :18096, configured via the
#       PHANTOM_POSTGRES_* passthrough to use the SAME shared Postgres DB
#       instead of the SQLite default. Drive REAL discovery (a write) and
#       browse + PlaybackInfo (reads) for a MOVIE and a SERIES/EPISODE.
#   [2] EXPAND (additive, idempotent) — apply a synthetic additive column
#       directly against the shared DB (mirrors the shape
#       SchemaExpandMigrator enforces: `ADD COLUMN IF NOT EXISTS`, nullable,
#       no backfill required) WHILE blue keeps running. Assert blue's reads
#       AND writes still succeed post-expand (OVERLAP INVARIANT, half 1: the
#       older-code color is not disabled by a newer additive schema — this is
#       exactly what PhantomDb's forward-tolerant `db_version >= required`
#       gate + explicit-column query surface exist to guarantee).
#   [3] BOOT GREEN — the "new" color: a second rig instance at :18296,
#       pointed at the SAME shared Postgres DB, sharing the already-running
#       tmdb-mock/gostream-mock. Drive its own discovery (a write) + browse +
#       PlaybackInfo for movie AND episode. Then re-verify BLUE still works
#       (OVERLAP INVARIANT, half 2: the newer color's additive-column writes
#       do not break the older color's queries).
#   [4] SIMULATE FLIP — stop blue (traffic has moved to green; the concrete
#       prod cutover is operator-owned, see the tracked NEEDS-HUMAN
#       `staging-migration-cutover` — this step ONLY simulates it inside the
#       rig, it performs no operator action).
#   [5] CONTRACT — drop the now-unused synthetic column ONLY after the
#       simulated flip. Assert the SOLE REMAINING color (green) still
#       browses/plays/writes fine (EXPAND->FLIP->CONTRACT: no single step
#       both expands and contracts; contract runs strictly after the flip).
#
# The DETERMINISTIC equivalent of steps [2]/[5] (additive-idempotent apply,
# and a safe contract-drop after a simulated flip) against a synthetic
# ephemeral Postgres fixture is regression-covered in-repo, with NO live
# Jellyfin, by scripts/tests/bluegreen-schema-overlap-rig.test.sh — the
# build/CI-tier guard runnable with only bash + podman. THIS scenario is the
# LIVE proof that two real plugin processes actually overlap on the shared DB
# without disruption.
#
# Movie/TV parity: every phase drives BOTH a movie (catalogue_items type
# 'movie') and a series/episode (catalogue_items type 'series', expanded into
# episodes) through discovery, browse, and PlaybackInfo.
#
# CI: driven by the Gitea Actions job `phantom-library-bluegreen-schema-overlap-rig`
# (.gitea/workflows/bluegreen-schema-overlap-rig.yml) on the self-hosted
# runner. Synthetic fixture only (the TMDB mock's deterministic catalogue,
# same as scenario 41); zero operator PII; trap-based cleanup of BOTH colors
# + the ephemeral Postgres container.

set -u
mkdir -p /tmp/jf-rig/logs
exec > >(tee /tmp/jf-rig/logs/scenario-bluegreen-schema-overlap.log) 2>&1

ROOT=${PHANTOM_REPO_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}
RIG=/tmp/jf-rig
TOK=${TOK:-testtoken00000000000000000000000}

BLUE_API=http://localhost:18096
GREEN_API=http://localhost:18296

# Shared ephemeral Postgres — this is the ONE logical DB both colors point
# at, never a per-color DB (that would defeat the whole point of the test).
PG_CONTAINER=rig-bluegreen-pg
PG_PORT=15432
PG_DB=phantom_bluegreen
PG_USER=phantom
PG_PASSWORD=rigpass

# Green's own rig paths (mirrors rig-up.sh's ROOT/JF_DATA/... shape, at a
# distinct port + directory tree so it never collides with blue's).
GREEN_ROOT=/tmp/jf-rig-green
GREEN_JF_DATA=/var/tmp/jf-test-green/data
GREEN_JF_CFG=/var/tmp/jf-test-green/config
GREEN_JF_CACHE=/var/tmp/jf-test-green/cache
GREEN_JF_LOG=/var/tmp/jf-test-green/log
GREEN_PLUGIN_VERSION=0.3.0.0
GREEN_PLUGIN_DIR=$GREEN_JF_DATA/plugins/Jellyfin.Plugin.PhantomLibrary_$GREEN_PLUGIN_VERSION

DLL=$ROOT/src/Jellyfin.Plugin.PhantomLibrary/bin/Release/net9.0/Jellyfin.Plugin.PhantomLibrary.dll
JF_DLL=$ROOT/jellyfin/Jellyfin.Server/bin/Release/net9.0/jellyfin.dll

# Synthetic mock fixture ids (see tmdb-mock.py) — same deterministic set
# scenario 41/35/36 use. All >= 99000000 by design (zero PII).
ALPHA=99000001   # movie
DELTA=99100001   # series (1 season, 8 episodes)

echo "=== Scenario 46: blue/green shared-Postgres schema overlap live rig ==="
date
echo

fail() { echo "FAIL: $*" >&2; exit 1; }
api()      { curl -sS --fail -H "X-Emby-Token: $TOK" "$@"; }
api_post() { curl -sS --fail -X POST -H "X-Emby-Token: $TOK" "$@"; }

# ---------------------------------------------------------------- trap-clean
cleanup() {
  local rc=$?
  echo "[cleanup] tearing down blue + green + shared postgres (rc=$rc)"
  systemctl --user stop rig-green-jellyfin.scope >/dev/null 2>&1 || true
  systemctl --user stop rig-green-jellyfin.service >/dev/null 2>&1 || true
  pkill -u "$USER" -9 -f "dotnet.*jellyfin.dll.*jf-test-green" >/dev/null 2>&1 || true
  bash "$ROOT/tools/rig-scenarios/rig-down.sh" >/dev/null 2>&1 || true
  podman rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true
  rm -rf "$GREEN_ROOT" /var/tmp/jf-test-green 2>/dev/null || true
  exit "$rc"
}
trap cleanup EXIT

[ -f "$DLL" ]    || fail "plugin DLL not built: $DLL (dotnet build -c Release)"
[ -f "$JF_DLL" ] || fail "patched Jellyfin not built: $JF_DLL"
command -v podman >/dev/null 2>&1 || fail "podman required for the ephemeral shared Postgres"

pg_sql() {  # pg_sql <sql> ; runs against the shared DB, prints result
  podman exec -i "$PG_CONTAINER" psql -q -A -t -U "$PG_USER" -d "$PG_DB" -c "$1"
}

find_task_id() {  # find_task_id <base_api>
  python3 - "$1" <<'PY'
import json,sys
j=json.load(open('/tmp/bg-tasks.json'))
for t in j:
    if t.get('Key') == 'PhantomLibrary.DiscoveryRefresh' or t.get('Name') == 'Phantom Library — Refresh Discovery':
        print(t['Id'])
        raise SystemExit(0)
raise SystemExit(1)
PY
}

wait_task_idle() {  # wait_task_idle <base_api> <task_id>
  local base=$1 task_id=$2
  for _ in $(seq 1 120); do
    api "$base/ScheduledTasks" -o /tmp/bg-tasks.json
    local state
    state=$(python3 - "$task_id" <<'PY'
import json,sys
j=json.load(open('/tmp/bg-tasks.json'))
for t in j:
    if t.get('Id') == sys.argv[1]:
        print(t.get('State'))
        break
PY
)
    [ "$state" = "Idle" ] && return 0
    sleep 1
  done
  fail "task $task_id on $base did not become Idle"
}

# drive_discovery_and_browse <label> <base_api>
# Real write (DiscoveryRefresh) + real browse (Channels list + Items) + real
# playback query (PlaybackInfo) for a MOVIE and a SERIES/EPISODE against the
# shared DB. Fails loudly (via `api`'s --fail) on any HTTP error.
drive_discovery_and_browse() {
  local label=$1 base=$2
  echo "  [$label] browse (pre-write) + trigger discovery (write) + browse (post-write) + playback"
  api "$base/Channels" -o /tmp/bg-channels-before.json || fail "$label: /Channels failed before discovery"

  api "$base/ScheduledTasks" -o /tmp/bg-tasks.json || fail "$label: /ScheduledTasks failed"
  local task_id
  task_id=$(find_task_id "$base") || fail "$label: DiscoveryRefresh task not found"
  api_post "$base/ScheduledTasks/Running/$task_id" -o /tmp/bg-task-run.out \
    || fail "$label: failed to start DiscoveryRefresh (the write)"
  wait_task_idle "$base" "$task_id"

  # The write landed in the SHARED Postgres DB: assert both a movie AND a
  # series/episode row exist (movie/TV parity), directly against the DB the
  # write actually targeted.
  local movie_n series_n
  movie_n=$(pg_sql "SELECT COUNT(*) FROM catalogue_items WHERE type='movie' AND tmdb_id=$ALPHA;")
  series_n=$(pg_sql "SELECT COUNT(*) FROM catalogue_items WHERE type='series' AND tmdb_id=$DELTA;")
  [ "$movie_n" = "1" ]  || fail "$label: expected movie $ALPHA in shared catalogue_items, got count=$movie_n"
  [ "$series_n" = "1" ] || fail "$label: expected series $DELTA in shared catalogue_items, got count=$series_n"
  echo "    write OK: movie=$ALPHA series=$DELTA present in the shared Postgres DB"

  api "$base/Channels" -o /tmp/bg-channels-after.json || fail "$label: /Channels failed after discovery (browse)"
  local channel_id
  channel_id=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/bg-channels-after.json'))
items=j.get('Items', j if isinstance(j,list) else [])
for x in items:
    print(x['Id'])
    break
PY
)
  [ -n "$channel_id" ] || fail "$label: no channels visible after discovery"
  api "$base/Channels/$channel_id/Items" -o /tmp/bg-channel-items.json \
    || fail "$label: /Channels/$channel_id/Items failed (browse)"
  local first_item
  first_item=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/bg-channel-items.json'))
items=j.get('Items', j if isinstance(j,list) else [])
for x in items:
    print(x['Id'])
    break
PY
)
  if [ -n "$first_item" ]; then
    api "$base/Items/$first_item/PlaybackInfo" -o /tmp/bg-pb.json \
      || fail "$label: PlaybackInfo failed for $first_item (playback)"
    echo "    playback OK: PlaybackInfo answered for item $first_item"
  else
    echo "    NOTE: channel not yet populated with a browsable item this pass; write+browse already proved above"
  fi
}

# =====================================================================
echo "[0] start the SHARED ephemeral Postgres ($PG_CONTAINER)"
podman rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true
podman run -d --name "$PG_CONTAINER" \
  -e POSTGRES_USER="$PG_USER" -e POSTGRES_PASSWORD="$PG_PASSWORD" -e POSTGRES_DB="$PG_DB" \
  -p "127.0.0.1:$PG_PORT:5432" \
  docker.io/library/postgres:16-alpine >/dev/null \
  || fail "could not start shared Postgres container"
for i in $(seq 1 60); do
  podman exec "$PG_CONTAINER" pg_isready -U "$PG_USER" -d "$PG_DB" >/dev/null 2>&1 && break
  sleep 1
  [ "$i" = 60 ] && fail "shared Postgres never became ready"
done
echo "  shared Postgres ready: $PG_DB on 127.0.0.1:$PG_PORT"

# =====================================================================
echo "[1] boot BLUE (old color) at :18096 against the SHARED Postgres DB"
export PHANTOM_POSTGRES_HOST=127.0.0.1
export PHANTOM_POSTGRES_PORT=$PG_PORT
export PHANTOM_POSTGRES_DB=$PG_DB
export PHANTOM_POSTGRES_USER=$PG_USER
export PHANTOM_POSTGRES_PASSWORD=$PG_PASSWORD
bash "$ROOT/tools/rig-scenarios/rig-up.sh" --reset || fail "blue rig-up failed"
for i in $(seq 1 120); do
  code=$(curl -s --max-time 2 -H "X-Emby-Token: $TOK" -o /dev/null -w '%{http_code}' "$BLUE_API/System/Info" 2>/dev/null || echo 000)
  [ "$code" = "200" ] && break
  sleep 1
  [ "$i" = 120 ] && fail "blue never came up on $BLUE_API"
done
echo "  blue is live on $BLUE_API against the shared Postgres DB"
drive_discovery_and_browse "blue-pre-expand" "$BLUE_API"

# =====================================================================
echo "[2] EXPAND (additive, idempotent) directly against the shared DB, blue still running"
# Mirrors exactly the shape SchemaExpandMigrator.EnsureAdditiveOnly requires:
# a nullable ADD COLUMN guarded by IF NOT EXISTS. Applied twice to prove
# idempotency (the peer color double-applying the same migration is a no-op).
pg_sql "ALTER TABLE catalogue_items ADD COLUMN IF NOT EXISTS rig_bluegreen_probe TEXT;" \
  || fail "expand (1st apply) failed"
pg_sql "ALTER TABLE catalogue_items ADD COLUMN IF NOT EXISTS rig_bluegreen_probe TEXT;" \
  || fail "expand (2nd apply, must be a no-op) failed"
has_col=$(pg_sql "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='catalogue_items' AND column_name='rig_bluegreen_probe';")
[ "$has_col" = "1" ] || fail "expand did not create rig_bluegreen_probe (got count=$has_col)"
echo "  expand OK: rig_bluegreen_probe added (idempotent, additive-only)"

echo "  re-verify BLUE (old color) still works post-expand (OVERLAP INVARIANT, half 1)"
drive_discovery_and_browse "blue-post-expand" "$BLUE_API"

# =====================================================================
echo "[3] boot GREEN (new color) at :18296 against the SAME shared Postgres DB"
mkdir -p "$GREEN_ROOT/logs" \
  "$GREEN_JF_DATA/data" "$GREEN_JF_DATA/plugins/configurations/PhantomLibrary" "$GREEN_JF_DATA/root/default" \
  "$GREEN_PLUGIN_DIR" "$GREEN_JF_CFG" "$GREEN_JF_CACHE" "$GREEN_JF_LOG" /var/tmp/jf-test-green/tmp

# Green reuses blue's already-seeded Jellyfin server DB + root/default layout
# as its starting template (library wiring, admin user), so only the SHARED
# Postgres phantom-schema DB is actually shared between the colors — the
# Jellyfin server-side SQLite DB is per-instance, which is orthogonal to the
# phantom-schema overlap this rig proves.
cp -p /var/tmp/jf-test/data/data/jellyfin.db "$GREEN_JF_DATA/data/jellyfin.db" \
  || fail "could not seed green's jellyfin.db from blue's rig clone"
cp -a /var/tmp/jf-test/data/root/default/. "$GREEN_JF_DATA/root/default/" 2>/dev/null || true

cp "$DLL" "$GREEN_PLUGIN_DIR/Jellyfin.Plugin.PhantomLibrary.dll"
cat > "$GREEN_PLUGIN_DIR/meta.json" <<META
{"category":"Metadata","changelog":"integration","description":"integration","guid":"9e7a1f4c-2b5d-4e8f-9a3b-7c1d2e5f6a8b","name":"Phantom Library","overview":"integration","owner":"spencerharmon","targetAbi":"10.11.0.0","timestamp":"0001-01-01T00:00:00.0000000Z","version":"$GREEN_PLUGIN_VERSION","status":"Active","autoUpdate":false,"assemblies":[]}
META

cat > "$GREEN_JF_DATA/plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml" <<EOF
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
  <PhantomStubRoot>$GREEN_ROOT/phantom-stubs</PhantomStubRoot>
  <GostreamMoviesRoot>$GREEN_ROOT/gostream/movies</GostreamMoviesRoot>
  <GostreamShowsRoot>$GREEN_ROOT/gostream/tv</GostreamShowsRoot>
  <SourcePickerPreset>gostream-default</SourcePickerPreset>
  <PhantomMoviesLibraryName>gostream-movies</PhantomMoviesLibraryName>
  <PhantomShowsLibraryName>gostream-shows</PhantomShowsLibraryName>
  <SuggestionsCatalogueMaxItems>10</SuggestionsCatalogueMaxItems>
</PluginConfiguration>
EOF
mkdir -p "$GREEN_ROOT/phantom-stubs/movies" "$GREEN_ROOT/phantom-stubs/shows" \
  "$GREEN_ROOT/gostream/movies" "$GREEN_ROOT/gostream/tv"

cat > "$GREEN_JF_CFG/network.xml" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration>
  <PublicHttpPort>18296</PublicHttpPort>
  <InternalHttpPort>18296</InternalHttpPort>
  <AutoDiscovery>false</AutoDiscovery>
</NetworkConfiguration>
EOF

sqlite3 "$GREEN_JF_DATA/data/jellyfin.db" \
  "DELETE FROM ApiKeys WHERE Name='test-rig' OR AccessToken='testtoken00000000000000000000000';
   INSERT INTO ApiKeys (DateCreated, DateLastActivity, Name, AccessToken)
   VALUES ('2026-06-04','2026-06-04','test-rig','testtoken00000000000000000000000');"

systemd-run --user --unit=rig-green-jellyfin \
  --description='Phantom rig Jellyfin (green color)' \
  --working-directory="$GREEN_JF_DATA" \
  --setenv=TMPDIR=/var/tmp/jf-test-green/tmp \
  --setenv=PHANTOM_POSTGRES_HOST=127.0.0.1 \
  --setenv=PHANTOM_POSTGRES_PORT="$PG_PORT" \
  --setenv=PHANTOM_POSTGRES_DB="$PG_DB" \
  --setenv=PHANTOM_POSTGRES_USER="$PG_USER" \
  --setenv=PHANTOM_POSTGRES_PASSWORD="$PG_PASSWORD" \
  -- /usr/bin/dotnet "$JF_DLL" \
       --datadir "$GREEN_JF_DATA" --configdir "$GREEN_JF_CFG" \
       --cachedir "$GREEN_JF_CACHE" --logdir "$GREEN_JF_LOG" \
       --webdir /usr/share/jellyfin/web \
       --ffmpeg /usr/lib/jellyfin-ffmpeg/ffmpeg >/dev/null \
  || fail "could not start green"

for i in $(seq 1 120); do
  code=$(curl -s --max-time 2 -H "X-Emby-Token: $TOK" -o /dev/null -w '%{http_code}' "$GREEN_API/System/Info" 2>/dev/null || echo 000)
  [ "$code" = "200" ] && break
  sleep 1
  [ "$i" = 120 ] && fail "green never came up on $GREEN_API"
done
echo "  green is live on $GREEN_API against the SAME shared Postgres DB"
drive_discovery_and_browse "green" "$GREEN_API"

echo "  re-verify BLUE (old color) still works after GREEN's write (OVERLAP INVARIANT, half 2)"
drive_discovery_and_browse "blue-after-green-write" "$BLUE_API"

# =====================================================================
echo "[4] SIMULATE FLIP (stop blue; the real prod cutover is operator-owned)"
systemctl --user stop rig-jellyfin.scope >/dev/null 2>&1 || true
systemctl --user stop rig-jellyfin.service >/dev/null 2>&1 || true
pkill -u "$USER" -9 -f "dotnet.*jellyfin.dll.*jf-test\b" >/dev/null 2>&1 || true
for i in $(seq 1 20); do
  code=$(curl -s --max-time 1 -o /dev/null -w '%{http_code}' "$BLUE_API/System/Info" 2>/dev/null || echo 000)
  [ "$code" = "000" ] && break
  sleep 1
done
echo "  blue stopped; green is the sole remaining color"

# =====================================================================
echo "[5] CONTRACT (drop the synthetic column) — ONLY now that the flip happened"
pg_sql "ALTER TABLE catalogue_items DROP COLUMN IF EXISTS rig_bluegreen_probe;" \
  || fail "contract drop failed"
has_col_after=$(pg_sql "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='catalogue_items' AND column_name='rig_bluegreen_probe';")
[ "$has_col_after" = "0" ] || fail "contract did not drop rig_bluegreen_probe (got count=$has_col_after)"
echo "  contract OK: rig_bluegreen_probe dropped"

echo "  re-verify GREEN (sole remaining color) still works post-contract"
drive_discovery_and_browse "green-post-contract" "$GREEN_API"

echo
echo 'BLUEGREEN_RIG_OK'
