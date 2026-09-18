# ttfb-reduction-007 — daily TTFB-reduction analysis

ROI Priority 9, successor of `ttfb-reduction-006`. DIAGNOSE-AND-ENQUEUE ONLY:
this is the SEVENTH consecutive daily analytical pass. `check=none` — no fix
code is shipped in this pass; the only submodule commits are this analysis
doc plus the one enqueued fix task's design doc.

## Data source

Queried Grafana Mimir's `phantom_loadtime_seconds` / `phantom_loadtime_errors_total`
series directly against the real deployed color (`color=green`), exactly as
`ttfb-reduction-002` through `-006` did:

```
kubectl -n monitoring port-forward svc/mimir 18083:8080 &
NOW=$(date +%s)
curl -s 'http://localhost:18083/prometheus/api/v1/query_range' \
    --data-urlencode 'query=phantom_loadtime_seconds{flow=~"materialise|get_sources"}' \
    --data-urlencode "start=$((NOW-1209600))" --data-urlencode "end=$NOW" \
    --data-urlencode 'step=300'
```

## Live measurement (Mimir, real deployed green color, 2026-09-18T18:1xZ)

Last-pushed sample bucket timestamp `1789440815` (15s off `ttfb-reduction-006`'s
`1789440830` due to `step=300` bucket alignment on a slightly different query
window — NOT a new sample), values:

```
flow=materialise   item_type=movie    duration_s=4.169275   errors=1  runs=1
flow=materialise   item_type=episode  duration_s=0.015307   errors=1  runs=1
flow=get_sources   item_type=movie    duration_s=0.315222   errors=0  runs=1
flow=get_sources   item_type=episode  duration_s=0.181446   errors=1  runs=1
```

## Finding — the sample is STILL byte-for-byte unchanged (SEVENTH read)

`movie-materialise = 4.169275s` is IDENTICAL to the value `ttfb-reduction-002`
through `-006` all read. Per this task's explicit fallback — "if still stale
(a seventh identical read), note that explicitly and check on BOTH
`ttfb-daily-rig-incluster-cronjob`'s and `gitea-mirror-provision-wiring`'s
progress rather than re-deriving the same ranking a seventh time" — this pass
does NOT re-derive the stage ranking. It is carried forward from
`-003`/`-004`/`-005`/`-006` UNCHANGED because the underlying data is unchanged:

1. **movie-materialise = 4.169275s** — dominant stage, two orders of magnitude
   above every other flow.
2. movie-get_sources = 0.315222s
3. episode-get_sources = 0.181446s
4. episode-materialise = 0.015307s (effectively warm/instant)

### Cold-materialise failure rate — 100%

`phantom_loadtime_errors_total{flow="materialise"} = 1` with `runs_total = 1`
for BOTH movie and episode, identical to `-002` through `-006`. The
4.169275s movie figure is a FAILED cold materialise, not a successful first
byte.

## Progress check on BOTH unblock routes (per this task's instruction)

### (a) `ttfb-daily-rig-incluster-cronjob` — landed, but NOT yet producing samples

Checked `submodules/phantom-library/PLAN.md`: `ttfb-daily-rig-incluster-cronjob`
is now `[DONE]` (merged since `-006`). Read the merged chart
(`deploy/helm/phantom-library/templates/loadtime-daily-cronjob.yaml`,
`deploy/helm/phantom-library/values.yaml:386`): the CronJob template is real
and renders correctly, BUT:

- `loadtimeDaily.enabled` defaults to `false` in the tracked chart (by design —
  "default off until reviewed", per the task's own design doc
  `docs/tasks/ttfb-daily-rig-incluster-cronjob.md`). No values override
  enabling it exists anywhere in this checkout (the phantom-library repo ships
  no environment-specific values file; a production override would live in
  the `flux` submodule's HelmRelease values, which this pass's worktree does
  not have checked out as a cross-dep — dependency not yet declared).
