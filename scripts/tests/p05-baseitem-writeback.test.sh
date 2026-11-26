#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/p05-baseitem-writeback.test.sh
#
# Definition-of-done harness for p05-baseitem-writeback (ROI P0.5, remaining
# item 1). Before this task SwarmHealthWorker wrote at most 2000 phantom
# BaseItem scores every 10 ticks, skipped orphan / non-TMDB channel items
# entirely, and enumerated the whole channel on every sync — so on a catalogue
# above 2000 titles many BaseItems kept CriticRating = NULL, which PostgreSQL's
# DESC sort puts FIRST on the "Peers (DHT)" list.
#
# This harness drives the REAL thing, end to end, with no mocks of Jellyfin or
# of the plugin:
#   * an isolated PostgreSQL 16 (rootless podman, random loopback port) as the
#     plugin's phantom DB (PHANTOM_POSTGRES_*);
#   * the patched Jellyfin server built from the `jellyfin` submodule, started
#     fresh in a private temp dir on a random loopback port (never :8096), with
#     the plugin DLL built from THIS tree;
#   * a deterministic swarm-health service stand-in speaking the real wire
#     contract and logging every observation (p05-baseitem-writeback.swarm-mock.py).
#
# Seeds > 2000 Phantom channel titles through the REAL channel browse path:
# catalogue movies + series (some with measurement candidates, most without —
# NULL until scored), gostream-only orphan movies and orphan series folders
# (no TMDB id), series -> season -> episode items, plus non-Phantom controls
# and stale ratings in jellyfin.db. Then runs ONE configured measurement cycle
# (SwarmHealthRecheckHours=1), killing Jellyfin mid-sweep and restarting it,
# and asserts:
#   A. plugin unit/PG layer: SwarmHealth* xUnit suite (incl. the new sweep /
#      resume / orphan / write-through cases + the Postgres per-batch query),
#      exact count, no skips, against the same PostgreSQL;
#   B. before the cycle: > 2000 phantom titles NULL, and PostgreSQL DESC puts
#      NULLs first (the bug);
#   C. cancellation/restart: SIGTERM mid-sweep leaves a persisted, unfinished
#      cursor + partially scored catalogue; the restarted process resumes the
#      SAME sweep and reads each title exactly once in total;
#   D. full convergence inside the cycle: every phantom movie/series/orphan
#      BaseItem non-NULL and equal to the live > unmeasured(0.5) > dead(0)
#      policy computed independently from the service's logged observations
#      (series = best episode swarm); episodes/seasons and non-Phantom controls
#      byte-for-byte unchanged; sweep duration < cycle;
#   E. bounded cost: every sync step reads <= SwarmHealthSyncBatchSize rows,
#      the sweep took >= ceil(N/batch) steps, the logged keyset statement is an
#      index SEARCH on IX_BaseItems_Type_TopParentId_Id (no table SCAN), and the
#      per-batch swarm_health lookup uses idx_swarm_health_item on PostgreSQL;
#   F. no DHT admission change: the service was asked ONLY about seeded
#      candidate infohashes (never orphans/unmeasured titles), <= 256 per call;
#   G. REST first pages for BOTH channels — default order and Peers
#      (CriticRating) DESC — carry exactly the scores the service observations
#      imply, DESC is live-by-size then unmeasured then dead with stable
#      pagination, and PostgreSQL DESC over the converged ratings has no NULLs;
#   H. no schema change: phantom schema version unchanged and the cursor lives
#      in the existing plugin_meta table (old/new color overlap safe).
#
# No production data, host config, shared rig or prod port is touched: every
# process, port, directory and container is owned by this run and torn down on
# EXIT. No bulk availability UPDATE, no prod flip.
#
# No optional skip: missing podman/dotnet/python3/sqlite3/curl is a FAILURE.
# Exit 0 = all assertions passed.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
TEST_PROJ="$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/Jellyfin.Plugin.PhantomLibrary.Tests.csproj"
PLUGIN_PROJ="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Jellyfin.Plugin.PhantomLibrary.csproj"
JF_PROJ="$REPO_ROOT/jellyfin/Jellyfin.Server/Jellyfin.Server.csproj"
JF_DLL="$REPO_ROOT/jellyfin/Jellyfin.Server/bin/Release/net9.0/jellyfin.dll"
MOCK="$HERE/p05-baseitem-writeback.swarm-mock.py"
PG_IMAGE="${PHANTOM_TEST_POSTGRES_IMAGE:-docker.io/library/postgres:16-alpine}"
EXPECTED_UNIT_TESTS=29

# Catalogue shape (> 2000 scored titles in total).
N_MOVIES=1700          # catalogue movies (tmdb 7000001..)
N_MOVIE_CAND=260       # of which have a measurement candidate infohash
N_SERIES=600           # catalogue series (tmdb 7100001..)
N_SERIES_CAND=120      # of which have candidate episode infohashes (2 episodes each)
N_ORPHAN_MOVIES=25     # gostream-only movie files (no TMDB id)
N_ORPHAN_SERIES=10     # gostream-only series folders (no TMDB id)
SYNC_BATCH=400         # SwarmHealthSyncBatchSize under test
INTERVAL=10            # SwarmHealthIntervalSeconds (plugin minimum)
RECHECK_H=1            # SwarmHealthRecheckHours = the configured measurement cycle

MOVIES_CH=80089d10394fb545b5e4d7d56a872393
SHOWS_CH=40ab6e9af516a84f46dcea7140855d88
MOVIES_CH_U=80089D10-394F-B545-B5E4-D7D56A872393
SHOWS_CH_U=40AB6E9A-F516-A84F-46DC-EA7140855D88
T_MOVIE=MediaBrowser.Controller.Entities.Movies.Movie
T_SERIES=MediaBrowser.Controller.Entities.TV.Series
T_FOLDER=MediaBrowser.Controller.Entities.Folder

