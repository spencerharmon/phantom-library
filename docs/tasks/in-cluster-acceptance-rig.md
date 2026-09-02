# in-cluster-acceptance-rig — P3 Stage 5 design

## What this proves that the other rigs don't

Every prior rig (`live-rig`, `gitea-live-rig-job`, `m14-per-user-rig`) proves
the plugin correct against a **throwaway Jellyfin the rig itself stands up**
on `:18096`. That proves the code; it says nothing about whether the actual
Flux-deployed `phantom-library-bluegreen-deploy` stack on the real cluster
works — the real ingress/TLS, the real co-located gostream FUSE mount, the
real shared-Postgres plugin state. This task closes that gap: it is the
deploy's own acceptance proof, and the precondition the flux host→cluster
prod migration validates against before any prod CNAME flip.

## Topology assumptions (2026-08-25 cutover)

Both colors now share ONE Postgres instance for BOTH the Jellyfin core DB
(`jellyfin_dev`) and the plugin's own tables (`phantom_dev`,
`DatabaseType=PLUGIN_PROVIDER`) — the per-color `phantom.db`/`jellyfin.db`
sqlite files this task originally targeted no longer exist on the deployed
Pod. Every DB-facing assertion here reads Postgres via `to_regclass` /
`psql`, never sqlite.

## What the rig asserts, live, against the deployed dev color

1. **Rollout Ready** — `kubectl rollout status`.
2. **HTTPS + cert SAN** — the dev host serves a valid cert covering it.
3. **gostream FUSE co-location** — `$GOSTREAM_MOUNT_PATH` is a live
   `mountpoint` inside the SAME Pod's `jellyfin` container (proves the
   single-mount-namespace consolidation, not just that gostream is running
   somewhere).
4. **Shared-Postgres plugin schema** — `phantom_dev.user_hidden_items` /
   `user_prefs` resolve via `to_regclass` (proves the PLUGIN_PROVIDER
   topology, not the retired sqlite path).
5. **Admin API reachability** — either a provided token, or a rig-minted,
   rig-owned `ApiKeys` row (see `tools/ci/provision-in-cluster-rig-creds.md`
   "Why a DB-minted API key"), torn down unconditionally at exit.
6. **Scenario 35 parity (movie e2e) + REQ-M14-PER-USER show/hide** — against
   a REAL catalog movie (never a fixture tmdb id, which does not exist on
   the deployed stack's real library): two rig-only non-admin users prove
   per-user hide/unhide isolation (A hides → invisible to A, still visible
   to B; unhide restores), and `PlaybackInfo` resolves live for the real
   item.
7. **Scenario 36 parity (TV e2e)** — a real series from the deployed
   `Phantom Shows` channel drills to >=1 season/episode child live.

## Environment safety

- Refuses outright if `PHANTOM_INCLUSTER_DEV_HOST` equals
  `PHANTOM_INCLUSTER_PROD_HOST` (misconfiguration guard).
- Resolves which color (`blue`/`green`) is "dev" LIVE from the Ingress Host
  rules (per `GATES.md` THE AUTHORITY) — never a cached/CNAME/comment guess —
  and additionally refuses if the resolved color ALSO carries the apex/prod
  host.
- Every mutation (rig API key, rig users, one transient hidden-item row) is
  rig-owned and deleted by an unconditional `EXIT` trap.
- Never writes to `phantom`/`jellyfin` (the prod logical DBs) — only
  `phantom_dev`/`jellyfin_dev` via the dev-color Pod's own already-injected
  connection env.

## Files

- `tools/ci/in-cluster-acceptance-run.sh` — the rig itself (see its header
  for full knob/scenario documentation).
- `tools/ci/provision-in-cluster-rig-creds.md` — CI credential provisioning
  recipe; explains why all four values are swarm-obtainable, not
  operator-only secrets.
- `.gitea/workflows/in-cluster-acceptance-rig.yaml` — self-hosted Gitea
  Actions workflow (`workflow_dispatch` + push to main).
- `scripts/tests/in-cluster-acceptance-rig.test.sh` — the task `Check:`, a
  static/structural harness (`script-test` framework) proving the
  workflow/script shape is sound and running a toolchain-agnostic dry run;
  it does NOT itself drive the cluster (see the change doc for the actual
  LIVE run evidence, executed directly from the honeybee sandbox, which
  proved to have real in-cluster `kubectl` reach).
