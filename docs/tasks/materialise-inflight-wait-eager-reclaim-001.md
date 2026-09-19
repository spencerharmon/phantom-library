# materialise-inflight-wait-eager-reclaim-001 — design

## Problem

`Materialiser.MaterialiseAsync` (`Materialisation/Materialiser.cs`) already
self-heals a leaked in-flight materialise claim: `TryInsertMaterialiseInFlightAsync`
lets the NEXT caller reclaim a claim older than `MaterialiseInFlightStaleMinutes`
(default 10 minutes) — "steal-if-stale", documented inline at
`Materialiser.cs:284-291`.

But a concurrent request that observes `MaterialisationStatus.AlreadyInProgress`
defers to `PhantomMaterialisingMediaSourceProvider.WaitForMaterialisedStateAsync`
(`Channels/PhantomMaterialisingMediaSourceProvider.cs:409-429`), which ONLY
polls the `materialised_state` row for up to `FusePathWaitTimeoutSeconds`
(default 60s) — an order of magnitude shorter than the 10-minute stale
threshold — and never calls back into the materialiser's claim path itself.
If the claim it is waiting behind is actually leaked (the original claimant
crashed without running its `finally` cleanup), this request will ALWAYS
time out after 60s and get classified `first_byte_timeout`, even though the
materialiser's own steal-if-stale logic would gladly reclaim that exact row
— nothing in this 60s poll loop ever triggers that reclaim. Only some
unrelated future top-level `MaterialiseAsync` call (or the once-at-startup
`MaterialiseInFlightSweeper`) eventually clears it, and this request never
benefits from that.

## Fix

Mirror the existing steal-if-stale discipline into
`WaitForMaterialisedStateAsync`'s own timeout, exactly the same bounded
shape as the day-6 `gostream-fuse-wait-eager-reregister-001` fix one step
later in the handoff:

- On poll-timeout (the `materialised_state` row still hasn't appeared after
  `FusePathWaitTimeoutSeconds`), issue exactly ONE additional call into
  `IMaterialiser.MaterialiseAsync` for the same `(tmdbId, type, season,
  episode)` key, `MaterialiseTrigger.Play`.
  - If the original claim was genuinely stale, this call's own
    `TryInsertMaterialiseInFlightAsync` steal-if-stale path reclaims it and
    starts a real materialise. On `Success`/`Duplicate`, re-read the
    `materialised_state` row and, if present, RETURN it (success — no
    further wait needed since the materialise ran synchronously inside this
    call). If the row is somehow still absent, extend the wait by exactly
    one additional bounded poll window (`FusePathWaitTimeoutSeconds`,
    `FusePathPollIntervalMilliseconds` — reused config, no new knob) before
    giving up.
  - If the reclaim attempt itself returns `AlreadyInProgress` again (the
    original claim is still genuinely fresh — a real concurrent materialise
    is legitimately still running), do NOT loop or extend further: fail fast
    to the existing behavior (return `null`, which the caller already turns
    into `TimeoutException` → `first_byte_timeout`). No stacked retries, no
    unbounded/exponential backoff — same bounded contract as the day-6 fix.
  - If the reclaim attempt returns `Unavailable`/`Error`, likewise fail fast
    (return `null` — the caller's existing timeout classification stands;
    this method's contract is "state row appeared or not", not raw
    materialise-failure propagation, so it deliberately does not throw a
    `MaterialiseOutcomeSignal` from inside the wait helper).
- Movie AND episode parity required — `WaitForMaterialisedStateAsync` is
  already shared across both, so the regression must assert both
  `item_type=movie` and `item_type=episode` token shapes.
- No change to `MaterialiseInFlightStaleMinutes`, `MaterialiseInFlightSweeper`,
  or the day-6 gostream-FUSE-wait fix (out of scope, non-goal).
- No change to `gostream_register_fail`/`gostream_cannot_fetch`
  classification — this only affects the upstream materialise-wait path
  that precedes the gostream handoff entirely.

## Regression test (required before DONE)

Add a dotnet unit regression to `PhantomMaterialisingMediaSourceProviderTests`
that FAILS without the change and PASSES with it, asserting:

1. When the first `MaterialiseAsync` call returns `AlreadyInProgress` and the
   `materialised_state` row never appears within the first poll window, a
   SECOND `MaterialiseAsync` call is issued (the eager reclaim) — for both
   `item_type=movie` and `item_type=episode`.
2. If that second call succeeds (`Success`/`Duplicate`) and the state row is
   then present, the overall open succeeds (no `TimeoutException`) instead
   of the pre-fix behavior of always timing out.
3. If that second call ALSO returns `AlreadyInProgress` (genuinely still in
   flight), the method still fails to the existing `TimeoutException` —
   exactly once, no third call, no unbounded retry loop.

`Check:` `dotnet test --filter
FullyQualifiedName~PhantomMaterialisingMediaSourceProviderTests` (matches the
`dotnet-test` framework registered in `CHECKS.md`).

## Non-goals

- No change to the availability/candidate-side dials (out of scope).
- No change to the day-6 gostream-FUSE-wait eager-reregister fix.
- No deployed-series verification (still gated on the separate, unfiled,
  "deploy playback-outcome-instrumentation-001 to prod" gap noted in the
  day-7 analysis doc).
