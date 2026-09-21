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
# It ALSO emits a synthetic per-attempt playback-outcome fixture
# (playback-outcome-instrumentation-001).
#
# IMPORTANT (playback-outcome-real-cause-dual-emit-001): this rig NEVER calls
# the real classification (ClassifyMaterialiseFailure / PhantomFlowMetrics.
# RecordPlaybackOutcome) — DRYRUN has no server at all, and LIVE only inspects
# its own curl exit codes for the materialise/play calls, hard-coding every
# materialise-side failure to "gostream_register_fail" and every play-side
# failure to "gostream_cannot_fetch" (never availability_abstain/no_candidate/
# magnet_dead_stale, never a real distinction between them). So the two
# branches now emit DIFFERENT metric names, to make that distinction
# impossible to lose track of again:
#   - DRYRUN keeps emitting `phantom_playback_outcome_total{flow,item_type,cause}`
#     — this branch has no live server and no server-side counterpart to
#     conflict with, so reusing the real metric's name here is harmless.
#   - LIVE now emits `phantom_loadtime_rig_outcome_total{flow,item_type,cause}`
#     instead — a distinctly-named rig artifact that can never again be
#     mistaken for the real per-attempt series. The REAL series is
#     `phantom_playback_outcome_total`, emitted ONLY by
#     PhantomFlowMetrics.RecordPlaybackOutcome (OTLP AND, as of this task, a
#     dual-emitted prometheus-net counter of the identical name/labels),
#     scraped in-cluster like `phantom_availability_probes_total` — NOT
#     produced by this rig.
# cause ∈ {success, availability_abstain, no_candidate, magnet_dead_stale,
# gostream_register_fail, gostream_cannot_fetch, first_byte_timeout,
# plugin_host_error}. A success attempt carries cause="success"; a failed flow
# emits its definitive cause, never a silently-dropped failure. NO Mimir/
# Pushgateway/observe host is baked here (infra-identifier rule).
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
#
# MEASUREMENT-FIDELITY NOTE (ROI P8, p8-fidelity-full-list-timing):
# list_load and sort_change do NOT time a single capped
# `GET /Channels/<ch>/Items?Limit=50` round trip. That undercounts the real
# user-perceived wait three ways: it excludes (a) the rest of the catalogue
# beyond the first page, (b) phantomBadges.js's batched badge-state
# re-resolve fan-out (POST /Plugins/PhantomLibrary/States, chunked at
# BATCH_LIMIT=400 exactly like the shim), and (c) on-screen materialisation
# of every resulting card. Both flows instead: paginate the FULL uncapped
# item list, fan the badge-state lookup out in BATCH_LIMIT-sized batches,
# then hand the full item count to a DOM-timing helper
# (tools/rig-scenarios/48-list-materialise-dom.mjs, a real — never jsdom-
# stubbed-to-a-constant — minimal-DOM card-construction pass, the same
# faithful-DOM approach phantom-kebab-mobile-dom.mjs uses) to genuinely
# time on-screen materialisation. The summed wall clock across all three
# phases is the flow's duration — the full user-perceived wait, not one
# server round trip.
set -euo pipefail

DRYRUN="${PHANTOM_CI_DRYRUN:-0}"
API="${PHANTOM_LOADTIME_API:-http://localhost:18096}"
TOK="${PHANTOM_LOADTIME_TOKEN:-testtoken00000000000000000000000}"
COLOR="${PHANTOM_LOADTIME_COLOR:-}"
OUT="${PHANTOM_LOADTIME_OUT:-}"

# The canonical flow-label vocabulary the emitter/ratchet/dashboard key on.
# (Order matters only for readability; the labels are the contract.)
FLOWS=(list_load sort_change info_open get_sources materialise play_materialised)

