# TTFB Reduction 032

Diagnosed 2026-11-15 UTC; read day 031 first. Diagnose-and-enqueue only.
No runtime, deployment, measurement behavior or thresholds changed.

## Recovery And Attribution

Exactly one retained manifest is newer than 1794572228: run_start
**1794658629**, attempt `29d93210833a4992b4ad3c77cd1f31ff`, checkout
`4d97d6d6dbd9685a628b08448d32f24cd53a2ffe`, retention 32.
Discovery 19s/0, measure 607s/0, push 0s/0, guard 4s/3; status complete,
exit 0, failures empty. The scheduled Job ran Nov 14, 12:17:00-12:27:25 UTC,
succeeded=1. All four discovery targets match day 031.

The executed checkout includes browse decomposition `1151342`, OIDC Service
path fix `b87f853`, bounds `7e28c4a` and error-aware ratchet `190cf37`.
This measures the authenticated BACKEND Service, excluding TLS, ingress,
OIDC and external network overhead. Oct 30, Nov 7 and Nov 8 used the earlier
edge path; cross-path comparisons are descriptive, not controlled estimates.

| evaluation | daily run_start | flow push_time_seconds | daily push_time_seconds |
| --- | --- | --- | --- |
| 1793361200 | 1793359027 | 1793361169.4467623 | 1793361174.3147707 |
| 1794113444.263 | 1794053824 | 1794054641.644034 | 1794054646.763412 |
| 1794141100 | 1794140228 | 1794140993.9278193 | 1794140998.84607 |
| 1794573200 | 1794572228 | 1794573078.935169 | 1794573085.3758104 |
| 1794725505.021 | 1794658629 | 1794659236.9590526 | 1794659241.4343555 |

Flow push falls in the manifest's push-stage second, 1794659236; daily push
follows guard (start 1794659237, duration 4s). Attribution uses payload
timestamps, not evaluation time alone. This is temporal binding, not an
attempt-ID flow receipt; concurrent-writer ambiguity is not eliminated.
CronJob concurrency remains Forbid. Failed discovery batches, including the
eight attempts described on day 031, never enter the flow denominators.

## Durations And Errors

Each cell is median seconds, errors/runs. These are full HTTP-flow durations,
not raw server TTFB. Episode list/sort means Shows root, not timed navigation
through series, season and episode.

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

Totals: **23/36 -> 6/36 -> 0/36 -> 1/36 -> 0/36**. Versus Nov 13,
movie list/sort fell 27.274915s/36.571691s and Shows-root fell
8.062410s/10.754911s. Versus Nov 8, movie fell 15.199877s/15.449626s;
versus Nov 7, 16.213796s/10.868656s. October's failed movie browse is not
a successful speed baseline. Even Nov 13 versus Nov 14 is uncontrolled:
the former includes a movie-sort badge-stage curl28/HTTP000 failure and
the instrumentation changed. No particular plugin optimization caused
these differences on the evidence available.

## Browse Decomposition

All 12 retained browse samples succeeded. Each movie sample fetched 83
pages and sent 42 badge batches; each Shows-root sample fetched 49 pages
and sent 25 batches. Component means below use the SAME three samples
per row, calculated from retained seconds and confirmed against published
stage sums/counts. These means, unlike independently selected medians, add.

| flow / scope | channel_items | badge_states | dom_materialise | residual | total mean |
| --- | --- | --- | --- | --- | --- |
| list_load / movies_root | 38.889180 | 27.834935 | 0.023847 | 9.122930 | 75.870891 |
| sort_change / movies_root | 37.997562 | 27.927153 | 0.025561 | 8.825928 | 74.776204 |
| list_load / shows_root | 11.842390 | 5.319442 | 0.024256 | 5.194448 | 22.380536 |
| sort_change / shows_root | 11.772059 | 5.205041 | 0.023014 | 5.151743 | 22.151857 |

