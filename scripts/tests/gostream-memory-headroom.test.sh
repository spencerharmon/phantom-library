#!/usr/bin/env bash
# DoD for gostream-shared-oom-stream-drop-001: render the chart with `helm template` and assert the
# gostream memory budget leaves headroom for concurrent pumps (incident 2026-10-31 OOMKill at
# 3Gi / GOMEMLIMIT=2200MiB, idle ~2091MiB, peak ~2858MiB with two pumps).
#  1. GOMEMLIMIT is rendered and DERIVED (= limitMiB*gomemlimitPercent/100; scales with the limit).
#  2. GOMEMLIMIT <= 75% of the limit; headroom (limit-GOMEMLIMIT) >= config.read_ahead_budget_mb.
#  3. limit >= baseline + maxConcurrentStreams * perStreamBudgetMiB, and >= observed peak 2858MiB
#     plus one more pump (~380MiB).
#  4. An under-budgeted override is REFUSED at render time.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
CHART="$ROOT/deploy/helm/phantom-library"
VALUES="$CHART/values.yaml"
VENDOR_TARBALL="$SCRIPT_DIR/vendor/helm-v3.16.3-linux-amd64.tar.gz"
CACHE_DIR="${TMPDIR:-/tmp}/phantom-library-helm-lint-vendor-cache"
HELM="$(command -v helm || true)"
for c in "$HOME/.local/bin/helm" /usr/local/bin/helm /usr/bin/helm; do [ -n "$HELM" ] || { [ -x "$c" ] && HELM="$c"; }; done
if [ -z "$HELM" ]; then
  mkdir -p "$CACHE_DIR"; [ -x "$CACHE_DIR/linux-amd64/helm" ] || tar -xzf "$VENDOR_TARBALL" -C "$CACHE_DIR"
  HELM="$CACHE_DIR/linux-amd64/helm"
fi
fail=0; ok(){ echo "ok   - $*"; }; bad(){ echo "FAIL - $*"; fail=1; }

gomem() { # $@ extra helm args -> GOMEMLIMIT MiB (empty if absent)
  "$HELM" template t "$CHART" "$@" | awk '/name: GOMEMLIMIT/{getline; gsub(/[^0-9]/,"",$0); print; exit}'
}
val() { awk -v k="$1" '$1==k":"{print $2; exit}' "$VALUES"; }

g="$(gomem)"
limit="$(val limitMiB)"; pct="$(val gomemlimitPercent)"; base="$(val baselineMiB)"
per="$(val perStreamBudgetMiB)"; streams="$(val maxConcurrentStreams)"; ra="$(val read_ahead_budget_mb)"
echo "rendered GOMEMLIMIT=${g:-<none>}MiB limitMiB=${limit:-<none>} pct=${pct:-?} base=${base:-?} per=${per:-?} streams=${streams:-?} read_ahead=${ra:-?}"
if [ -z "$g" ] || [ -z "$limit" ] || [ -z "$pct" ] || [ -z "$base" ] || [ -z "$per" ] || [ -z "$streams" ]; then
  bad "gostream.memory budget / rendered GOMEMLIMIT missing"; exit 1
fi
[ "$g" -eq $(( limit * pct / 100 )) ] && ok "GOMEMLIMIT derived from limit" || bad "GOMEMLIMIT $g != $limit*$pct/100"
g2="$(gomem --set gostream.memory.limitMiB=8192)"
[ "${g2:-0}" -eq $(( 8192 * pct / 100 )) ] && ok "GOMEMLIMIT scales with limit ($g2)" || bad "GOMEMLIMIT not derived (8Gi -> $g2)"
[ $(( g * 100 )) -le $(( limit * 75 )) ] && ok "GOMEMLIMIT <= 75% of limit" || bad "GOMEMLIMIT > 75% of limit"
[ $(( limit - g )) -ge "$ra" ] && ok "headroom $((limit-g))MiB >= read_ahead_budget ${ra}MiB" || bad "headroom < read_ahead_budget"
[ "$limit" -ge $(( base + streams * per )) ] && ok "limit covers baseline + $streams streams" || bad "limit < baseline + streams*per"
[ "$limit" -ge $(( 2858 + 380 )) ] && ok "limit covers incident peak + one pump" || bad "limit < incident peak + one pump"
if "$HELM" template t "$CHART" --set gostream.memory.limitMiB=3072 >/dev/null 2>&1; then
  bad "under-budgeted limit (3072MiB) was not refused"
else ok "under-budgeted limit refused at render"; fi
exit $fail
