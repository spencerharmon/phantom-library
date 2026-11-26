# TTFB Reduction 033

Diagnosed 2026-11-26 UTC; read day 032 first. Diagnose-and-enqueue only.
No runtime, deployment, measurement behavior or thresholds changed.

## Retained Runs And Push Binding

The read-only evidence snapshot contains 32 manifests, **five newer than
1794658629**: four completed measurements and one failed discovery. Retention
is 32, not a guarantee that every scheduled Job attempt left a manifest.
No measurements are inferred for gaps between these runs.

| UTC run start / epoch | attempt_id | checkout | discovery s/exit | measure s/exit | push s/exit | guard s/exit |
| --- | --- | --- | --- | --- | --- | --- |
| Nov 15 12:17:10 / 1794745030 | 1b866287c6e6486facbaa35d8929c55b | bc140db7fec1458efe9a2d1ed313914a164d230e | 21/0 | 664/0 | 0/0 | 4/3 |
| Nov 16 12:17:08 / 1794831428 | 5a7b755e9bd548a0b913b2df7895b662 | b04395f239efdedf0c2bf672d0799cabc6137ae6 | 19/0 | 634/0 | 0/0 | 9/3 |
| Nov 18 23:41:37 / 1795045297 | da47e78a3fc74c7e81f84d68ceedcaba | b04395f239efdedf0c2bf672d0799cabc6137ae6 | 28/0 | 585/0 | 0/0 | 5/3 |
| Nov 25 12:17:04 / 1795609024 | e13ad267f585423d911c59ec01955841 | b04395f239efdedf0c2bf672d0799cabc6137ae6 | 71/1 | 71/1 | absent | absent |
| Nov 25 12:18:33 / 1795609113 | 52b2653916c24ffd906b2cd38513cabc | b04395f239efdedf0c2bf672d0799cabc6137ae6 | 5/0 | 611/0 | 0/0 | 4/3 |

Discovery is inside measure, not an additive stage. Failed discovery says
`series-request-failed`, curl28/HTTP000, with only the movie target selected.
It has zero browse samples, no push/guard and **no place in a 36-sample flow
denominator**. The four completed manifests select the same four targets as
day 032. Three completed batches nevertheless contain six HTTP failures each.
`complete`/exit0 means the measurement finished, not that playback or guard passed.

The current instant metrics query returned an empty vector. Historical queries
below returned the actual measurements; do not call their evaluation times
fresh pushes. Rechecked all five day-032 baseline bindings as well:

| evaluation | daily run_start | flow push_time_seconds | daily push_time_seconds |
| --- | --- | --- | --- |
| 1793361200 | 1793359027 | 1793361169.4467623 | 1793361174.3147707 |
| 1794113444.263 | 1794053824 | 1794054641.644034 | 1794054646.763412 |
| 1794141100 | 1794140228 | 1794140993.9278193 | 1794140998.84607 |
| 1794573200 | 1794572228 | 1794573078.935169 | 1794573085.3758104 |
| 1794725505.021 | 1794658629 | 1794659236.9590526 | 1794659241.4343555 |
| 1794745789 | 1794745030 | 1794745694.992348 | 1794745699.763486 |
| 1794832161 | 1794831428 | 1794832062.5569715 | 1794832071.4278424 |
| 1795045978 | 1795045297 | 1795045883.7842288 | 1795045888.1096196 |
| 1795609819 | 1795609113 | 1795609725.6506348 | 1795609730.0128436 |

Each new flow push falls in its retained push-stage second; each daily push
follows guard. The last guard starts 1795609725, lasts 4s, and publishes early
in second 1795609730. This is temporal binding, not an attempt-ID receipt;
concurrent-writer ambiguity is not eliminated. CronJob concurrency is Forbid.

## Durations AND Errors

Cells are **median seconds, errors/runs**, not raw server TTFB. These full
HTTP-flow durations exclude ingress/TLS/OIDC/network-edge cost on the newer
Service path. Oct 30, Nov 7 and Nov 8 measured the earlier edge path;
cross-path comparisons are descriptive, not controlled estimates. Episode
list/sort means Shows root, not timed series/season/episode navigation.

### All Five Baselines (requeried)

