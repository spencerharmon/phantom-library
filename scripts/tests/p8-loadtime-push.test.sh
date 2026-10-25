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

head_ "G. engine exposition is well-formed Prometheus text (parser-validated)"
# Regression for ttfb-loadtime-push-exposition-newline-fix: flush_records() used
# to glue the load-time TYPE header block directly onto the outcome-metric HELP
# header (printf '%s%s\n...' with the only newline AFTER $outcome_header, not
# between $header and $outcome_header), producing a malformed line like
#   `# TYPE ... counter# HELP phantom_loadtime_rig_outcome_total ...`
# that the Pushgateway/promtool parser rejects with
#   `text format parsing error in line N: unknown metric type "counter# HELP ..."`.
# This silently discarded every fresh measurement batch (HTTP 400). The engine
# ALWAYS emits an outcome-metric block in live mode, and the DRYRUN path here
# also triggers emit_playback_outcome, so the DRYRUN exposition reproduces the
# concatenation. Validate it through a REAL Prometheus text-format parser.
validate_wellformed() {
    local label="$1" text="$2"
    if command -v promtool >/dev/null 2>&1; then
        if printf '%s\n' "$text" | promtool check metrics >/tmp/p8-promtool.$$.log 2>&1; then
            ok "$label exposition is valid Prometheus text (promtool check metrics exit 0)"
        else
            bad "$label exposition is MALFORMED Prometheus text (promtool rejected it):"
            sed 's/^/      /' /tmp/p8-promtool.$$.log >&2
        fi
        rm -f /tmp/p8-promtool.$$.log
    else
        # Fallback structural validator: no line may contain a metric-type token
        # (`counter`/`gauge`/`histogram`/`summary`/`untyped`) immediately followed
        # by a `#` directive, i.e. two segments concatenated onto one physical line.
        if printf '%s\n' "$text" | grep -Eq '(counter|gauge|histogram|summary|untyped)#'; then
            bad "$label exposition has two directives/samples concatenated onto one line (promtool unavailable; structural check)"
            printf '%s\n' "$text" | grep -En '(counter|gauge|histogram|summary|untyped)#' | sed 's/^/      /' >&2
        else
            ok "$label exposition has no concatenated lines (promtool unavailable; structural check)"
        fi
    fi
}
ENGINE_DRYRUN_OUT="$(PHANTOM_CI_DRYRUN=1 PHANTOM_LOADTIME_COLOR=rigtest bash "$ENGINE" 2>/dev/null)" \
    || fatal "could not run the measurement engine for the well-formedness assertion"
validate_wellformed "DRYRUN engine" "$ENGINE_DRYRUN_OUT"

head_ "H. synthetic runs and reserved playback samples never reach the push transport (intercepted curl)"
# A test-local `curl` on PATH intercepts the REAL emitter's transport: it
# records each call (args + stdin body) and never contacts any network.
STUB_DIR="$(mktemp -d)"
trap 'rm -f "$FIXTURE"; rm -rf "$STUB_DIR"' EXIT
CALLS="$STUB_DIR/calls"
cat > "$STUB_DIR/curl" <<STUB
#!/usr/bin/env bash
n=\$(( \$(ls "$STUB_DIR"/call.* 2>/dev/null | wc -l) + 1 ))
printf '%s\n' "\$*" >> "$CALLS"
cat > "$STUB_DIR/call.\$n.body"
exit 0
STUB
chmod +x "$STUB_DIR/curl"
reset_calls() { rm -f "$CALLS" "$STUB_DIR"/call.*; }
ncalls() { if [ -f "$CALLS" ]; then wc -l < "$CALLS" | tr -d ' '; else echo 0; fi; }
LIVE_URL="http://pushgateway.example.net:9091"
# run_live <desc> <expect:fail|ok> <cmd...> — runs with the stub on PATH and a live URL.
expect_refused() {
    local desc="$1"; shift
    reset_calls
    if env PATH="$STUB_DIR:$PATH" PHANTOM_PUSHGATEWAY_URL="$LIVE_URL" "$@" >"$STUB_DIR/out" 2>&1 </dev/null; then
        bad "$desc: expected refusal, got exit 0 ($(ncalls) transport calls)"
    elif [ "$(ncalls)" != 0 ]; then
        bad "$desc: refused but transport was called $(ncalls) time(s)"
    else
        ok "$desc: refused, zero transport calls"
    fi
}
expect_refused "synthetic run, push dry-run default-disabled" \
    env PHANTOM_CI_DRYRUN=1 PHANTOM_LOADTIME_COLOR=rigtest "$EMITTER" run
