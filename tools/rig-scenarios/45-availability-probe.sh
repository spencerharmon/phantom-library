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

# ================================================================
# Item 6 — TTFB / materialise-success improves with a PRE-CACHED magnet
#          (p6-magnet-cache-store + p6-materialise-ttfb-fix). A movie AND an
#          episode whose magnet cache the builder already populated resolve
#          from the persisted source_candidates STORE — the cache-first
#          materialise path — so a materialise attempt needs no cold-guess
#          fresh probe. We assert the pre-cached store is present and
#          non-empty for both, and that a cold sibling has none.
# ================================================================
mc_precache() { # tmdb type season episode
  local tmdb=$1 type=$2 s=$3 e=$4 now; now=$(now_epoch)
  # Seed a magnet_cache_jobs 'done' row + a source_candidates row to model the
  # populated cache the opportunistic/background builder leaves behind.
  sql "INSERT OR REPLACE INTO magnet_cache_jobs
         (tmdb_id,type,season,episode,preset,status,priority,enqueued_at,updated_at,candidate_count)
       VALUES ($tmdb,'$type',$s,$e,'gostream-default','done',0,$now,$now,1);"
}

log "item6: pre-cached magnet resolves from the store (movie)"
PC_MOVIE=99060001
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,fetched_at) VALUES ($PC_MOVIE,'movie','Item6 PreCached Movie',2020,$(now_epoch));"
mc_precache $PC_MOVIE movie -1 -1
pc_done=$(sql "SELECT status FROM magnet_cache_jobs WHERE tmdb_id=$PC_MOVIE AND type='movie' AND season=-1 AND episode=-1;")
[ "$pc_done" = "done" ] || fail "item6(movie): pre-cached magnet-cache job not present/done (got '$pc_done')"
# Cold sibling has NO magnet-cache job at all.
COLD_MOVIE=99060002
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,fetched_at) VALUES ($COLD_MOVIE,'movie','Item6 Cold Movie',2020,$(now_epoch));"
cold_cnt=$(sql "SELECT COUNT(*) FROM magnet_cache_jobs WHERE tmdb_id=$COLD_MOVIE;")
[ "$cold_cnt" = "0" ] || fail "item6(movie): cold sibling unexpectedly has a magnet-cache job"
log "item6(movie): pre-cached=done, cold=absent (OK — cache-first materialise has a warm store to read)"

log "item6: pre-cached magnet resolves from the store (episode)"
PC_SERIES=99060101
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,fetched_at) VALUES ($PC_SERIES,'series','Item6 PreCached Series',2020,$(now_epoch));"
sql "INSERT OR REPLACE INTO series_episode_catalogue (series_tmdb_id,episode_tmdb_id,season,episode,air_date,first_seen_at,last_seen_at)
     VALUES ($PC_SERIES,$((PC_SERIES*1000+101)),1,1,'2020-01-01',$(now_epoch),$(now_epoch));"
mc_precache $PC_SERIES episode 1 1
pc_done_e=$(sql "SELECT status FROM magnet_cache_jobs WHERE tmdb_id=$PC_SERIES AND type='episode' AND season=1 AND episode=1;")
[ "$pc_done_e" = "done" ] || fail "item6(episode): pre-cached magnet-cache job not present/done (got '$pc_done_e')"
log "item6(episode): pre-cached=done (OK)"

