# playback-error-reduction-019: day-19 diagnostic loop

Not before 2026-10-28T05:10:00Z, approximately +24h after day 18.
DIAGNOSE-AND-ENQUEUE ONLY; check=none justified by analysis and queued designs.
The task and schedule already exist from the earlier day-18 attempt; retain
them rather than filing a duplicate. This source doc supplies the refreshed
baseline and landed-fix context to complement that existing task card.

Read `playback-error-reduction-001.md` FIRST, then day-18 analysis and this doc.
Day 18: 32 exact fresh inactive/dev zero tuples at common evaluation time,
49.986-second source age, same exporter/process identity; 30d window also
32 zeros. Historical rig 35d still contains cold episode magnet_dead_stale=2
and cold movie success=1. Serving colour unchanged and uninstrumented:
organic production traffic is unmeasured, not zero. No statistical ranking.

BASELINE FIRST: instant plus max_over_time[30d], exact fresh instance/job
coverage using timestamp(...) at the same explicit query time, live ingress
role attribution and process identity. Re-attribute loaded build/exporter
on any process change. Check serving outcome coverage; never infer no
traffic from missing instrumentation. Runtime-info remained absent.

The coverage analyser and raw-window repair have landed; day 18 ran its
72-case harness and live capture report (inactive undefined; serving
UNINSTRUMENTED). Serving exporter instance is unknown: day-18 expected pod
entry is only a missing-workload marker, not a measured OTLP identity.
Inspect progress on `playback-outcome-coverage-flow-item-breakdown-001`;
use it if landed, never duplicate any of these tasks.

Rank statistically only with attributed real volume and per-slice
denominators. Write day-19 analysis; enqueue exactly ONE distinct concrete
fix with design doc and CHECKS.md-matching Check, then day-20 TODO held
approximately +24h. No implementation, deployment, counter reset,
synthetic live traffic or production promotion.
