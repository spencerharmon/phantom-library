# TTFB Reduction 030

Diagnosed 2026-11-13 UTC after reading day 029 first. Diagnose-and-enqueue
only; no runtime, deployment, measurement or threshold changes.

## Newer Evidence

Exactly two retained manifests are newer than run_start 1794399558:

| run_start | attempt_id | checkout | discovery / measure |
| --- | --- | --- | --- |
| 1794485827 | cc6a500eb1cc49aebd060c10b4797eaf | 53da799141b58a1be01fa043731ebc153725f56e | both 0s, exit 1 |
| 1794485847 | 5d9a7b17c2b54bd7808fcec38fcf5738 | 53da799141b58a1be01fa043731ebc153725f56e | both 0s, exit 1 |

Both Nov 12 attempts failed with durable
`discovery_failure={reason:user-invalid-response,curl_exit_code:0,http_status:302}`,
empty targets and failures, and no push/guard stage. Discovery starts were
1794485828 and 1794485914, respectively. Their 1s/67s preparation gaps are
outside the measurement stage, not browse latency. The scheduled Job failed
with BackoffLimitExceeded; its pods are gone. These are failed batches, not
0/36 flow samples. With the previous reports, eight Nov 9-12 attempts failed
before timing; only Nov 11-12 have these durable reasons. Earlier failures
must not retroactively be assigned HTTP 302 without evidence.

Ancestry checks prove `f0da20b` (durable discovery reasons), `7e28c4a`
(request/list bounds), and `190cf37` (error-aware ratchet) are all included
in the executed checkout. Reasons demonstrably ran; the ratchet did not.
No curl 28/HTTP 000, timed list failure, or 1500s measurement-wall hit was
recorded. The engine's default request/list limits remain 60s/120s; the
outer measurement wall is separate. Neither increasing these bounds nor
re-filing timeout/ratchet work addresses an immediate redirect.

## Redirect Diagnosis

The engine requests `/Users` before Channels. Its curl helper uses `--fail`
without redirect following; 302 is curl success, then the user-array parser
rejects the response (`47-loadtime-flows.sh:517-585`). The retained manifests
do not include the historical destination/body.

Read-only live probes now establish more than the status alone:

- The inactive host's Ingress attaches a forwardAuth middleware pointing to
  oauth2-proxy. The proxy uses `static://202` upstream and its only configured
  skip-auth route is `GET=^/System/Info/Public$`.
- `/Users` returns HTTP 302 to an HTTPS OIDC authorization endpoint, both
  without a token and with the daily job's token supplied as `X-Emby-Token`
  or `Authorization: MediaBrowser`. All probes returned curl exit 0.
- No redirects were followed, and no token, cookie, state or query value was
  printed. Deployment identifiers and exact probes are in the private hive doc.

Thus the current daily HTTPS path is gated before it can discover Jellyfin
users; a Jellyfin token alone does not satisfy this interactive gate. This
does not establish that the token is valid at Jellyfin, identify the rollout
that introduced the gate, or reconstruct every historical redirect. The
previously filed fix mentions a Nov 8 release correlation; that is not proof
of causation and still needs investigation by its owner.

## Metric Attribution

Requeried all three historical batches and the current series. Bind the flow
push to the historical daily run-start before interpreting durations:

| evaluation | daily run_start | flow push_time_seconds | daily push_time_seconds |
| --- | --- | --- | --- |
| 1793361200 | 1793359027 | 1793361169.4467623 | 1793361174.3147707 |
| 1794113444.263 | 1794053824 | 1794054641.644034 | 1794054646.763412 |
| 1794141100 | 1794140228 | 1794140993.9278193 | 1794140998.84607 |
| 1794558540.481 (current probe) | 1794485847 | 1794140993.9278193 | 1794485914.3866994 |

Current daily measure is 0s/exit 1, but current flow push is still Nov 8's.
There is no fresh latency measurement or demonstrated recovery. Nov 8
completed measurement but guard exited 3, with all twelve breach gauges 1.

## Durations And Errors

Each cell is median seconds, errors/runs. Full HTTP-flow durations, not raw
server TTFB; the episode list/sort label measures Shows root, not episode
navigation. Failed discovery batches are excluded from these denominators.

