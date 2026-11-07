# TTFB Reduction 026

Diagnosis on 2026-11-07 (UTC), after reading day 025 first. Diagnose-and-enqueue
only: no runtime, deployment, measurement, threshold or source-behavior edits.

## New Evidence: Attribution Landed, Discovery Still Fails

Manifests newer than run_start 1793881107 (read-only probe of the evidence PVC):

| run_start | checkout | color | discovery_failure | discovery seconds |
| --- | --- | --- | --- | ---: |
| 1793967423 | ec75cae | unknown | reason=kube-lookup-error, kubectl_exit_code=1 | 8 |
| 1793967449 | ec75cae | unknown | reason=kube-lookup-error, kubectl_exit_code=1 | 8 |

Job 29899457 (Nov 6 12:17:00Z) hit BackoffLimitExceeded at 12:17:44Z (two pods).
Checkout ec75cae contains the attribution fix e47b282
(ttfb-loadtime-discovery-failure-attribution), so the reason is now recorded.
No batch completed; there are still no new durations or errors to compare with
Oct 30 (Pushgateway still holds run_start 1793359027) and 7e28c4a's request
bounds were never exercised.

8 seconds = 2 colors x 3 attempts with 2 x 2s backoff each: every kubectl call
failed immediately with exit 1, not by timeout.

## Root Cause Of The Current Failure (Reproduced)

Pods from the CronJob's exact rig image digest and service account (in-cluster
env and SA token present, no kubeconfig file):

    kubectl -n <ns> get ingress phantom-library-green -o name                       -> rc=0
    kubectl -n <ns> --request-timeout=15s get ingress phantom-library-green -o name -> rc=1
      "The connection to the server localhost:8080 was refused"

kubectl v1.31.1 treats any explicit client-config override (here
`--request-timeout`) as a non-empty config and skips the in-cluster fallback,
dialling the default localhost:8080. The attribution fix's added
`--request-timeout` in `resolve_color` (tools/ci/loadtime-daily-run.sh) therefore
makes discovery fail deterministically in-cluster. The offline suite stubs
kubectl and cannot see this. (The flag ordering does not matter; both
placements fail.) The original Nov 1-5 53-85s failures predate this flag and
remain unattributed; they are superseded by this deterministic failure, which
must be removed before any further attribution is possible.

## Fix Enqueued

**ttfb-loadtime-kubectl-incluster-config**: make the Ingress lookup work with
in-cluster credentials while keeping a bound — drop `--request-timeout` (the
`timeout -k` wall bound already caps each call) or supply an explicit in-cluster
kubeconfig/`--server`+token+CA so the override does not disable the fallback.
Add a regression case to scripts/tests/p8-loadtime-daily-workflow.test.sh whose
stub kubectl mimics the real behaviour (exit 1 when `--request-timeout` is given
without a kubeconfig). Nonduplicate: day 025's fix added attribution; this
fixes the regression it introduced.

## Independent Successor

**ttfb-reduction-027**, no deps, not_before ~+24h: re-read manifests newer than
1793967449; if discovery passes, compare durations and errors to Oct 30 and
check whether 7e28c4a's bounds fired; otherwise use the recorded reason.

No behavioral regression test applies to this analytical document.