pass_count=0
fail_count=0
ok()    { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()   { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
check() { local d="$1"; shift; if "$@"; then ok "$d"; else bad "$d"; fi; }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

for t in podman dotnet python3 sqlite3 curl; do
    command -v "$t" >/dev/null 2>&1 || fatal "$t not found (required; no skip)"
done
[[ -f "$TEST_PROJ" && -f "$PLUGIN_PROJ" && -f "$MOCK" ]] || fatal "repo layout incomplete"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/p05-writeback.XXXXXX")"
PGC="phantom-p05-writeback-$$-$RANDOM"
JF_PID=""
MOCK_PID=""
cleanup() {
    local rc=$?
    [[ -n "$JF_PID" ]] && kill -9 "$JF_PID" >/dev/null 2>&1 || true
    [[ -n "$MOCK_PID" ]] && kill -9 "$MOCK_PID" >/dev/null 2>&1 || true
    dotnet build-server shutdown >/dev/null 2>&1 || true
    podman rm -f "$PGC" >/dev/null 2>&1 || true
    if [[ "$rc" -ne 0 && -n "${P05_KEEP_WORK:-}" ]]; then
        echo "work dir kept: $WORK"
    else
        rm -rf "$WORK"
    fi
    exit "$rc"
}
trap cleanup EXIT

free_port() { python3 -c 'import socket;s=socket.socket();s.bind(("127.0.0.1",0));print(s.getsockname()[1]);s.close()'; }
JF_PORT="$(free_port)"; MOCK_PORT="$(free_port)"
[[ "$JF_PORT" != 8096 ]] || fatal "refusing the production Jellyfin port"
B="http://127.0.0.1:$JF_PORT"
JFDB="$WORK/data/data/jellyfin.db"
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

# ---------------------------------------------------------------------------
head_ "0. build: jellyfin submodule + patched server + plugin from this tree"
if [[ ! -f "$REPO_ROOT/jellyfin/Jellyfin.Data/Jellyfin.Data.csproj" ]]; then
    ref_args=()
    if [[ -n "${PHANTOM_JELLYFIN_REFERENCE:-}" && -d "$PHANTOM_JELLYFIN_REFERENCE" ]]; then
        ref_args=(--reference "$PHANTOM_JELLYFIN_REFERENCE")
    fi
    git -C "$REPO_ROOT" -c protocol.file.allow=always submodule update --init "${ref_args[@]}" jellyfin \
        || fatal "could not initialise the jellyfin submodule"
fi
dotnet build "$JF_PROJ" -c Release -p:UseSharedCompilation=false -v q >"$WORK/jf-build.log" 2>&1 \
    || { tail -30 "$WORK/jf-build.log"; fatal "patched Jellyfin build failed"; }
dotnet publish "$PLUGIN_PROJ" -c Release -o "$WORK/plugin-pub" -p:UseSharedCompilation=false -v q >"$WORK/plugin-build.log" 2>&1 \
    || { tail -30 "$WORK/plugin-build.log"; fatal "plugin build failed"; }
[[ -f "$JF_DLL" ]] || fatal "jellyfin.dll not produced"
PLUGIN_VERSION="$(sed -n 's:.*<AssemblyVersion>\(.*\)</AssemblyVersion>.*:\1:p' "$PLUGIN_PROJ")"
echo "   jellyfin $(git -C "$REPO_ROOT/jellyfin" rev-parse --short HEAD), plugin $PLUGIN_VERSION"

# ---------------------------------------------------------------------------
head_ "1. isolated PostgreSQL ($PG_IMAGE)"
PGPASS="p05-$RANDOM$RANDOM"
PGPORT=""
for _ in 1 2 3 4 5; do
    PGPORT="$(free_port)"
    if podman run -d --name "$PGC" -e POSTGRES_USER=phantom -e POSTGRES_PASSWORD="$PGPASS" -e POSTGRES_DB=phantom \
            -p "127.0.0.1:$PGPORT:5432" "$PG_IMAGE" >"$WORK/pg-start.log" 2>&1; then
        break
    fi
    podman rm -f "$PGC" >/dev/null 2>&1 || true
    PGPORT=""
done
[[ -n "$PGPORT" ]] || { cat "$WORK/pg-start.log" >&2; fatal "could not start PostgreSQL"; }
psqlc() { podman exec -i -e PGPASSWORD="$PGPASS" "$PGC" psql -h 127.0.0.1 -U phantom -d "${PSQL_DB:-phantom}" -v ON_ERROR_STOP=1 -qtA "$@"; }
for _ in $(seq 1 90); do
    psqlc -c 'SELECT 1' 2>/dev/null | grep -qx 1 && break
    sleep 1
done
psqlc -c 'SELECT 1' | grep -qx 1 || fatal "PostgreSQL never became ready"
psqlc -c 'CREATE DATABASE unit_tests;' >/dev/null
echo "   postgres $(psqlc -c 'SHOW server_version') on 127.0.0.1:$PGPORT"

# ---------------------------------------------------------------------------
head_ "A. plugin unit + PostgreSQL layer (SwarmHealth* suites, exact count, no skip)"
export PHANTOM_TEST_POSTGRES_DSN="Host=127.0.0.1;Port=$PGPORT;Username=phantom;Password=$PGPASS;Database=unit_tests;Pooling=true"
export PHANTOM_TEST_POSTGRES_REQUIRED=1
set +e
dotnet test "$TEST_PROJ" -p:UseSharedCompilation=false --filter "FullyQualifiedName~SwarmHealth" \
    --logger "trx;LogFileName=unit.trx" --results-directory "$WORK/results" >"$WORK/unit.log" 2>&1
rc=$?
set -e
unset PHANTOM_TEST_POSTGRES_DSN PHANTOM_TEST_POSTGRES_REQUIRED
if [[ "$rc" -ne 0 ]]; then tail -40 "$WORK/unit.log"; fi
check "dotnet test SwarmHealth* exit 0" test "$rc" -eq 0
unit_summary="$(python3 - "$WORK/results/unit.trx" "$EXPECTED_UNIT_TESTS" <<'PY'
import sys, xml.etree.ElementTree as ET
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
res = ET.parse(sys.argv[1]).getroot().findall(".//t:UnitTestResult", ns)
outcomes = {}
for r in res:
    outcomes[r.get("outcome")] = outcomes.get(r.get("outcome"), 0) + 1
print(f"{len(res)} {outcomes.get('Passed', 0)} {outcomes}")
PY
)"
read -r unit_total unit_passed _ <<<"$unit_summary"
echo "   $unit_summary"
check "exactly $EXPECTED_UNIT_TESTS SwarmHealth* tests executed and passed (got $unit_passed/$unit_total)" \
    test "$unit_total" -eq "$EXPECTED_UNIT_TESTS" -a "$unit_passed" -eq "$EXPECTED_UNIT_TESTS"

# ---------------------------------------------------------------------------
head_ "2. fresh Jellyfin + plugin (127.0.0.1:$JF_PORT) on PostgreSQL phantom DB"
mkdir -p "$WORK"/{data,config,cache,log,gostream/movies,gostream/tv} "$WORK/data/plugins/configurations"
PD="$WORK/data/plugins/PhantomLibrary_$PLUGIN_VERSION"
mkdir -p "$PD"
# Exactly the assemblies the server does not already ship (what the image bakes).
for f in "$WORK"/plugin-pub/*.dll; do
    b="$(basename "$f")"
    [[ -f "$(dirname "$JF_DLL")/$b" ]] || cp "$f" "$PD/"
done
[[ -f "$PD/Jellyfin.Plugin.PhantomLibrary.dll" && -f "$PD/Npgsql.dll" ]] || fatal "plugin payload incomplete"
cat >"$PD/meta.json" <<META
{"category":"Metadata","changelog":"p05 rig","description":"p05 rig","guid":"9e7a1f4c-2b5d-4e8f-9a3b-7c1d2e5f6a8b","name":"Phantom Library","overview":"p05 rig","owner":"spencerharmon","targetAbi":"10.11.0.0","timestamp":"0001-01-01T00:00:00.0000000Z","version":"$PLUGIN_VERSION","status":"Active","autoUpdate":false,"assemblies":[]}
META
cat >"$WORK/config/network.xml" <<NET
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration><InternalHttpPort>$JF_PORT</InternalHttpPort><PublicHttpPort>$JF_PORT</PublicHttpPort><AutoDiscovery>false</AutoDiscovery><EnableIPv6>false</EnableIPv6></NetworkConfiguration>
NET
write_plugin_config() {  # $1 = swarm-health service url ('' = worker inert)
    cat >"$WORK/data/plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration>
  <TmdbApiKey>p05-rig</TmdbApiKey>
  <TmdbApiBaseUrl>http://127.0.0.1:9/3</TmdbApiBaseUrl>
  <GostreamBaseUrl>http://127.0.0.1:9</GostreamBaseUrl>
  <GostreamMoviesRoot>$WORK/gostream/movies</GostreamMoviesRoot>
  <GostreamShowsRoot>$WORK/gostream/tv</GostreamShowsRoot>
  <EagerResolveEnabled>false</EagerResolveEnabled>
  <EvictionEnabled>false</EvictionEnabled>
  <SeriesAutopilotEnabled>false</SeriesAutopilotEnabled>
  <SwarmHealthServiceUrl>$1</SwarmHealthServiceUrl>
  <SwarmHealthIntervalSeconds>$INTERVAL</SwarmHealthIntervalSeconds>
  <SwarmHealthBatchSize>256</SwarmHealthBatchSize>
  <SwarmHealthSyncBatchSize>$SYNC_BATCH</SwarmHealthSyncBatchSize>
  <SwarmHealthRecheckHours>$RECHECK_H</SwarmHealthRecheckHours>
  <SwarmHealthRankWindowDays>7</SwarmHealthRankWindowDays>
</PluginConfiguration>
EOF
}
start_jf() {  # $1 = stdout log name
    (
        cd "$WORK/data"
        exec env PHANTOM_POSTGRES_HOST=127.0.0.1 PHANTOM_POSTGRES_PORT="$PGPORT" PHANTOM_POSTGRES_DB=phantom \
            PHANTOM_POSTGRES_USER=phantom PHANTOM_POSTGRES_PASSWORD="$PGPASS" TMPDIR="$WORK/cache" \
            dotnet "$JF_DLL" --datadir "$WORK/data" --configdir "$WORK/config" --cachedir "$WORK/cache" \
            --logdir "$WORK/log" --nowebclient --ffmpeg "$(command -v ffmpeg || echo /usr/bin/ffmpeg)"
    ) >"$WORK/$1" 2>&1 &
    JF_PID=$!
    for _ in $(seq 1 180); do
        kill -0 "$JF_PID" 2>/dev/null || { tail -40 "$WORK/$1"; fatal "jellyfin exited during startup"; }
        [[ "$(curl -s -o /dev/null -w '%{http_code}' "$B/System/Info/Public")" == 200 ]] \
            && [[ "$(curl -s -o /dev/null -w '%{http_code}' "$B/Users/Public")" == 200 ]] && return 0
        sleep 1
    done
    fatal "jellyfin never became ready"
}
stop_jf() {  # graceful (SIGTERM -> IHostedService.StopAsync), bounded
    [[ -n "$JF_PID" ]] || return 0
    kill -TERM "$JF_PID" 2>/dev/null || true
    for _ in $(seq 1 90); do kill -0 "$JF_PID" 2>/dev/null || { JF_PID=""; return 0; }; sleep 1; done
    kill -9 "$JF_PID" 2>/dev/null || true
    JF_PID=""
    fatal "jellyfin did not stop within 90s of SIGTERM"
}

write_plugin_config ""
start_jf jf-1.log
H='Content-Type: application/json'
XA='X-Emby-Authorization: MediaBrowser Client="p05-rig", Device="p05-rig", DeviceId="p05-rig", Version="1"'
retry() {  # the wizard endpoints answer 503 until the host finished post-startup init
    for _ in $(seq 1 60); do "$@" >/dev/null 2>&1 && return 0; sleep 1; done
    fatal "gave up: $*"
}
retry curl -sf -X POST -H "$H" "$B/Startup/Configuration" -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
retry curl -sf "$B/Startup/User"   # GET materialises the wizard's initial user
retry curl -sf -X POST -H "$H" "$B/Startup/User" -d '{"Name":"rig","Password":"rigpass"}'
retry curl -sf -X POST "$B/Startup/Complete"
login() {
    for _ in $(seq 1 60); do
        curl -sf -X POST -H "$H" -H "$XA" "$B/Users/AuthenticateByName" -d '{"Username":"rig","Pw":"rigpass"}' >/dev/null 2>&1 && break
        sleep 1
    done
    TOK="$(curl -sf -X POST -H "$H" -H "$XA" "$B/Users/AuthenticateByName" -d '{"Username":"rig","Pw":"rigpass"}' \
        | python3 -c 'import json,sys;print(json.load(sys.stdin)["AccessToken"])')"
    USER_ID="$(curl -sf -H "X-Emby-Token: $TOK" "$B/Users/Me" | python3 -c 'import json,sys;print(json.load(sys.stdin)["Id"])')"
}
login
api() { curl -sf --max-time 900 -H "X-Emby-Token: $TOK" "$@"; }
for _ in $(seq 1 60); do
    [[ "$(psqlc -c "SELECT to_regclass('public.swarm_health') IS NOT NULL AND to_regclass('public.plugin_meta') IS NOT NULL")" == t ]] && break
    sleep 1
done
SCHEMA_BEFORE="$(psqlc -c 'SELECT version FROM phantom_schema_meta')"
TABLES_BEFORE="$(psqlc -c "SELECT string_agg(table_name, ',' ORDER BY table_name) FROM information_schema.tables WHERE table_schema='public'")"
echo "   phantom schema v$SCHEMA_BEFORE on PostgreSQL; user $USER_ID"

# ---------------------------------------------------------------------------
head_ "3. seed > 2000 Phantom titles through the real channel browse path"
psqlc <<SQL
-- catalogue movies (visible: available, never assessed); candidates carry an infohash
INSERT INTO tmdb_metadata (tmdb_id,type,title,year,fetched_at,relevance_score)
  SELECT 7000000+g,'movie','P05 Movie '||lpad(g::text,4,'0'),2000+g%25,1700000000+g,(g%97)::real FROM generate_series(1,$N_MOVIES) g;
INSERT INTO availability_items (tmdb_id,type,season,episode,status,next_check_at,priority,candidate_info_hash)
  SELECT 7000000+g,'movie',-1,-1,'available',2000000000,0,
         CASE WHEN g <= $N_MOVIE_CAND THEN md5('p05m'||g)||substr(md5('x'||g),1,8) END
  FROM generate_series(1,$N_MOVIES) g;
-- catalogue series; candidate series get TWO episodes with distinct infohashes (series = best)
INSERT INTO tmdb_metadata (tmdb_id,type,title,year,fetched_at,relevance_score)
  SELECT 7100000+g,'series','P05 Show '||lpad(g::text,4,'0'),2000+g%25,1700000000+g,(g%89)::real FROM generate_series(1,$N_SERIES) g;
INSERT INTO availability_items (tmdb_id,type,season,episode,status,next_check_at,priority,candidate_info_hash)
  SELECT 7100000+g,'episode',1,e,'available',2000000000,0,
         CASE WHEN g <= $N_SERIES_CAND THEN md5('p05e'||g||'_'||e)||substr(md5('y'||g||e),1,8) END
  FROM generate_series(1,$N_SERIES) g, generate_series(1,2) e;
INSERT INTO series_episode_catalogue (series_tmdb_id,episode_tmdb_id,season,episode,first_seen_at,last_seen_at)
  SELECT 7100000+g,(7100000+g)*10+e,1,e,1700000000,1700000000 FROM generate_series(1,$N_SERIES) g, generate_series(1,2) e;
INSERT INTO tmdb_episode_cache (series_tmdb_id,season,episode,title,fetched_at)
  SELECT 7100000+g,1,e,'P05 Episode '||e,1700000000 FROM generate_series(1,$N_SERIES) g, generate_series(1,2) e;
SQL
for i in $(seq 1 "$N_ORPHAN_MOVIES"); do head -c 64 /dev/zero >"$WORK/gostream/movies/Gostream Only Feature $i.mkv"; done
for i in $(seq 1 "$N_ORPHAN_SERIES"); do
    d="$WORK/gostream/tv/Gostream Only Series $i/Season 01"; mkdir -p "$d"
    head -c 64 /dev/zero >"$d/Gostream Only Series $i S01E01.mkv"
done
for _ in $(seq 1 60); do  # channel entities are created on the first /Channels listing
    [[ "$(api "$B/Channels?userId=$USER_ID" | python3 -c 'import json,sys;print(len(json.load(sys.stdin)["Items"]))')" -ge 2 ]] && break
    sleep 1
done
api "$B/Channels/$MOVIES_CH/Items?userId=$USER_ID&Limit=1" >/dev/null
api "$B/Channels/$SHOWS_CH/Items?userId=$USER_ID&Limit=1" >/dev/null
# Episode path parity (scenario 36 shape): series -> season -> episodes for 3 measured series.
for s in 1 2 3; do
    sid="$(sqlite3 "$JFDB" "SELECT lower(replace(Id,'-','')) FROM BaseItems WHERE ExternalId='series_$((7100000+s))'")"
    [[ -n "$sid" ]] || fatal "series_$((7100000+s)) BaseItem was not persisted by the shows browse"
    season="$(api "$B/Channels/$SHOWS_CH/Items?userId=$USER_ID&folderId=$sid" | python3 -c 'import json,sys;print(json.load(sys.stdin)["Items"][0]["Id"])')"
    api "$B/Channels/$SHOWS_CH/Items?userId=$USER_ID&folderId=$season" >/dev/null
done
q() { sqlite3 "$JFDB" "$@"; }
SCORED_WHERE="((Type='$T_MOVIE' AND TopParentId='$MOVIES_CH_U' AND ChannelId='$MOVIES_CH_U') OR (Type IN ('$T_SERIES','$T_FOLDER') AND TopParentId='$SHOWS_CH_U' AND ChannelId='$SHOWS_CH_U'))"
N_SCORED="$(q "SELECT count(*) FROM BaseItems WHERE $SCORED_WHERE")"
N_EPISODES="$(q "SELECT count(*) FROM BaseItems WHERE Type='MediaBrowser.Controller.Entities.TV.Episode' AND ChannelId='$SHOWS_CH_U'")"
N_ORPHANS="$(q "SELECT count(*) FROM BaseItems WHERE $SCORED_WHERE AND (ExternalId LIKE 'orphan\\_%' ESCAPE '\\' OR ExternalId LIKE 'orphanseries\\_%' ESCAPE '\\')")"
EXPECTED_SCORED=$((N_MOVIES + N_ORPHAN_MOVIES + N_SERIES + N_ORPHAN_SERIES))
echo "   persisted: $N_SCORED scored titles ($N_ORPHANS orphan/non-TMDB), $N_EPISODES episodes"
check "every seeded title persisted as a channel BaseItem ($N_SCORED == $EXPECTED_SCORED)" test "$N_SCORED" -eq "$EXPECTED_SCORED"
check "> 2000 Phantom titles seeded" test "$N_SCORED" -gt 2000
check "orphan/non-TMDB movies + series persisted ($N_ORPHANS == $((N_ORPHAN_MOVIES + N_ORPHAN_SERIES)))" test "$N_ORPHANS" -eq $((N_ORPHAN_MOVIES + N_ORPHAN_SERIES))
check "episode BaseItems exist (movie/episode parity path)" test "$N_EPISODES" -ge 6

# ---------------------------------------------------------------------------
head_ "4. non-Phantom controls + stale ratings (Jellyfin stopped)"
stop_jf
q <<SQL
CREATE TEMP TABLE tpl AS SELECT * FROM BaseItems WHERE ExternalId='movie_7000001';
UPDATE tpl SET Id='C0000000-0000-0000-0000-000000000001', ChannelId=NULL, TopParentId=NULL, ParentId=NULL, ExternalId=NULL, Name='Control Library Movie', CriticRating=72.5, PresentationUniqueKey='ctl1';
INSERT INTO BaseItems SELECT * FROM tpl;
UPDATE tpl SET Id='C0000000-0000-0000-0000-000000000002', Name='Control Unrated Movie', CriticRating=NULL, PresentationUniqueKey='ctl2';
INSERT INTO BaseItems SELECT * FROM tpl;
UPDATE tpl SET Id='C0000000-0000-0000-0000-000000000003', Type='$T_SERIES', Name='Control Library Series', CriticRating=0.25, PresentationUniqueKey='ctl3';
INSERT INTO BaseItems SELECT * FROM tpl;
-- a foreign channel's movie that happens to look like a phantom id: must be untouched
UPDATE tpl SET Id='C0000000-0000-0000-0000-000000000004', Type='$T_MOVIE', ChannelId='D0000000-0000-0000-0000-00000000000D', TopParentId='D0000000-0000-0000-0000-00000000000D', ExternalId='movie_7000002', Name='Other Channel Movie', CriticRating=33, PresentationUniqueKey='ctl4';
INSERT INTO BaseItems SELECT * FROM tpl;
-- stale phantom ratings from an older policy: must be rewritten
UPDATE BaseItems SET CriticRating=77.7 WHERE ExternalId IN ('movie_7000001','movie_7001500','series_7100002','series_7100500');
SQL
CONTROL_SNAPSHOT="$(q "SELECT Id||'|'||ifnull(CriticRating,'NULL')||'|'||ifnull(ChannelId,'') FROM BaseItems WHERE Id LIKE 'C0000000-%' ORDER BY Id")"
EPISODE_SNAPSHOT="$(q "SELECT Id||'|'||ifnull(CriticRating,'NULL') FROM BaseItems WHERE ChannelId='$SHOWS_CH_U' AND Type IN ('MediaBrowser.Controller.Entities.TV.Episode','MediaBrowser.Controller.Entities.TV.Season') ORDER BY Id")"
NULL_BEFORE="$(q "SELECT count(*) FROM BaseItems WHERE $SCORED_WHERE AND CriticRating IS NULL")"
check "4 stale phantom ratings seeded" test "$(q "SELECT count(*) FROM BaseItems WHERE $SCORED_WHERE AND abs(CriticRating-77.7)<0.001")" -eq 4
check "> 2000 Phantom titles start with CriticRating NULL ($NULL_BEFORE)" test "$NULL_BEFORE" -gt 2000

pg_desc_page() {  # stage (Id, CriticRating) of the scored titles into PostgreSQL; print the DESC first-page ratings
    q -csv "SELECT Id, CriticRating FROM BaseItems WHERE $SCORED_WHERE" >"$WORK/ratings.csv"
    PSQL_DB=unit_tests psqlc -c 'DROP TABLE IF EXISTS p05_ratings; CREATE TABLE p05_ratings (id text PRIMARY KEY, critic_rating real);' >/dev/null
    PSQL_DB=unit_tests psqlc -c '\copy p05_ratings FROM STDIN WITH (FORMAT csv)' <"$WORK/ratings.csv"
    PSQL_DB=unit_tests psqlc -c "SELECT coalesce(critic_rating::text,'NULL') FROM p05_ratings ORDER BY critic_rating DESC, id LIMIT 50"
}
PG_BEFORE="$(pg_desc_page)"
check "bug reproduced: PostgreSQL DESC first page is all NULL before the cycle" \
    test "$(grep -c '^NULL$' <<<"$PG_BEFORE")" -eq 50

# ---------------------------------------------------------------------------
head_ "5. one measurement cycle, interrupted mid-sweep (SIGTERM) and resumed"
python3 "$MOCK" --port "$MOCK_PORT" --log "$WORK/observations.jsonl" >"$WORK/mock.log" 2>&1 &
MOCK_PID=$!
for _ in $(seq 1 30); do curl -sf "http://127.0.0.1:$MOCK_PORT/healthz" >/dev/null && break; sleep 0.2; done
write_plugin_config "http://127.0.0.1:$MOCK_PORT"
CYCLE_START=$(date +%s)
start_jf jf-2.log
login
steps_in() { grep -c 'Swarm-health score sync step: sweep=1 read=[1-9]' "$WORK/$1" || true; }
for _ in $(seq 1 120); do [[ "$(steps_in jf-2.log)" -ge 2 ]] && break; sleep 1; done
[[ "$(steps_in jf-2.log)" -ge 2 ]] || { tail -60 "$WORK/jf-2.log"; fatal "no sync steps observed"; }
stop_jf
CURSOR_MID="$(psqlc -c "SELECT value FROM plugin_meta WHERE key='swarm_health_sync_cursor'")"
echo "   cursor after SIGTERM: $CURSOR_MID"
check "persisted cursor exists and is NOT done after the interrupt" \
    python3 -c 'import json,sys;c=json.loads(sys.argv[1]);sys.exit(0 if c["sweep"]==1 and not c["done"] and c["read"]>0 else 1)' "$CURSOR_MID"
NULL_MID="$(q "SELECT count(*) FROM BaseItems WHERE $SCORED_WHERE AND CriticRating IS NULL")"
check "catalogue only partially scored at the interrupt ($NULL_MID NULL left of $N_SCORED)" test "$NULL_MID" -gt 0 -a "$NULL_MID" -lt "$NULL_BEFORE"
check "worker logged a clean stop" grep -q 'Swarm-health worker stopped' "$WORK/jf-2.log"

start_jf jf-3.log
login
for _ in $(seq 1 $((RECHECK_H * 3600))); do
    grep -q 'Swarm-health score sweep 1 completed' "$WORK/jf-3.log" && break
    kill -0 "$JF_PID" 2>/dev/null || fatal "jellyfin died during the cycle"
    sleep 1
done
CYCLE_SECS=$(( $(date +%s) - CYCLE_START ))
grep -q 'Swarm-health score sweep 1 completed' "$WORK/jf-3.log" || fatal "sweep 1 did not complete inside the ${RECHECK_H}h cycle"
# let in-flight measurement ticks (write-through) drain: wait until every candidate was asked
for _ in $(seq 1 120); do
    asked="$(python3 -c 'import json,sys;print(len({h for l in open(sys.argv[1]) for h in json.loads(l)["requested"]}))' "$WORK/observations.jsonl" 2>/dev/null || echo 0)"
    [[ "$asked" -ge $((N_MOVIE_CAND + 2 * N_SERIES_CAND)) ]] && break
    sleep 1
done
sleep $((INTERVAL + 5))
stop_jf   # flush; assertions below read the DBs directly
COMPLETED_LINE="$(grep -h 'Swarm-health score sweep 1 completed' "$WORK/jf-3.log" | tail -1)"
echo "   $COMPLETED_LINE"
check "restarted process resumed sweep 1 (no 'sweep 1 started' after restart)" bash -c "! grep -q 'Swarm-health score sweep 1 started' '$WORK/jf-3.log'"
SWEEP_READ="$(sed -n 's/.*completed: read=\([0-9]*\) .*/\1/p' <<<"$COMPLETED_LINE")"
SWEEP_TOOK="$(sed -n 's/.* took=\([0-9]*\)s.*/\1/p' <<<"$COMPLETED_LINE")"
check "sweep read every title exactly once across the restart ($SWEEP_READ == $N_SCORED)" test "$SWEEP_READ" -eq "$N_SCORED"
check "sweep finished inside one measurement cycle (${SWEEP_TOOK}s < $((RECHECK_H * 3600))s; wall ${CYCLE_SECS}s)" test "$SWEEP_TOOK" -lt $((RECHECK_H * 3600))

# ---------------------------------------------------------------------------
head_ "D. convergence vs the service's observations"
q -separator '|' "SELECT Id, Type, ExternalId, ifnull(CriticRating,'NULL') FROM BaseItems WHERE $SCORED_WHERE" >"$WORK/scored.txt"
python3 - "$WORK/scored.txt" "$WORK/observations.jsonl" "$WORK/expected.tsv" "$N_MOVIE_CAND" "$N_SERIES_CAND" <<'PY' >"$WORK/convergence.txt"
import hashlib, json, sys
scored, obs, out, nmc, nsc = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4]), int(sys.argv[5])
answers = {}
for line in open(obs):
    rec = json.loads(line)
    for r in rec["results"]:
        answers[r["info_hash"]] = r["dht_peers"]
