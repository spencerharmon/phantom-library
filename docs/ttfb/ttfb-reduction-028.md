# TTFB Reduction 028

Diagnosed 2026-11-11 UTC after reading day 027 first. Analysis and enqueue
only; no measurement, runtime, deployment or threshold changes.

## Newer Evidence

Five finalized evidence manifests are newer than run_start 1794053824. All
executed checkout `1db50e117e23f1c58aea41ab147f3d2dc37cce42`, which contains
both request bounds `7e28c4a` and episode-selection repair `020019b`.

| run_start | status / exit | discovery seconds / exit | measure seconds / exit |
| --- | --- | --- | --- |
| 1794140228 (Nov 8) | complete / 0 | 20 / 0 | 764 / 0 |
| 1794226627 (Nov 9) | failed / 1 | 0 / 1 | 0 / 1 |
| 1794226647 (Nov 9 retry) | failed / 1 | 0 / 1 | 0 / 1 |
| 1794313027 (Nov 10) | failed / 1 | 0 / 1 | 0 / 1 |
| 1794313046 (Nov 10 retry) | failed / 1 | 1 / 1 | 1 / 1 |

The completed batch retained all four typed targets, no failures, push exit
0, and guard exit 3 in 5s. Completion is not a guard pass: the historical
metrics show all twelve breach gauges equal 1. The four later attempts
resolved a color but retained empty targets, no flow failures and **no
discovery_failure reason**. These are failed batches, not zero-error samples.
Their scheduled Jobs failed with BackoffLimitExceeded; latest Job pods are
already gone. The precise HTTP/JSON/user/channel failure is unrecoverable
from these manifests. Do not label it a dead episode or an Ingress failure.

No new manifest records curl 28/HTTP 000. There is no evidence that the
`7e28c4a` request/list bounds fired. The completed measurement took 764s
(817s on Nov 7); none of these five attempts hit the 1500s wall. The later
0-1s failures stopped during discovery, not during successful fast browsing.

## Durations And Errors

Historical evaluations 1793361200, 1794113444.263 and 1794141100 identify
run_start 1793359027, 1794053824 and 1794140228 respectively. Each cell below
is median seconds, errors/runs. Values are full-flow durations, not raw server
TTFB; episode list/sort labels actually measure the Shows root, not episode
folder navigation.

| flow / type | Oct 30 | Nov 7 | Nov 8 |
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

Total HTTP-flow errors: **23/36 -> 6/36 -> 0/36**, not the old card's 31.
Movie list decreased 1.013919s while sort increased 4.580970s; Shows-root list
and sort increased 0.779844s and 1.815764s. No sustained browse speedup is
established by one batch. Failed October browse is not a successful-latency
baseline. Movie list remains the largest successful duration, closely followed
by movie sort; neither is remotely sub-second.

The episode-selection repair actually ran on Nov 8, but selected the SAME
episode ID as Nov 7. HTTP success therefore does not prove it avoided the
previous source, nor that it caused the improvement. Availability can change;
Phantom is not guaranteed playable. Curl-only materialise/play outcomes do
not verify MediaSources, stream bytes or cold materialisation. No server-cause
or real-playback success claim follows from these timings.

At current evaluation 1794374599.133 the daily run-start value is 1794313046,
but flow push time is still 1794140993.9278193. Historical evaluation
1794141100 binds those exact flow values to Nov 8 (daily push
1794140998.84607). Never attach the stale zero-error flow metrics to Nov 10's
failed discovery. No newer successful flow comparison exists.

## One Nonduplicate Fix

Enqueue **ttfb-loadtime-target-discovery-failure-reasons**. The latest failures
prevent all fresh latency diagnosis, including the 91s movie list. In
`47-loadtime-flows.sh`, User/Channels/Movie/Series/Season/Episode HTTP and
validation failures call generic `fail`; cleanup records only the discovery
stage exit. `loadtime-evidence.py` accepts only Ingress/evidence-write reasons
and no-playable-episode. Thus known target-discovery error branches lose their
reason when pods expire. Empty targets narrow these runs to before the first
successful Movie receipt, not to one proven failing endpoint.

Extend the existing sanitized evidence contract and wire these actual failure
branches to enumerated stage/reason plus relevant transport status, without
persisting bodies, URLs, credentials or arbitrary stderr. Exercise the real
LIVE engine through the daily wrapper with HTTP, malformed JSON, empty/wrong
types and availability failures. Assert durable reasons, valid target prefixes,
no timing/push after failed discovery, and unchanged successful twelve-flow
behavior. Preserve existing timeout and no-playable-episode semantics.

This is distinct from the existing Ingress-only
`ttfb-loadtime-discovery-failure-attribution`, typed-target discovery,
playable-target selection, and movie-render DB-batching fix
`ttfb-list-load-movie-render-profile`. Do not refile those tasks or guess a
server optimization from aggregate full-list time. Prioritize restoring
actionable daily evidence over an unsupported duplicate movie optimization.

## Independent Successor

Enqueue **ttfb-reduction-029**, no dependencies, not before
2026-11-12T05:30:00Z (about +24h). Read this report first; inspect manifests
newer than 1794313046 and compare fresh successful samples with Nov 8, Nov 7
and Oct 30, keeping failed batches separate. Verify target-failure reasons,
request bounds and the measurement wall independently. Movie list/sort remain
the top browse candidates once fresh evidence exists. Append one nonduplicate
fix and independent day 030, without threshold relaxation or runtime changes.

No behavioral regression test applies to this analytical report. Read-only
probe commands and results are in the private hive change doc. No operator
action is required.
