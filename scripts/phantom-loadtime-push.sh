#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/phantom-loadtime-push.sh
#
# ROI Priority 8, item 2 — the load-time EMITTER. One-shot batch push of every
# ROI-named channel load-time flow's record into Grafana Mimir, via the flux
# monitoring stack's Prometheus Pushgateway (already DEPLOYED; this script only
# EMITS into it — it never deploys, schedules, or measures anything itself).
#
# Mirrors the flux `scripts/coldstart-bench.sh` push contract: a PUT to the
# Pushgateway `/metrics/job/<job>` endpoint replaces the whole group in one
# payload (so a later push never silently leaves a stale half), the endpoint
# is env-parameterized with the same cluster-internal default that
# coldstart-bench.sh bakes (never a NEW infra identifier — same precedent,
# same value), and `--dry-run` prints the PUT target + payload instead of
# curling, so the in-repo regression harness
# (scripts/tests/p8-loadtime-push.test.sh) can assert the contract offline.
#
# INPUT: the exposition text emitted by the load-time MEASUREMENT ENGINE
# (tools/rig-scenarios/47-loadtime-flows.sh) —
#   phantom_loadtime_seconds{flow=…,item_type=movie|episode,color=…}   <secs>
#   phantom_loadtime_runs_total{flow=…,item_type=…,color=…}            <runs>
#   phantom_loadtime_errors_total{flow=…,item_type=…,color=…}          <errors>
# plus, in LIVE engine mode, the rig-outcome family
#   phantom_loadtime_rig_outcome_total{flow=…,item_type=…,cause=…}         1
# read from a FILE (arg or PHANTOM_LOADTIME_RECORDS env) or, absent both, from
# stdin. Legitimate payloads are passed through to the Pushgateway verbatim
# (never re-derived or reshaped).
#
# RESERVED-FAMILY GUARD (playback-outcome-synthetic-push-guard-001): the
# engine's offline fixture (PHANTOM_CI_DRYRUN=1) still emits SYNTHETIC
# `phantom_playback_outcome_total` samples. That family name is reserved for
# REAL playback outcomes, so a LIVE (non-dry-run) push REFUSES the whole
# payload if it carries any sample of exactly that family — whatever the input
# source (file arg, PHANTOM_LOADTIME_RECORDS, stdin, engine output) and
# whatever PHANTOM_LOADTIME_PUSH_JOB says. Nothing is stripped; the push fails.
# Comments (# HELP/# TYPE) and similar names (e.g. the rig family) are not
# samples and do not trigger it. Dry-run output is unaffected.
#
# DRY-RUN CONTROLS ARE INDEPENDENT: PHANTOM_CI_DRYRUN=1 makes the ENGINE
# synthetic; it does NOT make the PUSH offline. `run` therefore refuses
# PHANTOM_CI_DRYRUN=1 unless push dry-run (--dry-run or
# PHANTOM_LOADTIME_PUSH_DRY_RUN=1) is also explicitly enabled — checked before
# the engine runs. This guard does not prove provenance of arbitrary file/stdin
# data that lacks reserved samples.
#
# GROUPING KEY: PUT'd under Pushgateway job="phantom-loadtime" (no `instance`
# grouping label) — the per-flow identity (flow/item_type/color) lives as
# METRIC labels in the exposition text itself, not as Pushgateway grouping-key
# labels, so Prometheus's scrape config for the pushgateway job must set
# `honor_labels: true` (documented here, not baked into this script) — that
# tells Prometheus to keep the metrics' OWN flow/item_type/color labels rather
# than overwrite them with the scrape target's `instance`/`job` labels. This
# script's own job here is only the PUT; the scrape-config `honor_labels`
# setting lives in the flux Prometheus config repo, per the infra-identifier
# rule (no cluster config baked into submodule code).
#
# SUBCOMMANDS:
#   push [file]   PUT the exposition (from <file>, PHANTOM_LOADTIME_RECORDS,
#                 or stdin) to the Pushgateway.
#   run           Run the measurement engine
#                 (tools/rig-scenarios/47-loadtime-flows.sh) and push its
#                 output in one step. Any PHANTOM_LOADTIME_* env the caller
#                 has set (API/TOKEN/COLOR/etc) passes through to the engine
#                 unchanged; PHANTOM_CI_DRYRUN=1 additionally requires an
#                 explicit push dry-run (see RESERVED-FAMILY GUARD).
#
# Knobs (env):
#   PHANTOM_PUSHGATEWAY_URL      Pushgateway base URL. REQUIRED for a live
#                                (non-dry-run) push — supplied by the CI-deploy
#                                config / store->k8s Secret, never baked into
#                                this tracked script (infra-identifier rule).
#                                Unlike flux's scripts/coldstart-bench.sh
#                                (which bakes its cluster-internal default
#                                verbatim), this emitter's own task DoD +
#                                sibling p8-loadtime-flows harness explicitly
#                                grep-refuse a baked
#                                pushgateway/mimir/svc.cluster.local host in
#                                THIS submodule's tracked code, so no default
#                                is baked here — a `--dry-run` run may omit it
#                                (falls back to a neutral placeholder for
#                                offline contract testing only; never used to
#                                push for real).
#   PHANTOM_LOADTIME_PUSH_JOB    Pushgateway group job label (default
#                                "phantom-loadtime" — a metric-naming
#                                convention, not an infra identifier).
#   PHANTOM_LOADTIME_RECORDS     path to a file of exposition text (used by
#                                `push` when no file arg is given).
#   PHANTOM_LOADTIME_PUSH_DRY_RUN / --dry-run
#                                print the PUT target + payload instead of
#                                curling (no network; also the only mode that
#                                may run without PHANTOM_PUSHGATEWAY_URL set).
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENGINE="$HERE/../tools/rig-scenarios/47-loadtime-flows.sh"