def h(seed, tail):
    return hashlib.md5(seed.encode()).hexdigest() + hashlib.md5(tail.encode()).hexdigest()[:8]
movie_hash = {7000000 + g: h(f"p05m{g}", f"x{g}") for g in range(1, nmc + 1)}
series_hashes = {7100000 + g: [h(f"p05e{g}_{e}", f"y{g}{e}") for e in (1, 2)] for g in range(1, nsc + 1)}
def score(peers):
    if peers is None:
        return 0.5
    return 0.0 if peers <= 0 else float(min(peers, 100))
bad = nulls = 0
live = dead = unmeasured = 0
with open(out, "w") as fo:
    for line in open(scored):
        iid, typ, ext, rating = line.rstrip("\n").split("|")
        if ext.startswith("movie_"):
            hh = movie_hash.get(int(ext[6:]))
            exp = score(answers.get(hh)) if hh else 0.5
        elif ext.startswith("series_"):
            obs_e = [answers[x] for x in series_hashes.get(int(ext[7:]), []) if x in answers]
            exp = score(max(obs_e)) if obs_e else 0.5
        elif ext.startswith("orphan_") or ext.startswith("orphanseries_"):
            exp = 0.5
        else:
            print(f"unexpected scored row {ext}"); bad += 1; continue
        if rating == "NULL":
            nulls += 1
        elif abs(float(rating) - exp) > 0.01:
            bad += 1
            if bad <= 10:
                print(f"MISMATCH {ext}: stored {rating} expected {exp}")
        if exp > 0.5: live += 1
        elif exp == 0.5: unmeasured += 1
        else: dead += 1
        fo.write(f"{iid}\t{ext}\t{exp}\n")
