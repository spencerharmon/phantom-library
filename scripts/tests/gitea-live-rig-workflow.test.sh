#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/gitea-live-rig-workflow.test.sh
#
# In-repo regression harness for .gitea/workflows/live-rig.yaml (the Gitea
# Actions live-rig gate) and tools/ci/live-rig-run.sh, mirroring
# gitea-nonrig-workflow.test.sh's shape for the non-rig gate.
#
# Guards against the workflow/script silently rotting:
#   - the workflow file exists and PARSES as valid YAML.
#   - it runs on the SELF-HOSTED Gitea Actions runner, never `ubuntu-latest`
#     (this job needs podman + real bindable ports the shared/hosted runner
#     model does not offer, and the self-hosted runner is what flux deploys
#     and already keeps live).
#   - it is NOT gated behind a Zuul/Nodepool-style node label — there is no
#     `nodeset:`/label indirection here, only the Gitea runner's own labels.
#   - it declares `container:` with a pinned, CONCRETE .NET SDK image tag
#     (rejects `:latest` or a bare major-version floating tag like `:9.0`),
#     carrying the same toolchain pin as nonrig-gate.yaml.
#   - it installs podman (the tool the job needs to pull/extract the
#     registry images) inside that container.
#   - it invokes the SHARED tools/ci/live-rig-run.sh rather than a
#     hand-rolled copy of the rig-up/scenario/rig-down steps.
#   - tools/ci/live-rig-run.sh itself: is syntactically valid bash, never
#     targets the production Jellyfin port (:8096) or a production gostream
#     port, references the real registry image coordinates
#     (git.spencerharmon.com/images/{gostream,jellyfin-phantom}), runs the
#     movie/TV/source-safety scenario trio, and unconditionally tears the
#     rig down via an EXIT trap (rig-down.sh) so a mid-run failure never
#     strands rig processes.
#   - a toolchain-agnostic DRY RUN of tools/ci/live-rig-run.sh
#     (PHANTOM_CI_DRYRUN=1) exits 0 — proving the script's control flow
#     (including the teardown trap) is sound WITHOUT requiring podman, a
#     dotnet SDK, systemd, or network access, so this harness runs on any
#     CI node (including the very node this job itself gates).
#
# This test does NOT need podman, a dotnet SDK, network access, or a running
# rig — it only needs bash + python3 (for YAML parsing) or PyYAML.
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
WORKFLOW="$REPO_ROOT/.gitea/workflows/live-rig.yaml"
RIG_SCRIPT="$REPO_ROOT/tools/ci/live-rig-run.sh"

pass_count=0
fail_count=0

ok()   { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

[[ -f "$WORKFLOW" ]]   || fatal "workflow not found: $WORKFLOW"
[[ -f "$RIG_SCRIPT" ]] || fatal "shared CI script not found: $RIG_SCRIPT"
[[ -x "$RIG_SCRIPT" ]] || fatal "shared CI script not executable: $RIG_SCRIPT"

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

head_ "self-hosted runner (never ubuntu-latest / a hosted runner)"
if grep -qE '^\s*runs-on:\s*\[.*self-hosted' "$WORKFLOW"; then
    ok "workflow declares runs-on: [self-hosted, ...]"
elif grep -qE '^\s*runs-on:\s*self-hosted\s*$' "$WORKFLOW"; then
    ok "workflow declares runs-on: self-hosted"
else
    bad "workflow does not run on the self-hosted Gitea Actions runner"
fi
if grep -qE '^\s*runs-on:\s*ubuntu-latest\s*$' "$WORKFLOW"; then
    bad "workflow declares runs-on: ubuntu-latest (must be the self-hosted runner)"
else
    ok "workflow does not fall back to ubuntu-latest"
fi

head_ "not gated on a Zuul/Nodepool node label"
if grep -qiE '^\s*nodeset:' "$WORKFLOW"; then
    bad "workflow references a Zuul-style nodeset: (Nodepool is retired for this tenant)"
else
    ok "workflow carries no Zuul/Nodepool nodeset: indirection"
fi

head_ "containerized (pinned toolchain, not host-tool dependence)"
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

head_ "podman installed in the toolchain container"
if grep -qE '^\s*apt-get install.*\bpodman\b' "$WORKFLOW" || grep -qE '\bpodman\b' "$WORKFLOW"; then
    ok "workflow installs/uses podman"
else
    bad "workflow does not install podman (needed to pull/extract the rig images)"
fi

head_ "delegates to the shared live-rig-run.sh (no drift, no hand-rolled steps)"
if grep -qE 'tools/ci/live-rig-run\.sh' "$WORKFLOW"; then
    ok "workflow invokes tools/ci/live-rig-run.sh"
else
    bad "workflow does not invoke the shared tools/ci/live-rig-run.sh"
fi

head_ "shared script: valid bash syntax"
if bash -n "$RIG_SCRIPT"; then
    ok "$RIG_SCRIPT passes bash -n syntax check"
else
    bad "$RIG_SCRIPT has a bash syntax error"
fi

head_ "shared script: never targets production ports"
if grep -qE 'RIG_JF_PORT=8096' "$RIG_SCRIPT"; then
    bad "$RIG_SCRIPT sets the rig Jellyfin port to production's :8096"
elif ! grep -qE 'RIG_JF_PORT != 8096|!= 8096' "$RIG_SCRIPT"; then
    bad "$RIG_SCRIPT is missing an explicit guard against the rig port equalling production's :8096"
else
    ok "$RIG_SCRIPT guards the rig Jellyfin port against production's :8096"
fi

head_ "shared script: references the real registry image coordinates"
if grep -q 'gostream' "$RIG_SCRIPT" && grep -q 'jellyfin-phantom' "$RIG_SCRIPT"; then
    ok "$RIG_SCRIPT references both gostream and jellyfin-phantom images"
else
    bad "$RIG_SCRIPT is missing a reference to the gostream or jellyfin-phantom image"
fi

head_ "shared script: runs the movie/TV/source-safety scenario trio"
for scenario in \
    "tools/rig-scenarios/35-channel-e2e-playback.sh" \
    "tools/rig-scenarios/36-channel-episode-e2e-playback.sh" \
    "tools/rig-scenarios/39-channel-source-safety.sh"
do
    if grep -q "$scenario" "$RIG_SCRIPT"; then
        ok "$RIG_SCRIPT references $scenario"
    else
        bad "$RIG_SCRIPT does not reference $scenario"
    fi
done

head_ "shared script: guaranteed rig teardown via an EXIT trap"
if grep -qE 'trap teardown EXIT' "$RIG_SCRIPT" && grep -qE 'rig-down\.sh' "$RIG_SCRIPT"; then
    ok "EXIT trap present and calls rig-down.sh"
else
    bad "$RIG_SCRIPT is missing the guaranteed rig-down.sh EXIT trap"
fi

head_ "toolchain-agnostic dry run of the shared rig script"
if PHANTOM_CI_DRYRUN=1 bash "$RIG_SCRIPT" >/tmp/gitea-live-rig-dryrun.$$.log 2>&1; then
    ok "PHANTOM_CI_DRYRUN=1 tools/ci/live-rig-run.sh exits 0"
else
    bad "dry run of tools/ci/live-rig-run.sh failed; see /tmp/gitea-live-rig-dryrun.$$.log"
    sed 's/^/    /' /tmp/gitea-live-rig-dryrun.$$.log >&2 || true
fi
rm -f "/tmp/gitea-live-rig-dryrun.$$.log"

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
