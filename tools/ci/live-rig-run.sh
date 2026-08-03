#!/usr/bin/env bash
# tools/ci/live-rig-run.sh
#
# Gitea Actions live-rig gate for the Phantom Library Jellyfin plugin. Stands
# up the live integration rig (tools/rig-scenarios/rig-up.sh) against the
# REAL gostream + patched-jellyfin artifacts the sibling pipelines publish to
# the Gitea OCI registry, runs the movie/TV/source-safety scenarios that
# prove the channel/materialise/source-management flows end to end, and
# tears everything down — never touching production Jellyfin (:8096) or
# gostream.
#
# Runs on the self-hosted Gitea Actions runner (flux-deployed; see
# .gitea/workflows/live-rig.yaml), inside a container carrying the pinned
# .NET SDK + podman/buildah so the rig images can be pulled/extracted without
# depending on host-installed tooling.
#
#   1. Pull the pinned gostream + patched-jellyfin images from the registry
#      (git.spencerharmon.com/zuul/{gostream,jellyfin-phantom}:<tag> — the
#      `zuul` path segment is a legacy registry namespace only; a rename is
#      flux's to make, not this repo's).
#   2. Extract the built artifacts each image carries (the gostream binary,
#      the patched Jellyfin server + assemblies) onto the runner's
#      filesystem at exactly the paths tools/rig-scenarios/rig-up.sh expects,
#      via `podman create` + `podman cp` (no running container needed for
#      artifacts that are just files baked into the image).
#   3. Build the plugin DLL itself (`dotnet build -c Release`) — the plugin
#      is THIS repo's code, never pulled from a registry.
#   4. `rig-up.sh --reset`, run the live scenarios, `rig-down.sh`, all under
#      a trap so a mid-run failure never leaves rig processes or the runner
#      workspace dirty.
#
# Knobs (env):
#   PHANTOM_CI_DRYRUN=1            echo the heavy steps instead of running
#                                    them (toolchain-agnostic dry run; no
#                                    podman/dotnet/network/systemd needed).
#                                    Used by the in-repo regression check.
#   PHANTOM_REGISTRY=<host/ns>      default git.spencerharmon.com/zuul
#   PHANTOM_GOSTREAM_TAG=<tag>      default: value of PHANTOM_RIG_TAG, else
#                                    "main"
#   PHANTOM_JELLYFIN_IMAGE_TAG=<tag> default: value of PHANTOM_RIG_TAG, else
#                                    "main"
#   PHANTOM_RIG_TAG=<tag>           shared fallback tag for both images when
#                                    the per-image vars above are unset
#   PHANTOM_REPO_ROOT=<path>        repo root the rig scripts operate against
#                                    (default: this script's repo)
#   PHANTOM_RIG_SCENARIOS=<list>    space-separated scenario scripts to run
#                                    (default: the movie/TV/source-safety
#                                    trio below)
#
# Scenarios run (movie/TV parity + the M14 source-safety live-rig proof this
# job exists to gate):
#   35-channel-e2e-playback.sh            movie browse/playback/materialise
#   36-channel-episode-e2e-playback.sh    TV series/season/episode parity
#   39-channel-source-safety.sh           REQ-M14 source-safety live proof
#
# Never touches production: the rig binds :18096 (Jellyfin) / :19080
# (gostream), production owns :8096 / its own gostream instance. rig-down.sh
# runs in the EXIT trap unconditionally.
set -euo pipefail

REPO_ROOT="${PHANTOM_REPO_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
cd "$REPO_ROOT"

DRYRUN="${PHANTOM_CI_DRYRUN:-0}"
REGISTRY="${PHANTOM_REGISTRY:-git.spencerharmon.com/zuul}"
RIG_TAG_FALLBACK="${PHANTOM_RIG_TAG:-main}"
GOSTREAM_TAG="${PHANTOM_GOSTREAM_TAG:-$RIG_TAG_FALLBACK}"
JELLYFIN_IMAGE_TAG="${PHANTOM_JELLYFIN_IMAGE_TAG:-$RIG_TAG_FALLBACK}"
GOSTREAM_IMAGE="$REGISTRY/gostream:$GOSTREAM_TAG"
JELLYFIN_IMAGE="$REGISTRY/jellyfin-phantom:$JELLYFIN_IMAGE_TAG"

# Prod safety: never let a misconfigured env point this at prod ports.
RIG_JF_PORT=18096
RIG_GOSTREAM_PORT=19080
[ "$RIG_JF_PORT" != 8096 ] || { echo "REFUSING: rig Jellyfin port must not be 8096 (prod)" >&2; exit 1; }

DEFAULT_SCENARIOS="tools/rig-scenarios/35-channel-e2e-playback.sh tools/rig-scenarios/36-channel-episode-e2e-playback.sh tools/rig-scenarios/39-channel-source-safety.sh"
# shellcheck disable=SC2206
SCENARIOS=(${PHANTOM_RIG_SCENARIOS:-$DEFAULT_SCENARIOS})

log()  { printf '\n=== %s\n' "$*"; }
note() { printf '    %s\n' "$*"; }

log "phantom-library live-rig gate"
note "repo:                $REPO_ROOT"
note "gostream image:      $GOSTREAM_IMAGE"
note "jellyfin image:      $JELLYFIN_IMAGE"
note "rig jellyfin port:   $RIG_JF_PORT (prod owns 8096, never touched)"
note "rig gostream port:   $RIG_GOSTREAM_PORT"
note "scenarios:           ${SCENARIOS[*]}"
note "dry run:             $DRYRUN"

