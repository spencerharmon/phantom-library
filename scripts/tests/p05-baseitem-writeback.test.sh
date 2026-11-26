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
#      in the existing plugin_meta table (old/new color overlap safe);
#   I. two independent libraries, one shared Phantom DB (review 3): a SECOND
#      Jellyfin (own data dir => own server id, own library DB, smaller batch)
#      runs CONCURRENTLY against the same PostgreSQL, both are SIGTERMed
#      mid-sweep and restarted, and an unqualified legacy cursor claiming a
#      sweep in progress sits in plugin_meta. BOTH libraries must converge to
#      zero NULL / zero mismatch, each with its OWN completed cursor (read==N);
#      the legacy key is neither trusted nor rewritten;
#   E2. no per-tick full scan, server side (review 5): pg_stat_statements over
#      the whole two-library cycle shows ZERO whole-table swarm_health
#      aggregates (main's per-tick metadata-provider cache refresh is gone) and
#      only bounded tmdb_id IN (...) lookups;
#   K. metadata refresh (review 5): core's replace-all refresh re-NULLs
#      CriticRating and SwarmHealthMetadataProvider re-applies it. A sample of
#      live/dead/unmeasured/orphan movies, series and orphan series folders is
#      NULLed with the sweep held idle, a forced FullRefresh(replaceAllMetadata)
#      runs through the REST API, and every sample must come back non-NULL and
#      equal to the observation policy, via the provider's bounded lookups;
#   J. title gauges (review 3): phantom_swarm_health_titles{type,health} scraped
#      from each server's /metrics at a completed sweep equals the movie/series
#      live/dead counts implied by the service observations, and after a
#      restart the value is republished from the persisted cursor before any
#      new sweep completes (no full scan).
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
EXPECTED_UNIT_TESTS=41

