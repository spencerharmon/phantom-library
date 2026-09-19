# flux-phantom-loadtime-daily-enablement (design)

**Owning submodule: `flux`** (filed cross-submodule from phantom-library
via the registered flux↔phantom-library link). Enqueued by
`ttfb-reduction-008` (eighth consecutive stale-Mimir-sample analytical
pass).

## Why

The phantom-library daily TTFB measurement loop is BUILT end-to-end but
INERT. `ttfb-daily-rig-incluster-cronjob` (DONE) shipped a values-gated
in-cluster CronJob that fires `tools/ci/loadtime-daily-run.sh` on schedule;
`ttfb-daily-rig-image-build` (DONE + verify-after-merge DONE) shipped the
real pinned rig image (`deploy/loadtime-rig/Dockerfile` +
`.gitea/workflows/build-loadtime-rig-image.yml`, publishing to
`git.spencerharmon.com/phantom-library/loadtime-rig:<tag>`). What remains
is the flux-side HelmRelease values override that actually TURNS IT ON with
real infra values — deliberately kept out of the phantom-library tracked
chart per the infra-identifier rule (real hostnames/URLs/digests/creds live
ONLY on the flux side).

Eight consecutive daily passes (`ttfb-reduction-002` … `-008`) have read
the byte-for-byte identical stale sample (`movie-materialise = 4.169275s`,
bucket ~`1789440939`) because no fresh run has ever fired. This task is the
single remaining swarm-buildable step to produce fresh data and unblock the
whole TTFB-reduction loop's open questions.

## Deliverable (flux submodule)

In the flux HelmRelease (or its values overlay) for the phantom-library
stack:

1. Set `loadtimeDaily.enabled: true`.
2. Pin `loadtimeDaily.image` to the REAL published digest
   `git.spencerharmon.com/phantom-library/loadtime-rig@sha256:<digest>`
   (never a floating tag). Resolve the digest from the image the
   `build-loadtime-rig-image.yml` workflow published.
3. Set `loadtimeDaily.repoUrl` to the real forge repo URL and `repoRef` to
   the tracked branch.
4. Provision (in flux) the `phantom-loadtime-daily-config` ConfigMap (keys
   `devHost`, `prodHost`, `pushgatewayUrl` — real values, and the script's
   prod-safety refusal requires `devHost != prodHost`) and the
   `phantom-loadtime-daily-token` Secret (Jellyfin admin ApiKey/AccessToken)
   — via the flux secret-management path (SOPS / sealed-secret / whatever
   the flux repo already uses), NEVER plaintext in a tracked file.
5. Do NOT modify the phantom-library tracked chart defaults; this is purely
   a flux-side override.

## Definition of done — `Check:` (framework: `rollout`)

The effect is "the enabled CronJob object exists in-cluster after flux
reconciles." A CronJob has no `rollout status`, so use the `rollout`
framework's `wait` alternative to assert the enabled CronJob object exists:

```
Check: kubectl -n <phantom-namespace> wait --for=create cronjob/phantom-loadtime-daily --timeout=120s
```

(If `--for=create` is unavailable on the cluster's kubectl, use
`kubectl -n <ns> wait --for=jsonpath='{.spec.suspend}'=false
cronjob/phantom-loadtime-daily --timeout=120s` — still the `rollout`
framework's `kubectl ... wait` form.)

`Verify-After-Merge:` (the real fresh-sample effect, converges only after
the CronJob next fires) — query Mimir for a `phantom_loadtime_seconds`
sample with a bucket `> 1789440939` OR a `materialise/movie` value
`!= 4.169275`:

```
Verify-After-Merge: curl -sf <mimir>/prometheus/api/v1/query \
  --data-urlencode 'query=phantom_loadtime_seconds{flow="materialise",item_type="movie"} != 4.169275'
```

This is the honest live-effect check; the runner auto-spawns it as a
successor CHECK task after DONE (the fresh sample only exists after the
CronJob's next scheduled run).

## Scope guard

DIAGNOSE-AND-ENQUEUE produced this; the fix itself is a flux WORK task.
It does NOT touch phantom-library plugin code, does NOT re-run the TTFB
ranking, and does NOT attempt the operator-gated Gitea mirror (that
remains the complementary `gitea-mirror-provision-wiring` NEEDS-HUMAN,
now off the critical path).
