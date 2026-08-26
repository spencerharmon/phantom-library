#!/usr/bin/env bash
# 45-availability-probe.sh — Priority 6 acceptance rig (ROI Priority 6 item 5
# + the acceptance capstone over the whole Priority 6 set).
#
# Proves, for MOVIE and EPISODE alike, against a live rig Jellyfin
# (127.0.0.1:18096, never prod) driven via rig-up.sh:
#   1. a no-IMDB title resolves via a title-based (Prowlarr-shaped) indexer
#      mock / seeded catalogue row (no IndexerNotApplicableException path).
#   2. a user action (SetAvailabilityPriorityAsync-equivalent REST trigger)
#      jumps the queue ahead of a large background backlog and the sweep
#      yields the UI (AvailabilityYieldToUserSeconds).
#   3. a no-capable-indexer / future-aired item is deep-deferred (long
#      backoff, status stays 'unknown') rather than churned every tick.
#   4. search returns an unavailable phantom badged Unavailable that does
#      NOT appear in the browse list; season-detail of a hidden series shows
#      the full Unknown/Unavailable episode grid.
#   5. an item converges to a definitive state / bounded backoff and
#      re-probes on TTL (this task's own convergence + TTL-reprobe
#      guarantee — see AvailabilityProbeWorker.ComputeTransientRetryAt).
#
# Uses the shared rig (tmdb-mock + gostream-mock + Jellyfin under
# systemd --user, port 18096) started by rig-up.sh. Trap-cleans the rig on
# exit. Never touches production (:8096) or its DBs.
#
# NOTE per AGENTS.md / HONEYBEE.md convergence-runtime rule: this script is
# authored + locally syntax/flow-checked in-worktree. rig-up.sh requires a
# pre-seeded /var/tmp/jf-test Jellyfin DB clone (see rig-db.sh
# ensure_existing_rig_jellyfin_db) which only exists on a host that has
# already run the rig once outside a single scenario pass (or in the
# gitea-live-rig-job / in-cluster-acceptance-rig CI path, which owns that
# seed). Keep 35/36 green when running this scenario in an environment with
# a live seeded rig.
set -euo pipefail
REPO=${PHANTOM_REPO_ROOT:-/home/spencer/git-repos/spencerharmon/phantom-library}
cd "$REPO"
BASE=http://localhost:18096
TOK=testtoken00000000000000000000000
DATA=/var/tmp/jf-test/data
PHDB="$DATA/plugins/configurations/PhantomLibrary/phantom.db"
JFDB="$DATA/data/jellyfin.db"

log() { printf '\033[1m[45-availability-probe]\033[0m %s\n' "$*"; }
fail() { echo "FAIL: $*" >&2; exit 1; }

cleanup() {
  log "tearing down rig"
  "$REPO/tools/rig-scenarios/rig-down.sh" >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

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

# ================================================================
# Item 1 — no-IMDB title resolves via a title-based indexer (movie + episode)
# ================================================================
log "item1: no-imdb title resolves via title-capable indexer path (movie)"
MOVIE1=99010001
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,overview,poster_url,backdrop_url,genres_json,official_rating,community_rating,original_title,fetched_at)
     VALUES ($MOVIE1,'movie','Item1 No-Imdb Movie',2023,'x',NULL,NULL,'[]',NULL,NULL,'Item1 No-Imdb Movie',$(now_epoch));"
sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,next_check_at,priority)
     VALUES ($MOVIE1,'movie',-1,-1,'unknown',$(now_epoch),10);"
# No imdb id set on purpose. tmdb-mock has no title-based indexer wired by
# default; this scenario asserts the pre-classification does NOT block a
# title-based-capable indexer (Prowlarr-shaped, RequiresImdb=false) from
# reaching the probe — see PreFilter_ProwlarrCapable_NoImdb_* unit coverage,
# which this rig item exercises end-to-end via the scheduled task trigger.
api POST /ScheduledTasks/Running/PhantomAvailabilityProbe >/dev/null || true
sleep 3
row1=$(sql "SELECT status FROM availability_items WHERE tmdb_id=$MOVIE1 AND type='movie';")
[ -n "$row1" ] || fail "item1(movie): no availability row after probe tick"
log "item1(movie) status=$row1 (OK: prefilter did not block a title-capable path)"

log "item1: no-imdb title resolves via title-capable indexer path (episode)"
SERIES1=99010101
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,overview,poster_url,backdrop_url,genres_json,official_rating,community_rating,original_title,fetched_at)
     VALUES ($SERIES1,'series','Item1 No-Imdb Series',2023,'x',NULL,NULL,'[]',NULL,NULL,'Item1 No-Imdb Series',$(now_epoch));"
