# ttfb-reduction-008 — daily TTFB-reduction analysis

ROI Priority 9, successor of `ttfb-reduction-007`. DIAGNOSE-AND-ENQUEUE
ONLY: this is the EIGHTH consecutive daily analytical pass. `check=none` —
no fix code is shipped in this pass; the only submodule commits are this
analysis doc plus the one enqueued fix task's design doc.

## Data source

Queried Grafana Mimir's `phantom_loadtime_seconds` series directly against
the real deployed color (`color=green`), exactly as `ttfb-reduction-002`
through `-007` did:

```
kubectl -n monitoring port-forward svc/mimir 18083:8080 &
NOW=$(date +%s)
curl -s 'http://localhost:18083/prometheus/api/v1/query_range' \
    --data-urlencode 'query=phantom_loadtime_seconds{flow=~"materialise|get_sources"}' \
    --data-urlencode "start=$((NOW-1209600))" --data-urlencode "end=$NOW" \
    --data-urlencode 'step=300'
```

## Live measurement (Mimir, real deployed green color, 2026-09-19T18:2xZ)

Last-stored sample bucket timestamp `1789440939`, values:

```
flow=materialise   item_type=movie    color=green   duration_s=4.169275   npoints=2620
flow=materialise   item_type=episode  color=green   duration_s=0.015307   npoints=2620
flow=get_sources   item_type=movie    color=green   duration_s=0.315222   npoints=2620
flow=get_sources   item_type=episode  color=green   duration_s=0.181446   npoints=2620
```

An instant `query` (no range) returns an EMPTY vector — the series has no
recent (in-staleness-window) samples at all; only the historical push
survives in the range query. The last bucket `1789440939` is within
bucket-alignment distance of `ttfb-reduction-006`'s `1789440830` /
`-007`'s `1789440815` (the ~100s deltas are `step=300` window alignment on
slightly different query windows, NOT a new push). The VALUES are the
decisive signal, and they are byte-for-byte identical.

## Finding — the sample is STILL byte-for-byte unchanged (EIGHTH read)

`movie-materialise = 4.169275s` is IDENTICAL to the value
`ttfb-reduction-002` through `-007` all read. Per this task's explicit
fallback — "if STILL stale (an eighth identical read), note that
explicitly and check on the image-build tasks and the flux enablement
tasks progress rather than re-deriving the same ranking an eighth time" —
this pass does NOT re-derive the stage ranking. It is carried forward from
`-003`/`-004`/`-005`/`-006`/`-007` UNCHANGED because the underlying data is
unchanged:

1. **movie-materialise = 4.169275s** — dominant stage, two orders of
   magnitude above every other flow.
2. movie-get_sources = 0.315222s
3. episode-get_sources = 0.181446s
4. episode-materialise = 0.015307s (effectively warm/instant)

### Cold-materialise failure rate — unchanged (100% on the last real run)

Identical to `-002` through `-007`: the `4.169275s` movie figure is a
FAILED cold materialise, not a successful first byte. No fresh run has
occurred to change this.

Because the sample is stale, the THREE open questions every pass 002–008
still cannot answer remain blocked on a single thing — a genuinely fresh
sample:

- whether `ttfb-magnet-cache-drain-worker` (DONE) measurably improved cold
  materialise TTFB for opportunistically-touched items;
- whether `ttfb-fast-indexer-early-return-enable-default` (DONE — flag now
  defaults `true` in source) reduced movie-materialise duration once
  actually rolled out;
- whether movie-materialise is still the dominant stage post-fixes.

## Progress check on the measurement-unblock chain (per this task)

### `ttfb-daily-rig-image-build` — LANDED and VERIFIED

`ttfb-daily-rig-image-build` is `[DONE]`
(commits=`9f930732441be3937d3e75fc882a553273e47c50`) and its runner-spawned
`ttfb-daily-rig-image-build-verify-after-merge` is also `[DONE]`
(commits=`66c834e2d49e118f411ad5edc31f66cf46083185`). So the rig image now
exists and is pullable: `deploy/loadtime-rig/Dockerfile` +
`.gitea/workflows/build-loadtime-rig-image.yml` ship the pinned toolchain
image, publishing to `git.spencerharmon.com/phantom-library/loadtime-rig:<tag>`.

