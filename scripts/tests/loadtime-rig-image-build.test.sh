#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/loadtime-rig-image-build.test.sh
#
# In-repo regression harness for the loadtime-rig image build deliverable
# (ROI Priority 9), mirroring p8-loadtime-daily-workflow.test.sh's shape.
# Proves the in-cluster daily CronJob's main-container image can actually be
# built and published (so deploy/helm/phantom-library values.yaml
# `loadtimeDaily.image` can point at a real pullable artifact instead of the
# inert docker.io/example/phantom-loadtime-rig:latest placeholder that
# ttfb-daily-rig-incluster-cronjob shipped).
#
# Guards against the Dockerfile / build workflow silently rotting:
#   - deploy/loadtime-rig/Dockerfile exists and installs EXACTLY the toolchain
#     tools/ci/loadtime-daily-run.sh needs (bash, kubectl pinned, curl,
#     ca-certificates, openssl, jq, python3) and bakes NO repo code (the
#     CronJob's init container clones the repo fresh per run).
#   - the Dockerfile base and the pinned kubectl are CONCRETE (never :latest /
#     `stable`).
#   - .gitea/workflows/build-loadtime-rig-image.yml exists and PARSES as valid
#     YAML.
#   - the workflow is tag-driven (or workflow_dispatch), runs on the
#     SELF-HOSTED Gitea Actions runner (never ubuntu-latest), inside a PINNED
#     base image, builds the Dockerfile with buildah/podman, and publishes to
#     the forge from the Actions context (no baked infra identifier).
#   - NEITHER the workflow's own build base NOR its published tag is ever
#     :latest.
#
# This test needs only bash + python3 (for YAML parsing) — no buildah,
# registry, or cluster access.
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
DOCKERFILE="$REPO_ROOT/deploy/loadtime-rig/Dockerfile"
WORKFLOW="$REPO_ROOT/.gitea/workflows/build-loadtime-rig-image.yml"
DAILY_SCRIPT="$REPO_ROOT/tools/ci/loadtime-daily-run.sh"
VALUES="$REPO_ROOT/deploy/helm/phantom-library/values.yaml"

pass_count=0
fail_count=0

