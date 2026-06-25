#!/bin/bash
# Scenario 31: channel-arch materialise flow end-to-end (Stage 4.4).
#
# Verifies the Phase 4 acceptance gate:
#   - User clicks Materialise on a phantom movie tile (channel item)
#   - In-flight row appears in materialise_in_flight
#   - Pre-flight RefreshChannelItem fires (badge -> Materialising)
#   - gostream add returns FUSE path; FUSE path appears on disk
#   - Materialised_state row written
#   - Post-flight RefreshChannelItem fires with ForceProbe=true
#     (real MediaSource replaces the splash; probe re-runs)
#   - In-flight row cleared in finally
#   - DataVersion bumped twice (so client cache invalidates)
#   - Second click is idempotent (returns Duplicate, no gostream call)
#
# Regression assertions (per plan §4.4):
#   - BaseItem.MediaStreams reflect the real file's codecs, NOT the
#     splash's, after materialise (critic v2 BLOCKER 4: probe pinning).
#   - Cache-invalidate flag works: a fresh /Items/<id>/PlaybackInfo
#     returns the real file immediately, no 5-min wait
#     (critic v2 BLOCKER 5).
#   - Crash-sweep: kill rig Jellyfin between in-flight insert and
#     materialised_state insert; restart; MaterialiseInFlightSweeper
#     deletes the stale row within ~15s + threshold.
#
# *** OPERATOR-RUN-ONLY ***
#
# Like scenario 30, this requires the patched Jellyfin build from
# scripts/jellyfin-patches/0001..0003. The shared /tmp/jf-test/ rig
# ships unmodified Jellyfin; the channel-arch materialise path
# depends on the patched IChannelItemRefreshManager and won't
# exercise correctly there. Phase 7.2 wires "build patched Jellyfin
# into the rig"; until then this scenario is a deliverable for the
# operator to run on their box after stopping production Jellyfin
# and installing the patched build.
#
# Unit-test coverage of the same invariants (run via `dotnet test`):
#
#   MaterialiserTests
#     .TupleMovie_HappyPath_WritesMaterialisedState_CallsRefreshTwice
#     .SentinelDiscipline_MovieUsesMinusOnePair
#     .AlreadyMaterialised_ReturnsDuplicate_NoGostreamCall
#     .AlreadyInFlight_ReturnsAlreadyInProgress_NoGostreamCall
#     .GostreamFails_NoMaterialisedRow_InFlightCleanedUp_ErrorReturned
#     .PreFlightRefreshThrows_MaterialiseStillProceeds
#     .MagnetSelectorReturnsNull_WritesUnavailableMarker_ReturnsError
#     .SeriesType_Rejected
#     .EpisodeWithoutImdb_ReturnsError
#     .LegacyGuidWrapper_RoutesMovieExternalIdToTuplePath
#     .LegacyGuidWrapper_SeriesExternalIdRejected
#   MaterialiseInFlightSweeperTests
#     .PurgesStaleRows_LeavesFreshRows
#     .SweeperHostedService_RunOnceAsync_RespectsConfigThreshold
#   PhantomLibraryBadgesControllerTests
#     .* (all 8: state derivation precedence + edge cases)
#   MagnetSelectorTests
#     .AggregatesFromAllIndexers_PicksScorerWinner
#     .NoCandidates_ReturnsNull
#     .SkipsDisabledIndexers
#     .IndexerThrows_SwallowedAndOtherIndexersStillScored
#     .EpisodeQuery_PassesSeriesImdb
#
# The rig scenario below is a step-by-step scaffold the operator
# can drive manually or that the Stage 7.x rig harness can lift
# into a fully automated bash flow once the patched Jellyfin is
# bootstrapped into /tmp/jf-test/.

set -euo pipefail

