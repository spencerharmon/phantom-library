# availability-probe-reconcile-001 — raise the availability-probe success rate

Enqueued by `playback-error-reduction-002` (series ROI Priority 12, day 2) as
the biggest BEHAVIOUR win once the measurement dial
(`playback-outcome-instrumentation-001`) had landed. This is **primary dial #1**
of the series: raise the fraction of cold (`materialise_then_play`) attempts
that reach a definitive `available` verdict, directly attacking the two dominant
cause buckets — `availability_abstain` and `no_candidate` — that the day-1 and
day-2 rankings both put first.

This task DOES change playback behaviour (unlike the measurement task). It is the
first behaviour-changing fix in the series.

## Goal

At materialise time, before a cold attempt abstains or gives up with no
candidate, make the availability oracle SUCCEED more often — without weakening
the correctness of an `available` verdict and without hammering the upstream
oracles. Three coordinated moves:

1. **Reconcile the P6 Torrentio oracle with the Prowlarr high-confidence magnet
   set.** Today Torrentio abstains on any no-IMDB title
   (`IndexerNotApplicableException`) and some titles it simply has no result
   for. When Torrentio abstains but the Prowlarr high-confidence magnet set
   already holds a viable magnet for the same item, the oracle should reach a
   definitive `available` verdict from Prowlarr rather than surfacing
   `availability_abstain`. The Prowlarr magnet must clear the SAME
   high-confidence bar the source pipeline already uses (never lower the bar to
   fake availability).
2. **Re-probe on TTL expiry** rather than serving a stale abstain/negative. An
   availability verdict carries a TTL; once expired, the next attempt re-probes
   instead of replaying the last (possibly transient) abstain.
3. **Cache negative results with bounded exponential backoff.** A genuinely
   unavailable item must not be re-probed on every single attempt (that would
   hammer Torrentio/Prowlarr), but must eventually be re-checked. Cache the
   negative verdict with a backoff that grows on repeated confirmed-negative
   probes and resets on a positive, bounded by a max interval.

Movie AND episode parity is required (project rule): every reconcile / TTL /
negative-cache path is exercised for both `item_type=movie` and
`item_type=episode`. A fix that helps only movies is half a fix.

## Where to implement (the real playback path)

- `PhantomSourceManager` (`GetSourcesAsync` / `MaterialiseCandidateAsync`) — the
  point where `availability_abstain` / `no_candidate` outcomes are currently
  recorded (per `playback-outcome-instrumentation-001`). Insert the
  Torrentio↔Prowlarr reconcile step here, before recording an abstain.
- The P6 Torrentio availability oracle and the Prowlarr high-confidence magnet
  set (`TorrentioClient` and the Prowlarr indexer path) — read both; treat a
  Torrentio abstain as non-final when Prowlarr holds a high-confidence magnet.
- Availability verdict cache (the existing availability/probe cache the oracle
  consults) — add the TTL re-probe and the bounded-backoff negative-result
  entry. Reuse the existing repository abstractions in
  `src/Jellyfin.Plugin.PhantomLibrary/Data/`; do not open raw connections.
- Emit the correct `phantom_playback_outcome_total` cause via the existing
  `PhantomFlowMetrics.RecordPlaybackOutcome` helper so the series can PROVE the
  `availability_abstain` / `no_candidate` bucket shrinks after this lands.

Keep all infra hosts out of tracked code (oracle/indexer endpoints from
config/Secrets, never baked — infra-identifier rule).

## Non-goals

- No browse-prune (that is dial #2, a later series pass).
- No change to the gostream register / first-byte handoff (secondary cause
  bucket).
- No lowering of the high-confidence magnet bar — availability must remain a
  truthful verdict, not an optimistic one.

## Definition of done (Check)

`Check:` = `./scripts/tests/availability-probe-reconcile.test.sh` — an in-repo
`script-test` harness (matches the `CHECKS.md` `script-test` framework) that
drives the reconcile / TTL-re-probe / negative-backoff behaviour offline against
the `47-loadtime-flows.sh` synthetic fixture and asserts, for BOTH
`item_type=movie` and `item_type=episode`:

- a Torrentio-abstain item that has a Prowlarr high-confidence magnet resolves
  to a definitive `available` verdict (records `cause=success`, not
  `availability_abstain`);
- a verdict past its TTL triggers a re-probe rather than replaying the stale
  abstain;
- a confirmed-negative item is cached and NOT re-probed within the current
  backoff window, and IS re-probed once the (bounded) backoff elapses;
- the high-confidence bar is unchanged (a low-confidence Prowlarr magnet does
  NOT flip an abstain to available).

The harness FAILS before this task's reconcile logic exists and PASSES after.
The `.test.sh` runner is committed executable (sandbox denies `bash` as a check
word; exec'd directly by its shebang path). A behavioural change also requires a
`dotnet test` regression on the C# reconcile path — the implementer adds a unit
test that FAILS without the change and PASSES with it, alongside the rig harness,
and follows `docs/agents/testing.md` for the live-rig verification of the real
cold-materialise flow (movie AND episode) before marking done.

## Series linkage

Enqueued by `playback-error-reduction-002` (day 2). Attacks the day-1/day-2
top-ranked cause bucket (`availability_abstain` + `no_candidate` on the cold
flow). Its effect is provable via the now-landed
`phantom_playback_outcome_total{flow="materialise_then_play",cause=...}` series
once deployed.
