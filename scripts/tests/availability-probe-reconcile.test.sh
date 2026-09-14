#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/availability-probe-reconcile.test.sh
#
# In-repo regression harness (CHECKS.md `script-test` framework) for
# availability-probe-reconcile-001 (ROI Priority 12 dial #1 — raise the
# availability-probe success rate). This is the machine definition-of-done:
# it builds the plugin + test project (Release, shared-compilation disabled
# per AGENTS.md) and runs the real `dotnet test` regression suite scoped to
# the task's three behaviours, asserting a clean pass:
#
#   A. Torrentio/Prowlarr abstain-reconcile (MagnetSelectorTests +
#      AvailabilityProbeWorkerTests "reconcile"/"Sweep_NoImdb" cases) — the
#      oracle abstaining no longer unconditionally surfaces
#      availability_abstain/no_capable_indexer when a non-oracle indexer can
#      reach a definitive verdict at the SAME quality bar, and the reconcile
#      fan-out never fires once the oracle itself already resolved.
#   B. TTL re-probe on expiry (AvailabilityProbeWorkerTests
#      "PastNextCheckAt_IsClaimedAndReProbedOnTtlExpiry") — an expired
#      available/unavailable row is re-claimed and re-probed, never served
#      stale forever.
#   C. Bounded exponential negative-cache backoff
#      (AvailabilityProbeWorkerTests "ComputeNegativeCacheTtl"/
#      "RepeatedConfirmedNegative") — the effective unavailable TTL grows on
#      repeated confirmed-negatives, is capped at
#      AvailabilityUnavailableMaxTtlDays, and resets on any positive outcome.
#
# Movie AND episode parity is exercised by the underlying test fixtures
# themselves (see the test file for the *_Movie/_Episode pairs).
#
# Exit 0 = build + filtered test run passed. Non-zero on the first failure.
# Cleans up any dotnet build-server/test-host processes it starts, per
# AGENTS.md's mandatory build/test process cleanup rule.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
TEST_CSPROJ="$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/Jellyfin.Plugin.PhantomLibrary.Tests.csproj"

pass_count=0
fail_count=0
ok()    { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()   { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

cleanup_dotnet() {
  dotnet build-server shutdown >/dev/null 2>&1 || true
}
trap cleanup_dotnet EXIT INT TERM

command -v dotnet >/dev/null 2>&1 || fatal "dotnet SDK not found on PATH"

head_ "A. test project exists and is the expected csproj"
[[ -f "$TEST_CSPROJ" ]] || fatal "test project not found: $TEST_CSPROJ"
ok "found $TEST_CSPROJ"

head_ "B. build (Release, shared compilation disabled)"
if MSBUILDDISABLENODEREUSE=1 dotnet build "$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Jellyfin.Plugin.PhantomLibrary.csproj" \
    -c Release -p:UseSharedCompilation=false >/tmp/availability-probe-reconcile-build.log 2>&1; then
  ok "plugin builds clean"
else
  sed 's/^/    /' /tmp/availability-probe-reconcile-build.log
  bad "plugin build failed (see log above)"
fi
rm -f /tmp/availability-probe-reconcile-build.log

head_ "C. definition-of-done regression suite (reconcile + TTL reprobe + negative-cache backoff)"
FILTER='FullyQualifiedName~MagnetSelectorTests|FullyQualifiedName~AvailabilityProbeWorkerTests'
if MSBUILDDISABLENODEREUSE=1 dotnet test "$TEST_CSPROJ" -c Release -p:UseSharedCompilation=false \
    --filter "$FILTER" >/tmp/availability-probe-reconcile-test.log 2>&1; then
  ok "MagnetSelectorTests + AvailabilityProbeWorkerTests pass"
else
  sed 's/^/    /' /tmp/availability-probe-reconcile-test.log
  bad "regression suite failed (see log above)"
fi

head_ "D. the reconcile/TTL/negative-cache tests actually exist (guards against a hollowed-out filter)"
for needle in \
  "ProbeAvailabilityAsync_NoImdb_TorrentioAbstains_ProwlarrHighConfidence_ReconcilesToAvailable" \
  "ProbeAvailabilityAsync_ImdbBearing_TorrentioAvailable_NeverInvokesProwlarrReconcile" \
  "AvailableRow_PastNextCheckAt_IsClaimedAndReProbedOnTtlExpiry" \
  "UnavailableRow_PastNextCheckAt_IsClaimedAndReProbedOnTtlExpiry" \
  "ComputeNegativeCacheTtl_RepeatedConfirmedNegatives_GrowsExponentially" \
  "ComputeNegativeCacheTtl_IsBoundedByConfiguredMax" \
  "RepeatedConfirmedNegative_GrowsBackoff_ThenResetsOnPositive" \
  ; do
  if grep -rq -- "$needle" "$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/AvailabilityProbeWorkerTests.cs" \
      "$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/MagnetSelectorTests.cs" 2>/dev/null; then
    ok "test present: $needle"
  else
    bad "expected regression test missing: $needle"
  fi
done

rm -f /tmp/availability-probe-reconcile-test.log

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
