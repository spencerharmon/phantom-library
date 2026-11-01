# Gostream Memory Scope

The chart has two distinct consumers. Do not substitute one budget for the other:

- The combined StatefulSet's `jellyfin` container receives `gostream.env`, but its
  actual limit is **`jellyfin.resources`**, shared by Go and .NET. This change does
  not increase its existing 2200 MiB Go soft budget. `gostream.resources` is not
  consumed by that template and is not a standalone-service memory limit.
- A composition can disable `components.workload` and render only the shared
  tuning ConfigMap. The standalone Deployment is then owned by the deployment
  repository, which must derive Go's budget from **that container's** applied
  limit. Adding a Helm helper here cannot change a workload Helm does not own.

## Tuning And Budget

The chart now defaults to `master_concurrency_limit=6` and
`read_ahead_budget_mb=64`. Six preserves one background slot under the engine's
five-slot foreground reserve. This caps native-pump admission, not every attached
handle's foreground cache-miss I/O. Read-ahead limits logical accounting and pump
lead, **not total allocations or per-stream RSS**. The engine's minimum recycled
pool alone may retain sixteen 16 MiB buffers (256 MiB); torrent pieces, chunk
capacity, scratch buffers and retained shards are additional Go allocations.

The linked deployment's opt-in contract uses the Downward API (`limits.memory`,
same container, divisor `1Mi`) and exports at startup:

```text
reserveMiB = max(872, ceil(containerLimitMiB / 4))
GOMEMLIMIT = (containerLimitMiB - reserveMiB) MiB
```

At the unchanged 3 GiB limit, the Go budget is 2200 MiB and reserve 872 MiB
(71.6% Go, approximately 70%, not a strict 70% ceiling). At 4 GiB it would be
3072 MiB (75%); this implements the dependency's reviewed minimum-25%-reserve
policy, not a promise of <=70% at every size. Budgets below 2200 MiB fail startup.
The deployment also enforces `MKV_PROXY_CONCURRENCY=6` and
`MKV_PROXY_READ_AHEAD_BUDGET=67108864` after merged JSON, including secret config.

`GOMEMLIMIT` is a soft runtime limit, not a hard heap or RSS ceiling. Pump buffers
are largely Go allocations already counted by that runtime; adding
`N * read_ahead_budget_mb` to it as if that were measured off-heap memory is
incorrect. No proven per-stream allocation bound exists here, so **no memory
increase is justified or made**. Lower concurrency and retained-cache targets
are mitigation, not proof that OOM is impossible.

## Regression

`scripts/tests/gostream-memory-headroom.test.sh` uses Python/PyYAML, kubectl
Kustomize and Helm (with the existing vendored Helm fallback). Set `FLUX_REPO`
to the linked deployment checkout outside a beehive pass; within a pass it is
resolved under `BEEHIVE_WORKTREE/submodules/flux/repo`. Missing dependencies fail,
never skip. No cluster access or OCI chart download is needed.

The test composes the actual opt-in Deployment with **this local chart** rendered
using the linked shared HelmRelease values. It follows the volume reference to
the rendered ConfigMap, checks JSON/env agreement, reads the actual container's
resource limit and executes its rendered budget preamble without starting a
service. It rejects static Go budgets, wrong-container Downward API references,
undersizing and the old 25/256 tuning. A separate Helm render verifies the
combined-container resource ownership instead of using its budget for gostream.

## Rollout Boundary

The linked memory overlay is opt-in and inactive. This source change does not
publish a chart, repin a release, reconcile, promote, restart, or activate it.
The shared service serves all consumers; inactive-color permission does not
authorize interrupting it. After an authorized shared rollout, verify the actual
Go process environment (not a new exec process's environment), metrics showing
six pumps/64 MiB read-ahead, and sustained concurrent movie **and** episode
playback with seeks, reconnects and background work. Record heap, RSS and the
container-specific cgroup anon/file/kernel peaks, cancellation/slot release,
restart deltas and the next daily OOM observation. These live results remain
pending; passing the offline regression does not claim a repaired running service.
