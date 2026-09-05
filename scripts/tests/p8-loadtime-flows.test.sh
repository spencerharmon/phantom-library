#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/p8-loadtime-flows.test.sh
#
# In-repo regression harness for the P8 load-time MEASUREMENT ENGINE
# (tools/rig-scenarios/47-loadtime-flows.sh) — ROI Priority 8, item 1.
# Mirrors migration-rig.test.sh / in-cluster-acceptance-rig.test.sh: the live
# rig can only run on the self-hosted runner / in-cluster acceptance rig; THIS
# harness is the in-sandbox, deterministic machine gate (bash + python3 only,
# NO live Jellyfin, NO cluster, NO network).
#
# Asserts:
#   A. The engine exists, is executable, and is `bash -n` syntax-clean.
#   B. It is TRAP-clean and never bakes a Mimir/Pushgateway endpoint or any
#      hostname (infra-identifier rule), and refuses the prod port :8096.
#   C. DETERMINISTIC EFFECT (dry run): all SIX canonical flows emit a
#      well-formed Prometheus record with a NUMERIC duration in seconds and the
#      correct flow/item_type labels — for a movie AND an episode.
#   D. The simulated materialise FAILURE sets the error marker (errors_total 1)
#      while STILL emitting a duration record for the attempt (failure rate
#      recorded, never dropped).
#   E. It does not touch prod :8096 and refuses if pointed there.
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# Skips with a NOTE (exit 0) if python3 is unavailable — never breaks a CI node
# lacking the tool.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
ENGINE="$REPO_ROOT/tools/rig-scenarios/47-loadtime-flows.sh"

