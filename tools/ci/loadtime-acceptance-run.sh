#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# tools/ci/loadtime-acceptance-run.sh
#
# ROI Priority 8 CAPSTONE — the live acceptance bar
# (phantom-library:p8-loadtime-acceptance-rig).
#
# Proves the WHOLE P8 loop end to end against the REAL deployed stack: the
# actual in-cluster phantom-library-bluegreen-deploy color's Jellyfin (its
# co-located gostream FUSE, patched image, shared-Postgres plugin state),
# measured via the P8/1 engine, pushed to the REAL Mimir Pushgateway (P8/2),
# and CONFIRMED to land as live Mimir series that the P8/4 Grafana dashboard's
# own datasource can query — for BOTH movie and episode.
#
# WHY THIS DOES NOT DRIVE https://$DEV_HOST DIRECTLY (unlike the daily
# schedule job's own doc, tools/ci/loadtime-daily-run.sh): the deployed
# Jellyfin host sits behind a Traefik ForwardAuth Keycloak SSO gate
# (infrastructure/phantom-library-oidc-gate in the flux repo) that gates
# EVERY path except the anonymous `/System/Info/Public` probe and the
# `/oauth2/*` handshake — an API token alone (X-Emby-Token) is REJECTED with
# a 302 to Keycloak on every other path, including `/Users`, `/Items`, and
# every endpoint the six flows exercise. This is a REAL, live-discovered
# constraint (not a design choice this script invents): driving the flows
# through the public host as currently designed can never succeed. Instead
# this script reaches the SAME real Pod's Jellyfin directly via
# `kubectl port-forward` to its Service port — an in-cluster-only path that
# bypasses the Ingress/Traefik/oauth2-proxy chain entirely while still
# measuring the identical deployed container, image, FUSE mount, and
# Postgres-backed plugin state a browser session would hit after logging in.
# This is the exact same "reach the real deployed color from outside the
# Ingress" posture tools/ci/in-cluster-acceptance-run.sh already uses via
# `kubectl exec ... curl localhost:8096` — port-forward is just the
# curl-from-outside-the-pod equivalent, letting this script reuse the P8/1
# engine (tools/rig-scenarios/47-loadtime-flows.sh) UNMODIFIED via its
# existing PHANTOM_LOADTIME_API knob (a local https://localhost:<port>
# forward, never :8096 literally — the engine's own hardcoded :8096 refusal
# stays intact as a second layer of protection against ever recording a
# prod-shaped port).
#
# Flow (mirrors tools/ci/in-cluster-acceptance-run.sh's shape):
#   1. resolve the dev color LIVE from the phantom-library-{blue,green}
#      Ingress (never a cached/CNAME guess) + the dev==prod refusal.
#   2. locate the resolved color's Running Pod.
#   3. mint a rig-only Jellyfin ApiKeys row directly via the Pod's own
#      Postgres connection (functionally identical to an admin-minted key;
#      see tools/ci/provision-in-cluster-rig-creds.md) — or reuse a caller-
#      supplied PHANTOM_INCLUSTER_ADMIN_TOKEN.
#   4. `kubectl port-forward` the Pod's 8096 to an ephemeral local port.
#   5. run the P8/1 measurement engine against that forward (six flows,
#      movie + episode).
#   6. push the resulting exposition to the REAL Pushgateway (P8/2).
#   7. query Mimir DIRECTLY for every one of the 12
#      phantom_loadtime_seconds{flow=...,item_type=...} series just pushed —
#      REFUSES (non-zero exit) unless all 12 are present, so this can never
#      pass on a "should have landed" assumption.
#   8. query Grafana's OWN dashboard datasource proxy
#      (api/datasources/proxy/uid/<ds>/...) for the two PRIORITY signals
#      (materialise, play_materialised) to prove the P8/4 dashboard's exact
#      query path resolves live data, and confirm the dashboard + its six
#      panel titles exist via api/dashboards/uid.
#   9. mandatory cleanup (EXIT/INT/TERM trap): kill the port-forward, delete
#      the rig-minted ApiKeys row — unconditionally, mid-run failure or not.
#
# Never touches the ACTIVE/prod color (refused by the dev==prod guard AND by
# only ever resolving+targeting the IDLE color). `trap`-clean.
#
# Knobs (env):
#   PHANTOM_INCLUSTER_DEV_HOST      current inactive/dev role's public host
#                                    (e.g. dev.example.com). Required (or
#                                    defaulted under PHANTOM_CI_DRYRUN=1).
#   PHANTOM_INCLUSTER_PROD_HOST     apex/prod host — refused as a target,
#                                    used only for the safety guard.
#   PHANTOM_INCLUSTER_NAMESPACE     k8s namespace (default: phantom-library).
#   PHANTOM_INCLUSTER_KUBE_CONTEXT  kubectl context (default: current).
#   PHANTOM_INCLUSTER_ADMIN_TOKEN   skip DB-minting, reuse this token.
#   PHANTOM_PUSHGATEWAY_URL         Pushgateway base URL (never baked here —
#                                    infra-identifier rule).
#   PHANTOM_MIMIR_QUERY_URL         Mimir's Prometheus-compatible query base
#                                    (e.g. http://mimir.<ns>.svc.cluster.local:8080/prometheus)
#                                    — never baked here.
#   PHANTOM_GRAFANA_URL             Grafana base URL (never baked here).
#   PHANTOM_GRAFANA_USER / PHANTOM_GRAFANA_PASSWORD
#                                   Grafana basic-auth credentials for the
#                                   read-only dashboard/datasource-proxy
#                                   verification calls.
#   PHANTOM_GRAFANA_DASHBOARD_UID   dashboard UID to verify (default:
#                                    phantom-library-loadtime).
#   PHANTOM_MIMIR_WAIT_SECONDS      seconds to wait/poll for the pushed
#                                    series to appear in Mimir (default 30).
#   PHANTOM_CI_DRYRUN=1             toolchain-agnostic dry run: no cluster/
#                                    network access; exercises the whole
#                                    control flow against synthetic fixtures.
# Exit non-zero on ANY hard failure — a missing series in Mimir or a
# dashboard/datasource verification failure is a HARD failure, never a
# soft warning: this is the "do NOT claim this bar met on a dry-run/
# structural basis" acceptance gate.
# ---------------------------------------------------------------------------
set -euo pipefail