# Catalogue shape (> 2000 scored titles in total).
N_MOVIES=1700          # catalogue movies (tmdb 7000001..)
N_MOVIE_CAND=260       # of which have a measurement candidate infohash
N_SERIES=600           # catalogue series (tmdb 7100001..)
N_SERIES_CAND=120      # of which have candidate episode infohashes (2 episodes each)
N_ORPHAN_MOVIES=25     # gostream-only movie files (no TMDB id)
N_ORPHAN_SERIES=10     # gostream-only series folders (no TMDB id)
SYNC_BATCH=400         # SwarmHealthSyncBatchSize under test (library A)
SYNC_BATCH_B=200       # library B: a slower sweep, so both overlap and interleave
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
JF_LOG=""
MOCK_PID=""
JROOT="$WORK"          # data/config/cache/log root of the instance being driven
B_PID=""; B_LOG=""; B_TOK=""; B_USER_ID=""
cleanup() {
    local rc=$?
    [[ -n "$JF_PID" ]] && kill -9 "$JF_PID" >/dev/null 2>&1 || true
    [[ -n "$B_PID" ]] && kill -9 "$B_PID" >/dev/null 2>&1 || true
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
JF_PORT="$(free_port)"; MOCK_PORT="$(free_port)"; JF_PORT_B="$(free_port)"
[[ "$JF_PORT" != 8096 && "$JF_PORT_B" != 8096 ]] || fatal "refusing the production Jellyfin port"
B="http://127.0.0.1:$JF_PORT"
JFDB="$WORK/data/data/jellyfin.db"
# Library B: a second, independent Jellyfin server sharing ONLY the Phantom PostgreSQL.
WORK_B="$WORK/libB"
BB="http://127.0.0.1:$JF_PORT_B"
JFDB_B="$WORK_B/data/data/jellyfin.db"
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
            -p "127.0.0.1:$PGPORT:5432" "$PG_IMAGE" \
            -c shared_preload_libraries=pg_stat_statements -c pg_stat_statements.track=all >"$WORK/pg-start.log" 2>&1; then
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
# Statement statistics: section E2 proves from the server side that no worker runs a
# whole-table swarm_health aggregate per tick (review 5: provider cache refresh).
psqlc -c 'CREATE EXTENSION IF NOT EXISTS pg_stat_statements;' >/dev/null
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
mkdir -p "$WORK"/{gostream/movies,gostream/tv}
setup_instance() {  # $1 root  $2 port: a private Jellyfin data/config tree with the plugin installed
    local root="$1" port="$2" f b
    mkdir -p "$root"/{data,config,cache,log} "$root/data/plugins/configurations"
    local pd="$root/data/plugins/PhantomLibrary_$PLUGIN_VERSION"
    mkdir -p "$pd"
    # Exactly the assemblies the server does not already ship (what the image bakes).
    for f in "$WORK"/plugin-pub/*.dll; do
        b="$(basename "$f")"
        [[ -f "$(dirname "$JF_DLL")/$b" ]] || cp "$f" "$pd/"
    done
    [[ -f "$pd/Jellyfin.Plugin.PhantomLibrary.dll" && -f "$pd/Npgsql.dll" ]] || fatal "plugin payload incomplete"
    cat >"$pd/meta.json" <<META
{"category":"Metadata","changelog":"p05 rig","description":"p05 rig","guid":"9e7a1f4c-2b5d-4e8f-9a3b-7c1d2e5f6a8b","name":"Phantom Library","overview":"p05 rig","owner":"spencerharmon","targetAbi":"10.11.0.0","timestamp":"0001-01-01T00:00:00.0000000Z","version":"$PLUGIN_VERSION","status":"Active","autoUpdate":false,"assemblies":[]}
META
    cat >"$root/config/network.xml" <<NET
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration><InternalHttpPort>$port</InternalHttpPort><PublicHttpPort>$port</PublicHttpPort><AutoDiscovery>false</AutoDiscovery><EnableIPv6>false</EnableIPv6></NetworkConfiguration>
NET
}
setup_instance "$WORK" "$JF_PORT"
setup_instance "$WORK_B" "$JF_PORT_B"
write_plugin_config() {  # $1 = swarm-health service url ('' = worker inert)  [$2 = root, $3 = sync batch]
    local root="${2:-$WORK}" batch="${3:-$SYNC_BATCH}"
    cat >"$root/data/plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml" <<EOF
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
  <SwarmHealthSyncBatchSize>$batch</SwarmHealthSyncBatchSize>
  <SwarmHealthRecheckHours>$RECHECK_H</SwarmHealthRecheckHours>
  <SwarmHealthRankWindowDays>7</SwarmHealthRankWindowDays>
</PluginConfiguration>
EOF
}
start_jf() {  # $1 = stdout log name (instance = $JROOT / $B)
    (
        cd "$JROOT/data"
        exec env PHANTOM_POSTGRES_HOST=127.0.0.1 PHANTOM_POSTGRES_PORT="$PGPORT" PHANTOM_POSTGRES_DB=phantom \
            PHANTOM_POSTGRES_USER=phantom PHANTOM_POSTGRES_PASSWORD="$PGPASS" TMPDIR="$JROOT/cache" \
            dotnet "$JF_DLL" --datadir "$JROOT/data" --configdir "$JROOT/config" --cachedir "$JROOT/cache" \
            --logdir "$JROOT/log" --nowebclient --ffmpeg "$(command -v ffmpeg || echo /usr/bin/ffmpeg)"
    ) >"$WORK/$1" 2>&1 &
    JF_PID=$!
    JF_LOG="$WORK/$1"
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

# with_b CMD...: run CMD against library B (its root, URL, DB, process, token),
# then restore library A's globals. Always restores, and returns CMD's status.
with_b() {
    local a_root="$JROOT" a_url="$B" a_db="$JFDB" a_pid="$JF_PID" a_log="$JF_LOG" a_tok="${TOK:-}" a_uid="${USER_ID:-}" rc=0
    JROOT="$WORK_B"; B="$BB"; JFDB="$JFDB_B"; JF_PID="$B_PID"; JF_LOG="$B_LOG"; TOK="$B_TOK"; USER_ID="$B_USER_ID"
    "$@" || rc=$?
    B_PID="$JF_PID"; B_LOG="$JF_LOG"; B_TOK="${TOK:-}"; B_USER_ID="${USER_ID:-}"
    JROOT="$a_root"; B="$a_url"; JFDB="$a_db"; JF_PID="$a_pid"; JF_LOG="$a_log"; TOK="$a_tok"; USER_ID="$a_uid"
    return "$rc"
}
enable_metrics() {  # $1 = root; Jellyfin writes system.xml on first start (instance stopped)
    local sx="$1/config/system.xml"
    [[ -f "$sx" ]] || fatal "no $sx to enable metrics in"
    if grep -q '<EnableMetrics>' "$sx"; then
        sed -i 's:<EnableMetrics>[a-z]*</EnableMetrics>:<EnableMetrics>true</EnableMetrics>:' "$sx"
    else
        sed -i 's:</ServerConfiguration>:  <EnableMetrics>true</EnableMetrics>\n</ServerConfiguration>:' "$sx"
    fi
    grep -q '<EnableMetrics>true</EnableMetrics>' "$sx" || fatal "could not enable metrics in $sx"
}
server_id() {  # $1 = base url -> the server id, normalised like the worker (32 lower hex)
    curl -sf "$1/System/Info/Public" | python3 -c 'import json,sys,uuid;print(uuid.UUID(json.load(sys.stdin)["Id"]).hex)'
}

write_plugin_config ""
write_plugin_config "" "$WORK_B" "$SYNC_BATCH_B"
start_jf jf-1.log
H='Content-Type: application/json'
XA='X-Emby-Authorization: MediaBrowser Client="p05-rig", Device="p05-rig", DeviceId="p05-rig", Version="1"'
retry() {  # the wizard endpoints answer 503 until the host finished post-startup init
    for _ in $(seq 1 60); do "$@" >/dev/null 2>&1 && return 0; sleep 1; done
    fatal "gave up: $*"
}
wizard() {
    retry curl -sf -X POST -H "$H" "$B/Startup/Configuration" -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
    retry curl -sf "$B/Startup/User"   # GET materialises the wizard's initial user
    retry curl -sf -X POST -H "$H" "$B/Startup/User" -d '{"Name":"rig","Password":"rigpass"}'
    retry curl -sf -X POST "$B/Startup/Complete"
}
wizard
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
# api_get OUT URL: GET with retry. A channel refresh running right after the
# seed can answer a transient 5xx / time out; retry it, and only give up with a
# diagnostic (HTTP status, body head, Jellyfin log tail) instead of a bare
# `curl -f` exit 22 under set -e.
api_get() {
    local out="$1" url="$2" code="" attempt
    for attempt in $(seq 1 40); do
        code="$(curl -s --max-time 900 -o "$out" -w '%{http_code}' -H "X-Emby-Token: $TOK" "$url" || true)"
        if [[ "$code" == 200 ]] && python3 -c 'import json,sys;json.load(open(sys.argv[1]))' "$out" 2>/dev/null; then
            return 0
        fi
        echo "   api retry $attempt: HTTP ${code:-none} for $url" >&2
        sleep 3
    done
    echo "---- last response body (head) ----" >&2
    head -c 2000 "$out" >&2 || true
    echo >&2
    if [[ -n "$JF_LOG" ]]; then
        echo "---- $(basename "$JF_LOG") tail ----" >&2
        tail -60 "$JF_LOG" >&2 || true
    fi
    fatal "GET $url failed after 40 attempts (last HTTP status ${code:-none})"
}
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
q() { sqlite3 "$JFDB" "$@"; }
SCORED_WHERE="((Type='$T_MOVIE' AND TopParentId='$MOVIES_CH_U' AND ChannelId='$MOVIES_CH_U') OR (Type IN ('$T_SERIES','$T_FOLDER') AND TopParentId='$SHOWS_CH_U' AND ChannelId='$SHOWS_CH_U'))"
EXPECTED_SCORED=$((N_MOVIES + N_ORPHAN_MOVIES + N_SERIES + N_ORPHAN_SERIES))
# browse_catalogue: persist the whole phantom catalogue of the CURRENT instance
# ($B / $JFDB) as channel BaseItems through the real browse path.
# The browse persists channel items as BaseItems; when the first browse races
# the channel refresh (transient 500) the retried request can return before
# every item is persisted. Re-browse until the persisted catalogue converges
# (bounded), instead of counting a half-written channel.
browse_catalogue() {
    local n_channels=0 persisted=0 attempt s sid season
    for _ in $(seq 1 60); do  # channel entities are created on the first /Channels listing
        api_get "$WORK/channels.json" "$B/Channels?userId=$USER_ID"
        n_channels="$(python3 -c 'import json,sys;print(len(json.load(open(sys.argv[1]))["Items"]))' "$WORK/channels.json")"
        [[ "$n_channels" -ge 2 ]] && break
        sleep 1
    done
    [[ "$n_channels" -ge 2 ]] || fatal "Phantom channels never listed on $B (got $n_channels)"
    for attempt in $(seq 1 40); do
        api_get "$WORK/browse-movies.json" "$B/Channels/$MOVIES_CH/Items?userId=$USER_ID&Limit=1"
        api_get "$WORK/browse-shows.json" "$B/Channels/$SHOWS_CH/Items?userId=$USER_ID&Limit=1"
        persisted="$(q "SELECT count(*) FROM BaseItems WHERE $SCORED_WHERE")"
        [[ "$persisted" -ge "$EXPECTED_SCORED" ]] && break
        echo "   browse $attempt: $persisted of $EXPECTED_SCORED titles persisted on $B; waiting" >&2
        sleep 3
    done
    # Episode path parity (scenario 36 shape): series -> season -> episodes for 3 measured series.
    for s in 1 2 3; do
        sid="$(sqlite3 "$JFDB" "SELECT lower(replace(Id,'-','')) FROM BaseItems WHERE ExternalId='series_$((7100000+s))'")"
        [[ -n "$sid" ]] || fatal "series_$((7100000+s)) BaseItem was not persisted by the shows browse"
        api_get "$WORK/browse-series-$s.json" "$B/Channels/$SHOWS_CH/Items?userId=$USER_ID&folderId=$sid"
        season="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["Items"][0]["Id"])' "$WORK/browse-series-$s.json")"
        api_get "$WORK/browse-season-$s.json" "$B/Channels/$SHOWS_CH/Items?userId=$USER_ID&folderId=$season"
    done
}
browse_catalogue
N_SCORED="$(q "SELECT count(*) FROM BaseItems WHERE $SCORED_WHERE")"
N_EPISODES="$(q "SELECT count(*) FROM BaseItems WHERE Type='MediaBrowser.Controller.Entities.TV.Episode' AND ChannelId='$SHOWS_CH_U'")"
N_ORPHANS="$(q "SELECT count(*) FROM BaseItems WHERE $SCORED_WHERE AND (ExternalId LIKE 'orphan\\_%' ESCAPE '\\' OR ExternalId LIKE 'orphanseries\\_%' ESCAPE '\\')")"
echo "   persisted: $N_SCORED scored titles ($N_ORPHANS orphan/non-TMDB), $N_EPISODES episodes"
check "every seeded title persisted as a channel BaseItem ($N_SCORED == $EXPECTED_SCORED)" test "$N_SCORED" -eq "$EXPECTED_SCORED"
check "> 2000 Phantom titles seeded" test "$N_SCORED" -gt 2000
check "orphan/non-TMDB movies + series persisted ($N_ORPHANS == $((N_ORPHAN_MOVIES + N_ORPHAN_SERIES)))" test "$N_ORPHANS" -eq $((N_ORPHAN_MOVIES + N_ORPHAN_SERIES))
check "episode BaseItems exist (movie/episode parity path)" test "$N_EPISODES" -ge 6
SID_A="$(server_id "$B")"

# Library B: an independent Jellyfin server (own data dir => own server id and
# library DB) whose ONLY shared state with A is the Phantom PostgreSQL.
with_b start_jf jfB-1.log
with_b wizard
with_b login
with_b browse_catalogue
SID_B="$(server_id "$BB")"
N_SCORED_B="$(sqlite3 "$JFDB_B" "SELECT count(*) FROM BaseItems WHERE $SCORED_WHERE")"
N_EPISODES_B="$(sqlite3 "$JFDB_B" "SELECT count(*) FROM BaseItems WHERE Type='MediaBrowser.Controller.Entities.TV.Episode' AND ChannelId='$SHOWS_CH_U'")"
echo "   library A server id $SID_A; library B server id $SID_B: $N_SCORED_B scored titles, $N_EPISODES_B episodes"
check "library B persisted the full catalogue independently ($N_SCORED_B == $EXPECTED_SCORED)" test "$N_SCORED_B" -eq "$EXPECTED_SCORED"
check "the two libraries are distinct servers (different server ids)" test -n "$SID_A" -a -n "$SID_B" -a "$SID_A" != "$SID_B"
with_b stop_jf

# ---------------------------------------------------------------------------
head_ "4. non-Phantom controls + stale ratings (Jellyfin stopped)"
stop_jf
enable_metrics "$WORK"
enable_metrics "$WORK_B"
seed_controls() {  # $1 = jellyfin.db
sqlite3 "$1" <<SQL
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
}
seed_controls "$JFDB"
seed_controls "$JFDB_B"
CONTROL_SNAPSHOT_B="$(sqlite3 "$JFDB_B" "SELECT Id||'|'||ifnull(CriticRating,'NULL')||'|'||ifnull(ChannelId,'') FROM BaseItems WHERE Id LIKE 'C0000000-%' ORDER BY Id")"
EPISODE_SNAPSHOT_B="$(sqlite3 "$JFDB_B" "SELECT Id||'|'||ifnull(CriticRating,'NULL') FROM BaseItems WHERE ChannelId='$SHOWS_CH_U' AND Type IN ('MediaBrowser.Controller.Entities.TV.Episode','MediaBrowser.Controller.Entities.TV.Season') ORDER BY Id")"
NULL_BEFORE_B="$(sqlite3 "$JFDB_B" "SELECT count(*) FROM BaseItems WHERE $SCORED_WHERE AND CriticRating IS NULL")"
check "library B also starts with > 2000 NULL Phantom titles ($NULL_BEFORE_B)" test "$NULL_BEFORE_B" -gt 2000
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
head_ "5. one measurement cycle on TWO libraries sharing the Phantom DB, both interrupted (SIGTERM) and resumed"
KEY_A="swarm_health_sync_cursor:$SID_A"
KEY_B="swarm_health_sync_cursor:$SID_B"
# An unqualified (pre-review-3) global cursor claiming sweep 7 is mid-flight far
# into the catalogue: a worker that trusted it would skip almost every title.
LEGACY_CURSOR='{"sweep":7,"phase":2,"lastId":"ffffffff-ffff-ffff-ffff-ffffffffffff","startedAt":1,"done":false,"read":4242,"written":0}'
psqlc -c "INSERT INTO plugin_meta(key,value) VALUES('swarm_health_sync_cursor','$LEGACY_CURSOR') ON CONFLICT(key) DO UPDATE SET value=excluded.value" >/dev/null
python3 "$MOCK" --port "$MOCK_PORT" --log "$WORK/observations.jsonl" >"$WORK/mock.log" 2>&1 &
MOCK_PID=$!
for _ in $(seq 1 30); do curl -sf "http://127.0.0.1:$MOCK_PORT/healthz" >/dev/null && break; sleep 0.2; done
write_plugin_config "http://127.0.0.1:$MOCK_PORT"
write_plugin_config "http://127.0.0.1:$MOCK_PORT" "$WORK_B" "$SYNC_BATCH_B"
CYCLE_START=$(date +%s)
start_jf jf-2.log
with_b start_jf jfB-2.log
login
with_b login
# From here until section K only the two workers (plus logins and /metrics
# scrapes) touch the Phantom DB: no channel browse. Reset statement stats (E2).
psqlc -c 'SELECT pg_stat_statements_reset();' >/dev/null
steps_in() { grep -c 'Swarm-health score sync step: sweep=1 read=[1-9]' "$WORK/$1" || true; }
for _ in $(seq 1 120); do [[ "$(steps_in jf-2.log)" -ge 2 && "$(steps_in jfB-2.log)" -ge 2 ]] && break; sleep 1; done
[[ "$(steps_in jf-2.log)" -ge 2 ]] || { tail -60 "$WORK/jf-2.log"; fatal "no sync steps observed on library A"; }
[[ "$(steps_in jfB-2.log)" -ge 2 ]] || { tail -60 "$WORK/jfB-2.log"; fatal "no sync steps observed on library B"; }
stop_jf
CURSOR_MID="$(psqlc -c "SELECT value FROM plugin_meta WHERE key='$KEY_A'")"
echo "   library A cursor after SIGTERM: $CURSOR_MID"
check "persisted cursor exists and is NOT done after the interrupt" \
    python3 -c 'import json,sys;c=json.loads(sys.argv[1]);sys.exit(0 if c["sweep"]==1 and not c["done"] and c["read"]>0 else 1)' "$CURSOR_MID"
NULL_MID="$(q "SELECT count(*) FROM BaseItems WHERE $SCORED_WHERE AND CriticRating IS NULL")"
check "catalogue only partially scored at the interrupt ($NULL_MID NULL left of $N_SCORED)" test "$NULL_MID" -gt 0 -a "$NULL_MID" -lt "$NULL_BEFORE"
check "worker logged a clean stop" grep -q 'Swarm-health worker stopped' "$WORK/jf-2.log"
# B keeps stepping alone for a couple of ticks while A is down, then is interrupted too.
sleep $((INTERVAL * 2))
with_b stop_jf
CURSOR_MID_B="$(psqlc -c "SELECT value FROM plugin_meta WHERE key='$KEY_B'")"
echo "   library B cursor after SIGTERM: $CURSOR_MID_B"
check "library B's own cursor is mid-sweep 1 at its interrupt" \
    python3 -c 'import json,sys;c=json.loads(sys.argv[1]);sys.exit(0 if c["sweep"]==1 and not c["done"] and c["read"]>0 else 1)' "$CURSOR_MID_B"
NULL_MID_B="$(sqlite3 "$JFDB_B" "SELECT count(*) FROM BaseItems WHERE $SCORED_WHERE AND CriticRating IS NULL")"
check "library B only partially scored at its interrupt ($NULL_MID_B NULL)" test "$NULL_MID_B" -gt 0 -a "$NULL_MID_B" -lt "$NULL_BEFORE_B"

start_jf jf-3.log
with_b start_jf jfB-3.log
login
with_b login
# wait_both N [stop]: wait for sweep N to complete on BOTH libraries. The moment
# each completes, snapshot its cursor (CURSOR_<N>_{A,B}) and scrape its /metrics
# (the worker legitimately starts the next sweep later and resets the cursor);
# with "stop", SIGTERM that server right there, so its persisted cursor is a
# DONE sweep and the restart in section J restores outside any sweep.
wait_both() {
    local n="$1" stop="${2:-}" a_done="" b_done="" deadline=$(( $(date +%s) + RECHECK_H * 3600 ))
    while [[ -z "$a_done" || -z "$b_done" ]]; do
        if [[ -z "$a_done" ]]; then
            kill -0 "$JF_PID" 2>/dev/null || fatal "library A jellyfin died during the cycle"
            if grep -q "Swarm-health score sweep $n completed" "$JF_LOG"; then
                printf -v "CURSOR_${n}_A" '%s' "$(psqlc -c "SELECT value FROM plugin_meta WHERE key='$KEY_A'")"
                curl -sf "$B/metrics" >"$WORK/metrics-A-sweep$n.txt" || fatal "library A /metrics scrape failed"
                [[ -z "$stop" ]] || stop_jf
                a_done=1
            fi
        fi
        if [[ -z "$b_done" ]]; then
            kill -0 "$B_PID" 2>/dev/null || fatal "library B jellyfin died during the cycle"
            if grep -q "Swarm-health score sweep $n completed" "$B_LOG"; then
                printf -v "CURSOR_${n}_B" '%s' "$(psqlc -c "SELECT value FROM plugin_meta WHERE key='$KEY_B'")"
                curl -sf "$BB/metrics" >"$WORK/metrics-B-sweep$n.txt" || fatal "library B /metrics scrape failed"
                [[ -z "$stop" ]] || with_b stop_jf
                b_done=1
            fi
        fi
        [[ "$(date +%s)" -lt "$deadline" ]] || fatal "sweep $n did not complete on both libraries inside the ${RECHECK_H}h cycle"
        sleep 1
    done
}
wait_both 1
CYCLE_SECS=$(( $(date +%s) - CYCLE_START ))
# (A sweep-1 snapshot is informational: A resumes with few rows left and may
# already be in sweep 2 when polled. Deterministic cursor assertions use the
# sweep-2 snapshots, taken with the server stopped right at completion.)
echo "   library A cursor when sweep 1 was seen complete: $CURSOR_1_A"
echo "   library B cursor when sweep 1 was seen complete: $CURSOR_1_B"
# Sweep 2 starts MinTicksBetweenSweeps ticks after sweep 1 started, i.e. after
# the intent-bounded measurement drained: its title gauges are exact vs the
# observations. Stop each server right at its sweep-2 completion.
wait_both 2 stop
CURSOR_DONE="$CURSOR_2_A"
echo "   library A cursor at sweep-2 completion: $CURSOR_2_A"
echo "   library B cursor at sweep-2 completion: $CURSOR_2_B"
COMPLETED_LINE="$(grep -h 'Swarm-health score sweep 1 completed' "$WORK/jf-3.log" | tail -1)"
echo "   A: $COMPLETED_LINE"
check "restarted process resumed sweep 1 (no 'sweep 1 started' after restart)" bash -c "! grep -q 'Swarm-health score sweep 1 started' '$WORK/jf-3.log'"
SWEEP_READ="$(sed -n 's/.*completed: read=\([0-9]*\) .*/\1/p' <<<"$COMPLETED_LINE")"
SWEEP_TOOK="$(sed -n 's/.* took=\([0-9]*\)s.*/\1/p' <<<"$COMPLETED_LINE")"
check "sweep read every title exactly once across the restart ($SWEEP_READ == $N_SCORED)" test "$SWEEP_READ" -eq "$N_SCORED"
check "sweep finished inside one measurement cycle (${SWEEP_TOOK}s < $((RECHECK_H * 3600))s; wall ${CYCLE_SECS}s)" test "$SWEEP_TOOK" -lt $((RECHECK_H * 3600))
COMPLETED_LINE_B="$(grep -h 'Swarm-health score sweep 1 completed' "$WORK/jfB-3.log" | tail -1)"
echo "   B: $COMPLETED_LINE_B"
check "library B resumed ITS sweep 1 after restart (no 'sweep 1 started' after restart)" bash -c "! grep -q 'Swarm-health score sweep 1 started' '$WORK/jfB-3.log'"
SWEEP_READ_B="$(sed -n 's/.*completed: read=\([0-9]*\) .*/\1/p' <<<"$COMPLETED_LINE_B")"
check "library B's sweep read every one of ITS titles exactly once ($SWEEP_READ_B == $N_SCORED_B)" test "$SWEEP_READ_B" -eq "$N_SCORED_B"
check "every seeded candidate was measured by sweep 2" python3 -c '
import json,sys
asked={h for l in open(sys.argv[1]) for h in json.loads(l)["requested"]}
sys.exit(0 if len(asked)>=int(sys.argv[2]) else 1)' "$WORK/observations.jsonl" $((N_MOVIE_CAND + 2 * N_SERIES_CAND))

# ---------------------------------------------------------------------------
head_ "D. convergence vs the service's observations (library A; library B in section I)"
# converge DB PREFIX: classify every scored title of one library vs the logged
# observations; writes <prefix>expected.tsv (id, ext, score), <prefix>gauges.json
# (movie/series live/dead titles implied by the observations) and
# <prefix>convergence.txt (last line = summary).
converge() {
    local db="$1" pre="$2"
    sqlite3 -separator '|' "$db" "SELECT Id, Type, ExternalId, ifnull(CriticRating,'NULL') FROM BaseItems WHERE $SCORED_WHERE" >"$WORK/${pre}scored.txt"
    python3 - "$WORK/${pre}scored.txt" "$WORK/observations.jsonl" "$WORK/${pre}expected.tsv" "$N_MOVIE_CAND" "$N_SERIES_CAND" "$WORK/${pre}gauges.json" <<'PY' >"$WORK/${pre}convergence.txt"
import hashlib, json, sys
scored, obs, out, nmc, nsc, gout = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4]), int(sys.argv[5]), sys.argv[6]
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
gauges = {"movie/live": 0, "movie/dead": 0, "series/live": 0, "series/dead": 0}
with open(out, "w") as fo:
    for line in open(scored):
        iid, typ, ext, rating = line.rstrip("\n").split("|")
        kind, peers = None, None
        if ext.startswith("movie_"):
            hh = movie_hash.get(int(ext[6:]))
            peers = answers.get(hh) if hh else None
            exp, kind = score(peers), "movie"
        elif ext.startswith("series_"):
            obs_e = [answers[x] for x in series_hashes.get(int(ext[7:]), []) if x in answers]
            peers = max(obs_e) if obs_e else None
            exp, kind = score(peers), "series"
        elif ext.startswith("orphan_") or ext.startswith("orphanseries_"):
            exp = 0.5
        else:
            print(f"unexpected scored row {ext}"); bad += 1; continue
        if kind and peers is not None:
            gauges[f"{kind}/{'live' if peers > 0 else 'dead'}"] += 1
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
json.dump(gauges, open(gout, "w"))
print(f"nulls={nulls} mismatches={bad} live={live} unmeasured={unmeasured} dead={dead}")
PY
}
converge "$JFDB" ""
converge "$JFDB_B" "B-"
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
MAX_READ_B="$(cat "$WORK/jfB-2.log" "$WORK/jfB-3.log" | sed -n 's/.*Swarm-health score sync step: sweep=[0-9]* read=\([0-9]*\) .*/\1/p' | sort -n | tail -1)"
check "library B: no sync step read more than its batch ($MAX_READ_B <= $SYNC_BATCH_B)" test "$MAX_READ_B" -le "$SYNC_BATCH_B"
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
head_ "J. phantom_swarm_health_titles gauges: at a completed sweep, and after restart"
# gauge_cmp METRICS_FILE EXPECTED_JSON LABEL: the 4 emitted title samples == expected (all > 0)
gauge_cmp() {
    python3 - "$1" "$2" "$3" <<'PY'
import json, re, sys
text, exp, label = open(sys.argv[1]).read(), json.load(open(sys.argv[2])), sys.argv[3]
got = {}
for m in re.finditer(r'^phantom_swarm_health_titles\{type="(\w+)",health="(\w+)"\} ([0-9.e+]+)$', text, re.M):
    got[f"{m.group(1)}/{m.group(2)}"] = int(float(m.group(3)))
print(f"   [{label}] emitted {dict(sorted(got.items()))} expected {dict(sorted(exp.items()))}")
sys.exit(0 if got == exp and all(v > 0 for v in exp.values()) else 1)
PY
}
check "library A: gauges scraped at sweep-2 completion == observation-implied movie/series live/dead" gauge_cmp "$WORK/metrics-A-sweep2.txt" "$WORK/gauges.json" "A sweep 2"
check "library B: gauges scraped at sweep-2 completion == observation-implied movie/series live/dead" gauge_cmp "$WORK/metrics-B-sweep2.txt" "$WORK/B-gauges.json" "B sweep 2"
check "library A: persisted published title counts == observation-implied values (cursor at sweep 2)" python3 -c '
import json,sys
c=json.loads(sys.argv[1])["published"]; e=json.load(open(sys.argv[2]))
sys.exit(0 if {"movie/live":c["movieLive"],"movie/dead":c["movieDead"],"series/live":c["seriesLive"],"series/dead":c["seriesDead"]}==e else 1)' "$CURSOR_2_A" "$WORK/gauges.json"
# Restart: each fresh process must republish its library's last completed sweep
# from the persisted cursor on its first tick, BEFORE any new sweep completes.
restored_scrape() {  # $1 log  $2 base url  $3 out
    for _ in $(seq 1 $((INTERVAL * 6))); do
        grep -q 'Swarm-health title gauges restored from sweep' "$WORK/$1" && break
        sleep 1
    done
    grep -q 'Swarm-health title gauges restored from sweep 2' "$WORK/$1" || { echo "   no 'restored from sweep 2' in $1" >&2; return 1; }
    curl -sf "$2/metrics" >"$WORK/$3" || { echo "   /metrics scrape failed after restart ($1)" >&2; return 1; }
    if grep -q 'Swarm-health score sweep [0-9]* completed' "$WORK/$1"; then
        echo "   a sweep already completed in $1 before the scrape: the restore is not isolated" >&2
        return 1
    fi
    grep -h 'Swarm-health title gauges restored from sweep' "$WORK/$1" | tail -1 | sed 's/^.*\(Swarm-health title gauges\)/   \1/'
}
start_jf jf-4.log
with_b start_jf jfB-4.log
check "library A: restarted process emits restored gauges before any sweep completes" restored_scrape jf-4.log "$B" metrics-A-restart.txt
check "library B: restarted process emits restored gauges before any sweep completes" restored_scrape jfB-4.log "$BB" metrics-B-restart.txt
check "library A: restored gauges == observation-implied values" gauge_cmp "$WORK/metrics-A-restart.txt" "$WORK/gauges.json" "A restart"
check "library B: restored gauges == observation-implied values" gauge_cmp "$WORK/metrics-B-restart.txt" "$WORK/B-gauges.json" "B restart"
with_b stop_jf

