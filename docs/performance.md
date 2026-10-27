# Phantom Library performance operations

## Cold-start thundering stampede

After a schema wipe, Phantom has no local catalogue, no TMDB metadata cache,
no availability rows, and no Jellyfin channel cache. Triggering discovery can
otherwise create a thundering stampede:

- many TMDB Discover pages fetched/deserialised;
- many catalogue/metadata/availability rows written;
- series expansion and availability workers start finding due rows;
- Jellyfin UI requests channels while caches are cold.

The intended mitigation is incremental discovery plus availability-gated
visibility. Discovery is allowed to know about a large TMDB catalogue, but
Jellyfin should only see rows that are playable or already materialised.

## Discovery throttles

Plugin settings:

- `DiscoverPagesPerRun` — maximum `/discover` pages per kind per Discovery
  task run. Default `50`. `0` means walk all pages up to TMDB's page-500
  limit in one run.
- `DiscoverPageDelayMilliseconds` — delay between page fetch/write batches.
  Default `100`.

Discovery stores cursors in `plugin_meta`:

- `discovery.cursor.movie`
- `discovery.cursor.series`

Each scheduled/manual run resumes from the cursor. This preserves eventual
full-catalogue coverage without forcing all pages into one post-wipe burst.

Recommended initial post-wipe defaults:

```text
DiscoverPagesPerRun = 50
DiscoverPageDelayMilliseconds = 100
AvailabilityProbeMinIntervalSeconds = 4
AvailabilityProbeMaxIntervalSeconds = 28
AvailabilityMaxBatchSize = 1
```

If Jellyfin remains sluggish during cold start, lower pages per run to `10`
and increase page delay to `250`–`500` ms. If the server is idle and you want
faster fill, raise pages per run or set it to `0` temporarily.

## Availability worker throttles

Plugin settings:

- `AvailabilityProbeEnabled`
- `AvailabilityProbeMinIntervalSeconds`
- `AvailabilityProbeMaxIntervalSeconds`
- `AvailabilityMaxBatchSize`
- `AvailabilityAvailableTtlDays`
- `AvailabilityUnavailableTtlDays`
- `AvailabilityTransientRetryMinutes`
- `AvailabilityLeaseMinutes`

Available stale rows remain visible while due for recheck. Transient source
failures preserve prior state and retry later; only definitive no-candidate
results hide phantoms.

## Prometheus metrics

Jellyfin already exposes Prometheus metrics when server config
`EnableMetrics` is true. Phantom metrics are registered with the same
`prometheus-net` default registry, so they appear on Jellyfin's normal
`/metrics` endpoint when metrics are enabled.

Current Phantom metrics:

```text
phantom_discovery_runs_total
phantom_discovery_pages_total{kind,cache}
phantom_discovery_rows_total{kind,result}
phantom_discovery_cursor_page{kind}
phantom_discovery_run_seconds_bucket/sum/count
phantom_availability_probes_total{type,outcome}
phantom_availability_probe_seconds_bucket/sum/count
phantom_series_expansions_total{outcome}
```

Useful queries:

```promql
rate(phantom_discovery_pages_total[5m])
rate(phantom_discovery_rows_total{result="inserted"}[5m])
phantom_discovery_cursor_page
rate(phantom_availability_probes_total[10m])
histogram_quantile(0.95, rate(phantom_availability_probe_seconds_bucket[10m]))
```

## Daily load-time threshold state

The daily CronJob and Gitea Actions trigger use `tools/ci/loadtime-daily-run.sh`.
Live runs require an explicit `PHANTOM_LOADTIME_THRESHOLDS_PATH` pointing at a
durable threshold registry, not the checkout's `tools/perf/loadtime-thresholds.json`.
The tracked file is a seed, not the daily run's mutable state: replacing a Pod
or checking out source must not reset the accumulated ratchet history.

The chart remains disabled by default. When `loadtimeDaily.enabled=true`,
`loadtimeDaily.thresholds.existingClaim` is required. The chart mounts that
existing PVC as `thresholds-state` at `loadtimeDaily.thresholds.mountPath`
(default `/var/lib/phantom-loadtime`) and sets
`PHANTOM_LOADTIME_THRESHOLDS_PATH` to `<mountPath>/loadtime-thresholds.json`.
The mount must be an absolute directory outside `/src`, and cannot be `/`;
the template normalizes it before validation. The `/src` checkout remains an
`emptyDir`, mounted separately, and the checkout init container does not mount
the threshold volume. The chart neither creates the claim nor seeds its data.

The daily Python helper serializes state access with a sidecar file lock
(`flock`), validates the seeded schema, and rejects corrupt or unwritable state
rather than silently resetting it. A concurrent writer fails visibly instead
of waiting or overwriting another run. Ratchet saves are atomic; the daily
helper also fsyncs the parent directory before reporting success. Dry runs do not
write state and are not proof that the live volume is usable.

### Retained breach detail

Guard exit 3 adds three gauges per breached `(flow, item_type)` to the same
`job="phantom-loadtime-daily-run"` Pushgateway group as the cumulative stage
outcomes: `phantom_loadtime_guard_breach` (1),
`phantom_loadtime_guard_observed_seconds`, and
`phantom_loadtime_guard_ceiling_seconds`. Values come from the ratchet's
structured decision, before optional task filing, including when `--no-file`
is used because the Job image has no beehive CLI. No thresholds are loosened.

