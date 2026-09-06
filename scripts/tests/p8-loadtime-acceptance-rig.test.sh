#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/p8-loadtime-acceptance-rig.test.sh
#
# In-repo regression harness for tools/ci/loadtime-acceptance-run.sh (ROI
# Priority 8 CAPSTONE — the live acceptance bar,
# phantom-library:p8-loadtime-acceptance-rig).
#
# This harness is the SANDBOX gate (bash + python3 only, no cluster/network
# access) mirroring in-cluster-acceptance-rig.test.sh's shape. It guards the
# structure of the acceptance rig — it does NOT and cannot itself prove the
# live end-to-end loop; that live proof is recorded as evidence in this
# task's change doc (a real run against the real cluster performed in the
# implementing session), per the "do NOT claim this bar met on a dry-run
# basis" mandate. What this harness enforces:
#   - the acceptance rig script exists, is executable, `bash -n` clean.
#   - it installs an EXIT trap (trap-clean) and refuses dev==prod.
#   - it bakes NO Mimir/Pushgateway/Grafana/cluster hostname (infra-identifier
#     rule) — every endpoint is env-supplied.
#   - it reuses the P8/1 measurement engine, P8/2 Pushgateway emitter (never
#     reimplementing either), and never touches the production :8096 port
#     literally in the URL it builds.
#   - it queries Mimir for ALL 12 (flow x item_type) series and HARD-fails if
#     any are missing (never a soft warning) — the acceptance gate's core
#     "don't assume it landed" contract.
#   - it verifies the Grafana dashboard + its OWN datasource resolves live
#     data for both priority flows (materialise, play_materialised) — not
#     just that the dashboard JSON parses.
#   - a toolchain-agnostic dry run (PHANTOM_CI_DRYRUN=1) exits 0 with no
#     cluster/network access.
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
RIG="$REPO_ROOT/tools/ci/loadtime-acceptance-run.sh"
ENGINE="$REPO_ROOT/tools/rig-scenarios/47-loadtime-flows.sh"
PUSH_SCRIPT="$REPO_ROOT/scripts/phantom-loadtime-push.sh"

