#!/bin/bash
# Scenario 42: per-user show/hide + per-user prefs + per-user favourite
# protection, LIVE against the patched rig Jellyfin (REQ-M14-PER-USER, 4/4).
#
# This is the mandatory user-visible live rig for the per-user surface. It
# drives the real plugin over HTTP with TWO distinct, non-elevated users (A and
# B) so the per-user isolation is exercised through Jellyfin's actual
# Jellyfin-UserId claim resolution — never a route/body parameter — and through
# the real per-user channel-item cache key (IHasCacheKey.GetCacheKey(userId)).
#
# Verified end to end, for BOTH a movie and a series (episode dimension):
#   1. Show/hide is per-user and user-visible:
#      - baseline: both A and B see phantom movie 99000001 and phantom series
#        99100001 in their channel browse.
#      - A hides the title -> it disappears from A's OWN channel browse but
#        stays visible in B's; GET User/Hidden reports hidden=true for A and
#        hidden=false for B; user_hidden_items has exactly A's row.
#      - hiding the SERIES also removes it (and therefore its seasons/episodes)
#        from A's Phantom Shows browse while B can still drill series -> season
#        -> episodes (the episode dimension of the requirement).
#      - A unhides -> the title returns to A's browse; hidden=false for both;
#        user_hidden_items row is gone.
#   2. Per-user prefs toggle end to end:
#      - GET User/Prefs returns all-on defaults for a fresh user.
#      - A POSTs protectFavourites=false; GET User/Prefs echoes it for A but B
#        still reads defaults (all on); user_prefs has exactly A's row.
#   3. Per-user favourite protection (the sweeper's live input):
#      - the EvictionSweeper reads GetUserPrefsAsync(userId).ProtectFavourites
#        per favouriting user; this scenario asserts that per-user input is
#        wired and isolated live (A's protect_favourites=0 does not change B's
#        effective protect=1). The eviction DECISION itself (a favourite pins a
#        shared file only while >=1 favouriting user keeps protect on) has no
#        on-demand HTTP trigger — EvictionSweeper is a cron hosted service, not
#        an IScheduledTask — so its decision matrix is covered exhaustively by
#        EvictionSweeperTests (per-user protect on/off, opt-out, movie/TV
#        parity). See docs/tasks/m14-per-user-rig.md.
#
# *** OPERATOR-RUN-ONLY ***
#
# Requires the patched channel-arch Jellyfin (scripts/jellyfin-patches/*) AND a
# plugin DLL built from this branch (it exercises the per-user User/Hidden,
# User/Prefs endpoints and the IHasCacheKey channel cache key). Bring the rig up
# with tools/rig-scenarios/rig-up.sh --reset first, then run this script. The
# equivalent invariants are also covered by the in-memory unit tests:
#   PhantomLibraryUserControllerTests (hide/unhide/prefs claim resolution + 400s)
#   PhantomMoviesChannelTests / PhantomShowsChannelTests (per-user visible rows,
#     hidden short-circuit, IHasCacheKey.GetCacheKey)
#   EvictionSweeperTests (per-user favourite protection decision matrix)
#   PhantomDbTests (user_prefs / user_hidden_items persistence)

set -u
mkdir -p /tmp/jf-rig/logs
exec > >(tee /tmp/jf-rig/logs/scenario-per-user-show-hide.log) 2>&1

