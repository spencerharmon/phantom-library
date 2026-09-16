# ttfb-reduction-005 — daily TTFB-reduction analysis

ROI Priority 9, successor of `ttfb-reduction-004`. DIAGNOSE-AND-ENQUEUE
ONLY: this pass's charter was to check whether
`ttfb-daily-rig-gitea-mirror-wiring` has landed and whether a genuinely
FRESH `phantom_loadtime_seconds` sample has appeared and, if so, finally
assess `ttfb-magnet-cache-drain-worker`'s and
`ttfb-fast-indexer-early-return`'s live impact — the open question all of
`ttfb-reduction-002` through `-004` could not answer for lack of fresh
data. **Still no fresh sample.** This pass confirms the pipeline is still
not producing new data, checks on the mirror-wiring task's progress per
the task's explicit fallback instruction, and enqueues an orthogonal
code-only fix that does not depend on the stalled measurement pipeline.

## Data source — queried Mimir directly (live, not the rig)

```
$ kubectl -n monitoring port-forward svc/mimir 18080:8080 &
$ NOW=$(date +%s)
$ curl -s 'http://localhost:18080/prometheus/api/v1/query_range' \
    --data-urlencode 'query=phantom_loadtime_seconds{flow="materialise"}' \
    --data-urlencode "start=$((NOW-1209600))" --data-urlencode "end=$NOW" \
    --data-urlencode 'step=3600'
```

An instant query (`.../query?query=phantom_loadtime_seconds`) again
returns an empty vector (all samples are older than Mimir's ~5m
instant-query staleness window). The 14-day range query recovers the
same last-pushed bucket every prior pass has read:

- Last sample bucket timestamp `1789439787` (the bucket boundary the
  15-day window scrolled to; the underlying push itself is unchanged).
- `movie-materialise = 4.169275s` — **byte-for-byte identical** to the
  value read by `ttfb-reduction-002`, `-003`, and `-004`.
- `episode-materialise = 0.015307s`, unchanged.
- Unique values across the whole window remain exactly
  `{4.169275, 64.720732}` for movie-materialise and
  `{0.011467, 0.015307}` for episode-materialise — the same two
  historical values ever recorded, no new push in between.

**This is now the FIFTH consecutive daily analytical pass reading the
exact same stale sample.** Per the task's explicit fallback instruction —
"if still stale, note that explicitly and check on
`ttfb-daily-rig-gitea-mirror-wiring`'s progress rather than re-deriving
the same ranking a fifth time" — this pass does NOT re-derive the stage
ranking; it is carried forward unchanged from `-004`.

### Ranking (carried forward — data unchanged)

1. **movie-materialise = 4.169275s** — still by far the dominant stage,
   two orders of magnitude above every other movie/episode flow.
2. movie-get_sources = 0.315222s
3. episode-get_sources = 0.181446s
4. episode-materialise = 0.015307s (already effectively warm/instant)

### Cold-materialise failure rate — 100% (unchanged)

`phantom_loadtime_errors_total{flow="materialise"} = 1` with
`runs_total = 1` for both movie and episode item types, identical to
every prior pass. The `4.169275s` movie figure remains a FAILED cold
materialise, not a successful first byte.

## Checking on `ttfb-daily-rig-gitea-mirror-wiring`'s progress

`ttfb-daily-rig-gitea-mirror-wiring` is `[DONE]` in `PLAN.md` — its
committable portion (sync script + regression test +
`docs/ci-gitea-actions.md`) landed. Its residual operator-only remainder
was correctly split out as a separate task,
`gitea-mirror-provision-wiring`, which remains `[NEEDS-HUMAN]`
(`category=external-permission`): creating the Gitea-hosted mirror repo,
enabling Gitea Actions, registering the self-hosted runner, and
populating the workflow's secrets/vars all require Gitea-instance admin
access the swarm does not hold. There is no further swarm-side action
available on that path — it is a standing operator blocker, not a
buildable prerequisite (confirmed: creating a git hosting repo + granting
Actions/secrets admin on a third-party instance is not something the
swarm can provision for itself). This pass does not re-file it; it is
already correctly scoped and waiting on the operator.

Both open questions (`ttfb-magnet-cache-drain-worker`'s live impact on
opportunistic cold-materialise TTFB, and `ttfb-fast-indexer-early-return`'s
live impact on movie-materialise duration) remain unanswerable for lack
of fresh data, deferred again to whichever future `ttfb-reduction-00N`
pass first observes a sample distinct from `1789439787` / `4.169275s`.

## Enqueued fix (biggest remaining win the swarm CAN act on right now)

`ttfb-fast-indexer-early-return-enable-default` (ROI P9): the
`FastIndexerEarlyReturnEnabled` early-return race in
`MagnetSelector.AwaitFanOutAsync` already exists, is fully config-gated
(byte-for-byte unchanged full-wait behavior when off), and already has
partial unit coverage, but ships **default-disabled**
(`PluginConfiguration.cs:151`) and has been confirmed not enabled
anywhere in the deployed configuration across four prior passes. Unlike
the Mimir/Gitea-mirror gap, flipping this default and extending its test
coverage requires no operator secret or external permission — it is a
pure code change plus a rollout, both within swarm authority — and it
targets exactly the stage every pass to date has ranked dominant
(`movie-materialise`). See
`docs/tasks/ttfb-fast-indexer-early-return-enable-default.md` for the
full design.
`Check: dotnet test` (a new/extended `MagnetSelectorTests` regression
that fails against today's `false` default and passes once flipped to
`true`).

## Successor

`ttfb-reduction-006 [TODO]` appended, held `not_before` ~= +24h (daily
cadence). It must first check whether
`gitea-mirror-provision-wiring` has landed (operator action) and a
genuinely fresh `phantom_loadtime_seconds` sample has appeared (different
timestamp/value than this pass's `1789439787` / `4.169275s`) before
attempting the drain-worker / early-return live-impact assessment, and
should separately verify whether `ttfb-fast-indexer-early-return-enable-
default` has landed and rolled out.

## Non-goals of this pass

No fix code shipped in THIS pass (`check=none`, DIAGNOSE-AND-ENQUEUE
ONLY — `ttfb-fast-indexer-early-return-enable-default` is filed for a
future work pass to implement). No attempt was made to manually trigger
an out-of-band rig run as a substitute for the still-stalled daily
pipeline.