print(f"nulls={nulls} mismatches={bad} live={live} unmeasured={unmeasured} dead={dead}")
PY
cat "$WORK/convergence.txt"
summary="$(tail -1 "$WORK/convergence.txt")"
check "zero NULL scores after one cycle" grep -q '^nulls=0 ' <<<"$summary"
check "every score equals live>unmeasured>dead policy of the service observations" grep -q ' mismatches=0 ' <<<"$summary"
check "live, unmeasured and dead titles all present" python3 -c 'import re,sys;d=dict(re.findall(r"(\w+)=(\d+)",sys.argv[1]));sys.exit(0 if all(int(d[k])>0 for k in ("live","unmeasured","dead")) else 1)' "$summary"
check "orphan/non-TMDB titles scored unmeasured (0.5)" test "$(q "SELECT count(*) FROM BaseItems WHERE $SCORED_WHERE AND (ExternalId LIKE 'orphan%') AND abs(CriticRating-0.5)<0.001")" -eq "$N_ORPHANS"
check "stale phantom ratings (77.7, measured + unmeasured titles) rewritten" test "$(q "SELECT count(*) FROM BaseItems WHERE $SCORED_WHERE AND abs(CriticRating-77.7)<0.001")" -eq 0
check "non-Phantom controls unchanged" test "$(q "SELECT Id||'|'||ifnull(CriticRating,'NULL')||'|'||ifnull(ChannelId,'') FROM BaseItems WHERE Id LIKE 'C0000000-%' ORDER BY Id")" == "$CONTROL_SNAPSHOT"
check "episodes/seasons untouched" test "$(q "SELECT Id||'|'||ifnull(CriticRating,'NULL') FROM BaseItems WHERE ChannelId='$SHOWS_CH_U' AND Type IN ('MediaBrowser.Controller.Entities.TV.Episode','MediaBrowser.Controller.Entities.TV.Season') ORDER BY Id")" == "$EPISODE_SNAPSHOT"

