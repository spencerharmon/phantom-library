# Provisioning the in-cluster acceptance rig's CI credentials

The 2026-09-02 reopen of `in-cluster-acceptance-rig` established that all four
values this rig needs are **swarm-mintable/emittable**, not operator-only
secrets:

| Var | How it is obtained | Secret? |
|---|---|---|
| `PHANTOM_INCLUSTER_ADMIN_TOKEN` | **Not required as a pre-provisioned secret.** The rig mints its OWN rig-only Jellyfin `ApiKeys` row directly via the deployed Pod's own Postgres connection (`INSERT INTO "ApiKeys" ...` using the jellyfin container's own `POSTGRES_*` env — no new credential, no new attack surface) and deletes it again in its EXIT trap. Set this var only to skip minting (e.g. reuse an existing admin-issued key for a faster run). | rig-scoped, ephemeral |
| `PHANTOM_INCLUSTER_KUBE_CONTEXT` / kubeconfig | Emit a scoped, read+exec-only kubeconfig via `flux:scripts/emit-flip-kubeconfig.sh` (the same `zuul-flip`-pattern SA construction already used for the blue/green flip tooling), or run the workflow on a self-hosted runner that already carries an in-cluster kubeconfig (`~/.kube/config`, read-only bind). | scoped SA token |
| `PHANTOM_INCLUSTER_DEV_HOST` | Non-secret config: the current inactive/dev role's public host. Resolve LIVE (never hardcode) via `scripts/phantom-library-bluegreen-flip.sh status` in the flux submodule, or `kubectl -n <ns> get ingress phantom-library-{blue,green} -o jsonpath=...` (see phantom-library `GATES.md` "THE AUTHORITY"). | non-secret var |
| `PHANTOM_INCLUSTER_PROD_HOST` | Non-secret config: the apex/prod host, used only as the rig's safety guard (refuses to run if dev==prod). | non-secret var |
| `PHANTOM_INCLUSTER_NAMESPACE` | Non-secret config: the k8s namespace phantom-library deploys into (default `phantom-library`). | non-secret var |

## Provisioning as Gitea Actions repo secrets/vars

Use the same leak-safe bridge pattern as
`flux:scripts/provision-gitea-actions-registry-secret.sh` (values piped
through a Gitea admin-token-authenticated API call, never echoed to a log or
committed):

```
# vars (non-secret; Gitea Actions "repo variables")
gitea_api PUT /repos/<owner>/phantom-library/actions/variables/PHANTOM_INCLUSTER_DEV_HOST  '{"value":"<live dev host>"}'
gitea_api PUT /repos/<owner>/phantom-library/actions/variables/PHANTOM_INCLUSTER_PROD_HOST '{"value":"<live apex host>"}'
gitea_api PUT /repos/<owner>/phantom-library/actions/variables/PHANTOM_INCLUSTER_NAMESPACE '{"value":"phantom-library"}'

# secrets
gitea_api PUT /repos/<owner>/phantom-library/actions/secrets/PHANTOM_INCLUSTER_KUBECONFIG_B64 '{"data":"<base64 kubeconfig from emit-flip-kubeconfig.sh>"}'
```

The self-hosted runner writes `PHANTOM_INCLUSTER_KUBECONFIG_B64` to
`$KUBECONFIG` at job start (see `.gitea/workflows/in-cluster-acceptance-rig.yaml`)
so `kubectl`/the rig script authenticate without any admin token ever leaving
the cluster.

## Why a DB-minted API key, not an admin-API-minted one

Minting via Jellyfin's `POST /Users/AuthenticateByName` + an interactive
admin session requires an admin PASSWORD, which is operator-held (this
Jellyfin instance's login flow front-ends through oauth2-proxy/Keycloak SSO —
there is no local-admin password flow for CI to drive non-interactively).
The plugin's own Postgres `ApiKeys` table is a simple, well-understood
append-only credential store the deployed Jellyfin already trusts identically
to an admin-panel-minted key (same table, same auth code path) — inserting a
rig-owned row via the Pod's own already-injected `POSTGRES_*` env is
functionally the mint operation without requiring an interactive admin
session or a new standing secret. The rig deletes its row unconditionally on
exit (see the `teardown` trap in `tools/ci/in-cluster-acceptance-run.sh`).
