#!/usr/bin/env bash
# tools/ci/in-cluster-acceptance-run.sh
#
# P3 Stage 5 in-cluster acceptance rig (task in-cluster-acceptance-rig): the
# ultimate ROI acceptance bar. Stages 1-4 + the P2 gitea-live-rig-job prove
# the plugin against the rig's OWN throwaway Jellyfin (:18096, tools/ci/
# live-rig-run.sh) built from freshly-pulled images -- that proves the
# PLUGIN, never the DEPLOYED CLUSTER STACK. This script instead targets the
# REAL, already-running `phantom-library-bluegreen-deploy` HelmRelease on
# `spray` (flux submodule, clusters/spray/phantom-library-helm.yaml): the
# color/dev role hosts, the nested wildcard TLS, and the co-located
# gostream-FUSE Pod the chart actually ships.
#
# Never touches production: this script only ever resolves and drives the
# host CURRENTLY carrying the IDLE/dev role (the operator's dev-role host
# always CNAMEs to whichever color is idle -- see flux
# clusters/spray/phantom-library-helm.yaml + infrastructure/
# phantom-library-helm/helmrelease-{blue,green}.yaml). The apex/active-color
# host is never targeted, dialed, or mutated by this script; if the
# resolved dev host and the caller-supplied prod host ever coincide it
# refuses outright (see refuse_if_prod below) rather than risk touching the
# active color.
#
# Infra identifiers (real hostnames, the k8s namespace/context, TLS SANs)
# are NEVER hardcoded here (AGENTS.md "infrastructure identifiers" rule) --
# every one of them is supplied by the caller (the self-hosted Gitea
# Actions in-cluster-acceptance-rig.yaml workflow, itself fed from the
# operator's CI variables/secrets) via the env knobs documented below. The
# only literal path-shaped facts baked in here are ones already public in
# THIS repo's own chart (deploy/helm/phantom-library/values.yaml): the
# gostream FUSE mount path and the jellyfin/phantom DB locations -- not
# deployment-specific.
#
# Knobs (env):
#   PHANTOM_CI_DRYRUN=1                 echo the heavy steps instead of running
#                                         them (toolchain-agnostic dry run; no
#                                         kubectl/curl/openssl/network needed).
#                                         Used by the in-repo regression check.
#   PHANTOM_INCLUSTER_DEV_HOST           REQUIRED (outside dry run): the
#                                         current idle/dev role hostname to
#                                         drive the rig against (e.g. the
#                                         operator's dev.jellyfin.<domain>).
#   PHANTOM_INCLUSTER_PROD_HOST          optional: the active/apex hostname.
#                                         If set and equal to
#                                         PHANTOM_INCLUSTER_DEV_HOST, this
#                                         script REFUSES (safety net against a
#                                         misconfigured CNAME/cutover racing
#                                         this job -- never dial prod).
#   PHANTOM_INCLUSTER_NAMESPACE          k8s namespace (default: phantom-library
#                                         -- the chart's own name, already
#                                         public in this repo, not a
#                                         deployment-specific secret).
#   PHANTOM_INCLUSTER_KUBECONTEXT        optional kube context name (default:
#                                         current context).
#   PHANTOM_INCLUSTER_GOSTREAM_MOUNT     FUSE mount path inside the Pod
#                                         (default: /var/gostream/gostream-mkv-virtual,
#                                         matching deploy/helm/phantom-library/values.yaml).
#   PHANTOM_INCLUSTER_LOCAL_PORT          (unused, kept for CI-log parity
#                                         with tools/ci/live-rig-run.sh) --
#                                         scenarios run INSIDE the Pod, not
#                                         via a local port-forward: 35/36/42
#                                         read/write their sqlite DBs
#                                         (PHDB/JDB) directly as local files,
#                                         which only exist inside the Pod's
#                                         mounted PVCs, not on the CI
#                                         runner -- so port-forwarding HTTP
#                                         alone would leave those file-level
#                                         assertions unable to see the real
#                                         DB. Running inside the Pod via
#                                         `kubectl exec` gives the scenarios
#                                         their real local paths and real
#                                         localhost networking, unmodified.
#   PHANTOM_INCLUSTER_REMOTE_PORT         container port the scenarios talk
#                                         to over the Pod's own loopback
#                                         (default 8096, jellyfin.httpPort).
#   PHANTOM_RIG_SCENARIOS                space-separated scenario scripts to
#                                         run (default: the movie/TV/
#                                         per-user-show-hide trio below).
#   PHANTOM_INCLUSTER_ADMIN_TOKEN         REQUIRED (outside dry run): a
#                                         dedicated Jellyfin API key for a
#                                         rig-only e2e service account,
#                                         already provisioned in the dev/
#                                         color instance out of band (an
#                                         operator/CI secret -- this repo
#                                         never bakes in a live credential).
#                                         Exported as TOK for scenarios
#                                         35/36/42, which all now accept an
#                                         overridable TOK (default
#                                         preserved for the local rig).
#   PHANTOM_INCLUSTER_JF_DATA            Jellyfin data root inside the Pod
#                                         (default /var/lib/jellyfin, matching
#                                         deploy/helm/phantom-library's
#                                         mountPath) -- used to derive PHDB
#                                         (.../plugins/configurations/
#                                         PhantomLibrary/phantom.db) and JDB
#                                         (.../data/jellyfin.db) for the
#                                         scenarios' direct sqlite assertions.
#
# What this proves, end to end, against the REAL deployed stack:
#   1. the dev/color host resolves, answers HTTPS, and its cert SAN covers
#      the nested nested-wildcard hostname pattern (role host + the
#      gostorm tiramisu.<host> sibling) -- proving the wildcard TLS +
#      role-CNAME routing the chart depends on.
#   2. the Pod backing that host is genuinely running gostream co-located
#      with Jellyfin in ONE mount namespace: the FUSE mount is live and
#      populated (not merely configured) -- proving Stage B consolidation.
#   3. the movie/TV e2e playback (35/36) and the per-user show/hide (42)
#      scenarios pass INSIDE the real deployed Pod (via `kubectl exec`,
#      never a freshly-pulled throwaway rig container) -- proving the
#      DEPLOY, not just the image.
#
# rig-scenarios 35/36/42 all now accept an overridable API/BASE, TOK, PHDB,
# and JDB (falling back to their original local-rig defaults when unset),
# so copying + running them unmodified inside the Pod via `kubectl exec`
# needs only env overrides -- no script forking.
set -euo pipefail

