# Playback outcome zero seeding

ROI Priority 12 fix already filed by `playback-error-reduction-012`. This is the
originating design for that existing task, not a second fix. Day 12 observed an
empty current Mimir outcome query; restart and absence of traffic are hypotheses,
not facts established by that response. The source does establish that untouched
label combinations have no samples until seeded or recorded.

## Lifecycle contract

- Reuse the canonical 2 playback flows x 2 item types x 8 causes (success plus
  seven failure causes): exactly 32 combinations on both sinks, no new labels.
- Initialize prometheus-net children early during plugin construction so the
  first untouched scrape exposes zeros even if OTLP is disabled or not started.
- Seed synchronous OTLP counter measurements with `Add(0, tags)` AFTER the
  hosted exporter's MeterProvider has subscribed (`Build()` has returned).
  Plugin construction happens before this subscription. A one-time constructor
  seed is insufficient: synchronous measurements made before subscription are
  lost, not buffered for the future provider. Seed for each newly built provider;
  a process-global "already seeded" guard must not suppress the later seed.
- `SeedPlaybackOutcomes()` may serve both lifecycle points if idempotent. Reuse
  existing Prometheus children and add only zero on OTLP; never clear, remove,
  reset, replace, or increment existing counters during seeding. Reseeding an
  active provider preserves all counts. A new provider has its own aggregation
  lifetime; this design does not replay pre-subscription attempts.

This explicitly corrects the queued fix's known review failure: a pre-attached
listener test passed while constructor-time Add(0) disappeared before the real
hosted provider subscribed. Build on that task's rework, do not duplicate it.

## Acceptance

1. Exercise actual startup order in an isolated test: construct the real plugin
   before starting `PhantomMetricsExporter`, without recording playback. Scrape
   real Prometheus text exposition BEFORE exporter start and assert exactly all
   32 label combinations with value zero. Assertions must only read exposition,
   never call `WithLabels` or otherwise create the children they assert exist.
2. Start the real exporter with an SDK reader on its actual provider, collect
   cumulative metric points, and assert exactly the same 32 zero combinations.
   A helper call under a pre-attached MeterListener alone is not sufficient.
   Removing the post-subscription seed must fail this collection assertion.
3. Reseed before and after recording an attempt. In both actual SDK collection
   and untouched Prometheus exposition, assert the selected series increments
   exactly once and stays unchanged on reseed; all other series remain zero.
   Exercise both flows and movie/episode parity (all four target combinations),
   inspecting all 32 combinations in every snapshot.
4. Isolate static metric state and restore it after each case so prior tests
   cannot supply missing children or measurements. Removing early initialization
   must fail the pre-exporter exposition test. Record red/green evidence in the
   fix's own change doc and run the full registered framework below.

Check: dotnet test

Matches the hive CHECKS.md `dotnet-test` framework. These tests prove local
startup/collection behavior, not network delivery. Source merge does not imply
the plugin has been deployed or loaded, and a zero-filled local scrape does not
prove Mimir ingestion. The daily successor must separately establish deployed
version/instance and export freshness before interpreting remote series. Even
32 remotely visible zeros provide no nonzero denominator for an error rate.
