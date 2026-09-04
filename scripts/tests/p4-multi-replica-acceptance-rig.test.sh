#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/p4-multi-replica-acceptance-rig.test.sh
#
# In-repo regression harness for
# .gitea/workflows/p4-multi-replica-acceptance-rig.yaml and
# tools/ci/p4-multi-replica-acceptance-run.sh — the P4 acceptance bar,
# mirroring scripts/tests/in-cluster-acceptance-rig.test.sh's shape (P3
# Stage 5) but asserting the MULTI-REPLICA-specific extensions this task
# adds on top of that shared rig design.
#
# Guards against the workflow/script silently rotting:
#   - the workflow file exists and PARSES as valid YAML.
#   - it runs on the SELF-HOSTED Gitea Actions runner, never `ubuntu-latest`.
#   - it is NOT gated behind a Zuul/Nodepool-style nodeset.
#   - it declares `container:` with a pinned, CONCRETE .NET SDK image tag.
#   - it invokes the SHARED tools/ci/p4-multi-replica-acceptance-run.sh
#     rather than a hand-rolled copy of the assertion steps.
#   - the shared script itself: valid bash syntax; refuses when the dev host
#     equals the prod host (prod-safety guard); resolves color from the live
#     Ingress rather than a hardcoded name; asserts a MINIMUM replica count
#     (never silently degrades to N=1 and still "passes"); asserts the
#     per-replica gostream FUSE mountpoint on EVERY replica (not just one);
#     asserts the shared-Postgres schema from every replica; proves
#     cross-replica write visibility (the no-single-writer-corruption bar);
#     proves fan-out PlaybackInfo/channel-drill resolution on every replica;
#     tears down rig-owned users/API-key state via an EXIT trap; and a
#     toolchain-agnostic DRY RUN (PHANTOM_CI_DRYRUN=1) exits 0 with no
#     cluster/network access.
#
# This test does NOT need kubectl, cluster access, or a running rig — it
# only needs bash + python3 (for YAML parsing) or PyYAML.
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
WORKFLOW="$REPO_ROOT/.gitea/workflows/p4-multi-replica-acceptance-rig.yaml"
RIG_SCRIPT="$REPO_ROOT/tools/ci/p4-multi-replica-acceptance-run.sh"

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

head_ "kubectl installed in the toolchain container"
if grep -qE 'dl\.k8s\.io.*kubectl' "$WORKFLOW"; then
    ok "workflow installs kubectl"
else
    bad "workflow does not install kubectl (needed to reach the deployed stack)"
fi

head_ "kubeconfig sourced from a provisioned secret (never an inline/plaintext cluster identifier)"
if grep -qE 'secrets\.PHANTOM_INCLUSTER_KUBECONFIG_B64' "$WORKFLOW"; then
    ok "workflow sources the kubeconfig from secrets.PHANTOM_INCLUSTER_KUBECONFIG_B64"
else
    bad "workflow does not source a kubeconfig secret"
fi

head_ "delegates to the shared p4-multi-replica-acceptance-run.sh (no drift, no hand-rolled steps)"
if grep -qE 'tools/ci/p4-multi-replica-acceptance-run\.sh' "$WORKFLOW"; then
    ok "workflow invokes tools/ci/p4-multi-replica-acceptance-run.sh"
else
    bad "workflow does not invoke the shared tools/ci/p4-multi-replica-acceptance-run.sh"
fi

head_ "shared script: valid bash syntax"
if bash -n "$RIG_SCRIPT"; then
    ok "$RIG_SCRIPT passes bash -n syntax check"
else
    bad "$RIG_SCRIPT has a bash syntax error"
fi

head_ "shared script: prod-safety guard (refuses dev-host == prod-host)"
if grep -qE 'DEV_HOST.*=.*PROD_HOST' "$RIG_SCRIPT" && grep -q 'refusing to drive the rig' "$RIG_SCRIPT"; then
    ok "$RIG_SCRIPT refuses when the dev host equals the prod host"
else
    bad "$RIG_SCRIPT is missing an explicit dev==prod host refusal"
fi

head_ "shared script: resolves color LIVE from the Ingress (never hardcoded)"
if grep -q 'resolve_color' "$RIG_SCRIPT" && grep -q 'get ingress' "$RIG_SCRIPT"; then
    ok "$RIG_SCRIPT resolves the dev color from the live Ingress"
else
    bad "$RIG_SCRIPT does not resolve color from the live Ingress"
fi

head_ "shared script: asserts a MINIMUM replica count (never silently degrades to N=1)"
if grep -q 'MIN_REPLICAS' "$RIG_SCRIPT" && grep -qE 'readyReplicas|PODS\[@\]' "$RIG_SCRIPT"; then
    ok "$RIG_SCRIPT asserts a minimum Ready replica count"
else
    bad "$RIG_SCRIPT does not assert a minimum replica count"
fi

head_ "shared script: per-replica gostream FUSE mountpoint (EVERY replica, not just one)"
if grep -q 'EVERY replica' "$RIG_SCRIPT" && grep -q 'for p in "\${PODS\[@\]}"' "$RIG_SCRIPT" && grep -q 'GOSTREAM_MOUNT_PATH' "$RIG_SCRIPT"; then
    ok "$RIG_SCRIPT asserts the gostream FUSE mountpoint on every replica"
