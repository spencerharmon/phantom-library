# TTFB Reduction 023

Diagnosis on 2026-10-31 after reading day 022 first. Diagnose-and-enqueue only:
no runtime, deployment, workload, threshold, or source-behavior changes.
This report supersedes the prior attempt's stale "fix not loaded" conclusion.

## New Retained Batch

Evaluation 1793422252.331 is query time, NOT measurement time. Run-start VALUE
1793359027 = October 30 11:17:07Z, newer than day 022's 1793272624 and
17.563 hours old at evaluation. Flow/stage push VALUEs are
1793361169.4467623 / 1793361174.3147707 (11:52:49.447Z / 11:52:54.315Z).
The retained Job ran 11:17:00Z-11:52:58Z. No new measurement was triggered.

All tuples have runs_total=3. Seconds are median-of-three wall durations,
INCLUDING failures, not successful-request percentiles or isolated TTFB.
The episode list/sort label measures the Shows root, not episode navigation.

| Flow | Episode seconds | Errors | Movie seconds | Errors |
| --- | ---: | ---: | ---: | ---: |
| list_load | 343.661371 | 2 | 10.118952 | 3 |
| sort_change | 36.425254 | 3 | 30.393210 | 3 |
| info_open | 0.048825 | 3 | 0.041445 | 0 |
| get_sources | 0.030021 | 3 | 0.029544 | 0 |
| materialise | 0.029920 | 3 | 0.031143 | 0 |
| play_materialised | 0.032538 | 3 | 0.031667 | 0 |

Movie list 150.896953s -> 10.118952s is faster failure, NOT an improvement:
errors rose from two to three. Episode list worsened 1.423x. Episode/movie
sort ratios are 0.788x/0.933x, but every sample failed. Fast episode item
failures are not wins; movie curl success does not validate sources or media.

## Recovered Provenance And Failures

A temporary probe mounted the existing evidence volume READ-ONLY and recovered
attempt 3711762600cf4b0f9e750a905b2d35a0, status complete, exit_code=0,
checkout_sha=1eef6d11a8f39457288d21a8d9c33482343c2b74. Discovery succeeded
in 173s. This is the measurement driver's checkout, NOT the loaded plugin.
Typed Movie/Series/Season/Episode targets and 23 sanitized failures survive;
exact identifiers and commands are confined to the private hive change doc.

All failures have curl_exit_code=22. Eleven page_fetch HTTP 500s: movie list
samples 1/2/3, episode list samples 2/3, and all sort samples of both types.
All twelve episode item-flow attempts are HTTP 404, stage=request, against
the same discovered Episode target as day 022. Synthetic
cause=gostream_register_fail is therefore not evidence of a registration fault.
The manifest does not prove what deleted or invalidated that target.

Measure/push/guard exit-duration pairs are 0/2142s, 0/0s, 3/5s. Both retained
push_failure_time_seconds values are zero. Successful orchestration accepts
flow errors and guard exit 3; zero push seconds is integer-resolution timing.
All twelve breach flags are 1; observed values match the duration table.

| Flow | Episode ceiling | Movie ceiling |
| --- | ---: | ---: |
| list_load | 0.201845 | 0.145203 |
| sort_change | 0.197808 | 0.136298 |
| info_open | 0.036792 | 0.038513 |
| get_sources | 0.028156 | 0.028538 |
| materialise | 0.026654 | 0.029027 |
| play_materialised | 0.027743 | 0.028798 |

Ceilings are unchanged from day 022. List/sort thresholds predate the corrected
user-context workload. Do not reseed or loosen them to hide failures.

## Server Evidence And Retention Limit

The prior attempt captured the October 30 daily log BEFORE the dev pod was
replaced: window 11:17:00-11:53:00 UTC, 25,544 timestamped lines, 24,987
removal messages (19,499 Movie, 5,460 Series, 26 Episode, 2 Folder), fourteen
request errors, warmup tick failure at 11:28:12.804. These are historical
transcript evidence, not fresh counts or distinct deleted rows. The window
ends two seconds after the Job. Its shape is consistent with day 022 churn.

The prior probe discarded non-timestamped exception continuation lines BEFORE
counting SQLSTATE strings. Its zero 40P01/23503 counts cannot exclude deadlock
or FK failures. This pass's direct read of the old daily log failed because
the file no longer exists in the replacement pod; it did NOT observe zero
errors. No fresh SQLSTATE/stack attribution is possible from that file.

## Fix Is Now Loaded, Improvement Is Not Yet Measured

Historical batch-era evidence still identifies the old 0.5.7.7 / d9a58ece
plugin. The dev pod has since been replaced: created October 31 04:18:27Z,
container started 04:18:47Z, Ready, zero restarts. Its startup log at
04:18:49.993Z reports loaded assembly informational version
0.5.7.8+885873935b9062a70501981e3a780f784fe2a240, MVID
5edbe4a8-c25d-4425-83b6-5ab9b20f4779. phantom_runtime_info now returns 1
with the same source/version/MVID. The installed DLL hash matches the build
receipt. An ancestry check confirms that revision contains warmup fix
80a119a51b2aad10e3398959431d8603ee423786.

This is loaded-runtime evidence, stronger than the earlier on-disk DLL scan.
It is AFTER the retained batch: neither this batch nor pod readiness measures
the fix's latency/removal effect. The deployment repair's receipt also records
an external health redirect failure and an outstanding acceptance-rig gap;
loaded does not mean end-to-end CD acceptance succeeded.

## One Fix And Independent Successor

Preserve **ttfb-warmup-fix-dev-load**, already appended by the prior attempt.
It is distinct from the warmup code fix: it closes delivery to dev. Its
investigation led to **actions:phantom-cd-plugin-ref-build-repair**; the
runtime evidence above now supersedes its initial stale-load diagnosis.
Do not enqueue another deployment fix or implement one in this analytical pass.

Preserve **ttfb-reduction-024**, already appended independently with empty deps,
weight 8, check=none, not_before=2026-11-01T03:45:00Z (roughly +24h).
Both append retries refused duplicates. Read this report first; inspect a
batch measured AFTER the new process start, recover its manifest and server
exception context before pod replacement, and compare error counts as well
as durations. Do not attribute old failures to the newly loaded fix.

No behavioral regression test honestly applies to this analytical report.
Read-only evidence commands and retention limitations are in the hive change
doc. No operator action is required by this pass.