# ---------------------------------------------------------------------------
head_ "E. bounded batches + indexed keyset access"
cat "$WORK/jf-2.log" "$WORK/jf-3.log" | grep -h 'Swarm-health score sync step: sweep=1 ' >"$WORK/steps.txt" || true
MAX_READ="$(sed -n 's/.* read=\([0-9]*\) .*/\1/p' "$WORK/steps.txt" | sort -n | tail -1)"
N_STEPS="$(grep -c ' read=[1-9]' "$WORK/steps.txt" || true)"
MIN_STEPS=$(( (N_SCORED + SYNC_BATCH - 1) / SYNC_BATCH ))
echo "   $N_STEPS non-empty steps, max read/step $MAX_READ (batch $SYNC_BATCH, N $N_SCORED)"
check "no sync step read more than SwarmHealthSyncBatchSize rows" test "$MAX_READ" -le "$SYNC_BATCH"
check "the sweep was spread over >= ceil(N/batch) = $MIN_STEPS steps" test "$N_STEPS" -ge "$MIN_STEPS"
KEYSET_SQL="$(grep -h -o 'via IX_BaseItems_Type_TopParentId_Id: SELECT .*LIMIT @limit' "$WORK/jf-2.log" | head -1 | sed 's/^via IX_BaseItems_Type_TopParentId_Id: //')"
[[ -n "$KEYSET_SQL" ]] || fatal "keyset statement not logged at sweep start"
some_id="$(q "SELECT Id FROM BaseItems WHERE Type='$T_MOVIE' AND TopParentId='$MOVIES_CH_U' ORDER BY Id LIMIT 1 OFFSET 700")"
plan_sql="$(sed -e "s/@type/'$T_MOVIE'/" -e "s/@top/'$MOVIES_CH_U'/g" -e "s/@after/'$some_id'/" -e "s/@limit/$SYNC_BATCH/" <<<"$KEYSET_SQL")"
PLAN="$(q "EXPLAIN QUERY PLAN $plan_sql")"
echo "   plan: $(tr '\n' ' ' <<<"$PLAN")"
check "logged keyset statement is an index SEARCH on IX_BaseItems_Type_TopParentId_Id" grep -q 'SEARCH BaseItems USING INDEX IX_BaseItems_Type_TopParentId_Id (Type=? AND TopParentId=? AND Id>?)' <<<"$PLAN"
check "no full BaseItems SCAN and no temp sort" bash -c "! grep -Eq 'SCAN BaseItems|USE TEMP B-TREE' <<<'$PLAN'"
check "the old per-sync full channel enumeration is gone (no GetItemList in the worker)" bash -c "! grep -q 'GetItemList' '$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Scheduled/SwarmHealthWorker.cs'"
PG_PLAN="$(psqlc <<'SQL'
ANALYZE swarm_health;
SET enable_seqscan = off;
EXPLAIN SELECT tmdb_id, MAX(dht_peers) FROM swarm_health WHERE type = 'movie' AND tmdb_id IN (7000001,7000002,7000003) AND checked_at >= 0 GROUP BY tmdb_id;
SQL
)"
check "per-batch swarm_health lookup is served by idx_swarm_health_item on PostgreSQL" grep -q 'idx_swarm_health_item' <<<"$PG_PLAN"

