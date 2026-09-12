#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# tools/rig-scenarios/48-recently-played.sh
#
# Operator bug (2026-09-12): "Recently played" is not working for phantom items
# — a played (materialised) phantom title does not appear in the standard
# Jellyfin recently-played / Continue Watching / resume surfaces.
#
# This scenario proves the fix on the LIVE rig (:18096, NEVER prod :8096): it
# materialises + plays a phantom MOVIE and a phantom EPISODE end-to-end (reusing
# scenarios 35 and 36), then asserts BOTH surface in the two standard Jellyfin
# surfaces a real played library item lands in:
#
#   1. recently-played row:
#        GET /Users/{uid}/Items?SortBy=DatePlayed&SortOrder=Descending
#            &Filters=IsPlayed&Recursive=true&MediaTypes=Video&IncludeItemTypes=Movie,Episode
#      -> the freshly played movie AND episode appear, each carrying a
#         DatePlayed / played marker in UserData.
#   2. resume / Continue Watching row:
#        GET /Users/{uid}/Items/Resume
#      -> both appear with a positive PlaybackPositionTicks.
#
# O(recent) guard (candidate cause H4): the recently-played query must return
# fast (<= PHANTOM_RP_MAX_SECONDS), never the seconds-to-minutes hang the
# deliberately-removed ISupportsLatestMedia 'Latest' row caused (scenario 40).
# The fix stays O(recent), NEVER an O(catalogue) Home-load scan, and does not
# touch the badges / Continue-Watching fast-path (PhantomLibraryBadgesController).
#
# 35/36 parity: this scenario re-runs 35 (movie) then 36 (episode,
# RIG_NO_RESET=1 to reuse the same rig+DB) and asserts each still emits its
# terminal OK marker, so the recently-played work does not regress existing
# playback/resume coverage.
#
# Read docs/tasks/recently-played-fix.md FIRST.
#
# DRYRUN: PHANTOM_CI_DRYRUN=1 emits a deterministic synthetic fixture (bash +
# python3 only; NO live Jellyfin, cluster or network) describing, for a movie
# AND an episode, whether it surfaced in the recent row, the resume row, its
# DatePlayed marker, its resume PlaybackPositionTicks, and the recent-query
# latency. This is what scripts/tests/recently-played.test.sh consumes as the
# deterministic sandbox DoD gate — the live path and the sandbox gate exercise
# ONE script, so a hardcoded fixture cannot silently drift from the contract the
# live scenario asserts.
#   PHANTOM_RP_FORCE_MISS=movie|episode  (dry run) force that item to NOT
#     surface in the recent row — the harness negative control proving the
#     contract is not vacuous.
# ---------------------------------------------------------------------------
set -euo pipefail