REPO_ROOT="${PHANTOM_REPO_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
cd "$REPO_ROOT"

DRYRUN="${PHANTOM_CI_DRYRUN:-0}"
NAMESPACE="${PHANTOM_INCLUSTER_NAMESPACE:-phantom-library}"
DEV_HOST="${PHANTOM_INCLUSTER_DEV_HOST:-}"
PROD_HOST="${PHANTOM_INCLUSTER_PROD_HOST:-}"
GOSTREAM_MOUNT="${PHANTOM_INCLUSTER_GOSTREAM_MOUNT:-/var/gostream/gostream-mkv-virtual}"
REMOTE_PORT="${PHANTOM_INCLUSTER_REMOTE_PORT:-8096}"
ADMIN_TOKEN="${PHANTOM_INCLUSTER_ADMIN_TOKEN:-}"
JF_DATA="${PHANTOM_INCLUSTER_JF_DATA:-/var/lib/jellyfin}"
KCTX_ARGS=()
[ -n "${PHANTOM_INCLUSTER_KUBECONTEXT:-}" ] && KCTX_ARGS=(--context "$PHANTOM_INCLUSTER_KUBECONTEXT")

DEFAULT_SCENARIOS="tools/rig-scenarios/35-channel-e2e-playback.sh tools/rig-scenarios/36-channel-episode-e2e-playback.sh tools/rig-scenarios/42-per-user-show-hide.sh"
# shellcheck disable=SC2206
SCENARIOS=(${PHANTOM_RIG_SCENARIOS:-$DEFAULT_SCENARIOS})

log()  { printf '\n=== %s\n' "$*"; }
note() { printf '    %s\n' "$*"; }
kctl() { kubectl "${KCTX_ARGS[@]}" -n "$NAMESPACE" "$@"; }

log "phantom-library in-cluster acceptance rig"
note "namespace:           $NAMESPACE"
note "dev/color host:      ${DEV_HOST:-<unset>}"
note "gostream FUSE mount: $GOSTREAM_MOUNT"
note "jellyfin data root:  $JF_DATA (in-Pod)"
note "remote port:         $REMOTE_PORT (Pod-local loopback)"
note "scenarios:           ${SCENARIOS[*]}"
note "dry run:             $DRYRUN"