- Even if `enabled: true` were set, `loadtimeDaily.image` defaults to the
  placeholder `docker.io/example/phantom-loadtime-rig:latest`
  (values.yaml:401) — an **infra-identifier-safe placeholder that is not a
  real pullable image**. The chart's own comment says "supply a
  purpose-built/pinned image at deploy time," but **no such image has been
  built or published anywhere in this repo** (`.gitea/workflows/` has no
  Dockerfile-based image-build workflow; only `release.yaml`'s `jprm plugin
  build` and the acceptance/migration/live rig workflows exist — none produce
  a container image shipping `bash`+`kubectl`+`curl`+`jq`+`openssl`+`python3`,
  the toolchain `tools/ci/loadtime-daily-run.sh` needs, unlike
  `.gitea/workflows/phantom-loadtime-daily.yaml`'s Gitea-Actions job which
  installs that toolchain inline into the pinned
  `mcr.microsoft.com/dotnet/sdk:9.0.305-noble` container at run time).

So route (a) is **built but inert**: the CronJob code merged, but two
things must still happen before it can fire even one real run — a real
image must exist, and a values override must flip `loadtimeDaily.enabled` to
`true` with real ConfigMap/Secret/image wiring. Neither has happened. This is
the concrete, swarm-buildable next step (see enqueued fix below).

### (b) `gitea-mirror-provision-wiring` — still `[NEEDS-HUMAN]`

Confirmed via `submodules/phantom-library/PLAN.md:1763`:
`gitea-mirror-provision-wiring [NEEDS-HUMAN] <!-- category=external-permission -->`
is unchanged since `-006`. No swarm action is available on it directly.

## Why still stale — root cause is now the missing rig IMAGE, not the mirror

The mirror route (b) remains the same standing operator blocker it has been
for four passes. But route (a), which this pass expected to be the unblocker,
is ALSO still not producing data — not because of another human gate, but
because `ttfb-daily-rig-incluster-cronjob` shipped the CronJob *template*
without a runnable image or an enablement override, exactly as its own design
doc anticipated ("default off until reviewed"). That gap is entirely within
swarm authority to close (an image build is a Gitea Actions workflow, per the
`gitea-actions-image-workflow` pattern already `[DONE]` in `jellyfin` and
`gitea-actions-image-build-workflows` already `[DONE]` in `flux`) and is this
pass's enqueued fix.

## Progress since `-006` (what DID change)

- `ttfb-daily-rig-incluster-cronjob` is now `[DONE]` (was enqueued by `-006`,
  not yet complete when `-006` ran).
- No change to `ttfb-fast-indexer-early-return-enable-default` (`[DONE]` since
  before `-006`) or `ttfb-magnet-cache-drain-worker` (`[DONE]`); their live
  impact remains UNMEASURABLE for the same reason as every prior pass — no
  fresh sample.
- `gitea-mirror-provision-wiring` unchanged, still `[NEEDS-HUMAN]`.

The three open questions every pass 002–007 could not answer (drain-worker
impact, early-return impact, whether movie-materialise is still dominant
post-fixes) ALL remain blocked on a single thing: a fresh sample — which now
concretely depends on the rig getting a real image + enablement, not the
Gitea mirror.

## Enqueued fix (the biggest remaining, swarm-buildable win)

`ttfb-daily-rig-image-build` (ROI P9, weight 9): add a Gitea Actions
workflow (mirroring the already-`[DONE]` `gitea-actions-image-workflow`
pattern in `jellyfin` / `gitea-actions-image-build-workflows` in `flux`) that
builds a small pinned container image shipping exactly the toolchain
`tools/ci/loadtime-daily-run.sh` needs (`bash`, `kubectl`, `curl`, `jq`,
`openssl`, `python3` — the same set `.gitea/workflows/phantom-loadtime-daily.yaml`
installs inline today) from a new `deploy/loadtime-rig/Dockerfile`, and
publishes it to the Gitea OCI registry as
`git.spencerharmon.com/phantom-library/loadtime-rig:<tag>` (never `:latest`).
This directly unblocks `loadtimeDaily.image`'s placeholder in
`deploy/helm/phantom-library/values.yaml:401` with a real pullable digest —
the concrete missing piece keeping the already-merged CronJob inert. It does
NOT attempt the separate `flux`-owned enablement override (setting
`loadtimeDaily.enabled=true` with real ConfigMap/Secret values in the
deployed HelmRelease) — that is a follow-up, cross-submodule task once a real
image exists to point it at; scoping both into one task would conflate a
phantom-library-owned deliverable (the image) with a flux-owned one (the
values override) in a single Check. Design doc:
`docs/tasks/ttfb-daily-rig-image-build.md`.
`Check: podman run --rm --entrypoint "" -v "$PWD":/chart:ro -w /chart docker.io/alpine/helm:latest helm lint deploy/helm/phantom-library`
is NOT this task's check (no chart change) — see the design doc for the
`image-digest` framework check this task actually uses.

## Successor

`ttfb-reduction-008 [TODO]` appended, held `not_before` ~= +24h (daily
cadence). It must check whether `ttfb-daily-rig-image-build` has landed and
whether a follow-up flux enablement task has been filed/completed, and
re-query Mimir for a genuinely fresh sample (bucket beyond `1789440830` /
value ≠ `4.169275s`) before finally attempting the drain-worker /
early-return impact assessment. Design doc: `docs/tasks/ttfb-reduction-008.md`.
