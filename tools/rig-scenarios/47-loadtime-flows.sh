#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# tools/rig-scenarios/47-loadtime-flows.sh
#
# ROI Priority 8, item 1 — the load-time MEASUREMENT ENGINE.
#
# TIMES each of the six ROI-named channel load-time flows against the deployed
# Jellyfin stack and emits each duration as a stable, machine-readable
# Prometheus text-exposition record that `p8-mimir-pushgateway-emit`
# (scripts/phantom-loadtime-push.sh) consumes and PUSHes to the flux
# Pushgateway. This script is the MEASUREMENT ENGINE ONLY: it never touches a
# metrics endpoint, never bakes a Mimir/Pushgateway host, and never schedules
# itself — the sink is p8-mimir-pushgateway-emit and the schedule is
# p8-daily-schedule-job.
#
# The six flows (each timed for a movie AND, where applicable, an episode):
#   1. list_load        — open a library/channel list view.
#   2. sort_change      — change the sort order on a list.
#   3. info_open        — open an item's info/details page.
#   4. get_sources      — get sources for an item (source/candidate probe).
#   5. materialise      — materialise a phantom            (PRIORITY signal).
#   6. play_materialised— start playback of a materialised item (PRIORITY).
# Flows 5 and 6 are the priority signals (the ratchet hits them hardest); both
# are captured for a movie AND an episode for movie/TV parity.
#
# It REUSES the P3 Stage-5 in-cluster rig harness: the same live-vs-dryrun
# posture, color resolution, prod-safety refusal, and API endpoints proven by
# tools/ci/in-cluster-acceptance-run.sh + the 35/36 e2e scenarios. Against the
# live rig it drives the real channel/native-open flow on :18096 (never prod
# :8096); with PHANTOM_CI_DRYRUN=1 it emits a deterministic synthetic fixture
# (no cluster/network access) so the in-repo regression harness
# scripts/tests/p8-loadtime-flows.test.sh can assert record well-formedness.
#
# EMITTED RECORD FORMAT (the contract p8-mimir-pushgateway-emit consumes) —
# Prometheus text-exposition, durations in SECONDS:
#   phantom_loadtime_seconds{flow="<flow>",item_type="<movie|episode>",color="<c>"} <secs>
#   phantom_loadtime_runs_total{flow=…,item_type=…,color=…}    <run-count>
#   phantom_loadtime_errors_total{flow=…,item_type=…,color=…}  <error-count>
# (a success record carries errors_total 0; a failed flow — materialise most
# often fails today per P6 — still emits a duration record for the attempt AND
# errors_total 1, so the failure RATE is recorded, never silently dropped.)
#
# Output goes to stdout AND, if PHANTOM_LOADTIME_OUT is set, to that file.
# rig `:18096`, never prod; trap-clean.
#
# Knobs (env), mirroring tools/ci/in-cluster-acceptance-run.sh:
#   PHANTOM_LOADTIME_API        base URL of the rig Jellyfin (default the rig
#                               :18096; NEVER prod :8096).
#   PHANTOM_LOADTIME_TOKEN      X-Emby-Token for the rig.
#   PHANTOM_LOADTIME_COLOR      color label for the records (default resolved,
#                               or "rig" in a dry run).
#   PHANTOM_LOADTIME_OUT        also write the exposition text to this path.
#   PHANTOM_CI_DRYRUN=1         emit the deterministic synthetic fixture
#                               instead of driving a live rig (no network).
#   PHANTOM_LOADTIME_FORCE_MATERIALISE_FAIL=1
#                               (dry run) force the materialise flow's error
#                               marker set, proving the failure path is
#                               recorded (used by the regression harness).
# Exit non-zero on a harness/protocol failure (NOT on a per-flow flow error —
# a flow error is recorded in the record, the run still succeeds).
# ---------------------------------------------------------------------------
set -euo pipefail

DRYRUN="${PHANTOM_CI_DRYRUN:-0}"
API="${PHANTOM_LOADTIME_API:-http://localhost:18096}"
TOK="${PHANTOM_LOADTIME_TOKEN:-testtoken00000000000000000000000}"
COLOR="${PHANTOM_LOADTIME_COLOR:-}"
OUT="${PHANTOM_LOADTIME_OUT:-}"

# The canonical flow-label vocabulary the emitter/ratchet/dashboard key on.
# (Order matters only for readability; the labels are the contract.)
FLOWS=(list_load sort_change info_open get_sources materialise play_materialised)

