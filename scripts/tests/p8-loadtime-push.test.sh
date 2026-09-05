#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/p8-loadtime-push.test.sh
#
# In-repo regression harness for the P8 load-time EMITTER
# (scripts/phantom-loadtime-push.sh) — ROI Priority 8, item 2. Mirrors
# scripts/tests/p8-loadtime-flows.test.sh's pattern: the LIVE push (a real
# curl to the flux Pushgateway) can only run in-cluster; this harness is the
# in-sandbox, deterministic machine gate (bash + python3 only, NO network, NO
# cluster) that proves the push CONTRACT — using `--dry-run` to capture the
# PUT target + payload instead of curling.
#
# Asserts:
#   A. The emitter exists, is executable, and is `bash -n` syntax-clean.
#   B. Infra-identifier rule: the Pushgateway endpoint is ENV-overridable
#      (PHANTOM_PUSHGATEWAY_URL actually changes the PUT target) — mirroring
#      flux scripts/coldstart-bench.sh's own precedent of a baked-but-
#      overridable cluster-internal default, not a NEW hardcoded value.
#   C. `push` on a synthetic exposition file emits a well-formed PUT to
#      `/metrics/job/phantom-loadtime` carrying every phantom_loadtime_*
#      record unchanged (pass-through, not reshaped).
#   D. `push` also accepts the same exposition on STDIN (no file arg).
#   E. `run` composes the measurement engine (47-loadtime-flows.sh, DRYRUN)
#      with the push, in one step, and its dry-run output carries all six
#      canonical flows for movie AND episode.
#   F. Empty input and a missing records file are both REFUSED (non-zero
#      exit), never silently pushing nothing.
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
EMITTER="$REPO_ROOT/scripts/phantom-loadtime-push.sh"
ENGINE="$REPO_ROOT/tools/rig-scenarios/47-loadtime-flows.sh"

