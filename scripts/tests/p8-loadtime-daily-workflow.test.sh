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
# needs bash, python3 + PyYAML, dotnet, and Helm. Missing tools are failures.
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

assert_stage_payloads() {
    python3 - "$1" "$2" "${3:-dry}" <<'PY'
import re
import sys

with open(sys.argv[1]) as f:
    output = f.read()
if sys.argv[3] == 'live':
    payloads = re.findall(
        r'^PUT \S+/metrics/job/phantom-loadtime-daily-run\n'
        r'((?:phantom_loadtime_.*\n)+)--', output, re.MULTILINE)
else:
    payloads = re.findall(
        r'^    \[stage-metric dry-run\] PUT \S+/metrics/job/phantom-loadtime-daily-run\n'
        r'((?:    phantom_loadtime_.*\n)+)', output, re.MULTILINE)
assert len(payloads) == 3, f"expected three stage PUTs, got {len(payloads)}"
run_start = None
for index in (2, 1, 0):
    payload = payloads[index]
    lines = [line for line in payload.splitlines() if 'phantom_loadtime_daily_' in line]
    samples = dict(line.strip().split() for line in lines)
    stages = ('measure', 'push', 'guard')[:index + 1]
    expected = {'phantom_loadtime_daily_run_start_timestamp_seconds'}
    for stage in stages:
        for metric in ('exit_code', 'seconds'):
            key = f'phantom_loadtime_daily_stage_{metric}{{stage="{stage}"}}'
            expected.add(key)
            assert key in samples, f"PUT {index + 1} lost {key}"
            value = int(samples[key])
            if metric == 'seconds':
                assert value >= 0, f"negative duration: {key}"
            else:
                allowed = {int(v) for v in sys.argv[2].split(',')} if stage == 'guard' else {0}
                assert value in allowed, f"unexpected exit code: {key} {value}"
    assert set(samples) == expected, f"PUT {index + 1} has missing or unexpected samples"
    assert len(lines) == len(expected), "duplicate samples"
    if index < 2 or sys.argv[2] == '2':
        assert 'phantom_loadtime_guard_' not in payload, 'breaches emitted without a valid guard result'
    timestamp = int(samples['phantom_loadtime_daily_run_start_timestamp_seconds'])
    assert timestamp > 0, "run start must be an epoch timestamp"
    if run_start is None:
        run_start = timestamp
    assert timestamp == run_start, "run start changed between stage PUTs"
PY
}

assert_breach_payload() {
    python3 - "$1" "$2" "$3" "${4:-dry}" <<'PY'
import re, sys
output = open(sys.argv[1]).read()
if sys.argv[4] == 'live':
    payloads = re.findall(r'^PUT \S+/metrics/job/phantom-loadtime-daily-run\n'
                          r'((?:phantom_loadtime_.*\n)+)--', output, re.M)
else:
    payloads = re.findall(
        r'^    \[stage-metric dry-run\] PUT \S+/metrics/job/phantom-loadtime-daily-run\n'
        r'((?:    phantom_loadtime_.*\n)+)', output, re.M)
assert len(payloads) == 3, 'expected three retained-group PUTs'
# Model Pushgateway PUT replacement, including a previous run's stale breach.
retained = {'phantom_loadtime_guard_breach{flow="list_load",item_type="movie"}': '1'}
for payload in payloads:
    lines = payload.splitlines()
    retained = dict(line.strip().split() for line in lines)
    assert len(retained) == len(lines), 'duplicate samples'
actual = {k: float(v) for k, v in retained.items() if k.startswith('phantom_loadtime_guard_')}
expected = {}
if sys.argv[3] == 'A-breach':
    labels = f'{{flow="list_load",item_type="{sys.argv[2]}"}}'
    expected = {'phantom_loadtime_guard_breach' + labels: 1,
                'phantom_loadtime_guard_observed_seconds' + labels: 2,
                'phantom_loadtime_guard_ceiling_seconds' + labels: 1}
assert actual == expected, (actual, expected)
assert int(retained['phantom_loadtime_daily_stage_exit_code{stage="guard"}']) == (3 if expected else 0)
PY
}

[[ -f "$WORKFLOW" ]]      || fatal "workflow not found: $WORKFLOW"
[[ -f "$DAILY_SCRIPT" ]]  || fatal "shared daily script not found: $DAILY_SCRIPT"
[[ -x "$DAILY_SCRIPT" ]]  || fatal "shared daily script not executable: $DAILY_SCRIPT"
[[ -f "$ENGINE" ]]        || fatal "measurement engine not found: $ENGINE"
[[ -f "$PUSH_SCRIPT" ]]   || fatal "push emitter not found: $PUSH_SCRIPT"
[[ -f "$GUARD_SCRIPT" ]]  || fatal "regression guard not found: $GUARD_SCRIPT"
[[ -f "$THRESHOLDS" ]]    || fatal "thresholds registry not found: $THRESHOLDS"

head_ "required integration dependencies (no silent skips)"
for tool in python3 dotnet helm git; do
    command -v "$tool" >/dev/null 2>&1 || fatal "$tool is required for daily workflow integration coverage"
done
python3 -c 'import yaml' || fatal "PyYAML is required; structural grep is not YAML validation"

head_ "YAML parse"
if python3 - "$WORKFLOW" <<'PY'
import sys
import yaml
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
    bad "$WORKFLOW is not valid YAML"
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
    && grep -qE 'trap teardown EXIT' "$DAILY_SCRIPT" \
    && grep -q "trap 'exit 130' INT" "$DAILY_SCRIPT" \
    && grep -q "trap 'exit 143' TERM" "$DAILY_SCRIPT"; then
    ok "$DAILY_SCRIPT sources tools/ci/lib-cleanup.sh and traps EXIT/INT/TERM"
else
    bad "$DAILY_SCRIPT is missing the mandatory build/test process cleanup trap"
fi

head_ "toolchain-agnostic dry run of the shared daily script"
initial_evidence="$(mktemp -d -t p8-daily-evidence.XXXXXX)"
before_thresholds_sha="$(sha256sum "$THRESHOLDS" | awk '{print $1}')"
if PHANTOM_LOADTIME_EVIDENCE_DIR="$initial_evidence" PHANTOM_CI_DRYRUN=1 PHANTOM_CI_PKILL=0 PHANTOM_REPO_ROOT="$REPO_ROOT" bash "$DAILY_SCRIPT" \
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
if assert_stage_payloads "/tmp/p8-loadtime-daily-dryrun.$$.log" "0,3"; then
    ok "dry-run PUTs retain all completed stages and one stable run-start timestamp"
else
    bad "dry-run PUTs lost stage outcomes or run-start timestamp"
fi
rm -f "/tmp/p8-loadtime-daily-dryrun.$$.log"

head_ "shared script: pushes a durable per-stage outcome metric (ttfb-loadtime-daily-stage-outcome-metric)"
if grep -q 'record_stage' "$DAILY_SCRIPT" && grep -q 'record_stage measure' "$DAILY_SCRIPT"; then
    ok "$DAILY_SCRIPT records a stage-outcome push call site for stage=measure"
else
    bad "$DAILY_SCRIPT is missing a stage-outcome push call site for stage=measure"
fi
if grep -q 'record_stage push' "$DAILY_SCRIPT"; then
    ok "$DAILY_SCRIPT records a stage-outcome push call site for stage=push"
else
    bad "$DAILY_SCRIPT is missing a stage-outcome push call site for stage=push"
fi
if grep -q 'record_stage guard' "$DAILY_SCRIPT"; then
    ok "$DAILY_SCRIPT records a stage-outcome push call site for stage=guard"
else
    bad "$DAILY_SCRIPT is missing a stage-outcome push call site for stage=guard"
fi
if grep -qE 'phantom_loadtime_daily_stage_exit_code\{stage=' "$DAILY_SCRIPT" \
    && grep -qE 'phantom_loadtime_daily_stage_seconds\{stage=' "$DAILY_SCRIPT"; then
    ok "$DAILY_SCRIPT emits phantom_loadtime_daily_stage_exit_code/seconds{stage=...}"
else
    bad "$DAILY_SCRIPT does not emit the stage-outcome metric families"
fi
if grep -qE 'job/\$\{?STAGE_PUSH_JOB\}?' "$DAILY_SCRIPT" \
    && grep -qE 'STAGE_PUSH_JOB="phantom-loadtime-daily-run"' "$DAILY_SCRIPT"; then
    ok "$DAILY_SCRIPT pushes the stage-outcome metric under a DISTINCT job=phantom-loadtime-daily-run group"
else
    bad "$DAILY_SCRIPT does not push the stage-outcome metric under its own distinct job group"
fi

head_ "shared script: a forced stage=guard failure pushes its outcome BEFORE the wrapper's own non-zero exit (no live cluster needed)"
rc=0
PHANTOM_LOADTIME_EVIDENCE_DIR="$initial_evidence" PHANTOM_CI_DRYRUN=1 PHANTOM_CI_PKILL=0 PHANTOM_REPO_ROOT="$REPO_ROOT" PHANTOM_CI_FORCE_GUARD_FAIL=1 \
    bash "$DAILY_SCRIPT" >/tmp/p8-loadtime-daily-guardfail.$$.log 2>&1 || rc=$?