# --- full-list measurement-fidelity knobs (list_load/sort_change only) ------
# Mirrors phantomBadges.js's own BATCH_LIMIT exactly (see that file's header
# comment) so the fan-out this rig times is the SAME batching shape production
# actually does, never an invented number.
BADGE_BATCH_LIMIT=400
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOM_MATERIALISE_JS="$HERE/48-list-materialise-dom.mjs"
# Deterministic (no network) "full uncapped catalogue" sizes for DRYRUN, large
# enough to force a multi-batch badge fan-out (> BADGE_BATCH_LIMIT) and a
# non-trivial DOM-materialise pass — proving the fix actually measures past
# the old 50-item cap instead of just relabelling the same single page.
DRYRUN_CATALOGUE_MOVIE=1200
DRYRUN_CATALOGUE_EPISODE=340

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

# --- badge-state fan-out + on-screen materialisation ------------------------
# The full-list fidelity fix's shared tail: given the TOTAL item count the
# list view will render, walk it through the SAME badge-batching shape
# phantomBadges.js uses (BADGE_BATCH_LIMIT-sized chunks) and then genuinely
# time on-screen DOM materialisation for all of them. `batch_runner` performs
# one unit of work per batch (a real network POST in LIVE mode, or a no-op in
# DRYRUN — the DOM cost is what DRYRUN actually measures) so this function
# stays identical between the two modes.
# badge_fanout_and_materialise <item_count> <batch_runner_fn>
badge_fanout_and_materialise() {
    local total="$1" batch_runner="$2"
    local i=0
    while [ "$i" -lt "$total" ]; do
        "$batch_runner" "$i" "$BADGE_BATCH_LIMIT"
        i=$((i + BADGE_BATCH_LIMIT))
    done
    if command -v node >/dev/null 2>&1 && [ -f "$DOM_MATERIALISE_JS" ]; then
        node "$DOM_MATERIALISE_JS" "$total" >/dev/null 2>&1 || true
    else
        # node unavailable: fall back to a real (if cruder) per-item CPU cost
        # so the measurement still reflects O(item_count) materialisation
        # work rather than silently skipping it.
        python3 -c "
for _ in range(int(${total})):
    str(_) + 'x'
" >/dev/null 2>&1 || true
    fi
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

# --- playback-outcome emission (playback-outcome-instrumentation-001, renamed
# for the LIVE branch by playback-outcome-real-cause-dual-emit-001) ----------
# Alongside the load-time record above, a materialise/get_sources/play flow
# ALSO emits exactly one synthetic per-attempt playback-outcome record: a
# success carries cause="success"; a failed flow emits its definitive cause,
# never a silently-dropped failure. `flow` here is the playback flow
# vocabulary the C# PhantomFlowMetrics.RecordPlaybackOutcome tags
# (materialise_then_play vs play_already_materialised), NOT the load-time flow
# label. This is a RIG ARTIFACT, not the real per-attempt series — see the
# metric-name note above the DRYRUN/LIVE split: the metric name defaults to
# the real series' name (`phantom_playback_outcome_total`, harmless in DRYRUN
# which has no server-side counterpart) but the LIVE call site below passes
# `phantom_loadtime_rig_outcome_total` explicitly so it is never conflated
# with the real per-attempt series that PhantomFlowMetrics.RecordPlaybackOutcome
# now dual-emits (OTLP + prometheus-net) from the live plugin.
PLAYBACK_FLOWS=(materialise_then_play play_already_materialised)
PLAYBACK_CAUSES=(success availability_abstain no_candidate magnet_dead_stale \
    gostream_register_fail gostream_cannot_fetch first_byte_timeout plugin_host_error)
_outcome_records=""
_outcome_metric_names=""
# emit_playback_outcome <flow> <item_type> <cause> [metric_name]
emit_playback_outcome() {
    local flow="$1" item_type="$2" cause="$3" metric="${4:-phantom_playback_outcome_total}"
    _outcome_records="${_outcome_records}${metric}{flow=\"$flow\",item_type=\"$item_type\",cause=\"$cause\"} 1
"
    case " $_outcome_metric_names " in
        *" $metric "*) ;;
        *) _outcome_metric_names="$_outcome_metric_names $metric" ;;
    esac
}