expect_refused "synthetic run, PHANTOM_LOADTIME_PUSH_DRY_RUN=0 explicit" \
    env PHANTOM_CI_DRYRUN=1 PHANTOM_LOADTIME_PUSH_DRY_RUN=0 PHANTOM_LOADTIME_COLOR=rigtest "$EMITTER" run
if grep -q 'push dry-run is disabled' "$STUB_DIR/out"; then ok "synthetic-run refusal is actionable (names push dry-run)"; else bad "synthetic-run refusal message unhelpful: $(cat "$STUB_DIR/out")"; fi

# Reserved movie+episode / cold+warm samples (the engine fixture carries all four).
for it in movie episode; do for fl in play_already_materialised materialise_then_play; do
    if grep -q "^phantom_playback_outcome_total{flow=\"$fl\",item_type=\"$it\"" "$FIXTURE"; then
        ok "fixture carries reserved sample flow=$fl item_type=$it"
    else bad "fixture lacks reserved sample flow=$fl item_type=$it"; fi
done; done
expect_refused "reserved samples via positional file (custom job)" \
    env PHANTOM_LOADTIME_PUSH_JOB=custom-job "$EMITTER" push "$FIXTURE"
expect_refused "reserved samples via PHANTOM_LOADTIME_RECORDS (custom job)" \
    env PHANTOM_LOADTIME_RECORDS="$FIXTURE" PHANTOM_LOADTIME_PUSH_JOB=custom-job "$EMITTER" push
if grep -q 'reserved phantom_playback_outcome_total samples' "$STUB_DIR/out" \
    && grep -q 'use --dry-run' "$STUB_DIR/out"; then
    ok "reserved-family refusal names the family and offline remedy"
else bad "reserved-family refusal is not actionable"; fi
reset_calls
if PATH="$STUB_DIR:$PATH" PHANTOM_PUSHGATEWAY_URL="$LIVE_URL" PHANTOM_LOADTIME_PUSH_JOB=custom-job \
        "$EMITTER" push < "$FIXTURE" >/dev/null 2>&1; then
    bad "reserved samples via stdin: expected refusal"
elif [ "$(ncalls)" != 0 ]; then bad "reserved samples via stdin: transport called"
else ok "reserved samples via stdin (custom job): refused, zero transport calls"; fi

# Whitespace-valid reserved samples (bare name + space, leading whitespace) are refused.
WS="$STUB_DIR/ws.prom"
printf 'phantom_loadtime_seconds{flow="list_load",item_type="movie",color="x"} 1\nphantom_playback_outcome_total 1\n' > "$WS"
expect_refused "reserved sample without labels (name + space)" "$EMITTER" push "$WS"
printf 'phantom_loadtime_seconds{flow="list_load",item_type="movie",color="x"} 1\n  phantom_playback_outcome_total\t{cause="success"} 1\n' > "$WS"
expect_refused "reserved sample with leading/tab whitespace" "$EMITTER" push "$WS"

# Legitimate live load-time + rig-outcome payload, with misleading comments
# and similar names, reaches the transport ONCE with exact target + body.
LEGIT="$STUB_DIR/legit.prom"
cat > "$LEGIT" <<'EOP'
# HELP phantom_loadtime_seconds Wall-clock duration.
# TYPE phantom_loadtime_seconds gauge
# HELP phantom_loadtime_rig_outcome_total not phantom_playback_outcome_total (renamed).
# TYPE phantom_loadtime_rig_outcome_total counter
phantom_loadtime_rig_outcome_total{flow="play_already_materialised",item_type="movie",cause="success"} 1
phantom_loadtime_rig_outcome_total{flow="materialise_then_play",item_type="episode",cause="success"} 1
phantom_playback_outcome_total_extra{cause="x"} 1
EOP
for it in movie episode; do
    for flow in "${FLOWS[@]}"; do
        printf 'phantom_loadtime_seconds{flow="%s",item_type="%s",color="blue"} 0.42\n' "$flow" "$it" >> "$LEGIT"
        printf 'phantom_loadtime_runs_total{flow="%s",item_type="%s",color="blue"} 1\n' "$flow" "$it" >> "$LEGIT"
        printf 'phantom_loadtime_errors_total{flow="%s",item_type="%s",color="blue"} 1\n' "$flow" "$it" >> "$LEGIT"
    done
