# P0 Database Load Observability

## Instruments

All instruments use the existing `Phantom.Flows` meter and OTel 1.10.0 exporter.
No new exporter, dependency upgrade, schema migration, dashboard, or scrape
deployment is included. The linked Flux task owns export/scrape and dashboard wiring.

| Prometheus name | Type | Instrument labels | Meaning |
| --- | --- | --- | --- |
| `phantom_availability_claims_total` | counter | `media_type`, `lane` | Successful committed lease acquisitions |
| `phantom_availability_completions_total` | counter | `media_type`, `lane` | Guarded committed probe completion or transient reschedule |
| `phantom_availability_backlog` | gauge | `media_type`, `state` | Cached actual SQL count, including explicit zero states |
| `phantom_availability_backlog_observed_at` | gauge | none | Unix seconds at start of last successful count |

`media_type` is movie/episode; lane is priority (claimed row priority > 0) or
background. Lane travels with the lease, never inferred from the worker's ID.
Failed claims, wrong owners, obsolete generations and duplicate completions do
not increment. Completions include abstentions/transient retries, not only available
verdicts: this is committed work throughput, not playback or availability success.
Counter emission is after SQL commit, not transactionally durable with SQL; a process
crash between commit and emission can undercount, never claim uncommitted progress.

Backlog classification uses the actual claim predicate, in this order:
leased (`lease_until >= now`), eligible (due time OR missing/changed policy), dormant
(future no-capable-indexer/unreleased backoff), scheduled (other future retry/TTL).
On this revision a policy mismatch can make a future dormant row eligible; the
metric intentionally exposes that existing scheduler behavior rather than hiding it.

Every worker timer callback requests refresh before the batch-running guard, even
when disabled, yielding to users, or waiting on a slow movie/episode probe.
Each database object attempts at most one aggregate scan per 300 seconds; failures
also consume that cadence, use a 10-second SQL timeout, and preserve the old timestamp.
Scrapes read only an immutable memory snapshot. A stopped observation loop becomes stale;
a healthy long-running probe does not prevent observation callbacks.
Counts are shared-database observations: dashboards must use max, not sum, across
replicas. The collector should retain pod identity separately from instrument labels.
Every successful refresh also emits zero increments to initialize quiet counter lanes.
The probe allows 660 seconds backlog age (cadence plus scheduling/export margin),
independently of its 120-second maximum scrape gap. The aggregate scan's own tuple
reads ARE included in P0 evidence; this task does not claim the current query plan
meets the subsequent zero-sequential-read acceptance threshold.

Suggested dashboard queries: `sum by(media_type,lane)
(increase(phantom_availability_claims_total[1h]))` and
`max by(media_type,state)(phantom_availability_backlog)` with deployment selectors,
plus observation age. Do not collapse missing series into zero with `or vector(0)`.

## Regression

The bootstrap resolves `PHANTOM_JELLYFIN_REPO`, then the configured submodule URL,
then `.gitmodules`, and fetches/verifies the exact recorded gitlink. It repairs a
clean wrong-tip checkout and refuses to overwrite dirty wrong-tip work. Both the
dedicated harness and nonrig CI use this helper. Nonrig Gitea provisions a healthy
PostgreSQL 16 service and passes its DSN to the entire unfiltered test suite;
local nonrig runs without a DSN use the same Podman fixture as this harness.
The harness also tests bootstrap on isolated repositories and validates workflow
service/DSN wiring. The shared validator still requires Bash and unconditional
Node/git/CA bootstrap before SDK-container checkout. Static runner labels remain
`k8s`/`ubuntu-latest`; `tools/ci/gitea_runner_policy.py` permits only two exact
per-workflow/job scheduling exceptions: playback's optional string dispatch
`runner_label` (required=false, default=k8s, exact `inputs.runner_label || 'k8s'`
expression) and the swarm image job's `vars.SWARM_HEALTH_RUNNER_LABEL || 'k8s'`.
The former preserves owned disposable manual runners; push/PR or an empty input
still use k8s. The latter preserves existing operator-configured image CI capacity.
Neither exception generalizes to other jobs, inputs, variables, fallback labels
or expressions, nor bypasses bootstrap, shell or PostgreSQL assertions. Six
runner-policy tests cover accepted and rejected parsed-YAML cases. The playback
and concurrent-index SDK workflows explicitly set `defaults.run.shell: bash`. Slow-worker coverage holds real PostgreSQL leases for both
media types while advancing the observation clock across three 301-second intervals.

