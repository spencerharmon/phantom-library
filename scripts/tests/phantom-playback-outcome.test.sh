#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/phantom-playback-outcome.test.sh
#
# In-repo regression harness (CHECKS.md `script-test` framework) for the
# DEFINITIVE per-attempt playback-outcome metric
# (playback-outcome-instrumentation-001). It is the machine definition-of-done
# for that task: it drives the load-time MEASUREMENT ENGINE
# (tools/rig-scenarios/47-loadtime-flows.sh) in dry-run (bash + python3 only,
# NO live Jellyfin, NO cluster, NO network) and asserts the
# `phantom_playback_outcome_total{flow,item_type,cause}` record CONTRACT:
#
#   A. The engine exists, is executable, and is `bash -n` syntax-clean.
#   B. Dry run emits, for a movie AND an episode, exactly one definitive
#      `phantom_playback_outcome_total` record per playback FLOW
#      (materialise_then_play, play_already_materialised), with the required
#      flow/item_type/cause labels and a value of 1.
#   C. The cause label is always drawn from the eight-value contract vocabulary
#      (success, availability_abstain, no_candidate, magnet_dead_stale,
#      gostream_register_fail, gostream_cannot_fetch, first_byte_timeout,
#      plugin_host_error).
#   D. A clean dry run records cause="success" for every attempt (no dropped
#      failure masquerading as success), and a FORCED failure emits a
#      NON-success definitive cause for the materialise_then_play flow for both
#      item types — the failure is recorded, never silently dropped.
#   E. Movie/episode parity: every flow present for movie is present for episode.
#
# Exit 0 = all assertions passed; non-zero on the first failure. Skips with a
# NOTE (exit 0) if python3 is unavailable — never breaks a CI node lacking it.
# The runner exec's this by its shebang path (the check sandbox denies the bare
# word `bash` as a check command); it is committed executable.
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

command -v python3 >/dev/null 2>&1 || { printf 'NOTE: python3 unavailable; skipping phantom-playback-outcome harness.\n'; exit 0; }

PLAYBACK_FLOWS=(materialise_then_play play_already_materialised)
CAUSES=(success availability_abstain no_candidate magnet_dead_stale gostream_register_fail gostream_cannot_fetch first_byte_timeout plugin_host_error)

head_ "A. engine exists, executable, syntax-clean"
[[ -f "$ENGINE" ]] || fatal "measurement engine not found: $ENGINE"
if [[ -x "$ENGINE" ]]; then ok "engine is executable"; else bad "engine is not executable (chmod +x): $ENGINE"; fi
if bash -n "$ENGINE"; then ok "$ENGINE passes bash -n"; else bad "$ENGINE has a bash syntax error"; fi

head_ "B/C/E. dry run emits the outcome contract for both flows, movie + episode"
OUT="$(PHANTOM_CI_DRYRUN=1 PHANTOM_LOADTIME_COLOR=rigtest bash "$ENGINE" 2>/dev/null)" \
    || fatal "dry run of the engine exited non-zero"
printf '%s\n' "$OUT" | grep phantom_playback_outcome_total | sed 's/^/    /'
EXPO_FILE="$(mktemp)"; printf '%s\n' "$OUT" > "$EXPO_FILE"

if python3 - "$EXPO_FILE" "${CAUSES[@]}" <<'PY'
import re, sys
expo_file = sys.argv[1]
causes = set(sys.argv[2:])
text = open(expo_file).read()
line_re = re.compile(r'^phantom_playback_outcome_total\{([^}]*)\}\s+(\S+)\s*$')
def labels(s):
    d = {}
    for part in s.split(','):
        k, v = part.split('=', 1)
        d[k.strip()] = v.strip().strip('"')
    return d