| flow / type | Oct 30: 1793359027 | Nov 7: 1794053824 | Nov 8: 1794140228 | Nov 13: 1794572228 | Nov 14: 1794658629 |
| --- | --- | --- | --- | --- | --- |
| list_load / movie | 10.118952, 3/3 | 92.149758, 0/3 | 91.135839, 0/3 | 103.210877, 0/3 | 75.935962, 0/3 |
| sort_change / movie | 30.393210, 3/3 | 85.425209, 0/3 | 90.006179, 0/3 | 111.128244, 1/3 | 74.556553, 0/3 |
| info_open / movie | 0.041445, 0/3 | 0.053861, 0/3 | 0.058460, 0/3 | 0.033739, 0/3 | 0.030881, 0/3 |
| get_sources / movie | 0.029544, 0/3 | 0.042807, 0/3 | 0.039677, 0/3 | 0.025077, 0/3 | 0.015017, 0/3 |
| materialise / movie | 0.031143, 0/3 | 0.039514, 0/3 | 0.037035, 0/3 | 0.022006, 0/3 | 0.013739, 0/3 |
| play_materialised / movie | 0.031667, 0/3 | 0.043975, 0/3 | 0.058742, 0/3 | 0.028043, 0/3 | 0.013604, 0/3 |
| list_load / episode | 343.661371, 2/3 | 26.283003, 0/3 | 27.062847, 0/3 | 30.365053, 0/3 | 22.302643, 0/3 |
| sort_change / episode | 36.425254, 3/3 | 24.448986, 0/3 | 26.264750, 0/3 | 32.689070, 0/3 | 21.934159, 0/3 |
| info_open / episode | 0.048825, 3/3 | 0.037072, 0/3 | 0.044795, 0/3 | 0.048761, 0/3 | 0.020936, 0/3 |
| get_sources / episode | 0.030021, 3/3 | 0.027547, 0/3 | 0.035536, 0/3 | 0.023320, 0/3 | 0.013652, 0/3 |
| materialise / episode | 0.029920, 3/3 | 30.085435, 3/3 | 0.053166, 0/3 | 0.031458, 0/3 | 0.012804, 0/3 |
| play_materialised / episode | 0.032538, 3/3 | 0.028753, 3/3 | 0.033450, 0/3 | 0.029975, 0/3 | 0.012791, 0/3 |
| total errors/runs | **23/36** | **6/36** | **0/36** | **1/36** | **0/36** |

### Four New Completed Measurements

| flow / type | Nov 15: 1794745030 | Nov 16: 1794831428 | Nov 18: 1795045297 | Nov 25: 1795609113 |
| --- | --- | --- | --- | --- |
| list_load / movie | 79.698064, 0/3 | 76.019322, 0/3 | 72.441434, 0/3 | 76.096095, 0/3 |
| sort_change / movie | 75.344849, 0/3 | 77.692698, 0/3 | 69.639986, 0/3 | 76.084308, 0/3 |
| info_open / movie | 0.022502, 0/3 | 0.029551, 0/3 | 0.024034, 0/3 | 0.021984, 0/3 |
| get_sources / movie | 0.011608, 0/3 | 0.013196, 0/3 | 0.014122, 0/3 | 0.012349, 0/3 |
| materialise / movie | 0.020270, 0/3 | 0.013546, 0/3 | 0.012254, 0/3 | 0.012430, 0/3 |
| play_materialised / movie | 0.015085, 0/3 | 0.017209, 0/3 | 0.013601, 0/3 | 0.012480, 0/3 |
| list_load / episode | 23.809120, 0/3 | 20.758448, 0/3 | 20.475425, 0/3 | 20.414236, 0/3 |
| sort_change / episode | 22.676193, 0/3 | 22.377047, 0/3 | 20.742639, 0/3 | 20.732964, 0/3 |
| info_open / episode | 0.023106, 0/3 | 0.020585, 0/3 | 0.019019, 0/3 | 0.020146, 0/3 |
| get_sources / episode | 0.012878, 0/3 | 0.010484, 0/3 | 0.009592, 0/3 | 0.009628, 0/3 |
| materialise / episode | 0.013418, 0/3 | 0.034004, 3/3 | 0.030082, 3/3 | 0.030396, 3/3 |
| play_materialised / episode | 0.014395, 0/3 | 0.011960, 3/3 | 0.012094, 3/3 | 0.011033, 3/3 |
| total errors/runs | **0/36** | **6/36** | **6/36** | **6/36** |

Versus Nov 14, newest movie list/sort is **0.160133s/1.527755s slower**;
Shows-root is **1.888407s/1.201195s faster**. Movie remains faster than Nov
7/8/13, but this is not a monotonic improvement: Nov 25 is slower than Nov
18 by 3.654661s/6.444322s. October's failed browse is not a speed baseline.
Nov 13 also includes one movie-sort badge curl28/HTTP000 failure.