done
for mode in file env stdin; do
    reset_calls
    case "$mode" in
        file)  PATH="$STUB_DIR:$PATH" PHANTOM_PUSHGATEWAY_URL="$LIVE_URL" "$EMITTER" push "$LEGIT" >/dev/null 2>&1 && rc=0 || rc=$? ;;
        env)   PATH="$STUB_DIR:$PATH" PHANTOM_PUSHGATEWAY_URL="$LIVE_URL" PHANTOM_LOADTIME_RECORDS="$LEGIT" "$EMITTER" push >/dev/null 2>&1 && rc=0 || rc=$? ;;
        stdin) PATH="$STUB_DIR:$PATH" PHANTOM_PUSHGATEWAY_URL="$LIVE_URL" "$EMITTER" push < "$LEGIT" >/dev/null 2>&1 && rc=0 || rc=$? ;;
    esac
    if [ "$rc" -eq 0 ] && [ "$(ncalls)" = 1 ] \
        && grep -q -- "-X PUT $LIVE_URL/metrics/job/phantom-loadtime\$" "$CALLS" \
        && cmp -s "$LEGIT" "$STUB_DIR/call.1.body"; then
        ok "legitimate live payload ($mode) reaches transport once, exact PUT target, unchanged body"
    else
        bad "legitimate live payload ($mode): rc=$rc calls=$(ncalls) args=$(cat "$CALLS" 2>/dev/null)"
    fi
done

# Explicit push dry-run (flag and env) stays offline and keeps fixture output.
reset_calls
FLAG_OUT="$(PATH="$STUB_DIR:$PATH" PHANTOM_PUSHGATEWAY_URL="$LIVE_URL" PHANTOM_CI_DRYRUN=1 PHANTOM_LOADTIME_COLOR=rigtest "$EMITTER" run --dry-run 2>/dev/null)" || true
ENV_OUT="$(PATH="$STUB_DIR:$PATH" PHANTOM_PUSHGATEWAY_URL="$LIVE_URL" PHANTOM_LOADTIME_PUSH_DRY_RUN=1 "$EMITTER" push "$FIXTURE" 2>/dev/null)" || true
if [ "$(ncalls)" = 0 ] \
    && [ "$(printf '%s\n' "$FLAG_OUT" | grep -c '^phantom_playback_outcome_total{')" = 4 ] \
    && [ "$(printf '%s\n' "$ENV_OUT" | grep -c '^phantom_playback_outcome_total{')" = 4 ]; then
    ok "explicit push dry-run (flag + env) is offline and retains the fixture output"
else
    bad "explicit push dry-run regressed (calls=$(ncalls))"
fi

head_ "I. complete measurement batches gate every real PUT (offline engine + intercepted curl)"
if python3 - "$EMITTER" "$STUB_DIR" "$LEGIT" "$LIVE_URL" <<'PY'
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

emitter, stub, fixture, url = sys.argv[1:]
stub = Path(stub)
env = dict(os.environ, PATH=f"{stub}:{os.environ['PATH']}",
           PHANTOM_PUSHGATEWAY_URL=url, PHANTOM_LOADTIME_PUSH_JOB="batch-test",
           PHANTOM_LOADTIME_PUSH_DRY_RUN="0", PHANTOM_CI_DRYRUN="0",
           PHANTOM_LOADTIME_RECORDS="",
           P8_ENGINE=f"{Path(emitter).parent}/../tools/rig-scenarios/47-loadtime-flows.sh",
           P8_BASH=shutil.which("bash"), P8_BATCH=str(stub / "batch.prom"))
# Intercept only the engine subprocess; the emitter and curl stub still use real bash.
(stub / "bash").write_text('''#!/bin/sh
if [ "$1" = "$P8_ENGINE" ]; then
    exec /bin/cat "$P8_BATCH"
fi
exec "$P8_BASH" "$@"
''')
(stub / "bash").chmod(0o755)
base = Path(fixture).read_text()
failures = []
checks = 0

def check(name, body, accepted):
    global checks
    for mode in ("file", "env", "stdin", "run"):
        checks += 1
        for path in [stub / "calls", *stub.glob("call.*")]:
            path.unlink(missing_ok=True)
        (stub / "batch.prom").write_text(body)
        run_env = env.copy()
        args = [emitter, "push"]
        if mode == "file":
            args.append(str(stub / "batch.prom"))
        elif mode == "env":
            run_env["PHANTOM_LOADTIME_RECORDS"] = str(stub / "batch.prom")
        elif mode == "run":
            args = [emitter, "run"]
        result = subprocess.run(args, input=body if mode == "stdin" else "",
                                text=True, capture_output=True, env=run_env)
        calls = (stub / "calls").read_text().splitlines() if (stub / "calls").exists() else []
        if accepted:
            valid = (result.returncode == 0 and len(calls) == 1
                     and calls[0].endswith(f"-X PUT {url}/metrics/job/batch-test")
                     and (stub / "call.1.body").read_bytes() == body.encode())
        else:
            valid = result.returncode != 0 and not calls
        if not valid:
            failures.append(f"{name} ({mode}): rc={result.returncode}, calls={len(calls)}")