```sh
beehive submodule worktree exec phantom-library bee-p0-probe-load-observability -- ./scripts/tests/p0-database-load-observability.test.sh
```

Requires .NET 9, Python 3 with PyYAML, Git and Podman (or a disposable PostgreSQL DSN in
`PHANTOM_TEST_POSTGRES_DSN`). The script provisions isolated PostgreSQL, compiles
against the pinned Jellyfin gitlink, and runs a real worker/DB regression with a
MeterListener. Missing DSN is never silently skipped. It covers movie/episode,
both lanes, guarded/duplicate completions, disabled/enabled worker, transient
outcomes, and cached backlog cadence. Then nine Python tests run the actual CLI
against controlled Kubernetes/Mimir HTTP responses and a read-only psql transport
double. Reset, gap, stale, partial, missing lane/media, disabled worker, no demand,
bad CPU/backlog, incomplete observation and invalid final receipts must fail.
Boundary regressions independently cover claims/completions for both media types
across both lanes: before-start-only and after-end-only increments must fail, while
observed in-window increments (including an exact end scrape) count without fractions.
Backlog regressions cover start/end excess for both media and eligible/leased states,
including an excess smaller than interpolation would detect; exact boundary scrapes
exclude unrelated outside excess.

Container storage is isolated under ignored `.p0-test-runtime/`; the script removes
its own container and shuts down build servers on exit. No live service is touched.

## Runtime Configuration

The read-only probe needs Python 3, `psql`, and either `kubectl` credentials or a
Kubernetes API endpoint in config. PostgreSQL uses standard `PGHOST`, `PGPORT`,
`PGDATABASE`, `PGUSER`, `PGPASSWORD`/`PGPASSFILE`/`PGSERVICE` variables. It forces
read-only transactions and a 15-second timeout. Use a read-only monitoring role.
HTTP header values come only from environment variable names in `headers_env`.
URLs must not contain credentials; redirects are refused. TLS verification stays on.
Evidence includes raw SQL output, raw decoded API responses, query timestamps,
deployed pod UIDs/image digests/resources, policy digest and explicit thresholds.

Example shape, with neutral identifiers only (replace all deployment facts at runtime):

```json
{
  "rollout_at": "2026-01-01T00:00:00Z",
  "mimir": {"url": "https://metrics.example.com/prometheus", "headers_env": {"Authorization": "MIMIR_AUTH"}},
  "database_name": "phantom",
  "availability_tables": [{"schema": "public", "table": "availability_items"}],
  "backlog_max": 1000,
  "backlog_growth_max": 0,
  "cpu_baseline_cores": 0.46,
  "seq_tup_read_per_hour_max": 0,
  "max_gap_seconds": 120,
  "workloads": {
    "application": {"namespace": "media", "selector": "app=phantom", "images": {"app": "example.com/phantom@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}},
    "database": {"namespace": "database", "selector": "app=postgres", "images": {"postgres": "example.com/postgres@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}}
  },
  "metrics": {
    "cpu": {"namespace": "database", "container": "postgres", "job": "cadvisor"},
    "claims": {"namespace": "media", "job": "phantom"},
    "completions": {"namespace": "media", "job": "phantom"},
    "backlog": {"namespace": "media", "job": "phantom"},
    "observed": {"namespace": "media", "job": "phantom"},
    "seq": {"namespace": "database", "datname": "phantom", "job": "postgres"}
  }
}
```

Optional `kubernetes: {"url": "https://kubernetes.example.com", "headers_env":
{"Authorization": "KUBE_AUTH"}}` replaces kubectl read calls. Header environment
values include the authentication scheme (e.g. Bearer). Config/evidence files can
contain private runtime identities; keep them outside tracked public source.
Selectors are exact match maps and must select only intended pods; per-pod coverage,
every movie/episode lane/state, and every configured availability table are checked.
CPU is PostgreSQL CPU, summed across its pods, not the plugin's CPU.

