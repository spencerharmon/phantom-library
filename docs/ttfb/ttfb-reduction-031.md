# TTFB Reduction 031

Diagnosed 2026-11-14 UTC; read day 030 first. Diagnose-and-enqueue only.
No runtime, deployment, thresholds or measurement behavior changed.

## Recovery And Attribution

Exactly one retained manifest is newer than 1794485847:
run_start **1794572228**, attempt `638c85ad6d6b4d8f9354e0fe4ec3d16b`,
checkout `baa36c6eb8feceff641072c00f297af115ec515c`, inactive color green.
Discovery succeeded in 25s at 1794572229 and retained Movie, Series, Season
and Episode targets. Measure ran 849s/exit 0, push 0s/exit 0, guard 6s/exit 3.
The manifest is complete/exit 0, which does NOT mean all samples succeeded
or the regression guard passed. The scheduled Job completed on Nov 13.

Executed-checkout ancestry includes the OIDC path fix `b87f853`, request/list
bounds `7e28c4a`, and error-aware ratchet `190cf37`. The wrapper now resolves
the inactive Ingress backend and measures its authenticated in-cluster
Service, not the interactive HTTPS edge. This restores discovery, NOT edge
authentication behavior. TLS, ingress, OIDC and external network overhead
are excluded: the historical edge and new backend measurements are different
populations. Do not attribute their differences solely to plugin speed.

Flow pushes are bound to daily run identity before comparing:

| evaluation | daily run_start | flow push_time_seconds | daily push_time_seconds |
| --- | --- | --- | --- |
| 1793361200 | 1793359027 | 1793361169.4467623 | 1793361174.3147707 |
| 1794113444.263 | 1794053824 | 1794054641.644034 | 1794054646.763412 |
| 1794141100 | 1794140228 | 1794140993.9278193 | 1794140998.84607 |
| 1794573200 | 1794572228 | 1794573078.935169 | 1794573085.3758104 |
| 1794634589.947 (current) | 1794572228 | 1794573078.935169 | 1794573085.3758104 |

The fresh flow push falls in the manifest's push stage at 1794573078;
the daily push follows its guard. An earlier historical query at 1794573100
still returned Nov 12 daily / Nov 8 flow values, consistent with scrape
visibility lag: query evaluation time alone is not batch identity. This is
temporal attribution, not an attempt-ID-bearing flow receipt. No overlapping
daily Job is indicated; the schedule uses concurrencyPolicy Forbid. The
existing receipt verifier's concurrent-writer limitation remains.

## Durations And Errors

Cells are median seconds, errors/runs. These are full HTTP-flow durations,
NOT raw server TTFB. Episode list/sort means Shows root, not navigation to
an episode. The eight Nov 9-12 failed-discovery attempts remain separate
failed batches, never added to flow denominators.

| flow / type | Oct 30: 1793359027 | Nov 7: 1794053824 | Nov 8: 1794140228 | Nov 13: 1794572228 |
| --- | --- | --- | --- | --- |
| list_load / movie | 10.118952, 3/3 | 92.149758, 0/3 | 91.135839, 0/3 | 103.210877, 0/3 |
| sort_change / movie | 30.393210, 3/3 | 85.425209, 0/3 | 90.006179, 0/3 | 111.128244, 1/3 |
| info_open / movie | 0.041445, 0/3 | 0.053861, 0/3 | 0.058460, 0/3 | 0.033739, 0/3 |
| get_sources / movie | 0.029544, 0/3 | 0.042807, 0/3 | 0.039677, 0/3 | 0.025077, 0/3 |
| materialise / movie | 0.031143, 0/3 | 0.039514, 0/3 | 0.037035, 0/3 | 0.022006, 0/3 |
| play_materialised / movie | 0.031667, 0/3 | 0.043975, 0/3 | 0.058742, 0/3 | 0.028043, 0/3 |
| list_load / episode | 343.661371, 2/3 | 26.283003, 0/3 | 27.062847, 0/3 | 30.365053, 0/3 |
| sort_change / episode | 36.425254, 3/3 | 24.448986, 0/3 | 26.264750, 0/3 | 32.689070, 0/3 |
| info_open / episode | 0.048825, 3/3 | 0.037072, 0/3 | 0.044795, 0/3 | 0.048761, 0/3 |
| get_sources / episode | 0.030021, 3/3 | 0.027547, 0/3 | 0.035536, 0/3 | 0.023320, 0/3 |
| materialise / episode | 0.029920, 3/3 | 30.085435, 3/3 | 0.053166, 0/3 | 0.031458, 0/3 |
| play_materialised / episode | 0.032538, 3/3 | 0.028753, 3/3 | 0.033450, 0/3 | 0.029975, 0/3 |

