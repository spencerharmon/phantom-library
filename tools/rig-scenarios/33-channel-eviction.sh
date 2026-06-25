#!/bin/bash
# Scenario 33: channel-arch eviction sweeper (Stage 6.2 / plan §6.2).
#
# Verifies the Phase 6 acceptance gate for EvictionSweeper:
#   - A materialised item that is idle (no recent plays AND
#     materialised outside the EvictionIdleDays window) is evicted:
#       * gostream.RemoveAsync(stub_path) is called,
#       * materialised_state row deleted,
#       * channel item refreshed so the next browse renders the
#         phantom MediaSource (splash) + 'phantom' tag,
#       * ChannelStateProvider DataVersion bumped.
#   - A materialised item favourited by any user is NOT evicted
#     when ProtectFavourites=true (default).
#   - A recently-played materialised item is NOT evicted regardless
#     of materialised_at.
#   - A recently-materialised + never-played item is NOT evicted
#     (operator's grace window).
#   - gostream.RemoveAsync failure leaves the materialised_state row
#     intact (next tick retries) and does NOT fire the
#     post-eviction refresh.
#   - Orphan materialised_state row (no BaseItem for ExternalId)
#     surfaces a warning and skips, leaving the row intact for
#     operator inspection rather than silently evicting.
#
# *** OPERATOR-RUN-ONLY ***
#
# Like scenarios 30 / 31 / 32, this requires the patched Jellyfin
# build from scripts/jellyfin-patches/. The shared /var/tmp/jf-test/ rig
# ships unmodified Jellyfin; EvictionSweeper's post-evict refresh
# depends on the patched IChannelItemRefreshManager and won't exercise
# correctly there. Phase 7.2 wires "build patched Jellyfin into the
# rig"; until then this scenario is a deliverable for the operator to
# run on their box after stopping production Jellyfin and installing
# the patched build.
#
# Unit-test coverage of the same invariants (run via `dotnet test`):
#
#   EvictionSweeperTests
#     .IdleMovie_NeverPlayed_OldEnough_EvictsCleanly
#     .IdleMovie_LastPlayedLongAgo_Evicts
#     .FavouriteProtected_NoEviction
#     .RecentlyPlayed_NoEviction
#     .RecentlyMaterialised_NeverPlayed_NoEviction
#     .GostreamRemoveFails_StateRowStays_NoRefresh
#     .OrphanStateRow_NoBaseItem_LogsAndSkips
#     .IdleEpisode_Evicts_RefreshesShowsChannel
#
# Operator trigger:
#   EvictionSweeper is an IHostedService driven by NCrontab using the
#   EvictionScheduleCron config field (default "0 4 * * *", daily at
#   04:00 UTC). For deterministic rig runs, either:
#     (a) temporarily set EvictionScheduleCron to "* * * * *" via the
#         plugin config XML, restart Jellyfin, and wait one minute, OR
#     (b) restart Jellyfin with the cron at e.g. "*/2 * * * *" so a
#         tick fires within ~2 minutes of plugin startup.
#   A dedicated /Plugins/PhantomLibrary/Eviction/RunNow REST endpoint
#   is a Phase 7+ deliverable; until then the cron-poke is the
#   canonical operator trigger.
#
# The rig steps below are the operator-runnable scaffold the Phase 7.x
# patched-Jellyfin rig will lift into an automated bash flow once the
# patched build is bootstrapped into /var/tmp/jf-test/.

set -euo pipefail