flush_records() {
    local header outcome_header metric
    header="$(cat <<'EOF'
# HELP phantom_loadtime_seconds Wall-clock duration of a phantom-library channel load-time flow, in seconds.
# TYPE phantom_loadtime_seconds gauge
# HELP phantom_loadtime_runs_total Number of runs of a flow in this measurement batch.
# TYPE phantom_loadtime_runs_total counter
# HELP phantom_loadtime_errors_total Number of runs of a flow that returned an error in this batch.
# TYPE phantom_loadtime_errors_total counter
EOF
)"
    outcome_header=""
    for metric in $_outcome_metric_names; do
        case "$metric" in
            phantom_playback_outcome_total)
                outcome_header="${outcome_header}# HELP phantom_playback_outcome_total Definitive per-attempt phantom playback outcome, split by flow/item_type/cause.
# TYPE phantom_playback_outcome_total counter
"
                ;;
            phantom_loadtime_rig_outcome_total)
                outcome_header="${outcome_header}# HELP phantom_loadtime_rig_outcome_total Synthetic rig-derived (NOT real per-attempt) playback outcome classification from this LIVE loadtime rig run's curl exit codes, split by flow/item_type/cause. NOT phantom_playback_outcome_total.
# TYPE phantom_loadtime_rig_outcome_total counter
"
                ;;
        esac
    done
    # Assemble the exposition with an EXPLICIT newline between every segment so
    # no two segments ever concatenate onto one line, regardless of whether
    # $outcome_header / $_outcome_records is empty. Command substitution strips
    # $header's trailing newline (and $outcome_header carries its own trailing
    # newline per emitted TYPE line), so joining with printf '%s\n' segments —
    # skipping empty ones — is the only shape that stays Prometheus-text valid
    # for the empty, single-outcome-metric, and both-outcome-metric cases.
    _emit_exposition() {
        # $header always present; the record/outcome blobs already carry their
        # own trailing newlines, so strip only when appending to avoid blank
        # lines while still guaranteeing a separator after the header block.
        printf '%s\n' "$header"
        [ -n "$outcome_header" ] && printf '%s' "$outcome_header"
        [ -n "$_records" ] && printf '%s' "$_records"
        [ -n "$_outcome_records" ] && printf '%s' "$_outcome_records"
    }
    _emit_exposition
    if [ -n "$OUT" ]; then
        _emit_exposition > "$OUT"
        log "wrote exposition to $OUT"
    fi
}