BASE=${BASE:-http://localhost:18096}
TOK=${TOK:-testtoken00000000000000000000000}          # admin API key (rig-up)
PHDB=${PHDB:-/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db}

DISCOVERY_TASK_ID=PhantomLibrary.DiscoveryRefresh
MOVIE_TMDB=99000001          # "Phantom Rig Alpha" (tmdb-mock discover fixture)
SERIES_TMDB=99100001         # "Phantom Rig Delta"  (tmdb-mock discover fixture)

USER_A=phantom-rig-a
USER_B=phantom-rig-b
PW_A=rigpass-a
PW_B=rigpass-b

echo "=== Scenario: per-user show/hide + prefs + favourite-protection (REQ-M14-PER-USER) ==="
date
echo

fail() { echo "FAIL: $*" >&2; exit 1; }
count() { sqlite3 "$PHDB" "$1"; }

# trap-clean: tear down the two rig-only test users (and their per-user
# prefs/hidden rows) on ANY exit — pass, fail, or interrupt — so neither the
# rig clone nor (never — this only ever runs against :18096) production is left
# polluted. GUIDs are captured into UID_A/UID_B once the users exist.
UID_A=""; UID_B=""
cleanup() {
  local rc=$?
  for uid in "$UID_A" "$UID_B"; do
    [ -n "$uid" ] || continue
    curl -s -o /dev/null -X DELETE -H "X-Emby-Token: $TOK" "$BASE/Users/$uid" || true
    sqlite3 "$PHDB" "DELETE FROM user_prefs WHERE user_id='$uid'; DELETE FROM user_hidden_items WHERE user_id='$uid';" 2>/dev/null || true
  done
  return $rc
}
trap cleanup EXIT INT TERM

# ---------------------------------------------------------------- preflight
if [ ! -f "$PHDB" ]; then
  fail "phantom.db missing at $PHDB — bring up the rig with tools/rig-scenarios/rig-up.sh --reset first."
fi

ver=$(count 'PRAGMA user_version;')
if [ "$ver" != "12" ]; then
  fail "phantom.db at user_version=$ver; expected 12 (per-user schema). Wipe and rebuild (scripts/phantom-wipe.sh)."
fi

# The per-user endpoints must exist in this DLL. Probe with the admin API key:
# it carries no Jellyfin-UserId claim, so the endpoint must answer 401 (present
# but no acting user) rather than 404 (endpoint missing from an old DLL).
probe=$(curl -s -o /dev/null -w '%{http_code}' -H "X-Emby-Token: $TOK" \
  "$BASE/Plugins/PhantomLibrary/User/Prefs")
case "$probe" in
  404) fail "User/Prefs returned 404 — plugin DLL predates the per-user surface. Rebuild + redeploy." ;;
  401) echo "preflight ok: schema v12, User/Prefs present, admin key correctly has no acting user (401)" ;;
  *)   echo "preflight note: User/Prefs probe with admin key returned $probe (expected 401; continuing)" ;;
esac