From Nov 16 onward, all three episode materialise samples return HTTP500,
and all three play samples HTTP400 (curl22, stage=request). Same target IDs
do not guarantee continued playability. The ~30ms materialise failures are
not equivalent to Nov 7's ~30s failures or to successful cold materialisation.
The emitted `rig_outcome` gostream_register_fail is synthetic, not a real
server cause diagnosis. Do not refile target selection or failure attribution
on that label alone. HTTP success also does not establish response-body
validity, playable bytes or browser paint. No new failure cause is claimed.

## Same-Population Browse Components

All 48 new browse samples succeeded. Means below are calculated from the
same three samples in each row, not sums of independent medians. Published
stage sums/counts agree with retained components. The first four baselines
have no retained decomposition; their stage costs are **unknown**, not zero.

| run / flow / scope | pages / badge batches per sample | channel_items mean | badge_states mean | DOM mean | residual mean | total mean |
| --- | --- | --- | --- | --- | --- | --- |
| Nov 14 / list / movies | 83 / 42 | 38.889180 | 27.834935 | 0.023847 | 9.122930 | 75.870891 |
| Nov 14 / sort / movies | 83 / 42 | 37.997562 | 27.927153 | 0.025561 | 8.825928 | 74.776204 |
| Nov 14 / list / shows | 49 / 25 | 11.842390 | 5.319442 | 0.024256 | 5.194448 | 22.380536 |
| Nov 14 / sort / shows | 49 / 25 | 11.772059 | 5.205041 | 0.023014 | 5.151743 | 22.151857 |
| Nov 15 / list / movies | 83 / 42 | 40.778087 | 29.315113 | 0.026329 | 9.622419 | 79.741949 |
| Nov 15 / sort / movies | 83 / 42 | 41.732988 | 29.645348 | 0.024572 | 8.995988 | 80.398897 |
| Nov 15 / list / shows | 49 / 25 | 13.002830 | 5.717536 | 0.025093 | 5.581137 | 24.326596 |
| Nov 15 / sort / shows | 49 / 25 | 12.448554 | 5.425870 | 0.023993 | 5.312923 | 23.211340 |
| Nov 16 / list / movies | 83 / 42 | 40.125701 | 29.738807 | 0.026746 | 6.523839 | 76.415093 |
| Nov 16 / sort / movies | 83 / 42 | 41.345866 | 30.122997 | 0.025151 | 6.416877 | 77.910891 |
| Nov 16 / list / shows | 49 / 25 | 12.050127 | 5.240240 | 0.024425 | 3.539872 | 20.854664 |
| Nov 16 / sort / shows | 49 / 25 | 12.773172 | 5.663980 | 0.024761 | 3.722243 | 22.184156 |
| Nov 18 / list / movies | 83 / 42 | 38.978285 | 27.572168 | 0.023600 | 6.417702 | 72.991754 |
| Nov 18 / sort / movies | 83 / 42 | 37.542544 | 26.382915 | 0.023242 | 5.731149 | 69.679849 |
| Nov 18 / list / shows | 49 / 25 | 11.943294 | 5.067242 | 0.022580 | 3.466619 | 20.499735 |
| Nov 18 / sort / shows | 49 / 25 | 12.049276 | 5.125681 | 0.023146 | 3.440755 | 20.638858 |
| Nov 25 / list / movies | 84 / 42 | 40.726551 | 30.410821 | 0.024699 | 6.033874 | 77.195945 |
| Nov 25 / sort / movies | 84 / 42 | 42.892407 | 30.212305 | 0.023050 | 5.852377 | 78.980139 |
| Nov 25 / list / shows | 49 / 25 | 12.068955 | 5.125555 | 0.022436 | 3.351086 | 20.568032 |
| Nov 25 / sort / shows | 49 / 25 | 12.116649 | 5.123659 | 0.021939 | 3.426451 | 20.688698 |

Maximum per-sample reconciliation difference is 1.4210854715202004e-14s.
Rounded table means can differ in the last displayed decimal. Latest movie
list sample 1: 40.644843 + 29.512881 + 0.024070 + 5.914301 = 76.096095s.
Movie fetch plus badge work still dominates (~92% of latest mean totals).

Page-parse fix `9163950` is an ancestor of the Nov 16/18/25 executed checkout,
not the Nov 15 checkout. After it, movie residual falls from roughly 9s to
6s and Shows-root from roughly 5s to 3.5s. This is consistent with fewer
interpreter launches but not a controlled causal estimate: request costs,
pages and load also change. The newest movie sample fetches 84 rather than
83 pages. Do not credit all the residual reduction to parsing or promise
that the remaining residual is removable; it includes budget checks, timing,
ID preparation and temporary-file overhead as well.

## Independent Bounds

