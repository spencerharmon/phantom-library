# playback-error-reduction — day 6 analysis

Series doc: `docs/tasks/playback-error-reduction-001.md`. Prior day: day 5
(`docs/tasks/playback-error-reduction-005-analysis.md` was not committed by
that pass; its findings are recorded in its change doc,
`submodules/phantom-library/docs/bee-playback-error-reduction-005-playback-
error-reduction-005.md`, which this pass treats as the day-5 baseline of
record).

## Baseline (P5 discipline — measure first)

No real, deployed, cause-labelled `phantom_playback_outcome_total` series is
queryable yet in prod: `playback-outcome-instrumentation-001` has not been
confirmed deployed-and-emitting-a-day-of-data. Re-baselining from the coarse
P8 rig signals (`errors_total`, `phantom_availability_probes_total`) as days
1-5 did — this remains a coarse proxy, not the real cause-labelled
`playback_error_rate` split by flow x item_type x cause the series ultimately
wants. This gap has persisted unchanged for five consecutive daily passes
(days 2-6); it is recorded again rather than fabricated.

## Dial / follow-up status check

Read directly from `PLAN.md` (`submodules/phantom-library/PLAN.md`):

- **Dial #1** `availability-probe-reconcile-001` — `DONE`.
- **Dial #2** `browse-prune-dead-swarm-001` — `DONE`.
- **Day-4 follow-up** `availability-stale-candidate-reprobe-001` — `DONE`
  (dotnet-unit + script-test structural harness only; its own change doc left
  the live-rig cold-materialise verification to a follow-up).
- **Day-5 follow-up** `availability-stale-candidate-reprobe-verify-001` —
  `NEEDS-HUMAN` (`external-permission`): the live-rig verification script it
  authored cannot run because the host's pre-seeded rig DB clone
  (`/var/tmp/jf-test/data/data/jellyfin.db`) is 0-byte/corrupted, and
  `docs/agents/testing.md` explicitly assigns refreshing that seed to the
  operator, not a test run. This is a genuine `external-permission` escalation
  (the swarm is forbidden from cloning live prod state itself to reseed it) —
  correctly left standing, not re-filed as a buildable prerequisite.

So both primary candidate/probe-side dials are landed, and the day-4/day-5
follow-up chain has run its course short of an operator-gated rig reseed. The
candidate/probe side of the funnel is, for now, exhausted of swarm-actionable
work.

## Ranking the dominant remaining actionable cause

With the candidate/probe side gated on the operator, the next concrete,
code-visible, actionable gap is a retry ASYMMETRY in the gostream handoff
itself: `GostreamClient.AddAsync` already retries its register HTTP call once
on a transient failure (`PostWithOneRetryAsync`, 1s backoff on 5xx/429), but
`PhantomMaterialisingMediaSourceProvider.WaitForFileAsync` — which polls for
the FUSE path gostream is expected to expose after a successful register —
has ZERO retry: a poll-timeout throws `FileNotFoundException` straight
through, classified terminal `gostream_cannot_fetch`, one step later in the
very same handoff that just demonstrated a one-retry discipline is warranted.
This is ranked ahead of jumping further into `first_byte_timeout` or
`plugin_host_error` (both still gated behind a real deployed series showing
they dominate, per the series' own discipline from days 1-5) because it is a
concrete, already-identified code asymmetry rather than a speculative next
guess.

## Fix enqueued

`gostream-fuse-wait-eager-reregister-001` — on `WaitForFileAsync`'s
poll-timeout, issue exactly one eager re-register call to gostream for the
same item and, if it succeeds, extend the wait by one additional bounded poll
window (reusing the existing `FusePathWaitTimeoutSeconds`/
`FusePathPollIntervalMilliseconds` config) before finally raising
`FileNotFoundException`. No stacked retries, no unbounded/exponential
backoff. Movie and episode parity required. This task landed and reached
`DONE` (commit `474da0cf6312024719e2b701fcde07cc6386ff51`) with a dotnet
regression asserting both item_types.

## Successor

`playback-error-reduction-007` appended `[TODO]` held on `not_before ~=
+24h`, re-checking (among other things) whether the operator has resolved
`availability-stale-candidate-reprobe-verify-001`'s rig-seed blocker.

## Notes

- This day-6 pass had an earlier interrupted attempt (visible in `PLAN.md`
  history / `origin/bee-playback-error-reduction-006`) that reached the same
  ranking and enqueued the same fix + successor; those artifacts (the fix
  task, its implementation, and the appended day-7 task) are already
  committed to tracked `main` and are NOT re-appended here. This completing
  pass supplies only the analysis doc that attempt never actually committed
  to the submodule repo, plus the change doc and status flip.