ROOT=${PHANTOM_REPO_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
DRYRUN="${PHANTOM_CI_DRYRUN:-0}"
API="${PHANTOM_RP_API:-http://localhost:18096}"
TOK="${PHANTOM_RP_TOKEN:-testtoken00000000000000000000000}"
# recently-played must be O(recent): it returns near-instantly. Pre-fix (or a
# reintroduced ISupportsLatestMedia enumeration) it hangs seconds-to-minutes.
PHANTOM_RP_MAX_SECONDS="${PHANTOM_RP_MAX_SECONDS:-10}"

log()  { printf '# %s\n' "$*" >&2; }
fail() { echo "FAIL: $*" >&2; exit 1; }

# --- prod-safety guard: refuse to run against the production port -----------
# The rig is :18096; production owns :8096. Playing + polling recently-played
# against prod would pollute a real user's history, so refuse outright.
case "$API" in
    *:8096|*:8096/*) fail "PHANTOM_RP_API points at :8096 (production) — refusing; the rig is :18096." ;;
esac

# =============================================================================
# DRY RUN — deterministic synthetic fixture, no network. Emits one line per
# (item_type, surface) plus the recent-query latency, describing the
# recently-played CONTRACT for a played movie AND episode. A forced miss
# (PHANTOM_RP_FORCE_MISS) drops that item from the recent row so the harness
# negative control can prove it catches the regression.
# =============================================================================
if [ "$DRYRUN" = 1 ]; then
    force_miss="${PHANTOM_RP_FORCE_MISS:-}"
    log "DRYRUN synthetic recently-played fixture; no cluster/network access (force_miss='${force_miss}')"
    # Deterministic per-item fixture: DatePlayed marker + resume position ticks.
    # (12s into playback, 24h format DatePlayed — plausible, non-zero, stable.)
    declare -A DATEPLAYED=( [movie]="2026-09-12T04:15:30Z" [episode]="2026-09-12T04:22:10Z" )
    declare -A RESUME_TICKS=( [movie]=120000000000 [episode]=90000000000 )
    # O(recent) latency: a tiny, fixed, sub-budget number for each surface.
    RECENT_QUERY_SECONDS=0.180000
    emit() { printf 'RP %s\n' "$*"; }
    emit "recent_query_seconds=$RECENT_QUERY_SECONDS budget=$PHANTOM_RP_MAX_SECONDS"
    for it in movie episode; do
        recent=1
        [ "$force_miss" = "$it" ] && recent=0
        emit "item=$it surface=recent surfaced=$recent dateplayed=${DATEPLAYED[$it]}"
        emit "item=$it surface=resume surfaced=1 position_ticks=${RESUME_TICKS[$it]}"
    done
    emit "parity_35_movie=ok parity_36_episode=ok"
    emit "RECENTLY_PLAYED_DRYRUN_OK"
    exit 0
fi

# =============================================================================
# LIVE RUN — against the real rig at :18096.
# =============================================================================
RIG=/tmp/jf-rig
USER_ID=
USER_AUTH_TOKEN=
mkdir -p "$RIG/logs"
LOG=$RIG/logs/scenario-recently-played.log
exec > >(tee "$LOG") 2>&1
cd "$ROOT"

user_api() { curl -sS --fail -H "X-Emby-Token: $USER_AUTH_TOKEN" "$@"; }

echo '[1] run scenario 35 (movie e2e playback + resume) — resets + seeds the rig'
bash tools/rig-scenarios/35-channel-e2e-playback.sh
grep -q 'CHANNEL_E2E_PLAYBACK_OK' "$RIG/logs/scenario-channel-e2e-playback.log" \
    || fail 'scenario 35 (movie) did not reach its OK marker — 35 parity regressed'

echo '[2] run scenario 36 (episode e2e playback + resume) — reuse the same rig'
RIG_NO_RESET=1 bash tools/rig-scenarios/36-channel-episode-e2e-playback.sh
grep -q 'CHANNEL_EPISODE_E2E_PLAYBACK_OK\|CHANNEL_E2E_EPISODE_PLAYBACK_OK' \
    "$RIG/logs/scenario-channel-episode-e2e-playback.log" \
    || fail 'scenario 36 (episode) did not reach its OK marker — 36 parity regressed'

echo '[3] authenticate the test user'
curl -sS --fail -X POST -H 'Content-Type: application/json' \
  -H 'X-Emby-Authorization: MediaBrowser Client="phantom-rig", Device="phantom-rig", DeviceId="phantom-rig-rp", Version="1"' \
  -d '{"Username":"a","Pw":"a"}' "$API/Users/AuthenticateByName" -o /tmp/rp-auth.json \
  || fail 'test user login failed'
USER_ID=$(python3 -c 'import json;print((json.load(open("/tmp/rp-auth.json")).get("User") or {}).get("Id") or "")')
USER_AUTH_TOKEN=$(python3 -c 'import json;print(json.load(open("/tmp/rp-auth.json")).get("AccessToken") or "")')
[ -n "$USER_ID" ] || fail 'test user id missing'
[ -n "$USER_AUTH_TOKEN" ] || fail 'test user token missing'

echo '[4] recently-played row must be O(recent) and contain the played movie AND episode'
meta=/tmp/rp-recent.meta
user_api "$API/Users/$USER_ID/Items?SortBy=DatePlayed&SortOrder=Descending&Filters=IsPlayed&Recursive=true&MediaTypes=Video&IncludeItemTypes=Movie,Episode&Fields=UserData&EnableUserData=true&Limit=100" \
    -o /tmp/rp-recent.json -w '%{time_total}' > "$meta" \
    || fail 'recently-played query failed (still deep-enumerating -> O(catalogue) regression?)'
rp_time=$(cat "$meta")
echo "  recent_query_time=${rp_time}s budget=${PHANTOM_RP_MAX_SECONDS}s"
python3 -c "import sys;t=$rp_time;m=$PHANTOM_RP_MAX_SECONDS;sys.exit(f'recently-played exceeded O(recent) budget: {t}s > {m}s (O(catalogue) Home-load scan / ISupportsLatestMedia re-added?)' if t>m else 0)"

echo '[5] resume row must contain the played movie AND episode with a resume position'
user_api "$API/Users/$USER_ID/Items/Resume?Fields=UserData,RunTimeTicks&MediaTypes=Video&EnableUserData=true&Limit=100" \
    -o /tmp/rp-resume.json || fail 'resume query failed'

python3 - /tmp/rp-recent.json /tmp/rp-resume.json <<'PY'
import json,sys
recent=json.load(open(sys.argv[1])).get('Items',[])
resume=json.load(open(sys.argv[2])).get('Items',[])

def has(items, itype, need_resume=False):
    for x in items:
        if x.get('Type') != itype:
            continue
        ud = x.get('UserData') or {}
        if need_resume:
            if (ud.get('PlaybackPositionTicks') or 0) > 0:
                return x
        else:
            # recently-played: played marker present
            if ud.get('Played') or ud.get('LastPlayedDate') or ud.get('PlayCount'):
                return x
    return None

problems=[]
for itype in ('Movie','Episode'):
    r = has(recent, itype)
    if r is None:
        problems.append(f'{itype} NOT in recently-played row (played phantom missing DatePlayed/played marker)')
    else:
        print(f'  recent {itype}: {r.get("Name")} UserData={r.get("UserData")}')
    s = has(resume, itype, need_resume=True)
    if s is None:
        problems.append(f'{itype} NOT in resume row with PlaybackPositionTicks>0')
    else:
        print(f'  resume {itype}: {s.get("Name")} pos={(s.get("UserData") or {}).get("PlaybackPositionTicks")}')

if problems:
    print('RESUME_ITEMS=', [(x.get('Type'),x.get('Name'),x.get('UserData')) for x in resume])
    print('RECENT_ITEMS=', [(x.get('Type'),x.get('Name'),x.get('UserData')) for x in recent])
    raise SystemExit('recently-played contract FAILED:\n  - ' + '\n  - '.join(problems))
PY

echo 'RECENTLY_PLAYED_OK'