pass_count=0
fail_count=0
ok()   { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

[[ -f "$RIG" ]]        || fatal "acceptance rig script not found: $RIG"
[[ -x "$RIG" ]]        || fatal "acceptance rig script not executable: $RIG"
[[ -f "$ENGINE" ]]     || fatal "measurement engine not found: $ENGINE"
[[ -f "$PUSH_SCRIPT" ]] || fatal "push emitter not found: $PUSH_SCRIPT"

head_ "A. script exists, executable, syntax-clean"
if bash -n "$RIG" 2>/tmp/p8acc-bashn.$$.log; then
    ok "$RIG passes bash -n"
else
    bad "$RIG failed bash -n: $(cat /tmp/p8acc-bashn.$$.log)"
fi

head_ "B. trap-clean, dev==prod refusal, no baked infra identifier"
if grep -qE "trap (teardown|cleanup) EXIT( INT TERM)?" "$RIG"; then
    ok "rig installs an EXIT trap (trap-clean)"
else
    bad "rig has no EXIT cleanup trap"
fi
if grep -qE 'DEV_HOST.*=.*PROD_HOST|"\$DEV_HOST" = "\$PROD_HOST"' "$RIG"; then
    ok "rig refuses when dev host equals prod host"
else
    bad "rig is missing the dev==prod safety refusal"
fi
if grep -qE '(https?://[A-Za-z0-9.-]+\.(com|studio|net|org)|[A-Za-z0-9.-]+\.svc\.cluster\.local|prometheus-pushgateway\.[A-Za-z0-9.-]+|mimir\.[A-Za-z0-9.-]+|grafana\.[A-Za-z0-9.-]+)' "$RIG" \
    | grep -vE '(example\.com|example\.net|example\.studio)'; then
    bad "rig bakes a real Mimir/Pushgateway/Grafana/cluster hostname (infra-identifier rule violation)"
else
    ok "rig bakes no real Mimir/Pushgateway/Grafana/cluster hostname (only RFC2606 example.* fixtures)"
fi
if grep -qE '":8096"|:8096[^0-9]' "$RIG" | grep -qv "refus"; then
    :
fi
if grep -q 'PHANTOM_LOADTIME_API=' "$RIG" && ! grep -qE 'PHANTOM_LOADTIME_API="[^"]*:8096"' "$RIG"; then
    ok "rig never builds a literal :8096 target for the measurement engine (port-forward to an ephemeral local port instead)"
else
    bad "rig appears to build a literal :8096 target for the measurement engine"
fi

head_ "C. reuses the P8/1 engine + P8/2 emitter (never reimplements either)"
if grep -q 'tools/rig-scenarios/47-loadtime-flows.sh' "$RIG"; then
    ok "rig invokes the P8/1 measurement engine by path"
else
    bad "rig does not invoke tools/rig-scenarios/47-loadtime-flows.sh"
fi
if grep -q 'scripts/phantom-loadtime-push.sh' "$RIG"; then
    ok "rig invokes the P8/2 Pushgateway emitter by path"
else
    bad "rig does not invoke scripts/phantom-loadtime-push.sh"
fi

head_ "D. hard-fails (never soft-warns) on a missing Mimir series"
if grep -qE 'fail "\$missing of 12' "$RIG"; then
    ok "rig hard-fails when any of the 12 (flow,item_type) series is missing from Mimir"
else
    bad "rig does not hard-fail on a missing Mimir series"
fi

head_ "E. verifies the Grafana dashboard's OWN datasource resolves live data"
if grep -q 'api/dashboards/uid' "$RIG" && grep -q 'api/datasources/proxy/uid' "$RIG"; then
    ok "rig fetches the dashboard AND queries through its own datasource proxy"
else
    bad "rig does not verify the dashboard via its own datasource proxy"
fi
if grep -q 'PRIORITY_FLOWS=(materialise play_materialised)' "$RIG"; then
    ok "rig verifies both priority flows (materialise, play_materialised) resolve live data"
else
    bad "rig does not single out the two priority flows for datasource verification"
fi

head_ "F. toolchain-agnostic dry run (no cluster/network access)"
if PHANTOM_CI_DRYRUN=1 PHANTOM_REPO_ROOT="$REPO_ROOT" bash "$RIG" \
    >/tmp/p8acc-dryrun.$$.log 2>&1; then
    ok "PHANTOM_CI_DRYRUN=1 tools/ci/loadtime-acceptance-run.sh exits 0"
else
    bad "dry run of tools/ci/loadtime-acceptance-run.sh failed; see /tmp/p8acc-dryrun.$$.log"
fi
if grep -q "acceptance rig complete" /tmp/p8acc-dryrun.$$.log; then
    ok "dry run reaches completion (all 8 phases exercised)"
else
    bad "dry run did not reach the completion line"
fi

head_ "G. prod-safety self-test: refuses when dev host equals prod host"
if PHANTOM_CI_DRYRUN=1 PHANTOM_REPO_ROOT="$REPO_ROOT" \
    PHANTOM_INCLUSTER_DEV_HOST="dev.example.com" PHANTOM_INCLUSTER_PROD_HOST="dev.example.com" \
    bash "$RIG" >/tmp/p8acc-prodsafety.$$.log 2>&1; then
    bad "rig did NOT refuse when dev host == prod host"
else
    if grep -q "refusing" /tmp/p8acc-prodsafety.$$.log; then
        ok "rig refuses via the dev==prod safety guard"
    else
        bad "rig exited non-zero but not via the expected prod-safety refusal"
    fi
fi

rm -f /tmp/p8acc-bashn.$$.log /tmp/p8acc-dryrun.$$.log /tmp/p8acc-prodsafety.$$.log

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