PHANTOM_DB=${PHANTOM_DB:-/var/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db}
JELLYFIN_DB=${JELLYFIN_DB:-/var/tmp/jf-test/data/data/jellyfin.db}
# Default DB paths point at the existing rig clone. Override only with another
# sandbox clone; never point scenario tests at production DBs.
PLUGIN_CFG=${PLUGIN_CFG:-/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.PhantomLibrary.xml}
# Default to TMDB id 603 (The Matrix) for the idle-eviction movie target.
MOVIE_TMDB=${MOVIE_TMDB:-603}
FAV_TMDB=${FAV_TMDB:-604}
RECENT_TMDB=${RECENT_TMDB:-605}
YOUNG_TMDB=${YOUNG_TMDB:-606}
API=${API:-http://127.0.0.1:18096}
TOKEN=${TOKEN:-}   # operator-supplied API key

if [[ -z "${TOKEN}" ]]; then
  echo "TOKEN env var required (Jellyfin API key for the patched rig)" >&2
  exit 1
fi

phantom_sql() { sqlite3 "${PHANTOM_DB}" "$@"; }
jellyfin_sql() { sqlite3 "${JELLYFIN_DB}" "$@"; }

echo "[1/12] Wipe + start patched Jellyfin"
echo "       bash scripts/phantom-wipe.sh --commit     # answer WIPE"
echo "       systemctl --user start jellyfin-rig.service"
echo "       (wait for /System/Info to respond)"

echo "[2/12] Configure EvictionScheduleCron for a fast tick"
echo "       Edit ${PLUGIN_CFG}:"
echo "         <EvictionScheduleCron>*/2 * * * *</EvictionScheduleCron>"
echo "         <EvictionIdleDays>30</EvictionIdleDays>"
echo "         <ProtectFavourites>true</ProtectFavourites>"
echo "         <EvictionEnabled>true</EvictionEnabled>"
echo "       systemctl --user restart jellyfin-rig.service"

echo "[3/12] Run DiscoveryRefreshTask + materialise four test movies"
echo "       For TMDB in ${MOVIE_TMDB} ${FAV_TMDB} ${RECENT_TMDB} ${YOUNG_TMDB}:"
echo "         curl -X POST -H 'X-Emby-Token: ${TOKEN}' \\"
echo "           ${API}/Plugins/PhantomLibrary/Materialise/<movie_BaseItem_Id>"
echo "       Verify all four rows exist:"
echo "         sqlite3 ${PHANTOM_DB} \\"
echo "           \"SELECT tmdb_id, stub_path FROM materialised_state WHERE type='movie' AND tmdb_id IN (${MOVIE_TMDB}, ${FAV_TMDB}, ${RECENT_TMDB}, ${YOUNG_TMDB});\""

echo "[4/12] Backdate materialised_at to >30d ago for the idle + fav targets"
echo "       sqlite3 ${PHANTOM_DB} \\"
echo "         \"UPDATE materialised_state SET materialised_at = strftime('%s','now','-45 days') WHERE tmdb_id IN (${MOVIE_TMDB}, ${FAV_TMDB}, ${RECENT_TMDB}) AND type='movie';\""
echo "       (YOUNG_TMDB row keeps materialised_at = now → must NOT evict.)"

echo "[5/12] Favourite FAV_TMDB as any user (UI or REST)"
echo "       curl -X POST -H 'X-Emby-Token: ${TOKEN}' \\"
echo "         ${API}/Users/<user_id>/FavoriteItems/<fav_BaseItem_Id>"
echo "       Verify:"
echo "         sqlite3 ${JELLYFIN_DB} \\"
echo "           \"SELECT IsFavorite FROM UserDatas WHERE Key=(SELECT UserDataKey FROM BaseItems WHERE Id=lower(hex('<fav_BaseItem_Id_no_hyphens>')));\""

echo "[6/12] Mark RECENT_TMDB as played within the last few days"
echo "       curl -X POST -H 'X-Emby-Token: ${TOKEN}' \\"
echo "         '${API}/Users/<user_id>/PlayedItems/<recent_BaseItem_Id>?DatePlayed=$(date -u -d '-3 days' +%Y-%m-%dT%H:%M:%SZ)'"
echo "       Verify:"
echo "         sqlite3 ${JELLYFIN_DB} \\"
echo "           \"SELECT LastPlayedDate FROM UserDatas WHERE Key=(SELECT UserDataKey FROM BaseItems WHERE Id=lower(hex('<recent_BaseItem_Id_no_hyphens>')));\""

echo "[7/12] Wait for the next eviction tick (up to 2 minutes given */2 cron)"
echo "       journalctl --user -fu jellyfin-rig.service | grep -i '\\[Eviction\\]'"
echo "       Expect log lines:"
echo "         [Eviction] tick start: 4 candidate row(s) ..."
echo "         [Eviction] evicted ExternalId=movie_${MOVIE_TMDB} stub_path=..."
echo "         [Eviction] tick done: evicted=1 fav=1 recent=1 young=1 orphan=0 removeFailed=0"

echo "[8/12] Assert MOVIE_TMDB evicted: state row gone, gostream remove fired"
echo "       sqlite3 ${PHANTOM_DB} \\"
echo "         \"SELECT COUNT(*) FROM materialised_state WHERE tmdb_id=${MOVIE_TMDB} AND type='movie';\""
echo "       Expect: 0"
echo "       Verify gostream side (per gostream's library list endpoint):"
echo "         curl -fsS http://127.0.0.1:9080/api/library/list | jq '.[] | select(.tmdb==${MOVIE_TMDB})'"
echo "       Expect: empty result."

echo "[9/12] Assert FAV_TMDB protected: state row stays"
echo "       sqlite3 ${PHANTOM_DB} \\"
echo "         \"SELECT COUNT(*) FROM materialised_state WHERE tmdb_id=${FAV_TMDB} AND type='movie';\""
echo "       Expect: 1"

echo "[10/12] Assert RECENT_TMDB protected: state row stays"
echo "        sqlite3 ${PHANTOM_DB} \\"
echo "          \"SELECT COUNT(*) FROM materialised_state WHERE tmdb_id=${RECENT_TMDB} AND type='movie';\""
echo "        Expect: 1"

echo "[11/12] Assert YOUNG_TMDB protected (recently materialised, never played)"
echo "        sqlite3 ${PHANTOM_DB} \\"
echo "          \"SELECT COUNT(*) FROM materialised_state WHERE tmdb_id=${YOUNG_TMDB} AND type='movie';\""
echo "        Expect: 1"

echo "[12/12] Post-eviction browse: MOVIE_TMDB renders splash + 'phantom' tag"
echo "        curl -H 'X-Emby-Token: ${TOKEN}' \\"
echo "          '${API}/Items/<movie_BaseItem_Id>?Fields=MediaSources,Tags' | jq '{Tags, Path: .MediaSources[0].Path}'"
echo "        Expect:"
echo "          Tags contains 'phantom'"
echo "          MediaSources[0].Path ends in /splash.mp4"

echo ""
echo "Negative-path scenarios to drive separately:"
echo ""
echo "  (A) gostream-failure regression:"
echo "      systemctl --user stop gostream.service"
echo "      Backdate another materialised row to >30d, wait one tick."
echo "      Expect log: '[Eviction] gostream RemoveAsync failed for stub_path=... leaving state row intact'"
echo "      Assert: state row STILL present (next tick will retry)."
echo "      Restart gostream; wait one more tick; assert state row gone."
echo ""
echo "  (B) orphan regression:"
echo "      INSERT INTO materialised_state for a tmdb that has no BaseItem"
echo "      (e.g. delete the BaseItem first via DELETE FROM BaseItems WHERE Id=...)"
echo "      then backdate, wait one tick."
echo "      Expect log: '[Eviction] orphan materialised_state row; no BaseItem for ExternalId=... skipping (operator should inspect)'"
echo "      Assert: state row STILL present (orphans are NOT silently swept)."
echo ""
echo "Stage 6.2 scaffold complete. Once Phase 7.2 wires the patched"
echo "Jellyfin into /var/tmp/jf-test/, the above steps lift directly into"
echo "an automated bash flow with assertions."