## Two Visits

After deployment and telemetry have converged, capture a baseline (this is NOT acceptance):

```sh
beehive submodule worktree exec phantom-library bee-p0-probe-load-observability -- ./scripts/tests/p0-database-load-live.test.sh --config p0.local.json --baseline artifacts/p0-baseline.json --output artifacts/p0-baseline.json --capture-baseline
```

On a later pass, after at least 24 hours plus the scrape freshness margin:

```sh
beehive submodule worktree exec phantom-library bee-p0-probe-load-observability -- ./scripts/tests/p0-database-load-live.test.sh --config p0.local.json --baseline artifacts/p0-baseline.json --output artifacts/p0-acceptance.json --evaluate
```

Output files must be new, and their parent directory must exist. Insufficient history
fails immediately; defer a later verification task rather than sleeping in-session.
Baseline must be post-rollout; all containers must predate it. Image, UID, resource,
replica or policy changes require a new baseline. The bounded observation is 24h..7d.
Original scrape timestamps are fetched through instant range selectors, NOT lookback-
filled query_range timestamps. Any uncovered per-series reset is rejected, even if
Prometheus `increase()` would otherwise compensate. Re-baseline after restarts/resets.

Acceptance requires mean CPU <= min(0.092, baseline*0.2) cores, near-zero tuple-read
growth (default exactly zero/hour), bounded eligible/leased backlog without growth,
and eligible demand plus positive claims AND completions for both movies and episodes.
Boundary policy: claims/completions count only the last minus first **actual scrape
inside the closed interval** `[start,end]`, per series before lane/replica aggregation.
An increment spanning either boundary cannot be the sole evidence of progress; it
may have happened entirely outside the window. At least two in-window scrapes are
required. This deliberately undercounts uncertain edge work. CPU and tuple-read
interval deltas retain linear boundary interpolation; counter reset/gap checks
still cover every fetched series, including its bracketing samples.

The eligible/leased backlog maximum includes all in-window samples AND both interval
boundaries. At an exact boundary scrape, use that value; otherwise use the **larger
of the two bracketing gauge values**, not their interpolated average. This is a
conservative bound on the sampled boundary, not a claim to detect unsampled spikes.
The independent backlog-growth check continues to use interpolated boundary values.
A nonzero tuple tolerance requires `seq_tolerance_evidence`, retained in the report.
No-demand or quiet stopped workers do not prove improvement. A failed report remains
useful evidence but must never be presented as a P0 pass.

## Final Right-Sizing

Flux owns right-sizing. Its receipt must name an earlier accepted pre-sizing report,
bind its SHA256, identify a Ready Kustomization at the applied revision, and give
the actual post-sizing pod resources. Example receipt shape:

```json
{
  "schema": 1,
  "pre_sizing_report": "p0-acceptance.json",
  "pre_sizing_report_sha256": "<sha256 of that report>",
  "applied_at": "<RFC3339 sizing timestamp>",
  "flux": {"namespace": "gitops", "name": "database-sizing", "revision": "<lastAppliedRevision>"},
  "resources": {
    "application": {"app": {"requests": {"cpu": "100m"}, "limits": {"cpu": "1"}}},
    "database": {"postgres": {"requests": {"cpu": "100m"}, "limits": {"cpu": "1"}}}
  }
}
```

Update only `rollout_at` in config after sizing, capture a NEW post-sizing baseline,
then run after another complete day:

```sh
beehive submodule worktree exec phantom-library bee-p0-probe-load-observability -- ./scripts/tests/p0-database-load-live.test.sh --config p0.local.json --baseline artifacts/p0-sized-baseline.json --output artifacts/p0-final.json --final artifacts/flux-sizing-receipt.json
```

Final mode re-runs all live checks, validates chronology and report digest, reads
Flux's current Ready/applied revision, compares receipt resources with live pods,
and requires database CPU requests lower than the pre-sizing binding. It never
changes resources, restarts workloads or treats the receipt alone as acceptance.
