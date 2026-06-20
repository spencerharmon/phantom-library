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
