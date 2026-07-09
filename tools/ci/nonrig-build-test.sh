#!/usr/bin/env bash
# tools/ci/nonrig-build-test.sh
#
# Non-rig CI gate for the Phantom Library Jellyfin plugin. Runs identically in
# Zuul (playbooks/phantom-library-nonrig-build-test.yaml) and locally.
#
#   1. Restore the pinned, PATCHED Jellyfin source tree the plugin compiles
#      against. The plugin/test csprojs ProjectReference ../../jellyfin/* (the
#      additive IChannelItemRefresh{,Manager} patches), so a bare
#      `dotnet build` fails without it. We clone jellyfin at the pinned tag
#      (single source of truth: scripts/jellyfin-patches/REBASE.md) into
#      jellyfin/ and apply scripts/jellyfin-patches/*.patch, mirroring
#      install.sh's --build patch step.
#   2. `dotnet build -c Release`  then  `dotnet test`  — with MSBuild node
#      reuse and shared compilation DISABLED (no reusable build servers).
#   3. An EXIT trap shuts the dotnet build servers down and VERIFIES no stray
#      dotnet/testhost/VBCSCompiler/MSBuild process survives.
#
# Knobs (env):
#   PHANTOM_CI_DRYRUN=1          echo the heavy steps instead of running them
#                                (toolchain-agnostic dry run; used by the
#                                 in-repo regression check). Skips clone/build/
#                                 test/pkill but still exercises control flow +
#                                 the cleanup trap.
#   PHANTOM_JELLYFIN_DIR=<path>  where to place the jellyfin/ clone (default
#                                 <repo>/jellyfin)
#   PHANTOM_JELLYFIN_REPO=<url>  clone URL (default upstream jellyfin/jellyfin)
#   PHANTOM_JELLYFIN_TAG=<tag>   pinned tag (default: parsed from REBASE.md,
#                                 fallback v10.11.9)
#   PHANTOM_CI_RELAX_SDK=1       rewrite the THROWAWAY jellyfin/global.json
#                                 rollForward to latestMajor so a >=9 SDK is
#                                 accepted when a 9.x SDK is absent (default 1;
#                                 only ever touches the ephemeral CI clone)
#   PHANTOM_CI_STRICT_LEFTOVERS  fail the job if build servers survive cleanup
#                                 (default 1)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

# shellcheck source=tools/ci/lib-cleanup.sh
. "$REPO_ROOT/tools/ci/lib-cleanup.sh"

DRYRUN="${PHANTOM_CI_DRYRUN:-0}"
PATCHES_DIR="$REPO_ROOT/scripts/jellyfin-patches"
JELLYFIN_DIR="${PHANTOM_JELLYFIN_DIR:-$REPO_ROOT/jellyfin}"
JELLYFIN_REPO="${PHANTOM_JELLYFIN_REPO:-https://github.com/jellyfin/jellyfin.git}"
RELAX_SDK="${PHANTOM_CI_RELAX_SDK:-1}"

# --- Mandatory MSBuild process-cleanup contract -----------------------------
# No reusable build servers: disable MSBuild node reuse and Roslyn shared
# compilation so nothing long-lived is spawned in the first place.
export MSBUILDDISABLENODEREUSE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
DOTNET_FLAGS=(-c Release -p:UseSharedCompilation=false)

# EXIT trap: guaranteed cleanup + leftover verification on every exit path.
cleanup() {
    local ec=$?
    if ! phantom_ci_cleanup_dotnet "$ec"; then
        # cleanup found surviving build servers in strict mode; surface it, but
        # never turn an already-failing run green.
        [ "$ec" -eq 0 ] && ec=1
    fi
    exit "$ec"
}
trap cleanup EXIT

log()  { printf '\n=== %s\n' "$*"; }
note() { printf '    %s\n' "$*"; }

# Pinned Jellyfin tag — REBASE.md is the source of truth.
derive_tag() {
    local t=""
    if [ -f "$PATCHES_DIR/REBASE.md" ]; then
        t="$(sed -n 's/.*tag `\(v10\.11\.[0-9][0-9]*\)`.*/\1/p' \
             "$PATCHES_DIR/REBASE.md" | head -1)"
    fi
    printf '%s' "${PHANTOM_JELLYFIN_TAG:-${t:-v10.11.9}}"
}
JELLYFIN_TAG="$(derive_tag)"

log "phantom-library non-rig gate"
note "repo:            $REPO_ROOT"
note "jellyfin clone:  $JELLYFIN_DIR"
note "jellyfin repo:   $JELLYFIN_REPO"
note "jellyfin tag:    $JELLYFIN_TAG"
note "dotnet flags:    ${DOTNET_FLAGS[*]}"
note "MSBUILDDISABLENODEREUSE=$MSBUILDDISABLENODEREUSE"
note "dry run:         $DRYRUN"