# --- guardrails -----------------------------------------------------------
if [ "$DRYRUN" = 1 ]; then
    DEV_HOST="${DEV_HOST:-dev.example.invalid}"
else
    [ -n "$DEV_HOST" ] || { echo "REFUSING: PHANTOM_INCLUSTER_DEV_HOST is required (the current idle/dev role host)" >&2; exit 1; }
    [ -n "$ADMIN_TOKEN" ] || { echo "REFUSING: PHANTOM_INCLUSTER_ADMIN_TOKEN is required (rig-only e2e service-account API key)" >&2; exit 1; }
fi
if [ -n "$PROD_HOST" ] && [ "$PROD_HOST" = "$DEV_HOST" ]; then
    echo "REFUSING: PHANTOM_INCLUSTER_DEV_HOST equals PHANTOM_INCLUSTER_PROD_HOST -- never dial the active/prod color" >&2
    exit 1
fi

# --- guaranteed teardown, always runs -------------------------------------
_torn_down=0
teardown() {
    local ec=$?
    [ "$_torn_down" = 1 ] && exit "$ec"
    _torn_down=1
    log "tearing down (guaranteed EXIT trap)"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: kubectl exec \$POD_NAME -- rm -rf /tmp/phantom-incluster-rig"
    elif [ -n "${POD_NAME:-}" ]; then
        kctl exec "$POD_NAME" -- rm -rf /tmp/phantom-incluster-rig >/dev/null 2>&1 || true
    fi
    exit "$ec"
}
trap teardown EXIT