# ---------------------------------------------------------------------------
head_ "F. score backfill is not DHT admission"
python3 - "$WORK/observations.jsonl" "$N_MOVIE_CAND" "$N_SERIES_CAND" <<'PY' >"$WORK/admission.txt"
import hashlib, json, sys
obs, nmc, nsc = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
def h(seed, tail):
    return hashlib.md5(seed.encode()).hexdigest() + hashlib.md5(tail.encode()).hexdigest()[:8]
seeded = {h(f"p05m{g}", f"x{g}") for g in range(1, nmc + 1)} | {h(f"p05e{g}_{e}", f"y{g}{e}") for g in range(1, nsc + 1) for e in (1, 2)}
asked, max_batch, calls = set(), 0, 0
for line in open(obs):
    r = json.loads(line)["requested"]
    calls += 1; max_batch = max(max_batch, len(r)); asked |= set(r)
print(f"calls={calls} max_batch={max_batch} asked={len(asked)} seeded={len(seeded)} extra={len(asked - seeded)} missing={len(seeded - asked)}")
PY
cat "$WORK/admission.txt"
check "service asked ONLY about seeded candidate infohashes (no orphans/unmeasured/catalogue sweep)" grep -q ' extra=0 ' "$WORK/admission.txt"
check "every seeded candidate was measured inside the cycle" grep -q ' missing=0$' "$WORK/admission.txt"
check "measurement batches stayed <= SwarmHealthBatchSize (256)" python3 -c 'import re,sys;sys.exit(0 if int(re.search(r"max_batch=(\d+)",open(sys.argv[1]).read()).group(1))<=256 else 1)' "$WORK/admission.txt"