| flow / type | Oct 30: 1793359027 | Nov 7: 1794053824 | Nov 8: 1794140228 |
| --- | --- | --- | --- |
| list_load / movie | 10.118952, 3/3 | 92.149758, 0/3 | 91.135839, 0/3 |
| sort_change / movie | 30.393210, 3/3 | 85.425209, 0/3 | 90.006179, 0/3 |
| info_open / movie | 0.041445, 0/3 | 0.053861, 0/3 | 0.058460, 0/3 |
| get_sources / movie | 0.029544, 0/3 | 0.042807, 0/3 | 0.039677, 0/3 |
| materialise / movie | 0.031143, 0/3 | 0.039514, 0/3 | 0.037035, 0/3 |
| play_materialised / movie | 0.031667, 0/3 | 0.043975, 0/3 | 0.058742, 0/3 |
| list_load / episode | 343.661371, 2/3 | 26.283003, 0/3 | 27.062847, 0/3 |
| sort_change / episode | 36.425254, 3/3 | 24.448986, 0/3 | 26.264750, 0/3 |
| info_open / episode | 0.048825, 3/3 | 0.037072, 0/3 | 0.044795, 0/3 |
| get_sources / episode | 0.030021, 3/3 | 0.027547, 0/3 | 0.035536, 0/3 |
| materialise / episode | 0.029920, 3/3 | 30.085435, 3/3 | 0.053166, 0/3 |
| play_materialised / episode | 0.032538, 3/3 | 0.028753, 3/3 | 0.033450, 0/3 |

Totals remain **23/36 -> 6/36 -> 0/36**. Nov 7 to 8 movie list improved
1.013919s but movie sort worsened 4.580970s; Shows-root list/sort worsened
0.779844s/1.815764s. Failed October browse is not a successful speed baseline.
Movie list/sort remain the first optimization candidates once fresh successful
measurements exist; the existing DB-batching task is not duplicated.

No new targets establish selection causality. Day 028 found the same episode
on Nov 7/8 despite the selection repair. HTTP success and outcome labels still
do not prove real stream playback, validated MediaSources, stream bytes or
cold materialisation.

## One Nonduplicate Fix

Preserve **ttfb-loadtime-oidc-gate-measurement-path**, already appended by
this task's earlier attempt. The durable task and day-031 successor are in
hive commit `cb9784595fdc6a8885cde1ebcff0d094bfcb2e6f`; this resumed pass
completes the missing report, not a second enqueue of the same work.

The fix must restore authenticated, non-interactive daily measurement of the
deployed inactive API for movie AND episode while preserving interactive
authentication. It may use a gate-validated credential, a carefully secured
infra-side API path, or the resolved inactive in-cluster Service. A direct
Service path must explicitly document losing edge-path timing fidelity;
merely trusting the presence of a token header must not become an auth bypass.
Retain an explicit redirect reason without leaking response credentials or
blindly following redirects. Identify the introducing configuration change
from evidence rather than the release-time correlation alone.

Registered integration Check:
`./scripts/tests/p8-loadtime-daily-workflow.test.sh`.
The existing card requires a redirecting token-bearing HTTP fixture, real
daily LIVE engine/wrapper discovery and flow timing for both item types,
red-before/green-after proof, then post-merge retained nonempty targets and
a fresh flow push. A synthetic dry-run or parser-only check cannot prove
restoration. No future test pass or live recovery is claimed here.

This changes measurement reachability, not DB batching, target selection,
generic failure attribution or ratchet eligibility. The latter already
landed as `190cf37` and cannot help a batch that never reaches the guard.

## Independent Successor

Preserve **ttfb-reduction-031**, no dependencies, not before
2026-11-14T05:30:00Z (about +24h from the earlier enqueue). Read this report
first; inspect manifests newer than 1794485847 and check whether the gate fix
actually ran. Bind fresh flow push/run identity before comparing all three
historical durations AND error rates; keep failed batches separate. Check
request/list bounds and the 1500s wall independently. Prioritize movie
list/sort when fresh evidence exists, append one nonduplicate fix and an
independent day 032. Neither day 030 nor its fix blocks the daily successor.

No behavioral regression test applies to this analytical report. Read-only
verification and queue provenance are recorded in the hive change doc.
No operator steps required.