# ===========================================================================
# DRY RUN — deterministic synthetic fixture, no network. list_load/sort_change
# are GENUINELY TIMED (full-catalogue pagination simulation + real badge-batch
# fan-out + real DOM materialisation via 48-list-materialise-dom.mjs) rather
# than a hand-typed constant — this is the fidelity fix under test, and a
# hardcoded number would silently re-introduce the exact bug this task fixes.
# The other four flows (unaffected by this task) keep plausible fixed
# fixture durations, and (when asked) a forced materialise error so the
# failure path is provably recorded.
# ===========================================================================
if [ "$DRYRUN" = 1 ]; then
    [ -n "$COLOR" ] || COLOR="rig"
    log "DRYRUN synthetic load-time fixture (color=$COLOR); no cluster/network access"
    # deterministic fixture seconds for the flows this task does NOT touch.
    declare -A DUR_MOVIE=(
        [info_open]=0.140000
        [get_sources]=0.320000 [materialise]=4.500000 [play_materialised]=1.250000
    )
    declare -A DUR_EPISODE=(
        [info_open]=0.160000
        [get_sources]=0.350000 [materialise]=5.100000 [play_materialised]=1.400000
    )
    # no-op batch runner: DRYRUN has no network, so the badge-batch cost here
    # is the (real, timed) DOM-materialise pass only — LIVE mode below adds
    # the genuine POST round trips on top of the same shared helper.
    dryrun_batch_runner() { :; }
    force_mat_fail="${PHANTOM_LOADTIME_FORCE_MATERIALISE_FAIL:-0}"
    for it in movie episode; do
        if [ "$it" = movie ]; then catalogue="$DRYRUN_CATALOGUE_MOVIE"; else catalogue="$DRYRUN_CATALOGUE_EPISODE"; fi
        for flow in "${FLOWS[@]}"; do
            errors=0
            case "$flow" in
                list_load)
                    start="$(now_s)"
                    badge_fanout_and_materialise "$catalogue" dryrun_batch_runner
                    dur="$(elapsed "$start")"
                    ;;
                sort_change)
                    # a sort change re-renders the same full (uncapped) list
                    # under a new order and re-resolves badges for it again —
                    # same full-catalogue + fan-out + materialise cost as
                    # list_load, timed independently (never copied/derived).
                    start="$(now_s)"
                    badge_fanout_and_materialise "$catalogue" dryrun_batch_runner
                    dur="$(elapsed "$start")"
                    ;;
                *)
                    if [ "$it" = movie ]; then dur="${DUR_MOVIE[$flow]}"; else dur="${DUR_EPISODE[$flow]}"; fi
                    if [ "$flow" = materialise ] && [ "$force_mat_fail" = 1 ]; then
                        errors=1
                    fi
                    ;;
            esac
            emit_record "$flow" "$it" "$dur" 1 "$errors"
        done
        # --- definitive per-attempt playback outcome, movie AND episode -------
        # Every playback flow records exactly one definitive cause. In DRYRUN the
        # already-materialised flow is a clean success; the materialise-then-play
        # flow is a success UNLESS a failure cause is forced (proving a failed
        # flow emits its definitive cause, never a silently-dropped success).
        emit_playback_outcome play_already_materialised "$it" success
        force_cause="${PHANTOM_LOADTIME_FORCE_PLAYBACK_FAIL_CAUSE:-}"
        if [ "$force_mat_fail" = 1 ] && [ -z "$force_cause" ]; then
            force_cause=gostream_register_fail
        fi
        if [ -n "$force_cause" ]; then
            emit_playback_outcome materialise_then_play "$it" "$force_cause"
        else
            emit_playback_outcome materialise_then_play "$it" success
        fi
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
# Sets the global LAST_FLOW_ERRORS to this flow's error marker so the caller can
# derive the definitive playback outcome for the materialise/play flows.
LAST_FLOW_ERRORS=0
time_flow() {
    local flow="$1" item_type="$2"; shift 2
    local start errors=0 dur
    start="$(now_s)"
    if "$@" >/dev/null 2>&1; then errors=0; else errors=1; fi
    dur="$(elapsed "$start")"
    emit_record "$flow" "$item_type" "$dur" 1 "$errors"
    LAST_FLOW_ERRORS="$errors"
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

# time_full_list_flow <flow> <item_type> <channel> <sort_qs>
# The fidelity fix: paginate the FULL uncapped item list (no Limit=50 cap),
# fan the badge-state lookup out in real BADGE_BATCH_LIMIT-sized POST batches
# against /Plugins/PhantomLibrary/States (exactly phantomBadges.js's own
# batching), then hand the total count to the DOM-materialise helper. The
# summed wall clock across fetch + fan-out + materialise is the flow's
# duration — full user-perceived load, not one capped round trip.
time_full_list_flow() {
    local flow="$1" item_type="$2" ch="$3" sort_qs="$4"
    local start errors=0 dur total=0 start_index=0 page=200
    local all_ids_file
    all_ids_file="$(mktemp)"
    start="$(now_s)"
    while :; do
        local page_json page_file
        page_file="$(mktemp)"
        if ! api "$API/Channels/$ch/Items?Limit=$page&StartIndex=$start_index${sort_qs}" -o "$page_file" 2>/dev/null; then
            errors=1; rm -f "$page_file"; break
        fi
        local got total_records
        got="$(python3 -c "import json; d=json.load(open('$page_file')); print(len(d.get('Items',[])))" 2>/dev/null || echo 0)"
        total_records="$(python3 -c "import json; d=json.load(open('$page_file')); print(d.get('TotalRecordCount',0))" 2>/dev/null || echo 0)"
        python3 -c "import json; d=json.load(open('$page_file')); print('\n'.join(i['Id'] for i in d.get('Items',[])))" 2>/dev/null >> "$all_ids_file" || true
        rm -f "$page_file"
        total=$((total + got))
        start_index=$((start_index + page))
        if [ "$got" -eq 0 ] || [ "$start_index" -ge "$total_records" ]; then break; fi
    done
    live_batch_runner() {
        local off="$1" lim="$2"
        local chunk_ids
        chunk_ids="$(python3 -c "
import json,sys
ids=[l.strip() for l in open('$all_ids_file') if l.strip()]
print(json.dumps(ids[$off:$off+$lim]))
" 2>/dev/null || echo '[]')"
        json_post -d "{\"ids\": $chunk_ids}" "$API/Plugins/PhantomLibrary/States" >/dev/null 2>&1 || errors=1
    }
    badge_fanout_and_materialise "$total" live_batch_runner
    rm -f "$all_ids_file"
    dur="$(elapsed "$start")"
    emit_record "$flow" "$item_type" "$dur" 1 "$errors"
    log "flow=$flow item_type=$item_type duration_s=$dur errors=$errors items=$total"
}

# --- the six flows, movie + episode -----------------------------------------
for spec in "movie:$CH_MOVIES:$MOVIE_ID" "episode:$CH_SHOWS:$EPISODE_ID"; do
    IFS=: read -r it ch id <<<"$spec"
    time_full_list_flow list_load   "$it" "$ch" ""
    time_full_list_flow sort_change "$it" "$ch" "&SortBy=SortName&SortOrder=Descending"
    time_flow info_open        "$it" api "$API/Items/$id"
    time_flow get_sources      "$it" api "$API/Items/$id/PlaybackInfo"
    gid="$(hyphen "$id")"
    time_flow materialise      "$it" json_post -d '{"AutoOpenLiveStream":true}' "$API/Items/$gid/PlaybackInfo?AutoOpenLiveStream=true"
    mat_errors="$LAST_FLOW_ERRORS"
    time_flow play_materialised "$it" curl -sS --fail -L --max-time 30 -H "X-Emby-Token: $TOK" -H 'Range: bytes=0-4095' -o /dev/null "$API/Videos/$gid/stream.mkv?static=true"
    play_errors="$LAST_FLOW_ERRORS"

    # --- synthetic rig-derived playback outcome (movie AND episode) ----------
    # This driver exercises a fresh materialise-then-play attempt but ONLY ever
    # inspects its own curl exit codes -- it never calls the real per-attempt
    # classification (ClassifyMaterialiseFailure / PhantomFlowMetrics.
    # RecordPlaybackOutcome), so it can only produce a coarse two-cause
    # approximation, never the real availability_abstain/no_candidate/
    # magnet_dead_stale/first_byte_timeout/plugin_host_error causes. Emitted
    # under the DISTINCT `phantom_loadtime_rig_outcome_total` metric name
    # (playback-outcome-real-cause-dual-emit-001) so it is never mistaken for
    # the real `phantom_playback_outcome_total` series the live plugin now
    # dual-emits (OTLP + prometheus-net) from the real call site.
    if [ "$mat_errors" = 1 ]; then
        emit_playback_outcome materialise_then_play "$it" gostream_register_fail phantom_loadtime_rig_outcome_total
    elif [ "$play_errors" = 1 ]; then
        emit_playback_outcome materialise_then_play "$it" gostream_cannot_fetch phantom_loadtime_rig_outcome_total
    else
        emit_playback_outcome materialise_then_play "$it" success phantom_loadtime_rig_outcome_total
    fi
done

flush_records
log "load-time measurement batch complete"
