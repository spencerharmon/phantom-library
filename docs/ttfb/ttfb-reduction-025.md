# TTFB Reduction 025

Diagnosis on 2026-11-06 (UTC), after reading day 024 first. Diagnose-and-enqueue
only: no runtime, deployment, measurement, threshold or source-behavior edits.

## No New Batch: Five Days Of Pre-Measurement Discovery Failure

Day 024 expected the next question to be "DeadlineExceeded again?". It is not.
Every daily Job since November 1 fails in the DISCOVERY stage within ~1-1.5
minutes, twice per Job (backoffLimit=1), with `BackoffLimitExceeded`:

| Job | startTime (UTC) | Failed pods | Condition |
| --- | --- | ---: | --- |
| 29895137 | 2026-11-03 12:17:00 | 2 | BackoffLimitExceeded |
| 29896577 | 2026-11-04 12:17:00 | 2 | BackoffLimitExceeded |
| 29898017 | 2026-11-05 12:17:00 | 2 | BackoffLimitExceeded (12:19:53) |

(The November 1/2 Jobs are already garbage-collected; their evidence survives.)
Job pods are deleted, so no pod log is recoverable for any of them.

The durable evidence volume retains ten new manifests (run_start > day 024's
1793445423), two per day for Nov 1-5. ALL are identical in shape:

    checkout_sha=ac5c08f4340805bf7d78f12c69ec58cadaf511f9  color=unknown
    status=failed exit_code=1 targets={} failures=[]
    stages={discovery: exit_code=1, seconds=53..85}

run_starts: 1793535425/1793535515, 1793621827/1793621929, 1793708226/1793708321,
1793794626/1793794715, 1793881026/1793881107. `color=unknown` means the script
failed BEFORE `evidence color` persisted: either `resolve_color` found no
Ingress carrying the dev host, or the color evidence write itself failed.
The checkout contains the request-time-bound repair (7e28c4a), so the measure
stage never even started; that repair is still unexercised live.

Consequently there is no new duration or error comparison against Oct 30: the
retained Pushgateway values remain the Oct 30 run-start 1793359027 batch
(see day 023/024 tables), now ~6 days stale. Twelve breaches unchanged.

## Reproduction: Discovery Passes Out-Of-Band

Two read-only reproductions, using the CronJob's EXACT pod template (same
SA `phantom-library-loadtime-daily`, rig image digest 07a257ff…, checkout init
container cloning main @ ac5c08f), with the admin token REMOVED so the script
aborts deterministically right after color resolution, before any request:

1. Evidence disabled: color resolved `green` in <2s, failed only on the
   missing token.
2. Evidence redirected to a scratch subdir of the same PVC: `init`, `color`,
   `finish` all succeeded; color resolved `green`; the scratch dir was removed.

Also: the SA `can-i get ingress` = yes; `phantom-library-green` Ingress carries
`dev.jellyfin.polyfam.studio`. So the failure is NOT deterministic in code,
RBAC or Ingress state; it is environmental and correlated with the 12:17Z
slot (the schedule `17 6 * * *` is controller-local time; since the Nov 1
DST shift it fires at 12:17Z, not 11:17Z — every failure is in the new slot).
The 53-85s pre-color duration (vs <2s here) indicates something blocks for
~a minute — consistent with a kubectl call stalling on the API server, or a
slow/failed fsync on the local-path PVC — then fails.

The root cause cannot be attributed from retained evidence because the
script deliberately hides it: `resolve_color` sends kubectl stderr to
/dev/null and collapses every outcome into "no Ingress carries host", kubectl
has no `--request-timeout`, `loadtime-evidence.py` prints a generic message,
and the only specific `FAIL:` line goes to stderr of a pod that is deleted.

## Fix Enqueued

**ttfb-loadtime-discovery-failure-attribution**: make discovery failures
attributable and less fragile — bounded `--request-timeout` + small retry on
the Ingress lookup, distinguish lookup-error vs host-not-found vs
evidence-write failure, and persist a sanitized discovery failure reason
(enumerated code + kubectl exit status, no stderr/URL text) into the evidence
manifest. Regression test in `scripts/tests/p8-loadtime-daily-workflow.test.sh`
with a stub kubectl that errors/hangs. Not a duplicate: 7e28c4a bounded the
MEASURE stage's HTTP requests; nothing bounds or attributes discovery.

## Independent Successor

**ttfb-reduction-026** ~+24h, no dependencies. It should first re-read the
evidence volume for manifests newer than 1793881107; if discovery still fails
and the fix has not landed, capture the Job pod log WHILE the pod exists
(it fails within ~3 minutes of 12:17Z) to name the failing step.

No behavioral regression test applies to this analytical document.
