#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/jellyfin-metadata-reaper.test.sh
#
# Definition-of-done check for task jellyfin-metadata-reaper-deadline-exceeded-fix:
# the hourly jellyfin-metadata-reaper CronJob intermittently exceeded its
# 3600s activeDeadlineSeconds with NO progress output (bimodal runtime: 3-5m
# success vs stalling past 60m to DeadlineExceeded). Root cause: the
# importance-query psql invocation had no QUERY-level timeout —
# PGCONNECT_TIMEOUT only bounds establishing the TCP connection, never the
# query itself, so a slow/locked `UserData` scan could block silently for the
# whole deadline. This test asserts the two-part fix:
#
#   1. A hung/slow `psql` query is now BOUNDED — both scripts/jellyfin-
#      metadata-reaper.sh's own --db-* codepath and the CronJob template's
#      init container wrap the query in `timeout` AND a matching Postgres
#      `statement_timeout`, so the reaper degrades to atime-only LRU and
#      finishes quickly instead of hanging for the deadline.
#   2. The reaper now emits periodic progress/heartbeat log lines (elapsed
#      time, per-path scan counts, eviction progress) so any future slow run
#      is diagnosable from CronJob pod logs instead of going silent.
#   3. The chart-embedded copy of the reaper script stays byte-identical to
#      the authoritative scripts/ copy (same invariant as the sibling quota-
#      reaper test), and the rendered CronJob still wires the timeout knob.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
REAPER="$REPO_ROOT/scripts/jellyfin-metadata-reaper.sh"
CHART_DIR="$REPO_ROOT/deploy/helm/phantom-library"
CHART_COPY="$CHART_DIR/files/jellyfin-metadata-reaper.sh"

fail() { echo "FAIL: $*" >&2; exit 1; }
pass() { echo "PASS: $*"; }

# --- 0. chart-embedded copy must match the authoritative script -----------
if ! diff -q "$REAPER" "$CHART_COPY" >/dev/null 2>&1; then
  fail "deploy/helm/phantom-library/files/jellyfin-metadata-reaper.sh has drifted from scripts/jellyfin-metadata-reaper.sh"
fi
pass "chart-embedded reaper script matches the authoritative copy"

[ -x "$REAPER" ] || fail "scripts/jellyfin-metadata-reaper.sh must be committed executable"

WORK="$(mktemp -d)"
cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT

mkfile() {
  local path="$1" mb="$2" age_s="$3"
  mkdir -p "$(dirname "$path")"
  dd if=/dev/zero of="$path" bs=1M count="$mb" status=none
  touch -a -d "@$(( $(date +%s) - age_s ))" "$path"
}

# =====================================================================
# Scenario A: a HUNG psql (simulating the observed DeadlineExceeded root
# cause — a slow/locked UserData scan) must be BOUNDED by
# --db-query-timeout and NEVER allowed to stall the run. Without the fix
# this reproduces the exact symptom: the reaper would block indefinitely
# on the DB query with no output.
# =====================================================================
FIXTURE_A="$WORK/hang-db"
mkfile "$FIXTURE_A/library/item1/poster.jpg" 3 500
mkfile "$FIXTURE_A/library/item2/poster.jpg" 3 400

STUB_BIN="$WORK/hang-stubbin"
mkdir -p "$STUB_BIN"
# A psql stub that never returns (simulates a stuck/locked query). If the
# reaper's own timeout wrapping is broken, this run will hang until the test
# harness's surrounding timeout kills the whole test file — the point of
# bounding it INSIDE the reaper is that a real CronJob run must self-bound
# long before its 3600s deadline.
cat > "$STUB_BIN/psql" <<'EOF'
#!/usr/bin/env bash
sleep 300
EOF
chmod +x "$STUB_BIN/psql"

A_OUT="$WORK/hang-out.txt"
A_START=$(date +%s)
PATH="$STUB_BIN:$PATH" timeout 20 "$REAPER" \
  --root "$FIXTURE_A" --quota-gb 0 --path library \
  --db-host stub --db-name stub --db-user stub \
  --db-query-timeout 2 \
  --metrics-file "$WORK/metrics-a.prom" > "$A_OUT" 2>&1
A_RC=$?
A_ELAPSED=$(( $(date +%s) - A_START ))

[ "$A_RC" -eq 124 ] && fail "reaper did not respect --db-query-timeout: it was still running and had to be killed by the test's outer timeout (proves the DeadlineExceeded bug is NOT fixed)"
[ "$A_RC" -eq 0 ] || fail "reaper exited non-zero ($A_RC) against the hung-DB fixture; expected it to degrade and still succeed"
pass "reaper completed (exit 0) despite a permanently-hung psql query"

[ "$A_ELAPSED" -le 15 ] || fail "reaper took ${A_ELAPSED}s to finish against a hung DB query with --db-query-timeout 2 — the timeout is not actually bounding the query (this is the exact DeadlineExceeded failure mode)"
pass "hung DB query was bounded to a small slice of the run (${A_ELAPSED}s elapsed, not the full stall)"

grep -qi "timed out\|falling back to atime-only LRU\|DEGRADED to atime-only LRU" "$A_OUT" \
  || fail "reaper did not report degrading to atime-only LRU after the DB query timed out"
pass "reaper reported the DB timeout and degraded to atime-only LRU"

remaining_a=$(find "$FIXTURE_A/library" -type f | wc -l)
[ "$remaining_a" -eq 0 ] || fail "reaper did not protect the disk after the DB query timed out (0Gi quota fixture retained $remaining_a files)"
pass "disk still protected (reaped to quota) after the DB query timeout"

