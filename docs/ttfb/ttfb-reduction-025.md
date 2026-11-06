# TTFB Reduction 025

Diagnosis on 2026-11-06 (UTC), after reading day 024 first. Diagnose-and-enqueue
only: no runtime, deployment, measurement, threshold or source-behavior edits.
This revision corrects the prior attempt's interpretation of retained evidence;
historical cluster observations below were not re-collected during rework.

## No New Batch: Five Days Of Pre-Measurement Failure

Day 024 expected the next question to be "DeadlineExceeded again?". The retained
November 1-5 evidence instead records pre-measurement failures, labelled
discovery, after 53-85 seconds. The three surviving Jobs each have two failed
pods (backoffLimit=1) and `BackoffLimitExceeded`:

| Job | startTime (UTC) | Failed pods | Condition |
| --- | --- | ---: | --- |
| 29895137 | 2026-11-03 12:17:00 | 2 | BackoffLimitExceeded |
| 29896577 | 2026-11-04 12:17:00 | 2 | BackoffLimitExceeded |
| 29898017 | 2026-11-05 12:17:00 | 2 | BackoffLimitExceeded (12:19:53) |

The November 1/2 Jobs were already garbage-collected; their evidence survives.
Job pods were deleted, so the prior attempt could not recover their pod logs.

The durable evidence volume retained ten new manifests (run_start > day 024's
1793445423), two per day for Nov 1-5. All have this shape:

    checkout_sha=ac5c08f4340805bf7d78f12c69ec58cadaf511f9  color=unknown
    status=failed exit_code=1 targets={} failures=[]
    stages={discovery: exit_code=1, seconds=53..85}

run_starts: 1793535425/1793535515, 1793621827/1793621929, 1793708226/1793708321,
1793794626/1793794715, 1793881026/1793881107.

Persisted `color=unknown` is the initial manifest value, not a measurement of
shell `COLOR`. In this checkout, wrapper teardown writes a failed discovery
stage only when shell `COLOR` is empty and exit status is nonzero
(`tools/ci/loadtime-daily-run.sh:183-191`). Together with the finalized failed
manifest and empty targets, this supports failure before successful color
assignment and before measurement, but does not identify which operation failed.
Missing dev-host configuration or the prod-safety refusal also reaches this
path (lines 226-234), as does unsuccessful `resolve_color` (line 255).
By contrast, `evidence color` runs AFTER successful shell assignment (line 256);
failure of that write alone does not emit the observed teardown discovery stage,
even if the persisted color remains unknown. A failure to initialize evidence
normally leaves no usable manifest path for teardown, unlike these finalized
records. The full shape matters, not just the unknown-color field.

Discovery duration is measured from `RUN_START`, before evidence initialization
and configuration checks, to teardown's stage recording. Thus 53-85 seconds is
not isolated Ingress lookup time. The checkout contains request-time-bound repair
7e28c4a, but these attempts never reached measurement and do not exercise it.
Empty `failures` means no retained request failures, not successful requests.

Consequently there is no new duration or error comparison against Oct 30: the
retained Pushgateway values remain the Oct 30 run-start 1793359027 batch
(see day 023/024 tables), about six days stale at the prior observation. Twelve
breaches were unchanged; neither durations nor errors demonstrate improvement.

## Reproduction: Color Resolution Passed Later

The prior attempt reports two out-of-band reproductions derived from the
CronJob pod template (same service account and rig image digest 07a257ff...,
checkout init container cloning main at ac5c08f). They were NOT identical runs:
the admin token was removed to abort before measurement, and evidence settings
were changed as follows:

1. Evidence disabled: color resolved `green` in <2s, then the missing-token
   guard failed before measurement.
2. Evidence redirected to a scratch subdirectory of the same PVC: `init`,
   `color`, and `finish` succeeded; color resolved `green`. The prior attempt
   reports removing the scratch directory afterwards.

The service account's `can-i get ingress` check returned yes, and the green
Ingress carried the configured `PHANTOM_INCLUSTER_DEV_HOST` (for example,
`dev.example.com`; literal site evidence is retained only in the private hive).
These later observations establish success under the reproduction conditions,
not the failed Jobs' actual API responses, configuration, or filesystem state.
They do not exclude code/configuration differences or prove an environmental
cause. Recorded checkout SHAs identify source, not complete runtime equivalence.

The observed start shifted from 11:17Z to 12:17Z around November 1. A scheduling
or DST-related interaction is only a hypothesis, not a demonstrated cause.
API delay/error and PVC initialization/locking/fsync delay are also hypotheses;
the 53-85s total cannot locate a stall or explain the final failure by itself.
In particular, a color-write failure after successful assignment alone does not
explain the full retained manifest shape above.

Root cause cannot be attributed from retained evidence: `resolve_color` sends
kubectl stderr to /dev/null and collapses lookup errors and absent host matches
into "no Ingress carries host", kubectl has no `--request-timeout`, and
`loadtime-evidence.py` prints a generic persistence error. Specific wrapper
`FAIL:` lines went to stderr of pods no longer available. The retained discovery
stage also conflates pre-color configuration/safety failures with lookup failure.

## Fix Already Enqueued

Preserve **ttfb-loadtime-discovery-failure-attribution**: bounded
`--request-timeout` plus a small retry on Ingress lookup, distinguish lookup-error
vs host-not-found vs evidence-write failure, and persist a sanitized failure
reason (enumerated code plus kubectl exit status, no stderr/URL text). Attribution
must distinguish pre-color configuration/safety failures and post-assignment
evidence-write failures rather than infer them from persisted unknown color.
Its registered regression suite is
`scripts/tests/p8-loadtime-daily-workflow.test.sh`, including stub kubectl
error/hang coverage to be implemented by that task. This is distinct from
7e28c4a's measurement HTTP bounds. No duplicate fix or runtime change is made here.

## Independent Successor

Preserve the already-filed **ttfb-reduction-026**, no dependencies,
not_before=2026-11-07T03:45:00Z (roughly +24h from the prior diagnosis).
It should first re-read manifests newer than 1793881107; if failures continue
without the attribution fix, capture the Job pod log while it exists (the
observed Jobs failed within about three minutes of 12:17Z). Compare complete
manifest shape, executed revision and configuration before attributing a cause.

No behavioral regression test applies to this analytical document. Rework
manually checked the wrapper and evidence-writer control flow; prior review's
offline workflow suite passed 237/237, which does not verify historical live
observations or their cause. Exact verification references are in the private
hive change doc. No live measurement was triggered and no operator action is
required.