# ---------------------------------------------------------------- helpers
# Create a non-admin user (idempotent) and echo its Jellyfin user GUID.
ensure_user() {
  local name=$1 pw=$2 uid
  uid=$(curl -s -H "X-Emby-Token: $TOK" "$BASE/Users" \
    | python3 -c "import json,sys
d=json.load(sys.stdin)
print(next((u['Id'] for u in d if u.get('Name')=='$name'),''))" 2>/dev/null)
  if [ -z "$uid" ]; then
    uid=$(curl -s -X POST -H "X-Emby-Token: $TOK" -H 'Content-Type: application/json' \
      -d "{\"Name\":\"$name\"}" "$BASE/Users/New" \
      | python3 -c "import json,sys; print(json.load(sys.stdin).get('Id',''))" 2>/dev/null)
    [ -n "$uid" ] || fail "could not create user $name"
    # Set the initial password (fresh user has an empty password).
    curl -s -o /dev/null -X POST -H "X-Emby-Token: $TOK" -H 'Content-Type: application/json' \
      -d "{\"Id\":\"$uid\",\"CurrentPw\":\"\",\"NewPw\":\"$pw\"}" \
      "$BASE/Users/$uid/Password"
  fi
  echo "$uid"
}

# Authenticate a user by name/password and echo their AccessToken.
auth_token() {
  local name=$1 pw=$2
  curl -s -X POST \
    -H "Content-Type: application/json" \
    -H "X-Emby-Authorization: MediaBrowser Client=\"phantom-rig\", Device=\"rig-$name\", DeviceId=\"rig-$name\", Version=\"1.0\"" \
    -d "{\"Username\":\"$name\",\"Pw\":\"$pw\"}" \
    "$BASE/Users/AuthenticateByName" \
    | python3 -c "import json,sys; print(json.load(sys.stdin).get('AccessToken',''))" 2>/dev/null
}

# Resolve a channel's GUID by display name.
channel_id() {
  local name=$1
  curl -s -H "X-Emby-Token: $TOK" "$BASE/Channels" \
    | python3 -c "import json,sys
d=json.load(sys.stdin)
print(next((c['Id'] for c in d.get('Items',[]) if c.get('Name')=='$name'),''))"
}

# 1 if the given tmdb id is present in that channel for the user token, else 0.
# Matches on ProviderIds.Tmdb, so it is robust to display-name changes.
browse_has_tmdb() {
  local tok=$1 chan=$2 tmdb=$3 folder=${4:-}
  local url="$BASE/Channels/$chan/Items?Limit=200"
  [ -n "$folder" ] && url="$url&FolderId=$folder"
  curl -s -H "X-Emby-Token: $tok" "$url" \
    | python3 -c "import json,sys
d=json.load(sys.stdin)
def tid(it):
    p=it.get('ProviderIds') or {}
    return str(p.get('Tmdb') or p.get('tmdb') or '')
print('1' if any(tid(it)=='$tmdb' for it in d.get('Items',[])) else '0')"
}

# Echo the channel item Id whose ProviderIds.Tmdb matches (for folder drill).
browse_item_id() {
  local tok=$1 chan=$2 tmdb=$3
  curl -s -H "X-Emby-Token: $tok" "$BASE/Channels/$chan/Items?Limit=200" \
    | python3 -c "import json,sys
d=json.load(sys.stdin)
def tid(it):
    p=it.get('ProviderIds') or {}
    return str(p.get('Tmdb') or p.get('tmdb') or '')
print(next((it['Id'] for it in d.get('Items',[]) if tid(it)=='$tmdb'),''))"
}

# GET User/Hidden/{type}/{tmdb} for a user token -> 'true'/'false'.
hidden_state() {
  local tok=$1 type=$2 tmdb=$3
  curl -s -H "X-Emby-Token: $tok" \
    "$BASE/Plugins/PhantomLibrary/User/Hidden/$type/$tmdb" \
    | python3 -c "import json,sys
d=json.load(sys.stdin)
print(str(d.get('hidden')).lower())" 2>/dev/null
}

hide_title()   { curl -s -o /dev/null -w '%{http_code}' -X POST   -H "X-Emby-Token: $1" "$BASE/Plugins/PhantomLibrary/User/Hidden/$2/$3"; }
unhide_title() { curl -s -o /dev/null -w '%{http_code}' -X DELETE -H "X-Emby-Token: $1" "$BASE/Plugins/PhantomLibrary/User/Hidden/$2/$3"; }

# ---------------------------------------------------------------- users
echo
echo "=== provision two non-admin users ==="
UID_A=$(ensure_user "$USER_A" "$PW_A"); [ -n "$UID_A" ] || fail "no GUID for $USER_A"
UID_B=$(ensure_user "$USER_B" "$PW_B"); [ -n "$UID_B" ] || fail "no GUID for $USER_B"
echo "  A=$USER_A ($UID_A)"
echo "  B=$USER_B ($UID_B)"

TOK_A=$(auth_token "$USER_A" "$PW_A"); [ -n "$TOK_A" ] || fail "auth failed for $USER_A"
TOK_B=$(auth_token "$USER_B" "$PW_B"); [ -n "$TOK_B" ] || fail "auth failed for $USER_B"
echo "  authenticated A and B (per-user access tokens obtained)"

# Sanity: the acting user resolves from the token's claim, not a param.
who_a=$(curl -s -H "X-Emby-Token: $TOK_A" "$BASE/Plugins/PhantomLibrary/User/Prefs" -o /dev/null -w '%{http_code}')
[ "$who_a" = "200" ] || fail "User/Prefs with A's token returned $who_a, expected 200 (claim resolution broken)"

# ---------------------------------------------------------------- warm channels
echo
echo "=== trigger DiscoveryRefreshTask so fixture titles appear in both channels ==="
TASKID=$(curl -s -H "X-Emby-Token: $TOK" "$BASE/ScheduledTasks" \
  | python3 -c "import json,sys; d=json.load(sys.stdin); print(next((t['Id'] for t in d if t.get('Key')=='$DISCOVERY_TASK_ID'),''))")
[ -n "$TASKID" ] || fail "DiscoveryRefreshTask not registered — plugin DI broken."
curl -s -o /dev/null -X POST -H "X-Emby-Token: $TOK" "$BASE/ScheduledTasks/Running/$TASKID"
discovery_ok=0
for i in $(seq 1 120); do
  state=$(curl -s -H "X-Emby-Token: $TOK" "$BASE/ScheduledTasks/$TASKID" \
    | python3 -c "import json,sys; print(json.load(sys.stdin).get('State','?'))")
  [ "$state" = "Idle" ] && { echo "  discovery completed after ${i}s"; discovery_ok=1; break; }
  sleep 1
done
# Fail loudly on a stuck task rather than silently proceeding to a confusing
# "fixture not visible" baseline failure 100+ lines later.
[ "$discovery_ok" = "1" ] || fail "DiscoveryRefreshTask did not return to Idle within 120s (last state=$state) — rig discovery is stuck; check the rig Jellyfin log."

CH_MOVIES=$(channel_id "Phantom Movies"); [ -n "$CH_MOVIES" ] || fail "Phantom Movies channel not registered"
CH_SHOWS=$(channel_id "Phantom Shows");   [ -n "$CH_SHOWS" ]  || fail "Phantom Shows channel not registered"
echo "  channels: movies=$CH_MOVIES shows=$CH_SHOWS"

# ================================================================ MOVIE
echo
echo "=== [MOVIE] per-user show/hide (tmdb $MOVIE_TMDB) ==="

[ "$(browse_has_tmdb "$TOK_A" "$CH_MOVIES" "$MOVIE_TMDB")" = "1" ] \
  || fail "baseline: movie $MOVIE_TMDB not visible to A (discovery/warm did not emit it)"
[ "$(browse_has_tmdb "$TOK_B" "$CH_MOVIES" "$MOVIE_TMDB")" = "1" ] \
  || fail "baseline: movie $MOVIE_TMDB not visible to B"
echo "  baseline: movie visible to BOTH A and B"

code=$(hide_title "$TOK_A" movie "$MOVIE_TMDB")
[ "$code" = "204" ] || fail "A hide movie returned $code, expected 204"

[ "$(hidden_state "$TOK_A" movie "$MOVIE_TMDB")" = "true" ]  || fail "after hide: A's hidden-state not true"
[ "$(hidden_state "$TOK_B" movie "$MOVIE_TMDB")" = "false" ] || fail "after A hide: B's hidden-state not false (leak!)"

rowsA=$(count "SELECT COUNT(*) FROM user_hidden_items WHERE user_id='$UID_A' AND tmdb_id=$MOVIE_TMDB AND type='movie';")
rowsB=$(count "SELECT COUNT(*) FROM user_hidden_items WHERE user_id='$UID_B' AND tmdb_id=$MOVIE_TMDB AND type='movie';")
[ "$rowsA" = "1" ] || fail "expected exactly A's user_hidden_items row, got $rowsA"
[ "$rowsB" = "0" ] || fail "B has a user_hidden_items row it never created ($rowsB) — per-user isolation broken"

[ "$(browse_has_tmdb "$TOK_A" "$CH_MOVIES" "$MOVIE_TMDB")" = "0" ] \
  || fail "after hide: movie STILL visible to A (browse not filtered / cache not invalidated)"
[ "$(browse_has_tmdb "$TOK_B" "$CH_MOVIES" "$MOVIE_TMDB")" = "1" ] \
  || fail "after A hide: movie no longer visible to B (cross-user cache contamination!)"
echo "  after A hides: hidden for A (browse + endpoint), still visible for B"

code=$(unhide_title "$TOK_A" movie "$MOVIE_TMDB")
[ "$code" = "204" ] || fail "A unhide movie returned $code, expected 204"
[ "$(hidden_state "$TOK_A" movie "$MOVIE_TMDB")" = "false" ] || fail "after unhide: A's hidden-state not false"
[ "$(browse_has_tmdb "$TOK_A" "$CH_MOVIES" "$MOVIE_TMDB")" = "1" ] \
  || fail "after unhide: movie did not return to A's browse"
rowsA=$(count "SELECT COUNT(*) FROM user_hidden_items WHERE user_id='$UID_A' AND tmdb_id=$MOVIE_TMDB AND type='movie';")
[ "$rowsA" = "0" ] || fail "after unhide: A's user_hidden_items row not removed ($rowsA)"
echo "  after A unhides: title returns to A; both false; row cleared"

# ================================================================ SERIES / EPISODE
echo
echo "=== [SERIES/EPISODE] per-user show/hide (tmdb $SERIES_TMDB) ==="

[ "$(browse_has_tmdb "$TOK_A" "$CH_SHOWS" "$SERIES_TMDB")" = "1" ] \
  || fail "baseline: series $SERIES_TMDB not visible to A"
[ "$(browse_has_tmdb "$TOK_B" "$CH_SHOWS" "$SERIES_TMDB")" = "1" ] \
  || fail "baseline: series $SERIES_TMDB not visible to B"
echo "  baseline: series visible to BOTH A and B"

# Drill into the series folder as B to reach the episode dimension: the series
# folder must expand to at least one child (season/episode) so we prove hiding
# the series removes the WHOLE subtree, not just the top tile.
SERIES_ITEM_B=$(browse_item_id "$TOK_B" "$CH_SHOWS" "$SERIES_TMDB")
[ -n "$SERIES_ITEM_B" ] || fail "could not resolve B's series channel-item id for folder drill"
children_b=$(curl -s -H "X-Emby-Token: $TOK_B" \
  "$BASE/Channels/$CH_SHOWS/Items?Limit=200&FolderId=$SERIES_ITEM_B" \
  | python3 -c "import json,sys; print(json.load(sys.stdin).get('TotalRecordCount',0))")
[ "${children_b:-0}" -ge 1 ] || fail "B: series folder expanded to $children_b children, expected >=1 (season/episode)"
echo "  B can drill series -> $children_b child folder/item(s) (season/episode dimension present)"

code=$(hide_title "$TOK_A" series "$SERIES_TMDB")
[ "$code" = "204" ] || fail "A hide series returned $code, expected 204"

[ "$(hidden_state "$TOK_A" series "$SERIES_TMDB")" = "true" ]  || fail "after hide: A's series hidden-state not true"
[ "$(hidden_state "$TOK_B" series "$SERIES_TMDB")" = "false" ] || fail "after A hide: B's series hidden-state not false (leak!)"

[ "$(browse_has_tmdb "$TOK_A" "$CH_SHOWS" "$SERIES_TMDB")" = "0" ] \
  || fail "after hide: series STILL visible to A"
[ "$(browse_has_tmdb "$TOK_B" "$CH_SHOWS" "$SERIES_TMDB")" = "1" ] \
  || fail "after A hide: series no longer visible to B (cross-user leak)"

# Episode dimension: with the series hidden for A, its subtree is unreachable
# for A (the hidden series short-circuits its season/episode browse), while B
# still sees the same >=1 children.
children_a=$(curl -s -H "X-Emby-Token: $TOK_A" \
  "$BASE/Channels/$CH_SHOWS/Items?Limit=200&FolderId=$SERIES_ITEM_B" \
  | python3 -c "import json,sys; print(json.load(sys.stdin).get('TotalRecordCount',0))")
[ "${children_a:-0}" = "0" ] || fail "A: hidden series still expands to $children_a child items (episode leak)"
children_b2=$(curl -s -H "X-Emby-Token: $TOK_B" \
  "$BASE/Channels/$CH_SHOWS/Items?Limit=200&FolderId=$SERIES_ITEM_B" \
  | python3 -c "import json,sys; print(json.load(sys.stdin).get('TotalRecordCount',0))")
[ "${children_b2:-0}" -ge 1 ] || fail "B: series subtree collapsed after A hid it ($children_b2), cross-user contamination"
echo "  after A hides series: subtree gone for A (episodes included), intact for B"

code=$(unhide_title "$TOK_A" series "$SERIES_TMDB")
[ "$code" = "204" ] || fail "A unhide series returned $code, expected 204"
[ "$(browse_has_tmdb "$TOK_A" "$CH_SHOWS" "$SERIES_TMDB")" = "1" ] \
  || fail "after unhide: series did not return to A's browse"
echo "  after A unhides series: returns to A"

# ================================================================ PREFS
echo
echo "=== [PREFS] per-user preference toggle end to end ==="

# Fresh users read all-on defaults.
defA=$(curl -s -H "X-Emby-Token: $TOK_A" "$BASE/Plugins/PhantomLibrary/User/Prefs" \
  | python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('protectFavourites'), d.get('showPhantoms'), d.get('allowEager'))")
[ "$defA" = "True True True" ] || fail "A default prefs=$defA, expected 'True True True'"
echo "  A default prefs: $defA"

# A turns protectFavourites off (the per-user favourite-protection input).
setcode=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "X-Emby-Token: $TOK_A" \
  -H 'Content-Type: application/json' \
  -d '{"protectFavourites":false,"showPhantoms":true,"allowEager":true}' \
  "$BASE/Plugins/PhantomLibrary/User/Prefs")
