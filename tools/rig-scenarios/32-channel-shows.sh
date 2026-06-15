#!/bin/bash
# Scenario 32: channel-arch shows-channel hierarchical browse + autopilot (Stage 5.3).
#
# Verifies the Phase 5 acceptance gate:
#   - "Phantom Shows" tile appears in library nav.
#   - Top-level browse lists series tiles (id "series_<tmdb>") sourced from
#     discovery_cache (type='series') plus the distinct series tmdb_ids
#     implied by materialised_state (type='episode').
#   - Clicking a series tile renders N season folders (id
#     "season_<tmdb>_s<NN>"), N = TmdbSeriesDetails.NumberOfSeasons.
#   - Clicking a season folder renders all episodes (id
#     "episode_<tmdb>_s<NN>e<NN>") fetched live from
#     /tv/{id}/season/{n}; each carries the splash MediaSource and the
#     'phantom' tag.
#   - Clicking a phantom episode plays the splash (file path is the
#     extracted splash.mp4 under PluginConfigurationsPath).
#   - Kebab -> Materialise on a phantom episode triggers the full Stage
#     4.2 materialise flow (see scenario 31 for the materialise-side
#     invariants); post-materialise the same episode tile carries the
#     FUSE-path MediaSource and no 'phantom' tag, and clicking play
#     hits the real file.
#   - Autopilot regression (plan §5.2): playing a materialised episode
#     past 80% triggers MaterialiseAsync for the next N episodes
#     (default SeriesAutopilotPrefetchEpisodes=1).
#   - Splash-guard regression: playing a STILL-PHANTOM (splash) episode
#     past 80% does NOT trigger autopilot (the listener filters items
#     carrying the 'phantom' tag; SeriesAutopilot also guards as
#     defence-in-depth).
#
# *** OPERATOR-RUN-ONLY ***
#
# Like scenarios 30 and 31, this requires the patched Jellyfin build
# from scripts/jellyfin-patches/. The shared /tmp/jf-test/ rig ships
# unmodified Jellyfin; the shows-channel post-flight refresh path
# depends on the patched IChannelItemRefreshManager and won't exercise
# correctly there. Phase 7.2 wires "build patched Jellyfin into the
# rig"; until then this scenario is a deliverable for the operator to
# run on their box after stopping production Jellyfin and installing
# the patched build.
#
# Unit-test coverage of the same invariants (run via `dotnet test`):
#
#   PhantomShowsChannelTests
#     .GetChannelItems_AllEmpty_ReturnsEmpty
#     .GetChannelItems_DiscoverySeriesOnly_EmitsSeriesFolder
#     .GetChannelItems_MaterialisedEpisodeOnly_EmitsSeriesFolder
#     .GetChannelItems_DiscoveryAndMaterialisedSameSeries_DedupesToOneTile
#     .GetChannelItems_DiscoveryWithoutMetadata_Skipped
#     .GetChannelItems_FolderIdMalformed_ReturnsEmpty
#     .GetChannelItems_SeriesFolder_EmitsNSeasons
#     .GetChannelItems_SeriesFolder_TmdbReturnsNull_ReturnsEmpty
#     .GetChannelItems_SeasonFolder_EmitsPhantomEpisodesWithSplash
#     .GetChannelItems_SeasonFolder_MaterialisedEpisode_EmitsFuseAndNoPhantomTag
#     .GetChannelItems_SeasonFolder_TmdbReturnsNull_ReturnsEmpty
#     .EpisodeId_StableAcrossPhantomToMaterialiseTransition
#     .GetChannelItemAsync_Series_Resolves
#     .GetChannelItemAsync_Season_Resolves
#     .GetChannelItemAsync_Episode_BeforeMaterialise_ReturnsSplashWithPhantomTag
#     .GetChannelItemAsync_Episode_AfterMaterialise_ReturnsFusePath
#     .GetChannelItemAsync_Episode_UsesCacheWhenPresent_NoTmdbCall
#     .GetChannelItemAsync_Malformed_ReturnsNull
#     .GetChannelItemAsync_Movie_ReturnsNull
#     .GetChannelItemMediaInfo_PhantomEpisode_ReturnsSplash
#     .GetChannelItemMediaInfo_MaterialisedEpisode_ReturnsFusePath
#     .GetChannelItemMediaInfo_Series_ReturnsEmpty
#     .GetChannelItemMediaInfo_Season_ReturnsEmpty
#     .GetChannelItemMediaInfo_Garbage_ReturnsEmpty
#     .GetLatestMedia_ReturnsMaterialisedEpisodesNewestFirst
#   SeriesAutopilotTests
#     .BelowThreshold_NoMaterialise
#     .AtThreshold_PhantomTagged_NoMaterialise_SplashGuard
#     .NonPhantomChannel_NoMaterialise
#     .Disabled_NoMaterialise
#     .PrefetchZero_NoMaterialise
#     .HappyPath_PrefetchesNextNEpisodes
#     .SkipsAlreadyMaterialisedEpisode
#     .SkipsAlreadyInFlightEpisode
#     .CrossesSeasonBoundary
#     .EndOfSeries_NoMoreSeasons_NoMaterialise
#     .MalformedExternalId_NoMaterialise
#     .WrongKindExternalId_NoMaterialise
#     .TmdbReturnsNull_NoMaterialise
#
# The rig steps below are the operator-runnable scaffold the Phase 7.x
# patched-Jellyfin rig will lift into an automated bash flow once the
# patched build is bootstrapped into /tmp/jf-test/.

