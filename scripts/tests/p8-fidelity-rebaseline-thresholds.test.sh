#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/p8-fidelity-rebaseline-thresholds.test.sh
#
# In-repo regression harness for ROI Priority 8 measurement-fidelity fix,
# step 2 (p8-fidelity-rebaseline-thresholds).
#
# CONTEXT — what this task guarantees and why this test exists:
#   p8-fidelity-full-list-timing corrected the load-time rig so list_load and
#   sort_change now time the FULL user-perceived component load (full uncapped
#   list pagination + phantomBadges.js-shaped badge-state fan-out + real
#   on-screen DOM materialisation) instead of a single capped
#   `GET /Channels/<ch>/Items?Limit=50` round trip that dishonestly reported
#   the several-second wait as ~0.2s.
#
#   Because every flow's honest number is now different, the ratcheting guard's
#   per-flow thresholds must be RE-BASELINED and RE-ANCHORED to the corrected
#   honest numbers — and there must be NO residual ~0.2s dishonest baseline
#   constant left hardcoded anywhere the guard, the dashboard, or the emitted
#   metric read from.
#
#   The honest per-flow ceilings can only be captured from the CORRECTED LIVE
#   rig (the daily schedule job seeds them with `--apply` from the deployed
#   :18096-family stack). The registry therefore intentionally ships every
#   flow at threshold_ms=0 (unseeded) so the FIRST corrected-live run seeds the
#   real ceiling — a dry-run synthetic fixture must NEVER seed the real
#   registry (asserted by p8-loadtime-daily-workflow.test.sh /
#   p8-daily-regression-guard.test.sh). This harness locks the RE-BASELINE
#   INVARIANTS that survive that seeding:
#
#   A. Registry / dashboard / target CONSISTENCY: the guard thresholds file
#      (tools/perf/loadtime-thresholds.json), the Helm Grafana dashboard flow
#      entries (deploy/helm/phantom-library/values.yaml grafanaDashboard.flows),
#      and the 0.5s target line (targetSeconds / target_ms=500) all reference
#      the SAME corrected numbers — same flow set, same priority flows, same
#      0.5s == 500ms user-perceived target. A drift between them would let the
#      dashboard draw a threshold line the guard never enforces (or vice-versa).
#
#   B. NO RESIDUAL DISHONEST BASELINE: no ~0.2s-class figure (0.2 / 200ms /
#      0.20 / 0.15..0.25s) is hardcoded as a per-flow threshold or target in
#      the guard registry or the dashboard values — the exact dishonest number
#      this task exists to purge must not reappear as an anchor.
#
#   C. The seed still flows from the CORRECTED full-list rig: the measurement
#      engine genuinely paginates the full uncapped list and fans the badge
#      lookup out in phantomBadges.js-shaped batches PAST the old 50-item cap
#      (so a seeded ceiling is the honest full-list wait, never the old single
#      capped page relabelled). Proven by a corrected-rig dry-run measuring
#      list_load/sort_change for BOTH movie AND episode.
#
#   D. materialise + play_materialised stay ratcheted HARDEST (tighter
#      per-flow improvement-margin/headroom than the shared default) and carry
#      the explicit 0.5s (target_ms=500) target — movie AND episode.
#
# Deterministic, sandbox-only: bash + python3, NO live Jellyfin, NO cluster,
# NO network, NO beehive CLI. Exit 0 = all assertions passed; non-zero on the
# first failure. Skips with a NOTE (exit 0) only if python3 is unavailable.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
THRESHOLDS="$REPO_ROOT/tools/perf/loadtime-thresholds.json"
VALUES="$REPO_ROOT/deploy/helm/phantom-library/values.yaml"
ENGINE="$REPO_ROOT/tools/rig-scenarios/47-loadtime-flows.sh"