# ---------------------------------------------------------------------------
head_ "E2. no per-tick full swarm_health scan (server-side statement statistics, whole cycle)"
# Every statement both workers ran against swarm_health since the cycle began
# (sections 5, C, D-cycle, J: 2 libraries, >= 2 sweeps each, restarts). The
# legacy cache refresh ran `... FROM swarm_health WHERE type = $1 AND
# checked_at >= $2 GROUP BY tmdb_id` (no tmdb_id restriction) on EVERY tick.
# stmt_counts FILE -> "<whole-table aggregate calls> <bounded tmdb_id IN calls>"
stmt_counts() {
    psqlc -F '|' -c "SELECT calls, regexp_replace(query, '\s+', ' ', 'g') FROM pg_stat_statements WHERE query ILIKE '%swarm_health%' AND query NOT ILIKE '%pg_stat_statements%' ORDER BY calls DESC" >"$1"
    python3 - "$1" <<'PY'
import re, sys
full = bounded = 0
for line in open(sys.argv[1]):
    if "|" not in line:
        continue
    calls, q = line.split("|", 1)
    ql = q.lower()
    if not re.search(r"from swarm_health\b", ql) or "max(dht_peers)" not in ql or ql.lstrip().startswith("explain"):
        continue  # (the harness's own EXPLAINs are not worker statements)
    if re.search(r"tmdb_id in \(", ql):
        bounded += int(calls)
    elif "group by tmdb_id" in ql:
        full += int(calls)
print(full, bounded)
PY
}
read -r FULL_AGG BOUNDED <<<"$(stmt_counts "$WORK/stmt-swarm.txt")"
sed 's/^/   /' "$WORK/stmt-swarm.txt" | cut -c1-200
WORKER_TICKS="$(cat "$WORK"/jf-2.log "$WORK"/jf-3.log "$WORK"/jf-4.log "$WORK"/jfB-2.log "$WORK"/jfB-3.log "$WORK"/jfB-4.log | grep -c 'Swarm-health score sync step:' || true)"
echo "   non-idle sync steps in window: $WORKER_TICKS; whole-table aggregates: $FULL_AGG; bounded tmdb_id IN lookups: $BOUNDED"
check "statement statistics captured the cycle (bounded per-batch lookups ran: $BOUNDED > 0; $WORKER_TICKS steps)" test "$BOUNDED" -gt 0 -a "$WORKER_TICKS" -gt 10
check "ZERO whole-table swarm_health aggregates across the cycle (no per-tick cache refresh; got $FULL_AGG)" test "$FULL_AGG" -eq 0