seen = {}   # (flow,item_type) -> cause
problems = 0
for ln in text.splitlines():
    ln = ln.strip()
    if not ln or ln.startswith('#') or not ln.startswith('phantom_playback_outcome_total'):
        continue
    m = line_re.match(ln)
    if not m:
        print(f"malformed outcome line: {ln!r}", file=sys.stderr); problems += 1; continue
    lbl, val = labels(m.group(1)), m.group(2)
    for req in ('flow', 'item_type', 'cause'):
        if req not in lbl:
            print(f"outcome line missing '{req}' label: {ln!r}", file=sys.stderr); problems += 1
    if val != '1':
        print(f"outcome value must be 1, got {val!r}: {ln!r}", file=sys.stderr); problems += 1
    if lbl.get('cause') not in causes:
        print(f"cause {lbl.get('cause')!r} outside the contract vocabulary: {ln!r}", file=sys.stderr); problems += 1
    key = (lbl.get('flow'), lbl.get('item_type'))
    if key in seen:
        print(f"more than one definitive outcome for {key}: {ln!r}", file=sys.stderr); problems += 1
    seen[key] = lbl.get('cause')
# both flows present for both item types (movie/episode parity)
for it in ('movie', 'episode'):
    for f in ('materialise_then_play', 'play_already_materialised'):
        if (f, it) not in seen:
            print(f"missing definitive outcome for flow={f} item_type={it}", file=sys.stderr); problems += 1
sys.exit(1 if problems else 0)
PY
then
    ok "both playback flows emit exactly one contract-valid outcome for movie AND episode"
else
    bad "dry-run outcome exposition failed the record contract"
fi
rm -f "$EXPO_FILE"

head_ "D. clean run is all-success; forced failure emits a definitive non-success cause (not dropped)"
CLEAN="$(PHANTOM_CI_DRYRUN=1 PHANTOM_LOADTIME_COLOR=rigtest bash "$ENGINE" 2>/dev/null)" || fatal "clean dry run exited non-zero"
if printf '%s\n' "$CLEAN" | grep -E '^phantom_playback_outcome_total' | grep -qvE 'cause="success"'; then
    bad "a clean dry run emitted a non-success cause (a dropped failure would look like this)"
else
    ok "clean dry run records cause=\"success\" for every attempt"
fi

FOUT="$(PHANTOM_CI_DRYRUN=1 PHANTOM_LOADTIME_COLOR=rigtest PHANTOM_LOADTIME_FORCE_PLAYBACK_FAIL_CAUSE=no_candidate \
    bash "$ENGINE" 2>/dev/null)" || fatal "forced-failure dry run exited non-zero"
FEXPO_FILE="$(mktemp)"; printf '%s\n' "$FOUT" > "$FEXPO_FILE"
if python3 - "$FEXPO_FILE" <<'PY'
import sys
text = open(sys.argv[1]).read()
def cause_for(flow, item):
    for ln in text.splitlines():
        ln = ln.strip()
        if ln.startswith('phantom_playback_outcome_total{') and f'flow="{flow}"' in ln and f'item_type="{item}"' in ln:
            # cause="X"
            for part in ln[ln.index('{')+1:ln.index('}')].split(','):
                k, v = part.split('=', 1)
                if k.strip() == 'cause':
                    return v.strip().strip('"')
    return None
ok = True
for it in ('movie', 'episode'):
    c = cause_for('materialise_then_play', it)
    if c is None:
        print(f"materialise_then_play/{it} outcome missing on forced failure", file=sys.stderr); ok = False
    elif c == 'success':
        print(f"materialise_then_play/{it} forced failure was recorded as success (dropped failure)", file=sys.stderr); ok = False
    elif c != 'no_candidate':
        print(f"materialise_then_play/{it} forced cause expected no_candidate, got {c!r}", file=sys.stderr); ok = False
sys.exit(0 if ok else 1)
PY
then
    ok "forced failure emits the definitive non-success cause for movie+episode (never dropped)"
else
    bad "forced-failure definitive cause not recorded correctly"
fi
rm -f "$FEXPO_FILE"

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
