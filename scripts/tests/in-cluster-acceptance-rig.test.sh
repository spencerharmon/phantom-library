#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/in-cluster-acceptance-rig.test.sh
#
# In-repo regression harness for .gitea/workflows/in-cluster-acceptance-rig.yaml
# (the P3 Stage 5 in-cluster acceptance gate) and
# tools/ci/in-cluster-acceptance-run.sh, mirroring
# gitea-live-rig-workflow.test.sh's shape for the throwaway live-rig gate.
#
# This is the task's `Check:` -- it is a STATIC/STRUCTURAL regression test,
# same shape as the existing live-rig harness: it does NOT need a live
# kubeconfig, network access, or a running cluster (the honeybee sandbox has
# none of those). The REAL in-cluster run happens on the self-hosted Gitea
# Actions runner (which does have cluster access) when the workflow fires;
# this harness guards that workflow + script from silently rotting between
# runs, exactly like gitea-live-rig-workflow.test.sh does for live-rig.yaml.
#
# Guards against the workflow/script silently rotting:
#   - the workflow file exists and PARSES as valid YAML.
#   - it runs on the SELF-HOSTED Gitea Actions runner, never `ubuntu-latest`.
#   - it invokes the SHARED tools/ci/in-cluster-acceptance-run.sh rather than
#     a hand-rolled copy of the resolve/assert/exec steps.
#   - it never hardcodes a real hostname/namespace/credential -- every site
#     fact is sourced from `secrets.*`/`vars.*` (AGENTS.md "infrastructure
#     identifiers" rule).
#   - tools/ci/in-cluster-acceptance-run.sh itself: is syntactically valid
#     bash, never hardcodes a real dev/prod hostname (env-driven only),
#     refuses when the dev host equals a caller-supplied prod host, resolves
#     the serving Pod from the live Ingress rather than assuming a color,
#     asserts the gostream FUSE mount is a live mountpoint (not merely
#     configured), and runs the movie/TV/per-user scenarios INSIDE the real
#     Pod via `kubectl exec` (not a local port-forward, since the scenarios'
#     PHDB/JDB sqlite assertions are local-file-coupled and only exist
#     inside the Pod).
#   - a toolchain-agnostic DRY RUN of tools/ci/in-cluster-acceptance-run.sh
#     (PHANTOM_CI_DRYRUN=1) exits 0 -- proving the script's control flow
#     (including the guaranteed EXIT-trap cleanup) is sound WITHOUT
#     requiring kubectl, curl, openssl, or network access, so this harness
#     runs on any CI node (including the very node this job itself gates).
#
# This test does NOT need kubectl, curl, openssl, network access, or a
# running cluster -- only bash + python3 (for YAML parsing) or PyYAML.
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
WORKFLOW="$REPO_ROOT/.gitea/workflows/in-cluster-acceptance-rig.yaml"
RIG_SCRIPT="$REPO_ROOT/tools/ci/in-cluster-acceptance-run.sh"

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

head_ "delegates to the shared in-cluster-acceptance-run.sh (no drift, no hand-rolled steps)"
if grep -qE 'tools/ci/in-cluster-acceptance-run\.sh' "$WORKFLOW"; then
    ok "workflow invokes tools/ci/in-cluster-acceptance-run.sh"
else
    bad "workflow does not invoke the shared tools/ci/in-cluster-acceptance-run.sh"
fi

head_ "no hardcoded infrastructure identifiers in the workflow"
if grep -qE '\bpolyfam\.studio\b|\bspencerharmon\.com\b|192\.168\.' "$WORKFLOW"; then
    bad "workflow hardcodes a real infra identifier -- must be sourced from secrets.*/vars.* instead"
else
    ok "workflow contains no hardcoded real hostname/IP"
fi
if grep -qE 'PHANTOM_INCLUSTER_DEV_HOST:\s*\$\{\{\s*vars\.' "$WORKFLOW" \
    && grep -qE 'PHANTOM_INCLUSTER_ADMIN_TOKEN:\s*\$\{\{\s*secrets\.' "$WORKFLOW"; then
    ok "dev host + admin token are sourced from vars./secrets., not literals"
else
    bad "workflow does not source PHANTOM_INCLUSTER_DEV_HOST/PHANTOM_INCLUSTER_ADMIN_TOKEN from vars./secrets."
fi

head_ "shared script: valid bash syntax"
if bash -n "$RIG_SCRIPT"; then
    ok "$RIG_SCRIPT passes bash -n syntax check"
else
    bad "$RIG_SCRIPT has a bash syntax error"
fi

head_ "shared script: no hardcoded infrastructure identifiers"
if grep -qE '\bpolyfam\.studio\b|\bspencerharmon\.com\b|192\.168\.' "$RIG_SCRIPT"; then
    bad "$RIG_SCRIPT hardcodes a real infra identifier -- must come from an env knob instead"