# --- 1. Restore the patched Jellyfin source tree ----------------------------
restore_jellyfin() {
    log "restoring patched Jellyfin source tree ($JELLYFIN_TAG)"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: git clone --depth 1 --branch $JELLYFIN_TAG $JELLYFIN_REPO $JELLYFIN_DIR"
        note "DRYRUN: git -C $JELLYFIN_DIR apply <scripts/jellyfin-patches/*.patch>"
        return 0
    fi

    if [ ! -d "$JELLYFIN_DIR/.git" ]; then
        git clone --depth 1 --branch "$JELLYFIN_TAG" "$JELLYFIN_REPO" "$JELLYFIN_DIR"
    else
        note "existing jellyfin/ checkout — resetting to $JELLYFIN_TAG"
        git -C "$JELLYFIN_DIR" fetch --depth 1 origin "refs/tags/$JELLYFIN_TAG:refs/tags/$JELLYFIN_TAG" 2>/dev/null || true
        git -C "$JELLYFIN_DIR" reset --hard "$JELLYFIN_TAG"
        git -C "$JELLYFIN_DIR" clean -fd
    fi

    # Apply the additive patches in lexicographic order (mirrors install.sh).
    local patch name
    for patch in "$PATCHES_DIR"/*.patch; do
        name="$(basename "$patch")"
        if git -C "$JELLYFIN_DIR" apply --check "$patch" 2>/dev/null; then
            git -C "$JELLYFIN_DIR" apply "$patch"
            note "applied: $name"
        elif git -C "$JELLYFIN_DIR" apply --check -R "$patch" 2>/dev/null; then
            note "already applied: $name"
        else
            echo "ERROR: patch $name does not apply cleanly to jellyfin/ at" \
                 "$(git -C "$JELLYFIN_DIR" rev-parse --short HEAD)." >&2
            echo "       Rebase the patches — see scripts/jellyfin-patches/REBASE.md" >&2
            return 1
        fi
    done

    # The CI clone is throwaway: relax its SDK pin so whatever >=9 SDK the node
    # ships is accepted (the operator box has the matching 9.x SDK). Never
    # committed anywhere — it only edits the ephemeral clone.
    if [ "$RELAX_SDK" = 1 ] && [ -f "$JELLYFIN_DIR/global.json" ]; then
        if command -v python3 >/dev/null 2>&1; then
            python3 - "$JELLYFIN_DIR/global.json" <<'PY' || true
import json, sys
p = sys.argv[1]
with open(p) as f:
    d = json.load(f)
sdk = d.get("sdk")
if isinstance(sdk, dict):
    sdk["rollForward"] = "latestMajor"
    with open(p, "w") as f:
        json.dump(d, f, indent=2)
        f.write("\n")
    print("    relaxed jellyfin/global.json rollForward -> latestMajor")
PY
        fi
    fi
}

# --- 2. build + test --------------------------------------------------------
build_and_test() {
    log "dotnet build -c Release"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: dotnet build ${DOTNET_FLAGS[*]}"
    else
        dotnet build "${DOTNET_FLAGS[@]}"
    fi

    log "dotnet test"
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: dotnet test ${DOTNET_FLAGS[*]} --no-build"
    else
        # Reuse the build output; keep the same no-reusable-server flags.
        dotnet test "${DOTNET_FLAGS[@]}" --no-build
    fi
}

# --- 0. In-repo additive-migration regression (sqlite3 only, no toolchain) --
# Exercises scripts/phantom-migrate-v11-to-v12.sh against a clone of a
# synthetic v11 phantom.db: dry-run no-op, --commit migrate, idempotency,
# resumability, and the user_version guard. Needs only bash + sqlite3, so it
# runs before the heavy Jellyfin clone/build and fails the gate fast. Skips
# (green) if sqlite3 is unavailable so it never breaks a node lacking it.
migration_regression() {
    local t="$REPO_ROOT/scripts/tests/phantom-migrate-v11-to-v12.test.sh"
    log "additive-migration regression (phantom-migrate-v11-to-v12)"
    if [ ! -x "$t" ]; then
        note "SKIP: $t not present/executable"
        return 0
    fi
    if [ "$DRYRUN" = 1 ]; then
        note "DRYRUN: bash $t"
        return 0
    fi
    if ! command -v sqlite3 >/dev/null 2>&1; then
        note "SKIP: sqlite3 not available on this node"
        return 0
    fi
    bash "$t"
}

migration_regression
restore_jellyfin
build_and_test

log "non-rig gate PASSED"
# EXIT trap runs cleanup + leftover verification next.
