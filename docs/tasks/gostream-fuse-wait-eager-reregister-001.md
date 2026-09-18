# gostream-fuse-wait-eager-reregister-001 — one eager re-register retry on FUSE-wait timeout

Enqueued by `playback-error-reduction-006` (series ROI Priority 12, day 6) as
the biggest remaining behaviour win now that BOTH primary dials
(`availability-probe-reconcile-001`, `browse-prune-dead-swarm-001`) AND both
their follow-ups (`availability-stale-candidate-reprobe-001`, and the
live-rig verification `availability-stale-candidate-reprobe-verify-001`,
currently `NEEDS-HUMAN` on an operator-owned rig-seed refresh — see day 6
analysis) are landed or dispatched. With the candidate/probe side of the
funnel exhausted for now, this pass moves to the gostream handoff stage, per
the series' own third option ("the gostream handoff stages ... once the
deployed series shows they dominate" — see the day 6 analysis for why this
pass treats the code-visible retry ASYMMETRY below, not a deployed series
reading, as sufficient justification to act now rather than deferring a
sixth day in a row).

## The gap

`GostreamClient.AddAsync` already retries the register HTTP call ONCE on a
transient failure (`PostWithOneRetryAsync`, 1s backoff on a 5xx/429) — see
`src/Jellyfin.Plugin.PhantomLibrary/Clients/GostreamClient.cs:95,391`. But
that one-retry discipline stops at the register call. The SAME logical
handoff — get gostream to expose a playable FUSE file for this item — has a
SECOND, later step with **zero** retry: `PhantomMaterialisingMediaSourceProvider
.WaitForFileAsync` (`Channels/PhantomMaterialisingMediaSourceProvider.cs:436-455`)
polls for the FUSE path to appear for up to `FusePathWaitTimeoutSeconds`, and
if it never appears, throws `FileNotFoundException` straight to the caller,
which `OpenMediaSource`'s catch classifies as the definitive terminal cause
`gostream_cannot_fetch` (`OpenMediaSource`, same file, lines ~224-231). There
is no attempt, anywhere in that whole wait window, to re-issue the register
call and give gostream a second chance to expose the file before the attempt
is abandoned as a hard failure — an asymmetry with the register call's own
one-retry pattern one level up, for what is often the SAME class of
transient failure (gostream registered the item but its own internal
fetch/mount step stalled or silently dropped without the FUSE path ever
appearing — see also `WaitForMaterialisedStateAsync`'s identical shape,
mapped to `first_byte_timeout` via the bare `TimeoutException` path when
`existing` never resolves).

## Goal

When `WaitForFileAsync`'s poll loop is about to time out (the FUSE path has
still not appeared), issue **exactly one** eager re-register call to gostream
for the SAME item (reusing `GostreamClient.AddAsync`'s existing request
shape/idempotency — this is not a new registration path, just re-invoking the
existing one) and, if that call succeeds, extend the wait by one additional
bounded poll window (reusing the existing `FusePathWaitTimeoutSeconds` /
`FusePathPollIntervalMilliseconds` config, not a new knob) before finally
raising `FileNotFoundException`. If the eager re-register call itself fails
(non-2xx after its own existing one retry), fail fast to the existing
`gostream_cannot_fetch` classification immediately — do not stack retries on
top of retries. Movie AND episode parity is required (a fix exercised only
against the movie path is half a fix — this provider path is shared by both
kinds already, so the fix is naturally shared, but the regression MUST still
assert both `item_type=movie` and `item_type=episode` token shapes). No
change to the register call's own existing one-retry discipline, no change to
`gostream_register_fail` classification, no change to the availability/
candidate-side dial logic (out of scope, non-goal — those are `DONE`/blocked
`NEEDS-HUMAN` separately). No schema change: this is a provider-level control-
flow change only, no new DB columns or tables.

## Non-goals

- Does not touch `first_byte_timeout`'s OTHER source
  (`WaitForMaterialisedStateAsync` returning `null` because the materialiser
  itself never produced a `materialised_state` row) — that failure is
  upstream of gostream entirely (the materialise pipeline, not the gostream
  handoff) and is out of scope for this task.
- Does not add a SECOND retry to the register call itself, and does not add
  unbounded/exponential retries anywhere — exactly one eager re-register,
  exactly one extended bounded wait, mirroring the existing one-retry
  discipline rather than escalating it.
- Does not change `AvailabilityProbeWorker`, `PhantomDb`, or any
  `ListVisible*RowsAsync` browse-visibility logic.

## Suggested shape

- Add an internal `_gostreamClient` (or equivalent) dependency to
  `PhantomMaterialisingMediaSourceProvider` if it does not already have direct
  access to re-issue the register call for the resolved `(tmdbId, type,
  season, episode)` key (check `IMaterialiser`/`_materialiser` first — the
  eager re-register may already be reachable through the existing
  materialiser abstraction rather than requiring a new gostream client
  reference; prefer reusing whatever the initial `MaterialiseAsync` call
  already uses to avoid duplicating the HTTP shape).
- In `WaitForFileAsync` (or a thin wrapper called once from
  `OpenMediaSourceCore` around the existing `WaitForFileAsync` call), on
  first poll-timeout, log a warning, re-issue the register call once, and on
  its success re-enter one more bounded poll window using the same
  timeout/interval config before giving up.
- Regression: a dotnet unit test against
  `PhantomMaterialisingMediaSourceProviderTests` — a fake FUSE-file check that
  never resolves on the FIRST window but resolves after a fake re-register
  call succeeds, for both `movie` and `episode` tokens; asserts the eager
  re-register is invoked exactly once and the FUSE-file check is invoked
  again after it. A companion case asserts NO extra retry attempt when the
  re-register call itself fails (fails straight to `gostream_cannot_fetch`,
  no infinite loop).

`Check:` `dotnet test --filter
FullyQualifiedName~PhantomMaterialisingMediaSourceProviderTests` (matches the
`dotnet-test` framework already registered in `CHECKS.md`).