# ---------------------------------------------------------------------------
head_ "G. REST default + Peers DESC first pages vs service observations (movies AND shows)"
start_jf jf-4.log
login
rest_check() {  # $1 channel id  $2 label
    local ch="$1" label="$2"
    api "$B/Items?ParentId=$ch&userId=$USER_ID&Limit=50&StartIndex=0" >"$WORK/rest-default-$label.json"
    api "$B/Items?ParentId=$ch&userId=$USER_ID&SortBy=CriticRating,SortName&SortOrder=Descending,Ascending&Limit=50&StartIndex=0" >"$WORK/rest-desc1-$label.json"
    api "$B/Items?ParentId=$ch&userId=$USER_ID&SortBy=CriticRating,SortName&SortOrder=Descending,Ascending&Limit=50&StartIndex=50" >"$WORK/rest-desc2-$label.json"
    python3 - "$WORK/expected.tsv" "$WORK/rest-default-$label.json" "$WORK/rest-desc1-$label.json" "$WORK/rest-desc2-$label.json" "$label" <<'PY'
import json, sys
exp_rows = [l.rstrip("\n").split("\t") for l in open(sys.argv[1])]
label = sys.argv[5]
want_prefixes = ("movie_", "orphan_") if label == "movies" else ("series_", "orphanseries_")
exp = {iid.replace("-", "").lower(): float(score) for iid, ext, score in exp_rows if ext.startswith(want_prefixes)}
default = json.load(open(sys.argv[2]))["Items"]
d1 = json.load(open(sys.argv[3]))["Items"]
d2 = json.load(open(sys.argv[4]))["Items"]
fails = []
def stored(i):
    return i.get("CriticRating")
for name, page in (("default", default), ("desc p1", d1), ("desc p2", d2)):
    if len(page) != 50:
        fails.append(f"{name}: {len(page)} items")
    for i in page:
        if i["Id"] not in exp:
            fails.append(f"{name}: unexpected item {i['Name']}")
        elif stored(i) is None or abs(stored(i) - exp[i["Id"]]) > 0.01:
            fails.append(f"{name}: {i['Name']} rating {stored(i)} != observed policy {exp[i['Id']]}")
ranked = sorted(exp.values(), reverse=True)
got = [stored(i) for i in d1 + d2]
if any(g is None for g in got) or [round(g, 2) for g in got] != [round(r, 2) for r in ranked[:100]]:
    fails.append(f"desc pages 1+2 score sequence != independent ranking of observations: {got[:12]}... vs {ranked[:12]}...")
if len({i['Id'] for i in d1} & {i['Id'] for i in d2}):
    fails.append("desc pagination overlaps between page 1 and page 2")
top_live = sum(1 for v in exp.values() if v > 0.5)
print(f"{label}: default p1 ok={not any(f.startswith('default') for f in fails)}; desc top={got[:5]}; live titles={top_live}; "
      f"unmeasured={sum(1 for v in exp.values() if v == 0.5)}; dead={sum(1 for v in exp.values() if v == 0)}")
for f in fails[:15]:
    print("  " + f)
sys.exit(1 if fails else 0)
PY
}
check "movies channel: default + Peers DESC pages match service observations" rest_check "$MOVIES_CH" movies
check "shows channel: default + Peers DESC pages match service observations" rest_check "$SHOWS_CH" shows
check "Peers DESC ends with dead (0) below unmeasured (0.5): last page tail" bash -c "
  api() { curl -sf --max-time 300 -H 'X-Emby-Token: $TOK' \"\$@\"; }
  n=\$(api '$B/Items?ParentId=$MOVIES_CH&userId=$USER_ID&Limit=0' | python3 -c 'import json,sys;print(json.load(sys.stdin)[\"TotalRecordCount\"])')
  api \"$B/Items?ParentId=$MOVIES_CH&userId=$USER_ID&SortBy=CriticRating,SortName&SortOrder=Descending,Ascending&Limit=5&StartIndex=\$((n-5))\" \
    | python3 -c 'import json,sys;r=[i.get(\"CriticRating\") for i in json.load(sys.stdin)[\"Items\"]];print(\"   tail\",r);sys.exit(0 if r and all(x==0 for x in r) else 1)'"
PG_AFTER="$(pg_desc_page)"
check "PostgreSQL DESC over the converged ratings: no NULL on the first page" test "$(grep -c '^NULL$' <<<"$PG_AFTER" || true)" -eq 0
check "PostgreSQL DESC first page == independent ranking of observations" python3 -c '
import sys
pg=[float(x) for x in sys.argv[1].split()]
exp=sorted((float(l.split("\t")[2]) for l in open(sys.argv[2])), reverse=True)[:50]
sys.exit(0 if [round(x,2) for x in pg]==[round(x,2) for x in exp] else 1)' "$PG_AFTER" "$WORK/expected.tsv"
stop_jf

# ---------------------------------------------------------------------------
head_ "H. no schema change (old/new color overlap)"
check "phantom schema version unchanged (v$SCHEMA_BEFORE)" test "$(psqlc -c 'SELECT version FROM phantom_schema_meta')" == "$SCHEMA_BEFORE"
check "no table added/removed in the phantom DB" test "$(psqlc -c "SELECT string_agg(table_name, ',' ORDER BY table_name) FROM information_schema.tables WHERE table_schema='public'")" == "$TABLES_BEFORE"
check "sweep cursor stored in the existing plugin_meta table, marked done" \
    python3 -c 'import json,sys;c=json.loads(sys.argv[1]);sys.exit(0 if c["done"] and c["sweep"]==1 else 1)' "$(psqlc -c "SELECT value FROM plugin_meta WHERE key='swarm_health_sync_cursor'")"

# ---------------------------------------------------------------------------
printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[[ "$fail_count" -eq 0 ]] || exit 1
echo "p05-baseitem-writeback: PASS ($N_SCORED Phantom titles converged in one ${RECHECK_H}h cycle; sweep ${SWEEP_TOOK}s)"