REPO_ROOT="${PHANTOM_REPO_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
cd "$REPO_ROOT"

DRYRUN="${PHANTOM_CI_DRYRUN:-0}"
NAMESPACE="${PHANTOM_INCLUSTER_NAMESPACE:-phantom-library}"
DEV_HOST="${PHANTOM_INCLUSTER_DEV_HOST:-}"
PROD_HOST="${PHANTOM_INCLUSTER_PROD_HOST:-}"
ADMIN_TOKEN="${PHANTOM_INCLUSTER_ADMIN_TOKEN:-}"
PUSHGATEWAY_URL="${PHANTOM_PUSHGATEWAY_URL:-}"
MIMIR_QUERY_URL="${PHANTOM_MIMIR_QUERY_URL:-}"
GRAFANA_URL="${PHANTOM_GRAFANA_URL:-}"
GRAFANA_USER="${PHANTOM_GRAFANA_USER:-}"
GRAFANA_PASSWORD="${PHANTOM_GRAFANA_PASSWORD:-}"
DASHBOARD_UID="${PHANTOM_GRAFANA_DASHBOARD_UID:-phantom-library-loadtime}"
MIMIR_WAIT="${PHANTOM_MIMIR_WAIT_SECONDS:-30}"
KCTX=()
[ -n "${PHANTOM_INCLUSTER_KUBE_CONTEXT:-}" ] && KCTX=(--context "$PHANTOM_INCLUSTER_KUBE_CONTEXT")

# The six ROI-named flows and the two priority signals within them.
FLOWS=(list_load sort_change info_open get_sources materialise play_materialised)
PRIORITY_FLOWS=(materialise play_materialised)
ITEM_TYPES=(movie episode)

