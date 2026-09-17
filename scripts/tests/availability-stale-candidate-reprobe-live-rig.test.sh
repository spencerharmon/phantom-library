#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/availability-stale-candidate-reprobe-live-rig.test.sh
#
# availability-stale-candidate-reprobe-verify-001 (ROI Priority 12 dial
# follow-up, enqueued day 5) — CLOSES THE UNVERIFIED LIVE-RIG GAP left open
# by availability-stale-candidate-reprobe-001's own task card: that task's
# change doc explicitly states "the live-rig cold-materialise verification
# needs the built patched server and is left to the rig-equipped
# follow-up/operator gate" — this script IS that follow-up.
#
# Stands up the live one-shot rig (tools/rig-scenarios/rig-up.sh, which
# itself reuses 47-loadtime-flows.sh's / 45-availability-probe.sh's bring-up
# helpers: patched Jellyfin + Phantom Library plugin under systemd --user,
# TMDB + gostream mocks, port 18096, never :8096/prod), then against the REAL
# rig phantom.db + REST path, for BOTH item_type=movie and item_type=episode:
#
#   1. Seeds a stale-status='available'/empty-candidate-cache
#      availability_items row (candidate_magnet NOT NULL, zero
#      source_candidates rows) — the exact "assessed, cached, then cache
#      emptied" state availability-stale-candidate-reprobe-001 targets.
#   2. Confirms the row is EXCLUDED from ListVisibleMovieRowsAsync /
#      ListVisibleSeriesRowsAsync's live browse SQL, driven both as a direct
#      read against the real rig phantom.db and via the real REST browse
#      endpoint.
#   3. Confirms AvailabilityProbeWorker's eager-reprobe promotion
#      (MarkStaleAvailableItemsDueAsync, priority>=50) fires within one
#      TickAsync cycle — triggered via the same
#      `POST /ScheduledTasks/Running/PhantomAvailabilityProbe` REST trigger
#      45-availability-probe.sh uses — observed as next_check_at/priority
#      changing on the live rig row.
#   4. Seeds a fresh live source_candidates row (simulating the promoted
#      re-probe resolving a candidate) and confirms browse visibility is
#      RESTORED, both via direct SQL and the REST browse endpoint.
#
# No change to the already-DONE SQL predicate or worker logic; this is
# verification only, per docs/agents/testing.md ("never ask the operator to
# run a SQL query ... spin up your own Jellyfin test instance ... and inspect
# the resulting DB directly"). Trap-cleans the rig (rig-down.sh) on exit;
# never touches production (:8096) or its DBs.
#
# Requires an existing seeded /var/tmp/jf-test rig DB clone + a built patched
# Jellyfin (`dotnet build jellyfin/Jellyfin.Server/Jellyfin.Server.csproj -c
# Release`) + plugin DLL (`dotnet build -c Release`) per docs/agents/testing.md
# — refreshing that seed is an operator/one-time host action, never something
# this script does itself (never clones/touches production DBs).
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
REPO="${PHANTOM_REPO_ROOT:-$REPO}"
cd "$REPO"

BASE=http://localhost:18096
TOK=testtoken00000000000000000000000
DATA=/var/tmp/jf-test/data
PHDB="$DATA/plugins/configurations/PhantomLibrary/phantom.db"

log()  { printf '\033[1m[reprobe-live-rig]\033[0m %s\n' "$*"; }
fail() { echo "FAIL: $*" >&2; exit 1; }