log()  { printf '# %s\n' "$*" >&2; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

# --- prod-safety guard: refuse to measure against the production port -------
# The rig is :18096; production owns :8096. A record captured against prod
# would poison the ratchet with prod-shaped numbers, so refuse outright.
case "$API" in
    *:8096|*:8096/*) fail "PHANTOM_LOADTIME_API points at :8096 (production) — refusing; the rig is :18096." ;;
esac

# --- portable high-resolution wall clock (seconds, float) -------------------
now_s() {
    # date +%s.%N is GNU-specific; fall back to python3 for portability.
    if date +%s.%N >/dev/null 2>&1 && [ "$(date +%N)" != "N" ]; then
        date +%s.%N
    else
        python3 -c 'import time; print(f"{time.time():.6f}")'
    fi
}
# elapsed <start> -> seconds with 6 decimals
elapsed() {
    python3 -c 'import sys; print(f"{max(0.0, float(sys.argv[2]) - float(sys.argv[1])):.6f}")' "$1" "$(now_s)"
}

# --- record emission --------------------------------------------------------
# emit_record <flow> <item_type> <duration_s> <runs> <errors>
_records=""
emit_record() {
    local flow="$1" item_type="$2" dur="$3" runs="$4" errors="$5"
    local block
    block="$(cat <<EOF
phantom_loadtime_seconds{flow="$flow",item_type="$item_type",color="$COLOR"} $dur
phantom_loadtime_runs_total{flow="$flow",item_type="$item_type",color="$COLOR"} $runs
phantom_loadtime_errors_total{flow="$flow",item_type="$item_type",color="$COLOR"} $errors
EOF
)"
    _records="${_records}${block}
"
}

flush_records() {
    local header
    header="$(cat <<'EOF'
# HELP phantom_loadtime_seconds Wall-clock duration of a phantom-library channel load-time flow, in seconds.
# TYPE phantom_loadtime_seconds gauge
# HELP phantom_loadtime_runs_total Number of runs of a flow in this measurement batch.
# TYPE phantom_loadtime_runs_total counter
# HELP phantom_loadtime_errors_total Number of runs of a flow that returned an error in this batch.
# TYPE phantom_loadtime_errors_total counter
EOF
)"
    printf '%s\n%s' "$header" "$_records"
    if [ -n "$OUT" ]; then
        { printf '%s\n%s' "$header" "$_records"; } > "$OUT"
        log "wrote exposition to $OUT"
    fi
}

# ===========================================================================
# DRY RUN — deterministic synthetic fixture, no network. Emits all six flows
# for movie AND episode with plausible seconds durations, and (when asked)
# a forced materialise error so the failure path is provably recorded.
# ===========================================================================
if [ "$DRYRUN" = 1 ]; then
    [ -n "$COLOR" ] || COLOR="rig"
    log "DRYRUN synthetic load-time fixture (color=$COLOR); no cluster/network access"
    # deterministic fixture seconds per (flow,item_type) — plausible, distinct.
    declare -A DUR_MOVIE=(
        [list_load]=0.180000 [sort_change]=0.090000 [info_open]=0.140000
        [get_sources]=0.320000 [materialise]=4.500000 [play_materialised]=1.250000
    )
    declare -A DUR_EPISODE=(
        [list_load]=0.210000 [sort_change]=0.110000 [info_open]=0.160000
        [get_sources]=0.350000 [materialise]=5.100000 [play_materialised]=1.400000
    )
    force_mat_fail="${PHANTOM_LOADTIME_FORCE_MATERIALISE_FAIL:-0}"
    for it in movie episode; do
        for flow in "${FLOWS[@]}"; do
            if [ "$it" = movie ]; then dur="${DUR_MOVIE[$flow]}"; else dur="${DUR_EPISODE[$flow]}"; fi
            errors=0
            if [ "$flow" = materialise ] && [ "$force_mat_fail" = 1 ]; then
                errors=1
            fi
            emit_record "$flow" "$it" "$dur" 1 "$errors"
        done
    done
    flush_records
    exit 0
fi

# ===========================================================================
# LIVE — drive the real rig on :18096 and TIME each flow. Reuses the same
# endpoints exercised by tools/rig-scenarios/35 & 36 and the Stage-5 harness.
# ===========================================================================
[ -n "$COLOR" ] || COLOR="rig"
log "live load-time measurement against $API (color=$COLOR); rig :18096, never prod"

api()      { curl -sS --fail -H "X-Emby-Token: $TOK" "$@"; }
json_post(){ curl -sS --fail -X POST -H "X-Emby-Token: $TOK" -H 'Content-Type: application/json' "$@"; }
hyphen()   { python3 - "$1" <<'PY'
import sys
s=sys.argv[1]; print(f'{s[:8]}-{s[8:12]}-{s[12:16]}-{s[16:20]}-{s[20:]}')
PY
}

# resolve the two channels once
CH_JSON="$(api "$API/Channels" || true)"
CH_MOVIES="$(printf '%s' "$CH_JSON" | python3 -c "import json,sys; d=json.load(sys.stdin); print(next((c['Id'] for c in d.get('Items',[]) if c.get('Name')=='Phantom Movies'),''))" 2>/dev/null || true)"
CH_SHOWS="$(printf '%s' "$CH_JSON" | python3 -c "import json,sys; d=json.load(sys.stdin); print(next((c['Id'] for c in d.get('Items',[]) if c.get('Name')=='Phantom Shows'),''))" 2>/dev/null || true)"
[ -n "$CH_MOVIES" ] || fail "Phantom Movies channel not registered on $API"
[ -n "$CH_SHOWS" ]  || fail "Phantom Shows channel not registered on $API"

# time_flow <flow> <item_type> <curl-cmd...>
# Runs the command, times it, and emits a record; a non-zero command marks the
# error but never aborts the batch (a flow error is DATA, not a harness fault).
time_flow() {
    local flow="$1" item_type="$2"; shift 2
    local start errors=0 dur
    start="$(now_s)"
    if "$@" >/dev/null 2>&1; then errors=0; else errors=1; fi
    dur="$(elapsed "$start")"
    emit_record "$flow" "$item_type" "$dur" 1 "$errors"
    log "flow=$flow item_type=$item_type duration_s=$dur errors=$errors"
}

# --- pick a movie + an episode to drive -------------------------------------
api "$API/Channels/$CH_MOVIES/Items?Limit=1&Fields=ProviderIds" -o /tmp/p8-movies.$$.json || fail "movie list fetch failed"
MOVIE_ID="$(python3 -c "import json; d=json.load(open('/tmp/p8-movies.$$.json')); print(d['Items'][0]['Id'])" 2>/dev/null || true)"
[ -n "$MOVIE_ID" ] || fail "could not resolve a movie item from Phantom Movies"
SERIES_ID="$(api "$API/Channels/$CH_SHOWS/Items?Limit=1" | python3 -c "import json,sys; print(json.load(sys.stdin)['Items'][0]['Id'])" 2>/dev/null || true)"
[ -n "$SERIES_ID" ] || fail "could not resolve a series item from Phantom Shows"
EPISODE_ID="$(api "$API/Channels/$CH_SHOWS/Items?Limit=1&FolderId=$SERIES_ID" | python3 -c "import json,sys; its=json.load(sys.stdin).get('Items',[]); print(its[0]['Id'] if its else '')" 2>/dev/null || true)"
[ -n "$EPISODE_ID" ] || EPISODE_ID="$SERIES_ID"   # drill fallback: time against the series container

trap 'rm -f /tmp/p8-movies.$$.json /tmp/p8-pb.$$.json' EXIT

# --- the six flows, movie + episode -----------------------------------------
for spec in "movie:$CH_MOVIES:$MOVIE_ID" "episode:$CH_SHOWS:$EPISODE_ID"; do
    IFS=: read -r it ch id <<<"$spec"
    time_flow list_load        "$it" api "$API/Channels/$ch/Items?Limit=50"
    time_flow sort_change      "$it" api "$API/Channels/$ch/Items?Limit=50&SortBy=SortName&SortOrder=Descending"
    time_flow info_open        "$it" api "$API/Items/$id"
    time_flow get_sources      "$it" api "$API/Items/$id/PlaybackInfo"
    gid="$(hyphen "$id")"
    time_flow materialise      "$it" json_post -d '{"AutoOpenLiveStream":true}' "$API/Items/$gid/PlaybackInfo?AutoOpenLiveStream=true"
    time_flow play_materialised "$it" curl -sS --fail -L --max-time 30 -H "X-Emby-Token: $TOK" -H 'Range: bytes=0-4095' -o /dev/null "$API/Videos/$gid/stream.mkv?static=true"
done

flush_records
log "load-time measurement batch complete"