PHANTOM_DB=${PHANTOM_DB:-/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db}
JELLYFIN_DB=${JELLYFIN_DB:-/tmp/jf-test/data/data/jellyfin.db}
# Default DB paths point at the existing rig clone. Override only with another
# sandbox clone; never point scenario tests at production DBs.
TMDB_ID=${TMDB_ID:-872585}   # default: a recent movie expected in trending
API=${API:-http://127.0.0.1:18096}
TOKEN=${TOKEN:-}   # operator-supplied API key

if [[ -z "${TOKEN}" ]]; then
  echo "TOKEN env var required (Jellyfin API key for the patched rig)" >&2
  exit 1
fi

phantom_sql() { sqlite3 "${PHANTOM_DB}" "$@"; }
jellyfin_sql() { sqlite3 "${JELLYFIN_DB}" "$@"; }

echo "[1/9] Wipe + start patched Jellyfin"
echo "      bash scripts/phantom-wipe.sh --commit     # answer WIPE"
echo "      systemctl --user start jellyfin-rig.service"
echo "      (wait for /System/Info to respond)"

echo "[2/9] Run DiscoveryRefreshTask"
echo "      curl -X POST -H 'X-Emby-Token: ${TOKEN}' \\"
echo "        ${API}/ScheduledTasks/Running/<phantom-discovery-task-id>"
echo "      (poll /ScheduledTasks/<id> until Idle)"

echo "[3/9] Verify discovery_cache populated"
echo "      sqlite3 ${PHANTOM_DB} 'SELECT COUNT(*) FROM discovery_cache WHERE type=\"movie\";'"
echo "      (expect: >= 20)"

echo "[4/9] Browse Phantom Movies channel; find target tmdb=${TMDB_ID}"
echo "      curl -H 'X-Emby-Token: ${TOKEN}' \\"
echo "        '${API}/Items?ParentId=<phantom-movies-channel-guid>&Recursive=true' \\"
echo "        | jq '.Items[] | select(.ExternalId == \"movie_${TMDB_ID}\")'"
echo "      record .Id as MOVIE_ITEM_ID"

echo "[5/9] Confirm initial state = Phantom"
echo "      curl -X POST -H 'X-Emby-Token: ${TOKEN}' \\"
echo "        -H 'Content-Type: application/json' \\"
echo "        -d '{\"ids\":[\"<MOVIE_ITEM_ID>\"]}' \\"
echo "        ${API}/Plugins/PhantomLibrary/States"
echo "      (expect: {\"<MOVIE_ITEM_ID>\": \"Phantom\"})"

echo "[6/9] Trigger materialise"
echo "      curl -X POST -H 'X-Emby-Token: ${TOKEN}' \\"
echo "        ${API}/Plugins/PhantomLibrary/Materialise/<MOVIE_ITEM_ID>"
echo "      (expect: Status: Success, FusePath populated)"
echo ""
echo "      During the call, in another shell:"
echo "      watch -n 0.2 \"sqlite3 ${PHANTOM_DB} \\\"SELECT * FROM materialise_in_flight; SELECT * FROM materialised_state WHERE tmdb_id=${TMDB_ID};\\\"\""
echo "      (expect: in-flight row appears, then disappears + state row appears)"

echo "[7/9] Verify post-materialise state"
echo "      Repeat step [5] States query \u2192 expect \"Materialised\""
echo "      Re-browse the channel item:"
echo "        curl -H 'X-Emby-Token: ${TOKEN}' \\"
echo "          '${API}/Items/<MOVIE_ITEM_ID>?Fields=MediaSources,MediaStreams'"
echo "      Assert: MediaSources[0].Path is the FUSE path (not splash)."
echo "      Assert: MediaStreams reflect the real file's video codec, NOT"
echo "              the splash's. Regression for critic v2 BLOCKER 4."

echo "[8/9] PlaybackInfo cache invalidation regression"
echo "      curl -X POST -H 'X-Emby-Token: ${TOKEN}' \\"
echo "        '${API}/Items/<MOVIE_ITEM_ID>/PlaybackInfo'"
echo "      Assert: MediaSources[0].Path is FUSE path."
echo "      Regression for critic v2 BLOCKER 5 (5-min cache should be"
echo "      invalidated by post-flight InvalidateMediaInfoCache=true)."

echo "[9/9] Crash-sweep regression"
echo "      Reproduce crash mid-materialise:"
echo "        - Insert in-flight row manually:"
echo "          sqlite3 ${PHANTOM_DB} \"INSERT INTO materialise_in_flight"
echo "            (tmdb_id, type, season, episode, started_at)"
echo "            VALUES (999, 'movie', -1, -1, strftime('%s', 'now', '-1 hour'));\""
echo "        - Restart rig Jellyfin."
echo "        - Wait ~30s for MaterialiseInFlightSweeper to fire."
echo "        - sqlite3 ${PHANTOM_DB} 'SELECT * FROM materialise_in_flight WHERE tmdb_id=999;'"
echo "        - Assert: zero rows. (Default stale threshold is 10m;"
echo "          we backdated 1 hour so it's well past.)"

echo ""
echo "Stage 4.4 scaffold complete. Once Phase 7.2 wires the patched"
echo "Jellyfin into /tmp/jf-test/, the above steps lift directly into"
echo "an automated bash flow with assertions."