if [ "$rc" = 0 ]; then
    bad "daily script did NOT fail when PHANTOM_CI_FORCE_GUARD_FAIL=1 forced a stage=guard failure"
else
    if grep -qE 'stage-metric dry-run.*job/phantom-loadtime-daily-run' /tmp/p8-loadtime-daily-guardfail.$$.log \
        && grep -A2 'stage="guard"' /tmp/p8-loadtime-daily-guardfail.$$.log | grep -qE 'phantom_loadtime_daily_stage_exit_code\{stage="guard"\} [1-9][0-9]*'; then
        ok "daily script pushed a non-zero stage=\"guard\" exit-code outcome before its own non-zero exit ($rc)"
    else
        bad "daily script exited non-zero ($rc) but did not push a non-zero stage=\"guard\" outcome metric first"
    fi
fi
if assert_stage_payloads "/tmp/p8-loadtime-daily-guardfail.$$.log" "2"; then
    ok "forced guard failure retains measure+push successes and guard exit=2 in the final PUT"
else
    bad "forced guard failure lost earlier stages or failed guard outcome in the final PUT"
fi
rm -f "/tmp/p8-loadtime-daily-guardfail.$$.log"

head_ "prod-safety self-test: dry run REFUSES when dev host equals prod host"
if PHANTOM_LOADTIME_EVIDENCE_DIR="$initial_evidence" PHANTOM_CI_DRYRUN=1 PHANTOM_CI_PKILL=0 PHANTOM_REPO_ROOT="$REPO_ROOT" \
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
rm -rf "$initial_evidence"

# ---------------------------------------------------------------------------
# same-batch behavioral integration (ttfb-loadtime-daily-same-batch-guard)
#
# Executes the REAL wrapper, guard, converter and ratchet (dotnet) inside a
# scratch repo root whose only substitutions are: a COUNTING engine fixture
# that returns batch A on call 1 and batch B on call 2 (opposite threshold
# outcomes), scratch thresholds, and fake kubectl/curl/beehive on PATH that
# record what was resolved/pushed/filed. Required — never silently skipped.
# ---------------------------------------------------------------------------
head_ "same-batch: daily wrapper guards the exact batch it pushed (real guard + converter + ratchet)"
SB="$(mktemp -d -t p8-same-batch.XXXXXX)"
trap 'chmod -R u+rwX "$SB"; rm -rf "$SB"' EXIT
sb_root="$SB/root"; sb_bin="$SB/bin"
mkdir -p "$sb_root/tools/ci" "$sb_root/tools/perf" "$sb_root/tools/rig-scenarios" "$sb_root/scripts" "$sb_bin"
ln -s "$DAILY_SCRIPT" "$sb_root/tools/ci/loadtime-daily-run.sh"
ln -s "$REPO_ROOT/tools/ci/lib-cleanup.sh" "$sb_root/tools/ci/lib-cleanup.sh"
if [ -f "$REPO_ROOT/tools/ci/loadtime-daily-state.py" ]; then
    cp "$REPO_ROOT/tools/ci/loadtime-daily-state.py" "$sb_root/tools/ci/"
else
    bad "durable state helper tools/ci/loadtime-daily-state.py is missing"
fi
if [ -f "$REPO_ROOT/tools/ci/loadtime-evidence.py" ]; then
    cp "$REPO_ROOT/tools/ci/loadtime-evidence.py" "$sb_root/tools/ci/"
else
    bad "retained evidence helper tools/ci/loadtime-evidence.py is missing"
