#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/p8-loadtime-daily-workflow.test.sh
#
# In-repo regression harness for .gitea/workflows/phantom-loadtime-daily.yaml
# and tools/ci/loadtime-daily-run.sh (ROI Priority 8, item 3 — the daily
# schedule job), mirroring in-cluster-acceptance-rig.test.sh's shape.
#
# Guards against the workflow/script silently rotting:
#   - the workflow file exists and PARSES as valid YAML.
#   - it fires on a `schedule:` cron trigger (a DAILY cadence, not just
#     workflow_dispatch) AND runs on the SELF-HOSTED Gitea Actions runner,
#     never `ubuntu-latest`.
#   - it declares `container:` with a pinned, CONCRETE .NET SDK image tag.
#   - it delegates to the SHARED tools/ci/loadtime-daily-run.sh rather than a
#     hand-rolled copy of the steps.
#   - it sources every host/token/endpoint from secrets/vars — never a baked
#     infra identifier in the tracked YAML.
#   - the shared script itself: valid bash syntax; refuses when the dev host
#     equals the prod host (prod-safety guard); resolves color from the live
#     Ingress rather than a hardcoded name; measures the DEPLOYED stack over
#     HTTPS (never a standalone rig's :18096, never prod's raw :8096 baked as
#     the daily target); delegates to the P8/1 measurement engine, the P8/2
#     Pushgateway emitter, and the P8/5 ratcheting regression guard rather
#     than reimplementing any of them; performs mandatory build/test process
#     cleanup via a trap; and a toolchain-agnostic DRY RUN
#     (PHANTOM_CI_DRYRUN=1) exits 0 with no cluster/network access and never
#     mutates the real thresholds registry from a synthetic fixture.
#
# This test does NOT need kubectl, cluster access, or a running rig — it
# only needs bash + python3 (for YAML parsing).
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
WORKFLOW="$REPO_ROOT/.gitea/workflows/phantom-loadtime-daily.yaml"
DAILY_SCRIPT="$REPO_ROOT/tools/ci/loadtime-daily-run.sh"
ENGINE="$REPO_ROOT/tools/rig-scenarios/47-loadtime-flows.sh"
PUSH_SCRIPT="$REPO_ROOT/scripts/phantom-loadtime-push.sh"
GUARD_SCRIPT="$REPO_ROOT/tools/perf/loadtime-guard.sh"
THRESHOLDS="$REPO_ROOT/tools/perf/loadtime-thresholds.json"

pass_count=0
fail_count=0