Totals: **23/36 -> 6/36 -> 0/36 -> 1/36**. Versus Nov 8, movie
list/sort are +12.075038s/+21.122065s; Shows-root list/sort are
+3.302206s/+6.424320s. Versus Nov 7, movie list/sort are
+11.061119s/+25.703035s. These are descriptive differences across the
measurement-path change, not a controlled regression estimate. October's
failed movie browse is not a successful speed baseline. Movie browse remains
the first diagnostic priority even without edge overhead.

## Bounds And Failure

The one retained failure is movie sort sample 3, `badge_states`, curl 28,
HTTP 000, transport/no-response. The matching list-error gauge is 1.
This proves bounded failure in the badge-stage vocabulary, not that all
111s were spent on a States request: the code also assigns budget exhaustion
and the DOM tail to that stage (`47-loadtime-flows.sh:755-790`).

Request/list defaults remain 60s/120s. The deployed CronJob supplies no
timeout/deadline override or envFrom; the wrapper therefore uses its 1800s
default minus 300s = **1500s measurement wall**, and a **1410s engine budget**.
The Kubernetes Job deadline is independently 5400s. Measure completed in
849s without outer exit 124; this was not a 1500s wall hit. Do not raise
limits or re-file timeout work. Guard exited 3 with six breach series;
the erroring movie-sort tuple is not a successful ratchet candidate.
No actual stream bytes, cold materialisation or playback correctness are
established by HTTP success/outcome labels.

## One Nonduplicate Fix

Enqueue **ttfb-loadtime-browse-stage-durations**: retain actual per-sample
elapsed time for pagination (`channel_items`), badge fan-out (`badge_states`),
DOM work (`dom_materialise`) and explicit residual overhead. Preserve totals,
errors, deadlines, selection, batch sizes and ratchet policy. Publish bounded
stage sums/counts for the same sample population, separating success/failure;
keep skipped and unattempted stages explicit, not successful zeroes.

The engine currently retains only one total around all three phases
(`47-loadtime-flows.sh:705-785`); its badge helper combines network fan-out
and DOM work (`:232-259`). Existing failure attribution explicitly adds
nothing for successful samples. Server-side episode render/DB profiling is
already separately owned. This fix measures the client pipeline seam; it
does not duplicate either optimization or error categorization. No speedup
is claimed from adding instrumentation.

Invariant: each attempted sample's total reconciles to its component sum
within documented rounding tolerance. Independently aggregated medians are
NOT additive; use sample-level reconciliation and matching-population sums.

Registered Check: `./scripts/tests/p8-loadtime-daily-workflow.test.sh`.
Extend it with real LIVE engine, delayed local HTTP fixtures, wrapper,
publisher and retained evidence: both movie and Shows-root list/sort;
multiple pages and badge batches; delay each stage independently; mixed
costs across three samples; late page failure and badge timeout; skipped
samples; captured PUT containing the measured stage sums/counts. Show
red-before/green-after assertions on actual emitted timing, not source text
or synthetic dry-run values. Preserve existing totals/counts. No future
implementation/test pass is claimed by this analytical report.

## Independent Successor

Enqueue **ttfb-reduction-032**, no dependencies, not before
**2026-11-15T05:40:00Z**, about +24h. Read this report first; inspect manifests
newer than 1794572228, bind flow push and daily identity, and compare durations
AND errors against all four batches above. Keep discovery failures separate.
Track the backend/edge discontinuity; prioritize movie list/sort, using stage
durations if available, without duplicating existing work. Check per-request,
per-list, engine and outer-wall limits separately. Append one nonduplicate
fix and an independent day 033. Neither day 031 nor its fix blocks day 032.

No behavioral regression test applies to analytical documentation. Exact
read-only verification is retained in the private hive change doc. No
operator steps required.
