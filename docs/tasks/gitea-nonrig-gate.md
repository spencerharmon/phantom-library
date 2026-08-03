# Gitea Actions non-rig gate

Adds a **Gitea-native** non-rig build/unit-test gate, replacing the
obsolete Zuul `zuul-nonrig-gate` for this repo's CI-migration effort. See
`docs/ci-zuul.md` for the (now-superseded) Zuul-based version of this same
gate; that doc remains accurate for the Zuul job definitions still present
in `zuul.d/` — retiring those is the separate `gitea-cutover` task, not this
one. GHA (`.github/workflows/`, if/when added) is likewise left untouched.

## What lands in this repo

| Path | Role |
| --- | --- |
| `.gitea/workflows/nonrig-gate.yaml` | Gitea Actions workflow: build + unit-test gate on `push`/`pull_request`. |
| `tools/ci/nonrig-build-test.sh` | Unchanged — the SAME script the Zuul job runs; the workflow calls it directly so the two CI backends can never drift apart. |
| `tools/ci/lib-cleanup.sh` | Unchanged — shared cleanup, reused as-is. |
| `scripts/tests/gitea-nonrig-workflow.test.sh` | In-repo regression harness so the workflow can't silently rot. |

## Why a container, not the bare Actions runner

The workflow declares a `container:` (`mcr.microsoft.com/dotnet/sdk:9.0.305-noble`
— net9.0 to match every csproj's `TargetFramework`, pinned to a CONCRETE tag,
never `:9.0`/`:latest`), so the job carries its own toolchain instead of
depending on whatever happens to be pre-installed on the Gitea Actions runner
host or the old Zuul/Nodepool build-node image. `git` and `sqlite3` are
installed into the container explicitly (the base SDK image ships neither),
since the Jellyfin clone step and the in-repo migration-regression pre-check
both need them.

## Delegation, not duplication

The workflow's only substantive step is:

```yaml
- name: non-rig build + test (shared script; Zuul + Gitea identical)
  run: ./tools/ci/nonrig-build-test.sh
```

All of the actual logic — restoring the pinned/patched Jellyfin source tree,
`dotnet build -c Release` / `dotnet test`, and the mandatory build/test
process-cleanup contract (`MSBUILDDISABLENODEREUSE=1`,
`-p:UseSharedCompilation=false`, an `EXIT` trap that shuts down dotnet build
servers and verifies no leftover `dotnet`/`testhost`/`VBCSCompiler`/`MSBuild`
process survives) — is unchanged from the Zuul gate; see `docs/ci-zuul.md`
for the full description. Nothing about that contract was re-implemented
here, only re-invoked.

## Local reproduction / regression harness

`scripts/tests/gitea-nonrig-workflow.test.sh` needs only `bash` + `python3`
(PyYAML if available, with a structural-grep fallback) — no dotnet SDK, no
container runtime, no network. It asserts:

- `.gitea/workflows/nonrig-gate.yaml` parses as valid YAML.
- the workflow declares a `container:` block (no host-tool dependence).
- the SDK image tag is pinned to a concrete version (rejects `latest` or a
  floating `major.minor` tag).
- the workflow invokes `tools/ci/nonrig-build-test.sh` (not a hand-rolled
  copy of its steps).
- `tools/ci/nonrig-build-test.sh` still carries the
  `MSBUILDDISABLENODEREUSE=1` / `-p:UseSharedCompilation=false` / `EXIT` trap
  cleanup contract.
- a toolchain-agnostic dry run (`PHANTOM_CI_DRYRUN=1
  tools/ci/nonrig-build-test.sh`) exits 0, proving the script's control flow
  — including the cleanup trap — is sound without a dotnet SDK on `PATH`.

Run it directly:

```sh
./scripts/tests/gitea-nonrig-workflow.test.sh
```

It is also wired in as this task's definition-of-done `Check:`.

## Not in scope here

- Retiring the Zuul `zuul.d/` job / GHA workflow — that is `gitea-cutover`.
- The live-Jellyfin **rig** job (integration scenarios) — out of scope for
  the non-rig gate on every CI backend, tracked separately (see
  `docs/ci-zuul.md`'s own scope note for the Zuul equivalent).
