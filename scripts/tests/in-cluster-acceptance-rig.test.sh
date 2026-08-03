#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/in-cluster-acceptance-rig.test.sh
#
# In-repo regression harness for the P3 Stage 5 IN-CLUSTER ACCEPTANCE rig —
# the .gitea/workflows/in-cluster-acceptance.yaml gate and its shared driver
# tools/ci/in-cluster-acceptance-run.sh — mirroring
# gitea-live-rig-workflow.test.sh's shape for the live-rig gate.
#
# This is the task's definition-of-done Check. It is a STRUCTURAL +
# control-flow regression guard that runs anywhere (bash + python3 only, no
# kubectl / curl / cluster / dotnet): it proves the acceptance harness and its
# workflow exist, are wired correctly, carry the prod-safety guards, drive the
# DEPLOYED stack (not a throwaway :18096 rig), assert the consolidated image +
# co-located gostream FUSE + movie/TV/per-user scenarios, and that a
# toolchain-agnostic dry run of the driver exits 0. The ACTUAL authenticated
# live run against the deployed blue/green stack is what the Gitea Actions job
# performs on the self-hosted (in-cluster) runner with the operator's API-key
# secret — this harness guards that machinery from silently rotting.
#
# Guards against the workflow/driver silently rotting:
#   - the workflow file exists and PARSES as valid YAML.
#   - it runs on the SELF-HOSTED Gitea Actions runner, never ubuntu-latest.
#   - it is NOT gated behind a Zuul/Nodepool nodeset label.
#   - it declares container: with a pinned, CONCRETE .NET SDK image tag.
#   - it installs kubectl (Phase B introspection) + curl (drive the ingress).
#   - it invokes the SHARED tools/ci/in-cluster-acceptance-run.sh, not a
#     hand-rolled copy of the steps.
#   - it sets PHANTOM_REQUIRE_AUTH=1 (fail-closed) and passes the API-key
#     secret, and targets the DEV host (not the prod apex).
#   - the driver: is valid bash; targets the DEPLOYED stack over the ingress
#     (dev.jellyfin.polyfam.studio), NOT a local :18096 / :8096 rig; carries
#     the prod-safety identity guard (refuses when the target server Id equals
#     prod's); introspects the CONSOLIDATED single container + the co-located
#     gostream FUSE at /var/gostream/gostream-mkv-virtual; runs the movie/TV/
#     per-user acceptance scenario trio (35/36/42); cleans up via an EXIT trap.
#   - the movie/TV/per-user scenarios honour the PHANTOM_TARGET_API /
#     PHANTOM_TARGET_TOKEN retarget hooks the driver uses.
#   - a toolchain-agnostic DRY RUN of the driver (PHANTOM_CI_DRYRUN=1) exits 0
#     WITHOUT kubectl, curl, a dotnet SDK, or network access.
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
WORKFLOW="$REPO_ROOT/.gitea/workflows/in-cluster-acceptance.yaml"
DRIVER="$REPO_ROOT/tools/ci/in-cluster-acceptance-run.sh"

pass_count=0
fail_count=0

