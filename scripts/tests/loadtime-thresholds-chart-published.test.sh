#!/usr/bin/env bash
# DoD for phantom-loadtime-thresholds-chart-publish: the chart version declared in source
# Chart.yaml (> 2.11.0) is PUBLISHED to the configured OCI registry and the published bytes carry the
# durable-thresholds capability (thresholds-state PVC volume + PHANTOM_LOADTIME_THRESHOLDS_PATH),
# which the published 2.11.0 lacks. Pulls the real artifact and renders it.
# Supply REGISTRY at runtime from deployment/CI configuration, for example:
# REGISTRY=oci://registry.example.com/charts scripts/tests/loadtime-thresholds-chart-published.test.sh
set -euo pipefail
fail() { echo "FAIL: $*" >&2; exit 1; }
pass() { echo "ok: $*"; }

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
[[ -n "${REGISTRY:-}" ]] || fail "REGISTRY is required (e.g. oci://registry.example.com/charts)"
CHART=phantom-library
command -v helm >/dev/null || fail "helm required"

VER="$(sed -n 's/^version: *"\{0,1\}\([0-9.]*\)"\{0,1\} *$/\1/p' "$ROOT/deploy/helm/$CHART/Chart.yaml")"
[ -n "$VER" ] || fail "cannot read version from Chart.yaml"
printf '%s\n%s\n' 2.11.0 "$VER" | sort -C -V && [ "$VER" != 2.11.0 ] \
  || fail "Chart.yaml version $VER is not > 2.11.0"
pass "source Chart.yaml version $VER > 2.11.0"

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
helm pull "$REGISTRY/$CHART" --version "$VER" -d "$WORK" >/dev/null 2>&1 \
  || fail "published chart $REGISTRY/$CHART:$VER is not pullable (not package+pushed?)"
tar xzf "$WORK/$CHART-$VER.tgz" -C "$WORK"
pass "published $CHART:$VER pullable"

RENDER="$(helm template t "$WORK/$CHART" --set nameSuffix="" --set loadtimeDaily.enabled=true \
  --set loadtimeDaily.thresholds.existingClaim=thr-claim --set loadtimeDaily.thresholds.mountPath=/var/lib/thr \
  --show-only templates/loadtime-daily-cronjob.yaml)" || fail "published $VER failed to render loadtime cronjob"
echo "$RENDER" | grep -q 'name: thresholds-state' || fail "no thresholds-state volume in published $VER"
echo "$RENDER" | grep -q 'claimName: "thr-claim"' || fail "thresholds PVC claim not wired in published $VER"
echo "$RENDER" | grep -A1 'PHANTOM_LOADTIME_THRESHOLDS_PATH' | grep -q '/var/lib/thr/loadtime-thresholds.json' \
  || fail "PHANTOM_LOADTIME_THRESHOLDS_PATH not wired in published $VER"
pass "published $VER renders durable thresholds PVC + PHANTOM_LOADTIME_THRESHOLDS_PATH"

# Published bytes must equal source for the capability template (no stale publish).
cmp -s "$WORK/$CHART/templates/loadtime-daily-cronjob.yaml" "$ROOT/deploy/helm/$CHART/templates/loadtime-daily-cronjob.yaml" \
  || fail "published $VER loadtime-daily-cronjob.yaml differs from source"
pass "published template matches source"
echo "ALL PASS"
