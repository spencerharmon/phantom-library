#!/usr/bin/env bash
# tools/ci/in-cluster-acceptance-run.sh
#
# P3 Stage 5 — the in-cluster ACCEPTANCE rig (the ultimate ROI acceptance
# bar). Unlike tools/ci/live-rig-run.sh (the gitea-live-rig-job gate), this
# harness does NOT stand up the rig's own throwaway Jellyfin on :18096
# against freshly-pulled images — that proves the plugin/images in isolation,
# NOT the deployed cluster stack. This harness instead retargets the
# acceptance assertions at the ACTUAL in-cluster phantom-library blue/green
# deployment on `spray`: the color/dev hosts + role CNAMEs, the nested
# wildcard TLS, the CONSOLIDATED single-container image with its co-located
# gostream FUSE (Stage B: one mount namespace), driven over the real ingress
# via dev.jellyfin.polyfam.studio / the color host.
#
# It asserts, against the DEPLOYED stack:
#   Phase A (public, no auth):
#     - the target resolves over real TLS through the ingress,
#     - it is a phantom-library COLOR deployment (ServerName phantom-library-
#       {blue,green}-...), running the expected Jellyfin version,
#     - PROD SAFETY: the target is NEVER the color currently fronting live
#       production. The prod apex host (jellyfin.polyfam.studio) is fetched
#       and its ACTIVE color resolved; the target is refused if it is that
#       same color. (Blue and green share the authoritative library DB — P4
#       Stage A — so they report the SAME server Id; the COLOR, carried by the
#       ServerName/Pod name, is the correct prod-safety axis.) The rig only
#       ever drives the dev host / the INACTIVE color, never prod Jellyfin.
#   Phase B (in-cluster introspection, kubectl):
#     - the deployed Pod runs the single CONSOLIDATED jellyfin-phantom image
#       (gostream baked in), not a multi-container sidecar split,
#     - gostream's virtual-MKV FUSE is co-mounted INSIDE the jellyfin
#       container at /var/gostream/gostream-mkv-virtual (a real `fuse` mount),
#       exposing movies/ and tv/ in the SAME mount namespace Jellyfin reads,
#     - the PhantomLibrary plugin is loaded in the deployed Pod.
#   Phase C (authenticated e2e, needs PHANTOM_INCLUSTER_APIKEY):
#     - the deployed /Plugins lists PhantomLibrary,
#     - Phantom channels enumerate,
#     - movie e2e playback (scenario 35 semantics), TV episode e2e playback
#       (scenario 36 semantics), and the per-user show/hide scenario
#       (scenario 42 semantics) succeed AGAINST THE DEPLOYED STACK.
#
# This is BOTH the blue/green deploy's acceptance proof AND the precondition
# the operator's host->cluster prod-migration (flux ROI P3) validates against
# before any prod CNAME flip.
#
# Knobs (env):
#   PHANTOM_CI_DRYRUN=1              echo the heavy steps instead of running
#                                     them (toolchain-agnostic dry run — no
#                                     kubectl, curl, network, or a live
#                                     cluster needed). Used by the in-repo
#                                     regression check
#                                     scripts/tests/in-cluster-acceptance-rig.test.sh.
#   PHANTOM_INCLUSTER_BASE_URL=<url> deployed target base URL
#                                     (default: https://dev.jellyfin.polyfam.studio —
#                                     the dev host, which fronts the INACTIVE/
#                                     dev color, never prod).
#   PHANTOM_PROD_BASE_URL=<url>       the live PROD apex used only for the
#                                     prod-safety identity guard
#                                     (default: https://jellyfin.polyfam.studio).
#   PHANTOM_INCLUSTER_APIKEY=<key>   Jellyfin API key for Phase C (operator-
#                                     held CI secret). Absent -> Phase C is
#                                     SKIPPED in a normal run, but REQUIRED
#                                     (fail-closed) when PHANTOM_REQUIRE_AUTH=1.
#   PHANTOM_REQUIRE_AUTH=1           make a missing API key a hard failure
#                                     (the Gitea Actions job sets this so the
#                                     acceptance bar cannot silently degrade
#                                     to the unauthenticated subset).
#   PHANTOM_INCLUSTER_NAMESPACE=<ns> k8s namespace of the deployment
#                                     (default: phantom-library).
#   PHANTOM_INCLUSTER_COLOR=<color>  which color to introspect in Phase B
#                                     (default: derived from the target
#                                     ServerName).
#   PHANTOM_EXPECT_JF_VERSION=<v>    expected Jellyfin version prefix
#                                     (default: 10.11).
#   PHANTOM_REPO_ROOT=<path>         repo root (default: this script's repo).
#
# Never touches prod: Phase A fails closed if the target color equals the
# color currently fronting the prod apex; the harness only reads/drives the
# dev host / inactive color. It creates no rig processes; a temp dir is
# cleaned in an EXIT trap.
set -euo pipefail