pass_count=0
fail_count=0
ok()    { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()   { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

head_ "A. emitter exists, executable, syntax-clean"
[[ -f "$EMITTER" ]] || fatal "emitter not found: $EMITTER"
if [[ -x "$EMITTER" ]]; then ok "emitter is executable"; else bad "emitter is not executable (chmod +x): $EMITTER"; fi
if bash -n "$EMITTER"; then ok "$EMITTER passes bash -n"; else bad "$EMITTER has a bash syntax error"; fi

FIXTURE="$(mktemp)"
trap 'rm -f "$FIXTURE"' EXIT
PHANTOM_CI_DRYRUN=1 PHANTOM_LOADTIME_COLOR=rigtest bash "$ENGINE" > "$FIXTURE" 2>/dev/null \
    || fatal "could not produce a synthetic exposition fixture from the measurement engine"

head_ "B. Pushgateway endpoint is env-driven, never a baked cluster hostname (infra-identifier rule)"
# The emitter legitimately mentions "pushgateway.example.com" as its RFC2606
# neutral --dry-run placeholder; strip that one line before checking for a
# REAL baked endpoint/hostname.
BAKED_HOST_HITS="$(grep -viE 'pushgateway\.example\.com' "$EMITTER" | grep -iE '(https?://[^ ]*(mimir|pushgateway))|([a-z0-9-]+\.)+(spencerharmon\.com|svc\.cluster\.local)|prometheus-pushgateway\.[a-z]' || true)"
if [ -n "$BAKED_HOST_HITS" ]; then
    bad "emitter bakes a Mimir/Pushgateway endpoint or cluster hostname (infra-identifier rule violation)"
    printf '%s\n' "$BAKED_HOST_HITS" | sed 's/^/      /'
else
    ok "emitter bakes no Mimir/Pushgateway endpoint or cluster hostname"
fi
DEFAULT_OUT="$("$EMITTER" push --dry-run "$FIXTURE" 2>/dev/null)" || fatal "push --dry-run (no env set) failed"
if printf '%s\n' "$DEFAULT_OUT" | head -1 | grep -q '^PUT http://pushgateway\.example\.com:9091/metrics/job/phantom-loadtime$'; then
    ok "--dry-run with no PHANTOM_PUSHGATEWAY_URL set falls back to the neutral RFC2606 placeholder (never a real host)"
else
    bad "unexpected --dry-run (no env) PUT target: $(printf '%s\n' "$DEFAULT_OUT" | head -1)"
fi
if "$EMITTER" push "$FIXTURE" >/tmp/p8-push-noenv.$$.log 2>&1; then
    bad "a LIVE push (no --dry-run) with PHANTOM_PUSHGATEWAY_URL unset should have been refused"
else
    if grep -qi 'PHANTOM_PUSHGATEWAY_URL is not set' /tmp/p8-push-noenv.$$.log; then
        ok "a live push refuses to run without PHANTOM_PUSHGATEWAY_URL set (never silently pushes to a default host)"
    else
        bad "live push failed for the wrong reason: $(cat /tmp/p8-push-noenv.$$.log)"
    fi
fi
rm -f /tmp/p8-push-noenv.$$.log
OVERRIDE_OUT="$(PHANTOM_PUSHGATEWAY_URL="http://example.com:9091" "$EMITTER" push --dry-run "$FIXTURE" 2>/dev/null)" \
    || fatal "push --dry-run (overridden url) failed"
if printf '%s\n' "$OVERRIDE_OUT" | head -1 | grep -q '^PUT http://example\.com:9091/metrics/job/phantom-loadtime$'; then
    ok "PHANTOM_PUSHGATEWAY_URL supplies the PUT target"
else
    bad "env var did not set the PUT target: $(printf '%s\n' "$OVERRIDE_OUT" | head -1)"
fi
JOB_OUT="$(PHANTOM_PUSHGATEWAY_URL="http://example.com:9091" PHANTOM_LOADTIME_PUSH_JOB="custom-job" "$EMITTER" push --dry-run "$FIXTURE" 2>/dev/null)" \
    || fatal "push --dry-run (overridden job) failed"
if printf '%s\n' "$JOB_OUT" | head -1 | grep -q '/metrics/job/custom-job$'; then
    ok "PHANTOM_LOADTIME_PUSH_JOB overrides the Pushgateway group job"
else
    bad "job override did not change the PUT target: $(printf '%s\n' "$JOB_OUT" | head -1)"
fi

head_ "C. push emits a well-formed PUT carrying every record unchanged (pass-through)"
OUT_BODY="$(printf '%s\n' "$DEFAULT_OUT" | tail -n +2)"
missing=0
while IFS= read -r line; do
    [ -z "$line" ] && continue
    if ! printf '%s\n' "$OUT_BODY" | grep -qF -- "$line"; then
        printf 'push output missing fixture line: %s\n' "$line" >&2
        missing=1
    fi
done < "$FIXTURE"
if [ "$missing" -eq 0 ]; then
    ok "every fixture exposition line is passed through to the PUT payload unchanged"
else
    bad "push payload dropped or altered one or more fixture lines"
fi

head_ "D. push accepts the same exposition on stdin (no file arg)"
STDIN_OUT="$(cat "$FIXTURE" | "$EMITTER" push --dry-run 2>/dev/null)" || fatal "push via stdin failed"
if printf '%s\n' "$STDIN_OUT" | grep -q '^phantom_loadtime_seconds{flow="list_load"'; then
    ok "push reads the exposition from stdin when no file arg is given"
else
    bad "push via stdin did not carry the expected records"
fi

head_ "E. run composes the measurement engine + push, all six flows, movie + episode"
RUN_OUT="$(PHANTOM_CI_DRYRUN=1 PHANTOM_LOADTIME_COLOR=rigtest "$EMITTER" run --dry-run 2>/dev/null)" \
    || fatal "run --dry-run failed"
if printf '%s\n' "$RUN_OUT" | head -1 | grep -q '^PUT http://pushgateway\.example\.com:9091/metrics/job/phantom-loadtime$'; then
    ok "run emits the expected PUT target"
else
    bad "run did not emit the expected PUT target: $(printf '%s\n' "$RUN_OUT" | head -1)"
fi
FLOWS=(list_load sort_change info_open get_sources materialise play_materialised)
run_missing=0
for it in movie episode; do
    for f in "${FLOWS[@]}"; do
        if ! printf '%s\n' "$RUN_OUT" | grep -q "phantom_loadtime_seconds{flow=\"$f\",item_type=\"$it\""; then
            printf 'run output missing flow=%s item_type=%s\n' "$f" "$it" >&2
            run_missing=1
        fi
    done
done
if [ "$run_missing" -eq 0 ]; then
    ok "run's pushed payload carries all six flows for movie AND episode"
else
    bad "run's pushed payload is missing one or more flow/item_type records"
fi

head_ "F. empty input and a missing records file are both refused"
if printf '' | "$EMITTER" push --dry-run >/tmp/p8-push-empty.$$.log 2>&1; then
    bad "push did not refuse empty input"
else
    ok "push refuses empty input (non-zero exit)"
fi
rm -f /tmp/p8-push-empty.$$.log
if "$EMITTER" push --dry-run /tmp/p8-push-nonexistent-records-file.$$ >/tmp/p8-push-missing.$$.log 2>&1; then
    bad "push did not refuse a missing records file"
else
    ok "push refuses a missing records file (non-zero exit)"
fi
rm -f /tmp/p8-push-missing.$$.log

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