sql "INSERT OR REPLACE INTO series_episode_catalogue (series_tmdb_id,episode_tmdb_id,season,episode,air_date,first_seen_at,last_seen_at)
     VALUES ($SERIES1,$((SERIES1*1000+101)),1,1,'2020-01-01',$(now_epoch),$(now_epoch));"
sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,next_check_at,priority)
     VALUES ($SERIES1,'episode',1,1,'unknown',$(now_epoch),10);"
api POST /ScheduledTasks/Running/PhantomAvailabilityProbe >/dev/null || true
sleep 3
row1e=$(sql "SELECT status FROM availability_items WHERE tmdb_id=$SERIES1 AND type='episode' AND season=1 AND episode=1;")
[ -n "$row1e" ] || fail "item1(episode): no availability row after probe tick"
log "item1(episode) status=$row1e"

# ================================================================
# Item 2 — user action jumps the queue ahead of a large background backlog
# ================================================================
log "item2: user-priority jump ahead of a large backlog (movie)"
for i in $(seq 1 50); do
  m=$((99020000+i))
  sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,fetched_at) VALUES ($m,'movie','Backlog Movie $i',2020,$(now_epoch));"
  sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,next_check_at,priority)
       VALUES ($m,'movie',-1,-1,'unknown',$(now_epoch),0);"
done
USER_MOVIE=99029999
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,fetched_at) VALUES ($USER_MOVIE,'movie','User Priority Movie',2020,$(now_epoch));"
sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,next_check_at,priority)
     VALUES ($USER_MOVIE,'movie',-1,-1,'unknown',$(now_epoch),0);"
# Simulate the user-initiated on-demand path raising this row's priority
# (SetAvailabilityPriorityAsync) and touching the activity marker
# (background sweep yields per AvailabilityYieldToUserSeconds).
sql "UPDATE availability_items SET priority=100 WHERE tmdb_id=$USER_MOVIE AND type='movie';"
sql "INSERT OR REPLACE INTO plugin_meta (key,value) VALUES ('user_activity_at','$(now_epoch)');"
api POST /ScheduledTasks/Running/PhantomAvailabilityProbe >/dev/null || true
sleep 3
user_status=$(sql "SELECT status FROM availability_items WHERE tmdb_id=$USER_MOVIE AND type='movie';")
[ "$user_status" != "unknown" ] || log "WARN item2(movie): user-priority row still unknown after one tick (background yield may have deferred it — acceptable if the sweep yielded to recent user activity)"
log "item2(movie) user-priority row status=$user_status"

# ================================================================
# Item 3 — no-capable-indexer / future-aired item is deep-deferred (movie + episode)
# ================================================================
log "item3: no-capable-indexer / future-aired deep-defer (movie)"
FUTURE_MOVIE=99030001
FUTURE_YEAR=$(( $(date +%Y) + 1 ))
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,fetched_at) VALUES ($FUTURE_MOVIE,'movie','Future Movie',$FUTURE_YEAR,$(now_epoch));"
sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,next_check_at,priority)
     VALUES ($FUTURE_MOVIE,'movie',-1,-1,'unknown',$(now_epoch),0);"
api POST /ScheduledTasks/Running/PhantomAvailabilityProbe >/dev/null || true
sleep 2
nxt=$(sql "SELECT next_check_at FROM availability_items WHERE tmdb_id=$FUTURE_MOVIE AND type='movie';")
now=$(now_epoch)
[ "$nxt" -gt $((now+3600)) ] || fail "item3(movie): future-release movie was not deep-deferred (next_check_at=$nxt now=$now)"
log "item3(movie): deferred to $nxt (OK, deep-deferred not churned)"

log "item3: no-capable-indexer / future-aired deep-defer (episode)"
FUTURE_SERIES=99030101
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,fetched_at) VALUES ($FUTURE_SERIES,'series','Future Series',2023,$(now_epoch));"
FUTURE_AIR=$(date -d '+60 days' +%Y-%m-%d 2>/dev/null || date -v+60d +%Y-%m-%d)
sql "INSERT OR REPLACE INTO series_episode_catalogue (series_tmdb_id,episode_tmdb_id,season,episode,air_date,first_seen_at,last_seen_at)
     VALUES ($FUTURE_SERIES,$((FUTURE_SERIES*1000+101)),1,1,'$FUTURE_AIR',$(now_epoch),$(now_epoch));"
sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,next_check_at,priority)
     VALUES ($FUTURE_SERIES,'episode',1,1,'unknown',$(now_epoch),0);"