- **60s request / 120s list:** engine defaults remain unchanged. New completed
  batches' largest browse sample is 90.816777s (Nov 15 movie sort); latest
  maximum is 87.536535s. None of the 48 new browse samples timed out. Individual
  request maxima are not retained; dividing stage sums by pages is not a max.
  The 71s failed discovery is an aggregate containing a series curl28, not
  proof of a single 71s request violating the 60s request default.
- **1410s inner engine budget:** wrapper derives 1500 minus 90. The longest
  new completed measure is 664s; latest 611s. No observed engine exhaustion.
- **1500s outer measurement wall:** derived from wrapper default input 1800
  minus 300. No observed measure exit124 or hard-wall termination. This wall
  encloses the engine, not the entire wrapper, checkout/init or Job retries.
- **5400s Kubernetes Job deadline:** separately confirmed on the live CronJob;
  no timeout/budget/sample/active-deadline env override and no envFrom. Its
  actual 5400 is NOT passed as the wrapper's default input. Completed Jobs
  retained for Nov 16/18/25 have wall spans 655/1572/714s respectively. Nov 18
  includes one failed pod plus one success; 1572s does not imply the successful
  engine's 585s measure hit a 1500s wall. Latest Job likewise has failed=1,
  succeeded=1. The latest Job's pod query returned no pods; its logs were not
  available for a deeper attempt audit.

All four new guards exit3; latest publishes six breach series (four browse,
movie get_sources, episode info_open). Errors and guard breaches remain
independent. No ceiling relaxation, ratchet change or deploy is proposed here.

## One Nonduplicate Fix: Prepare Badge Payloads Once

Enqueue **ttfb-loadtime-badge-payload-preparation**. In
`tools/rig-scenarios/47-loadtime-flows.sh:862-869`, `live_batch_runner` launches
Python, rereads the ENTIRE accumulated `all_ids_file`, constructs all IDs,
then slices out one batch. It repeats for every batch: 42 full-file scans
per successful movie sample and 25 per Shows-root sample. Across the latest
six movie and six Shows-root samples, that is **402 Python launches and
full-file scans**, solely for payload slicing. These costs are before the
badge request timer and therefore in residual, not in `badge_states`.

Replace that repeated preparation with a single linear pass per successful
browse sample, partitioning ordered IDs into the same payloads before serial
sending. Eliminate the old repeated full-file path; retain bounded scratch
lifetime and count the preparation inside the existing list/engine budget.
Keep pagination, 400-item badge limit, request ordering, exact IDs/counts,
DOM totals, failure/skip semantics, deadlines, stage accounting, target
selection and ratchet unchanged. No parallel requests or larger batches.

This is **not** day 032's per-page JSON parse fix (already consumed): it
optimizes the post-pagination ID-file slicing loop, not page parsing. It
also does not duplicate stage instrumentation, server DB batching, target
selection, failure attribution, ratchet or OIDC work. Network work dominates,
but server optimization already has owners. Savings here must be measured;
402 launches are a source-derived operation count, not measured CPU seconds.

Registered integration Check: `./scripts/tests/p8-loadtime-daily-workflow.test.sh`.
Extend its real LIVE HTTP fixture through wrapper, engine, retained evidence,
publisher and guard. For BOTH roots and BOTH list/sort flows, use multiple
pages/batches with distinct ordered IDs and a partial last batch; assert exact
payload partition/order and DOM totals. Observe actual ID-file read/prepare
operations **per sample pathname**, proving one complete traversal rather
than one per batch (red before on current code). Include small and larger
multi-batch populations so an implementation that still rescans per batch
cannot pass a fixed global count. Keep the already-landed per-page parser
assertions. Cover empty lists, late-page failure (no preparation/sending),
late badge failure and budget expiration (no subsequent request/DOM), complete
12-tuple publication, errors/skips and additive retained/published components.
Do not substitute a helper-only benchmark or flaky wall-time threshold for
these end-to-end assertions. No future test pass or speedup is claimed here.

## Independent Successor

Enqueue **ttfb-reduction-034**, no dependencies, not before
**2026-11-27T04:15:00Z** (about +24h). Read this report first. Inspect retained
manifests newer than 1795609113; do not turn the current empty instant query
into a fabricated zero/healthy batch. Bind flow/daily pushes before comparing
all five historical baselines and these four newer batches. Track 6/36 episode
HTTP errors separately from browse speed and exclude failed discovery entirely
from flow denominators. Compare same-population stage components and 84/42
movie versus 49/25 Shows-root fan-out; inspect payload preparation only after
its executed checkout contains the fix. Keep all four bounds separate. Append
one nonduplicate fix with real integration Check and an independent day 035.
Neither this diagnosis nor its fix gates the successor.

Analytical documentation justifies check=none. Read-only commands and retained
raw evidence are recorded in the private hive change doc. No operator steps.
