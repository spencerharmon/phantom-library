# Offline Gostream OOM Acceptance

`scripts/gostream-oom-acceptance.sh` uses Python 3's standard library. It reads
retained evidence only: no network, kubectl, deployment, restart, or limit changes.
It prints one line, beginning with a verdict, and exits:

| Verdict | Exit | Meaning |
| --- | --- | --- |
| INCOMPLETE | 2 | Missing, malformed, contradictory, or insufficient evidence |
| TUNING_ABSENT | 3 | Retained pod/runtime differs from expected tuning |
| OOM_SINCE_ROLLOUT | 4 | Target container terminated OOMKilled at/after rollout; timestamp printed |
| HEADROOM_BREACH | 5 | 24h maximum RSS is at least 90% of the same container's limit |
| ACCEPTED | 0 | Tuning matches and supplied evidence covers the interval without either breach |

Malformed required inputs take precedence. With valid inputs, tuning mismatch
takes precedence over OOM, which takes precedence over headroom. Concrete negative
evidence can be reported despite incomplete historical coverage; incomplete history
can never produce ACCEPTED. This is an evidence verdict, not a workload soak test
or proof that Go's soft memory limit prevents all future OOMs.

## Invocation

```bash
./scripts/gostream-oom-acceptance.sh \
  --pods pods.json --deployment deployment.json \
  --rss rss-24h-max.json --limits limits.json \
  --expected-tuning expected.json --runtime runtime.json --coverage coverage.json \
  --now 2026-11-06T12:00:00Z
```

Run the offline regression with `./scripts/tests/gostream-oom-acceptance.test.sh`.
No service installation or restart is needed to use either script.

## Required Evidence

- `--pods`: raw `kubectl get pod -o json`, a Pod or List containing **all and only**
  the target deployment's current pods. Retain UIDs, owner references, creation
  times, specs, container statuses and lastState. Do not select only a healthy pod.
- `--deployment`: raw Deployment JSON, including generation, selector, template,
  and status. All replicas must be updated, available and ready.
- `--rss`: successful Prometheus/Mimir instant-query response for
  `max_over_time(container_memory_rss{...}[24h])`, evaluated at `--now`.
- `--limits`: successful instant-query response for the matching container memory
  limit in **bytes**. Both vectors must retain `namespace`, `pod`, `container`.
  Scope to one cluster and de-duplicate scrape replicas at query time; multiple
  series per identity are rejected. No sidecars or pause containers. Values must
  be finite, nonnegative; limits must equal the Pod and Deployment limits.
- `--now`: explicit timezone-qualified RFC3339 evaluation time, not the current
  wall clock when replaying old files. Vector evaluation times must match each
  other and be no more than 300 seconds before this time.
- `--expected-tuning`: operator/daily-pass supplied JSON describing the intended
  fix and its **actual rollout start**, never Deployment creation time or a
  container restart time. For example:

```json
{
  "container": "stream",
  "rollout_at": "2026-11-06T10:00:00Z",
  "env": {
    "GOMEMLIMIT": "2200MiB",
    "MKV_PROXY_CONCURRENCY": "6",
    "MKV_PROXY_READ_AHEAD_BUDGET": "67108864"
  },
  "config": {"master_concurrency_limit": 6, "read_ahead_budget_mb": 64}
}
```

These are illustrative tuning values, not defaults imposed by this tool. Supply
the values from the intended fix for the actual container. Env values are strings;
config values preserve JSON types. Empty env and config together are refused.

## Runtime Receipt

`--runtime` is required for config keys, startup-exported env, and env references
that raw Pod JSON cannot resolve. Supply an array with one receipt per target
process, captured from the **actual Go process environment and effective loaded
config**, not a new exec shell, Deployment desired config, or a mounted file that
the process may not have reloaded:

```json
[
  {
    "pod_uid": "pod-1",
    "container": "stream",
    "container_id": "containerd://one",
    "started_at": "2026-11-06T10:00:00Z",
    "captured_at": "2026-11-06T12:00:00Z",
    "env": {"GOMEMLIMIT": "2200MiB", "MKV_PROXY_CONCURRENCY": "6", "MKV_PROXY_READ_AHEAD_BUDGET": "67108864"},
    "config": {"master_concurrency_limit": 6, "read_ahead_budget_mb": 64}
  }
]
```

Retain only requested non-secret tuning fields. Receipts must match the current
container ID/start time and be within 300 seconds of `--now`. Literal Pod env can
be checked without a receipt when no config keys are requested; that proves only
the Kubernetes startup environment, not that a wrapper did not override it.

## Coverage Receipt

Raw current Pod JSON and 24h-max vectors cannot prove continuous historical
coverage: Kubernetes retains only the last termination, replaced pods disappear,
and a newly evaluated range query can use stale samples. For ACCEPTED, also retain
`--coverage`, derived from deployment/ReplicaSet inventory history and **raw RSS
sample timestamps**, not invented from the instant-query evaluation time:

```json
{
  "deployment_uid": "deploy-1",
  "rollout_at": "2026-11-06T10:00:00Z",
  "observed_until": "2026-11-06T12:00:00Z",
  "replicaset_owners": {"rs-1": "deploy-1"},
  "pod_uids_since_rollout": ["pod-1"],
  "rss": [
    {
      "namespace": "media", "pod": "stream-abc-xyz", "container": "stream",
      "first_sample_at": "2026-11-06T10:00:00Z",
      "last_sample_at": "2026-11-06T12:00:00Z",
      "max_gap_seconds": 60
    }
  ]
}
```

Inventory must include every pod UID serving during the interval, including
deleted pods. ReplicaSet owners come from their controller Deployment UIDs.
RSS coverage is per identity, from the later of rollout or pod creation, through
query evaluation; no edge/interior sampling gap may exceed 300 seconds. Retain
the raw inventory and sample evidence alongside this summary for audit. The
analyser trusts capture receipts as evidence; it does not fabricate or fetch them.

Conservative limitations: replaced/missing pods, more than one restart, missing
last termination after a restart, or rollout older than the 24h RSS window prevent
ACCEPTED. Do not reset the rollout timestamp to bypass these gaps. A current or
last-state OOM at/after rollout is still reported even with ten restarts (the day25
shape). A recent rollout's 24h maximum can include pre-rollout memory samples for
an old pod, but old pods first yield TUNING_ABSENT. Broader historical acceptance
needs additional historical captures, not a claim that one snapshot proves it.

The next daily measurement should retain these captures, run the analyser, and
record the verdict. Non-ACCEPTED is not permission to deploy or raise limits:
file the concrete tuning/coverage correction indicated by the evidence.
