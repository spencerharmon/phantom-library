# Indexed Availability Batches (Work In Progress)

Not deployable yet. The acceptance gate has not passed. No production schema or
configuration was changed by this work pass.

## Design

PostgreSQL adds defaulted scheduler_ready, scheduler_bucket and scheduler_due_at
columns without rewriting the base table. A trigger maintains these for both old
and new writers. Four partial indexes cover future due promotion, global ready
ordering, typed ordering and episode-after-cursor ordering. Concurrent index builds
run outside transactions; interrupted invalid builds are dropped and recreated.
The base v22 schema version remains unchanged and newer versions remain readable.

Claims use one atomic UPDATE from FOR UPDATE SKIP LOCKED candidates and return
the existing owner/generation lease fields. Episode recursion selects one row per
series through a real indexed cursor range, not a bounded oldest-due window.
Policy changes do not reset unfinished sweeps. A persisted keyset visits at most
256 rows per call and a completed sweep has a 60-second policy-change cooldown.
The trigger makes late old-policy writes eligible again. This is not yet a proven
rate limit on unfinished sweeps: calls can advance successive chunks immediately.

The worker starts at most two claims concurrently. It keeps no cross-opportunity
FIFO, alternates type preference across ticks, checks ownership before side effects,
cancels before the original lease deadline and releases remaining ownership in a
finally block. SQLite retains serialized claims/lease mutations.

## Query Inventory

| Operation | Production location | Frequency / bounds |
| --- | --- | --- |
| Global / movie claim | PostgresAvailabilityScheduler.CreateClaimCommand | <=32 rows; worker <=2 |
| Episode after cursor + wrap | Same builder, recursive episode variant | <=32 distinct series |
| Higher-priority type selection | PostgresAvailabilityScheduler.ClaimAsync | Two indexed priority lookups |
| Due promotion | CreatePromotionCommand | <=256 rows per claim call |
| Policy sweep | CreateSweepCommand | Two statements, <=256 keyed rows |
| Lease renew/release | PhantomDb lease APIs | Primary-key, owner/generation fenced |
| User activity lookup | AvailabilityProbeWorker.TickAsync | Before each opportunity |
| Stale-available promotion | MarkStaleAvailableItemsDueAsync | Unchanged, potentially broad per tick |
| Series expansion | ClaimDueSeriesExpansionAsync and worker | One chance per opportunity |
| Completion/cache/relevance | Existing PhantomDb write methods | Per successful probe |

The polling sibling must measure all of these, including metadata/cursor traffic
and stale-available promotion, rather than just reducing timer frequency.

## Verification And Remaining Work

The .NET build succeeds. Eight new SQLite worker scenarios and seven PostgreSQL
claim scenarios pass. PostgreSQL scenarios exercise sparse/empty state, disjoint
claimers, expiry fencing, row-lock contention, cursor wrap past a dominant series,
alternating policies and late old writes, and v22/legacy-writer overlap.

The real successful worker test deliberately uses a 5 GiB candidate. It exposes
an existing PostgreSQL INTEGER overflow in source_candidates.size, before cache or
availability completion. This needs a separate additive, overlap-safe 64-bit media
size prerequisite; reducing the fixture size would hide the production defect.

Still required before review: successful PostgreSQL worker/cancellation suite;
4M-row scale execution and retained plans; measured latency and no-Seq-Scan proof
(including avoiding singleton-state Seq Scans inside claim plans); bounded clone
rehearsal predicted/actual counts; interrupted-index regression; per-chunk sweep
rate limiting; tests of the complete schema upgrade chain; required rig35/36 and
Gitea run. The current CI rig adapter requires a provisioned isolated runner and
must fail rather than omit parity. The scale fixture currently hydrates all rows
with a clone-only UPDATE and needs predicted/actual rehearsal instrumentation.

Run from the owning worktree:

```
beehive submodule worktree exec phantom-library bee-p0-indexed-batch-availability -- ./scripts/tests/p0-availability-claims.test.sh
```

The script requires PostgreSQL or provisions a disposable container, requires
patched Jellyfin source for compilation, retains artifacts, and fails closed on
missing rig prerequisites. This command has not passed in this session.
