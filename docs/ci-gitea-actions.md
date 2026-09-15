# CI on self-hosted Gitea Actions

Phantom Library's CI runs on the swarm's **self-hosted Gitea Actions** runner
(flux-deployed into k3s), **not** GitHub Actions and **not** the retired Zuul
provider. The old GitHub Actions workflows (`.github/workflows/build.yml`,
`release.yml`) and the obsolete Zuul config (`zuul.d/`, `playbooks/`,
`tools/ci/verify-zuul-config.py`) were removed by the `gitea-cutover` task once
the Gitea Actions path was proven live end to end.

## The repo must exist as a Gitea repo (mirror wiring)

Gitea Actions can only trigger on a Gitea-hosted repo — `.gitea/workflows/*`
is invisible to GitHub. This repo's *only* git remote is GitHub
(`git@github.com:spencerharmon/phantom-library.git`), so a Gitea-hosted
mirror of `phantom-library` must exist (analogous to the `jellyfin` fork
submodule's dual-remote `origin` + `gitea` pattern) for ANY workflow under
`.gitea/workflows/` — including the daily `phantom-loadtime-daily.yaml`
rig — to ever actually run.

`tools/ci/gitea-mirror-sync.sh` is the sync helper: given
`PHANTOM_GITEA_REMOTE_URL` (the mirror's remote URL — never baked into this
tracked file, operator/CI-config-supplied per the infra-identifier rule) it
adds/updates a `gitea-mirror` remote and pushes the tracked branch's current
tip, idempotently (a no-op if the mirror is already at the local tip).
Regression harness: `scripts/tests/gitea-mirror-sync.test.sh` (throwaway
synthetic repos only, no network, no real Gitea instance).

Provisioning the actual Gitea-side repo (creating `phantom-library` under
`git.spencerharmon.com`, ideally as a pull-mirror from GitHub so ordinary
pushes need no dual-push discipline), enabling Gitea Actions on it,
confirming the `gitea-actions-runner` label is registered, and wiring the
workflow's already-declared secrets/vars
(`PHANTOM_INCLUSTER_KUBECONFIG_B64`, `PHANTOM_INCLUSTER_ADMIN_TOKEN`,
`PHANTOM_INCLUSTER_DEV_HOST`, `PHANTOM_INCLUSTER_PROD_HOST`,
`PHANTOM_INCLUSTER_NAMESPACE`, `PHANTOM_PUSHGATEWAY_URL`) are operator-side
Gitea-instance actions outside this repo's own tracked state — see the
`ttfb-daily-rig-gitea-mirror-wiring` change doc for the current status of
that provisioning step.

## What lands in this repo

| Path | Role |
| --- | --- |
| `.gitea/workflows/nonrig-gate.yaml` | Non-rig gate: `dotnet build -c Release` + `dotnet test` in a pinned SDK container. Replaces GHA `build.yml`. |
| `.gitea/workflows/release.yaml` | Tag-driven (`v*`) release: `jprm plugin build` package + `manifest.json` update + published release with the zip. Replaces GHA `release.yml`. |
| `.gitea/workflows/live-rig.yaml` | Live Jellyfin/gostream integration rig against the real published images. |
| `.gitea/workflows/migration-rig.yml` | P3 migration + integration live rig (v11→v12, synthetic fixture). |
| `tools/ci/nonrig-build-test.sh` | The actual restore-Jellyfin + `dotnet build`/`dotnet test` + cleanup logic (shared by the gate). |
| `tools/ci/live-rig-run.sh` | Live-rig driver (shared by `live-rig.yaml`). |
| `tools/ci/lib-cleanup.sh` / `nonrig-cleanup.sh` | Shared "no reusable build servers / no leftover processes" cleanup. |
| `.yamllint` | yamllint config for `.gitea/workflows/`. |

## Coverage map (GHA → Gitea Actions)

- GHA **`build.yml`** (build + unit test on every push/PR) → `.gitea/workflows/nonrig-gate.yaml`.
  Same build/test logic, delegated to the shared `tools/ci/nonrig-build-test.sh`,
  now run in a pinned `.NET` SDK container on the self-hosted runner.
- GHA **`release.yml`** (tag `v*`: jprm package + manifest update + release) →
  `.gitea/workflows/release.yaml`. Same jprm packaging and `manifest.json`
  update; the forge host + repo are taken from the Actions context
  (`github.server_url` / `github.repository`) rather than a hardcoded
  `github.com`, so no deployment-specific identifier is baked into the repo.

## The build depends on a patched Jellyfin source tree

The plugin and test csprojs `ProjectReference` `../../jellyfin/*` — the additive
`IChannelItemRefresh{,Manager}` patches under `scripts/jellyfin-patches/`. A bare
`dotnet build` therefore fails without a patched `jellyfin/` checkout.
`tools/ci/nonrig-build-test.sh` reproduces that prerequisite deterministically
(clone Jellyfin at the pinned `scripts/jellyfin-patches/REBASE.md` tag, `git apply`
the patches, then build + test). The pinned tag is single-sourced from
`REBASE.md`.

## Mandatory build/test process-cleanup contract

.NET leaves long-lived helpers behind (`VBCSCompiler`, persistent `MSBuild`
nodes, `testhost`) that leak file locks/state across runs on a reused runner.
The gate disables them and verifies none survive:

- `MSBUILDDISABLENODEREUSE=1` (env) — no persistent MSBuild nodes.
- `-p:UseSharedCompilation=false` (on every `dotnet build`/`dotnet test`).
- An `EXIT` **cleanup trap** runs `dotnet build-server shutdown` and **verifies**
  no `dotnet`/`testhost`/`VBCSCompiler`/`MSBuild` process survives.

## Reproduce locally

Toolchain-agnostic (needs only `python3` — no .NET, no runtime):

```bash
./scripts/tests/gitea-cutover.test.sh            # cutover regression harness
./scripts/tests/gitea-nonrig-workflow.test.sh    # non-rig gate harness
./scripts/tests/gitea-live-rig-workflow.test.sh  # live-rig harness
yamllint -c .yamllint .gitea/workflows           # if yamllint is installed
```

A real end-to-end build (needs the .NET 9 SDK + git + network to clone Jellyfin):

```bash
./tools/ci/nonrig-build-test.sh
```