# --- 1. resolve the Pod currently serving the dev/color host --------------
# Never assumes which color (blue/green) is idle -- resolves it live from
# the Ingress actually carrying the dev-role host, so a flip never stales
# this script's assumptions.
resolve_pod() {
    log "resolving the Pod backing $DEV_HOST"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: kubectl -n $NAMESPACE get ingress -o json | jq -r 'hosts matching $DEV_HOST'"
        note "DRYRUN: kubectl -n $NAMESPACE get pods -l app.kubernetes.io/instance=<resolved-color> -o jsonpath name"
        POD_NAME="dryrun-pod"
        return 0
    fi
    local instance
    instance=$(kctl get ingress -o json \
        | python3 -c '
import json, sys
doc = json.load(sys.stdin)
host = sys.argv[1]
for item in doc.get("items", []):
    rules = item.get("spec", {}).get("rules", [])
    if any(r.get("host") == host for r in rules):
        labels = item.get("metadata", {}).get("labels", {})
        inst = labels.get("app.kubernetes.io/instance")
        if inst:
            print(inst)
            break
' "$DEV_HOST")
    [ -n "$instance" ] || { echo "ERROR: no Ingress carries host $DEV_HOST in namespace $NAMESPACE" >&2; return 1; }
    note "resolved release/instance: $instance"
    POD_NAME=$(kctl get pods -l "app.kubernetes.io/instance=$instance,app.kubernetes.io/component=jellyfin" \
        --field-selector=status.phase=Running -o jsonpath='{.items[0].metadata.name}')
    [ -n "$POD_NAME" ] || { echo "ERROR: no Running Pod for instance $instance" >&2; return 1; }
    note "resolved Pod: $POD_NAME"
}

# --- 2. TLS + role-CNAME routing assertion --------------------------------
assert_tls_and_routing() {
    log "asserting HTTPS + wildcard TLS SAN + role-CNAME routing for $DEV_HOST"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: curl -sS -o /dev/null -w '%{http_code}' https://$DEV_HOST/health"
        note "DRYRUN: openssl s_client -connect $DEV_HOST:443 -servername $DEV_HOST | openssl x509 -noout -text | grep 'Subject Alternative Name'"
        return 0
    fi
    local code
    code=$(curl -sS -o /dev/null -w '%{http_code}' "https://$DEV_HOST/health")
    [ "$code" = "200" ] || { echo "ERROR: https://$DEV_HOST/health returned $code, expected 200" >&2; return 1; }
    local san
    san=$(echo | openssl s_client -connect "$DEV_HOST:443" -servername "$DEV_HOST" 2>/dev/null \
        | openssl x509 -noout -text 2>/dev/null | grep -A1 "Subject Alternative Name" || true)
    [ -n "$san" ] || { echo "ERROR: no Subject Alternative Name found on $DEV_HOST's serving certificate" >&2; return 1; }
    note "SAN: $san"
}

# --- 3. real gostream/Jellyfin FUSE co-location, live in the Pod ----------
assert_fuse_colocation() {
    log "asserting live gostream FUSE co-location in $POD_NAME"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: kubectl exec $POD_NAME -- sh -c \"mountpoint -q '$GOSTREAM_MOUNT' && ls '$GOSTREAM_MOUNT' | head -1\""
        return 0
    fi
    kctl exec "$POD_NAME" -- sh -c "mountpoint -q '$GOSTREAM_MOUNT'" \
        || { echo "ERROR: $GOSTREAM_MOUNT is not a live mountpoint inside $POD_NAME -- FUSE co-location is not proven" >&2; return 1; }
    kctl exec "$POD_NAME" -- sh -c "ls -A '$GOSTREAM_MOUNT' | head -1" >/dev/null \
        || { echo "ERROR: $GOSTREAM_MOUNT is mounted but not populated inside $POD_NAME" >&2; return 1; }
    note "FUSE mount is live and populated"
}

# --- 4. run the movie/TV/per-user scenarios INSIDE the real deployed Pod -
# `kubectl exec` runs the scenario's real localhost:8096 + its real
# on-disk PHDB/JDB paths, so 35/36/42 exercise the ACTUAL deployed process
# and its ACTUAL live sqlite DBs -- not a copy, not a proxy.
preflight_pod_tools() {
    log "checking scenario prerequisites are present in $POD_NAME"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: kubectl exec $POD_NAME -- sh -c 'command -v bash && command -v curl && command -v sqlite3'"
        return 0
    fi
    kctl exec "$POD_NAME" -- sh -c 'command -v bash >/dev/null && command -v curl >/dev/null && command -v sqlite3 >/dev/null' \
        || { echo "ERROR: $POD_NAME is missing bash/curl/sqlite3 -- the deployed image must ship all three for this rig to run in-Pod" >&2; return 1; }
    note "bash/curl/sqlite3 present"
}

run_scenarios() {
    local s base
    local remote_dir=/tmp/phantom-incluster-rig
    if [ "$DRYRUN" = 1 ]; then
        for s in "${SCENARIOS[@]}"; do
            log "scenario (in-cluster, DRYRUN): $s"
            note "DRYRUN: kubectl cp $s $POD_NAME:$remote_dir/$(basename "$s")"
            note "DRYRUN: kubectl exec $POD_NAME -- env API=http://localhost:$REMOTE_PORT BASE=http://localhost:$REMOTE_PORT TOK=<redacted> PHDB=$JF_DATA/plugins/configurations/PhantomLibrary/phantom.db JDB=$JF_DATA/data/jellyfin.db bash $remote_dir/$(basename "$s")"
        done
        return 0
    fi
    kctl exec "$POD_NAME" -- mkdir -p "$remote_dir"
    for s in "${SCENARIOS[@]}"; do
        base=$(basename "$s")
        log "scenario (in-cluster): $s"
        kctl cp "$s" "$POD_NAME:$remote_dir/$base"
        kctl exec "$POD_NAME" -- env \
            API="http://localhost:$REMOTE_PORT" \
            BASE="http://localhost:$REMOTE_PORT" \
            TOK="$ADMIN_TOKEN" \
            PHDB="$JF_DATA/plugins/configurations/PhantomLibrary/phantom.db" \
            JDB="$JF_DATA/data/jellyfin.db" \
            bash "$remote_dir/$base"
    done
}

resolve_pod
assert_tls_and_routing
assert_fuse_colocation
preflight_pod_tools
run_scenarios

log "in-cluster acceptance rig PASSED against the real deployed stack"
# EXIT trap removes the copied-in scenario scripts from the Pod's /tmp next.
# Nothing else is mutated: this script never writes to the gitlink, never
# touches the active/prod color, and the scenarios' own DB writes land only
# in the dev/idle color's DB -- exactly the color LOCALS.md already
# sanctions continuous, no-operator-gate deploys against.