[ "$setcode" = "204" ] || fail "A set prefs returned $setcode, expected 204"

pfA=$(curl -s -H "X-Emby-Token: $TOK_A" "$BASE/Plugins/PhantomLibrary/User/Prefs" \
  | python3 -c "import json,sys; print(json.load(sys.stdin).get('protectFavourites'))")
[ "$pfA" = "False" ] || fail "A protectFavourites=$pfA after set, expected False"

pfB=$(curl -s -H "X-Emby-Token: $TOK_B" "$BASE/Plugins/PhantomLibrary/User/Prefs" \
  | python3 -c "import json,sys; print(json.load(sys.stdin).get('protectFavourites'))")
[ "$pfB" = "True" ] || fail "B protectFavourites=$pfB, expected True (A's change leaked into B)"

# DB: exactly A's row, with protect_favourites=0; B has no row (reads defaults).
dbA=$(count "SELECT protect_favourites FROM user_prefs WHERE user_id='$UID_A';")
dbB=$(count "SELECT COUNT(*) FROM user_prefs WHERE user_id='$UID_B';")
[ "$dbA" = "0" ] || fail "user_prefs A protect_favourites=$dbA, expected 0"
[ "$dbB" = "0" ] || fail "B has a user_prefs row ($dbB) it never wrote — isolation broken"
echo "  A protectFavourites=False; B still True; DB carries only A's row"

# --- per-user favourite protection (sweeper input) --------------------------
# The EvictionSweeper reads GetUserPrefsAsync(userId).ProtectFavourites for each
# favouriting user. We have just proved live that this per-user input is
# isolated: A=off, B=on(default). The sweeper's eviction DECISION has no
# on-demand HTTP trigger (cron hosted service, not IScheduledTask), so its
# matrix is covered by EvictionSweeperTests — see the header + change doc.
echo "  per-user favourite-protection input verified isolated live (A off, B on);"
echo "  sweeper decision matrix covered by EvictionSweeperTests (no live trigger)."

# Restore A's prefs to defaults so a re-run starts clean.
curl -s -o /dev/null -X POST -H "X-Emby-Token: $TOK_A" -H 'Content-Type: application/json' \
  -d '{"protectFavourites":true,"showPhantoms":true,"allowEager":true}' \
  "$BASE/Plugins/PhantomLibrary/User/Prefs"

echo
echo "=== PASS: REQ-M14-PER-USER per-user show/hide + prefs + favourite-protection (movie + series/episode) ==="