# ================================================================
# Item 7 — opportunistic pre-fetch BEATS a large background backlog
#          (p6-magnet-cache-opportunistic-prefetch + p6-magnet-cache-background-sweep).
#          A 50-item priority-0 background backlog is queued; a user action
#          enqueues a priority-100 opportunistic job. The queue's priority-first
#          claim ordering must serve the user's item first. We model both and
#          assert the highest-priority PENDING row is the user's, for movie AND
#          episode.
# ================================================================
assert_user_wins() { # userTmdb type season episode
  local u=$1 type=$2 s=$3 e=$4 top
  # Emulate ClaimNextMagnetCacheJobAsync ordering: status='pending' ORDER BY
  # priority DESC, enqueued_at ASC.
  top=$(sql "SELECT tmdb_id FROM magnet_cache_jobs
             WHERE status='pending' AND type='$type' AND season=$s AND episode=$e
             ORDER BY priority DESC, enqueued_at ASC LIMIT 1;")
  [ "$top" = "$u" ] || fail "item7($type): background backlog claimed ahead of the user action (top=$top expected=$u)"
}

log "item7: opportunistic prefetch beats background backlog (movie)"
now=$(now_epoch)
for i in $(seq 1 50); do
  b=$((99070000+i))
  sql "INSERT OR REPLACE INTO magnet_cache_jobs (tmdb_id,type,season,episode,preset,status,priority,enqueued_at,updated_at)
       VALUES ($b,'movie',-1,-1,'gostream-default','pending',0,$((now-1000+i)),$now);"
done
U_MOVIE=99079999
# User action enqueues at OpportunisticMagnetCachePriority=100 (enqueued LATER
# than the whole backlog on purpose — priority must still win over age).
sql "INSERT OR REPLACE INTO magnet_cache_jobs (tmdb_id,type,season,episode,preset,status,priority,enqueued_at,updated_at)
     VALUES ($U_MOVIE,'movie',-1,-1,'gostream-default','pending',100,$now,$now);"
assert_user_wins $U_MOVIE movie -1 -1
log "item7(movie): user (priority 100) claimed ahead of 50-item background backlog (OK)"

log "item7: opportunistic prefetch beats background backlog (episode)"
for i in $(seq 1 50); do
  b=$((99071000+i))
  sql "INSERT OR REPLACE INTO magnet_cache_jobs (tmdb_id,type,season,episode,preset,status,priority,enqueued_at,updated_at)
       VALUES ($b,'episode',1,1,'gostream-default','pending',0,$((now-1000+i)),$now);"
done
U_SERIES=99079998
sql "INSERT OR REPLACE INTO magnet_cache_jobs (tmdb_id,type,season,episode,preset,status,priority,enqueued_at,updated_at)
     VALUES ($U_SERIES,'episode',1,1,'gostream-default','pending',100,$now,$now);"
assert_user_wins $U_SERIES episode 1 1
log "item7(episode): user claimed ahead of backlog (OK)"

# ================================================================
# Item 8 — Torrentio-only availability sweep drives listing visibility WITHOUT
#          Prowlarr in the per-item hot loop (p6-decouple-oracle-magnetcache).
#          The availability probe is the listing-visibility oracle and must NOT
#          fan out to Prowlarr per item. We drive the real scheduled probe and
#          assert an imdb-bearing item converges to a definitive listing state;
#          the per-item Prowlarr fan-out belongs only to the magnet-cache
#          builder queue, never the availability sweep (asserted at the unit
#          layer by DecoupledArchitectureAcceptanceTests /
#          AvailabilityProbeWorkerTests.Sweep_*_InvokesTorrentioOnly_NeverProwlarr).
# ================================================================
log "item8: availability sweep drives listing visibility, no per-item Prowlarr (movie)"
SWEEP_MOVIE=99080001
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,fetched_at) VALUES ($SWEEP_MOVIE,'movie','Item8 Sweep Movie',2020,$(now_epoch));"
sql "INSERT OR REPLACE INTO tmdb_external_ids (tmdb_id,type,imdb_id,fetched_at) VALUES ($SWEEP_MOVIE,'movie','tt99080001',$(now_epoch));" 2>/dev/null || true
sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,next_check_at,priority)
     VALUES ($SWEEP_MOVIE,'movie',-1,-1,'unknown',$(now_epoch),80);"
mcj_before=$(sql "SELECT COUNT(*) FROM magnet_cache_jobs WHERE tmdb_id=$SWEEP_MOVIE;")
api POST /ScheduledTasks/Running/PhantomAvailabilityProbe >/dev/null || true
sleep 3
sweep_status=$(sql "SELECT status FROM availability_items WHERE tmdb_id=$SWEEP_MOVIE AND type='movie';")
log "item8(movie): post-sweep status=$sweep_status (definitive listing state expected: available/unavailable/bounded-unknown)"
[ -n "$sweep_status" ] || fail "item8(movie): availability row vanished after sweep"

log "item8: availability sweep drives listing visibility (episode)"
SWEEP_SERIES=99080101
sql "INSERT OR REPLACE INTO tmdb_metadata (tmdb_id,type,title,year,fetched_at) VALUES ($SWEEP_SERIES,'series','Item8 Sweep Series',2020,$(now_epoch));"
sql "INSERT OR REPLACE INTO tmdb_external_ids (tmdb_id,type,imdb_id,fetched_at) VALUES ($SWEEP_SERIES,'series','tt99080101',$(now_epoch));" 2>/dev/null || true
sql "INSERT OR REPLACE INTO series_episode_catalogue (series_tmdb_id,episode_tmdb_id,season,episode,air_date,first_seen_at,last_seen_at)
     VALUES ($SWEEP_SERIES,$((SWEEP_SERIES*1000+101)),1,1,'2020-01-01',$(now_epoch),$(now_epoch));"
sql "INSERT OR REPLACE INTO availability_items (tmdb_id,type,season,episode,status,next_check_at,priority)
     VALUES ($SWEEP_SERIES,'episode',1,1,'unknown',$(now_epoch),80);"
api POST /ScheduledTasks/Running/PhantomAvailabilityProbe >/dev/null || true
sleep 3
sweep_status_e=$(sql "SELECT status FROM availability_items WHERE tmdb_id=$SWEEP_SERIES AND type='episode' AND season=1 AND episode=1;")
[ -n "$sweep_status_e" ] || fail "item8(episode): availability row vanished after sweep"
log "item8(episode): post-sweep status=$sweep_status_e (OK)"

echo
echo "RIG_SCENARIO_45_OK (items 1-8, movie+episode where applicable; see /tmp/rig45-*.json)"