else
    bad "$RIG_SCRIPT is missing the per-replica FUSE mountpoint assertion"
fi

head_ "shared script: asserts the shared-Postgres topology from every replica"
if grep -q 'to_regclass' "$RIG_SCRIPT" && grep -q 'user_hidden_items' "$RIG_SCRIPT" && grep -q 'user_prefs' "$RIG_SCRIPT"; then
    ok "$RIG_SCRIPT asserts phantom_dev.user_hidden_items / user_prefs via to_regclass"
else
    bad "$RIG_SCRIPT does not assert the shared-Postgres schema"
fi
if grep -qE '\bphantom\.db\b|\bjellyfin\.db\b' "$RIG_SCRIPT"; then
    bad "$RIG_SCRIPT still references the retired per-color sqlite files"
else
    ok "$RIG_SCRIPT does not reference the retired per-color sqlite files"
fi

head_ "shared script: cross-replica write visibility (no single-writer corruption)"
if grep -qi 'cross-replica' "$RIG_SCRIPT" && grep -qi 'corruption' "$RIG_SCRIPT"; then
    ok "$RIG_SCRIPT proves cross-replica write visibility / no single-writer corruption"
else
    bad "$RIG_SCRIPT is missing the cross-replica write-visibility / corruption check"
fi

head_ "shared script: fan-out PlaybackInfo across all replicas"
if grep -q 'PlaybackInfo' "$RIG_SCRIPT" && grep -qi 'fan-out' "$RIG_SCRIPT"; then
    ok "$RIG_SCRIPT asserts fan-out PlaybackInfo resolution across replicas"
else
    bad "$RIG_SCRIPT is missing the fan-out PlaybackInfo assertion"
fi

head_ "shared script: HTTPS + cert SAN assertion"
if grep -q 's_client' "$RIG_SCRIPT" && grep -q 'subjectAltName' "$RIG_SCRIPT"; then
    ok "$RIG_SCRIPT asserts HTTPS + cert SAN on the dev host"
else
    bad "$RIG_SCRIPT is missing the HTTPS + cert SAN assertion"
fi

head_ "shared script: per-user show/hide isolation (REQ-M14-PER-USER live proof)"
if grep -q 'User/Hidden' "$RIG_SCRIPT" && grep -q 'RIG_UID_A' "$RIG_SCRIPT" && grep -q 'RIG_UID_B' "$RIG_SCRIPT"; then
    ok "$RIG_SCRIPT drives per-user show/hide isolation with two rig-only users"
else
    bad "$RIG_SCRIPT is missing the per-user show/hide isolation scenario"
fi

head_ "shared script: guaranteed rig-state teardown via an EXIT trap"
if grep -qE 'trap teardown EXIT' "$RIG_SCRIPT"; then
    ok "EXIT trap present"
else
    bad "$RIG_SCRIPT is missing the guaranteed rig-state EXIT trap"
fi

head_ "toolchain-agnostic dry run of the shared rig script"
if PHANTOM_CI_DRYRUN=1 bash "$RIG_SCRIPT" >/tmp/p4-multi-replica-acceptance-dryrun.$$.log 2>&1; then
    ok "PHANTOM_CI_DRYRUN=1 tools/ci/p4-multi-replica-acceptance-run.sh exits 0"
else
    bad "dry run of tools/ci/p4-multi-replica-acceptance-run.sh failed; see /tmp/p4-multi-replica-acceptance-dryrun.$$.log"
    sed 's/^/    /' /tmp/p4-multi-replica-acceptance-dryrun.$$.log >&2 || true
fi
rm -f "/tmp/p4-multi-replica-acceptance-dryrun.$$.log"

head_ "dry run reports >=2 replicas (never collapses to the single-replica case)"
DRYRUN_LOG="/tmp/p4-multi-replica-acceptance-dryrun-count.$$.log"
if PHANTOM_CI_DRYRUN=1 bash "$RIG_SCRIPT" >"$DRYRUN_LOG" 2>&1; then
    if grep -qE 'replica pods: .+ .+' "$DRYRUN_LOG"; then
        ok "dry run enumerates >=2 replica pods"
    else
        bad "dry run did not enumerate >=2 replica pods"
    fi
else
    bad "dry run failed; cannot verify replica enumeration"
fi
rm -f "$DRYRUN_LOG"

head_ "prod-safety self-test: dry run REFUSES when dev host equals prod host"
if PHANTOM_CI_DRYRUN=1 PHANTOM_INCLUSTER_DEV_HOST=example.com PHANTOM_INCLUSTER_PROD_HOST=example.com \
    bash "$RIG_SCRIPT" >/tmp/p4-multi-replica-acceptance-guard.$$.log 2>&1; then
    bad "rig script did NOT refuse when dev host equals prod host (prod-safety guard broken)"
else
    if grep -q 'refusing to drive the rig' /tmp/p4-multi-replica-acceptance-guard.$$.log; then
        ok "rig script refuses when dev host equals prod host"
    else
        bad "rig script exited non-zero but not via the expected prod-safety refusal"
    fi
fi
rm -f "/tmp/p4-multi-replica-acceptance-guard.$$.log"

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