pass_count=0
fail_count=0
ok()    { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()   { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

command -v python3 >/dev/null 2>&1 || { printf 'NOTE: python3 unavailable; skipping p8-loadtime-flows harness.\n'; exit 0; }

FLOWS=(list_load sort_change info_open get_sources materialise play_materialised)

head_ "A. engine exists, executable, syntax-clean"
[[ -f "$ENGINE" ]] || fatal "measurement engine not found: $ENGINE"
if [[ -x "$ENGINE" ]]; then ok "engine is executable"; else bad "engine is not executable (chmod +x): $ENGINE"; fi
if bash -n "$ENGINE"; then ok "$ENGINE passes bash -n"; else bad "$ENGINE has a bash syntax error"; fi

head_ "B. infra-identifier rule + trap-clean + prod-port refusal (static)"
if grep -qE 'trap .* EXIT' "$ENGINE"; then ok "engine installs an EXIT trap (trap-clean)"; else bad "engine has no EXIT trap"; fi
# No baked Mimir/Pushgateway host or endpoint — the emitter task wires the target.
# A real baked endpoint is a scheme://host, a dotted hostname, or a
# svc.cluster.local target — NOT a bare prose/task-id mention of the word
# "Pushgateway"/"Mimir" (this script legitimately names the sibling task
# p8-mimir-pushgateway-emit and describes the contract in comments).
if grep -qiE '(https?://[^ ]*(mimir|pushgateway))|([a-z0-9-]+\.)+(spencerharmon\.com|svc\.cluster\.local)|prometheus-pushgateway\.[a-z]' "$ENGINE"; then
    bad "engine bakes a Mimir/Pushgateway endpoint or hostname (infra-identifier rule violation)"
    grep -niE '(https?://[^ ]*(mimir|pushgateway))|([a-z0-9-]+\.)+(spencerharmon\.com|svc\.cluster\.local)|prometheus-pushgateway\.[a-z]' "$ENGINE" | sed 's/^/      /'
else
    ok "engine bakes no Mimir/Pushgateway endpoint or hostname"
fi
if grep -qE ':8096' "$ENGINE" && grep -qi 'refus' "$ENGINE"; then
    ok "engine refuses the production port :8096"
else
    bad "engine does not explicitly refuse the production port :8096"
fi

head_ "C. dry run emits well-formed records for all six flows, movie + episode"
OUT="$(PHANTOM_CI_DRYRUN=1 PHANTOM_LOADTIME_COLOR=rigtest bash "$ENGINE" 2>/dev/null)" \
    || fatal "dry run of the engine exited non-zero"
printf '%s\n' "$OUT" | sed 's/^/    /' | head -40
EXPO_FILE="$(mktemp)"; printf '%s\n' "$OUT" > "$EXPO_FILE"

# Prometheus exposition well-formedness + label/duration assertions in python3.
# (python reads the exposition from a FILE arg — its own script arrives on stdin
# via the heredoc, so the two cannot share stdin.)
if python3 - "$EXPO_FILE" "${FLOWS[@]}" <<'PY'
import re, sys
expo_file = sys.argv[1]
flows = sys.argv[2:]
text = open(expo_file).read()
seconds = {}   # (flow,item) -> float
runs = {}
errors = {}
line_re = re.compile(
    r'^phantom_loadtime_(seconds|runs_total|errors_total)\{([^}]*)\}\s+(\S+)\s*$')
def labels(s):
    d = {}
    for part in s.split(','):
        k, v = part.split('=', 1)
        d[k.strip()] = v.strip().strip('"')
    return d
for ln in text.splitlines():
    ln = ln.strip()
    if not ln or ln.startswith('#'):
        continue
    m = line_re.match(ln)
    if not m:
        print(f"malformed exposition line: {ln!r}", file=sys.stderr)
        sys.exit(1)
    metric, lbl, val = m.group(1), labels(m.group(2)), m.group(3)
    for req in ('flow', 'item_type', 'color'):
        if req not in lbl:
            print(f"line missing '{req}' label: {ln!r}", file=sys.stderr); sys.exit(1)
    key = (lbl['flow'], lbl['item_type'])
    try:
        fval = float(val)
    except ValueError:
        print(f"non-numeric value {val!r} in: {ln!r}", file=sys.stderr); sys.exit(1)
    if metric == 'seconds':
        if fval < 0:
            print(f"negative duration {fval} in: {ln!r}", file=sys.stderr); sys.exit(1)
        seconds[key] = fval
    elif metric == 'runs_total':
        runs[key] = fval
    else:
        errors[key] = fval

problems = 0
for it in ('movie', 'episode'):
    for f in flows:
        k = (f, it)
        if k not in seconds:
            print(f"missing seconds record for flow={f} item_type={it}", file=sys.stderr); problems += 1
        if k not in runs:
            print(f"missing runs_total for flow={f} item_type={it}", file=sys.stderr); problems += 1
        if k not in errors:
            print(f"missing errors_total for flow={f} item_type={it}", file=sys.stderr); problems += 1
# both priority signals present for both item types
for it in ('movie', 'episode'):
    for f in ('materialise', 'play_materialised'):
        if (f, it) not in seconds:
            print(f"PRIORITY signal missing: {f}/{it}", file=sys.stderr); problems += 1
sys.exit(1 if problems else 0)
PY
then
    ok "all six flows emit a well-formed numeric-seconds record for movie AND episode"
else
    bad "dry-run exposition failed well-formedness / coverage assertions"
fi
rm -f "$EXPO_FILE"

head_ "D. simulated materialise failure sets the error marker (rate recorded, not dropped)"
FOUT="$(PHANTOM_CI_DRYRUN=1 PHANTOM_LOADTIME_COLOR=rigtest PHANTOM_LOADTIME_FORCE_MATERIALISE_FAIL=1 \
    bash "$ENGINE" 2>/dev/null)" || fatal "forced-failure dry run exited non-zero"
FEXPO_FILE="$(mktemp)"; printf '%s\n' "$FOUT" > "$FEXPO_FILE"
if python3 - "$FEXPO_FILE" <<'PY'
import re, sys
text = open(sys.argv[1]).read()
def get(metric, flow, item):
    for ln in text.splitlines():
        ln = ln.strip()
        if ln.startswith(f'phantom_loadtime_{metric}{{') and f'flow="{flow}"' in ln and f'item_type="{item}"' in ln:
            return ln.rsplit(None, 1)[-1]
    return None
ok = True
for it in ('movie', 'episode'):
    err = get('errors_total', 'materialise', it)
    dur = get('seconds', 'materialise', it)
    if err != '1':
        print(f"materialise/{it} errors_total expected 1, got {err!r}", file=sys.stderr); ok = False
    if dur is None:
        print(f"materialise/{it} still must emit a duration record on failure", file=sys.stderr); ok = False
    else:
        try: float(dur)
        except (TypeError, ValueError):
            print(f"materialise/{it} duration non-numeric: {dur!r}", file=sys.stderr); ok = False
# a non-failing flow keeps errors 0
if get('errors_total', 'list_load', 'movie') != '0':
    print("list_load/movie errors_total should be 0 in the forced-fail run", file=sys.stderr); ok = False
sys.exit(0 if ok else 1)
PY
then
    ok "materialise failure sets errors_total=1 for movie+episode while still emitting a duration"
else
    bad "materialise-failure error marker not recorded correctly"
fi
rm -f "$FEXPO_FILE"

head_ "E. prod-port refusal self-test (dry run pointed at :8096 must refuse)"
if PHANTOM_CI_DRYRUN=1 PHANTOM_LOADTIME_API=http://localhost:8096 bash "$ENGINE" >/tmp/p8-prodguard.$$.log 2>&1; then
    bad "engine did NOT refuse when pointed at production port :8096"
else
    if grep -qi 'refus' /tmp/p8-prodguard.$$.log; then
        ok "engine refuses when pointed at production port :8096"
    else
        bad "engine exited non-zero but not via the expected prod-port refusal"
    fi
fi
rm -f /tmp/p8-prodguard.$$.log

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