set -euo pipefail

PHANTOM_DB=${PHANTOM_DB:-/var/lib/jellyfin/plugins/configurations/PhantomLibrary/phantom.db}
JELLYFIN_DB=${JELLYFIN_DB:-/var/lib/jellyfin/data/jellyfin.db}
# Default to TMDB id 1399 (Game of Thrones) — long-running series with
# multiple seasons makes the cross-season autopilot path easy to drive.
SERIES_TMDB=${SERIES_TMDB:-1399}
API=${API:-http://127.0.0.1:18096}
TOKEN=${TOKEN:-}   # operator-supplied API key

if [[ -z "${TOKEN}" ]]; then
  echo "TOKEN env var required (Jellyfin API key for the patched rig)" >&2
  exit 1
fi

phantom_sql() { sqlite3 "${PHANTOM_DB}" "$@"; }
jellyfin_sql() { sqlite3 "${JELLYFIN_DB}" "$@"; }

echo "[1/10] Wipe + start patched Jellyfin"
echo "       bash scripts/phantom-wipe.sh --commit     # answer WIPE"
echo "       systemctl --user start jellyfin-rig.service"
echo "       (wait for /System/Info to respond)"

echo "[2/10] Run DiscoveryRefreshTask so the series tile appears"
echo "       curl -X POST -H 'X-Emby-Token: ${TOKEN}' \\"
echo "         ${API}/ScheduledTasks/Running/<phantom-discovery-task-id>"
echo "       (poll /ScheduledTasks/<id> until Idle)"

echo "[3/10] Verify discovery_cache series populated and the chosen tmdb"
echo "       is present (or insert one for determinism):"
echo "       sqlite3 ${PHANTOM_DB} 'SELECT COUNT(*) FROM discovery_cache WHERE type=\"series\";'"
echo "       sqlite3 ${PHANTOM_DB} 'INSERT OR IGNORE INTO discovery_cache(tmdb_id, type, discovered_at, last_refreshed) VALUES (${SERIES_TMDB}, \"series\", strftime(\"%s\",\"now\"), strftime(\"%s\",\"now\"));'"
echo "       (note: requires tmdb_metadata to also be warmed; trigger"
echo "        DiscoveryRefreshTask once more if you inserted by hand.)"

echo "[4/10] Browse Phantom Shows channel top-level"
echo "       curl -H 'X-Emby-Token: ${TOKEN}' \\"
echo "         '${API}/Items?ParentId=<phantom-shows-channel-guid>&Recursive=false' \\"
echo "         | jq '.Items[] | select(.ExternalId == \"series_${SERIES_TMDB}\")'"
echo "       record .Id as SERIES_ITEM_ID; assert Type==Series."

echo "[5/10] Browse the series folder \u2192 list of seasons"
echo "       curl -H 'X-Emby-Token: ${TOKEN}' \\"
echo "         '${API}/Items?ParentId=<SERIES_ITEM_ID>&Recursive=false'"
echo "       Assert: N season folders, each ExternalId matches"
echo "         'season_${SERIES_TMDB}_s<NN>', Type==Season."

echo "[6/10] Browse season 1 \u2192 list of episodes"
echo "       curl -H 'X-Emby-Token: ${TOKEN}' \\"
echo "         '${API}/Items?ParentId=<SEASON_1_ITEM_ID>&Recursive=false'"
echo "       Assert: every episode ExternalId matches"
echo "         'episode_${SERIES_TMDB}_s01e<NN>', Type==Episode,"
echo "         Tags contains 'phantom', MediaSources[0].Path is the"
echo "         splash.mp4 (under <PluginConfigurationsPath>/PhantomLibrary/)."
echo "       Verify tmdb_episode_cache populated:"
echo "         sqlite3 ${PHANTOM_DB} \"SELECT COUNT(*) FROM tmdb_episode_cache WHERE series_tmdb_id=${SERIES_TMDB} AND season=1;\""

echo "[7/10] Play a phantom episode \u2192 splash"
echo "       (Drive via the web client or)"
echo "       curl -H 'X-Emby-Token: ${TOKEN}' \\"
echo "         '${API}/Items/<EP_ITEM_ID>/PlaybackInfo' \\"
echo "         | jq '.MediaSources[0].Path'"
echo "       Assert: ends in /splash.mp4 (the bundled looped placeholder)."

echo "[8/10] Materialise the episode"
echo "       curl -X POST -H 'X-Emby-Token: ${TOKEN}' \\"
echo "         ${API}/Plugins/PhantomLibrary/Materialise/<EP_ITEM_ID>"
echo "       (expect: Status: Success, FusePath populated)"
echo "       Re-fetch /Items/<EP_ITEM_ID>?Fields=MediaSources,Tags"
echo "       Assert: MediaSources[0].Path is FUSE path (not splash)."
echo "       Assert: Tags does NOT contain 'phantom'."
echo "       Re-fetch /Items/<EP_ITEM_ID>/PlaybackInfo"
echo "       Assert: still the FUSE path (post-flight"
echo "         InvalidateMediaInfoCache=true regression \u2014 see plan"
echo "         critic v2 BLOCKER 5)."

echo "[9/10] Autopilot positive regression"
echo "       Simulate finishing the materialised episode at 90%:"
echo "       curl -X POST -H 'X-Emby-Token: ${TOKEN}' \\"
echo "         -H 'Content-Type: application/json' \\"
echo "         -d '{\"PlayedPercentage\": 90, \"PlaybackPositionTicks\": <90% of RunTimeTicks>}' \\"
echo "         ${API}/UserPlayedItems/<EP_ITEM_ID>"
echo "       (Or simpler: POST a Played marker via /Users/<uid>/PlayedItems.)"
echo "       Wait ~5s for fire-and-forget materialise + gostream add."
echo "       sqlite3 ${PHANTOM_DB} \"SELECT tmdb_id, season, episode FROM materialised_state WHERE tmdb_id=${SERIES_TMDB} AND type='episode' ORDER BY season, episode;\""
echo "       Assert: next episode (s01e02 if you played s01e01) appears."

echo "[10/10] Autopilot splash-guard regression"
echo "       Pick a STILL-PHANTOM episode (no materialised_state row,"
echo "       Tags contains 'phantom'). Simulate the same play-at-90%."
echo "       Wait ~5s."
echo "       Assert: no new materialised_state row for that series."
echo "       Assert: no new materialise_in_flight row."
echo "       Regression for the v2 critic's storm scenario: a 10-second"
echo "       splash play must not kick the autopilot."

echo ""
echo "Stage 5.3 scaffold complete. Once Phase 7.2 wires the patched"
echo "Jellyfin into /tmp/jf-test/, the above steps lift directly into"
echo "an automated bash flow with assertions."