REPO_ROOT="${PHANTOM_REPO_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
cd "$REPO_ROOT"

DRYRUN="${PHANTOM_CI_DRYRUN:-0}"
BASE_URL="${PHANTOM_INCLUSTER_BASE_URL:-https://dev.jellyfin.polyfam.studio}"
PROD_URL="${PHANTOM_PROD_BASE_URL:-https://jellyfin.polyfam.studio}"
NS="${PHANTOM_INCLUSTER_NAMESPACE:-phantom-library}"
EXPECT_VER="${PHANTOM_EXPECT_JF_VERSION:-10.11}"
REQUIRE_AUTH="${PHANTOM_REQUIRE_AUTH:-0}"
APIKEY="${PHANTOM_INCLUSTER_APIKEY:-}"
GOSTREAM_FUSE_PATH="/var/gostream/gostream-mkv-virtual"

# The dev host fronts a color, never the prod apex. Refuse an obviously-prod
# target up front (belt); the identity guard below is the braces.
PROD_HOST="$(printf '%s' "$PROD_URL" | sed -E 's#^[a-z]+://([^/]+).*#\1#')"
TARGET_HOST="$(printf '%s' "$BASE_URL" | sed -E 's#^[a-z]+://([^/]+).*#\1#')"

log()  { printf '\n=== %s\n' "$*"; }
note() { printf '    %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

# --- temp workspace + guaranteed cleanup --------------------------------
WORK="$(mktemp -d "${TMPDIR:-/tmp}/phantom-incluster.XXXXXX")"
_torn_down=0
teardown() {
    local ec=$?
    [ "$_torn_down" = 1 ] && exit "$ec"
    _torn_down=1
    log "cleanup (guaranteed EXIT trap)"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: rm -rf $WORK"
    else
        rm -rf "$WORK" || true
    fi
    exit "$ec"
}
trap teardown EXIT

log "phantom-library IN-CLUSTER acceptance rig (P3 Stage 5)"
note "target (deployed):   $BASE_URL   (host: $TARGET_HOST)"
note "prod apex (guard):   $PROD_URL   (host: $PROD_HOST)"
note "namespace:           $NS"
note "expected version:    $EXPECT_VER.x"
note "gostream FUSE path:  $GOSTREAM_FUSE_PATH (co-located, one mount ns)"
note "require auth:        $REQUIRE_AUTH"
note "dry run:             $DRYRUN"

if [ "$TARGET_HOST" = "$PROD_HOST" ]; then
    fail "REFUSING: target host $TARGET_HOST IS the prod apex $PROD_HOST — the rig never drives prod Jellyfin"
fi

jf_public() {  # $1 = base url -> writes System/Info/Public json to stdout
    curl -sS --fail -m 20 "$1/System/Info/Public"
}
api()      { curl -sS --fail -m 30 -H "X-Emby-Token: $APIKEY" "$@"; }

# ---------------------------------------------------------------------------
# Phase A — public reachability, color identity, PROD-SAFETY identity guard
# ---------------------------------------------------------------------------
phase_a() {
    log "Phase A — public reachability + prod-safety identity guard"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: curl $BASE_URL/System/Info/Public"
        note "DRYRUN: curl $PROD_URL/System/Info/Public"
        note "DRYRUN: assert target ServerName ~ phantom-library-{blue,green}-*"
        note "DRYRUN: assert target Version ~ $EXPECT_VER.*"
        note "DRYRUN: REFUSE if target.Id == prod.Id"
        return 0
    fi

    jf_public "$BASE_URL" > "$WORK/target.json" || fail "target $BASE_URL /System/Info/Public unreachable"
    local sname ver tid tcolor
    sname="$(sed -n 's/.*"ServerName":"\([^"]*\)".*/\1/p' "$WORK/target.json")"
    ver="$(sed -n 's/.*"Version":"\([^"]*\)".*/\1/p' "$WORK/target.json")"
    tid="$(sed -n 's/.*"Id":"\([^"]*\)".*/\1/p' "$WORK/target.json")"
    note "target ServerName: $sname"
    note "target Version:    $ver"
    note "target Id:         $tid"

    printf '%s' "$sname" | grep -qE '^phantom-library-(blue|green)(-splash)?-' \
        || fail "target ServerName '$sname' is not a phantom-library color deployment"
    printf '%s' "$sname" | grep -q 'splash' \
        && fail "target ServerName '$sname' is the SPLASH pod, not the Jellyfin color deployment"
    case "$ver" in
        "$EXPECT_VER".*|"$EXPECT_VER") : ;;
        *) fail "target Version '$ver' is not $EXPECT_VER.x" ;;
    esac
    [ -n "$tid" ] || fail "target reported an empty server Id"

    # The blue and green colors intentionally SHARE the authoritative library
    # DB (P4 Stage A), so they report the SAME Jellyfin server Id — a server-Id
    # identity guard is therefore meaningless here. The color is what the
    # ServerName (the Pod name) carries, and the color is the correct
    # prod-safety axis.
    tcolor="$(printf '%s' "$sname" | sed -n 's/^phantom-library-\(blue\|green\).*/\1/p')"
    [ -n "$tcolor" ] || fail "could not extract the target color from ServerName '$sname'"
    note "target color:      $tcolor"

    # PROD SAFETY: never drive the color that currently fronts the prod apex.
    # Resolve prod's ACTIVE color from the prod apex ServerName and refuse when
    # the target color matches it.
    if jf_public "$PROD_URL" > "$WORK/prod.json" 2>/dev/null; then
        local psname pcolor
        psname="$(sed -n 's/.*"ServerName":"\([^"]*\)".*/\1/p' "$WORK/prod.json")"
        pcolor="$(printf '%s' "$psname" | sed -n 's/^phantom-library-\(blue\|green\).*/\1/p')"
        note "prod ServerName:   $psname"
        note "prod active color: $pcolor"
        [ -n "$pcolor" ] && [ "$tcolor" = "$pcolor" ] \
            && fail "REFUSING: target color '$tcolor' is the color CURRENTLY fronting prod ($PROD_URL) — the rig never drives the active prod color"
        note "prod-safety: target color '$tcolor' != active prod color '${pcolor:-?}' (ok)"
    else
        note "prod apex $PROD_URL not resolvable for the color guard; continuing on the host-name guard only"
    fi
    note "Phase A OK: target is the non-prod '$tcolor' color, reachable over TLS"
}

# ---------------------------------------------------------------------------
# Phase B — in-cluster introspection: consolidated image + co-located FUSE
# ---------------------------------------------------------------------------
phase_b() {
    log "Phase B — deployed Pod: consolidated image + co-located gostream FUSE"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: kubectl -n $NS get pod -l app.kubernetes.io/name=phantom-library"
        note "DRYRUN: assert the color Pod runs ONE container (consolidated jellyfin-phantom image)"
        note "DRYRUN: kubectl exec <pod> -- mount | grep 'fuse.* on $GOSTREAM_FUSE_PATH'"
        note "DRYRUN: assert $GOSTREAM_FUSE_PATH exposes movies/ and tv/ in the jellyfin mount ns"
        note "DRYRUN: assert PhantomLibrary plugin loaded in the Pod"
        return 0
    fi
    if ! command -v kubectl >/dev/null 2>&1; then
        note "kubectl not available; SKIPPING in-cluster introspection (Phase A + C still gate)."
        return 0
    fi

    local color pod
    color="${PHANTOM_INCLUSTER_COLOR:-}"
    if [ -z "$color" ]; then
        # derive from the target ServerName captured in Phase A
        color="$(sed -n 's/.*"ServerName":"phantom-library-\(blue\|green\)-.*/\1/p' "$WORK/target.json")"
    fi
    [ -n "$color" ] || fail "could not determine the deployed color to introspect"
    note "introspecting color: $color"

    pod="$(kubectl -n "$NS" get pod -l "app.kubernetes.io/instance=phantom-library-$color" \
            --field-selector=status.phase=Running -o name 2>/dev/null | head -n1)"
    if [ -z "$pod" ]; then
        # fall back to a name-prefix match (label schema may vary by chart rev)
        pod="pod/$(kubectl -n "$NS" get pods -o name 2>/dev/null \
                    | sed -n "s#^pod/\(phantom-library-$color-[a-z0-9]\+-[a-z0-9]\+\)\$#\1#p" \
                    | grep -v splash | head -n1)"
        [ "$pod" = "pod/" ] && fail "no Running Pod found for color $color in ns $NS"
    fi
    note "pod: $pod"

    # exactly ONE container = consolidated jellyfin-phantom image (Stage B)
    local ccount cimg
    ccount="$(kubectl -n "$NS" get "$pod" -o jsonpath='{.spec.containers[*].name}' | wc -w | tr -d ' ')"
    cimg="$(kubectl -n "$NS" get "$pod" -o jsonpath='{.spec.containers[0].image}')"
    note "container count:   $ccount"
    note "container image:   $cimg"
    [ "$ccount" = "1" ] || fail "expected ONE consolidated container, found $ccount (a sidecar split is the abandoned direction)"
    printf '%s' "$cimg" | grep -qi 'jellyfin-phantom' \
        || fail "deployed container image '$cimg' is not the jellyfin-phantom consolidated image"

    # gostream FUSE co-mounted INSIDE the jellyfin container (one mount ns)
    kubectl -n "$NS" exec "$pod" -c jellyfin -- mount > "$WORK/mounts.txt" 2>/dev/null \
        || fail "could not read mounts inside the jellyfin container"
    grep -qE "on $GOSTREAM_FUSE_PATH type fuse" "$WORK/mounts.txt" \
        || fail "gostream FUSE is NOT mounted at $GOSTREAM_FUSE_PATH inside the jellyfin container (co-location broken)"
    note "gostream FUSE co-mounted inside jellyfin container: $(grep -E "on $GOSTREAM_FUSE_PATH type fuse" "$WORK/mounts.txt" | head -n1)"

    kubectl -n "$NS" exec "$pod" -c jellyfin -- ls "$GOSTREAM_FUSE_PATH" > "$WORK/fuse-ls.txt" 2>/dev/null \
        || fail "could not list $GOSTREAM_FUSE_PATH inside the jellyfin container"
    grep -qx 'movies' "$WORK/fuse-ls.txt" || fail "$GOSTREAM_FUSE_PATH is missing movies/ (virtual-MKV movie root)"
    grep -qx 'tv' "$WORK/fuse-ls.txt"     || fail "$GOSTREAM_FUSE_PATH is missing tv/ (virtual-MKV show root)"
    note "co-located virtual-MKV roots present: movies/ tv/"

    # PhantomLibrary plugin loaded in the Pod
    kubectl -n "$NS" exec "$pod" -c jellyfin -- ls /var/lib/jellyfin/plugins > "$WORK/plugins.txt" 2>/dev/null \
        || fail "could not list plugins inside the jellyfin container"
    grep -qiE 'PhantomLibrary' "$WORK/plugins.txt" \
        || fail "PhantomLibrary plugin is NOT loaded in the deployed Pod"
    note "PhantomLibrary plugin loaded: $(grep -iE 'PhantomLibrary' "$WORK/plugins.txt" | head -n1)"
    note "Phase B OK: consolidated image + co-located gostream FUSE + plugin verified live"
}

# ---------------------------------------------------------------------------
# Phase C — authenticated e2e against the DEPLOYED stack (movie/TV/per-user)
# ---------------------------------------------------------------------------
phase_c() {
    log "Phase C — authenticated e2e (movie 35 / TV 36 / per-user show-hide 42) vs the deployed stack"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: api $BASE_URL/Plugins  -> assert PhantomLibrary present"
        note "DRYRUN: api $BASE_URL/Channels -> enumerate Phantom channels"
        note "DRYRUN: scenario 35 (movie e2e playback) vs $BASE_URL"
        note "DRYRUN: scenario 36 (TV episode e2e playback) vs $BASE_URL"
        note "DRYRUN: scenario 42 (per-user show/hide) vs $BASE_URL"
        return 0
    fi
    if [ -z "$APIKEY" ]; then
        if [ "$REQUIRE_AUTH" = 1 ]; then
            fail "PHANTOM_INCLUSTER_APIKEY is unset but PHANTOM_REQUIRE_AUTH=1 — the acceptance bar requires the authenticated e2e phase"
        fi
        note "PHANTOM_INCLUSTER_APIKEY unset and PHANTOM_REQUIRE_AUTH!=1; SKIPPING authenticated e2e."
        note "(Phase A + B already proved the deployed stack, TLS, color topology, consolidated image + co-located FUSE, and the loaded plugin live.)"
        return 0
    fi

    api "$BASE_URL/Plugins" -o "$WORK/plugins.json" || fail "GET /Plugins failed (bad API key?)"
    grep -qi 'PhantomLibrary' "$WORK/plugins.json" || fail "/Plugins does not list PhantomLibrary on the deployed stack"
    note "deployed /Plugins lists PhantomLibrary"

    api "$BASE_URL/Channels" -o "$WORK/channels.json" || fail "GET /Channels failed"
    grep -qi 'Phantom' "$WORK/channels.json" || fail "no Phantom channel enumerated on the deployed stack"
    note "Phantom channel(s) enumerate on the deployed stack"

    # Drive the movie/TV/per-user acceptance scenarios AGAINST THE DEPLOYED
    # stack. Each scenario honours PHANTOM_TARGET_API / PHANTOM_TARGET_TOKEN
    # to run in remote-target mode against the deployed ingress instead of a
    # local rig.
    local s
    for s in \
        tools/rig-scenarios/35-channel-e2e-playback.sh \
        tools/rig-scenarios/36-channel-episode-e2e-playback.sh \
        tools/rig-scenarios/42-per-user-show-hide.sh
    do
        log "scenario (deployed target): $s"
        PHANTOM_REPO_ROOT="$REPO_ROOT" \
        PHANTOM_TARGET_API="$BASE_URL" \
        PHANTOM_TARGET_TOKEN="$APIKEY" \
            bash "$s"
    done
    note "Phase C OK: movie/TV e2e playback + per-user show/hide passed against the deployed stack"
}

phase_a
phase_b
phase_c

log "in-cluster acceptance rig PASSED"
# EXIT trap cleans the temp workspace next.