# =====================================================================
# Scenario B: progress/heartbeat logging must be emitted so a slow run is
# diagnosable instead of silent (the second half of the fix).
# =====================================================================
FIXTURE_B="$WORK/heartbeat"
mkfile "$FIXTURE_B/library/item1/poster.jpg" 1 500
mkfile "$FIXTURE_B/library/item2/poster.jpg" 1 400
mkfile "$FIXTURE_B/library/item3/poster.jpg" 1 300
mkfile "$FIXTURE_B/People/person1/folder.jpg" 1 200

B_OUT="$WORK/heartbeat-out.txt"
"$REAPER" --root "$FIXTURE_B" --quota-gb 0 \
  --path library --path People \
  --heartbeat-every 1 \
  --metrics-file "$WORK/metrics-b.prom" > "$B_OUT" 2>&1 \
  || { cat "$B_OUT" >&2; fail "reaper exited non-zero against the heartbeat fixture"; }

grep -q "scanning .*(elapsed" "$B_OUT" || fail "reaper did not log a per-path scan start line with elapsed time"
pass "reaper logs a scan-start line per --path with elapsed time"

grep -q "finished scanning .* candidate files so far (elapsed" "$B_OUT" || fail "reaper did not log a per-path scan-complete line with a running candidate count"
pass "reaper logs a scan-complete line per --path with candidate counts"

progress_lines=$(grep -c "progress — evicted" "$B_OUT" || true)
[ "$progress_lines" -ge 2 ] || fail "expected multiple 'progress —' heartbeat lines with --heartbeat-every 1 across >=4 evictions, got $progress_lines"
pass "eviction loop emits a progress/heartbeat line with --heartbeat-every, ${progress_lines} lines seen"

grep -q "reclaimed .*elapsed" "$B_OUT" || fail "final summary line is missing elapsed-time reporting"
pass "final summary reports elapsed time"

# =====================================================================
# Scenario C: rendered CronJob's init container must wrap its psql query in
# BOTH an outer `timeout` and a `statement_timeout`, driven by the new
# jellyfinMetadataReaper.importance.queryTimeoutSeconds value — the same
# defect class fixed for the direct --db-* codepath in Scenario A, but in the
# actual code path production traffic uses (the init container).
# =====================================================================
resolve_helm() {
  if command -v helm >/dev/null 2>&1; then
    command -v helm
    return 0
  fi
  local candidate
  for candidate in "$HOME/.local/bin/helm" "/usr/local/bin/helm" "/usr/bin/helm" "/opt/homebrew/bin/helm"; do
    if [ -x "$candidate" ]; then
      echo "$candidate"
      return 0
    fi
  done
  local vendor_tarball="$REPO_ROOT/scripts/tests/vendor/helm-v3.16.3-linux-amd64.tar.gz"
  local cache_dir="${TMPDIR:-/tmp}/phantom-library-helm-lint-vendor-cache"
  if [ -f "$vendor_tarball" ]; then
    if [ ! -x "$cache_dir/linux-amd64/helm" ]; then
      mkdir -p "$cache_dir"
      tar -xzf "$vendor_tarball" -C "$cache_dir"
    fi
    if [ -x "$cache_dir/linux-amd64/helm" ]; then
      echo "$cache_dir/linux-amd64/helm"
      return 0
    fi
  fi
  return 1
}

HELM_BIN="$(resolve_helm)" || fail "no working 'helm' binary found on PATH, in common install locations, or via the vendored release"

RENDERED="$WORK/rendered.yaml"
"$HELM_BIN" template phantom-library "$CHART_DIR" \
  --set components.jellyfinMetadata=true \
  --set jellyfinMetadataReaper.importance.enabled=true \
  --set jellyfinMetadataReaper.importance.host=db.example.com \
  --set jellyfinMetadataReaper.importance.database=jellyfin \
  --set jellyfinMetadataReaper.importance.user=jellyfin \
  --set jellyfinMetadataReaper.importance.passwordSecretName=jellyfin-postgres \
  --set jellyfinMetadataReaper.importance.queryTimeoutSeconds=45 \
  > "$RENDERED" 2> "$WORK/helm-template.log" || {
    cat "$WORK/helm-template.log" >&2
    fail "helm template failed"
  }

grep -q "kind: CronJob" "$RENDERED" || fail "helm template did not render the jellyfin-metadata-reaper CronJob"

python3 - "$RENDERED" <<'PY' || fail "rendered CronJob failed the init-container timeout invariant"
import sys
docs = open(sys.argv[1]).read().split('\n---\n')
cj = [d for d in docs if 'kind: CronJob' in d]
assert cj, "no CronJob rendered with importance enabled"
d = cj[0]
init_part = d.split('initContainers:')[1].split('\n          containers:')[0]
assert 'QUERY_TIMEOUT_SECONDS' in init_part, "init container command/env does not reference QUERY_TIMEOUT_SECONDS"
assert 'value: "45"' in init_part, "queryTimeoutSeconds value (45) was not wired into the init container env"
assert 'timeout "${QUERY_TIMEOUT_SECONDS}"' in init_part, "init container psql invocation is not wrapped in an outer `timeout`"
assert 'statement_timeout' in init_part, "init container psql invocation does not set a Postgres statement_timeout"
PY
pass "rendered CronJob's init container bounds its psql query with both timeout(1) and statement_timeout"

echo "ALL PASS: jellyfin-metadata-reaper-deadline-exceeded-fix"
