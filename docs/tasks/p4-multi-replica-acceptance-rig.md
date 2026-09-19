# p4-multi-replica-acceptance-rig — P4 acceptance bar (design)

Retargets/extends `in-cluster-acceptance-rig` (P3 Stage 5) at a color running **>=2**
Jellyfin+gostream replicas (StatefulSet `.spec.replicas` from
`p4-chart-multi-replica-topology`) instead of a single instance, proving Stage A
(shared Postgres) + Stage B (consolidated co-located gostream) deliver horizontal
scale WITHOUT single-writer corruption.

## What this proves that in-cluster-acceptance-rig doesn't

The P3 rig proves the deploy works with exactly one replica per color. It cannot
catch a bug that only appears under fan-out: two replicas racing a write to the
same shared Postgres row, a replica whose gostream FUSE mount silently failed to
come up, or a Service load-balancing a request to a broken replica while the
"first" one looks healthy. This rig reuses every P3 assertion and adds the
multi-replica-specific proofs.

## What the rig asserts, live, against the deployed dev color's StatefulSet

1. **Multi-replica rollout Ready** — `kubectl rollout status statefulset/...` AND
   `status.readyReplicas >= PHANTOM_MULTI_MIN_REPLICAS` (default 2) — never
   silently accepts a degraded N=1.
2. **HTTPS + cert SAN** — unchanged from P3, fronting the multi-replica Service.
3. **Per-replica gostream FUSE co-location** — `$GOSTREAM_MOUNT_PATH` is a live
   `mountpoint` inside EVERY replica Pod's `jellyfin` container, not just one.
4. **Shared-Postgres plugin schema from every replica** — `phantom_dev.
   user_hidden_items` / `user_prefs` resolve via `to_regclass` identically from
   each replica's own connection.
5. **Admin API reachability across replicas** — a rig-minted `ApiKeys` row
   (shared Postgres) authenticates on EVERY replica, not just the one that
   minted it.
6. **Cross-replica write visibility (no single-writer corruption)** — a per-user
   hide mutation issued directly against replica 0 is immediately visible/absent
   correctly when read directly against every OTHER replica's own localhost API
   — the core "no single-writer corruption" proof this task exists for.
7. **Fan-out playback + channel drill (scenario 35/36 parity)** — `PlaybackInfo`
   for a real catalog movie, and a real series' season/episode drill, both
   resolve live (200 / >=1 children) when hit directly against EVERY replica.

## Environment safety

Identical to `in-cluster-acceptance-rig`: refuses if `PHANTOM_INCLUSTER_DEV_HOST`
equals `PHANTOM_INCLUSTER_PROD_HOST`; resolves color LIVE from the Ingress; every
rig mutation is rig-owned and torn down by an unconditional `EXIT` trap; never
writes `phantom`/`jellyfin` (the prod logical DBs).

## Files

- `tools/ci/p4-multi-replica-acceptance-run.sh` — the rig itself.
- `.gitea/workflows/p4-multi-replica-acceptance-rig.yaml` — self-hosted Gitea
  Actions workflow (`workflow_dispatch` + push to main).
- `scripts/tests/p4-multi-replica-acceptance-rig.test.sh` — the task `Check:`, a
  static/structural harness proving the workflow/script shape is sound and
  running a toolchain-agnostic dry run; it does NOT itself drive the cluster.

## Status (2026-09-04)

Implemented and dry-run verified. **Not yet run live**: the deployed
`phantom-library-green` HelmRelease is still pinned to chart `2.7.1`
(single-replica Deployment) — `p4-chart-multi-replica-topology`'s StatefulSet
support (chart 2.9.3) is code-DONE but not yet deployed. See
`flux:p4-phantomlibrary-multi-replica-deploy` (filed as this task's blocking
prerequisite) for the GitOps bump that unblocks a real live run.