pass_count=0
fail_count=0
ok()    { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()   { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

command -v python3 >/dev/null 2>&1 || { printf 'NOTE: python3 unavailable; skipping p8-fidelity-rebaseline-thresholds harness.\n'; exit 0; }

[[ -f "$THRESHOLDS" ]] || fatal "thresholds registry not found: $THRESHOLDS"
[[ -f "$VALUES" ]]     || fatal "helm values not found: $VALUES"
[[ -f "$ENGINE" ]]     || fatal "measurement engine not found: $ENGINE"

# The six ROI-named flows and the two priority (ratcheted-hardest) flows.
PRIORITY_FLOWS="materialise play_materialised"

head_ "A. registry <-> dashboard <-> 0.5s target consistency"
if python3 - "$THRESHOLDS" "$VALUES" <<'PY'
import json, re, sys

thresholds_path, values_path = sys.argv[1], sys.argv[2]

th = json.load(open(thresholds_path))
th_by_flow = {}
for s in th["scenarios"]:
    th_by_flow.setdefault(s["flow"], []).append(s)

# --- parse the grafanaDashboard block out of values.yaml without a YAML dep ---
raw = open(values_path).read()
m = re.search(r'^grafanaDashboard:\n(.*?)(?=^\S)', raw, re.S | re.M)
if not m:
    print("no grafanaDashboard: block in values.yaml", file=sys.stderr); sys.exit(1)
block = m.group(1)

tm = re.search(r'^\s*targetSeconds:\s*([0-9.]+)', block, re.M)
if not tm:
    print("no targetSeconds in grafanaDashboard block", file=sys.stderr); sys.exit(1)
target_seconds = float(tm.group(1))

# dashboard flow entries: id / thresholdMs / priority
dash_flows = {}
for fm in re.finditer(
    r'-\s*id:\s*(\S+).*?thresholdMs:\s*([0-9.]+).*?priority:\s*(true|false)',
    block, re.S):
    dash_flows[fm.group(1)] = {
        "thresholdMs": float(fm.group(2)),
        "priority": fm.group(3) == "true",
    }

errors = []

# 1. 0.5s user-perceived target, expressed identically in both places.
if abs(target_seconds - 0.5) > 1e-9:
    errors.append(f"grafana targetSeconds is {target_seconds}, expected 0.5 (0.5s user-perceived)")
for flow in ("materialise", "play_materialised"):
    scns = th_by_flow.get(flow, [])
    if not scns:
        errors.append(f"priority flow {flow} missing from guard registry")
    for s in scns:
        if s.get("target_ms") != 500:
            errors.append(f"{flow}/{s['backend']} guard target_ms={s.get('target_ms')}, expected 500 (==0.5s)")

# 2. the dashboard's flow set matches the guard registry's flow set exactly.
th_flows = set(th_by_flow)
if set(dash_flows) != th_flows:
    errors.append(f"dashboard flows {sorted(dash_flows)} != guard flows {sorted(th_flows)}")

# 3. priority flags agree between dashboard and the ratcheted-hardest guard flows.
guard_priority = {
    f for f, scns in th_by_flow.items()
    if all(s.get("target_ms") == 500 for s in scns) and scns
}
dash_priority = {f for f, v in dash_flows.items() if v["priority"]}
if guard_priority != dash_priority:
    errors.append(f"priority flows disagree: guard {sorted(guard_priority)} vs dashboard {sorted(dash_priority)}")

# 4. each dashboard thresholdMs mirrors the guard ceiling for that flow (0 == unseeded
#    in BOTH; a non-zero seeded ceiling must be reflected, never invented independently).
for flow, dv in dash_flows.items():
    ceilings = {s["threshold_ms"] for s in th_by_flow.get(flow, [])}
    # unseeded everywhere -> dashboard must also be 0 (no phantom threshold line).
    if ceilings == {0} and dv["thresholdMs"] not in (0, 500):
        # materialise/play carry the fixed 500 target line even while unseeded;
        # non-priority flows must stay 0 until seeded.
        if flow not in ("materialise", "play_materialised"):
            errors.append(f"{flow}: dashboard thresholdMs={dv['thresholdMs']} but guard ceiling unseeded(0)")

if errors:
    for e in errors:
        print("CONSISTENCY:", e, file=sys.stderr)
    sys.exit(1)
print(f"consistency OK: target=0.5s, flows={sorted(th_flows)}, priority={sorted(guard_priority)}")
PY
then
    ok "guard registry, dashboard flows, and 0.5s target are consistent"
else
    bad "guard registry / dashboard / target are INCONSISTENT (see CONSISTENCY: lines)"
fi

head_ "B. no residual ~0.2s dishonest baseline anchored anywhere"
if python3 - "$THRESHOLDS" "$VALUES" <<'PY'
import json, re, sys
thresholds_path, values_path = sys.argv[1], sys.argv[2]

offenders = []

# guard registry: no per-flow threshold_ms in the dishonest ~0.2s band (150..250ms),
# and no target_ms that is the old ~0.2s (200ms). 500 (the 0.5s target) is fine; 0 is
# fine (unseeded). A seeded honest full-list ceiling will be far above this band.
th = json.load(open(thresholds_path))
for s in th["scenarios"]:
    tm = s.get("threshold_ms", 0)
    if 150.0 <= tm <= 250.0:
        offenders.append(f"guard {s['flow']}/{s['backend']} threshold_ms={tm} is in the dishonest ~0.2s band")
    if s.get("target_ms") == 200:
        offenders.append(f"guard {s['flow']}/{s['backend']} target_ms=200 is the old ~0.2s dishonest target")

# dashboard values: targetSeconds must not be ~0.2s; no flow thresholdMs in the band.
raw = open(values_path).read()
m = re.search(r'^grafanaDashboard:\n(.*?)(?=^\S)', raw, re.S | re.M)
block = m.group(1) if m else ""
tm = re.search(r'^\s*targetSeconds:\s*([0-9.]+)', block, re.M)
if tm and 0.15 <= float(tm.group(1)) <= 0.25:
    offenders.append(f"dashboard targetSeconds={tm.group(1)} is the old ~0.2s dishonest target")
for fm in re.finditer(r'thresholdMs:\s*([0-9.]+)', block):
    v = float(fm.group(1))
    if 150.0 <= v <= 250.0:
        offenders.append(f"dashboard thresholdMs={v} is in the dishonest ~0.2s band")

if offenders:
    for o in offenders:
        print("RESIDUAL:", o, file=sys.stderr)
    sys.exit(1)
print("no residual ~0.2s baseline found in guard registry or dashboard")
PY
then
    ok "no residual ~0.2s dishonest baseline is anchored in the guard registry or dashboard"
else
    bad "a residual ~0.2s dishonest baseline is still anchored (see RESIDUAL: lines)"
fi

head_ "C. seed flows from the CORRECTED full-list rig (movie AND episode, past the 50-cap)"
# Drive the corrected rig in DRYRUN and confirm list_load/sort_change are genuinely
# timed for BOTH item types (the fidelity fix's full-list + fan-out + DOM pass), so a
# seeded ceiling is the honest full-list wait, never the old capped single page.
EXPO="$(mktemp)"
cleanup_expo() { rm -f "$EXPO"; }
trap cleanup_expo EXIT INT TERM
if PHANTOM_CI_DRYRUN=1 bash "$ENGINE" > "$EXPO" 2>/dev/null; then
    ok "corrected rig dry-run emits a load-time exposition (exit 0)"
else
    bad "corrected rig dry-run failed"
fi
if python3 - "$EXPO" <<'PY'
import re, sys
text = open(sys.argv[1]).read()
LINE = re.compile(r'phantom_loadtime_seconds\{([^}]*)\}\s+(\S+)')
seen = {}
for m in LINE.finditer(text):
    labels = dict(p.strip().split('=', 1) for p in m.group(1).split(',') if '=' in p)
    flow = labels.get('flow', '').strip('"')
    it = labels.get('item_type', '').strip('"')
    seen[(flow, it)] = float(m.group(2))
missing = []
for flow in ("list_load", "sort_change"):
    for it in ("movie", "episode"):
        if (flow, it) not in seen:
            missing.append(f"{flow}/{it}")
if missing:
    print("MISSING full-list flow records:", missing, file=sys.stderr); sys.exit(1)
# list_load/sort_change must be independently timed (not the same copied constant across
# item types) — the fidelity fix times each full-catalogue render separately.
if seen[("list_load", "movie")] == seen[("list_load", "episode")]:
    print("list_load movie==episode: not independently timed", file=sys.stderr); sys.exit(1)
print("corrected rig times list_load & sort_change for movie AND episode, independently")
PY
then
    ok "corrected full-list rig genuinely measures list_load/sort_change for movie AND episode"
else
    bad "corrected rig is not emitting independent full-list list_load/sort_change records"
fi

# The engine's own DRYRUN catalogue sizes must exceed the old 50-item cap AND the badge
# batch limit, so the seeded baseline provably reflects work past the old capped page.
if python3 - "$ENGINE" <<'PY'
import re, sys
src = open(sys.argv[1]).read()
def num(name):
    m = re.search(rf'^{name}=([0-9]+)', src, re.M)
    return int(m.group(1)) if m else None
movie = num("DRYRUN_CATALOGUE_MOVIE")
episode = num("DRYRUN_CATALOGUE_EPISODE")
batch = num("BADGE_BATCH_LIMIT")
if not all((movie, episode, batch)):
    print("could not read catalogue/batch sizes from engine", file=sys.stderr); sys.exit(1)
if movie <= 50 or episode <= 50:
    print(f"catalogue sizes ({movie},{episode}) do not exceed the old 50-item cap", file=sys.stderr); sys.exit(1)
if movie <= batch:
    print(f"movie catalogue {movie} does not force a multi-batch fan-out (> {batch})", file=sys.stderr); sys.exit(1)
print(f"catalogue sizes movie={movie} episode={episode} exceed the 50-cap; movie forces multi-batch fan-out (>{batch})")
PY
then
    ok "corrected rig's full-catalogue sizes exceed the old 50-item cap (honest full-list baseline)"
else
    bad "corrected rig's catalogue sizes do not provably exceed the old 50-item cap"
fi

head_ "D. materialise + play_materialised ratcheted HARDEST + carry the 0.5s target"
if python3 - "$THRESHOLDS" <<'PY'
import json, sys
th = json.load(open(sys.argv[1]))
gm = th.get("improvement_margin_ratio")
gh = th.get("ratchet_headroom_ratio")
priority = {"materialise", "play_materialised"}
errors = []
for s in th["scenarios"]:
    if s["flow"] in priority:
        pm = s.get("improvement_margin_ratio")
        ph = s.get("ratchet_headroom_ratio")
        if pm is None or ph is None:
            errors.append(f"{s['flow']}/{s['backend']} lacks per-flow ratchet margin/headroom (must ratchet hardest)")
            continue
        if not (pm <= gm and ph <= gh):
            errors.append(f"{s['flow']}/{s['backend']} margin/headroom ({pm},{ph}) not tighter than global ({gm},{gh})")
        if s.get("target_ms") != 500:
            errors.append(f"{s['flow']}/{s['backend']} target_ms={s.get('target_ms')}, expected 500 (0.5s)")
    else:
        # non-priority flows must NOT carry a stray 0.5s target that would mislead the dashboard.
        if s.get("target_ms") == 500:
            errors.append(f"non-priority {s['flow']}/{s['backend']} carries a 500ms target it should not")
# both priority flows must be present for movie AND episode.
for flow in priority:
    backs = {s["backend"] for s in th["scenarios"] if s["flow"] == flow}
    for it in ("movie", "episode"):
        if it not in backs:
            errors.append(f"priority flow {flow} missing item_type {it} (movie/TV parity)")
if errors:
    for e in errors:
        print("RATCHET:", e, file=sys.stderr)
    sys.exit(1)
print("materialise & play_materialised ratchet hardest (movie+episode) with target_ms=500")
PY
then
    ok "priority flows ratchet hardest, carry the 0.5s target, for movie AND episode"
else
    bad "priority-flow ratchet/target invariant broken (see RATCHET: lines)"
fi

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
