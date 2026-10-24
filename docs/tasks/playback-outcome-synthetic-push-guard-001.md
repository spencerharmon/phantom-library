# playback-outcome-synthetic-push-guard-001

## Evidence and scope

Day 15 reproduced an offline-to-live transport gap in
`scripts/phantom-loadtime-push.sh:117-154`. PHANTOM_CI_DRYRUN=1 makes
`tools/rig-scenarios/47-loadtime-flows.sh` generate synthetic load-time records
and four `phantom_playback_outcome_total` samples (movie/episode, cold/warm).
It does not set the emitter's independent push dry-run flag. With a configured
URL, the real emitter sends them to curl; an in-process stub confirmed four
reserved samples without contacting any server.

The prior dual-emit task deliberately retained the engine's offline fixture
family while renaming only LIVE rig outcomes to
`phantom_loadtime_rig_outcome_total`. Do not undo that work or duplicate the
runtime-info gauge. This task owns the missing push-boundary protection.

## Required behavior

Invariant: the load-time emitter must never send reserved real playback-outcome
samples or an explicitly synthetic engine run to a network sink; legitimate
live rig payloads remain unchanged and explicit push dry-run stays offline.

1. Refuse `run` with PHANTOM_CI_DRYRUN=1 unless push dry-run is explicitly
   enabled, preferably before running the engine. Give an actionable error.
2. For every non-dry-run push, reject an entire payload containing a sample
   of the exact reserved `phantom_playback_outcome_total` family, regardless
   of job override or input source: positional file, PHANTOM_LOADTIME_RECORDS,
   stdin, or engine output. Recognize metric-name boundaries and valid sample
   whitespace; do not confuse comments, similar names or the rig family with
   reserved samples. Never silently strip records or report success.
3. Preserve explicit --dry-run and PHANTOM_LOADTIME_PUSH_DRY_RUN=1 fixture
   output, with zero transport calls. Preserve normal live load-time plus
   rig-outcome PUT payload and grouping semantics unchanged. Preserve missing
   URL, empty input and missing file error behavior; adjust test fixtures
   where needed to test those errors independently of reserved-family rejection.
4. Update the emitter's stale documentation that currently promises real
   playback-family pass-through. Explain the independent dry-run controls and
   rejection, using neutral endpoints only.

No backend deletes/resets, deployment, production promotion, playback behavior
change or metric renaming is required. File/stdin data without reserved samples
cannot in general be proven nonsynthetic; do not claim universal provenance
validation from this guard.

## Regression and Check

Extend `scripts/tests/p8-loadtime-push.test.sh`, invoking the real emitter and
intercepting curl through a test-local executable or exported function. Do not
contact a real sink or introduce a parallel implementation of the guard.

- Synthetic run with a configured URL and push dry-run disabled must fail with
  no transport calls; cover both explicit and default-disabled push dry-run.
- Reserved movie and episode samples, including cold/warm coverage, must fail
  for positional file, environment file and stdin, even with a custom job.
- Explicit push dry-run via flag and environment must succeed offline and
  retain fixture output. Misleading comments/similar metric names must not
  cause false positives; whitespace-valid reserved samples must be refused.
- A legitimate live load-time + rig-outcome payload must reach the transport
  stub once, with exact expected PUT target and unchanged body.
- Existing error and exposition tests remain green. Record fail-before and
  pass-after evidence from the real command path, not merely helper tests.

Check: `./scripts/tests/p8-loadtime-push.test.sh`

This matches the hive CHECKS.md `script-test` framework. It verifies the entire
emitter-to-transport seam offline; no deployment acceptance is claimed.
Implementation-branch invocation:
`beehive submodule worktree exec phantom-library bee-playback-outcome-synthetic-push-guard-001 -- ./scripts/tests/p8-loadtime-push.test.sh`.