fi
ln -s "$GUARD_SCRIPT" "$sb_root/tools/perf/loadtime-guard.sh"
ln -s "$REPO_ROOT/tools/perf/loadtime-expo-to-measurements.py" "$sb_root/tools/perf/loadtime-expo-to-measurements.py"
# Real ratchet sources, copied (not symlinked) so its bin/obj build in scratch
# and never alias the repo's own build outputs through a symlinked path.
mkdir -p "$sb_root/tools/perf/ratchet-guard"
cp "$REPO_ROOT"/tools/perf/ratchet-guard/*.cs "$REPO_ROOT"/tools/perf/ratchet-guard/*.csproj "$sb_root/tools/perf/ratchet-guard/"
ln -s "$PUSH_SCRIPT" "$sb_root/scripts/phantom-loadtime-push.sh"
SB_SEED="$sb_root/tools/perf/loadtime-thresholds.json"
mkdir -p "$SB/state"
SB_THR="$SB/state/loadtime-thresholds.json"
# Counting engine fixture: SB_CASE=<backend>:<A-breach|A-pass>. The named
# backend's list_load is 2.0s (breach of 1000ms) in one batch and 0.5s
# (improvement -> ratchet to 525ms) in the other; the other backend is a
# steady 0.95s (no change) in both batches.
cat > "$sb_root/tools/rig-scenarios/47-loadtime-flows.sh" <<'ENG'
#!/usr/bin/env bash
set -euo pipefail
n=$(( $(cat "$SB_COUNT" 2>/dev/null || echo 0) + 1 )); echo "$n" > "$SB_COUNT"
backend="${SB_CASE%%:*}"; mode="${SB_CASE#*:}"
if [ "$n" = 1 ]; then batch=A; else batch=B; fi
if [ "$mode" = durable ]; then [ "$batch" = A ] && v="$SB_VALUE" || v=9.0
elif [ "$mode" = A-breach ]; then [ "$batch" = A ] && v=2.0 || v=0.5
else [ "$batch" = A ] && v=0.5 || v=2.0; fi
for b in movie episode; do
  val=0.95; [ "$mode" = durable ] && val=1.0; [ "$b" = "$backend" ] && val=$v
  for flow in list_load sort_change info_open get_sources materialise play_materialised; do
    seconds=1.0; [ "$flow" = list_load ] && seconds="$val"
    printf 'phantom_loadtime_seconds{flow="%s",item_type="%s",color="%s"} %s\n' "$flow" "$b" "${PHANTOM_LOADTIME_COLOR:-green}" "$seconds"
    printf 'phantom_loadtime_runs_total{flow="%s",item_type="%s",color="%s"} 1\n' "$flow" "$b" "${PHANTOM_LOADTIME_COLOR:-green}"
    printf 'phantom_loadtime_errors_total{flow="%s",item_type="%s",color="%s"} 0\n' "$flow" "$b" "${PHANTOM_LOADTIME_COLOR:-green}"
  done
done
ENG
cat > "$sb_bin/kubectl" <<'K'
#!/usr/bin/env bash
case "$*" in *phantom-library-green*) echo "$SB_DEV_HOST" ;; esac
K
cat > "$sb_bin/curl" <<'C'
#!/usr/bin/env bash
url="${@: -1}"; data=""
for ((i=1;i<=$#;i++)); do [ "${!i}" = --data-binary ] && { j=$((i+1)); data="${!j}"; }; done
{ echo "PUT $url"; if [ "$data" = @- ]; then cat; else cat "${data#@}"; fi; echo "--"; } >> "$SB_CURL_LOG"
C
cat > "$sb_bin/beehive" <<'B'
#!/usr/bin/env bash
echo "$*" >> "$SB_BEEHIVE_LOG"
if [ "$1 $2" = 'task add' ]; then
  for ((i=1;i<=$#;i++)); do
    if [ "${!i}" = --body-file ]; then j=$((i+1)); cat "${!j}" >> "$SB_BEEHIVE_LOG.body"; fi
  done
fi
B
chmod +x "$sb_root/tools/rig-scenarios/47-loadtime-flows.sh" "$sb_bin"/*

sb_reset() {
    cat > "$SB_SEED" <<'T'
{
  "improvement_margin_ratio": 0.1,
  "ratchet_headroom_ratio": 0.05,
  "scenarios": [
    { "flow": "list_load", "backend": "movie", "quantile": "single", "threshold_ms": 1000 },
    { "flow": "list_load", "backend": "episode", "quantile": "single", "threshold_ms": 1000 }
  ]
}
T
    cp "$SB_SEED" "$SB_THR"
    rm -f "$SB/count" "$SB/curl.log" "$SB/beehive.log" "$SB/beehive.log.body"; : > "$SB/curl.log"; : > "$SB/beehive.log"
}
sb_thr() { python3 -c 'import json,sys; print([s["threshold_ms"] for s in json.load(open(sys.argv[1]))["scenarios"] if s["backend"]==sys.argv[2]][0])' "$SB_THR" "$1"; }
sb_run() { # sb_run <case> <dryrun 0|1> -> sets sb_rc, log in $SB/out.log
    sb_rc=0
    env PATH="$sb_bin:$PATH" PHANTOM_REPO_ROOT="$sb_root" PHANTOM_CI_DRYRUN="$2" PHANTOM_CI_PKILL=0 \
        PHANTOM_LOADTIME_THRESHOLDS_PATH="$SB_THR" \
        PHANTOM_LOADTIME_EVIDENCE_DIR="$SB/evidence" \
        SB_VALUE="${SB_VALUE:-1.0}" \
        SB_CASE="$1" SB_COUNT="$SB/count" SB_CURL_LOG="$SB/curl.log" SB_BEEHIVE_LOG="$SB/beehive.log" \
        SB_DEV_HOST=dev.example.com PHANTOM_INCLUSTER_DEV_HOST=dev.example.com \
        PHANTOM_INCLUSTER_PROD_HOST=example.com PHANTOM_INCLUSTER_ADMIN_TOKEN=tok \
        PHANTOM_PUSHGATEWAY_URL=http://pushgateway.example.com:9091 \
        bash "$sb_root/tools/ci/loadtime-daily-run.sh" > "$SB/out.log" 2>&1 || sb_rc=$?
}
sb_check() { # sb_check <label> <cond-cmd...>
    local label="$1"; shift
    if "$@"; then ok "$label"; else bad "$label"; sed 's/^/      /' "$SB/out.log" | tail -40 >&2; fi
}
sb_guard_stage() { # final stage-outcome PUT's guard exit code
    python3 - "$1" <<'PY'
import re,sys
t=open(sys.argv[1]).read()
m=re.findall(r'phantom_loadtime_daily_stage_exit_code\{stage="guard"\} (\d+)',t)
print(m[-1] if m else "missing")
PY
}

for backend in movie episode; do
  for mode in A-breach A-pass; do
    c="$backend:$mode"
    if [ "$mode" = A-breach ]; then a_val=2.0; want_guard=3; want_thr=1000; else a_val=0.5; want_guard=0; want_thr=525; fi
    # ---- live ----
    sb_reset; sb_run "$c" 0
    sb_check "[$c live] wrapper exits 0 (got $sb_rc)" test "$sb_rc" = 0
    sb_check "[$c live] engine invoked exactly once" test "$(cat "$SB/count" 2>/dev/null)" = 1
    sb_check "[$c live] Pushgateway received batch A ($backend=$a_val)" \
        grep -q "phantom_loadtime_seconds{flow=\"list_load\",item_type=\"$backend\",color=\"green\"} $a_val\$" "$SB/curl.log"
    sb_check "[$c live] stage=guard outcome is A-based ($want_guard)" test "$(sb_guard_stage "$SB/curl.log")" = "$want_guard"
    sb_check "[$c live] all three PUTs retain cumulative stages" assert_stage_payloads "$SB/curl.log" "$want_guard" live
    sb_check "[$c live] retained PUT contains exact breach gauges or clears stale breaches" \
        assert_breach_payload "$SB/curl.log" "$backend" "$mode" live
    sb_check "[$c live] $backend threshold written from A (${want_thr}ms)" test "$(sb_thr "$backend")" = "$want_thr"
    other=episode; [ "$backend" = episode ] && other=movie
    sb_check "[$c live] $other threshold unchanged (1000ms)" test "$(sb_thr "$other")" = 1000
    if [ "$mode" = A-breach ]; then
        sb_check "[$c live] breach filed via beehive task add (live filing preserved)" \
            grep -q "^task add phantom-library p8-daily-perf-regression-.*$backend" "$SB/beehive.log"
    else
        sb_check "[$c live] pass filed nothing" test ! -s "$SB/beehive.log"
    fi
    # ---- dry-run ----
    sb_reset; before="$(sha256sum "$SB_THR" | awk '{print $1}')"; sb_run "$c" 1
    sb_check "[$c dry-run] wrapper exits 0 (got $sb_rc)" test "$sb_rc" = 0
    sb_check "[$c dry-run] engine invoked exactly once" test "$(cat "$SB/count" 2>/dev/null)" = 1
    sb_check "[$c dry-run] pushed (dry-run) batch A ($backend=$a_val)" \
        grep -q "phantom_loadtime_seconds{flow=\"list_load\",item_type=\"$backend\",color=\"green\"} $a_val\$" "$SB/out.log"
    sb_check "[$c dry-run] stage=guard outcome is A-based ($want_guard)" test "$(sb_guard_stage "$SB/out.log")" = "$want_guard"
    sb_check "[$c dry-run] retained PUT contains exact breach gauges or clears stale breaches" \
        assert_breach_payload "$SB/out.log" "$backend" "$mode"
    sb_check "[$c dry-run] thresholds byte-identical, nothing filed/curl'd" \
        test "$before" = "$(sha256sum "$SB_THR" | awk '{print $1}')" -a ! -s "$SB/beehive.log" -a ! -s "$SB/curl.log"
  done
done

head_ "guard --exposition: supplied-input preservation and error handling"
sb_guard() { sb_rc=0; env PATH="$sb_bin:$PATH" PHANTOM_REPO_ROOT="$sb_root" SB_CASE="$SB_CASE_G" SB_COUNT="$SB/count" \
    SB_BEEHIVE_LOG="$SB/beehive.log" bash "$sb_root/tools/perf/loadtime-guard.sh" "$@" > "$SB/out.log" 2>&1 || sb_rc=$?; }
sb_reset; SB_CASE_G=movie:A-breach
printf 'phantom_loadtime_seconds{flow="list_load",item_type="movie",color="green"} 2.0\nphantom_loadtime_seconds{flow="list_load",item_type="episode",color="green"} 0.95\n' > "$SB/in.expo"
in_sha="$(sha256sum "$SB/in.expo" | awk '{print $1}')"
sb_guard --exposition "$SB/in.expo" --no-file --breach-out "$SB/breaches.json"
sb_check "supplied breach exposition -> exit 3 without invoking the engine" test "$sb_rc" = 3 -a ! -e "$SB/count"
sb_check "supplied exposition file preserved byte-identical" test -f "$SB/in.expo" -a "$in_sha" = "$(sha256sum "$SB/in.expo" | awk '{print $1}')"
sb_check "--breach-out survives --no-file with exact ratchet decision" python3 - "$SB/breaches.json" <<'PY'
import json, sys
plan = json.load(open(sys.argv[1]))
assert plan['breach_count'] == len(plan['entries']) == 1
entry = plan['entries'][0]
assert (entry['scenario'], entry['measured_ms'], entry['threshold_ms']) == ('list_load:movie:single', 2000, 1000)
PY
printf 'phantom_loadtime_seconds{flow="list_load",item_type="movie",color="green"} 0.5\n' > "$SB/pass.expo"
sb_guard --exposition "$SB/pass.expo" --no-file --breach-out "$SB/breaches.json"
sb_check "passing guard replaces the previous breach output with an empty plan" test "$sb_rc" = 0
sb_check "--breach-out contains no stale entries after success" python3 - "$SB/breaches.json" <<'PY'
import json, sys
assert json.load(open(sys.argv[1])) == {'breach_count': 0, 'entries': []}
PY
sb_guard --exposition "$SB/missing.expo" --no-file
sb_check "missing --exposition file -> exit 2" test "$sb_rc" = 2
: > "$SB/empty.expo"; sb_guard --exposition "$SB/empty.expo" --no-file
sb_check "empty --exposition file -> exit 2" test "$sb_rc" = 2
echo 'garbage line' > "$SB/bad.expo"; sb_guard --exposition "$SB/bad.expo" --no-file
sb_check "malformed --exposition file -> exit 2" test "$sb_rc" = 2
echo 'phantom_other_metric 1' > "$SB/none.expo"; sb_guard --exposition "$SB/none.expo" --no-file
sb_check "--exposition with no load-time measurements -> exit 2" test "$sb_rc" = 2
sb_check "no engine invocation across all --exposition runs" test ! -e "$SB/count"
sb_reset; sb_guard --no-file
sb_check "standalone guard (no --exposition) still measures: engine once, A-breach -> exit 3" \
    test "$sb_rc" = 3 -a "$(cat "$SB/count" 2>/dev/null)" = 1

# Each scheduled run starts with a new source tree, build outputs, fixture
# counter and transport logs. Only the configured PVC JSON survives runs.
head_ "durable thresholds: rendered claim mount drives three fresh daily checkouts"
TEST_TMP="$SB"
trap 'chmod -R u+rwX "$TEST_TMP"; rm -rf "$TEST_TMP"' EXIT
fixture_engine="$sb_root/tools/rig-scenarios/47-loadtime-flows.sh"
fixture_bin="$sb_bin"
durable_mount="$TEST_TMP/thresholds-pvc"
mkdir -p "$durable_mount"
durable_path="$durable_mount/loadtime-thresholds.json"
chart="$REPO_ROOT/deploy/helm/phantom-library"
if helm template durable "$chart" --set loadtimeDaily.enabled=true \
    --set-string loadtimeDaily.thresholds.existingClaim=daily-thresholds-test \
    --set-string "loadtimeDaily.thresholds.mountPath=$durable_mount" > "$TEST_TMP/chart.yaml"; then
    if python3 - "$TEST_TMP/chart.yaml" "$durable_mount" "$durable_path" "$chart/values.yaml" <<'PY'
import sys, yaml
assert yaml.safe_load(open(sys.argv[4]))['loadtimeDaily']['thresholds']['mountPath'] == '/var/lib/phantom-loadtime'
docs = list(yaml.safe_load_all(open(sys.argv[1])))
jobs = [d for d in docs if d and d.get('kind') == 'CronJob'
        and d['metadata']['name'].endswith('-loadtime-daily')]
assert len(jobs) == 1, 'expected one daily CronJob'
pod = jobs[0]['spec']['jobTemplate']['spec']['template']['spec']
container = next(c for c in pod['containers'] if c['name'] == 'loadtime-daily')
env = [e for e in container['env'] if e['name'] == 'PHANTOM_LOADTIME_THRESHOLDS_PATH']
assert env == [{'name': 'PHANTOM_LOADTIME_THRESHOLDS_PATH', 'value': sys.argv[3]}], env
mounts = [m for m in container['volumeMounts'] if m['name'] == 'thresholds-state']
assert len(mounts) == 1 and mounts[0]['mountPath'] == sys.argv[2], mounts
assert not mounts[0].get('readOnly', False) and 'subPath' not in mounts[0], mounts
volumes = [v for v in pod['volumes'] if v['name'] == 'thresholds-state']
assert volumes == [{'name': 'thresholds-state', 'persistentVolumeClaim':
                    {'claimName': 'daily-thresholds-test'}}], volumes
assert all(m['name'] != 'thresholds-state' for c in pod.get('initContainers', [])
           for m in c.get('volumeMounts', [])), 'checkout must not touch durable state'
PY
    then ok "rendered PVC, writable mount and env use the exact integration state path"
    else bad "rendered daily CronJob does not wire the configured durable state path"; fi
else
    bad "Helm rendering failed for durable thresholds integration"
fi

durable_checkout() {
    SB="$TEST_TMP/durable/$1"
    sb_root="$SB/root"; sb_bin="$SB/bin"
    mkdir -p "$sb_root/tools/ci" "$sb_root/tools/perf/ratchet-guard" \
        "$sb_root/tools/rig-scenarios" "$sb_root/scripts" "$sb_bin" "$SB/tmp"
    cp "$DAILY_SCRIPT" "$REPO_ROOT/tools/ci/lib-cleanup.sh" "$sb_root/tools/ci/"
    if [ -f "$REPO_ROOT/tools/ci/loadtime-daily-state.py" ]; then
        cp "$REPO_ROOT/tools/ci/loadtime-daily-state.py" "$sb_root/tools/ci/"
    fi
    if [ -f "$REPO_ROOT/tools/ci/loadtime-evidence.py" ]; then
        cp "$REPO_ROOT/tools/ci/loadtime-evidence.py" "$sb_root/tools/ci/"
    fi
    cp "$GUARD_SCRIPT" "$REPO_ROOT/tools/perf/loadtime-expo-to-measurements.py" "$THRESHOLDS" "$sb_root/tools/perf/"
    cp "$REPO_ROOT"/tools/perf/ratchet-guard/*.cs "$REPO_ROOT"/tools/perf/ratchet-guard/*.csproj "$sb_root/tools/perf/ratchet-guard/"
    cp "$PUSH_SCRIPT" "$sb_root/scripts/"
    cp "$fixture_engine" "$sb_root/tools/rig-scenarios/47-loadtime-flows.sh"
    cp "$fixture_bin"/* "$sb_bin/"
    : > "$SB/curl.log"; : > "$SB/beehive.log"
    SB_THR="$durable_path"
    # Keep all per-invocation temp files separate, not just the checkout.
    export TMPDIR="$SB/tmp"
}

durable_registry() {
    python3 - "$SB_THR" "$THRESHOLDS" "$1" "$2" <<'PY'
import json, sys
state = json.load(open(sys.argv[1]))
seed = json.load(open(sys.argv[2]))
expected = seed
assert len(expected['scenarios']) == 12, 'shipped registry must cover six flows x movie/episode'
for scenario in expected['scenarios']:
    if scenario['flow'] == 'list_load':
        scenario['threshold_ms'] = float(sys.argv[3]) if scenario['backend'] == sys.argv[4] else 1050
    else:
        scenario['threshold_ms'] = 1020 if scenario['flow'] in ('materialise', 'play_materialised') else 1050
def canonical(value):
    scenarios = [{k: v for k, v in s.items() if v is not None} for s in value['scenarios']]
    return {**value, 'scenarios': sorted(scenarios, key=lambda s: (s['flow'], s['backend'], s['quantile']))}
assert canonical(state) == canonical(expected), (state, expected)
PY
}

durable_batch() {
    python3 - "$SB/curl.log" "$1" "$2" <<'PY'
import re, sys
output = open(sys.argv[1]).read()
payloads = re.findall(r'^PUT \S+/metrics/job/phantom-loadtime\n(.*?)^--$', output, re.M | re.S)
assert len(payloads) == 1, f'expected exactly one measurement PUT, got {len(payloads)}'
records = re.findall(r'^phantom_loadtime_seconds\{flow="list_load",item_type="(movie|episode)",color="green"\} ([0-9.]+)$', payloads[0], re.M)
expected = {'movie': 1.0, 'episode': 1.0}
expected[sys.argv[2]] = float(sys.argv[3])
assert len(records) == 2 and {k: float(v) for k, v in records} == expected, records
PY
}

durable_filing() {
    python3 - "$SB/beehive.log" "$1" <<'PY'
import pathlib, shlex, sys
lines = [shlex.split(line) for line in pathlib.Path(sys.argv[1]).read_text().splitlines()]
backend = sys.argv[2]
if backend == 'none':
    assert not lines, lines
else:
    task = f'p8-daily-perf-regression-list-load-{backend}-single'
    assert len(lines) == 2, lines
    assert lines[0][:4] == ['task', 'add', 'phantom-library', task], lines
    assert lines[1] == ['task', 'block', 'phantom-library', 'p8-daily-regression-guard', '--on', task], lines
    body = pathlib.Path(sys.argv[1] + '.body').read_text()
    assert '800' in body and '525' in body and backend in body, body
PY
}

for backend in movie episode; do
    rm -f "$durable_path"
    for run in 1 2 3; do
        durable_checkout "$backend/$run"
        case "$run" in
            1) SB_VALUE=1.0; want_thr=1050; want_guard=0 ;;
            2) SB_VALUE=0.5; want_thr=525; want_guard=0
               # An open reader of the old inode must never observe a partial
               # rewrite; a hard link preserves that inode for a deterministic check.
               if [ -f "$SB_THR" ]; then
                   cp "$SB_THR" "$SB/before.json"
                   ln "$SB_THR" "$SB/old-inode.json"
               fi ;;
            3) SB_VALUE=0.8; want_thr=525; want_guard=3
               if [ -f "$SB_THR" ]; then cp "$SB_THR" "$SB/before.json"; fi ;;
        esac
        sb_run "$backend:durable" 0
        label="[$backend fresh checkout $run, ${SB_VALUE}s]"
        sb_check "$label wrapper exits 0" test "$sb_rc" = 0
        sb_check "$label measures once, never remeasures for guard" test "$(cat "$SB/count" 2>/dev/null)" = 1
        sb_check "$label publishes exactly the guarded batch" durable_batch "$backend" "$SB_VALUE"
        sb_check "$label cumulative stage PUTs end with guard=$want_guard" assert_stage_payloads "$SB/curl.log" "$want_guard" live
        sb_check "$label durable 12-scenario registry preserves independent ceilings and metadata" durable_registry "$want_thr" "$backend"
        sb_check "$label shipped seed remains byte-identical" cmp -s "$THRESHOLDS" "$sb_root/tools/perf/loadtime-thresholds.json"
        if [ "$run" = 2 ]; then
            sb_check "$label atomic replacement leaves the old inode byte-identical" cmp -s "$SB/before.json" "$SB/old-inode.json"
        fi
        if [ "$run" = 3 ]; then
            sb_check "$label breach never loosens/replaces durable JSON" cmp -s "$SB/before.json" "$SB_THR"
            sb_check "$label files and blocks only the breached backend with observed/ceiling evidence" durable_filing "$backend"
        else
            sb_check "$label initialization/improvement files nothing" durable_filing none
        fi
    done
    durable_checkout "$backend/dry-existing"
    SB_VALUE=0.8
    if [ -f "$SB_THR" ]; then cp "$SB_THR" "$SB/before.json"; fi
    chmod 444 "$SB_THR"
    chmod 555 "$durable_mount"
    sb_run "$backend:durable" 1
    chmod 755 "$durable_mount"
    chmod 644 "$SB_THR"
    sb_check "[$backend dry existing] reads read-only durable 525ms ceiling, reports breach without failing wrapper" test "$sb_rc" = 0 -a "$(sb_guard_stage "$SB/out.log")" = 3
    sb_check "[$backend dry existing] state byte-identical" cmp -s "$SB/before.json" "$SB_THR"
    sb_check "[$backend dry existing] cumulative simulated stages retain breach" assert_stage_payloads "$SB/out.log" 3
    sb_check "[$backend dry existing] no publication or filing" test ! -s "$SB/curl.log" -a ! -s "$SB/beehive.log"
    durable_checkout "$backend/dry-improvement"
    SB_VALUE=0.25
    if [ -f "$SB_THR" ]; then cp "$SB_THR" "$SB/before.json"; fi
    sb_run "$backend:durable" 1
    sb_check "[$backend dry improvement] successful detect-only guard" test "$sb_rc" = 0 -a "$(sb_guard_stage "$SB/out.log")" = 0
    sb_check "[$backend dry improvement] never persists tighter synthetic ceilings" cmp -s "$SB/before.json" "$SB_THR"
    sb_check "[$backend dry improvement] no publication or filing" test ! -s "$SB/curl.log" -a ! -s "$SB/beehive.log"
done

durable_checkout dry-absent
rm -f "$durable_path" "$durable_path.lock"
SB_VALUE=0.5; sb_run movie:durable 1
sb_check "[dry absent] uses seed without creating durable JSON" test "$sb_rc" = 0 -a ! -e "$SB_THR"
sb_check "[dry absent] creates no lock sidecar" test ! -e "$SB_THR.lock"
sb_check "[dry absent] no publication or filing" test ! -s "$SB/curl.log" -a ! -s "$SB/beehive.log"
sb_check "[dry absent] seed unchanged" cmp -s "$THRESHOLDS" "$sb_root/tools/perf/loadtime-thresholds.json"

durable_checkout dry-corrupt
printf '{broken json\n' > "$SB_THR"
cp "$SB_THR" "$SB/before.json"
SB_VALUE=0.5; sb_run movie:durable 1
sb_check "[dry corrupt] wrapper fails with guard=2" test "$sb_rc" != 0 -a "$(sb_guard_stage "$SB/out.log")" = 2
sb_check "[dry corrupt] existing bytes unchanged" cmp -s "$SB/before.json" "$SB_THR"
sb_check "[dry corrupt] no publication or filing" test ! -s "$SB/curl.log" -a ! -s "$SB/beehive.log"

durable_checkout busy-lock
command -v flock >/dev/null 2>&1 || fatal "flock is required for busy daily state lock coverage"
cp "$THRESHOLDS" "$SB_THR"
cp "$SB_THR" "$SB/before.json"
# The harness owns the lock; closing its descriptor in the wrapper prevents
# inheritance. Shell exit also releases it if this fixture fails early.
exec 9> "$SB_THR.lock"
flock -n 9 || fatal "could not acquire fixture threshold lock"
SB_VALUE=0.5; sb_run movie:durable 0 9>&-
flock -u 9
exec 9>&-
sb_check "[busy lock] wrapper fails with published guard=2" test "$sb_rc" != 0 -a "$(sb_guard_stage "$SB/curl.log")" = 2
sb_check "[busy lock] cumulative stage outcomes retain lock failure" assert_stage_payloads "$SB/curl.log" 2 live
sb_check "[busy lock] existing bytes unchanged" cmp -s "$SB/before.json" "$SB_THR"
sb_check "[busy lock] no regression filing" test ! -s "$SB/beehive.log"

head_ "durable state errors: visible guard=2, wrapper failure, no false breach filing"
for invalid in corrupt missing-scenarios missing-entry missing-threshold duplicate negative unwritable-file unwritable-parent absent-parent relative in-checkout unset; do
    durable_checkout "invalid-$invalid"
    mkdir -p "$SB/state"
    SB_THR="$SB/state/thresholds.json"
    cp "$THRESHOLDS" "$SB_THR"
    case "$invalid" in
        corrupt) printf '{broken json\n' > "$SB_THR" ;;
        missing-scenarios|missing-entry|missing-threshold|duplicate|negative)
            python3 - "$SB_THR" "$invalid" <<'PY'
import json, sys
path, case = sys.argv[1:]
state = json.load(open(path))
if case == 'missing-scenarios': del state['scenarios']
elif case == 'missing-entry': state['scenarios'].pop()
elif case == 'missing-threshold': del state['scenarios'][0]['threshold_ms']
elif case == 'duplicate': state['scenarios'].append(state['scenarios'][0].copy())
elif case == 'negative': state['scenarios'][0]['threshold_ms'] = -1
with open(path, 'w') as f: json.dump(state, f)
PY
            ;;
        unwritable-file) chmod 444 "$SB_THR" ;;
        unwritable-parent) chmod 555 "$SB/state" ;;
        absent-parent) SB_THR="$SB/nonexistent/thresholds.json" ;;
        relative) SB_THR=relative-thresholds.json ;;
        in-checkout) SB_THR="$sb_root/tools/perf/loadtime-thresholds.json" ;;
        unset) SB_THR="" ;;
    esac
    if [ -f "$SB_THR" ]; then cp "$SB_THR" "$SB/before.json"; fi
    SB_VALUE=0.5; sb_run movie:durable 0
    sb_check "[$invalid] wrapper fails visibly" test "$sb_rc" != 0
    sb_check "[$invalid] guard outcome 2 published before failure" test "$(sb_guard_stage "$SB/curl.log")" = 2
    sb_check "[$invalid] diagnostic identifies threshold/state failure" python3 - "$SB/out.log" <<'PY'
import re, sys
lines = open(sys.argv[1]).read().splitlines()
assert any(re.search(r'(threshold|state)', line, re.I) and
           re.search(r'(^|[\s:])(error|invalid|fail|cannot|required|refusing|not writable|must)([\s:]|$)', line, re.I)
           for line in lines), 'missing actionable threshold/state error'
PY
    sb_check "[$invalid] tool failure never files regression tasks" test ! -s "$SB/beehive.log"
    if [ -f "$SB/before.json" ]; then
        sb_check "[$invalid] rejected state is not reset to seed" cmp -s "$SB/before.json" "$SB_THR"
    else
        rejected_path="$SB_THR"
        [ "$invalid" = relative ] && rejected_path="$sb_root/$SB_THR"
        sb_check "[$invalid] invalid path creates no state" test ! -e "$rejected_path"
    fi
    chmod u+rwx "$SB/state"
done

# Unlike the counting fixtures above, this runs the LIVE 47 engine, emitter,
# converter and guard unchanged. Only network/cluster/process cleanup and task
# filing boundaries are intercepted. Git belongs to the synthetic checkout.
head_ "retained evidence: real LIVE engine through the daily wrapper"
unset TMPDIR
if python3 - "$REPO_ROOT" "$TEST_TMP/retained" <<'PY'
import json, math, os, pathlib, re, shutil, subprocess, sys, time

source, tmp = map(pathlib.Path, sys.argv[1:])
tmp.mkdir()
bin_dir = tmp / 'bin'
bin_dir.mkdir()
root = tmp / 'checkout'
state = tmp / 'state'
state.mkdir()
thresholds = state / 'thresholds.json'
evidence = state / 'loadtime-evidence'
ids = dict(Movie='2' * 32, Series='3' * 32, Season='4' * 32, Episode='5' * 32)
flows = ('list_load', 'sort_change', 'info_open', 'get_sources', 'materialise', 'play_materialised')
secrets = ('token-DO-NOT-RETAIN', 'signed-DO-NOT-RETAIN', 'body-DO-NOT-RETAIN')

(bin_dir / 'curl').write_text(r'''#!/usr/bin/env python3
import json, os, pathlib, sys
from urllib.parse import parse_qs, urlsplit
args = sys.argv[1:]
url = next(a for a in args if a.startswith(('http://', 'https://')))
p = urlsplit(url)
q = parse_qs(p.query)
mode = os.environ.get('EVIDENCE_FAULT', '')
method = args[args.index('-X') + 1] if '-X' in args else 'GET'
data = args[args.index('-d') + 1] if '-d' in args else ''
entry = dict(method=method, path=p.path, query=q)
code, http, stage = 0, '200', ''
ids = dict(Movie='2'*32, Series='3'*32, Season='4'*32, Episode='5'*32)
body = {'secret': 'body-DO-NOT-RETAIN',
        'url': 'https://media.example.net/stream?signature=signed-DO-NOT-RETAIN'}
if '/metrics/job/' in p.path:
    assert method == 'PUT', args
    payload = args[args.index('--data-binary') + 1]
    entry['payload'] = sys.stdin.read() if payload == '@-' else pathlib.Path(payload[1:]).read_text()
    if mode == 'push' and p.path.endswith('/phantom-loadtime'):
        code, http = 22, '503'
elif p.path == '/Users':
    body = [{'Id': '1'*32}]
elif p.path == '/Channels':
    body = {'Items': [{'Name': 'Phantom Movies', 'Id': 'movies'},
                      {'Name': 'Phantom Shows', 'Id': 'shows'}]}
elif p.path.startswith('/Channels/'):
    if q.get('Limit') == ['1']:
        stage = 'Movie' if '/movies/' in p.path else {
            ids['Series']: 'Season', ids['Season']: 'Episode'
        }.get(q.get('FolderId', [''])[0], 'Series')
        body = {'Items': [{'Id': ids[stage], 'Type': stage}]}
    else:
        channel = p.path.split('/')[2]
        body = {'Items': [{'Id': channel + '-card'}], 'TotalRecordCount': 1}
        if mode == 'requests' and channel == 'movies' and 'SortOrder' not in q:
            code, http = 22, '500'
elif p.path == '/Plugins/PhantomLibrary/States':
    if mode == 'requests' and 'shows-card' in data:
        code, http = 28, '000'
elif p.path.startswith(('/Items/', '/Videos/')):
    if mode == 'requests':
        code, http = (22, '404') if p.path.split('/')[2].replace('-', '') == ids['Movie'] else (28, '000')
else:
    raise SystemExit('unexpected test URL: ' + url)
fault_type, _, fault = mode.partition(':')
if stage and fault_type == stage:
    if fault == 'wrong-type': body['Items'][0]['Type'] = 'Season' if stage == 'Episode' else 'Episode'
    elif fault == 'missing-type': del body['Items'][0]['Type']
    elif fault == 'null-id': body['Items'][0]['Id'] = None
    elif fault == 'unsafe-id': body['Items'][0]['Id'] = 'bad?signature=signed-DO-NOT-RETAIN'
    elif fault == 'empty': body['Items'] = []
    elif fault == 'http': code, http = 22, '503'
text = '{broken' if stage and fault_type == stage and fault == 'malformed' else json.dumps(body)
with open(os.environ['EVIDENCE_REQUESTS'], 'a') as log:
    log.write(json.dumps(entry) + '\n')
# Observe the actual active file BEFORE any engine discovery request returns.
if p.path == '/Users':
    active = list(pathlib.Path(os.environ['EVIDENCE_EXPECT_DIR']).glob('.run-*.partial.json'))
    with open(os.environ['EVIDENCE_ACTIVE'], 'w') as log:
        json.dump([{'name': f.name, 'manifest': json.loads(f.read_text())} for f in active], log)
if mode == 'storage-loss' and stage == 'Episode':
    pathlib.Path(os.environ['EVIDENCE_EXPECT_DIR']).chmod(0o555)
if '-o' in args:
    pathlib.Path(args[args.index('-o') + 1]).write_text(text)
else:
    sys.stdout.write(text)
if '-w' in args:
    sys.stdout.write(http)
sys.exit(code)
''')
(bin_dir / 'kubectl').write_text('''#!/usr/bin/env bash
printf '%s\n' "$*" >> "$EVIDENCE_KUBE"
[ "${EVIDENCE_FAULT:-}" != color ] || exit 1
case "$*" in *phantom-library-green*) printf '%s\n' dev.example.com ;; esac
''')
(bin_dir / 'beehive').write_text('''#!/usr/bin/env bash
printf '%s\n' "$*" >> "$EVIDENCE_FILING"
''')
for path in bin_dir.iterdir():
    path.chmod(0o755)

def checkout(label):
    if root.exists():
        shutil.rmtree(root)
    for rel in ('tools/ci/loadtime-daily-run.sh', 'tools/ci/loadtime-daily-state.py',
                'tools/ci/loadtime-evidence.py', 'tools/rig-scenarios/47-loadtime-flows.sh',
                'tools/rig-scenarios/48-list-materialise-dom.mjs',
                'tools/perf/loadtime-guard.sh', 'tools/perf/loadtime-expo-to-measurements.py',
                'tools/perf/loadtime-thresholds.json', 'scripts/phantom-loadtime-push.sh'):
        dest = root / rel
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source / rel, dest)
    ratchet = root / 'tools/perf/ratchet-guard'
    ratchet.mkdir()
    for path in (source / 'tools/perf/ratchet-guard').iterdir():
        if path.suffix in ('.cs', '.csproj'):
            shutil.copy2(path, ratchet / path.name)
    (root / 'tools/ci/lib-cleanup.sh').write_text('''phantom_ci_cleanup_dotnet() {
    printf '%s\\n' "$*" >> "$EVIDENCE_CLEANUP"
}
''')
    (root / 'fixture-version').write_text(label)
    git_env = dict(os.environ, GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL='/dev/null',
                   GIT_AUTHOR_NAME='Fixture', GIT_AUTHOR_EMAIL='fixture@example.com',
                   GIT_COMMITTER_NAME='Fixture', GIT_COMMITTER_EMAIL='fixture@example.com')
    for args in (('init', '-q'), ('add', '.'), ('-c', 'core.hooksPath=/dev/null', 'commit', '-qm', label)):
        subprocess.run(['git', '-C', str(root), *args], env=git_env, check=True, capture_output=True)
    return subprocess.check_output(['git', '-C', str(root), 'rev-parse', 'HEAD'], text=True).strip()

def seed():
    shutil.copy2(source / 'tools/perf/loadtime-thresholds.json', thresholds)

base_env = {k: v for k, v in os.environ.items() if not k.startswith(('PHANTOM_', 'SB_', 'GIT_'))}
base_env.update(PATH=str(bin_dir) + os.pathsep + os.environ['PATH'],
                PHANTOM_REPO_ROOT=str(root), PHANTOM_CI_DRYRUN='0', PHANTOM_CI_PKILL='0',
                PHANTOM_LOADTIME_THRESHOLDS_PATH=str(thresholds), PHANTOM_LOADTIME_SAMPLES='2',
                PHANTOM_INCLUSTER_DEV_HOST='dev.example.com', PHANTOM_INCLUSTER_PROD_HOST='example.com',
                PHANTOM_INCLUSTER_ADMIN_TOKEN=secrets[0],
                PHANTOM_PUSHGATEWAY_URL='http://pushgateway.example.com:9091',
                MSBUILDDISABLENODEREUSE='1', DOTNET_CLI_USE_MSBUILD_SERVER='0')

def run(name, fault='', directory=None, **overrides):
    directory = evidence if directory is None else directory
    logs = tmp / name
    logs.mkdir()
    scratch = logs / 'scratch'
    scratch.mkdir()
    before = set(directory.glob('run-*.json'))
    env = dict(base_env, EVIDENCE_FAULT=fault, EVIDENCE_EXPECT_DIR=str(directory),
               EVIDENCE_REQUESTS=str(logs / 'requests'), EVIDENCE_ACTIVE=str(logs / 'active'),
               EVIDENCE_KUBE=str(logs / 'kube'), EVIDENCE_FILING=str(logs / 'filing'),
               EVIDENCE_CLEANUP=str(logs / 'cleanup'), TMPDIR=str(scratch), **overrides)
    started = int(time.time())
    result = subprocess.run(['bash', str(root / 'tools/ci/loadtime-daily-run.sh')],
                            env=env, text=True, capture_output=True, timeout=120)
    (logs / 'output').write_text(result.stdout + result.stderr)
    fresh = set(directory.glob('run-*.json')) - before
    if overrides.get('PHANTOM_CI_DRYRUN') == '1':
        assert not fresh, 'dry run wrote durable evidence'
        assert not (logs / 'requests').exists(), 'dry run accessed network'
        return result, None, None, [], logs
    assert len(fresh) == 1, (name, 'expected one finalized manifest', fresh, result.stdout, result.stderr)
    path, = fresh
    manifest = json.loads(path.read_text())
    assert re.fullmatch(r'run-[0-9a-f]{32}\.json', path.name), path
    assert manifest['version'] == 1
    assert manifest['attempt_id'] == path.name[4:-5]
    assert type(manifest['run_start']) is int and started <= manifest['run_start'] <= int(time.time())
    assert manifest['checkout_sha'] == sha, (name, manifest)
    assert type(manifest['exit_code']) is int and manifest['exit_code'] == result.returncode
    assert manifest['status'] == ('complete' if result.returncode == 0 else 'failed'), manifest
    assert manifest['color'] == ('unknown' if fault == 'color' else 'green'), manifest
    assert isinstance(manifest['targets'], dict) and isinstance(manifest['failures'], list)
    for failure in manifest['failures']:
        assert type(failure['sample_index']) is int and failure['sample_index'] > 0, failure
        assert type(failure['curl_exit_code']) is int, failure
        assert re.fullmatch(r'[0-9]{3}', failure['http_status']), failure
    assert not (directory / ('.' + path.stem + '.partial.json')).exists(), 'active file not finalized'
    for stage, outcome in manifest['stages'].items():
        assert stage in ('discovery', 'measure', 'push', 'guard'), stage
        assert set(outcome) == {'exit_code', 'start', 'seconds'}, outcome
        assert all(type(v) is int for v in outcome.values()), outcome
        assert started <= outcome['start'] <= int(time.time()) and outcome['seconds'] >= 0, outcome
    for secret in secrets:
        assert secret not in path.read_text(), (name, 'secret in retained manifest')
    assert (logs / 'cleanup').read_text().splitlines() in (['0'], ['1']), 'cleanup not called once'
    assert not list(scratch.glob('loadtime-daily-*')), (name, 'wrapper scratch leaked')
    requests = [json.loads(s) for s in (logs / 'requests').read_text().splitlines()] if (logs / 'requests').exists() else []
    if (logs / 'active').exists():
        active = json.loads((logs / 'active').read_text())
        current, = [a for a in active if a['manifest']['attempt_id'] == manifest['attempt_id']]
        assert current['name'] == f'.run-{manifest["attempt_id"]}.partial.json'
        assert current['manifest']['status'] == 'running' and current['manifest']['targets'] == {}, current
        assert current['manifest']['checkout_sha'] == sha
    return result, manifest, path, requests, logs

def stages(manifest, expected):
    assert set(manifest['stages']) == set(expected), manifest
    for stage, code in expected.items():
        assert manifest['stages'][stage]['exit_code'] == code, manifest

sha = checkout('first executing checkout')
seed()
result, first, first_path, requests, logs = run('first', 'requests')
assert result.returncode == 0, (result.stdout, result.stderr)
assert first['targets'] == ids, first
stages(first, dict(discovery=0, measure=0, push=0, guard=0))
expected_failures = []
for item_type, target, code, http in (('movie', ids['Movie'], 22, '404'), ('episode', ids['Episode'], 28, '000')):
    for flow in flows[2:]:
        for sample in (1, 2):
            expected_failures.append(dict(flow=flow, item_type=item_type, sample_index=sample,
                target=target, curl_exit_code=code, http_status=http,
                response='http' if code == 22 else 'transport/no-response', stage='request'))
for flow, item_type, channel, stage, code, http in (
        ('list_load', 'movie', 'movies', 'page_fetch', 22, '500'),
        ('list_load', 'episode', 'shows', 'badge_states', 28, '000'),
        ('sort_change', 'episode', 'shows', 'badge_states', 28, '000')):
    for sample in (1, 2):
        expected_failures.append(dict(flow=flow, item_type=item_type, sample_index=sample,
            target=channel, curl_exit_code=code, http_status=http,
            response='http' if code == 22 else 'transport/no-response', stage=stage))
canonical = lambda items: sorted(json.dumps(i, sort_keys=True) for i in items)
assert canonical(first['failures']) == canonical(expected_failures), first['failures']
payload, = [r['payload'] for r in requests if r['path'].endswith('/phantom-loadtime')]
samples = {}
for line in payload.splitlines():
    if line and not line.startswith('#'):
        key, value = line.split()
        assert key not in samples, ('duplicate metric', key)
        samples[key] = float(value)
expected_keys = set()
for item_type in ('movie', 'episode'):
    for flow in flows:
        for metric in ('seconds', 'runs_total', 'errors_total'):
            key = f'phantom_loadtime_{metric}{{flow="{flow}",item_type="{item_type}",color="green"}}'
            expected_keys.add(key)
            assert key in samples and math.isfinite(samples[key]) and samples[key] >= 0, key
            if metric == 'runs_total': assert samples[key] == 2, key
            if metric == 'errors_total': assert samples[key] == (0 if (flow, item_type) == ('sort_change', 'movie') else 2), key
    key = f'phantom_loadtime_rig_outcome_total{{flow="materialise_then_play",item_type="{item_type}",cause="gostream_register_fail"}}'
    expected_keys.add(key)
    assert samples[key] == 1, key
for flow, item_type, stage, http in (('list_load', 'movie', 'page_fetch', '500'),
        ('list_load', 'episode', 'badge_states', '000'), ('sort_change', 'episode', 'badge_states', '000')):
    key = f'phantom_loadtime_list_errors_total{{flow="{flow}",item_type="{item_type}",stage="{stage}",http_code="{http}"}}'
    expected_keys.add(key)
    assert samples[key] == 2, key
assert set(samples) == expected_keys, ('metric names/labels changed', set(samples) ^ expected_keys)
stage_puts = [r['payload'] for r in requests if r['path'].endswith('/phantom-loadtime-daily-run')]
assert len(stage_puts) == 3, stage_puts
for index, payload in enumerate(stage_puts, 1):
    actual = dict(line.split() for line in payload.splitlines())
    expected = {'phantom_loadtime_daily_run_start_timestamp_seconds'}
    for stage in ('measure', 'push', 'guard')[:index]:
        for metric in ('exit_code', 'seconds'):
            key = f'phantom_loadtime_daily_stage_{metric}{{stage="{stage}"}}'
            expected.add(key)
            assert int(actual[key]) == first['stages'][stage][metric], (key, actual, first)
    assert set(actual) == expected, ('stage metric contract changed', actual)
    assert int(actual['phantom_loadtime_daily_run_start_timestamp_seconds']) == first['run_start']
print('EVIDENCE PASS: LIVE movie HTTP404/curl22 vs episode timeout28/000, list attribution, 1-based samples, unchanged metrics')

first_bytes = first_path.read_bytes()
old_sha = sha
shutil.rmtree(root)
assert first_path.read_bytes() == first_bytes, 'evidence died with executing checkout'
sha = checkout('second executing checkout')
assert sha != old_sha
seed()
result, second, second_path, requests, logs = run('second', 'Series:wrong-type')
assert result.returncode != 0 and second['targets'] == {'Movie': ids['Movie']}, second
assert second['attempt_id'] != first['attempt_id'] and second['failures'] == [], second
assert first_path.read_bytes() == first_bytes and second_path != first_path
assert not any(r['path'].startswith(('/Items/', '/Videos/')) for r in requests), requests
print('EVIDENCE PASS: checkout deletion/recreation preserves prior artifact/SHA; partial discovery has no stale targets')

for kind in ids:
    for fault in ('wrong-type', 'missing-type', 'null-id', 'unsafe-id', 'empty', 'malformed', 'http'):
        result, manifest, _, requests, _ = run(f'discovery-{kind}-{fault}', f'{kind}:{fault}')
        assert result.returncode != 0, (kind, fault)
        preceding = list(ids)[:list(ids).index(kind)]
        assert manifest['targets'] == {k: ids[k] for k in preceding}, (kind, fault, manifest)
        assert 'push' not in manifest['stages'] and 'guard' not in manifest['stages'], manifest
        assert not any(r['path'].startswith(('/Items/', '/Videos/', '/Plugins/', '/metrics/job/phantom-loadtime/'))
                       or r['query'].get('Limit') == ['200'] or r['path'].endswith('/phantom-loadtime')
                       for r in requests), (kind, fault, requests)
print('EVIDENCE PASS: 28 typed discovery failures retain exactly the validated prefix and never time/push a batch')

result, manifest, _, requests, _ = run('color-failure', 'color')
assert result.returncode != 0 and manifest['targets'] == {} and not requests, manifest
assert manifest['stages']['discovery']['exit_code'] != 0, manifest
result, manifest, _, _, _ = run('measure-failure', PHANTOM_LOADTIME_SAMPLES='invalid')
assert result.returncode != 0 and manifest['targets'] == {}, manifest
assert manifest['stages']['measure']['exit_code'] == 2, manifest
assert 'push' not in manifest['stages'] and 'guard' not in manifest['stages'], manifest
seed()
result, manifest, _, _, _ = run('push-failure', 'push')
assert result.returncode != 0 and manifest['targets'] == ids, manifest
assert manifest['stages']['measure']['exit_code'] == 0 and manifest['stages']['push']['exit_code'] != 0, manifest
assert 'guard' not in manifest['stages'], manifest
thresholds.write_text('{broken registry')
result, manifest, _, _, logs = run('guard-failure')
assert result.returncode != 0, manifest
stages(manifest, dict(discovery=0, measure=0, push=0, guard=2))
assert thresholds.read_text() == '{broken registry' and not (logs / 'filing').exists()
print('EVIDENCE PASS: color/discovery, measure, real emitter transport, real guard failures finalize with stage outcomes')

shutil.rmtree(root / '.git')
sha = 'unavailable'
result, manifest, _, _, _ = run('checkout-sha-unavailable', PHANTOM_LOADTIME_SAMPLES='invalid')
assert result.returncode != 0 and manifest['checkout_sha'] == 'unavailable', manifest

# A source archive nested inside someone else's checkout must not inherit HEAD.
(tmp / 'unrelated-file').write_text('unrelated enclosing repository')
for args in (('init', '-q'), ('add', 'unrelated-file'),
             ('-c', 'core.hooksPath=/dev/null', '-c', 'user.name=Fixture',
              '-c', 'user.email=fixture@example.com', 'commit', '-qm', 'unrelated repository')):
    subprocess.run(['git', '-C', str(tmp), *args], env=base_env, check=True, capture_output=True)
unrelated_sha = subprocess.check_output(['git', '-C', str(tmp), 'rev-parse', 'HEAD'],
                                        env=base_env, text=True).strip()
assert re.fullmatch(r'[0-9a-f]{40}|[0-9a-f]{64}', unrelated_sha)
result, manifest, _, _, _ = run('nested-archive-sha-unavailable', PHANTOM_LOADTIME_SAMPLES='invalid')
assert result.returncode != 0 and manifest['checkout_sha'] == 'unavailable', manifest
git_overrides = dict(GIT_DIR=str(tmp / '.git'), GIT_WORK_TREE=str(root), GIT_COMMON_DIR=str(tmp / '.git'))
result, manifest, _, _, _ = run('archive-git-overrides', PHANTOM_LOADTIME_SAMPLES='invalid', **git_overrides)
assert result.returncode != 0 and manifest['checkout_sha'] == 'unavailable', manifest
sha = checkout('restored executing checkout')
assert sha != unrelated_sha
result, manifest, _, _, _ = run('checkout-git-overrides', PHANTOM_LOADTIME_SAMPLES='invalid', **git_overrides)
assert result.returncode != 0 and manifest['checkout_sha'] == sha, manifest
checkout_root = root
root = tmp / 'linked-checkout'
subprocess.run(['git', '-C', str(checkout_root), 'worktree', 'add', '--detach', str(root), 'HEAD'],
               env=base_env, check=True, capture_output=True)
assert (root / '.git').is_file(), 'fixture must exercise worktree gitfile discovery'
result, manifest, _, _, _ = run('linked-checkout-sha', PHANTOM_REPO_ROOT=str(root),
                               PHANTOM_LOADTIME_SAMPLES='invalid')
assert result.returncode != 0 and manifest['checkout_sha'] == sha, manifest
root = checkout_root
print('EVIDENCE PASS: nested source archives record unavailable; genuine checkouts keep exact SHA despite inherited Git overrides')
print('EVIDENCE PASS: linked worktree gitfile resolves its exact checkout SHA')

seed()
# Populate the configured directory with finalized artifacts, a peer's partial,
# and its retention lock; dry mode must not even rewrite identical contents.
partial = evidence / ('.run-' + 'b' * 32 + '.partial.json')
partial.write_text(json.dumps(dict(first, status='running', attempt_id='b' * 32)))
assert list(evidence.glob('run-*.json')) and (evidence / '.retention.lock').exists()
def snapshot(directory):
    return {p.name: (p.read_bytes(), p.stat().st_ino, p.stat().st_mtime_ns, p.stat().st_mode)
            for p in directory.iterdir() if p.is_file()} if directory.exists() else None

for absent in (False, True):
    explicit = tmp / 'dry-absent' if absent else evidence
    if absent: thresholds.unlink()
    lock = pathlib.Path(str(thresholds) + '.lock')
    if absent:
        if lock.exists(): lock.unlink()
    else:
        lock.write_text('existing threshold lock')
    retained_before = snapshot(explicit)
    state_before = snapshot(state)
    result, manifest, _, requests, logs = run('dry-run-' + str(absent), directory=explicit,
        PHANTOM_CI_DRYRUN='1', PHANTOM_LOADTIME_EVIDENCE_DIR=str(explicit))
    assert result.returncode == 0 and manifest is None, (result.stdout, result.stderr)
    assert not requests and not (logs / 'kube').exists() and not (logs / 'filing').exists()
    assert snapshot(state) == state_before, 'dry-run changed registry or threshold lock'
    assert snapshot(explicit) == retained_before, 'dry-run changed configured evidence artifacts/partials/lock'
    assert (root / 'tools/perf/loadtime-thresholds.json').read_bytes() == (source / 'tools/perf/loadtime-thresholds.json').read_bytes()
print('EVIDENCE PASS: dry-run preserves configured artifacts, partials, locks and registry; absent storage stays absent; no cluster/network/filing')

# Fast real-wrapper failures exercise pruning without replacing the engine.
retained = tmp / 'bounded'
retained.mkdir()
partial = retained / ('.run-' + 'a' * 32 + '.partial.json')
partial.write_text(json.dumps(dict(first, status='running', attempt_id='a' * 32)))
partial_bytes = partial.read_bytes()
unrelated = retained / 'operator-notes.json'
unrelated.write_text('leave unrelated files alone')
for index in range(35):
    attempt = f'{index:032x}'
    path = retained / f'run-{attempt}.json'
    path.write_text(json.dumps(dict(first, attempt_id=attempt, run_start=index + 1)))
    os.utime(path, (index + 1, index + 1))
result, manifest, latest, _, _ = run('default-retention', directory=retained,
    PHANTOM_LOADTIME_EVIDENCE_DIR=str(retained), PHANTOM_LOADTIME_SAMPLES='invalid')
assert result.returncode != 0 and len(list(retained.glob('run-*.json'))) == 32
assert latest.exists() and not (retained / ('run-' + '0' * 32 + '.json')).exists()
for index in range(3):
    result, manifest, latest, _, _ = run(f'bounded-{index}', directory=retained,
        PHANTOM_LOADTIME_EVIDENCE_DIR=str(retained), PHANTOM_LOADTIME_EVIDENCE_KEEP='2',
        PHANTOM_LOADTIME_SAMPLES='invalid')
    assert len(list(retained.glob('run-*.json'))) == 2 and latest.exists()
    assert partial.read_bytes() == partial_bytes and unrelated.read_text() == 'leave unrelated files alone'
print('EVIDENCE PASS: default32/configured2 retention bounds completed files, preserves partial/unrelated files')

for invalid in ('0', '-1', 'nope', '1.5'):
    logs = tmp / ('keep-invalid-' + invalid)
    logs.mkdir()
    before = {p.name: p.read_bytes() for p in retained.iterdir()}
    result = subprocess.run(['bash', str(root / 'tools/ci/loadtime-daily-run.sh')], env=dict(base_env,
        PHANTOM_LOADTIME_EVIDENCE_DIR=str(retained), PHANTOM_LOADTIME_EVIDENCE_KEEP=invalid,
        EVIDENCE_CLEANUP=str(logs / 'cleanup'), EVIDENCE_KUBE=str(logs / 'kube'),
        EVIDENCE_REQUESTS=str(logs / 'requests')), text=True, capture_output=True, timeout=30)
    assert result.returncode != 0 and not (logs / 'requests').exists() and not (logs / 'kube').exists(), invalid
    assert {p.name: p.read_bytes() for p in retained.iterdir()} == before, invalid
    assert re.search(r'(evidence|keep|retention)', result.stdout + result.stderr, re.I), result

blocked = tmp / 'unwritable'
blocked.mkdir()
blocked.chmod(0o555)
try:
    result = subprocess.run(['bash', str(root / 'tools/ci/loadtime-daily-run.sh')], env=dict(base_env,
        PHANTOM_LOADTIME_EVIDENCE_DIR=str(blocked), EVIDENCE_CLEANUP=str(tmp / 'blocked-cleanup'),
        EVIDENCE_KUBE=str(tmp / 'blocked-kube'), EVIDENCE_REQUESTS=str(tmp / 'blocked-requests')),
        text=True, capture_output=True, timeout=30)
    assert result.returncode != 0, 'unwritable evidence storage silently accepted'
    assert not list(blocked.iterdir()) and not (tmp / 'blocked-requests').exists() and not (tmp / 'blocked-kube').exists()
    assert re.search(r'evidence', result.stdout + result.stderr, re.I), result
finally:
    blocked.chmod(0o755)
print('EVIDENCE PASS: invalid retention/unwritable storage fail before discovery, never prune or publish')

seed()
interrupted = tmp / 'storage-loss'
interrupted.mkdir()
try:
    result = subprocess.run(['bash', str(root / 'tools/ci/loadtime-daily-run.sh')], env=dict(base_env,
        PHANTOM_LOADTIME_EVIDENCE_DIR=str(interrupted), EVIDENCE_FAULT='storage-loss',
        EVIDENCE_EXPECT_DIR=str(interrupted), EVIDENCE_ACTIVE=str(tmp / 'loss-active'),
        EVIDENCE_CLEANUP=str(tmp / 'loss-cleanup'), EVIDENCE_KUBE=str(tmp / 'loss-kube'),
        EVIDENCE_REQUESTS=str(tmp / 'loss-requests')), text=True, capture_output=True, timeout=30)
    assert result.returncode != 0 and 'evidence' in result.stderr
    assert not list(interrupted.glob('run-*.json')), 'storage failure reported a completed manifest'
    active, = interrupted.glob('.run-*.partial.json')
    partial = json.loads(active.read_text())
    assert partial['status'] == 'running' and partial['targets'] == {k: ids[k] for k in ('Movie', 'Series', 'Season')}
    assert thresholds.read_bytes() == (source / 'tools/perf/loadtime-thresholds.json').read_bytes()
finally:
    interrupted.chmod(0o755)
print('EVIDENCE PASS: mid-discovery storage loss fails visibly, retains readable prefix, never completes or ratchets')
PY
then
    ok "retained run evidence through real daily wrapper/engine/emitter/guard"
else
    bad "retained run evidence integration"
fi

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