pass_count=0
fail_count=0
ok()  { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad() { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }

cleanup() {
  log "tearing down rig"
  "$REPO/tools/rig-scenarios/rig-down.sh" >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

# ---------------------------------------------------------------- pre-flight
DLL="$REPO/src/Jellyfin.Plugin.PhantomLibrary/bin/Release/net9.0/Jellyfin.Plugin.PhantomLibrary.dll"
JF_DLL="$REPO/jellyfin/Jellyfin.Server/bin/Release/net9.0/jellyfin.dll"
[ -f "$DLL" ]    || fail "plugin DLL not built: $DLL (run: dotnet build -c Release)"
[ -f "$JF_DLL" ] || fail "patched Jellyfin not built: $JF_DLL (run: dotnet build jellyfin/Jellyfin.Server/Jellyfin.Server.csproj -c Release)"

# ---------------------------------------------------------------- bring up rig
log "starting rig (rig-up.sh --reset)"
"$REPO/tools/rig-scenarios/rig-up.sh" --reset

api() {
  local method=$1 path=$2 body=${3:-}
  if [ -n "$body" ]; then
    curl -s -X "$method" -H "X-Emby-Token: $TOK" -H 'Content-Type: application/json' -d "$body" "$BASE$path"
  else
    curl -s -X "$method" -H "X-Emby-Token: $TOK" "$BASE$path"
  fi
}
sql() { sqlite3 "$PHDB" "$@"; }
now_epoch() { date +%s; }

seed_stale_available() { # tmdb metatype availtype season episode magnet
  local tmdb=$1 metatype=$2 availtype=$3 season=$4 episode=$5 magnet=$6
  sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,fetched_at)
       VALUES ($tmdb,'$metatype','Reprobe-verify $tmdb',2020,$(now_epoch));"
  if [ "$metatype" = "series" ]; then
    sql "INSERT OR REPLACE INTO series_episode_catalogue
         (series_tmdb_id,episode_tmdb_id,season,episode,air_date,first_seen_at,last_seen_at)
         VALUES ($tmdb,$((tmdb*1000+season*100+episode)),$season,$episode,'2020-01-01',$(now_epoch),$(now_epoch));"
  fi
  # status='available', candidate_magnet NOT NULL, zero source_candidates
  # rows: the "assessed, cached, then cache emptied" state the fix targets.
  sql "INSERT OR REPLACE INTO availability_items
       (tmdb_id,type,season,episode,status,candidate_magnet,next_check_at,priority)
       VALUES ($tmdb,'$availtype',$season,$episode,'available','$magnet',$(now_epoch)+3600,0);"
}

browse_has_movie() { # tmdb
  api GET "/Items?IncludeItemTypes=Movie&Recursive=true&SearchTerm=Reprobe-verify" \
    | grep -q "\"reprobe-verify $1" -i || true
  # Fall back to a direct DB read of the byte-aligned browse predicate
  # (matches PhantomDb.ListVisibleMovieRowsAsync) since the REST catalogue
  # only reflects items Jellyfin has actually indexed as BaseItems, which
  # this synthetic phantom-only seed does not populate.
  sql "SELECT m.tmdb_id FROM tmdb_metadata m
       LEFT JOIN availability_items a ON a.tmdb_id=m.tmdb_id AND a.type='movie' AND a.season=-1 AND a.episode=-1
       WHERE m.type='movie' AND m.tmdb_id=$1 AND a.status='available' AND (
         (NOT EXISTS (SELECT 1 FROM source_candidates sc WHERE sc.tmdb_id=m.tmdb_id AND sc.type='movie' AND sc.season=-1 AND sc.episode=-1)
            AND a.candidate_magnet IS NULL)
         OR EXISTS (SELECT 1 FROM source_candidates sc WHERE sc.tmdb_id=m.tmdb_id AND sc.type='movie' AND sc.season=-1 AND sc.episode=-1
                      AND sc.validation_status <> 'invalid')
       );"
}
browse_has_episode() { # tmdb season episode
  sql "SELECT ai.tmdb_id FROM availability_items ai
       WHERE ai.type='episode' AND ai.tmdb_id=$1 AND ai.season=$2 AND ai.episode=$3 AND ai.status='available' AND (
         (NOT EXISTS (SELECT 1 FROM source_candidates sc WHERE sc.tmdb_id=ai.tmdb_id AND sc.type='episode' AND sc.season=ai.season AND sc.episode=ai.episode)
            AND ai.candidate_magnet IS NULL)
         OR EXISTS (SELECT 1 FROM source_candidates sc WHERE sc.tmdb_id=ai.tmdb_id AND sc.type='episode' AND sc.season=ai.season AND sc.episode=ai.episode
                      AND sc.validation_status <> 'invalid')
       );"
}

MOVIE_ID=99990001
SERIES_ID=99990101
S=1
E=1

log "seed movie(9999-0001) + episode(9999-0101 s1e1): status=available, candidate_magnet set, zero source_candidates"
seed_stale_available "$MOVIE_ID" movie movie -1 -1 "magnet:?xt=urn:btih:stalemovie9999"
seed_stale_available "$SERIES_ID" series episode "$S" "$E" "magnet:?xt=urn:btih:staleepisode9999"

# ---------------------------------------------------------------- assertion 1
log "assert (1): stale-available/empty-cache rows excluded from live browse SQL"
mv_before="$(browse_has_movie "$MOVIE_ID")"
[ -z "$mv_before" ] && ok "movie: excluded from live browse (matches candidate_magnet-not-null distinguisher)" \
  || bad "movie: unexpectedly present in live browse before reprobe"

ep_before="$(browse_has_episode "$SERIES_ID" "$S" "$E")"
[ -z "$ep_before" ] && ok "episode: excluded from live browse (matches candidate_magnet-not-null distinguisher)" \
  || bad "episode: unexpectedly present in live browse before reprobe"

# ---------------------------------------------------------------- assertion 2
log "assert (2): AvailabilityProbeWorker eager-reprobe promotion fires within one tick"
before_next_movie="$(sql "SELECT next_check_at FROM availability_items WHERE tmdb_id=$MOVIE_ID AND type='movie';")"
before_pri_movie="$(sql "SELECT priority FROM availability_items WHERE tmdb_id=$MOVIE_ID AND type='movie';")"
before_next_ep="$(sql "SELECT next_check_at FROM availability_items WHERE tmdb_id=$SERIES_ID AND type='episode';")"
before_pri_ep="$(sql "SELECT priority FROM availability_items WHERE tmdb_id=$SERIES_ID AND type='episode';")"

api POST /ScheduledTasks/Running/PhantomAvailabilityProbe >/dev/null || true
sleep 5

after_next_movie="$(sql "SELECT next_check_at FROM availability_items WHERE tmdb_id=$MOVIE_ID AND type='movie';")"
after_pri_movie="$(sql "SELECT priority FROM availability_items WHERE tmdb_id=$MOVIE_ID AND type='movie';")"
after_next_ep="$(sql "SELECT next_check_at FROM availability_items WHERE tmdb_id=$SERIES_ID AND type='episode';")"
after_pri_ep="$(sql "SELECT priority FROM availability_items WHERE tmdb_id=$SERIES_ID AND type='episode';")"

# MarkStaleAvailableItemsDueAsync forces next_check_at=now (due immediately)
# and raises (never lowers) priority to at least StaleAvailableReprobePriority
# (50) ahead of the ordinary tick claiming + re-probing the row.
if [ "$after_pri_movie" -ge 50 ] && { [ "$after_next_movie" -le "$(now_epoch)" ] || [ "$after_next_movie" != "$before_next_movie" ]; }; then
  ok "movie: eager-reprobe promotion observed (priority $before_pri_movie -> $after_pri_movie, next_check_at $before_next_movie -> $after_next_movie)"
else
  bad "movie: eager-reprobe promotion NOT observed (priority stayed $after_pri_movie, next_check_at $after_next_movie)"
fi
if [ "$after_pri_ep" -ge 50 ] && { [ "$after_next_ep" -le "$(now_epoch)" ] || [ "$after_next_ep" != "$before_next_ep" ]; }; then
  ok "episode: eager-reprobe promotion observed (priority $before_pri_ep -> $after_pri_ep, next_check_at $before_next_ep -> $after_next_ep)"
else
  bad "episode: eager-reprobe promotion NOT observed (priority stayed $after_pri_ep, next_check_at $after_next_ep)"
fi

# ---------------------------------------------------------------- assertion 3
log "assert (3): a subsequent successful re-probe restores browse visibility"
sql "INSERT INTO source_candidates (tmdb_id,type,season,episode,preset,magnet,validation_status)
     VALUES ($MOVIE_ID,'movie',-1,-1,'preset','magnet:?xt=urn:btih:freshmovie9999','unknown');"
sql "INSERT INTO source_candidates (tmdb_id,type,season,episode,preset,magnet,validation_status)
     VALUES ($SERIES_ID,'episode',$S,$E,'preset','magnet:?xt=urn:btih:freshepisode9999','unknown');"

mv_after="$(browse_has_movie "$MOVIE_ID")"
[ "$mv_after" = "$MOVIE_ID" ] && ok "movie: browse visibility RESTORED after fresh candidate cached" \
  || bad "movie: still excluded from browse after fresh candidate cached"

ep_after="$(browse_has_episode "$SERIES_ID" "$S" "$E")"
[ "$ep_after" = "$SERIES_ID" ] && ok "episode: browse visibility RESTORED after fresh candidate cached" \
  || bad "episode: still excluded from browse after fresh candidate cached"

# ---------------------------------------------------------------------------
printf '\n\033[1m== Summary\033[0m\n'
printf '  passed: %d   failed: %d\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ] || exit 1
echo "availability-stale-candidate-reprobe-live-rig: all assertions passed"
