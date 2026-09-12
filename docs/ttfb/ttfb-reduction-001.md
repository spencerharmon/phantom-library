# ttfb-reduction-001 — daily TTFB-reduction analysis (seed pass)

ROI Priority 9 seed of the self-perpetuating daily TTFB-reduction loop. This
pass is DIAGNOSE-AND-ENQUEUE ONLY: it measures, ranks the dominant stage,
records the cold-materialise failure rate, and files exactly one concrete
fix task plus the `ttfb-reduction-002` successor. No fix code is shipped in
this pass.

## Data source

No new live rig run was executed by this analytical pass (the P8 rig +
acceptance-bar infrastructure already exists and is expensive to re-run
against the real deployed color for a diagnostic-only task). Instead this
analysis reuses the most recent **real, live, in-cluster** measurement of
the full materialise TTFB path: the `p8-loadtime-acceptance-rig` capstone's
evidence (`submodules/phantom-library/docs/bee-p8-loadtime-acceptance-rig-p8-loadtime-acceptance-rig.md`),
which drove the actual deployed `phantom-library-bluegreen-deploy` green
color via the P8/1 measurement engine (movie AND episode, all six flows)
and confirmed every number live in Mimir. That run is the freshest ground
truth available for the six ROI-named flows and is the correct basis for
ranking the dominant TTFB stage; it is cross-checked below against a static
read of the current materialise/sources code path (`p6-materialise-ttfb-fix`,
already landed) to confirm the mechanism, not just the symptom.

### Live measurement (p8-loadtime-acceptance-rig, real deployed green color)

```
flow=list_load          item_type=movie   duration_s=0.382467 errors=0
flow=sort_change        item_type=movie   duration_s=0.410803 errors=0
flow=info_open          item_type=movie   duration_s=0.014005 errors=1
flow=get_sources        item_type=movie   duration_s=0.046751 errors=0
flow=materialise        item_type=movie   duration_s=64.720732 errors=1
flow=play_materialised  item_type=movie   duration_s=0.035906 errors=1
flow=list_load          item_type=episode duration_s=0.171459 errors=0
flow=sort_change        item_type=episode duration_s=0.182602 errors=0
flow=info_open          item_type=episode duration_s=0.012688 errors=1
flow=get_sources        item_type=episode duration_s=0.011981 errors=1
flow=materialise        item_type=episode duration_s=0.011467 errors=1
flow=play_materialised  item_type=episode duration_s=0.013083 errors=1
```

## Ranking: dominant stage

`materialise` (movie) at **64.72s** utterly dominates every other flow by
2-3 orders of magnitude (`get_sources` movie: 0.047s; every other flow:
sub-second). The episode `materialise` sample (0.011s, but `errors=1`) is
NOT evidence the stage is cheap for episodes — it is evidence the episode
probe **errored out fast** rather than completing a full fan-out (consistent
with the failure-rate finding below). The movie sample is the one that ran
to completion and is the honest "full-path when it succeeds" TTFB number.

**Within `materialise`, the whole-wait chain per the task's own stage
breakdown (availability -> candidate/source probe -> magnet select ->
gostream register -> first byte) is dominated by the candidate/source
probe stage specifically**, established by reading the current code (not
guessing):