`loadtime-guard.sh --breach-out <file>` writes the JSON filing plan on exit 3
and an empty `{"breach_count":0,"entries":[]}` on exit 0. Output is only valid
on those exits. The wrapper converts milliseconds to seconds and PUTs the
entire stage group. A guard=0 PUT contains no breach samples, removing stale
series rather than accumulating past failures. Earlier stage PUTs also replace
the previous run's group: absence alone is not proof of health. Check the
run-start timestamp and require guard exit 0; a missing guard or exit 2 is not
a passing result. Transport warnings retain their existing non-fatal behavior.

### Infrastructure activation (separate from source delivery)

1. Provision an existing persistent claim in the CronJob's namespace. Choose
   storage whose retention, access mode, scheduling, advisory `flock` semantics,
   and atomic rename support suit every writer. Set filesystem ownership and
   permissions for the actual Job and Actions containers. Use the same filesystem
   UID for all daily writers (including Actions); initialization is owner-only,
   and replacement files inherit the writer's ownership, not prior ACLs. They need
   registry read/write access and directory access to create the sidecar lock
   and temporary files and atomically replace the registry. Workload Pod
   security settings do not automatically apply to the daily Job.
2. An absent registry initializes automatically from the reviewed
   `tools/perf/loadtime-thresholds.json` on the first live guard. Preserve an
   existing ratcheted registry; do not
   re-copy the seed on each checkout or Job. Back up durable history and repair
   corrupt state deliberately, never by falling back to a fresh checkout.
3. Wire `loadtimeDaily.thresholds.existingClaim` and, if needed, `mountPath` in
   the infrastructure values alongside the existing image/config/secret wiring.
   Serialize all writers, including manual Jobs, other releases, and Actions.
   `concurrencyPolicy: Forbid` only covers scheduled Jobs of this one CronJob;
   it is not a cross-trigger lock. Every writer must use the same registry and
   sidecar lock on a filesystem that shares locks across those writers.
4. For Actions, mount persistent storage into the workflow's **job container**
   through runner/infrastructure configuration, then set the repository variable
   `PHANTOM_LOADTIME_THRESHOLDS_PATH` to its absolute registry path. It must name
   the **same registry** as the CronJob, even if mounted at a different path.
   An environment variable alone creates no mount. A disposable runner checkout,
   container writable layer, or a per-run copy/artifact is not durable shared
   state. Do not activate Actions as a second writer until this is wired.
5. Verify the actual deployed Job/Pod, not only a rendered CronJob: record its
   source revision, claim binding, `thresholds-state` mount and effective
   `PHANTOM_LOADTIME_THRESHOLDS_PATH`. Run two sequential Jobs with replacement
   Pods/checkouts and record the registry before/after each. Confirm the second
   consumes the first run's saved history/thresholds without re-seeding, and that
   guard outcomes match the persisted registry. If Actions is enabled, verify
   its job container consumes that same state as well. A source merge or Helm
   render is **not live proof** of persistence or activation.

Keep concrete claim names, storage configuration, endpoints, and credentials
in the infrastructure repository or Actions configuration, not shared source.

## Profiling

Use live profiling only when needed; it can add overhead.

```bash
# 120 seconds by default; set PID or auto-detect Jellyfin.
tools/perf/profile-live-jellyfin.sh

# custom duration/output
DURATION=300 OUT=/tmp/phantom-perf-coldstart tools/perf/profile-live-jellyfin.sh
```

Outputs:

- `ps-start.txt` / `ps-end.txt`
- `dotnet-counters.txt`
- `trace.nettrace` if `dotnet-trace` is installed
- `dotnet-trace.log`

For rig profiling, use:

```bash
tools/perf/profile-rig-discovery.sh
tools/perf/profile-rig-channel-browse.sh
tools/perf/profile-rig-materialise.sh
```

## Known bug: materialised episode disappears/flickers during materialise

During episode materialisation, Jellyfin can briefly route season/episode
browse through stale channel `BaseItem` children instead of the freshly
synthesised channel response. Symptoms:

- the episode being materialised disappears and reappears after refresh;
- an empty or duplicate season folder appears;
- sibling phantom episodes show stale splash-backed `BaseItem` rows.

Observed with Spider-Noir S01E01 after a wipe: `materialised_state` and the
real gostream file existed, and the Jellyfin `BaseItems` row eventually pointed
at the correct gostream path, but UI browse was temporarily inconsistent while
channel caches and parent/child `BaseItem` rows converged.

Current workaround: refresh the page after materialise completes. Planned fix:
make materialise update parent series/season/item channel rows atomically from
Jellyfin's perspective, and add a channel cleanup/repair pass that removes stale
splash child rows that are no longer visible under availability gating.

## Future work

- Add DB backlog gauges for due availability and due series expansion counts.
- Make availability cadence adaptive from backlog/TTL math instead of simple
  min/max delay.
- Split series expansion into bounded season batches for very large shows.
- Replace per-episode materialised lookups during season browse with one bulk
  query.
- Add direct channel-id mapping cache for badge state fallback.