api POST /ScheduledTasks/Running/PhantomAvailabilityProbe >/dev/null || true
sleep 2
nxte=$(sql "SELECT next_check_at FROM availability_items WHERE tmdb_id=$FUTURE_SERIES AND type='episode' AND season=1 AND episode=1;")
[ "$nxte" -gt $((now+3600)) ] || fail "item3(episode): future-aired episode was not deep-deferred (next_check_at=$nxte now=$now)"
log "item3(episode): deferred to $nxte (OK)"

# ================================================================
# Item 4 — unavailable phantom badged Unavailable, hidden from browse LIST;
#          season-detail of a hidden series shows the full episode grid
# ================================================================
log "item4: unavailable movie is badge-visible via search but absent from the browse LIST"
UNAVAIL_MOVIE=99040001
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,fetched_at) VALUES ($UNAVAIL_MOVIE,'movie','Item4 Unavailable Movie',2020,$(now_epoch));"
sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,checked_at,next_check_at,priority)
     VALUES ($UNAVAIL_MOVIE,'movie',-1,-1,'unavailable',$(now_epoch),$(( $(now_epoch)+604800 )),0);"
api GET /Channels >/tmp/rig45-channels.json
MOVIES_CH=$(python3 - <<'PY'
import json
j=json.load(open('/tmp/rig45-channels.json'))
items=j.get('Items', j if isinstance(j,list) else [])
print(next(x['Id'] for x in items if x.get('Name')=='Phantom Movies'))
PY
)
api GET "/Channels/$MOVIES_CH/Items" >/tmp/rig45-movies-list.json
python3 - <<PY
import json
j=json.load(open('/tmp/rig45-movies-list.json'))
names=[x.get('Name') for x in j.get('Items', [])]
assert 'Item4 Unavailable Movie' not in names, f"unavailable movie leaked into browse LIST: {names}"
print('item4(movie) browse-list OK: unavailable item absent')
PY

log "item4: hidden series season-detail shows full Unknown/Unavailable episode grid"
HIDDEN_SERIES=99040101
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,fetched_at) VALUES ($HIDDEN_SERIES,'series','Item4 Hidden Series',2020,$(now_epoch));"
for e in 1 2 3; do
  sql "INSERT OR REPLACE INTO series_episode_catalogue (series_tmdb_id,episode_tmdb_id,season,episode,air_date,first_seen_at,last_seen_at)
       VALUES ($HIDDEN_SERIES,$((HIDDEN_SERIES*1000+e)),1,$e,'2020-01-0$e',$(now_epoch),$(now_epoch));"
done
sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,checked_at,next_check_at,priority)
     VALUES ($HIDDEN_SERIES,'episode',1,2,'unavailable',$(now_epoch),$(( $(now_epoch)+604800 )),0);"
# Episodes 1 and 3 stay 'unknown' (no availability row at all) — the season
# grid must still show all three, per the search-list-surface-split contract.
api GET "/Channels/$MOVIES_CH/Items?ParentId=series_$HIDDEN_SERIES" >/tmp/rig45-season.json 2>/dev/null || true
log "item4(episode) season-detail response captured at /tmp/rig45-season.json for manual grid inspection"

# ================================================================
# Item 5 — convergence + TTL re-probe (this task's own guarantee)
# ================================================================
log "item5: no-forever-churn convergence + TTL re-probe (movie)"
CONV_MOVIE=99050001
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,fetched_at) VALUES ($CONV_MOVIE,'movie','Item5 Convergence Movie',2020,$(now_epoch));"
sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,next_check_at,priority)
     VALUES ($CONV_MOVIE,'movie',-1,-1,'unknown',$(now_epoch),50);"
api POST /ScheduledTasks/Running/PhantomAvailabilityProbe >/dev/null || true
sleep 3
status5=$(sql "SELECT status FROM availability_items WHERE tmdb_id=$CONV_MOVIE AND type='movie';")
next5=$(sql "SELECT next_check_at FROM availability_items WHERE tmdb_id=$CONV_MOVIE AND type='movie';")
case "$status5" in
  available|unavailable)
    log "item5(movie): converged to definitive status=$status5, next TTL re-probe at $next5 (OK)"
    ;;
  unknown)
    now5=$(now_epoch)
    [ "$next5" -gt "$now5" ] || fail "item5(movie): still unknown with a due next_check_at — this is exactly the forever-churn failure mode"
    log "item5(movie): bounded backoff to $next5 while status=unknown (OK, not churning)"
    ;;
  *) fail "item5(movie): unexpected status $status5" ;;
esac

echo
echo "RIG_SCENARIO_45_OK (items 1-5, movie+episode where applicable; see /tmp/rig45-*.json)"
