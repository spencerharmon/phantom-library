# playback-error-reduction-006 — analysis (day 6 baseline + rank + enqueue)

Series: see `docs/tasks/playback-error-reduction-001.md` (read FIRST), then the
day-4 analysis `docs/tasks/playback-error-reduction-004-analysis.md` (the most
recent analysis doc actually present in the tree — see the "day 5 doc gap"
note below). This is the SIXTH daily analytical pass. Same DIAGNOSE-AND-ENQUEUE
contract as days 1-5: BASELINE the current playback error rate (P5
discipline), RANK the dominant remaining failure cause, and ENQUEUE ONE
concrete fix for the biggest win. It implements NOTHING itself.

## Day-5 doc gap (honest note, not this pass's to fix)

`playback-error-reduction-005` is stamped `DONE` in `PLAN.md`
(`commits=c60e4977705d60360686f124244e050f8129fef7`), and its task card names
`docs/tasks/playback-error-reduction-005-analysis.md` as its analysis doc —
but that file does not exist in the tree, and `git log --all` for that path
finds nothing (the recorded commit is a merge that landed an UNRELATED
task's diff, `ttfb-fast-indexer-early-return-enable-default`, not day 5's
own analysis doc). The day-5 analysis content is effectively lost. This pass
does NOT attempt to reconstruct it retroactively (out of scope, and the
`commits=` tag on a `DONE` task is not this analytical pass's to rewrite) —
it is noted here so a future audit pass can see the gap, and this pass
instead reads FORWARD from day 4's analysis (still intact) plus the two
follow-up tasks day 4 and day 5 are independently known (via their own
task cards) to have enqueued.

## Baseline (P5 discipline: measure BEFORE optimising)

### What has landed since day 4

- **Dial #1** (`availability-probe-reconcile-001`) and **Dial #2**
  (`browse-prune-dead-swarm-001`): unchanged, both `DONE` (see day 4).
- **Day-4 follow-up** `availability-stale-candidate-reprobe-001` — `DONE`
  (`commits=b505b52965aaae9716f1249fa5c98a69543ff092,028761ff9ef11d262741654cfe333486a9050dd6,c1dbabdcc532f3287e4b24435b2d6d32e8da597e`).
  Closed the stale-`'available'`-with-zero-cached-candidates browse gap dial #2
  left open: `ListVisibleMovieRowsAsync`/`ListVisibleSeriesRowsAsync` now
  distinguish "never assessed" from "assessed, cached, then cache emptied"
  and exclude the latter from default browse until `AvailabilityProbeWorker`'s
  eager re-probe (`MarkStaleAvailableItemsDueAsync`) restores a live candidate
  or confirms unavailability. Verified via dotnet-unit + `script-test`
  structural/behavioural harness (`./scripts/tests/availability-stale-candidate-reprobe.test.sh`),
  for both `item_type=movie` and `item_type=episode`.
- **Day-5 follow-up** `availability-stale-candidate-reprobe-verify-001` — the
  live-rig verification of the above, enqueued day 5 because
  `availability-stale-candidate-reprobe-001`'s own card required a live-rig
  cold-materialise confirmation before `DONE` but reached `DONE` on
  structural harness only. This task is now `NEEDS-HUMAN`
  (`category=external-permission`): the rig-seed DB clone
  (`/var/tmp/jf-test/data/data/jellyfin.db`) this host requires is 0-byte/
  corrupted, and `docs/agents/testing.md` explicitly assigns refreshing that
  seed to the operator (not a test run). Everything else in the toolchain
  (submodule init, dotnet build of the patched Jellyfin server + plugin,
  `systemctl --user`) was confirmed working by that session — the seed is the
  sole blocker, and it is a genuine `external-permission` case (host-local
  fixture refresh, explicitly operator-owned by policy), not a
  misclassified buildable prerequisite. This pass does not re-litigate that
  escalation; it is a correct, narrow, already-actionable human gate (see its
  `Steps:` for the exact remediation).
- The measurement dial `playback-outcome-instrumentation-001` (day 1) remains
  `DONE`. The in-cluster acceptance rig (`in-cluster-acceptance-rig`) that
  would prove the deployed-stack path is still `NEEDS-HUMAN` on
  operator-provisioned Gitea Actions runner secrets — unrelated to this
  series but the same root cause (no live cluster-facing query access from a
  honeybee sandbox) that keeps blocking a real deployed-series reading.

### Can day 6 read a real cause-labelled number yet? — Honest gap, unchanged

**Still not yet.** Same substrate gap as days 2-5: the
`phantom_playback_outcome_total{flow,item_type,cause}` metric CONTRACT is
`DONE` and regression-tested, but no PLAN task in this submodule records the
instrumentation as deployed-and-a-day-old against real prod traffic, and this
analytical pass has no `observe.spencerharmon.com` / `mimir.spencerharmon.com`
query access. The rig's own emission of this metric remains a deterministic
synthetic fixture (counting it would fabricate a baseline, P5 forbids it).
Per the task's explicit fallback clause, day 6 re-baselines from the coarse
P8 rig `errors_total` + `phantom_availability_probes_total` signals, exactly
as days 1-5 did, and records the gap again.

### Did the day-4/day-5 follow-ups move their buckets? — Honest read

Both follow-ups are landed in CODE (`availability-stale-candidate-reprobe-001`
`DONE`) but their LIVE bucket movement is unprovable from this worktree for
the same reason as dial #1/#2 in every prior day's analysis: no deployed-
series query access, and the one task that would prove it against the real
rig DB (`availability-stale-candidate-reprobe-verify-001`) is itself blocked
on the operator seed-refresh above. Offline, by construction, the structural/
behavioural harness (`./scripts/tests/availability-stale-candidate-reprobe.test.sh`)
is green for both item_types, which is the strongest evidence available
without deployed-series or live-rig access.

### Baseline value (honest, coarse — same substrate as days 1-5)

Unchanged in KIND: the P8 rig coarse error rate
(`phantom_loadtime_errors_total / phantom_loadtime_runs_total`, per flow ×
item_type) still shows the cold flow (`materialise_then_play`) as the
dominant error contributor, and `phantom_availability_probes_total{type,
outcome}` still shows cold-flow failures concentrating before a candidate is
ever tried. No new coarse signal shape emerged since day 4 (nothing on the
probe/candidate side changed the coarse-rig behaviour — dial #1/#2 and their
follow-up are UPSTREAM refinements the coarse rig counters cannot see below
their own resolution).

## Cause ranking (biggest win first)

With BOTH primary dials `DONE`, BOTH the day-4 follow-up `DONE` and its
day-5 live-rig verification correctly `NEEDS-HUMAN` (a genuine, narrow,
already-actionable operator gate — not a misclassification per
`AGENTS.md`'s buildable-dependency rule, since refreshing a host-local test
fixture per `docs/agents/testing.md`'s own policy is explicitly an operator
action, not something the swarm can provision itself), the candidate/probe
side of the funnel has no further UNENQUEUED gap this pass can find by
re-reading `ListVisible*RowsAsync`, `AvailabilityProbeWorker`, and
`MarkStaleAvailableItemsDueAsync` — days 4 and 5 already closed the concrete
gaps visible from code inspection, and the remaining unknown (did the fix
actually move the live bucket) is gated on the operator action above, not on
more probe-side design work.

This pass therefore moved to the SECOND avenue the task's own card
authorizes ("the gostream handoff stages ... once the deployed series shows
they dominate") — with the caveat that no deployed series exists yet to
PROVE the gostream stages dominate. What justifies acting now anyway,
consistent with P5 (measure before optimising) rather than guessing: a
concrete, CODE-VISIBLE structural gap found by re-reading the gostream
handoff path this pass, independent of any traffic-volume assumption:

1. **Retry-discipline asymmetry in the gostream handoff (NEW finding, this
   pass).** `GostreamClient.AddAsync` already retries its register HTTP call
   ONCE on a transient failure (`PostWithOneRetryAsync`,
   `Clients/GostreamClient.cs:95,391`). But
   `PhantomMaterialisingMediaSourceProvider.WaitForFileAsync`
   (`Channels/PhantomMaterialisingMediaSourceProvider.cs:436-455`), which
   polls for the FUSE path gostream is expected to expose AFTER a successful
   register, has ZERO retry: if the FUSE path never appears inside
   `FusePathWaitTimeoutSeconds`, it throws `FileNotFoundException` straight
   through to `OpenMediaSource`'s catch, which classifies it as the terminal
   `gostream_cannot_fetch` cause with no second attempt at ANY level. This is
   the exact same class of transient failure the register call's own
   one-retry already defends against (gostream flakiness), just one step
   later in the SAME handoff — an inconsistency independent of how often it
   fires in prod, and a concrete, bounded, low-risk fix (mirror the existing
   one-retry pattern rather than invent a new one).
2. **Residual `availability_abstain` + `no_candidate` after dial #1 and the
   stale-candidate follow-up (general)** — the live residual after deploy
   remains unmeasured (owed to the first real-series pass, unchanged gap).
3. **`first_byte_timeout`'s OTHER source** (`WaitForMaterialisedStateAsync`
   returning null because the materialiser itself never produced a
   `materialised_state` row) — upstream of gostream, explicitly out of scope
   for the fix enqueued below; noted for a future pass once/if it shows up
   as a distinct dominant bucket in a real series.

## The one fix enqueued (biggest win)

**`gostream-fuse-wait-eager-reregister-001`** — on `WaitForFileAsync`'s
poll-timeout (the FUSE path has still not appeared), issue exactly ONE eager
re-register call to gostream for the same item and, if it succeeds, extend
the wait by one additional bounded poll window (reusing the existing
`FusePathWaitTimeoutSeconds`/`FusePathPollIntervalMilliseconds` config) before
finally raising `FileNotFoundException` — mirroring `GostreamClient
.AddAsync`'s own existing one-retry discipline one step later in the same
handoff, rather than escalating to unbounded/exponential retries. Movie AND
episode parity required (the provider path is already shared, so the
regression must still assert both). No change to the register call's own
retry, to `gostream_register_fail` classification, or to any
availability/candidate-side logic (out of scope). Design doc:
`docs/tasks/gostream-fuse-wait-eager-reregister-001.md`. `Check:` `dotnet
test --filter FullyQualifiedName~PhantomMaterialisingMediaSourceProviderTests`
(matches the `dotnet-test` framework already registered in `CHECKS.md`).

This directly targets `gostream_cannot_fetch` — one of the two gostream
handoff-stage causes the series has deferred since day 1 pending a deployed
series, now justified by a concrete code-level asymmetry rather than a
traffic-volume reading, while the probe/candidate side waits on the operator
gate above.

## Successor

`playback-error-reduction-007` appended `[TODO]` held on `not_before ~= +24h`
for tomorrow's pass (daily cadence).