else
    ok "$RIG_SCRIPT contains no hardcoded real hostname/IP"
fi

head_ "shared script: refuses when dev host equals a supplied prod host"
if grep -qE 'PROD_HOST.*=.*DEV_HOST' "$RIG_SCRIPT" && grep -qE 'REFUSING.*prod' "$RIG_SCRIPT"; then
    ok "$RIG_SCRIPT guards against dialing the active/prod color"
else
    bad "$RIG_SCRIPT is missing the dev==prod host safety refusal"
fi

head_ "shared script: resolves the serving Pod from the live Ingress (never assumes a color)"
if grep -q 'get ingress' "$RIG_SCRIPT" && grep -q 'app.kubernetes.io/instance' "$RIG_SCRIPT"; then
    ok "$RIG_SCRIPT resolves the Pod via the Ingress's app.kubernetes.io/instance label"
else
    bad "$RIG_SCRIPT does not resolve the serving Pod from the live Ingress"
fi

head_ "shared script: asserts a LIVE (not merely configured) FUSE mount"
if grep -q 'mountpoint -q' "$RIG_SCRIPT"; then
    ok "$RIG_SCRIPT asserts the gostream FUSE path is a live mountpoint"
else
    bad "$RIG_SCRIPT does not assert the gostream FUSE mount is actually live"
fi

head_ "shared script: runs scenarios INSIDE the Pod (kubectl exec), not via local port-forward"
if grep -q 'kubectl cp\|kctl cp' "$RIG_SCRIPT" && grep -qE 'kctl exec .*bash \$?remote_dir|kctl exec "\$POD_NAME" -- env' "$RIG_SCRIPT"; then
    ok "$RIG_SCRIPT copies + execs scenarios inside the real Pod"
else
    bad "$RIG_SCRIPT does not run scenarios inside the Pod via kubectl cp + exec"
fi

head_ "shared script: references the movie/TV/per-user scenario trio"
for scenario in \
    "tools/rig-scenarios/35-channel-e2e-playback.sh" \
    "tools/rig-scenarios/36-channel-episode-e2e-playback.sh" \
    "tools/rig-scenarios/42-per-user-show-hide.sh"
do
    if grep -q "$scenario" "$RIG_SCRIPT"; then
        ok "$RIG_SCRIPT references $scenario"
    else
        bad "$RIG_SCRIPT does not reference $scenario"
    fi
done

head_ "shared script: guaranteed cleanup via an EXIT trap"
if grep -qE 'trap teardown EXIT' "$RIG_SCRIPT"; then
    ok "EXIT trap present"
else
    bad "$RIG_SCRIPT is missing the guaranteed EXIT trap cleanup"
fi

head_ "referenced scenarios accept an overridable TOK/PHDB/JDB (so kubectl-exec env overrides work)"
for scenario in 35-channel-e2e-playback.sh 36-channel-episode-e2e-playback.sh; do
    f="$REPO_ROOT/tools/rig-scenarios/$scenario"
    if grep -qE '^TOK=\$\{TOK:-' "$f" && grep -qE '^PHDB=\$\{PHDB:-' "$f" && grep -qE '^JDB=\$\{JDB:-' "$f"; then
        ok "$scenario accepts overridable TOK/PHDB/JDB"
    else
        bad "$scenario does not accept overridable TOK/PHDB/JDB (kubectl-exec env overrides would not take effect)"
    fi
done
if grep -qE '^BASE=\$\{BASE:-' "$REPO_ROOT/tools/rig-scenarios/42-per-user-show-hide.sh" \
    && grep -qE '^PHDB=\$\{PHDB:-' "$REPO_ROOT/tools/rig-scenarios/42-per-user-show-hide.sh"; then
    ok "42-per-user-show-hide.sh accepts overridable BASE/PHDB"
else
    bad "42-per-user-show-hide.sh does not accept overridable BASE/PHDB"
fi

head_ "toolchain-agnostic dry run of the shared rig script"
if PHANTOM_CI_DRYRUN=1 bash "$RIG_SCRIPT" >/tmp/in-cluster-acceptance-dryrun.$$.log 2>&1; then
    ok "PHANTOM_CI_DRYRUN=1 tools/ci/in-cluster-acceptance-run.sh exits 0"
else
    bad "dry run of tools/ci/in-cluster-acceptance-run.sh failed; see /tmp/in-cluster-acceptance-dryrun.$$.log"
    sed 's/^/    /' /tmp/in-cluster-acceptance-dryrun.$$.log >&2 || true
fi
rm -f "/tmp/in-cluster-acceptance-dryrun.$$.log"

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
