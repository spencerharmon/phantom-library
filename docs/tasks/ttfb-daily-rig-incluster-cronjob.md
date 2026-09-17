# ttfb-daily-rig-incluster-cronjob

ROI Priority 9 fix, enqueued by `ttfb-reduction-006`'s analysis (SIXTH
consecutive stale-Mimir-sample pass). The daily TTFB-reduction loop has been
starved of fresh `phantom_loadtime_seconds` data since before
`ttfb-reduction-002` because the ONLY trigger for the measurement rig,
`.gitea/workflows/phantom-loadtime-daily.yaml` (`cron: '17 6 * * *'`), is a
**Gitea Actions** workflow, and no Gitea mirror of `phantom-library` yet
exists. Its provisioning (`gitea-mirror-provision-wiring`) is a standing
`[NEEDS-HUMAN]` (external-permission — Gitea-instance admin access the swarm
does not hold), so the cadence has been frozen for six passes with the exact
same sample (`movie-materialise = 4.169275s`, last bucket `1789440830`).

## Why this, not another wait on the mirror

The mirror route is real but operator-gated and has not moved in three passes.
A **buildable dependency is never a standing human gate**: the swarm already
owns everything needed to drive the rig from INSIDE the cluster, independent of
Gitea Actions. `tools/ci/loadtime-daily-run.sh` is the complete, already-shipped
daily cadence engine — it resolves the dev color live from the Ingress, measures
the six load-time flows against the deployed HTTPS host, pushes the exposition
to the Pushgateway (which forwards to Mimir), and runs the ratcheting guard. It
needs only a Kubernetes **CronJob** to fire it on schedule the same way
`deploy/helm/phantom-library/templates/jellyfin-metadata-reaper-cronjob.yaml`
already drives the metadata reaper in-cluster. This removes the Gitea-mirror
dependency from the measurement loop ENTIRELY and is the single biggest remaining
win, because a fresh sample gates ALL further TTFB analysis (drain-worker impact,
fast-indexer early-return impact — see below).

## Deliverable

1. Add a CronJob manifest under
   `deploy/helm/phantom-library/templates/` (e.g.
   `loadtime-daily-cronjob.yaml`), gated behind a values flag
   (`loadtimeDaily.enabled`, default off until reviewed), mirroring the
   metadata-reaper cronjob's structure: schedule from values
   (`loadtimeDaily.schedule`, default `17 6 * * *`), a ServiceAccount with only
   the RBAC `loadtime-daily-run.sh` needs (read Ingress + the dev/prod color
   resolution it already performs), and the image/entrypoint that runs the
   existing `tools/ci/loadtime-daily-run.sh` against the deployed dev color.
2. Supply the rig's required config (dev host, Pushgateway URL, credentials)
   from a Secret/ConfigMap wired through Helm values — NEVER bake the real
   hostname/URL into the tracked chart (infra-identifier rule); use values +
   `example.com` placeholders in defaults.
3. Preserve the script's existing prod-safety refusal (it must NOT measure the
   prod color) — the CronJob must run with the same guard the CI path uses.
4. Do NOT delete or disable `.gitea/workflows/phantom-loadtime-daily.yaml`; the
   two triggers are complementary (whichever fires produces a sample). If the
   mirror later lands, both simply push more samples.

## Definition of done

`Check: podman run --rm --entrypoint "" -v "$PWD":/chart:ro -w /chart docker.io/alpine/helm:latest helm template deploy/helm/phantom-library --set loadtimeDaily.enabled=true`
— matches the `helm-lint` framework stub in `CHECKS.md`; proves the CronJob
template renders schema-valid with the feature enabled (a chart-only change, no
plugin code, so `dotnet build`/`test` do not apply). The live effect (a genuinely
fresh Mimir sample with a NEW bucket timestamp beyond `1789440830`) is confirmed
by the NEXT analytical pass querying Mimir — this task's job is to make the rig
fire in-cluster, not to wait for the sample.

## Once this lands (unblocks the frozen questions)

The three open questions every pass 002–006 could not answer — because they all
need a fresh sample — become answerable:
- Did `ttfb-magnet-cache-drain-worker` (DONE) measurably improve cold
  materialise TTFB for opportunistically-touched items?
- Did `ttfb-fast-indexer-early-return-enable-default` (DONE — the flag flip
  landed) reduce `movie-materialise` duration once actually enabled + rolled out?
- Is `movie-materialise` still the dominant stage post-fixes?
