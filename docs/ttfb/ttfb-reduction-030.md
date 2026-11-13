# TTFB Reduction 030

Diagnosed 2026-11-13 UTC after reading day 029 first. Diagnose-and-enqueue
only: no runtime, measurement, deployment or threshold changes.

## Newer Evidence

Two retained manifests are newer than run_start 1794399558:

| run_start | attempt_id | status / exit | discovery start | discovery / measure |
| --- | --- | --- | --- | --- |
| 1794485827 | cc6a500eb1cc49aebd060c10b4797eaf | failed / 1 | 1794485828 | 0s exit 1 / 0s exit 1 |
| 1794485847 | 5d9a7b17c2b54bd7808fcec38fcf5738 | failed / 1 | 1794485914 | 0s exit 1 / 0s exit 1 |

Both Nov 12 attempts executed checkout
`53da799141b58a1be01fa043731ebc153725f56e` (which contains `f0da20b`,
`7e28c4a` and the merged error-aware ratchet `190cf37`). Both retain
`discovery_failure={reason:user-invalid-response,curl_exit_code:0,http_status:302}`,
color green, empty targets/failures and no push/guard stage. The Nov 12
scheduled Job failed with BackoffLimitExceeded, like Nov 10 and Nov 11.
Eight consecutive attempts (Nov 9-12) have failed before timing any flow;
the newest six durably record the same user/302 reason. These are failed
batches, not flow samples, and stay out of every flow denominator.

## Following The 302

Day 029 had no redirect destination. This pass followed it with read-only
in-cluster GET probes against the same dev host the wrapper measures:

| request | result |
| --- | --- |
| `/Users`, no token | 302 to the OIDC provider's authorization endpoint (`client_id=jellyfin`, `redirect_uri=<dev host>/oauth2/callback`) |
| `/Users`, `X-Emby-Token` = the job's admin token | 302, Location host = the OIDC provider |
| `/Users`, `Authorization: MediaBrowser Token=...` | 302 |
| `/System/Info/Public`, no token | 200 |
| `/health`, no token | 302 to the OIDC provider |

The dev-host Ingress (green) carries a Traefik `forwardAuth` middleware
pointing at an `oauth2-proxy` deployment (`--upstream=static://202`), whose
only skip route is `GET=^/System/Info/Public$`. So the 302 is the
forward-auth gate issuing an interactive OIDC login redirect: the Jellyfin
API token is never evaluated by Jellyfin. This is directly observed, not
inferred from the status code, and it explains `curl_exit=0` (no `-L`) plus
the user-array parser rejection.

Not established: *when* the gate began covering the measured path. The
Ingress, middleware and oauth2-proxy objects predate the last successful
run (Nov 8 12:17Z); the phantom-library-{blue,green,shared} HelmReleases
re-released at 2026-11-08T23:37Z and a new oauth2-proxy ReplicaSet (args
unchanged) appeared the same minute, between the last success and the
first failure. That is temporal correlation only; which change put the
measured host behind the gate is left to the fix task to confirm.

## Bounds And Measurement Wall

`7e28c4a` request/list bounds are in the executed checkout but did not fire:
no curl 28 / HTTP 000, no timed list failure. The 1500s measurement wall was
not hit: the measurement stage lasted 0s. Run-start to discovery-start gaps
were 1s and 67s, outside the measured stage.

## Metric Attribution

Current evaluation 1794550040.166: daily run_start 1794485847, measure
stage 0s / exit 1, daily push 1794485914.3866994; flow push still
1794140993.9278193, which historical evaluation 1794141100 binds to Nov 8
run_start 1794140228. Current flow values are stale Nov 8 values.

| batch | run_start | flow push | errors |
| --- | --- | --- | --- |
| Oct 30 | 1793359027 | 1793361169.4467623 | 23/36 |
| Nov 7 | 1794053824 | 1794054641.644034 | 6/36 |
| Nov 8 | 1794140228 | 1794140993.9278193 | 0/36 |
| Nov 9-12 | (8 attempts) | none | not a flow batch |

No fresh duration exists to compare. Latest successful (Nov 8) values:
movie list/sort 91.135839s/90.006179s, Shows-root list/sort
27.062847s/26.264750s; day 029 holds the full 12-tuple table. Movie
list/sort remain the first optimization candidates once measurement
resumes. No stream playback, MediaSources validity or target-selection
causality is claimed.

## One Nonduplicate Fix

Enqueue **ttfb-loadtime-oidc-gate-measurement-path**. The daily loop cannot
produce any measurement while every API request to the measured host is
redirected to an interactive OIDC login; every other TTFB task is blocked
behind this. It is distinct from DB batching, target selection, discovery
failure attribution (the reason already names user/302) and the
error-aware ratchet (failed batches never reach the guard).

Required outcome: the daily LIVE measurement reaches the deployed
Jellyfin API with its token and completes discovery, while the gate keeps
protecting interactive users. The implementer chooses and documents one
of: a non-interactive credential the gate accepts (e.g. a bearer/JWT path
the proxy validates), a narrowly scoped gate exemption for token-bearing
API requests, or measuring via the resolved color's in-cluster Service
(trading away the edge path the wrapper currently claims to measure —
that fidelity loss must be stated). Also make a redirect fail discovery
with an explicit gate/redirect reason rather than a generic parser failure.
Check: the registered daily-workflow integration test must exercise a
stub host that 302s token-bearing API requests (fails before) and the
chosen path (passes after), movie and episode.

## Independent Successor

Enqueue **ttfb-reduction-031**, no dependencies, not before
2026-11-14T05:30:00Z. Read this report first.

No behavioral regression test applies to this analytical report. Exact
read-only probe commands are in the private hive change doc.
No operator steps required.
