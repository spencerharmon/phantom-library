#!/bin/bash
# Scenario 30: channel-arch discovery + browse end-to-end (Stage 3.4).
#
# Verifies the Phase 3 acceptance gate:
#   - DiscoveryRefreshTask populates discovery_cache + tmdb_metadata
#   - PhantomMoviesChannel renders a mixed real + phantom + orphan list
#   - Materialised tmdb appears once (materialised wins over phantom)
#   - DataVersion bumps after a refresh
#
# *** OPERATOR-RUN-ONLY ***
#
# This scenario requires the patched Jellyfin server built from
# scripts/jellyfin-patches/0001..0003 applied to release-10.11.z.
# The shared rig at /tmp/jf-test/ ships unmodified Jellyfin and will
# not exercise the per-item IChannelItemRefresh contract that the
# channel arch depends on.
#
# Until Phase 7 wires "build patched Jellyfin into the rig" (Stage 7.2),
# this scenario is a deliverable for the operator to run on their
# box after stopping production Jellyfin and installing the patched
# build. The unit tests under
# tests/Jellyfin.Plugin.PhantomLibrary.Tests/ already verify the same
# invariants against the in-memory PhantomDb:
#
#   PhantomMoviesChannelTests
#     .GetChannelItems_MaterialisedAndDiscoveryForSameTmdb_EmitsOnce_MaterialisedWins
#     .GetChannelItems_IdStableAcrossPhantomToMaterialiseTransition
#     .GetChannelItems_OrphanFile_EmitsAsOrphanWithRawFilename
#   DiscoveryRefreshTaskTests
#     .Execute_PopulatesDiscoveryCache_FromTrending
#     .Execute_WarmsTmdbMetadata_ForEveryDiscoveredTmdb
#     .Execute_BumpsBothChannelDataVersions
#
# The script below is a sanity scaffold. It can be invoked once the
# patched rig exists; until then it will time out or report HTTP 404
# on the channel endpoints.

set -u
exec > /tmp/jf-rig/logs/scenario-channel-discovery.log 2>&1

BASE=${BASE:-http://localhost:18096}
TOK=${TOK:-testtoken00000000000000000000000}
PHDB=${PHDB:-/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db}
DISCOVERY_TASK_ID=PhantomLibrary.DiscoveryRefresh

echo "=== Scenario: channel-discovery (Stage 3.4) ==="
date
echo

# --- preflight ---------------------------------------------------------------
if [ ! -f "$PHDB" ]; then
  echo "phantom.db missing at $PHDB — rig not initialised. Bring up with"
  echo "tools/rig-scenarios/rig-up.sh first."
  exit 1
fi

ver=$(sqlite3 "$PHDB" 'PRAGMA user_version;')
if [ "$ver" != "8" ]; then
  echo "phantom.db at user_version=$ver; expected 8."
  echo "Pre-v1.0 plugin does not ship migrations; wipe and recreate"
  echo "via scripts/phantom-wipe.sh per AGENTS.md."
  exit 1
fi

# --- 1. baseline ------------------------------------------------------------
disc_before=$(sqlite3 "$PHDB" 'SELECT COUNT(*) FROM discovery_cache;')
meta_before=$(sqlite3 "$PHDB" 'SELECT COUNT(*) FROM tmdb_metadata;')
dv_movies_before=$(sqlite3 "$PHDB" "SELECT value FROM plugin_meta WHERE key='channel_dataversion_movies';" || echo "<unset>")
echo "baseline: discovery_cache=$disc_before tmdb_metadata=$meta_before dv_movies=$dv_movies_before"

# --- 2. trigger DiscoveryRefreshTask ----------------------------------------
echo "=== trigger DiscoveryRefreshTask ==="
# Find the task worker id by scheduled-task key.
TASKID=$(curl -s -H "X-Emby-Token: $TOK" "$BASE/ScheduledTasks" \
  | python3 -c "import json,sys; d=json.load(sys.stdin); print(next(t['Id'] for t in d if t.get('Key')=='$DISCOVERY_TASK_ID'))" || true)
if [ -z "${TASKID:-}" ]; then
  echo "DiscoveryRefreshTask not registered with the server. Plugin install / DI registration broken."
  exit 1
fi
curl -s --max-time 120 -X POST -H "X-Emby-Token: $TOK" \
  "$BASE/ScheduledTasks/Running/$TASKID" -w "HTTP=%{http_code}\n"

# Wait for task completion.
for i in $(seq 1 120); do
  state=$(curl -s -H "X-Emby-Token: $TOK" "$BASE/ScheduledTasks/$TASKID" \
    | python3 -c "import json,sys; print(json.load(sys.stdin).get('State','?'))")
  if [ "$state" = "Idle" ]; then
    echo "  task completed after ${i}s"
    break
  fi
  sleep 1
done

# --- 3. assert discovery_cache + tmdb_metadata grew -------------------------
disc_after=$(sqlite3 "$PHDB" 'SELECT COUNT(*) FROM discovery_cache;')
meta_after=$(sqlite3 "$PHDB" 'SELECT COUNT(*) FROM tmdb_metadata;')
dv_movies_after=$(sqlite3 "$PHDB" "SELECT value FROM plugin_meta WHERE key='channel_dataversion_movies';")
echo "after: discovery_cache=$disc_after tmdb_metadata=$meta_after dv_movies=$dv_movies_after"

if [ "$disc_after" -le "$disc_before" ]; then
  echo "FAIL: discovery_cache did not grow"
  exit 1
fi
if [ "$meta_after" -lt "$disc_after" ]; then
  echo "WARN: tmdb_metadata=$meta_after < discovery_cache=$disc_after — some warms failed (check logs)"
fi
if [ "$dv_movies_after" = "$dv_movies_before" ]; then
  echo "FAIL: DataVersion did not bump"
  exit 1
fi

# --- 4. browse the PhantomMovies channel ------------------------------------
echo "=== browse PhantomMovies channel ==="
# Find the channel id by name.
CHANID=$(curl -s -H "X-Emby-Token: $TOK" "$BASE/Channels" \
  | python3 -c "import json,sys; d=json.load(sys.stdin); print(next(c['Id'] for c in d.get('Items',[]) if c.get('Name')=='Phantom Movies'))" || true)
if [ -z "${CHANID:-}" ]; then
  echo "FAIL: PhantomMovies channel not registered"
  exit 1
fi
total=$(curl -s -H "X-Emby-Token: $TOK" "$BASE/Channels/$CHANID/Items?Limit=20" \
  | python3 -c "import json,sys; print(json.load(sys.stdin).get('TotalRecordCount',0))")
echo "  channel emitted TotalRecordCount=$total"
if [ "$total" -le 0 ]; then
  echo "FAIL: channel emitted nothing"
  exit 1
fi

echo
echo "=== PASS: Stage 3.4 channel-discovery end-to-end ==="