# ---------------------------------------------------------------------------
head_ "K. forced replace-all metadata refresh keeps every sampled score (metadata provider)"
# Library A is up (restored, idle between sweeps). Stop it, NULL a sample of
# scored titles directly in its library DB (what core's replace-all merge does
# to CriticRating), and hold the sweep idle (cursor done, startedAt in the
# future => inside the MinTicksBetweenSweeps gap) so ONLY the provider can
# restore them. Then force a FullRefresh with replaceAllMetadata via REST.
stop_jf
python3 - "$WORK/expected.tsv" >"$WORK/k-sample.tsv" <<'PY'
import sys
rows = [l.rstrip("\n").split("\t") for l in open(sys.argv[1])]
want = []
def take(pred, n):
    got = [r for r in rows if pred(r) and r not in want][:n]
    want.extend(got)
    return len(got)
counts = [
    take(lambda r: r[1].startswith("movie_") and float(r[2]) > 0.5, 6),
    take(lambda r: r[1].startswith("movie_") and float(r[2]) == 0.0, 4),
    take(lambda r: r[1].startswith("movie_") and float(r[2]) == 0.5, 4),
    take(lambda r: r[1].startswith("orphan_"), 3),
    take(lambda r: r[1].startswith("series_") and float(r[2]) > 0.5, 4),
    take(lambda r: r[1].startswith("series_") and float(r[2]) == 0.0, 2),
    take(lambda r: r[1].startswith("series_") and float(r[2]) == 0.5, 2),
    take(lambda r: r[1].startswith("orphanseries_"), 3),
]
assert all(c > 0 for c in counts), counts
for r in want:
    print("\t".join(r))
