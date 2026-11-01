# TTFB Reduction 024

Diagnosis on 2026-11-01 after reading day 023 first. Diagnose-and-enqueue only:
no runtime, deployment, workload, threshold, or source-behavior changes.

## No Newer Retained Batch: Oct 31 Run Hit The Job Deadline

Retained Pushgateway metrics are UNCHANGED from day 023: run-start 1793359027
(Oct 30 11:17:07Z), measure/push/guard 0/2142s, 0/0s, 3/5s, same twelve
durations, error counts, ceilings and breaches. Nothing newer was pushed.

The Oct 31 daily Job (`phantom-library-loadtime-daily-29890757`) started
11:17:00Z and FAILED with `DeadlineExceeded` at 12:47:00Z
(activeDeadlineSeconds=5400, backoffLimit=1). Its pod is already gone, so the
Job's own stdout is unrecoverable.

Durable evidence (read-only probe): only a `.partial.json` manifest exists,
attempt 8f6d741c7d85426483e36f78af33cc1a, run_start 1793445423,
checkout_sha 344b9044 (rig code), status `running`, discovery exit 0 in 18s
(day 023: 173s), same four discovered targets, `failures: []`, NO further
stages. The measure stage therefore ran > 5,380s without completing and
without flushing any per-sample evidence.

## Fix Is Loaded And Removes The Churn

Dev green pod (process start 2026-10-31T06:58:38Z) loads 0.5.7.11+344b9044,
which contains warmup fix 80a119a (ttfb-warmup-fix-dev-load, DONE). The
Oct 31 batch is the FIRST to run against the loaded fix.

Server log captured for 11:17:00-12:48:00Z before rotation:
1,355 timestamped lines; 31 removal messages (27 Episode, 2 Movie,
2 Series) versus 24,987 in the Oct 30 window (~800x fewer). Every 10-minute
warmup tick SUCCEEDED ("wrapped 25038-25041 root items"); day 023 recorded a
warmup tick failure. Zero "Error processing request" lines (day 023: 14).
Remaining errors: 3 EpisodeMetadataService "Probe Provider" errors with 2
EF Core save failures (11:19-11:20), and steady background indexer noise
(Torrentio 429s, Prowlarr 30s timeouts) from availability/magnet workers.

The fix eliminated the root-churn and the page_fetch 500s' likely cause, but
latency is UNMEASURED: the run produced no numbers.

## Diagnosis: Unbounded Rig Requests Turn Slow Success Into No Data

`tools/rig-scenarios/47-loadtime-flows.sh` `api()`/`json_post()` call curl
with NO `--max-time`. `_full_list_sample` paginates the FULL uncapped channel
(~25k roots, Limit=200) and fans out badge-state POSTs per sample, three
samples per list/sort flow. Before the fix, list/sort pages failed FAST with
500 and broke the loop; with churn gone the pagination now runs to the end,
and any slow or hung page has no per-request bound. Only the 5400s Job
deadline stops it, killing the whole batch, so no partial measurement,
failure attribution or push survives. The manifest records discovery only.

## One Fix And Independent Successor

Enqueued **ttfb-loadtime-request-time-bound**: bound every rig request
(`--max-time`) and each list sample's wall clock so slow/hung pages become
attributed http 000/timeout failures, and flush per-flow progress into the
durable manifest, so the measure stage always completes inside the Job
deadline and pushes a batch. Distinct from the warmup and deploy fixes.
Thresholds must not be loosened.

Enqueued **ttfb-reduction-025** (~+24h, empty deps, check=none): read this
report first; check whether Nov 1 produced a batch; if again DeadlineExceeded,
capture the Job pod log WHILE running and the server log before rotation.
