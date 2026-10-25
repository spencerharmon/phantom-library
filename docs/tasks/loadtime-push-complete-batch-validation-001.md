# loadtime-push-complete-batch-validation-001

## Problem

Day 16 reproduced acceptance of one syntactically valid duration sample by
`scripts/phantom-loadtime-push.sh push`. `validate_records` (lines 126-134)
only requires nonempty input and a duration prefix. The subsequent PUT
(lines 147-170) replaces the whole group, potentially deleting run/error
denominators and movie/episode coverage from a previous batch.

The synthetic-push guard is already merged and must be retained. This fix is
batch completeness, not fixture provenance, runtime-info, newline syntax,
same-batch ratchet selection, or deployment. Do not claim organic error-rate
improvement from a rig-metric integrity fix.

## Design

Validate the actual payload before any live transport. The current engine
contract (`47-loadtime-flows.sh` emit_record and six-flow loop) is exactly one
duration, run count and error count per flow/item_type/color tuple, for all
six flows (`list_load`, `sort_change`, `info_open`, `get_sources`, `materialise`,
`play_materialised`) and both movie and episode, for one batch color.
Reject missing members, duplicate tuples, mismatched labels and incomplete
flow/item coverage. Validate finite nonnegative durations, positive integral
runs and integral errors between zero and runs. Do not hard-code runs=1:
the engine now supports N-sample aggregation. Do not reject genuine measured
errors; they are data to retain, not a reason to hide an unsuccessful run.

Keep unrelated families/comments, legitimate rig outcomes, configured job/URL
and complete bodies unchanged. Do not synthesize zeros to repair missing data,
strip records, or change PUT into POST. Preserve independent synthetic-run
and reserved-family rejection and explicit offline fixture dry-run support.
Use the same validation on positional file, environment file, stdin and run's
engine output. Reuse existing parsing only if it verifies this full contract;
do not introduce a disconnected validation path used only by tests.

Invariant: no incomplete measurement batch can reach the real emitter's PUT;
a complete batch, including measured failures and N>1, reaches it once with
unchanged body and movie/episode coverage intact.

## Acceptance

Check (existing CHECKS.md `script-test`):

```sh
./scripts/tests/p8-loadtime-push.test.sh
```

Run on the fix branch:

```sh
beehive submodule worktree exec phantom-library bee-loadtime-push-complete-batch-validation-001 -- ./scripts/tests/p8-loadtime-push.test.sh
```

Extend the real-emitter transport-interception harness. Starting with a complete
six-flow x two-item batch, remove a run, error, duration or entire flow/item
tuple; cover movie and episode and all file/env/stdin entrypoints. Require
nonzero exit and zero curl calls. Cover duplicate/mismatched tuples and invalid
numbers. Exercise run's engine-output seam offline, with no live playback.
Complete N=1 and N>1 batches with nonzero errors must produce exactly one PUT,
the expected target and unchanged body. Keep all synthetic-guard assertions,
including actionable reserved-family errors, and explicit push dry-run behavior.

The existing section-H "legitimate" fixture is itself incomplete (durations
and rig outcomes but no run/error records); replace it with a complete fixture,
not weaker assertions. Record fail-before/pass-after evidence for new cases;
today's unchanged harness already passes 30 tests and does not prove this fix.
No deployment or backend write is required for acceptance.
