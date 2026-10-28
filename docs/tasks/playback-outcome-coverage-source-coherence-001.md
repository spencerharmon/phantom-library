# playback-outcome-coverage-source-coherence-001

## Problem and scope

Day 19 found that `scripts/phantom-playback-outcome-coverage.sh` validates a
common outer query time (lines 124-133 at base 9d7dfb2) and maximum source
age (142-156), then calculates ratios/rankings (157-178). It does not verify
that the 32 counters for one identity share a source collection timestamp.
Prometheus lookback can combine individually fresh samples from different
collections. Such a mixture must not be presented as a coherent snapshot.

Extend this existing offline analyser and its real executable harness only.
This does not duplicate window-vector input parsing, per-slice breakdown,
runtime-info, seeding or deployment work. No live traffic or promotion.

## Required behavior

Invariant: an identity's numeric aggregate/slice rates and rankings require
complete, fresh counters with one common source timestamp within that identity.

Compare parsed numeric source timestamps from the companion timestamp vector,
not their string encodings and not the outer evaluation timestamp. Default to
exact equality: there is no justified error tolerance for a mixed collection.
Report a distinct INCOHERENT diagnostic with source-time minimum, maximum and
skew; return exit 2 and withhold that identity's derived rates/rankings. Raw
coverage/age/context diagnostics may remain visible. Incomplete and stale
inputs must remain non-OK and cannot bypass their existing checks. Apply the
same protection to inactive identities, whose numeric snapshots are also used
for diagnosis. Preserve the current healthy behavior for other identities.

Do not require timestamps to match BETWEEN identities: independently collected
exporters have independently reported denominators. Equal timestamps establish
this conservative collection prerequisite, not proof of atomicity, common
counter lifetime, no resets, organic provenance or a window error rate. Keep
the cumulative-not-window disclaimer. Document this strict policy in help and
the existing coverage contract; do not silently add a skew tolerance.

## Reproduction and acceptance

Use one serving identity `{"instance":"example.com"}`, the complete canonical
2 flows x 2 item types x 8 causes, outer evaluation time 1000 everywhere,
`--now 1000 --max-age 120 --min-attempts 100`, and an empty window vector.
Cold-movie success is 1000 at source timestamp 880; cold-movie no_candidate
is 101 at timestamp 990; all other counters are zero at timestamp 990.
The current real script returns exit 0, OK, attempts=1101, errors=101,
error_rate=9.173479%, cause_ranking=no_candidate:101, and the same slice rate.
This was reproduced offline on day 19, without live events or file mutations.

Extend `scripts/tests/phantom-playback-outcome-coverage.test.sh` to drive that
fixture against the real script and require INCOHERENT, skew=110, exit 2 and
no numeric rates/rankings. Rotate the lagging positive tuple through all four
flow/item slices; exercise serving and inactive identities. Include coherent
fresh positive/zero controls; two identities with internally coherent but
different timestamps; equivalent decimal timestamp spellings; and a mixed
healthy/incoherent report proving isolation. Preserve invalid/future/stale,
raw-window, vocabulary, volume-threshold and stub-rejection protections.

The existing inclusive freshness-boundary case changes one source timestamp
to 880 and expects OK. Change ALL 32 timestamps to 880 in that boundary fixture
so it still tests age=120 without conflicting with the new coherence contract.
Keep a separate skew regression; do not drop freshness coverage.

Check: `./scripts/tests/phantom-playback-outcome-coverage.test.sh`

Matches hive CHECKS.md `script-test`. The implementation pass must demonstrate
new assertions failing on the old script and passing on the fix, record the
exact worktree-exec command/output, and retain read-only input guarantees.
No Jellyfin rig/deployment is required for this offline reporting change.
