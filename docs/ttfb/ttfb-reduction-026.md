# TTFB Reduction 026

Diagnosis on 2026-11-07 UTC, after reading day 025 first. Diagnose-and-enqueue
only: no runtime, deployment, measurement or threshold changes. This report
restores the earlier attempt's diagnosis and independently rechecks its evidence.

## Attribution Landed, But Discovery Still Failed

The read-only evidence-volume probe found exactly two manifests newer than
run_start 1793881107:

| run_start | checkout | discovery reason | kubectl exit | discovery seconds |
| --- | --- | --- | ---: | ---: |
| 1793967423 | ec75cae4be0eda00fd4114b95af5854a1c957806 | kube-lookup-error | 1 | 8 |
| 1793967449 | ec75cae4be0eda00fd4114b95af5854a1c957806 | kube-lookup-error | 1 | 8 |

Both have status=failed, exit_code=1, color=unknown, targets={}, failures=[],
and only a failed discovery stage. The November 6 scheduled Job started at
12:17:00Z and reached BackoffLimitExceeded at 12:17:44Z with two failed pods.
Those pods no longer exist. This pass ran around 05:00Z on November 7, outside
the next scheduled window, so it could not capture that Job's live log.

Unlike day 025, the retained reason identifies a kubectl lookup error rather
than merely the wrapper's pre-color teardown fallback. Eight seconds is
consistent with two colors, three attempts each, and two 2-second backoffs
per color; it does not by itself prove every historical call's duration.

## Reproduced Regression And Existing Repair

A bounded, read-only probe using the scheduled rig image and service account
reported kubectl v1.31.1. With the same Ingress query:

```text
without --request-timeout: exit 0, Ingress returned
with --request-timeout=15s: exit 1, localhost:8080 connection refused
```

Both calls had an external `timeout -k 5 20` wall bound. This independently
reproduces the client-configuration regression documented by the prior attempt:
the request-timeout override bypasses the in-cluster fallback in this binary.
The attribution repair introduced that flag; the November 6 checkout includes
it. The probe corroborates the failure mechanism, but is not a recovered log
of the failed scheduled pods. The November 1-5 failures predate that flag and
remain unattributed; do not retroactively assign this cause to them.

The one distinct fix already enqueued by this task's earlier attempt is
**ttfb-loadtime-kubectl-incluster-config**, now landed as `7026ebf`. It removes
the kubectl flag while retaining the external wall bound, bounded retries and
sanitized failure reasons. Its regression stubs reject the flag and its hang
case retains timeout coverage. The repair receipt reports 237 passed, 0 failed;
this analytical pass did not rerun or author that behavioral suite. Current
tracked checkout `a0e692b` contains the repair. Successful scheduled measurement
after the repair is still unobserved, not implied by the probe or code landing.

## No Fresh Latency Or Error Comparison

The live metric query at evaluation 1794027718.755 still returned daily run-start
VALUE 1793359027, the October 30 batch, with flow/stage push times
1793361169.4467623 / 1793361174.3147707. A new query timestamp is not a new run.
All twelve breach gauges remain 1. Selected retained durations and errors:

| flow | movie seconds | movie errors / runs | episode seconds | episode errors / runs |
| --- | ---: | --- | ---: | --- |
| list_load | 10.118952 | 3 / 3 | 343.661371 | 2 / 3 |
| sort_change | 30.393210 | 3 / 3 | 36.425254 | 3 / 3 |
| info_open | 0.041445 | 0 / 3 | 0.048825 | 3 / 3 |
| get_sources | 0.029544 | 0 / 3 | 0.030021 | 3 / 3 |
| materialise | 0.031143 | 0 / 3 | 0.029920 | 3 / 3 |
| play_materialised | 0.031667 | 0 / 3 | 0.032538 | 3 / 3 |

These are stale baseline values, not evidence of improvement. No new batch
completed; empty manifest failures are not successful requests. The two newer
attempts never reached measurement, so they did not exercise `7e28c4a`'s HTTP
request bounds. No new timeout/error comparison is available for either media type.

## Independent Successor

Preserve the already-filed **ttfb-reduction-027**, independent of this report and
its fix, scheduled by the earlier attempt for the next daily diagnosis. Append
retries for both task IDs returned `already exists`; no duplicate was filed or
existing schedule moved. The retry requested November 8 05:05Z (about +24h),
but rejection does not establish or alter the existing task's exact timestamp.

Day 027 should read this report first and inspect manifests newer than
1793967449. Verify the executed checkout includes `7026ebf`. If discovery still
fails, inspect its retained reason and capture the scheduled pod log while it
exists when timing permits. If measurement resumes, compare both durations AND
errors to the October 30 baseline and check whether `7e28c4a`'s bounds fired.
Do not duplicate the discovery-attribution or kubectl-configuration fixes.

No behavioral regression test applies to this analytical document. Exact probe
commands and validation results are retained in the private hive change doc.
No operator action is required.