# --- guaranteed teardown, always runs -----------------------------------
_torn_down=0
teardown() {
    local ec=$?
    [ "$_torn_down" = 1 ] && exit "$ec"
    _torn_down=1
    log "tearing down rig (guaranteed EXIT trap)"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: bash tools/rig-scenarios/rig-down.sh"
        note "DRYRUN: podman rm -f phantom-rig-gostream"
    else
        bash tools/rig-scenarios/rig-down.sh || true
        podman rm -f phantom-rig-gostream >/dev/null 2>&1 || true
    fi
    exit "$ec"
}
trap teardown EXIT

# --- 1. pull the pinned images ------------------------------------------
pull_images() {
    log "pulling pinned rig images from the Gitea OCI registry"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: podman pull $GOSTREAM_IMAGE"
        note "DRYRUN: podman pull $JELLYFIN_IMAGE"
        return 0
    fi
    podman pull "$GOSTREAM_IMAGE"
    podman pull "$JELLYFIN_IMAGE"
}

# --- 2. extract the patched-jellyfin artifact ---------------------------
# The rig (tools/rig-scenarios/rig-up.sh) expects a built, patched Jellyfin
# server at jellyfin/Jellyfin.Server/bin/Release/net9.0/jellyfin.dll plus its
# sibling assemblies. Rather than rebuild patched Jellyfin from source on
# every rig run, extract the already-built tree the jellyfin-phantom image
# carries (produced by the sibling jellyfin pipeline) via a throwaway
# container — no image run required, just its filesystem contents.
extract_jellyfin() {
    log "extracting patched-jellyfin build tree from $JELLYFIN_IMAGE"
    local dest="$REPO_ROOT/jellyfin/Jellyfin.Server/bin/Release/net9.0"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: podman create --name phantom-rig-jf-extract $JELLYFIN_IMAGE"
        note "DRYRUN: mkdir -p $dest"
        note "DRYRUN: podman cp phantom-rig-jf-extract:/app/. $dest"
        note "DRYRUN: podman rm phantom-rig-jf-extract"
        return 0
    fi
    mkdir -p "$dest"
    podman create --name phantom-rig-jf-extract "$JELLYFIN_IMAGE" >/dev/null
    podman cp phantom-rig-jf-extract:/app/. "$dest"
    podman rm phantom-rig-jf-extract >/dev/null
}

# --- 3. run the real gostream image as a container ----------------------
# Replaces tools/rig-scenarios/gostream-mock.py for this job: the rig talks
# to the REAL gostream image on :19080 instead of the Python mock, proving
# the actual gostream contract rather than a hand-maintained stand-in.
start_gostream() {
    log "starting real gostream container on :$RIG_GOSTREAM_PORT"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: podman run -d --name phantom-rig-gostream -p 127.0.0.1:$RIG_GOSTREAM_PORT:8090 $GOSTREAM_IMAGE"
        return 0
    fi
    podman rm -f phantom-rig-gostream >/dev/null 2>&1 || true
    podman run -d --name phantom-rig-gostream \
        -p "127.0.0.1:$RIG_GOSTREAM_PORT:8090" \
        "$GOSTREAM_IMAGE" >/dev/null
    for i in $(seq 1 20); do
        if curl -s -o /dev/null -w '%{http_code}' -X OPTIONS "http://127.0.0.1:$RIG_GOSTREAM_PORT/api/library/add" | grep -q 405; then
            note "gostream container up"
            return 0
        fi
        sleep 0.5
    done
    echo "ERROR: real gostream container did not become healthy on :$RIG_GOSTREAM_PORT" >&2
    return 1
}

# --- 4. build the plugin -------------------------------------------------
build_plugin() {
    log "dotnet build -c Release (plugin)"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: dotnet build src/Jellyfin.Plugin.PhantomLibrary/Jellyfin.Plugin.PhantomLibrary.csproj -c Release -p:UseSharedCompilation=false"
        return 0
    fi
    MSBUILDDISABLENODEREUSE=1 dotnet build \
        src/Jellyfin.Plugin.PhantomLibrary/Jellyfin.Plugin.PhantomLibrary.csproj \
        -c Release -p:UseSharedCompilation=false
}

# --- 5. rig up + scenarios + rig down (down happens in the EXIT trap) ---
run_rig() {
    log "rig-up.sh --reset"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: PHANTOM_REPO_ROOT=$REPO_ROOT bash tools/rig-scenarios/rig-up.sh --reset"
    else
        PHANTOM_REPO_ROOT="$REPO_ROOT" bash tools/rig-scenarios/rig-up.sh --reset
    fi

    local s
    for s in "${SCENARIOS[@]}"; do
        log "scenario: $s"
        if [ "$DRYRUN" = 1 ]; then
            note "DRYRUN: PHANTOM_REPO_ROOT=$REPO_ROOT bash $s"
        else
            PHANTOM_REPO_ROOT="$REPO_ROOT" bash "$s"
        fi
    done
}

pull_images
extract_jellyfin
start_gostream
build_plugin
run_rig

log "live-rig gate PASSED"
# EXIT trap runs rig-down.sh + gostream container teardown next.