PY
N_K="$(wc -l <"$WORK/k-sample.tsv")"
K_IDS="$(cut -f1 "$WORK/k-sample.tsv" | sed "s/.*/'&'/" | paste -sd,)"
k_nulls() { q "SELECT count(*) FROM BaseItems WHERE Id IN ($K_IDS) AND CriticRating IS NULL"; }
q "UPDATE BaseItems SET CriticRating=NULL WHERE Id IN ($K_IDS)"
check "sample of $N_K scored titles NULLed (live/dead/unmeasured movies + series, orphans, orphan series folders)" test "$(k_nulls)" -eq "$N_K"
HOLD="$(python3 -c 'import json,sys,time;c=json.loads(sys.argv[1]);c["done"]=True;c["startedAt"]=int(time.time())+3600;print(json.dumps(c,separators=(",",":")))' "$(psqlc -c "SELECT value FROM plugin_meta WHERE key='$KEY_A'")")"
psqlc -c "UPDATE plugin_meta SET value='$HOLD' WHERE key='$KEY_A'" >/dev/null
start_jf jf-5.log
login
provider_restored() {  # $1 metrics file -> sum of phantom_swarm_health_provider_applied_total{result="restored"}
    python3 - "$1" <<'PY'
import re, sys
t = open(sys.argv[1]).read()
print(int(sum(float(m.group(1)) for m in re.finditer(r'^phantom_swarm_health_provider_applied_total\{[^}]*result="restored"[^}]*\} ([0-9.e+]+)$', t, re.M))))
PY
}
sleep $((INTERVAL + 2))   # at least one worker tick: the held sweep must stay idle and leave the NULLs
check "held sweep left the sample NULL before the refresh (no other writer)" test "$(k_nulls)" -eq "$N_K"
curl -sf "$B/metrics" >"$WORK/metrics-K-before.txt" || fatal "/metrics scrape failed"
K_BEFORE="$(provider_restored "$WORK/metrics-K-before.txt")"
while IFS=$'\t' read -r kid _ _; do
    code="$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "X-Emby-Token: $TOK" \
        "$B/Items/$kid/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=None&replaceAllMetadata=true&replaceAllImages=false")"
    [[ "$code" == 204 ]] || fatal "refresh of $kid answered HTTP $code"