log()  { printf '\n=== %s\n' "$*"; }
note() { printf '    %s\n' "$*"; }
result(){ printf 'RESULT: %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

kube() { kubectl "${KCTX[@]}" -n "$NAMESPACE" "$@"; }

log "phantom-library P8 acceptance rig (live end-to-end proof)"
note "namespace:  $NAMESPACE"
note "dev host:   ${DEV_HOST:-<unset>}"
note "prod host:  ${PROD_HOST:-<unset>}"
note "dry run:    $DRYRUN"

# --- prod safety guard (identical refusal to the Stage-5 acceptance rig) ---
if [ -n "$DEV_HOST" ] && [ -n "$PROD_HOST" ] && [ "$DEV_HOST" = "$PROD_HOST" ]; then
    fail "PHANTOM_INCLUSTER_DEV_HOST equals PHANTOM_INCLUSTER_PROD_HOST ($DEV_HOST) — refusing (may be the production apex)."
fi

if [ "$DRYRUN" = 1 ]; then
    [ -n "$DEV_HOST" ] || DEV_HOST="dev.example.com"
    [ -n "$PUSHGATEWAY_URL" ] || PUSHGATEWAY_URL="http://pushgateway.example.com:9091"
    [ -n "$MIMIR_QUERY_URL" ] || MIMIR_QUERY_URL="http://mimir.example.com:8080/prometheus"
    [ -n "$GRAFANA_URL" ] || GRAFANA_URL="http://grafana.example.com"
fi
[ -n "$DEV_HOST" ] || fail "PHANTOM_INCLUSTER_DEV_HOST is required"
[ -n "$PUSHGATEWAY_URL" ] || fail "PHANTOM_PUSHGATEWAY_URL is required for a live run"
[ -n "$MIMIR_QUERY_URL" ] || fail "PHANTOM_MIMIR_QUERY_URL is required for a live run"
[ -n "$GRAFANA_URL" ] || fail "PHANTOM_GRAFANA_URL is required for a live run"

# --- resolve which color is the dev host, LIVE from the Ingress ----------
resolve_color() {
    local host="$1" color
    if [ "$DRYRUN" = 1 ]; then echo "green"; return 0; fi
    for color in blue green; do
        if kube get ingress "phantom-library-$color" \
            -o jsonpath='{range .spec.rules[*]}{.host}{"\n"}{end}' 2>/dev/null \
            | grep -qx "$host"; then
            echo "$color"; return 0
        fi
    done
    return 1
}
log "[1] resolving dev color from the live Ingress"
COLOR="$(resolve_color "$DEV_HOST")" || fail "no phantom-library-{blue,green} Ingress carries host $DEV_HOST"
note "dev host $DEV_HOST -> color=$COLOR"
result "color resolution: $DEV_HOST -> $COLOR (live Ingress)"

POD=""
find_pod() {
    if [ "$DRYRUN" = 1 ]; then POD="phantom-library-$COLOR-dryrun-pod"; return 0; fi
    POD="$(kube get pods -l "app.kubernetes.io/instance=phantom-library-$COLOR" \
        -o jsonpath='{.items[0].metadata.name}' 2>/dev/null || true)"
    [ -n "$POD" ] || POD="$(kube get pods -o name 2>/dev/null | grep "phantom-library-$COLOR-" | head -1 | sed 's#^pod/##')"
    [ -n "$POD" ] || return 1
}
log "[2] locating the $COLOR color's Jellyfin Pod"
find_pod || fail "no Running phantom-library-$COLOR Pod found in namespace $NAMESPACE"
note "pod: $POD"

# --- guaranteed rig-state cleanup (EXIT/INT/TERM), always runs ------------
PF_PID=""; PF_PORT=""; RIG_KEY_NAME=""; RIG_TOKEN_MINTED=""
_torn_down=0
teardown() {
    local ec=$?
    [ "$_torn_down" = 1 ] && exit "$ec"
    _torn_down=1
    log "mandatory cleanup"
    if [ -n "$PF_PID" ]; then
        kill "$PF_PID" >/dev/null 2>&1 || true
        wait "$PF_PID" 2>/dev/null || true
        note "port-forward (pid $PF_PID) stopped"
    fi
    if [ "$DRYRUN" != 1 ] && [ -n "$RIG_TOKEN_MINTED" ] && [ -n "$RIG_KEY_NAME" ]; then
        kube exec "$POD" -c jellyfin -- sh -c "PGPASSWORD=\$POSTGRES_PASSWORD psql -h \$POSTGRES_HOST -U \$POSTGRES_USER -d \$POSTGRES_DB -c \"DELETE FROM \\\"ApiKeys\\\" WHERE \\\"Name\\\"='$RIG_KEY_NAME'\"" >/dev/null 2>&1 || true
        note "rig-minted ApiKeys row '$RIG_KEY_NAME' deleted"
    fi
    exit "$ec"
}
trap teardown EXIT INT TERM

# --- admin API access (provided token, or DB-minted rig-only fallback) ----
log "[3] admin API access"
if [ "$DRYRUN" = 1 ]; then
    ADMIN_TOKEN="dryrun-token"
    note "DRYRUN: skip mint"
elif [ -z "$ADMIN_TOKEN" ]; then
    RIG_KEY_NAME="phantom-p8acc-$(date +%s)"
    RIG_TOKEN_MINTED="$(head -c16 /dev/urandom | od -An -tx1 | tr -d ' \n')"
    kube exec "$POD" -c jellyfin -- sh -c "PGPASSWORD=\$POSTGRES_PASSWORD psql -h \$POSTGRES_HOST -U \$POSTGRES_USER -d \$POSTGRES_DB -c \"INSERT INTO \\\"ApiKeys\\\" (\\\"DateCreated\\\",\\\"DateLastActivity\\\",\\\"Name\\\",\\\"AccessToken\\\") VALUES (now(), now(), '$RIG_KEY_NAME', '$RIG_TOKEN_MINTED')\"" \
        || fail "could not mint a rig-only ApiKeys row"
    ADMIN_TOKEN="$RIG_TOKEN_MINTED"
    note "rig-minted key '$RIG_KEY_NAME' (torn down at exit)"
fi

# --- port-forward the Pod's Jellyfin port, bypassing the SSO gate --------
# (see the header comment: the public host is entirely gated by an OIDC
# ForwardAuth middleware except /System/Info/Public and /oauth2/*, so the
# only way to drive the six API-level flows against the REAL deployed Pod
# is a direct in-cluster path. Port-forward is that path; it never touches
# :8096 in the URL the engine sees, so the engine's own :8096 refusal is
# never bypassed — it forwards to an ephemeral LOCAL port instead.)
log "[4] port-forwarding to the $COLOR Pod's Jellyfin port"
LOCAL_API=""
if [ "$DRYRUN" = 1 ]; then
    LOCAL_API="http://localhost:18096"
    note "DRYRUN: skip port-forward, use $LOCAL_API"
else
    PF_PORT="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')"
    kube port-forward "pod/$POD" "$PF_PORT:8096" >/tmp/p8acc-portforward.$$.log 2>&1 &
    PF_PID=$!
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        curl -sS -o /dev/null --max-time 2 "http://localhost:$PF_PORT/System/Info/Public" && break
        sleep 1
    done
    LOCAL_API="http://localhost:$PF_PORT"
    curl -sS --fail -o /dev/null --max-time 5 "$LOCAL_API/System/Info/Public" \
        || fail "port-forward to $POD:8096 did not come up (see /tmp/p8acc-portforward.$$.log)"
    note "port-forward up: $LOCAL_API -> pod/$POD:8096 (pid $PF_PID)"
fi

# --- run the P8/1 measurement engine against the real deployed Pod --------
log "[5] measuring the six load-time flows (movie + episode) against the real $COLOR Pod"
EXPO="$(mktemp -t p8acc-expo.XXXXXX.txt)"
PHANTOM_LOADTIME_API="$LOCAL_API" \
PHANTOM_LOADTIME_TOKEN="$ADMIN_TOKEN" \
PHANTOM_LOADTIME_COLOR="$COLOR" \
PHANTOM_CI_DRYRUN="$DRYRUN" \
    bash "$REPO_ROOT/tools/rig-scenarios/47-loadtime-flows.sh" > "$EXPO"
note "measurement batch: $EXPO"
result "measurement: 6 flows x 2 item types captured against real $COLOR Pod"

# --- push to the REAL Pushgateway (P8/2) ----------------------------------
log "[6] pushing the measurement batch to the Pushgateway"
push_args=(push "$EXPO")
[ "$DRYRUN" = 1 ] && push_args=(--dry-run "${push_args[@]}")
PHANTOM_PUSHGATEWAY_URL="$PUSHGATEWAY_URL" \
    bash "$REPO_ROOT/scripts/phantom-loadtime-push.sh" "${push_args[@]}"
result "push: batch pushed to $PUSHGATEWAY_URL (job=phantom-loadtime)"

# --- confirm every series landed in the REAL Mimir (never assumed) -------
log "[7] confirming all 12 series landed in Mimir (never a should-work assumption)"
if [ "$DRYRUN" = 1 ]; then
    note "DRYRUN: skip live Mimir query"
    result "mimir: DRYRUN skipped"
else
    missing=0
    deadline=$(( $(date +%s) + MIMIR_WAIT ))
    for flow in "${FLOWS[@]}"; do
        for it in "${ITEM_TYPES[@]}"; do
            found=0
            while [ "$(date +%s)" -le "$deadline" ]; do
                resp="$(curl -sS --fail -G "$MIMIR_QUERY_URL/api/v1/query" \
                    --data-urlencode "query=phantom_loadtime_seconds{flow=\"$flow\",item_type=\"$it\",color=\"$COLOR\"}" 2>/dev/null || true)"
                if printf '%s' "$resp" | python3 -c "import json,sys; d=json.load(sys.stdin); sys.exit(0 if d.get('data',{}).get('result') else 1)" 2>/dev/null; then
                    found=1; break
                fi
                sleep 2
            done
            if [ "$found" = 1 ]; then
                note "PASS mimir has phantom_loadtime_seconds{flow=$flow,item_type=$it,color=$COLOR}"
            else
                note "MISSING phantom_loadtime_seconds{flow=$flow,item_type=$it,color=$COLOR}"
                missing=$((missing+1))
            fi
        done
    done
    [ "$missing" = 0 ] || fail "$missing of 12 (flow,item_type) series never appeared in Mimir within ${MIMIR_WAIT}s"
    result "mimir: all 12 (flow x item_type) series confirmed live"
fi

# --- confirm the Grafana dashboard exists + its datasource resolves data -
log "[8] confirming the Grafana dashboard renders real data"
if [ "$DRYRUN" = 1 ]; then
    note "DRYRUN: skip live Grafana query"
    result "grafana: DRYRUN skipped"
else
    auth=()
    [ -n "$GRAFANA_USER" ] && auth=(-u "$GRAFANA_USER:$GRAFANA_PASSWORD")
    dash_json="$(curl -sS --fail "${auth[@]}" "$GRAFANA_URL/api/dashboards/uid/$DASHBOARD_UID")" \
        || fail "could not fetch Grafana dashboard uid=$DASHBOARD_UID"
    panel_count="$(printf '%s' "$dash_json" | python3 -c "import json,sys; print(len(json.load(sys.stdin)['dashboard']['panels']))")"
    [ "$panel_count" -ge 6 ] || fail "dashboard uid=$DASHBOARD_UID has $panel_count panels, expected >= 6 (one per flow)"
    note "dashboard uid=$DASHBOARD_UID has $panel_count panels"
    ds_uid="$(printf '%s' "$dash_json" | python3 -c "
import json,sys
d=json.load(sys.stdin)['dashboard']
p=d['panels'][0]
print(p.get('datasource',{}).get('uid','') if isinstance(p.get('datasource'), dict) else '')
")"
    [ -n "$ds_uid" ] && [ "$ds_uid" != '${DS_MIMIR}' ] || ds_uid="mimir"
    for flow in "${PRIORITY_FLOWS[@]}"; do
        resp="$(curl -sS --fail "${auth[@]}" -G "$GRAFANA_URL/api/datasources/proxy/uid/$ds_uid/api/v1/query" \
            --data-urlencode "query=phantom_loadtime_seconds{flow=\"$flow\",color=\"$COLOR\"}")" \
            || fail "Grafana datasource proxy query failed for flow=$flow"
        printf '%s' "$resp" | python3 -c "import json,sys; d=json.load(sys.stdin); sys.exit(0 if d.get('data',{}).get('result') else 1)" \
            || fail "Grafana's own datasource proxy returned NO data for priority flow=$flow (dashboard would render empty)"
        note "PASS grafana datasource resolves live data for priority flow=$flow"
    done
    result "grafana: dashboard uid=$DASHBOARD_UID panels=$panel_count; both priority flows resolve live data via its own datasource"
fi

log "phantom-library P8 acceptance rig complete — live end-to-end loop proven"
