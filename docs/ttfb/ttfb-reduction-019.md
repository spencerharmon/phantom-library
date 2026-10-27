# TTFB Reduction 019

Analysis on 2026-10-27, after reading `ttfb-reduction-018.md` first.
Diagnosis/enqueue only; no runtime, deployment or threshold changes.

## Measurement and Comparison

Mimir query succeeded at 2026-10-27T03:27:30Z (1793071650.801).
Flow push VALUE 1793013436.0143259 is October 26 11:17:16Z, 16.1708
hours earlier: evaluation time is not measurement time. All twelve series
have runs_total=3 and errors_total=0; both synthetic playback outcomes are
success=1. These remain median-of-three synthetic full-flow wall times,
including pagination, badge requests and local DOM-helper work, not isolated
server TTFB or browser percentiles.

| Flow | Episode Seconds | Movie Seconds |
| --- | ---: | ---: |
| list_load | 0.290105 | 0.135362 |
| sort_change | 0.188389 | 0.146568 |
| info_open | 0.042260 | 0.039589 |
| get_sources | 0.034553 | 0.029086 |
| materialise | 0.035893 | 0.028458 |
| play_materialised | 0.029275 | 0.030760 |

Against 018, episode list decreased 0.019701s (6.3591%) and sort decreased
0.004752s (2.4604%). The 017 single samples were 0.193686s / 0.298508s;
018 medians were 0.309806s / 0.193141s. Across that estimator change the
observed movement was +59.9527% / -35.2979%, without proving a plugin change
or isolating how much movement came from the estimator versus daily variation.
019 versus 018 uses the same estimator, but two daily medians still do not
establish a causal runtime improvement or statistical significance.

Movie list increased 0.002954s (2.2310%), sort 0.013264s (9.9502%), and
info-open 0.001985s (5.2787%) against 018's 0.132408/0.133304/0.037604s.
List remains close; sort movement deserves follow-up rather than an
unqualified stability claim. No new broad order-of-magnitude slowdown is
visible, but a threshold breach is present and must not be hidden.

## Runtime Identity

Dev routing still selects the same Ready pod, created October 22 08:02:54Z,
process started 08:03:07Z, zero restarts, unchanged image digest. Installed
DLL strings still identify version 0.5.7.7 and revision
`d9a58ece9e7111f86663efe0c608147c099205a3`; modification time remains
07:22:12Z and change time 08:03:07.675780233Z. This corroborates the prior
loaded-build attribution, not a fresh in-memory MVID inspection.
`phantom_runtime_info` returns success with an empty vector.
Exact infrastructure identifiers and read-only commands are in the hive doc.

The measurement Job still clones main and logs only its ref. Its pods have
been removed; the exact daily checkout SHA and chosen item IDs cannot be
recovered from these retained metrics. Do not equate analysis HEAD
`6915de44b7e7a9dfe9e514764907ef3f08976d0e` with that checkout or loaded DLL.

## Daily Health and Durable State

The October 26 daily Job started 11:17:00Z and completed 11:17:23Z,
succeeded=1. CronJob lastSuccessfulTime now agrees, replacing October 24's
last success. It remains unsuspended, Forbid, schedule `17 6 * * *`, with
timeZone unset. This is Kubernetes completion, NOT an all-clear latency guard.

| Retained Stage | Exit Code | Seconds |
| --- | ---: | ---: |
| measure | 0 | 4 |
| push | 0 | 1 |
| guard | 3 | 4 |

Run-start VALUE 1793013431 is October 26 11:17:11Z; stage push VALUE
1793013440.1775858 is 11:17:20Z. These are one day newer than 018, bracketed
by the latest daily Job, and all three exit/duration pairs survive pod GC.
The wrapper accepts guard=3 as a completed run (`loadtime-daily-run.sh:278-281`)
but rejects tool errors. The former guard=2 configuration failure is gone;
a breach is now recorded. The retained stage metric does not name which
scenario breached, its ceiling, or whether filing occurred: the wrapper
can pass --no-file when beehive is unavailable. No deleted log was recovered.

`flux:flux-phantom-loadtime-durable-thresholds` and its post-merge verifier
are DONE. More importantly, live shared HelmRelease is Ready on chart 2.12.0
since October 26 04:36:01Z. The claim is Bound, and BOTH the current CronJob
and actual latest Job mount it outside the disposable source checkout and
set PHANTOM_LOADTIME_THRESHOLDS_PATH to the durable JSON file.

The post-merge change doc records actual two-Job continuity at 04:36-04:37Z:
source `d0f7df86b69131aa171f284f7f3074db9867079a`, first run 12 seeds, second
run 12 ratchets / zero seeds. First final and second initial SHA256 match:
`2cb53d58afeb08fdd0c364c0e2972294e63dad50b40f88c096e68b4ab0835e1d`.
Both Jobs remain recorded as completed. This is prior live verification
evidence, not a test rerun by 019. Together with the latest Job's actual
wiring and guard result it establishes activation beyond source/chart merge.
It does not prove node-loss durability, today's registry bytes, or which
scenario breached. Do not reseed or loosen ceilings to manufacture green.

## One Concrete Fix

Filed `ttfb-loadtime-episode-target-discovery`, weight 8, no dependencies.
The 018 fidelity concern remains: `47-loadtime-flows.sh:491-494` treats the
first series child as EPISODE_ID and falls back to the series. Channel source
routes Series -> Season -> Episode (`PhantomShowsChannel.cs:263-269`), and
containers have no playable media source. Discovery also omits resolved
userId: an empty-user root can instead return latest episodes (227-230).
Thus a particular historical wrong type cannot be asserted, but discovery
does not validate its measurement target under either route.

The fix must explicitly traverse and type-check both folder hops with
userId, remove container fallback, fail closed before batch emission on
HTTP/JSON/empty/wrong-type results, and log selected IDs/types safely. Install
temporary-file cleanup before fallible discovery. Extend the existing
`scripts/tests/p8-loadtime-flows.test.sh` to drive the actual LIVE engine
branch with a controlled curl stub, asserting all four episode-specific
endpoint targets, negative cases, movie parity and unchanged median/error
accounting. Require fail-before/pass-after; DRYRUN-only tests cannot prove it.
Check is `./scripts/tests/p8-loadtime-flows.test.sh`, approved by CHECKS.md
script-test; also run the existing push regression suite.

This narrow fix does not silently redefine list/sort: those still request
the Shows channel root without a season folder or explicit user context,
not proven interactive episode browsing. Nor does it turn curl-only
synthetic outcomes into validated playable response bodies. Those limits
remain visible for subsequent diagnosis, not additional fixes filed today.

## Successor and Scope

Filed independent `ttfb-reduction-020`, weight 8, no deps, check=none,
not_before=2026-10-28T03:35:00Z (approximately +24h). Read this report first;
track movie sort movement, actual runtime/measurement identities, guard
breaches and durable-state continuity, and discovery repair if landed.
Enqueue one concrete fix and independent +24h 021 regardless of fix status.

No automated behavioral regression test honestly applies to this analytical
documentation task. Read-only evidence and computed comparisons are in the
hive change doc. No deployment, remeasurement, registry mutation, ROI edit,
gitlink update or operator action was required.