ok()   { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

[[ -f "$WORKFLOW" ]] || fatal "workflow not found: $WORKFLOW"
[[ -f "$DRIVER" ]]   || fatal "shared driver not found: $DRIVER"
[[ -x "$DRIVER" ]]   || fatal "shared driver not executable: $DRIVER"

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

head_ "containerized (pinned toolchain)"
if grep -qE '^\s*container:\s*$' "$WORKFLOW"; then
    ok "workflow declares a container: block"
else
    bad "workflow does not declare container:"
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

head_ "installs kubectl (Phase B) + curl (drive the ingress)"
if grep -qE '\bkubectl\b' "$WORKFLOW"; then
    ok "workflow installs/uses kubectl"
else
    bad "workflow does not install kubectl (needed to introspect the deployed Pod)"
fi
if grep -qE '\bcurl\b' "$WORKFLOW"; then
    ok "workflow installs/uses curl"
else
    bad "workflow does not install curl"
fi

head_ "delegates to the shared in-cluster-acceptance-run.sh (no drift)"
if grep -qE 'tools/ci/in-cluster-acceptance-run\.sh' "$WORKFLOW"; then
    ok "workflow invokes tools/ci/in-cluster-acceptance-run.sh"
else
    bad "workflow does not invoke the shared tools/ci/in-cluster-acceptance-run.sh"
fi

head_ "acceptance bar is fail-closed (auth required) and targets DEV not prod"
if grep -qE '^\s*PHANTOM_REQUIRE_AUTH:\s*"?1"?\s*$' "$WORKFLOW"; then
    ok "workflow sets PHANTOM_REQUIRE_AUTH=1 (authenticated e2e is mandatory)"
else
    bad "workflow does not set PHANTOM_REQUIRE_AUTH=1 (bar could silently degrade to the unauthenticated subset)"
fi
if grep -qE 'PHANTOM_INCLUSTER_APIKEY:\s*\$\{\{\s*secrets\.' "$WORKFLOW"; then
    ok "workflow passes the API-key CI secret (never a hardcoded token)"
else
    bad "workflow does not pass an API-key secret for the authenticated e2e phase"
fi
if grep -qE 'PHANTOM_INCLUSTER_BASE_URL:\s*https://dev\.jellyfin\.polyfam\.studio' "$WORKFLOW"; then
    ok "workflow targets the DEV host (inactive/dev color), not the prod apex"
else
    bad "workflow does not target the dev host explicitly"
fi

head_ "driver: valid bash syntax"
if bash -n "$DRIVER"; then
    ok "$DRIVER passes bash -n syntax check"
else
    bad "$DRIVER has a bash syntax error"
fi

head_ "driver: drives the DEPLOYED stack, not a throwaway local rig"
if grep -qE 'dev\.jellyfin\.polyfam\.studio' "$DRIVER"; then
    ok "driver targets the deployed ingress (dev.jellyfin.polyfam.studio)"
else
    bad "driver does not reference the deployed ingress host"
fi
if grep -qE 'localhost:18096|127\.0\.0\.1:18096' "$DRIVER"; then
    bad "driver stands up / targets a local :18096 rig (this is the DEPLOYED-stack acceptance, not the live-rig gate)"
else
    ok "driver does not target a local :18096 throwaway rig"
fi

head_ "driver: prod-safety guards (never drives production Jellyfin)"
if grep -qE 'TARGET_HOST.*=.*PROD_HOST|"\$TARGET_HOST"\s*=\s*"\$PROD_HOST"' "$DRIVER"; then
    ok "driver refuses a target host equal to the prod apex host"
else
    bad "driver is missing the prod-apex host guard"
fi
if grep -qE '"\$tcolor"\s*=\s*"\$pcolor"' "$DRIVER" && grep -qi 'REFUSING' "$DRIVER"; then
    ok "driver refuses when the target color equals the active prod color (color guard; blue/green share the library DB so a server-Id guard is meaningless)"
else
    bad "driver is missing the prod-active-color guard"
fi

head_ "driver: asserts consolidated image + co-located gostream FUSE"
if grep -qE '/var/gostream/gostream-mkv-virtual' "$DRIVER"; then
    ok "driver asserts the co-located gostream FUSE path"
else
    bad "driver does not reference the co-located gostream FUSE mount path"
fi
if grep -qiE 'fuse' "$DRIVER" && grep -qiE 'jellyfin-phantom' "$DRIVER"; then
    ok "driver asserts a fuse mount and the consolidated jellyfin-phantom image"
else
    bad "driver does not assert the fuse mount / consolidated image"
fi

head_ "driver: runs the movie/TV/per-user acceptance scenario trio vs the deployed stack"
for scenario in \
    "tools/rig-scenarios/35-channel-e2e-playback.sh" \
    "tools/rig-scenarios/36-channel-episode-e2e-playback.sh" \
    "tools/rig-scenarios/42-per-user-show-hide.sh"
do
    if grep -q "$scenario" "$DRIVER"; then
        ok "driver references $scenario"
    else
        bad "driver does not reference $scenario"
    fi
done

head_ "scenarios honour the remote-target retarget hooks the driver uses"
for scenario in \
    "35-channel-e2e-playback.sh" \
    "36-channel-episode-e2e-playback.sh" \
    "42-per-user-show-hide.sh"
do
    if grep -qE 'PHANTOM_TARGET_API' "$REPO_ROOT/tools/rig-scenarios/$scenario" \
       && grep -qE 'PHANTOM_TARGET_TOKEN' "$REPO_ROOT/tools/rig-scenarios/$scenario"; then
        ok "$scenario honours PHANTOM_TARGET_API / PHANTOM_TARGET_TOKEN"
    else
        bad "$scenario does not honour the PHANTOM_TARGET_API / PHANTOM_TARGET_TOKEN retarget hooks"
    fi
done

head_ "driver: guaranteed cleanup via an EXIT trap"
if grep -qE 'trap teardown EXIT' "$DRIVER"; then
    ok "EXIT trap present (temp workspace cleanup)"
else
    bad "$DRIVER is missing the guaranteed EXIT trap"
fi

head_ "toolchain-agnostic dry run of the driver"
if PHANTOM_CI_DRYRUN=1 bash "$DRIVER" >/tmp/in-cluster-acceptance-dryrun.$$.log 2>&1; then
    ok "PHANTOM_CI_DRYRUN=1 tools/ci/in-cluster-acceptance-run.sh exits 0"
else
    bad "dry run of tools/ci/in-cluster-acceptance-run.sh failed; see below"
    sed 's/^/    /' /tmp/in-cluster-acceptance-dryrun.$$.log >&2 || true
fi
rm -f "/tmp/in-cluster-acceptance-dryrun.$$.log"

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
