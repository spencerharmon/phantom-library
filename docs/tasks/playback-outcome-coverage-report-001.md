# playback-outcome-coverage-report-001

## Problem

Seventeen daily passes have hand-assembled the playback baseline from ad hoc
PromQL: tuple counts, exact instance/job scope, sample freshness, zero
denominators and window aging. Day 17 again found 32 fresh all-zero tuples from
one exporter instance attributed to the inactive/dev colour, while the serving
production colour runs an older image that emits no
`phantom_playback_outcome_total` series at all. Nothing machine-distinguishes
"0 errors / 0 attempts" (undefined) from "0% error rate", nor "serving colour
emits nothing" from "no traffic". Window aging (old rig failures leaving the
30d window) has repeatedly needed manual explanation.

## Design

Add `scripts/phantom-playback-outcome-coverage.sh`, an offline, read-only
analyser over Prometheus HTTP API JSON (instant vector, and a
`max_over_time[<window>]` vector) plus an operator-supplied expected-instance
list with a role per instance (serving / inactive). No hard-coded hosts, jobs
or instance ids (neutral placeholders in tests only); inputs by file/stdin.

Per instance it reports: tuple coverage against the canonical product
(2 flows x 2 item types x 8 causes, exact label equality, duplicates and
unknown causes rejected), sample age vs a max-age, attempts (sum incl.
success), errors (non-success), and rate = errors/attempts ONLY when
attempts > 0, else the literal `undefined (no observed attempts)`. An expected
serving instance with no series is reported `UNINSTRUMENTED` and makes the
exit status non-zero (2) so a daily pass cannot read it as zero errors.
Window-only nonzero series absent from the instant vector are reported as
`aged-out/stale`, never as improvement. Cause ranking is printed only when
attributed attempts exceed a configurable minimum; otherwise ranking is
suppressed (`no organic traffic yet observed` for zeros, `insufficient
attributed volume` for a positive count below or equal to the threshold).

No network transport inside the script (callers fetch JSON), no counter
resets, no deployment. Do not duplicate the zero-seed Check, runtime-info gauge
or loadtime-push validators; never count `phantom_runtime_info` as volume.

Invariant: a zero denominator is never rendered as a 0% rate and a serving
instance with no outcome series always fails the report.

## Acceptance

Check (CHECKS.md `script-test`):
`./scripts/tests/phantom-playback-outcome-coverage.test.sh` (committed
executable). Fixtures must cover: complete fresh zeros (undefined rate, exit
0), missing serving instance (exit 2, UNINSTRUMENTED), incomplete/duplicate
tuples, stale samples, window-only aged-out failures, positive volume with
rate and ranking above the minimum, and below-minimum suppression; movie and
episode parity. Each assertion must fail against a stub that prints 0%.

## Offline invocation and input contract

Requires Bash and Python 3 (standard library only). The script reads inputs and
writes its report to stdout, diagnostics to stderr; it never contacts a server,
deploys, changes counters, or writes input files. No plugin install is needed.

```sh
./scripts/phantom-playback-outcome-coverage.sh \
  --instant instant.json --window window.json --timestamps timestamps.json \
  --expected expected.json --now 1700000000 --max-age 120 --min-attempts 100
```

For this task branch in a beehive checkout:

```sh
beehive submodule worktree exec phantom-library bee-playback-outcome-coverage-report-001 -- ./scripts/tests/phantom-playback-outcome-coverage.test.sh
```

`--now` defaults to the current Unix time. Set it to the capture time when
analysing archived evidence; a historical report does not establish live health.
Any one input may be `-` to read stdin. All others must be local files.

The caller supplies three successful Prometheus API **vector** responses,
queried at the same explicit evaluation time and with the same scope selector:

- `--instant`: `phantom_playback_outcome_total{...}`. Raw cumulative counters,
  never a sum, rate, increase, synthetic rig family, or runtime-info gauge.
- `--window`: `max_over_time(phantom_playback_outcome_total{...}[30d])` (or the
  caller's chosen window). Context only: maxima are not attempts in the window,
  not reset-adjusted deltas, and never added to current totals.
- `--timestamps`: `timestamp(phantom_playback_outcome_total{...})`. Values are
  actual source sample times; this function drops `__name__`. The API's outer
  sample timestamp is query evaluation time and **does not prove freshness**.
  This companion is mandatory, with exactly the same identities/tuples as the
  instant result. There is deliberately no fallback to evaluation timestamps.

Example `expected.json`:

```json
[
  {"labels": {"instance": "serving.example.com", "job": "playback"}, "role": "serving"},
  {"labels": {"instance": "inactive.example.com", "job": "playback"}, "role": "inactive"}
]
```

Each identity is the **exact full set** of exported labels other than
`__name__`, `flow`, `item_type`, and `cause`. Supply any resource/namespace/job
labels that actually exist in the capture; labels are not guessed, normalized,
partially matched, or stripped. This prevents mixing jobs or exporters with
similar instance names. Every captured identity (including historical ones)
must be listed. The list must contain at least one serving identity. Role is
operator-supplied attribution, not something telemetry can infer.

Canonical tuples are the exact Cartesian product of:

- `flow`: `materialise_then_play`, `play_already_materialised`.
- `item_type`: `movie`, `episode`.
- `cause`: `success`, `availability_abstain`, `no_candidate`, `magnet_dead_stale`,
  `gostream_register_fail`, `gostream_cannot_fetch`, `first_byte_timeout`,
  `plugin_host_error`.

Each present instance requires all 32 instant tuples exactly once. Partial
window coverage is allowed because historical series can age out independently.
Duplicates, unknown vocabulary, extra/unlisted identity labels, malformed API
responses, API warnings/infos, non-finite/negative/fractional counters, future
timestamps, and mismatched captures fail closed. Source ages equal to max-age
are accepted; older samples withhold the entire instance's rate and ranking.
Missing/partial data is unavailable, never an assumed zero denominator.

Exit codes: **0** valid report (including complete fresh zero counters and an
absent inactive instance); **1** invalid input; **2** instrumentation/coverage
or freshness failure (also argparse usage errors). A serving instance absent
from the instant vector is `UNINSTRUMENTED` even if it exists in the window.
Any present but incomplete/stale instance also fails the report. Inactive
series are shown separately and never unlock a serving instance's ranking.

Rates are cumulative **errors / attempts**, with every non-success cause
included as an error. They are not time rates, window increases, evidence of
improvement, or complete accounting of uninstrumented requests. No reset or
cross-capture delta is inferred. A ranking is per serving identity, only when
fresh complete attempts are strictly greater than `--min-attempts` (default
100); positive non-success causes are sorted by count descending, then name.
Below-threshold positive counts remain visible without claiming no traffic.
Window-only nonzero tuples are `aged-out/stale`, excluded from rates/rankings,
and explicitly **not improvement**. This input pair cannot distinguish series
expiration from stopped exporters, nor detect series absent from both captures.

The regression harness exercises this reporting surface, not startup seeding,
runtime provenance, or loadtime-push validation. It covers both flows and item
types, per-instance isolation, strict input handling, stdin, freshness boundaries,
and read-only inputs. Every case also rejects a fake analyser that prints `0%`.