check("complete N=1, all measured errors retained", base, True)
check("zero durations and zero errors", re.sub(r'(phantom_loadtime_errors_total\{[^\n]+\}) 1',
    r'\1 0', base.replace('} 0.42\n', '} 0\n')), True)
check("trailing blank lines preserved", base + '\n\n', True)
multi = base.replace('} 1\n', '} 3\n')
# All counters remain 3 except one mixed-error tuple; duration is unrelated to count.
multi = multi.replace('phantom_loadtime_errors_total{flow="list_load",item_type="movie",color="blue"} 3',
                      'phantom_loadtime_errors_total{flow="list_load",item_type="movie",color="blue"} 1')
check("complete N=3 with mixed and all-failed tuples", multi, True)
check("reordered labels and scientific numbers", multi.replace(
    'flow="list_load",item_type="movie",color="blue"',
    'color="blue", item_type="movie", flow="list_load"').replace('} 3\n', '} 3e0\n'), True)

for item in ("movie", "episode"):
    labels = f'{{flow="list_load",item_type="{item}",color="blue"}}'
    records = {family: f'phantom_loadtime_{family}{labels} {value}\n'
               for family, value in (("seconds", "0.42"), ("runs_total", "1"), ("errors_total", "1"))}
    for family, record in records.items():
        check(f"missing {family}/{item}", base.replace(record, ""), False)
        check(f"duplicate {family}/{item}", base + record, False)
        check(f"mismatched color {family}/{item}", base.replace(record, record.replace('"blue"', '"green"')), False)
        check(f"mismatched item {family}/{item}", base.replace(record, record.replace(f'"{item}"', '"series"')), False)
        check(f"missing label {family}/{item}", base.replace(record, record.replace(',color="blue"', '')), False)
        for value in ({"seconds": ("-1", "NaN", "+Inf", "-Inf", "1e999", "oops"),
                       "runs_total": ("0", "-1", "1.5", "NaN", "+Inf"),
                       "errors_total": ("-1", "2", "0.5", "NaN", "+Inf")}[family]):
            replacement = record.rsplit(' ', 1)[0] + f' {value}\n'
            check(f"invalid {family}={value}/{item}", base.replace(record, replacement), False)
    check(f"missing whole tuple/{item}", base.replace(''.join(records.values()), ''), False)
    check(f"missing whole item/{item}", ''.join(line for line in base.splitlines(True)
                                               if f'item_type="{item}"' not in line), False)
    check(f"duplicate reordered labels/{item}", base + records["seconds"].replace(
        labels, f'{{color="blue",item_type="{item}",flow="list_load"}}'), False)
    check(f"duplicate label/{item}", base.replace(records["seconds"], records["seconds"].replace(
        'color="blue"', 'color="blue",color="blue"')), False)
    check(f"unexpected flow/{item}", base.replace(f'flow="list_load",item_type="{item}"',
                                                 f'flow="unknown",item_type="{item}"'), False)
check("single duration only", next(line for line in base.splitlines(True)
                                   if line.startswith("phantom_loadtime_seconds{")), False)
check("two complete colors", base + base.replace('color="blue"', 'color="green"'), False)
check("carriage returns cannot turn comments into measurement records",
      '# comment\r' + base.replace('\n', '\r') + '\n', False)
check("large reserved payload cannot bypass synthetic guard",
      'phantom_playback_outcome_total 1\n' + base + '# ' + 'x' * 200000 + '\n', False)
for flow in ("list_load", "sort_change", "info_open", "get_sources", "materialise", "play_materialised"):
    check(f"missing whole flow/{flow}", ''.join(line for line in base.splitlines(True)
                                               if f'flow="{flow}"' not in line), False)
for failure in failures[:8]:
    print(f"  FAIL {failure}")
if len(failures) > 8:
    print(f"  ... {len(failures) - 8} more failures")
print(f"  Batch transport assertions: {checks - len(failures)} passed, {len(failures)} failed")
sys.exit(bool(failures))
PY
then ok "complete-batch transport matrix"; else bad "complete-batch transport matrix"; fi

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
