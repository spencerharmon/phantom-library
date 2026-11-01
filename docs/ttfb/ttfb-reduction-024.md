# TTFB Reduction 024

Diagnosis on 2026-11-01, after reading day 023 first. Diagnose-and-enqueue
only: no runtime, deployment, measurement, threshold or source-behavior edits.
This report completes the prior attempt's diagnosis and preserves its already
filed fix and independent successor rather than duplicating them.

## New Attempt, No New Completed Batch

The October 31 daily Job started at 11:17:00Z and reached its 5400-second
deadline at 12:47:00Z; Failed/DeadlineExceeded was recorded at 12:47:31Z.
Its pod has been removed. A read-only evidence-volume probe recovered one
attempt newer than day 023: run_start=1793445423 (October 31 11:17:03Z),
attempt_id=8f6d741c7d85426483e36f78af33cc1a,
checkout_sha=344b9044369beca516fa0bc7918ce4b3f800df40.

The manifest remains status=running, with discovery exit 0 in 18 seconds,
four typed targets and failures=[]. It has NO completed measure/push/guard
stages or final exit code. Kubernetes proves the attempt is no longer running;
the manifest is incomplete, not successful. Empty failures does not establish
zero request failures: no completed batch survived. The checkout SHA identifies
the driver, not the plugin loaded by the server.

At metric evaluation 1793508911.244, the run-start VALUE is still 1793359027
(October 30 11:17:07Z), about 41.635 hours old. Flow/stage push VALUEs remain
1793361169.4467623 / 1793361174.3147707. Query time is not measurement time.
The retained values below are the SAME day-023 batch, not a new comparison:

| Flow | Episode seconds | Errors | Movie seconds | Errors |
| --- | ---: | ---: | ---: | ---: |
| list_load | 343.661371 | 2 | 10.118952 | 3 |
| sort_change | 36.425254 | 3 | 30.393210 | 3 |
| info_open | 0.048825 | 3 | 0.041445 | 0 |
| get_sources | 0.030021 | 3 | 0.029544 | 0 |
| materialise | 0.029920 | 3 | 0.031143 | 0 |
| play_materialised | 0.032538 | 3 | 0.031667 | 0 |

All tuples have three samples; durations are median wall times INCLUDING
failures, not successful-request percentiles or isolated first-byte latency.
Episode list/sort measures the Shows root. Measure/push/guard still reads
0/2142s, 0/0s, 3/5s; twelve breaches and all ceilings are unchanged. Eleven
list/sort errors retain page_fetch/500 attribution. The fast failed movie
list and episode item flows are not improvements. Historical synthetic
registration labels do not identify a server-side registration failure.

## Server Evidence Before Replacement

The previous attempt captured the October 31 11:17:00-12:48:00 UTC window
before replacement: 1355 timestamped lines, 31 removal messages (27 Episode,
2 Movie, 2 Series), zero matching Error processing request lines, and nine
successful warmup messages from 11:19:29 through 12:39:27. Warmup wrapped
25038-25041 root items. These are transcript evidence, NOT counts rerun here.

Compared with day 023's 24987 removal messages over its shorter window,
this supports sharply reduced removal churn after the warmup fix. It does
NOT prove churn is eliminated, that all requests completed, or that latency
improved. Both summaries count messages, not distinct deleted rows. The
earlier filter selected lines by their own timestamp, dropping exception
continuations; absence of SQLSTATE strings cannot exclude FK/deadlock errors.

This pass attempted to inspect the October 31 file directly: it no longer
exists in the replacement pod. The batch's Job pod is also gone. Therefore
no fresh server exception attribution or exact hung request is recoverable
from those sources. A bounded search of the current November 1 log found no
matching request-error/warmup-failure/40P01/23503 text; this is a different
process/window and is not evidence about the failed October 31 attempt.

## Loaded Fix And Measurement Repair

The prior deployment receipt records October 31's loaded
0.5.7.11+344b9044369beca516fa0bc7918ce4b3f800df40, process start 06:58:38Z,
before that day's attempted measurement. It contains warmup fix 80a119a.
Current startup provenance now reports
0.5.7.12+1041bf673720aa8abd0481b4ad203b64529adfe9, MVID
5499fafc-14bc-44b2-9970-205d0cce25ca, process start November 1 04:21:30.877Z.
The current runtime metric agrees; the pod is Ready with zero restarts.
An ancestry check confirms this revision also contains the warmup fix.
Loaded is established; a completed post-fix latency batch is not.

Preserve **ttfb-loadtime-request-time-bound**, filed by the prior attempt of
this task. It is distinct from the warmup implementation and dev-load fixes:
the old driver waited on unbounded requests/list traversal, and could lose
all measurement and final-stage evidence at the Kubernetes deadline. The
manifest supports incomplete measurement, but cannot prove which request hung.

That repair has since landed as 7e28c4a35c0a9bb61265bdf5ebc0d67e56d32cb8:
60-second request limits, 120-second whole-list budgets, and an outer measure
watchdog reserving 300 seconds for reporting. The failed attempt's checkout
344b904 does NOT contain it. The current worktree does, and its offline
flow/timeout suite passes 17/17; daily workflow passes 237/237. These prove
the repair's fixture behavior, not a new live batch. Plugin startup identity
does not attest which scripts a future daily Job will execute.

The live CronJob still has a 5400-second deadline but lacks the new deadline
environment variable. Until updated, the repaired wrapper's conservative
1800-second default gives a 1500-second measure cap, not 5100 seconds. This
may yield an explicit timeout rather than a complete batch; day 025 must
inspect actual checkout, environment and retained outcome before interpreting it.

## Independent Successor

Preserve **ttfb-reduction-025**, already filed with empty dependencies,
weight 8, check=none, not_before=2026-11-02T03:45:00Z (roughly +24h).
Both append retries refused duplicates. No second fix or successor was added.
Day 025 should read this report first, distinguish new attempts from new
published batches, capture logs WITH continuations before rotation/replacement,
and compare durations AND errors only for completed, correctly identified
batches. Do not repeat the historical successor card's stronger "churn gone"
interpretation or assume a 5400-second Job implies a 5100-second measure cap.

No behavioral regression test applies to the analytical document itself.
Exact read-only commands, historical transcript references, test results and
retention limitations are in the private hive change doc. No operator action
is required by this diagnosis, and no live measurement was triggered.
