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
It controls offline age checks only, not the evaluation times in the input files.
Any one input may be `-` to read stdin. All others must be local files.

The caller supplies three successful Prometheus API **vector** responses,
queried at the same explicit evaluation time and with the same scope selector:
send the same `time=<evaluation_timestamp>` parameter to `/api/v1/query` for
all three requests, including `max_over_time(...)`. Do not let each request
independently default to "now"; differing outer sample timestamps are rejected.

- `--instant`: `phantom_playback_outcome_total{...}`. Raw cumulative counters,
  never a sum, rate, increase, synthetic rig family, or runtime-info gauge.
- `--window`: `max_over_time(phantom_playback_outcome_total{...}[30d])` (or the
  caller's chosen window). Continuity/aging evidence only: maxima are not attempts in the window,
  not reset-adjusted deltas, and never added to current totals.
  This function drops `__name__`; raw output is accepted without `label_replace`.
- `--timestamps`: `timestamp(phantom_playback_outcome_total{...})`. Values are
  actual source sample times; this function drops `__name__`. The API's outer
  sample timestamp is query evaluation time and **does not prove freshness**.
  This companion is mandatory, with exactly the same identities/tuples as the
  instant result. There is deliberately no fallback to evaluation timestamps.

For `--window` and `--timestamps`, a missing `__name__` is treated as the outcome
family. A present name must still be exactly `phantom_playback_outcome_total`;
`--instant` always requires that name. The caller must use the outcome queries
above: once a function drops the name, the analyser cannot prove the source
family from labels alone.

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
the caller's CURRENT routing role, not historical traffic attribution. A promotion
without a process restart must not relabel prior dev counters as organic serving.

### Historical attribution receipts

Rankings additionally require optional `--attribution attribution.json`. Omitting
this input (or supplying `[]`) remains valid: healthy counts and ratios are printed
as **unattributed observations**, but cumulative/interval aggregate and all four
slice rankings are withheld. `status=OK` and exit 0 describe valid observations,
not proven organic traffic. Current role, loaded build, matching endpoint roles,
volume, coherent timestamps and monotonic counters never establish role history.

The caller must independently establish organic provenance and uninterrupted
serving throughout the **entire population** attested. These are caller-supplied
receipts, not evidence inferred or verified by the analyser against live routing.
Do not manufacture them from current `role` or the two endpoint roles: an
intervening dev/serving flip invalidates continuous organic attribution.

```json
[
  {
    "labels": {"instance": "serving.example.com", "job": "playback"},
    "scope": "cumulative",
    "attribution": "organic-serving",
    "source_end": "990"
  },
  {
    "labels": {"instance": "serving.example.com", "job": "playback"},
    "scope": "interval",
    "attribution": "organic-serving",
    "source_start": "590",
    "source_end": "990"
  }
]
```

- `labels` must exactly match an expected identity, including job and every other
  identity label. There is at most one receipt per identity/scope; peers cannot
  lend authorization to one another.
- `scope=cumulative` asserts ALL counts in that identity's snapshot, across all
  32 tuples, from the actual counter population's origin through `source_end`.
  No `source_start` is allowed: attesting only a recent span is not lifetime
  evidence. The caller must know the population's origin; the tool does not infer
  it from evaluation time, exporter identity, a baseline or a window maximum.
- `scope=interval` asserts the whole current-minus-previous population. Both
  `source_start` and `source_end` must equal that identity's actual previous and
  current source sample times, NOT evaluation times. They are finite nonnegative
  timestamps, with start strictly earlier than end. Partial or larger enclosing
  spans do not match. Requires the existing previous capture pair to produce an
  interval report; otherwise the receipt is unused.
- The scopes are independent. An interval-only receipt NEVER enables cumulative
  rankings; a cumulative-only receipt does not substitute for an interval receipt.
- `attribution` is `organic-serving`, `unknown`, `non-serving` (the entire
  population is non-serving), or `mixed` (contains both serving and non-serving).
  Only `organic-serving` with a matching source span and current serving role
  authorizes rankings. A promotion after the samples cannot authorize history;
  a currently inactive identity is never ranked, even with historical receipts.
- Missing, unknown, non-serving, mixed, expired or partial/mismatched receipts
  print an explicit `cumulative attribution=unattributed` or
  `interval attribution=unattributed` diagnostic. Valid numbers are retained,
  never described as organic or improvement. Missing attribution alone does not
  change exit status. Old no-receipt invocations remain usable but never rank.
- Malformed fields, extra fields, invalid enums/numbers, unknown/partial identity
  labels, duplicate receipts and contradictory assertions are input errors (1).
  For example, an all-organic cumulative population cannot contain a non-serving
  or mixed interval ending at or before its endpoint; an entirely non-serving
  cumulative population cannot contain an organic or mixed such interval.
  A mixed cumulative population CAN contain an entirely organic interval.

For the example above, use current evaluation/source `1000/990` and previous
`600/590`, and set `--now 1000`. Add `--attribution attribution.json` and the
existing `--previous-instant previous.json --previous-timestamps previous-times.json`
flags to the invocation. With cold-movie success/timeout `900/100` versus `400/50`,
the cumulative observations remain `1000` attempts, `100` errors, `10.000000%`;
interval observations remain `550`, `50`, `9.090909%`. Without receipts neither
timeout ranking is emitted; interval-only evidence enables only the `50` ranking.

All coverage, source-coherence, freshness (including previous-endpoint freshness)
and continuity guards still apply before receipt authorization. Each aggregate
and each cold/warm movie/episode slice must independently exceed `--min-attempts`.
Attribution never resets counters, adjusts totals, deploys, contacts a network,
or discovers infrastructure. Receipts share the single-input stdin option and
are read-only like all other inputs.

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

For each exact identity + flow + item_type + cause present in both current and
window, a numerically greater `window_max` than `current` reports
`COUNTER_DISCONTINUITY` with the tuple and both values. This fails the report
(exit 2) for serving and inactive identities alike, for success and every failure
cause across cold/warm movie/episode slices. The affected identity's aggregate
and all slice rates, totals and rankings are unavailable; unaffected identities
remain independently gated. Coverage, freshness, source-coherence and window-only
aging diagnostics remain visible when several conditions fail together.

This is evidence only that **counter continuity is not established**: possible
reset/reseed, identity reuse, or capture inconsistency. It does not identify a
reset cause, time or count, or establish an improvement. Maxima never replace or
augment current totals, and no reset-adjusted rate is computed. Equal or lower
maxima do not trigger the guard. Partial and empty windows remain valid: missing
historical matches are neither errors nor proof of continuity. `OK` means no
guard detected a problem in the supplied evidence, not proven lifetime continuity.

Exit codes: **0** valid report (including complete fresh zero counters and an
absent inactive instance); **1** invalid input; **2** instrumentation/coverage
or freshness/coherence/continuity failure (also argparse usage errors). A serving instance absent
from the instant vector is `UNINSTRUMENTED` even if it exists in the window.
Any present but incomplete/stale/incoherent/discontinuous instance also fails the report. Inactive
series are shown separately and never unlock a serving instance's ranking.

Rates are cumulative **errors / attempts**, with every non-success cause
included as an error. They are not time rates, window increases, evidence of
improvement, or complete accounting of uninstrumented requests. No reset or
cross-capture delta is inferred without explicit previous capture inputs. A ranking
is per serving identity with a matching whole-population attribution receipt, only when
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
