#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# tools/ci/loadtime-daily-run.sh
#
# ROI Priority 8, item 3 — the DAILY schedule wiring. Ties together the three
# already-shipped P8 pieces into one cadence:
#   - the measurement engine  (tools/rig-scenarios/47-loadtime-flows.sh, P8/1)
#   - the Pushgateway emitter (scripts/phantom-loadtime-push.sh, P8/2)
#   - the ratcheting regression guard (tools/perf/loadtime-guard.sh, P8/5)
#
# Unlike tools/ci/live-rig-run.sh / tools/rig-scenarios' own default target
# (a throwaway rig Jellyfin on :18096), this script measures the ACTUAL
# **deployed** stack the user hits — the current dev/idle color's Jellyfin,
# reached over HTTPS at the deployed dev host (e.g.
# `dev.<tenant-domain>` — the real hostname is supplied by CI vars, never
# baked into this tracked script; see the infra-identifier rule in this
# repo's AGENTS.md). It reuses the exact P3 Stage-5 in-cluster rig harness's
# posture for resolving which color is "dev" LIVE from the Ingress (never a
# cached/CNAME guess) and its prod-safety refusal
# (tools/ci/in-cluster-acceptance-run.sh) — this is NOT a standalone rig
# instance.
#
# Flow:
#   1. resolve the dev color live from the Ingress (kubectl), refuse if the
#      configured dev host equals the prod host.
#   2. measure the six load-time flows against https://$DEV_HOST (never
#      :8096 or :18096 — a real port-443 hit against what the user hits).
#   3. push the resulting exposition to the Pushgateway (P8/2's emitter).
#   4. run the ratcheting regression guard against that SAME measurement
#      (P8/5's loadtime-guard.sh --live --apply), auto-filing a follow-up
#      task per breached scenario when `beehive` is on PATH.
#   5. mandatory build/test process cleanup (tools/ci/lib-cleanup.sh) +
#      an EXIT/INT/TERM trap so a mid-run failure never leaves a stray
#      dotnet build-server/testhost or a temp exposition file behind.
#
# Knobs (env), mirroring tools/ci/in-cluster-acceptance-run.sh:
#   PHANTOM_INCLUSTER_DEV_HOST      the current inactive/dev role's public
#                                    host (e.g. dev.example.com). Required
#                                    (or defaulted under PHANTOM_CI_DRYRUN=1).
#   PHANTOM_INCLUSTER_PROD_HOST      the apex/prod host — refused as a target,
#                                    used only for the safety guard.
#   PHANTOM_INCLUSTER_NAMESPACE      k8s namespace (default: phantom-library).
#   PHANTOM_INCLUSTER_KUBE_CONTEXT   kubectl context/kubeconfig reaching the
#                                    target cluster (default: current context).
#   PHANTOM_INCLUSTER_ADMIN_TOKEN    a Jellyfin admin ApiKey/AccessToken
#                                    already minted on the deployed stack
#                                    (see tools/ci/provision-in-cluster-rig-creds.md).
#                                    Required for a live run.
#   PHANTOM_PUSHGATEWAY_URL          Pushgateway base URL for the push step
#                                    (passed straight through to
#                                    scripts/phantom-loadtime-push.sh; never
#                                    baked here — infra-identifier rule).
#   PHANTOM_CI_DRYRUN=1              toolchain-agnostic dry run: no cluster/
#                                    network access; a deterministic
#                                    synthetic measurement is pushed/guarded
#                                    against instead (used by the in-repo
#                                    regression check).
# Exit non-zero on any hard failure. A regression-guard BREACH is reported
# (exit 3 from loadtime-guard.sh) but does not fail this wrapper's own exit
# code — the guard already files its own follow-up task; the daily job's
# purpose is to keep measuring, not to redundantly fail a schedule trigger.
# ---------------------------------------------------------------------------
set -euo pipefail

REPO_ROOT="${PHANTOM_REPO_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
cd "$REPO_ROOT"

# shellcheck source=tools/ci/lib-cleanup.sh
source "$REPO_ROOT/tools/ci/lib-cleanup.sh"

DRYRUN="${PHANTOM_CI_DRYRUN:-0}"
NAMESPACE="${PHANTOM_INCLUSTER_NAMESPACE:-phantom-library}"
DEV_HOST="${PHANTOM_INCLUSTER_DEV_HOST:-}"
PROD_HOST="${PHANTOM_INCLUSTER_PROD_HOST:-}"
ADMIN_TOKEN="${PHANTOM_INCLUSTER_ADMIN_TOKEN:-}"
KCTX=()
[ -n "${PHANTOM_INCLUSTER_KUBE_CONTEXT:-}" ] && KCTX=(--context "$PHANTOM_INCLUSTER_KUBE_CONTEXT")