PUSHGATEWAY="${PHANTOM_PUSHGATEWAY_URL:-}"
PUSH_JOB="${PHANTOM_LOADTIME_PUSH_JOB:-phantom-loadtime}"
RECORDS_FILE="${PHANTOM_LOADTIME_RECORDS:-}"
DRY_RUN="${PHANTOM_LOADTIME_PUSH_DRY_RUN:-0}"
# Placeholder target for --dry-run ONLY, when the caller has not set
# PHANTOM_PUSHGATEWAY_URL (e.g. exercising the push CONTRACT offline). RFC
# 2606 reserved domain — never a real/cluster hostname, and never used for an
# actual curl (a live push without the env set is refused below).
DRY_RUN_PLACEHOLDER="http://pushgateway.example.com:9091"

log()  { printf 'phantom-loadtime-push: %s\n' "$*" >&2; }
die()  { printf 'phantom-loadtime-push: ERROR: %s\n' "$*" >&2; exit 1; }

# --- read the exposition text: <file arg> > PHANTOM_LOADTIME_RECORDS > stdin --
read_records() {
    local file="${1:-}"
    if [ -n "$file" ]; then
        [ -e "$file" ] || die "records file not found: $file"
        cat "$file"
    elif [ -n "$RECORDS_FILE" ]; then
        [ -f "$RECORDS_FILE" ] || die "PHANTOM_LOADTIME_RECORDS file not found: $RECORDS_FILE"
        cat "$RECORDS_FILE"
    else
        cat -
    fi
}

# --- validate the exposition is non-empty and carries the expected metric ----
# family names, so a truncated/garbled harness run is refused rather than
# silently pushing an empty or malformed batch.
validate_records() {
    local payload="$1"
    [ -n "$payload" ] || die "no exposition records given (empty input)"
    printf '%s\n' "$payload" | grep -q '^phantom_loadtime_seconds{' \
        || die "exposition carries no phantom_loadtime_seconds record — refusing to push"
}

# --- refuse a LIVE push carrying a sample of the reserved real-playback family.
# A sample line is the exact metric name followed by `{` or whitespace (after
# optional leading whitespace); comment lines and longer names never match.
RESERVED_FAMILY="phantom_playback_outcome_total"
reject_reserved_samples() {
    local payload="$1"
    if printf '%s\n' "$payload" | grep -Eq "^[[:space:]]*${RESERVED_FAMILY}([{[:space:]]|\$)"; then
        die "payload carries reserved ${RESERVED_FAMILY} samples (synthetic/offline fixture data) — refusing the whole live push to job=${PUSH_JOB}; use --dry-run / PHANTOM_LOADTIME_PUSH_DRY_RUN=1 for fixture output"
    fi
}

# --- PUT the exposition to the Pushgateway group job=<PUSH_JOB>. PUT replaces
# the whole group so a later batch never leaves a stale prior flow behind.
push() {
    local file="${1:-}" payload url
    payload="$(read_records "$file")"
    validate_records "$payload"
    if [ -z "$PUSHGATEWAY" ]; then
        if [ "$DRY_RUN" = "1" ]; then
            PUSHGATEWAY="$DRY_RUN_PLACEHOLDER"
        else
            die "PHANTOM_PUSHGATEWAY_URL is not set — the Pushgateway target must come from CI-deploy config / a store->k8s Secret, never a baked default (infra-identifier rule); set it or use --dry-run to test the contract offline"
        fi
    fi
    url="${PUSHGATEWAY%/}/metrics/job/${PUSH_JOB}"
    if [ "$DRY_RUN" = "1" ]; then
        echo "PUT ${url}"
        printf '%s\n' "$payload"
        return 0
    fi
    reject_reserved_samples "$payload"
    log "pushing load-time metrics to ${url}"
    printf '%s\n' "$payload" | curl -sf --max-time 30 --data-binary @- -X PUT "$url" \
        || die "push to Pushgateway failed: $url"
    log "pushed load-time batch to job=${PUSH_JOB}"
}

# --- run the measurement engine, then push its output in one step -----------
run() {
    local payload
    if [ "${PHANTOM_CI_DRYRUN:-0}" = "1" ] && [ "$DRY_RUN" != "1" ]; then
        die "PHANTOM_CI_DRYRUN=1 produces a SYNTHETIC engine run, but push dry-run is disabled — refusing to send synthetic records to the Pushgateway; add --dry-run (or PHANTOM_LOADTIME_PUSH_DRY_RUN=1) for offline fixture output, or unset PHANTOM_CI_DRYRUN for a live measurement"
    fi
    payload="$(bash "$ENGINE")" || die "measurement engine failed"
    push <(printf '%s\n' "$payload")
}

usage() {
    cat >&2 <<EOF
usage: phantom-loadtime-push.sh <subcommand> [--dry-run] [file]
  push [file]   PUT the exposition (from <file>, PHANTOM_LOADTIME_RECORDS, or
                stdin) to the Pushgateway.
  run           run the measurement engine (47-loadtime-flows.sh) and push
                its output in one step.
EOF
    exit 2
}

main() {
    local args=()
    local a
    for a in "$@"; do
        case "$a" in
            --dry-run) DRY_RUN=1 ;;
            *) args+=("$a") ;;
        esac
    done
    set -- "${args[@]:-}"
    local cmd="${1:-}"; shift || true
    case "$cmd" in
        push) push "${1:-}" ;;
        run)  run ;;
        *)    usage ;;
    esac
}

main "$@"