ok()   { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

[[ -f "$WORKFLOW" ]]      || fatal "workflow not found: $WORKFLOW"
[[ -f "$DAILY_SCRIPT" ]]  || fatal "shared daily script not found: $DAILY_SCRIPT"
[[ -x "$DAILY_SCRIPT" ]]  || fatal "shared daily script not executable: $DAILY_SCRIPT"
[[ -f "$ENGINE" ]]        || fatal "measurement engine not found: $ENGINE"
[[ -f "$PUSH_SCRIPT" ]]   || fatal "push emitter not found: $PUSH_SCRIPT"
[[ -f "$GUARD_SCRIPT" ]]  || fatal "regression guard not found: $GUARD_SCRIPT"
[[ -f "$THRESHOLDS" ]]    || fatal "thresholds registry not found: $THRESHOLDS"

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

head_ "fires on a DAILY schedule (cron), not just workflow_dispatch"
if grep -qE '^\s*schedule:\s*$' "$WORKFLOW" && grep -qE "^\s*-\s*cron:\s*'.+'" "$WORKFLOW"; then
    ok "workflow declares an 'on: schedule:' cron trigger"
else
    bad "workflow is missing a scheduled cron trigger"
fi
if grep -qE '^\s*workflow_dispatch:\s*$' "$WORKFLOW"; then
    ok "workflow also allows a manual workflow_dispatch trigger"
else
    bad "workflow does not allow workflow_dispatch (manual re-run)"
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

head_ "kubeconfig sourced from a provisioned secret (never an inline/plaintext cluster identifier)"
if grep -qE 'secrets\.PHANTOM_INCLUSTER_KUBECONFIG_B64' "$WORKFLOW"; then
    ok "workflow sources the kubeconfig from secrets.PHANTOM_INCLUSTER_KUBECONFIG_B64"
else
    bad "workflow does not source a kubeconfig secret"
fi

head_ "every host/token/endpoint sourced from secrets/vars (never a baked infra identifier)"
if grep -qE 'vars\.PHANTOM_INCLUSTER_DEV_HOST' "$WORKFLOW" \
    && grep -qE 'secrets\.PHANTOM_INCLUSTER_ADMIN_TOKEN' "$WORKFLOW" \
    && grep -qE 'vars\.PHANTOM_PUSHGATEWAY_URL' "$WORKFLOW"; then
    ok "workflow sources dev host / admin token / Pushgateway URL from secrets/vars"
else
    bad "workflow is missing a secrets/vars source for dev host / admin token / Pushgateway URL"
fi
if grep -qE '(https?://)?[a-zA-Z0-9.-]+\.(polyfam\.studio|spencerharmon\.com)' "$WORKFLOW"; then
    bad "workflow bakes a real site-specific hostname (infra-identifier rule violation)"
else
    ok "workflow does not bake a real site-specific hostname"
fi

head_ "delegates to the shared tools/ci/loadtime-daily-run.sh (no drift, no hand-rolled steps)"
if grep -qE 'tools/ci/loadtime-daily-run\.sh' "$WORKFLOW"; then
    ok "workflow invokes tools/ci/loadtime-daily-run.sh"
else
    bad "workflow does not invoke the shared tools/ci/loadtime-daily-run.sh"
fi

head_ "shared script: valid bash syntax"
if bash -n "$DAILY_SCRIPT"; then
    ok "$DAILY_SCRIPT passes bash -n syntax check"
else
    bad "$DAILY_SCRIPT has a bash syntax error"
fi

head_ "shared script: prod-safety guard (refuses dev-host == prod-host)"
if grep -qE 'DEV_HOST.*=.*PROD_HOST' "$DAILY_SCRIPT" && grep -q 'refusing to measure' "$DAILY_SCRIPT"; then
    ok "$DAILY_SCRIPT refuses when the dev host equals the prod host"
else
    bad "$DAILY_SCRIPT is missing an explicit dev==prod host refusal"
fi

head_ "shared script: resolves color LIVE from the Ingress (never hardcoded)"
if grep -q 'resolve_color' "$DAILY_SCRIPT" && grep -q 'get ingress' "$DAILY_SCRIPT"; then
    ok "$DAILY_SCRIPT resolves the dev color from the live Ingress"
else
    bad "$DAILY_SCRIPT does not resolve color from the live Ingress"
fi

head_ "shared script: measures the DEPLOYED stack over HTTPS (never a baked :8096/:18096 daily target)"
if grep -qE 'API="https://\$DEV_HOST"' "$DAILY_SCRIPT"; then
    ok "$DAILY_SCRIPT targets https://\$DEV_HOST — the deployed stack, not a standalone rig"
else
    bad "$DAILY_SCRIPT does not target the deployed dev host over HTTPS"
fi
if grep -vE '^\s*#' "$DAILY_SCRIPT" | grep -qE ':18096|:8096'; then
    bad "$DAILY_SCRIPT bakes a rig/prod port reference outside comments — the daily job must never target a standalone rig instance"
else
    ok "$DAILY_SCRIPT does not bake a :18096/:8096 port reference outside comments"
fi

head_ "shared script: reuses the P8/1 measurement engine, P8/2 emitter, P8/5 guard (never reimplements them)"
if grep -q 'rig-scenarios/47-loadtime-flows.sh' "$DAILY_SCRIPT"; then
    ok "$DAILY_SCRIPT invokes the shared measurement engine (47-loadtime-flows.sh)"
else
    bad "$DAILY_SCRIPT does not invoke the shared measurement engine"
fi
if grep -q 'phantom-loadtime-push.sh' "$DAILY_SCRIPT"; then
    ok "$DAILY_SCRIPT invokes the shared Pushgateway emitter (phantom-loadtime-push.sh)"
else
    bad "$DAILY_SCRIPT does not invoke the shared Pushgateway emitter"
fi
if grep -q 'loadtime-guard.sh' "$DAILY_SCRIPT"; then
    ok "$DAILY_SCRIPT invokes the shared ratcheting regression guard (loadtime-guard.sh)"
else
    bad "$DAILY_SCRIPT does not invoke the shared ratcheting regression guard"
fi

head_ "shared script: never --apply's the real thresholds registry from a dry-run synthetic fixture"
if grep -qE 'guard_args=\(--color "\$COLOR" --no-file\)' "$DAILY_SCRIPT"; then
    ok "$DAILY_SCRIPT routes the dry-run guard call through --no-file (detect-only)"
else
    bad "$DAILY_SCRIPT does not guarantee --no-file on a dry-run guard call"
fi

head_ "shared script: mandatory build/test process cleanup trap"
if grep -q 'lib-cleanup.sh' "$DAILY_SCRIPT" && grep -q 'phantom_ci_cleanup_dotnet' "$DAILY_SCRIPT" \
    && grep -qE 'trap teardown EXIT INT TERM' "$DAILY_SCRIPT"; then
    ok "$DAILY_SCRIPT sources tools/ci/lib-cleanup.sh and traps EXIT/INT/TERM"
else
    bad "$DAILY_SCRIPT is missing the mandatory build/test process cleanup trap"
fi

head_ "toolchain-agnostic dry run of the shared daily script"
before_thresholds_sha="$(sha256sum "$THRESHOLDS" | awk '{print $1}')"
if PHANTOM_CI_DRYRUN=1 PHANTOM_CI_PKILL=0 PHANTOM_REPO_ROOT="$REPO_ROOT" bash "$DAILY_SCRIPT" \
    >/tmp/p8-loadtime-daily-dryrun.$$.log 2>&1; then
    ok "PHANTOM_CI_DRYRUN=1 tools/ci/loadtime-daily-run.sh exits 0"
else
    bad "dry run of tools/ci/loadtime-daily-run.sh failed; see /tmp/p8-loadtime-daily-dryrun.$$.log"
    sed 's/^/    /' /tmp/p8-loadtime-daily-dryrun.$$.log >&2 || true
fi
after_thresholds_sha="$(sha256sum "$THRESHOLDS" | awk '{print $1}')"
if [ "$before_thresholds_sha" = "$after_thresholds_sha" ]; then
    ok "dry run left tools/perf/loadtime-thresholds.json byte-identical (no synthetic-fixture contamination)"
else
    bad "dry run MUTATED tools/perf/loadtime-thresholds.json — a dry-run synthetic fixture must never seed/tighten the real thresholds registry"
fi
rm -f "/tmp/p8-loadtime-daily-dryrun.$$.log"

head_ "prod-safety self-test: dry run REFUSES when dev host equals prod host"
if PHANTOM_CI_DRYRUN=1 PHANTOM_CI_PKILL=0 PHANTOM_REPO_ROOT="$REPO_ROOT" \
    PHANTOM_INCLUSTER_DEV_HOST=example.com PHANTOM_INCLUSTER_PROD_HOST=example.com \
    bash "$DAILY_SCRIPT" >/tmp/p8-loadtime-daily-guard.$$.log 2>&1; then
    bad "daily script did NOT refuse when dev host equals prod host (prod-safety guard broken)"
else
    if grep -q 'refusing to measure' /tmp/p8-loadtime-daily-guard.$$.log; then
        ok "daily script refuses when dev host equals prod host"
    else
        bad "daily script exited non-zero but not via the expected prod-safety refusal"
    fi
fi
rm -f "/tmp/p8-loadtime-daily-guard.$$.log"

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