done <"$WORK/k-sample.tsv"
for _ in $(seq 1 120); do
    [[ "$(k_nulls)" -eq 0 ]] && break
    sleep 1
done
sleep 2
curl -sf "$B/metrics" >"$WORK/metrics-K-after.txt" || fatal "/metrics scrape failed"
K_AFTER="$(provider_restored "$WORK/metrics-K-after.txt")"
grep '^phantom_swarm_health_provider_applied_total' "$WORK/metrics-K-after.txt" | sed 's/^/   /' || true
check "no NULL left in the sample after the forced metadata refresh" test "$(k_nulls)" -eq 0
check "every refreshed sample equals the sweep's observation policy (live > unmeasured 0.5 > dead 0; orphans 0.5)" python3 - "$WORK/k-sample.tsv" "$JFDB" <<'PY'
import sqlite3, sys
db = sqlite3.connect(sys.argv[2])
bad = 0
for line in open(sys.argv[1]):
    iid, ext, exp = line.rstrip("\n").split("\t")
    row = db.execute("SELECT CriticRating FROM BaseItems WHERE Id=?", (iid,)).fetchone()
    if row is None or row[0] is None or abs(float(row[0]) - float(exp)) > 0.01:
        bad += 1
        print(f"   K MISMATCH {ext}: {row} != {exp}")