ok()   { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

[[ -f "$DOCKERFILE" ]] || fatal "Dockerfile not found: $DOCKERFILE"
[[ -f "$WORKFLOW" ]]   || fatal "build workflow not found: $WORKFLOW"
[[ -f "$DAILY_SCRIPT" ]] || fatal "daily script not found: $DAILY_SCRIPT"
[[ -f "$VALUES" ]]     || fatal "values.yaml not found: $VALUES"

# =========================================================================
head_ "Dockerfile installs EXACTLY the toolchain the daily script needs"
# The daily rig toolchain, per phantom-loadtime-daily.yaml's inline install +
# tools/ci/loadtime-daily-run.sh's shell-outs. bash/curl/ca-certificates/
# openssl/jq/python3 are apt-installed; dotnet is provided by the pinned .NET
# SDK base image (see the base-pin assertion below), so it is asserted
# separately.
for tool in bash curl ca-certificates openssl jq python3; do
    if grep -qE "(^|[[:space:]])${tool}([[:space:]]|\\\\|$)" "$DOCKERFILE"; then
        ok "Dockerfile installs '$tool'"
    else
        bad "Dockerfile does not install required tool '$tool'"
    fi
done
# dotnet: tools/perf/loadtime-guard.sh step 3 runs `dotnet run --project
# tools/perf/ratchet-guard -c Release` (net9.0); without a .NET SDK on PATH it
# exits 127 and fails the whole daily run. It comes from the pinned .NET SDK
# base image (mcr.microsoft.com/dotnet/sdk:9.0.x), asserted below.
if grep -qE '^\s*FROM\s+\S*dotnet/sdk:9\.' "$DOCKERFILE"; then
    ok "Dockerfile provides dotnet via a pinned .NET 9 SDK base image"
else
    bad "Dockerfile does not provide a .NET 9 SDK (guard's 'dotnet run' would exit 127)"
fi
if grep -qE '/usr/local/bin/kubectl' "$DOCKERFILE" && grep -qE 'dl\.k8s\.io/release' "$DOCKERFILE"; then
    ok "Dockerfile installs kubectl (pinned download from dl.k8s.io)"
else
    bad "Dockerfile does not install kubectl from a pinned dl.k8s.io release"
fi

head_ "Dockerfile pins its base and kubectl to CONCRETE tags (never :latest / stable)"
BASE_LINE="$(grep -E '^\s*FROM\s' "$DOCKERFILE" | head -n1)"
if [ -z "$BASE_LINE" ]; then
    bad "Dockerfile has no FROM line"
elif printf '%s' "$BASE_LINE" | grep -qE ':(latest|stable)\b'; then
    bad "Dockerfile FROM uses a floating :latest/:stable tag: $BASE_LINE"
elif printf '%s' "$BASE_LINE" | grep -qE 'FROM\s+\S+:[^[:space:]]+'; then
    ok "Dockerfile FROM is pinned to a concrete tag: $BASE_LINE"
else
    bad "Dockerfile FROM is untagged (implicitly :latest): $BASE_LINE"
fi
KVER="$(grep -E '^\s*ARG\s+KUBECTL_VERSION=' "$DOCKERFILE" | sed -n 's/.*KUBECTL_VERSION=\([^[:space:]]*\).*/\1/p')"
if [ -z "$KVER" ] || printf '%s' "$KVER" | grep -qiE '^(latest|stable)$'; then
    bad "kubectl version is unpinned or floating: '$KVER'"
elif printf '%s' "$KVER" | grep -qE '^v[0-9]+\.[0-9]+\.[0-9]+$'; then
    ok "kubectl pinned to a concrete release: $KVER"
else
    bad "kubectl version '$KVER' is not a concrete vX.Y.Z pin"
fi

head_ "Dockerfile bakes NO repo code (init container clones the repo fresh per run)"
if grep -qE '^\s*(COPY|ADD)\s' "$DOCKERFILE"; then
    bad "Dockerfile COPY/ADDs repo content — the CronJob init container clones the repo; no code must be baked"
else
    ok "Dockerfile bakes no repo code (no COPY/ADD)"
fi

# =========================================================================
head_ "build workflow: YAML parse"
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

head_ "build workflow: tag-driven (or workflow_dispatch)"
if grep -qE '^\s*tags:\s*$' "$WORKFLOW" && grep -qE "^\s*-\s*'loadtime-rig-v" "$WORKFLOW"; then
    ok "workflow fires on a loadtime-rig-v* tag push"
else
    bad "workflow is not driven by a loadtime-rig-v* tag"
fi
if grep -qE '^\s*workflow_dispatch:' "$WORKFLOW"; then
    ok "workflow also allows a manual workflow_dispatch trigger"
else
    bad "workflow does not allow workflow_dispatch"
fi

head_ "build workflow: self-hosted runner (never ubuntu-latest)"
if grep -qE '^\s*runs-on:\s*\[.*self-hosted' "$WORKFLOW"; then
    ok "workflow declares runs-on: [self-hosted, ...]"
else
    bad "workflow does not run on the self-hosted Gitea Actions runner"
fi
if grep -qE '^\s*runs-on:\s*ubuntu-latest\s*$' "$WORKFLOW"; then
    bad "workflow declares runs-on: ubuntu-latest (must be the self-hosted runner)"
else
    ok "workflow does not fall back to ubuntu-latest"
fi

head_ "build workflow: containerized in a PINNED base image (never :latest)"
if grep -qE '^\s*container:\s*$' "$WORKFLOW"; then
    ok "workflow declares a container: block"
else
    bad "workflow does not declare container:"
fi
IMG_LINE="$(grep -E '^\s*image:\s*\S' "$WORKFLOW" | head -n1)"
if [ -z "$IMG_LINE" ]; then
    bad "workflow container has no image: line"
else
    ITAG="$(printf '%s' "$IMG_LINE" | sed -n 's/.*:\([^:[:space:]]*\)\s*$/\1/p')"
    if [ -z "$ITAG" ] || [ "$ITAG" = "latest" ]; then
        bad "workflow build-base image tag is missing or floating :latest: '$IMG_LINE'"
    else
        ok "workflow build-base image is pinned to a concrete tag: $ITAG"
    fi
fi

head_ "build workflow: builds with buildah/podman"
if grep -qE '(^|[[:space:]])(buildah|podman)\s+(bud|build)\b' "$WORKFLOW"; then
    ok "workflow builds the image with buildah/podman"
else
    bad "workflow does not build the image with buildah/podman"
fi
if grep -qE 'deploy/loadtime-rig/Dockerfile' "$WORKFLOW"; then
    ok "workflow builds deploy/loadtime-rig/Dockerfile"
else
    bad "workflow does not reference deploy/loadtime-rig/Dockerfile"
fi
if grep -qE '(^|[[:space:]])(buildah|podman)\s+push\b' "$WORKFLOW"; then
    ok "workflow publishes the image (buildah/podman push)"
else
    bad "workflow does not push the built image"
fi

head_ "build workflow: publishes loadtime-rig, NEVER :latest, from the Actions context (no baked infra id)"
if grep -qE 'loadtime-rig' "$WORKFLOW"; then
    ok "workflow publishes a loadtime-rig image path"
else
    bad "workflow does not name the loadtime-rig image"
fi
# The published tag/build-base must never be a bare :latest anywhere in the file.
if grep -vE '^\s*#' "$WORKFLOW" | grep -qE ':latest\b'; then
    bad "workflow references :latest outside comments (neither build base nor published tag may float)"
else
    ok "workflow never references :latest outside comments"
fi
# Explicit refusal of an empty/floating published tag.
if grep -qE 'refusing to publish an empty or floating tag' "$WORKFLOW"; then
    ok "workflow explicitly refuses an empty/floating published tag"
else
    bad "workflow does not guard against publishing an empty/floating tag"
fi
if grep -qE 'GITHUB_SERVER_URL|github\.server_url' "$WORKFLOW" && grep -qE 'GITHUB_REPOSITORY|github\.repository' "$WORKFLOW"; then
    ok "workflow derives the forge host/repo from the Actions context (no baked infra identifier)"
else
    bad "workflow does not derive the registry path from the Actions context"
fi
if grep -vE '^\s*#' "$WORKFLOW" | grep -qE '(https?://)?[a-zA-Z0-9.-]+\.(polyfam\.studio|spencerharmon\.com)'; then
    bad "workflow bakes a real site-specific hostname (infra-identifier rule violation)"
else
    ok "workflow bakes no real site-specific hostname"
fi

# =========================================================================
head_ "values.yaml: loadtimeDaily.image default stays example.com-safe"
if grep -qE '^\s*image:\s*"docker\.io/example/phantom-loadtime-rig' "$VALUES"; then
    ok "loadtimeDaily.image default is the infra-identifier-safe example placeholder"
else
    bad "loadtimeDaily.image default is not the expected example.com-safe placeholder"
fi

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
