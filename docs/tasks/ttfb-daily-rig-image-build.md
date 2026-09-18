# ttfb-daily-rig-image-build

ROI Priority 9 fix, enqueued by `ttfb-reduction-007`'s analysis (SEVENTH
consecutive stale-Mimir-sample pass). `ttfb-daily-rig-incluster-cronjob`
(`[DONE]`) added the values-gated CronJob that fires
`tools/ci/loadtime-daily-run.sh` in-cluster, removing the loop's dependency on
the operator-gated Gitea mirror (`gitea-mirror-provision-wiring`,
`[NEEDS-HUMAN]`) — but it shipped `loadtimeDaily.image` defaulting to the
placeholder `docker.io/example/phantom-loadtime-rig:latest`
(`deploy/helm/phantom-library/values.yaml:401`), an infra-identifier-safe
non-image. No real image exists anywhere for this CronJob to actually run,
so it is currently inert even where enabled.

## Why this, not the flux enablement override

Two independent things must happen before the in-cluster rig produces a real
sample: (1) a real, pullable, pinned image for `loadtimeDaily.image`, and (2)
a values override (owned by the `flux` submodule's HelmRelease for
phantom-library) flipping `loadtimeDaily.enabled=true` with real
ConfigMap/Secret wiring (dev/prod host, Pushgateway URL, admin token). This
task does ONLY (1) — it is entirely within `phantom-library`'s own repo,
mirrors an already-`[DONE]` pattern (`jellyfin`'s `gitea-actions-image-workflow`,
`flux`'s `gitea-actions-image-build-workflows`), and is independently
checkable via the `image-digest` framework. Bundling (2) into the same task
would conflate a phantom-library deliverable with a flux-owned one under a
single Check, and flux enablement genuinely needs its own design (real
Secret/ConfigMap provisioning is out of phantom-library's scope). File that as
a SEPARATE follow-up (`<flux-task>`, cross-submodule dep on this one) once
this lands.

## Deliverable

1. Add `deploy/loadtime-rig/Dockerfile`: a small pinned base (e.g.
   `docker.io/library/debian:bookworm-slim` or similarly pinned, NOT `:latest`)
   installing exactly the toolchain `tools/ci/loadtime-daily-run.sh` needs —
   `bash`, `kubectl` (pinned version, matching the install step in
   `.gitea/workflows/phantom-loadtime-daily.yaml`), `curl`, `ca-certificates`,
   `openssl`, `jq`, `python3` — nothing more. No repo code is baked into the
   image (the CronJob's init container already clones the repo fresh into an
   emptyDir per run); the image is toolchain-only.
2. Add `.gitea/workflows/build-loadtime-rig-image.yml`: a tag-driven (or
   `workflow_dispatch`-triggered) Gitea Actions workflow on the self-hosted
   runner (`[self-hosted, gitea-actions-runner]`, matching `release.yaml` /
   `nonrig-gate.yaml`'s pattern) that builds `deploy/loadtime-rig/Dockerfile`
   with `buildah`/`podman` and publishes to the Gitea OCI registry as
   `git.spencerharmon.com/phantom-library/loadtime-rig:<tag>` — mirroring
   `flux`'s `gitea-actions-image-build-workflows` recipe
   (`flux/docs/runbooks/build-image-in-gitea.md`) and `jellyfin`'s
   `gitea-actions-image-workflow`. NEVER publish `:latest`; the tag is the
   git ref/short-sha.
3. Update `deploy/helm/phantom-library/values.yaml`'s `loadtimeDaily.image`
   comment to point at the new published image path (the DEFAULT value stays
   an `example.com`-safe placeholder per the infra-identifier rule — a real
   pinned digest is supplied by the flux-owned values override, not baked
   into this tracked chart).
4. Add a regression harness `scripts/tests/loadtime-rig-image-build.test.sh`
   (mirrors the existing `p8-loadtime-daily-workflow.test.sh` pattern) that
   asserts: the Dockerfile exists and installs the exact toolchain list
   above; the workflow YAML parses, runs on the self-hosted runner inside a
   pinned base image, and never references `:latest` for its own build base
   or its published tag.

## Definition of done

`Check: skopeo inspect docker://git.spencerharmon.com/phantom-library/loadtime-rig:<published-tag>`
— matches the `image-digest` framework stub in `CHECKS.md`; proves a real,
pullable, digest-addressable image was published. Because the real publish
only exists after this branch's workflow runs post-merge (the Gitea Actions
trigger fires on the tracked branch, same class of deferred effect as
`jellyfin`'s `gitea-actions-image-workflow-verify-after-merge`), this task
should carry a `Verify-After-Merge:` for the `skopeo inspect` command rather
than a pre-merge `Check:`, with the regression harness
(`./scripts/tests/loadtime-rig-image-build.test.sh`) as the pre-merge `Check:`
instead.

## Once this lands (unblocks the frozen rig)

- A follow-up, flux-owned task can then set `loadtimeDaily.enabled=true` in
  the deployed HelmRelease values with `loadtimeDaily.image` pointing at this
  task's published digest, plus the real ConfigMap (`devHost`, `prodHost`,
  `pushgatewayUrl`) and Secret (admin token) the ServiceAccount/RBAC already
  wired by `ttfb-daily-rig-incluster-cronjob` expects.
- Once that lands, the in-cluster CronJob fires independent of the Gitea
  mirror, finally producing the fresh `phantom_loadtime_seconds` sample every
  pass since `ttfb-reduction-002` has been waiting on.
