# p13-parallel-indexer-search — per-indexer fan-out under a search budget

ROI Priority 13. One slow Prowlarr indexer used to set the latency of every
source search (single aggregate `GET /api/v1/search`), and the 30 s HTTP timeout
threw away results the fast indexers had already returned.

## Behaviour

- `ProwlarrClient` (`IFanOutIndexerClient`) discovers enabled **torrent**
  indexers from `GET /api/v1/indexer` (disabled + usenet skipped). The list is a
  process-wide cache (`ProwlarrIndexerDirectory`, DI singleton) keyed by base
  URL + API key, refreshed after `ProwlarrIndexerDiscoveryTtlMinutes` (default
  15), single-flighted, stale-while-error. No indexer id/name is ever configured.
- Each discovered indexer is queried via Prowlarr's per-indexer Torznab endpoint
  `GET /{id}/api?t=search&q=…&cat=2000|5000&extended=1` (API key as `X-Api-Key`
  header and `apikey` param). Query shape unchanged: movie = IMDB id ∪
  title+year (both concurrent), episode = `Title SxxEyy`. Torznab parse: magnet
  from `magneturl` attr → magnet `link`/`enclosure` → `infohash` attr → http
  link redirect; seeders/peers/size from `torznab:attr`. `<error code=100..102>`
  → auth, other `<error>` → transient.
- `MagnetSelector.ProbeCoreAsync` is a budgeted collector over one request per
  plain indexer (Torrentio) + one request per discovered Prowlarr indexer, all
  concurrent. It stops at the first of: all requests done; budget
  `IndexerSearchBudgetMs` (default 10000) elapsed; early return
  (`FastIndexerEarlyReturnEnabled`, ≥ `MinEarlyReturnCandidates` candidates
  with seeders ≥ `MinSeeders`, after `EarlyReturnMinElapsedMs`). Received
  candidates are ranked by the existing `QualityScorer` (+ episode specificity);
  aggregation order is deterministic (enabled list, then discovered list); the
  same infohash from several Prowlarr indexers is kept once.
- Requests still running at return are **not discarded**: a background
  collector (≤ `IndexerLateCollectorMaxConcurrent`, default 16; over the bound
  the late requests are cancelled + logged) waits for them, each still capped
  by `IndexerProbeTimeoutSeconds`. Each late arrival re-ranks in-budget + late
  candidates and writes the full set through `ILateIndexerResultSink` →
  `MagnetCacheLateResultWriter` → `source_candidates`
  (`source=late_indexer_result`, preset `SourcePickerPreset`, movie -1/-1
  sentinels, episode real s/e). Host shutdown disposes the singleton selector,
  cancelling every background request.
- Caller cancellation (interactive preemption, request abort) still cancels all
  requests and propagates `OperationCanceledException` while the caller waits;
  after the probe returns, the caller link is dropped so late work outlives it.
- Outcome classification is per top-level client: a Prowlarr gateway is a
  failure only if discovery failed or every discovered indexer failed. No
  candidate yet with requests still running → `IndeterminateTransient`
  (`indexer_budget_expired`), never `DefinitiveUnavailable`.
- P6 separation preserved: `ProbeAvailabilityAsync` still only runs
  availability-oracle indexers (Torrentio); Prowlarr discovery never runs there.

## Metrics (OTel 1.10.x, meter `Phantom.Indexers`, exported by `PhantomMetricsExporter`)

| instrument | type | labels |
| --- | --- | --- |
| `phantom_indexer_requests_total` | counter | `client`, `indexer`, `status` (ok/error/auth/timeout/abstain/cancelled), `phase` (in_budget/late) |
| `phantom_indexer_request_duration_ms` | histogram | same |
| `phantom_indexer_candidates_total` | counter | `client`, `indexer`, `phase` |
| `phantom_indexer_search_budget_expired_candidates` | histogram (value = candidates held at expiry) | `item_type` |

`indexer` = Torrentio or a Prowlarr indexer name discovered at runtime (bounded
by the operator's indexer set).

## Config (runtime, `PluginConfiguration`)

`IndexerSearchBudgetMs` (10000, [500,120000]), `ProwlarrIndexerDiscoveryTtlMinutes`
(15, [1,1440]), `IndexerLateCollectorMaxConcurrent` (16, [1,256]); existing
`IndexerProbeTimeoutSeconds` (per-request cap), `FastIndexerEarlyReturnEnabled`,
`MinEarlyReturnCandidates`, `EarlyReturnMinElapsedMs`, `MinSeeders`.

## Verification

`./scripts/tests/p13-parallel-indexer-search.test.sh` — real `dotnet test` of
`ParallelIndexerSearchTests` (in-process fake Prowlarr: fast / hung /
slow-past-budget / 5xx / disabled / usenet; real `ProwlarrClient`,
`MagnetSelector`, SQLite `PhantomDb`), `ProwlarrClientTests`,
`MagnetSelectorTests`. Movie and episode. Inits `jellyfin/` if absent.