log()  { printf '\n=== %s\n' "$*"; }
note() { printf '    %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

kube() { kubectl "${KCTX[@]}" -n "$NAMESPACE" "$@"; }

EXPO="$(mktemp -t loadtime-daily-expo.XXXXXX.txt)"
_torn_down=0
teardown() {
    local ec=$?
    [ "$_torn_down" = 1 ] && exit "$ec"
    _torn_down=1
    log "mandatory cleanup"
    rm -f "$EXPO"
    phantom_ci_cleanup_dotnet "$([ "$ec" != 0 ] && echo 1 || echo 0)" || true
    exit "$ec"
}
trap teardown EXIT INT TERM

log "phantom-library daily load-time schedule job"
note "namespace:  $NAMESPACE"
note "dev host:   ${DEV_HOST:-<unset>}"
note "prod host:  ${PROD_HOST:-<unset>}"
note "dry run:    $DRYRUN"

# --- prod safety guard (identical refusal to the Stage-5 acceptance rig) ---
if [ -n "$DEV_HOST" ] && [ -n "$PROD_HOST" ] && [ "$DEV_HOST" = "$PROD_HOST" ]; then
    fail "PHANTOM_INCLUSTER_DEV_HOST equals PHANTOM_INCLUSTER_PROD_HOST ($DEV_HOST) — refusing to measure what may be the production apex."
fi

if [ "$DRYRUN" = 1 ]; then
    [ -n "$DEV_HOST" ] || DEV_HOST="dev.example.com"
fi
[ -n "$DEV_HOST" ] || fail "PHANTOM_INCLUSTER_DEV_HOST is required"

# --- resolve which color is the dev host, LIVE from the Ingress -----------
resolve_color() {
    local host="$1" color
    if [ "$DRYRUN" = 1 ]; then
        echo "green"
        return 0
    fi
    for color in blue green; do
        if kube get ingress "phantom-library-$color" \
            -o jsonpath='{range .spec.rules[*]}{.host}{"\n"}{end}' 2>/dev/null \
            | grep -qx "$host"; then
            echo "$color"
            return 0
        fi
    done
    return 1
}

log "resolving dev color from the live Ingress (never a cached/CNAME guess)"
COLOR="$(resolve_color "$DEV_HOST")" || fail "no phantom-library-{blue,green} Ingress carries host $DEV_HOST — cannot resolve the dev color live."
note "dev host $DEV_HOST -> color=$COLOR"

if [ "$DRYRUN" != 1 ]; then
    [ -n "$ADMIN_TOKEN" ] || fail "PHANTOM_INCLUSTER_ADMIN_TOKEN is required for a live daily measurement run (see tools/ci/provision-in-cluster-rig-creds.md)"
fi

# Deployed stack, over real HTTPS on the port the user hits — never a
# standalone rig instance's :18096, never prod's raw :8096.
API="https://$DEV_HOST"

# =========================================================================
# 1. measure the six flows against the DEPLOYED stack
# =========================================================================
log "[1] measuring six load-time flows against $API (color=$COLOR)"
PHANTOM_LOADTIME_API="$API" \
PHANTOM_LOADTIME_TOKEN="$ADMIN_TOKEN" \
PHANTOM_LOADTIME_COLOR="$COLOR" \
PHANTOM_CI_DRYRUN="$DRYRUN" \
    bash "$REPO_ROOT/tools/rig-scenarios/47-loadtime-flows.sh" > "$EXPO"
note "measurement batch written to $EXPO"

# =========================================================================
# 2. push the batch to the Pushgateway
# =========================================================================
log "[2] pushing the measurement batch to the Pushgateway"
push_args=(push "$EXPO")
[ "$DRYRUN" = 1 ] && push_args=(--dry-run "${push_args[@]}")
bash "$REPO_ROOT/scripts/phantom-loadtime-push.sh" "${push_args[@]}"

# =========================================================================
# 3. ratcheting regression guard against the SAME live target
# =========================================================================
log "[3] running the daily ratcheting regression guard"
if [ "$DRYRUN" = 1 ]; then
    # Never --apply against the real thresholds registry from a synthetic
    # dry-run fixture — that would seed/tighten real ceilings from fake
    # numbers. Detect-only, no filing.
    guard_args=(--color "$COLOR" --no-file)
else
    guard_args=(--live --api "$API" --token "$ADMIN_TOKEN" --color "$COLOR" --apply)
    command -v beehive >/dev/null 2>&1 || guard_args+=(--no-file)
fi
set +e
bash "$REPO_ROOT/tools/perf/loadtime-guard.sh" "${guard_args[@]}"
guard_rc=$?
set -e
if [ "$guard_rc" = 0 ]; then
    note "regression guard: OK (no breach)"
elif [ "$guard_rc" = 3 ]; then
    note "regression guard: BREACH detected and filed (see loadtime-guard.sh output above)"
else
    fail "regression guard exited $guard_rc (tool error, not a breach)"
fi

log "phantom-library daily load-time schedule job complete"