The image-build side of the chain is COMPLETE.

### The follow-up flux-owned enablement task — DOES NOT EXIST YET

Checked `submodules/flux/PLAN.md`: there is NO task filed to flip
`loadtimeDaily.enabled=true` with real deployed values. The tracked chart
default (`deploy/helm/phantom-library/values.yaml`) still ships:

- `loadtimeDaily.enabled: false` (default off, by design)
- `loadtimeDaily.image: docker.io/example/phantom-loadtime-rig:latest`
  (infra-identifier-safe placeholder — NOT the real published digest)
- `repoUrl: https://git.example.com/phantom-library/phantom-library.git`
  (placeholder)
- `configMapName: phantom-loadtime-daily-config` / `adminTokenSecretName:
  phantom-loadtime-daily-token` (referenced, provisioned out of band)

`ttfb-daily-rig-image-build`'s own scope note is explicit: "Does NOT
attempt the separate flux-owned enablement override (loadtimeDaily.enabled
=true + real ConfigMap/Secret values) — file that as a follow-up
cross-submodule task once a real image exists." A real image now exists.
That follow-up is THIS pass's enqueued fix.

### `gitea-mirror-provision-wiring` — still `[NEEDS-HUMAN]`

Unchanged, standing operator (external-permission) blocker. It is the OTHER
(complementary) route to a fresh sample, but the in-cluster CronJob route
(a) is entirely swarm-buildable and is now one enablement task away from
firing, so the mirror is no longer on the critical path.

## Root cause of the eighth stale read

The measurement loop's in-cluster route is BUILT end-to-end (CronJob
template DONE, real rig image DONE + verified) but INERT: it needs the
flux-side enablement override that flips `enabled=true`, points `image` at
the real published digest, points `repoUrl` at the real forge, and
provisions the `phantom-loadtime-daily-config` ConfigMap +
`phantom-loadtime-daily-token` Secret with real values. Until that flux
HelmRelease values override lands and reconciles, the CronJob never fires,
so no fresh `phantom_loadtime_seconds` sample is ever pushed — hence the
eighth identical read. This gap is entirely within swarm authority (a
flux-owned values override + provisioning), so it is filed as a real task,
NOT a human escalation.

## Enqueued fix (the biggest remaining, swarm-buildable win)

`flux:flux-phantom-loadtime-daily-enablement` (ROI P9, weight 9), filed in
the OWNING `flux` submodule via the registered flux↔phantom-library link:
add the flux-side values override for the phantom-library HelmRelease that
(1) sets `loadtimeDaily.enabled=true`; (2) pins `loadtimeDaily.image` to
the REAL published `git.spencerharmon.com/phantom-library/loadtime-rig`
digest (never a floating tag); (3) sets `repoUrl` to the real forge repo
URL; (4) provisions the `phantom-loadtime-daily-config` ConfigMap
(devHost/prodHost/pushgatewayUrl) and the `phantom-loadtime-daily-token`
Secret with real values (real infra identifiers live ONLY on the flux
side, never in the phantom-library tracked chart — infra-identifier rule);
(5) verifies after reconcile that the CronJob exists enabled and, on its
next fire, pushes a `phantom_loadtime_seconds` sample distinct from
`4.169275s` / bucket `> 1789440939`. Design doc (phantom-library side,
cross-referenced):
`docs/tasks/flux-phantom-loadtime-daily-enablement.md`.
`Check:` uses the `rollout` framework — `kubectl -n <ns> rollout status`
is not meaningful for a CronJob, so the check asserts the enabled CronJob
object exists post-reconcile via `kubectl ... wait` (the `rollout`
framework's `wait` alternative). See the design doc for the exact
invocation.

## Successor

`ttfb-reduction-009 [TODO]` appended, held `not_before` ~= +24h (daily
cadence, `2026-09-20T18:21:12Z`). It must check whether
`flux:flux-phantom-loadtime-daily-enablement` has landed + reconciled and
whether a genuinely fresh sample has finally appeared (bucket `> 1789440939`
/ value `!= 4.169275s`) before finally attempting the drain-worker /
early-return impact assessment. Design doc:
`docs/tasks/ttfb-reduction-009.md`.