Per-sample component reconciliation has maximum floating-point difference
1.4210854715202004e-14s (well below microsecond rounding). Example: movie
list sample 1 is 40.084838 + 28.758493 + 0.024275 + 9.255274 = 78.122880s.
Across movie samples pagination is 37.402719-40.084838s, badge fan-out
27.348161-28.758493s, residual 8.693463-9.420052s. Pagination accounts
for about 51%, badges 37%, residual 12%. Movie browse remains the priority.

These are aggregate stage times: dividing by 83 pages gives an AVERAGE,
not a maximum individual request duration. DOM is the synthetic helper,
not browser paint. Residual includes parsing, interpreter startup, budget
checks, temporary-file work and other unassigned overhead. It does not
isolate Python's contribution or prove that all 9 seconds are removable.

## Independent Bounds

Engine request/list defaults remain **60s/120s**. Maximum observed browse
sample is 78.122880s, with no retained failures. Individual request maxima
are not retained. The live CronJob supplies no timeout, budget, sample or
active-deadline environment override and no envFrom. Thus wrapper defaults
derive **1500s measurement wall** (1800 minus 300) and **1410s inner engine
budget** (1500 minus 90). The actual Kubernetes Job deadline is separately
**5400s**, not the wrapper's 1800s default input. Measure 607s/exit 0 did
not hit the measurement wall; that wall does not bound the entire wrapper.

Guard exit 3 and four browse breach series persist despite 0/36 errors.
Complete/exit 0 is not a passing guard. Do not raise limits or duplicate
the error-aware ratchet. HTTP outcomes do not establish playable stream
bytes, response-body validity or cold materialisation correctness.

## One Nonduplicate Fix

**ttfb-loadtime-page-parse-residual** was already filed by this task's prior
attempt; reuse it, do not file another. `_full_list_sample` in
`tools/rig-scenarios/47-loadtime-flows.sh:831-833` launches three Python
parsers for each successful page (count, TotalRecordCount, IDs): 249
parsers per movie sample and 147 per Shows-root sample. Parse each page
once and emit all three results. Preserve ordered IDs, counts, pagination,
failure semantics, batch sizes, deadlines, selection and ratchet policy.
This reduces a demonstrated source of harness overhead, not server work;
the eventual savings must be measured, not equated to the entire residual.

The fix is distinct from stage instrumentation, server DB batching, target
selection, failure attribution, OIDC and ratchet work. Pagination and badge
network work dominate, but their server optimization is already separately
owned. No second fix is enqueued by this recovery pass.

Registered integration Check: `./scripts/tests/p8-loadtime-daily-workflow.test.sh`.
Drive the real LIVE engine, wrapper and publisher over local multi-page
HTTP for BOTH roots and BOTH list/sort flows. Assert one parser launch per
successfully fetched page pathname (red before: three), exact ordered IDs
in badge payloads, exact item/DOM totals and reconciled stage sums/counts.
Cover late page failure and preserve errors/deadlines. A global launch count
alone can conceal per-page omissions/duplicates. Existing review feedback
on the fix specifically requires these stronger ID/count/per-page checks;
no implementation or future integration-test pass is claimed here.

## Independent Successor

**ttfb-reduction-033** was also already filed: no dependencies, not before
**2026-11-16T05:40:00Z**, about +24h from the original day 032 diagnosis.
Inspect manifests newer than 1794658629; bind flow/daily pushes before
comparing durations AND errors against all five batches. Compare stage
components where available; older batches have none. Watch residual after
the parsing fix lands, without attributing all residual to parsing. Keep
failed discovery out of denominators, track the backend/edge discontinuity,
and check all four bounds separately. Append one nonduplicate fix with a
real integration Check and independent day 034. Neither day 032 nor its
fix blocks day 033.

Analytical documentation justifies check=none. Exact read-only evidence
and queue-recovery records are in the private hive change doc. No operator
steps required.