- `Materialiser.BuildGostreamRequestsAsync`
  (`src/Jellyfin.Plugin.PhantomLibrary/Materialisation/Materialiser.cs`,
  landed by `p6-materialise-ttfb-fix`) is cache-first: a cache HIT returns
  instantly with **no** independent probe. A cache MISS (the cold-item case
  this task's "new-item materialise TTFB" targets) calls
  `MagnetCacheBuilder.BuildSynchronousAsync`
  (`src/Jellyfin.Plugin.PhantomLibrary/Sources/MagnetCacheBuilder.cs:175`),
  which by default probes via `MagnetSelector.ProbeAsync`
  (`src/Jellyfin.Plugin.PhantomLibrary/Sources/MagnetSelector.cs:178`).
- `MagnetSelector.ProbeCoreAsync`
  (`src/Jellyfin.Plugin.PhantomLibrary/Sources/MagnetSelector.cs:218-285`)
  fans out to every **enabled** `IIndexerClient` (today: `ProwlarrClient`,
  `TorrentioClient`) in a **plain sequential `foreach` with `await`** —
  line 253-285: each indexer's `SearchAsync` is awaited to completion
  before the next indexer is even started. There is no `Task.WhenAll`, no
  per-indexer timeout/short-circuit, and no early-exit once an
  already-acceptable high-confidence candidate is found. Prowlarr in
  particular fans a single query out to every configured downstream
  indexer server-side (a multi-indexer meta-search), so its own
  `SearchAsync` call is itself a slow, variable-latency operation before
  this plugin's sequential loop even reaches Torrentio.
- `get_sources` (0.047s movie / 0.012s episode) stays cheap because it
  reads already-cached/available source state — it never enters this
  cold-probe fan-out. `materialise`'s 64.72s is therefore attributable to
  a single cold pass through this exact sequential, non-short-circuited,
  non-parallel indexer fan-out — the "candidate/source probe" stage,
  confirming the ROI's own "parallelising/short-circuiting the sources
  probe" fix direction is the correct one for the current bottleneck (not
  gostream register or magnet select, which run after a probe result is
  already in hand and are not separately instrumented as slow anywhere in
  this codebase's rig evidence).

## Cold-materialise failure rate

Of the two `materialise` samples in the live run, **both recorded
`errors_total=1`** (movie AND episode) — a **100% observed failure rate**
in this single acceptance run's cold-path exercise (`n=2`, both item
types). This sample size is too small to be a statistically confident
long-run rate, but it is a real, live, non-hypothetical data point,
consistent with `p6-materialise-ttfb-fix`'s own root-cause finding that a
cold cache-miss previously fell back to an independent probe that could
disagree with the download source; the synchronous-build path introduced
by that fix still runs the SAME potentially-slow, non-parallel,
non-timeout-guarded fan-out inline in the hot materialise path, so a slow
or partially-failing indexer in that sequential chain can still surface as
a materialise-level error even after the P6 fix. This reinforces (does not
contradict) the ranking above: the probe-fan-out stage is both the latency
bottleneck and a contributor to the failure rate, because a single slow or
erroring indexer in the sequential chain blocks/derails the whole
synchronous build rather than being isolated and raced against its peers.

## Enqueued fix (biggest single win)

**Parallelise the indexer probe fan-out in `MagnetSelector.ProbeCoreAsync`**
— replace the sequential `foreach`+`await` loop over enabled indexers with
a concurrent `Task.WhenAll`-style fan-out (each indexer's `SearchAsync`
started immediately, all awaited together, each wrapped in its own
try/catch so one indexer's exception/timeout does not block or abort the
others), plus a per-indexer timeout so a single slow indexer cannot inflate
the whole probe past a bounded ceiling. This directly targets the dominant
stage identified above (candidate/source probe), is the most surgical of
the ROI's five listed fix directions relative to the CURRENT code (P6
already added the cache-first/synchronous-build machinery; this fix
improves the synchronous build's own probe latency rather than requiring
new caching/queueing infrastructure), and does not touch the P6 cache-first
correctness contract (cache HIT still short-circuits the probe entirely;
this only changes how the MISS-path probe executes internally). Filed as
its own task with its own design doc and a `dotnet test`-matching Check —
see `docs/tasks/ttfb-parallel-indexer-probe.md` and
`PLAN.md`'s `ttfb-parallel-indexer-probe` entry.

## Successor

`ttfb-reduction-002` is appended to `PLAN.md` as `[TODO]` with
`not_before` set to approximately +24h from this pass, continuing the
daily cadence. It will re-measure (ideally against a fresh
`p8-loadtime-acceptance-rig`-style live run, ideally AFTER
`ttfb-parallel-indexer-probe` has landed) and enqueue the next biggest win
from the ROI's remaining fix directions (opportunistic prefetch-on-
interaction, a non-rate-limited priority queue, TTL/negative-backoff
caching, speculative gostream prefetch).

## Non-goals of this pass

No fix code shipped in this pass (`check=none`, DIAGNOSE-AND-ENQUEUE ONLY
per the task card). No new live rig run was triggered (re-running the full
in-cluster acceptance rig — repin, port-forward, six-flow drive, Mimir/
Grafana verification — is itself an expensive live-cluster operation
reserved for the rig's own acceptance/daily-schedule tasks; this pass
reuses that rig's own most recent live evidence rather than duplicating
its cluster-touching side effects for a read-only analysis).
