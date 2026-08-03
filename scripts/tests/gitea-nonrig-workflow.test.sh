#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/gitea-nonrig-workflow.test.sh
#
# In-repo regression harness for .gitea/workflows/nonrig-gate.yaml (the
# Gitea Actions non-rig gate, replacing the obsolete Zuul `zuul-nonrig-gate`).
#
# Guards against the workflow silently rotting:
#   - the workflow file exists and PARSES as valid YAML.
#   - it declares `container:` (runs inside a container, not bare on the
#     Actions host) with a pinned, CONCRETE .NET SDK image tag (rejects
#     `:latest` or a bare major-version floating tag like `:9.0`).
#   - it invokes the SHARED tools/ci/nonrig-build-test.sh script rather than
#     a hand-rolled copy of the build/test steps, so the Zuul job and the
#     Gitea job can never drift apart.
#   - tools/ci/nonrig-build-test.sh itself still carries the mandatory
#     build/test process-cleanup contract: MSBUILDDISABLENODEREUSE=1,
#     -p:UseSharedCompilation=false, and an EXIT trap that verifies no
#     leftover dotnet/testhost/VBCSCompiler/MSBuild process survives.
#   - a toolchain-agnostic DRY RUN of tools/ci/nonrig-build-test.sh
#     (PHANTOM_CI_DRYRUN=1) exits 0 — proving the script's control flow
#     (including the cleanup trap) is sound WITHOUT requiring a dotnet SDK
#     or network access, so this harness runs on any CI node.
#
# This test does NOT need a dotnet SDK, network access, or a container
# runtime — it only needs bash + python3 (for YAML parsing) or PyYAML.
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
WORKFLOW="$REPO_ROOT/.gitea/workflows/nonrig-gate.yaml"
BUILD_SCRIPT="$REPO_ROOT/tools/ci/nonrig-build-test.sh"

pass_count=0
fail_count=0

ok()   { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

[[ -f "$WORKFLOW" ]]     || fatal "workflow not found: $WORKFLOW"
[[ -f "$BUILD_SCRIPT" ]] || fatal "shared CI script not found: $BUILD_SCRIPT"
[[ -x "$BUILD_SCRIPT" ]] || fatal "shared CI script not executable: $BUILD_SCRIPT"

head_ "YAML parse"
if command -v python3 >/dev/null 2>&1; then
    if python3 - "$WORKFLOW" <<'PY'
import sys
try:
    import yaml
except ImportError:
    sys.exit(3)
with open(sys.argv[1]) as f:
    try:
        yaml.safe_load(f)
    except Exception as e:
        print(f"YAML parse error: {e}", file=sys.stderr)
        sys.exit(1)
sys.exit(0)
PY
    then
        ok "$WORKFLOW parses as valid YAML (PyYAML)"
    else
        rc=$?
        if [ "$rc" -eq 3 ]; then
            printf '  NOTE: PyYAML unavailable; falling back to a structural grep check.\n'
            # Toolchain-agnostic fallback: basic sanity that this looks like a
            # YAML mapping document and not garbage (no tabs, has top-level keys).
            if grep -qP '^\S.*:\s*$|^\S.*:\s*\S' "$WORKFLOW" && ! grep -qP '\t' "$WORKFLOW"; then
                ok "$WORKFLOW passes structural YAML sanity check (no PyYAML)"
            else
                bad "$WORKFLOW failed structural YAML sanity check"
            fi
        else
            bad "$WORKFLOW is not valid YAML"
        fi
    fi
else
    fatal "python3 not available; cannot YAML-lint the workflow"
fi

head_ "containerized (no host-tool dependence)"
if grep -qE '^\s*container:\s*$' "$WORKFLOW"; then
    ok "workflow declares a container: block"
else
    bad "workflow does not declare container: (would depend on host-installed tools)"
fi

head_ "pinned, concrete SDK image tag"
IMAGE_LINE="$(grep -E '^\s*image:\s*mcr\.microsoft\.com/dotnet/sdk:' "$WORKFLOW" || true)"
if [ -z "$IMAGE_LINE" ]; then
    bad "no mcr.microsoft.com/dotnet/sdk image: line found"
else
    TAG="$(printf '%s' "$IMAGE_LINE" | sed -n 's/.*dotnet\/sdk:\([^[:space:]]*\).*/\1/p')"
    if [ -z "$TAG" ] || [ "$TAG" = "latest" ]; then
        bad "SDK image tag is missing or floating 'latest': '$TAG'"
    elif printf '%s' "$TAG" | grep -qE '^[0-9]+\.[0-9]+$'; then
        bad "SDK image tag '$TAG' is a floating major.minor tag, not a concrete pin"
    else
        ok "SDK image pinned to concrete tag: $TAG"
    fi
fi

head_ "delegates to the shared nonrig-build-test.sh (no drift vs Zuul)"
if grep -qE 'tools/ci/nonrig-build-test\.sh' "$WORKFLOW"; then
    ok "workflow invokes tools/ci/nonrig-build-test.sh"
else
    bad "workflow does not invoke the shared tools/ci/nonrig-build-test.sh"
fi

head_ "shared script still carries the mandatory cleanup contract"
if grep -q 'MSBUILDDISABLENODEREUSE=1' "$BUILD_SCRIPT"; then
    ok "MSBUILDDISABLENODEREUSE=1 present"
else
    bad "MSBUILDDISABLENODEREUSE=1 missing from $BUILD_SCRIPT"
fi
if grep -q -- '-p:UseSharedCompilation=false' "$BUILD_SCRIPT"; then
    ok "-p:UseSharedCompilation=false present"
else
    bad "-p:UseSharedCompilation=false missing from $BUILD_SCRIPT"
fi
if grep -q 'trap cleanup EXIT' "$BUILD_SCRIPT"; then
    ok "EXIT cleanup trap present"
else
    bad "EXIT cleanup trap missing from $BUILD_SCRIPT"
fi

head_ "toolchain-agnostic dry run of the shared CI script"
if PHANTOM_CI_DRYRUN=1 PHANTOM_CI_PKILL=0 bash "$BUILD_SCRIPT" >/tmp/gitea-nonrig-dryrun.$$.log 2>&1; then
    ok "PHANTOM_CI_DRYRUN=1 tools/ci/nonrig-build-test.sh exits 0"
else
    bad "dry run of tools/ci/nonrig-build-test.sh failed; see /tmp/gitea-nonrig-dryrun.$$.log"
    sed 's/^/    /' /tmp/gitea-nonrig-dryrun.$$.log >&2 || true
fi
rm -f "/tmp/gitea-nonrig-dryrun.$$.log"

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
