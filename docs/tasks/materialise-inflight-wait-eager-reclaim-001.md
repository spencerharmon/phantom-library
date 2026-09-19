# materialise-inflight-wait-eager-reclaim-001 — eager reclaim on materialise-wait timeout

ROI Priority 12 fix, enqueued by `playback-error-reduction-007` (day 7).

## The gap

`Materialiser.MaterialiseAsync` already self-heals a leaked in-flight
materialise claim: `TryInsertMaterialiseInFlightAsync` lets the next caller
reclaim a claim older than `MaterialiseInFlightStaleMinutes` (default 10 min).
But a concurrent request that observes `AlreadyInProgress` defers to
`PhantomMaterialisingMediaSourceProvider.WaitForMaterialisedStateAsync`, which
only polls the `materialised_state` row for up to `FusePathWaitTimeoutSeconds`
(default 60s — an order of magnitude shorter than the stale threshold) and
never calls back into the materialiser's claim path. If the claim it is
waiting behind is genuinely leaked, the request always times out after 60s
and classifies `first_byte_timeout`, even though the materialiser's own
steal-if-stale logic would gladly reclaim that exact row — nothing in the 60s
poll loop ever triggers the reclaim.

## Fix

On `WaitForMaterialisedStateAsync`'s poll-timeout, issue exactly ONE
additional call into `IMaterialiser.MaterialiseAsync` for the same item
before finally giving up:

- If the original claim is genuinely stale, this reclaims it and starts a
  real materialise; on success (`Success`/`Duplicate`), extend the wait by
  one additional bounded poll window (same `FusePathWaitTimeoutSeconds` /
  `FusePathPollIntervalMilliseconds` config, no new knob).
- If the claim is still fresh, the reclaim attempt itself returns
  `AlreadyInProgress` again (or fails outright) and the method fails fast —
  returns `null`, which the existing caller converts to the existing
  `TimeoutException` — no stacked retries, no unbounded backoff. Mirrors the
  exact bounded shape of the day-6
  `gostream-fuse-wait-eager-reregister-001` fix (one eager retry, one
  extended window, fail fast on the retry's own failure).

Implementation lives entirely in `WaitForMaterialisedStateAsync`, split into:

- `PollForMaterialisedStateAsync` — the original single bounded poll window
  over the `materialised_state` row (unchanged behaviour, factored out so it
  can be reused for both the initial window and the one extension).
- `EagerReclaimMaterialiseAsync` — issues the one eager
  `MaterialiseAsync` call and, on `Success`/`Duplicate`, re-polls once more;
  on anything else (including a thrown exception) returns `null` immediately.

## A pre-existing double-wait bug this fix exposed and closed

`OpenMediaSourceCore` had a redundant
`existing ??= await WaitForMaterialisedStateAsync(...)` fallback AFTER the
`if (outcome.Status == AlreadyInProgress) { existing = await
WaitForMaterialisedStateAsync(...); }` branch already ran. Before this fix
that merely doubled the (harmless, since it just re-polled) wait window on an
`AlreadyInProgress` timeout. With the eager-reclaim retry added, that
redundant fallback would have invoked `WaitForMaterialisedStateAsync` a
SECOND time — i.e. TWO eager reclaim `MaterialiseAsync` calls total for one
playback attempt, violating the "exactly one additional call" requirement of
this task. The redundant fallback line is removed; `existing` is already
fully resolved by the `if`/`else if`/`else` chain above it (Success/Duplicate
reads the row directly, AlreadyInProgress waits for it including the eager
reclaim, the `else` throws). No behaviour change for the Success/Duplicate/
Error paths; the AlreadyInProgress path now does exactly one wait+reclaim
cycle instead of up to two.

## Movie/episode parity

`WaitForMaterialisedStateAsync` and the new `EagerReclaimMaterialiseAsync` /
`PollForMaterialisedStateAsync` helpers are shared, type-agnostic code already
called from both the movie (`season`/`episode` sentinel `-1`/`-1`) and
episode paths in `OpenMediaSourceCore`. The regression test exercises both
`item_type`s via `[Theory]`/`[InlineData]`.

## Non-goals

- No change to `MaterialiseInFlightStaleMinutes`, the sweeper, or the day-6
  FUSE-wait (`WaitForFileAsync`) eager re-register fix — those are separate,
  already-landed/out-of-scope mechanisms.
- No new configuration knob — the eager reclaim's extended wait reuses the
  existing `FusePathWaitTimeoutSeconds` / `FusePathPollIntervalMilliseconds`.
- No change to the definitive playback-outcome classification
  (`first_byte_timeout` on the ultimate `TimeoutException`, unchanged).

`Check:` `dotnet test --filter
FullyQualifiedName~PhantomMaterialisingMediaSourceProviderTests` (matches the
`dotnet-test` framework already registered in `CHECKS.md`).