sys.exit(1 if bad else 0)
PY
check "the metadata provider (not the sweep) restored them: restored counter +$((K_AFTER - K_BEFORE)) >= $N_K" test $((K_AFTER - K_BEFORE)) -ge "$N_K"
check "the sweep stayed idle during the refresh (no sync step read/started in jf-5)" \
    bash -c "! grep -Eq 'Swarm-health score (sync step: sweep=[0-9]+ read=[1-9]|sweep [0-9]+ started)' '$WORK/jf-5.log'"
check "provider used bounded single-title lookups after the restart (source=\"lookup\" restored)" \
    grep -Eq '^phantom_swarm_health_provider_applied_total\{source="lookup",result="restored"\} [1-9]' "$WORK/metrics-K-after.txt"
read -r FULL_AGG_K BOUNDED_K <<<"$(stmt_counts "$WORK/stmt-swarm-K.txt")"
check "the provider refresh added NO whole-table swarm_health aggregate (still $FULL_AGG_K == 0; bounded lookups $BOUNDED -> $BOUNDED_K)" \
    test "$FULL_AGG_K" -eq 0 -a "$BOUNDED_K" -gt "$BOUNDED"

# ---------------------------------------------------------------------------
head_ "G. REST default + Peers DESC first pages vs service observations (movies AND shows)"
login
rest_check() {  # $1 channel id  $2 label
    local ch="$1" label="$2"
    api_get "$WORK/rest-default-$label.json" "$B/Items?ParentId=$ch&userId=$USER_ID&Limit=50&StartIndex=0"
    api_get "$WORK/rest-desc1-$label.json" "$B/Items?ParentId=$ch&userId=$USER_ID&SortBy=CriticRating,SortName&SortOrder=Descending,Ascending&Limit=50&StartIndex=0"
    api_get "$WORK/rest-desc2-$label.json" "$B/Items?ParentId=$ch&userId=$USER_ID&SortBy=CriticRating,SortName&SortOrder=Descending,Ascending&Limit=50&StartIndex=50"
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
# Assert on the snapshot taken at sweep-1 completion (a later sweep may be in
# flight by now and has reset the live cursor — that is correct behaviour).
check "sweep cursor in the existing plugin_meta table recorded a completed sweep (sweep>=1, done, completedAt, read==$N_SCORED)" \
    python3 -c 'import json,sys;c=json.loads(sys.argv[1]);n=int(sys.argv[2]);sys.exit(0 if c["sweep"]>=1 and c["done"] and c.get("completedAt") and c["read"]==n else 1)' "$CURSOR_DONE" "$N_SCORED"
check "live cursor still lives in plugin_meta with sweep >= 1 (standing loop)" \
    python3 -c 'import json,sys;c=json.loads(sys.argv[1]);sys.exit(0 if c["sweep"]>=1 else 1)' "$(psqlc -c "SELECT value FROM plugin_meta WHERE key='$KEY_A'")"

# ---------------------------------------------------------------------------
head_ "I. two independent libraries sharing one Phantom DB"
CURSOR_KEYS="$(psqlc -c "SELECT string_agg(key, ',' ORDER BY key) FROM plugin_meta WHERE key LIKE 'swarm_health_sync_cursor%'")"
echo "   cursor keys: $CURSOR_KEYS"
check "exactly one scoped cursor per library + the untouched legacy key" \
    test "$CURSOR_KEYS" == "$(printf '%s\n' swarm_health_sync_cursor "$KEY_A" "$KEY_B" | LC_ALL=C sort | paste -sd,)"
check "legacy unqualified cursor neither trusted nor rewritten" \
    test "$(psqlc -c "SELECT value FROM plugin_meta WHERE key='swarm_health_sync_cursor'")" == "$LEGACY_CURSOR"
# Each library numbers its OWN sweeps from 1 (a trusted legacy cursor would
# have continued at 8): at its sweep-2 completion each cursor has published
# sweep 2 (a later sweep 3 may already be in flight; it carries the published
# counts forward), and each server logged sweep 2 reading all of ITS rows.
check "each library's own cursor numbered its sweeps from 1 (legacy sweep 7 ignored): published sweep 2" python3 -c '
import json,sys
a,b=json.loads(sys.argv[1]),json.loads(sys.argv[2])
sys.exit(0 if all(c["publishedSweep"]==2 and c["sweep"] in (2,3) for c in (a,b)) and a["sweep"]==2 and a["done"] else 1)' "$CURSOR_2_A" "$CURSOR_2_B"
SWEEP2_READ_A="$(grep -h 'Swarm-health score sweep 2 completed' "$WORK/jf-3.log" | sed -n 's/.*completed: read=\([0-9]*\) .*/\1/p' | tail -1)"
SWEEP2_READ_B="$(grep -h 'Swarm-health score sweep 2 completed' "$WORK/jfB-3.log" | sed -n 's/.*completed: read=\([0-9]*\) .*/\1/p' | tail -1)"
check "each library's sweep 2 read ALL of its own rows (A: $SWEEP2_READ_A, B: $SWEEP2_READ_B == $N_SCORED)" test "$SWEEP2_READ_A" -eq "$N_SCORED" -a "$SWEEP2_READ_B" -eq "$N_SCORED"
check "each library logged ITS sweep 1 completion with read==N (A: $SWEEP_READ, B: $SWEEP_READ_B)" test "$SWEEP_READ" -eq "$N_SCORED" -a "$SWEEP_READ_B" -eq "$N_SCORED"
summary_b="$(tail -1 "$WORK/B-convergence.txt")"
echo "   library B: $summary_b"
grep MISMATCH "$WORK/B-convergence.txt" | head -5 || true
check "library B: zero NULL scores after the cycle" grep -q '^nulls=0 ' <<<"$summary_b"
check "library B: every score equals the observation policy" grep -q ' mismatches=0 ' <<<"$summary_b"
check "library B: non-Phantom controls unchanged" test "$(sqlite3 "$JFDB_B" "SELECT Id||'|'||ifnull(CriticRating,'NULL')||'|'||ifnull(ChannelId,'') FROM BaseItems WHERE Id LIKE 'C0000000-%' ORDER BY Id")" == "$CONTROL_SNAPSHOT_B"
check "library B: episodes/seasons untouched" test "$(sqlite3 "$JFDB_B" "SELECT Id||'|'||ifnull(CriticRating,'NULL') FROM BaseItems WHERE ChannelId='$SHOWS_CH_U' AND Type IN ('MediaBrowser.Controller.Entities.TV.Episode','MediaBrowser.Controller.Entities.TV.Season') ORDER BY Id")" == "$EPISODE_SNAPSHOT_B"

# ---------------------------------------------------------------------------
printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[[ "$fail_count" -eq 0 ]] || exit 1
echo "p05-baseitem-writeback: PASS ($N_SCORED Phantom titles converged in one ${RECHECK_H}h cycle on each of 2 libraries sharing one Phantom DB; sweep ${SWEEP_TOOK}s)"
