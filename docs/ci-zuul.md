# CI on self-hosted Zuul — non-rig gate

Phantom Library's build/test CI runs on the swarm's self-hosted **Zuul**
(deployed into k3s by the `flux` component), not GitHub Actions. This document
covers the **non-rig gate** — build + unit tests. The heavier live-Jellyfin
**rig** job (integration scenarios under `tools/rig-scenarios/`) is a separate,
later job and is intentionally out of scope here.

## What lands in this repo

| Path | Role |
| --- | --- |
| `zuul.d/jobs.yaml` | Defines `phantom-library-nonrig-build-test`. |
| `zuul.d/project.yaml` | Attaches that job to the tenant's `check` and `gate` pipelines. |
| `playbooks/phantom-library-nonrig-build-test.yaml` | Job body: honest node guard + build/test. |
| `playbooks/phantom-library-nonrig-cleanup.yaml` | `post-run` guaranteed executor cleanup. |
| `tools/ci/nonrig-build-test.sh` | The actual restore-Jellyfin + `dotnet build`/`dotnet test` + cleanup logic. |
| `tools/ci/lib-cleanup.sh` | Shared "no reusable build servers / no leftover processes" cleanup. |
| `tools/ci/nonrig-cleanup.sh` | Thin post-run/manual cleanup wrapper. |
| `tools/ci/verify-zuul-config.py` | In-repo regression check + local reproduce (lint + dry run). |
| `.yamllint` | yamllint config for `zuul.d/`, `playbooks/`. |

## Untrusted-project rules (why the config looks the way it does)

phantom-library is an **untrusted project** against flux's Zuul
config-project. It may only **attach jobs to pipelines flux already defines**
(`check` / `gate`); it must **never define its own `pipeline:`**, and
`project.yaml` omits `name:` so it defaults to this repository. This mirrors
the proven `beehive` (`zuul.d/{jobs,project}.yaml` + `playbooks/`) and
`gostream` (`release-verify` shape) precedents.

## The build depends on a patched Jellyfin source tree

The plugin and test csprojs `ProjectReference` `../../jellyfin/*` — the
additive `IChannelItemRefresh{,Manager}` patches under
`scripts/jellyfin-patches/`. A bare `dotnet build` therefore fails without a
patched `jellyfin/` checkout (this is exactly what `install.sh --build`
prepares before building). `tools/ci/nonrig-build-test.sh` reproduces that
prerequisite deterministically:

1. Clone Jellyfin at the pinned tag (**source of truth:
   `scripts/jellyfin-patches/REBASE.md`**, currently `v10.11.9` / base
   `e83a7e62f2`) into `jellyfin/` (git-ignored, throwaway on CI).
2. `git apply` `scripts/jellyfin-patches/*.patch` in order (mirrors
   `install.sh`).
3. `dotnet build -c Release`, then `dotnet test`.

The pinned tag is not duplicated as a constant — the script parses it from
`REBASE.md`, and `verify-zuul-config.py`/`REBASE.md` keep it single-sourced.

## Mandatory build/test process-cleanup contract

.NET leaves long-lived helpers behind (`VBCSCompiler`, persistent `MSBuild`
nodes, `testhost`) that leak file locks/state across runs on a reused executor.
The gate disables them at the source and verifies none survive:

- `MSBUILDDISABLENODEREUSE=1` (env) — no persistent MSBuild nodes.
- `-p:UseSharedCompilation=false` (on every `dotnet build`/`dotnet test`) — no
  Roslyn shared-compilation server.
- An `EXIT` **cleanup trap** runs `dotnet build-server shutdown` and
  **verifies** no `dotnet`/`testhost`/`VBCSCompiler`/`MSBuild` process
  survives; in strict mode (default) a survivor fails the job.
- The `post-run` playbook re-runs the cleanup as a guaranteed hook even when
  the main run failed.

## Honest red, never a silent green (no Nodepool yet)

The tenant has **no Nodepool build-node provider yet**, so there is no node
label to request. Rather than hardcode a nodeset the tenant cannot satisfy (or
let a `hosts: all` play run zero tasks against an empty inventory and report a
false green), the run playbook opens with a **localhost guard** that
`fail:`s when `groups['all']` is empty. Until flux provides a Nodepool, the
gate is honestly **red** on a real trigger; once a provider exists, add a
`nodeset:` to `zuul.d/jobs.yaml` and the guard passes through to the build.

## Cross-repo prerequisite (flux side — not this repo)

For these jobs to load and run in the deployed tenant, **flux must register
`spencerharmon/phantom-library` under `untrusted-projects`** in
`infrastructure/zuul/tenant-config.yaml` (it currently lists only
`spencerharmon/beehive`). That is a flux-side change, sequenced through the
authorized `phantom-library` <-> `flux` submodule link. This repo's task only
authors + locally reproduces the config.

## Reproduce locally

Toolchain-agnostic (needs only `python3` + PyYAML — no .NET, no Zuul):

```bash
python3 tools/ci/verify-zuul-config.py     # structural lint + dry run
yamllint -c .yamllint zuul.d playbooks     # if yamllint is installed
zuul --check-config ...                    # if a Zuul CLI is available
```

`verify-zuul-config.py` parses every `zuul.d/`/`playbooks/` file, enforces the
untrusted-project rules and the cleanup contract, `bash -n`-checks the scripts,
and runs `nonrig-build-test.sh` in **dry-run** mode (`PHANTOM_CI_DRYRUN=1`) so
the control flow and cleanup trap are exercised with no toolchain present. Run
it after touching any CI file so the config cannot silently rot.

A real end-to-end build (needs the .NET 9 SDK + git + network to clone
Jellyfin) is just:

```bash
./tools/ci/nonrig-build-test.sh
```

## GitHub Actions

`.github/workflows/*` are **left in place** — removing GHA is the separate
`zuul-cutover` task, done only once the Zuul path is live (which itself needs
the flux Nodepool + tenant onboarding above).
