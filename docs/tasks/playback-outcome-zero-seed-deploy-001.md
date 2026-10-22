# playback-outcome-zero-seed-deploy-001

## Corrected scope

This replaces the original live-color proposal. Seed code is merged, but empty
Mimir outcomes do NOT prove it is absent from the running plugin. Deployment
lag is a hypothesis until the loaded assembly and exporter are attributed.

1. Resolve the active color from live Ingress apex-host ownership, not DNS,
   comments, pod age, or a remembered color. Fail closed on ambiguity. Only the
   other, inactive/dev color is eligible for routine deployment. Recheck this
   authority immediately before any pin mutation; never change active/prod
   digests, route ownership, or request promotion.
2. Establish provenance: identify the intended dev process/container and loaded
   plugin assembly revision/hash (not just a file on a mounted volume); map the
   assembly to a reproducible build containing the seed change. Map that same
   process's OTLP resource instance through the collector to the Mimir labels.
   SDK version, pod start time, image age, and an arbitrary `target_info` instance
   alone are insufficient. If that mapping is unavailable, establish attribution
   first; do not choose the one instance that happens to make the query pass.
3. If attributed runtime evidence shows deployment is needed, build/publish via
   the existing pipeline and update only the freshly resolved inactive pin in
   the owning GitOps repo. Use an existing owning task or file a real prerequisite
   there if missing. If the seed build is already loaded, investigate the export
   path instead of blindly deploying. Record provenance and rollout evidence.
4. Query the intended instance at the configured Mimir endpoint. A successful
   response must contain exactly one sample for EACH of the 32 canonical tuples,
   with nonnegative finite counters, actual sample timestamps no older than
   120 seconds, and untouched tuples present at zero. Existing attempt counters
   may be positive and must not be reset to manufacture an all-zero result.

## Acceptance contract

Use `curl` plus response assertions (the hive CHECKS.md `endpoint` framework).
The concrete command and site-specific runtime inputs live in the hive design
doc, not this repository. An unset verified exporter identity must fail closed.
Use instant samples filtered with `time() - timestamp(metric) < 120` (also reject
future sample timestamps); the instant API evaluation timestamp alone does not
prove freshness. Never substitute `max_over_time` for this acceptance query.

Compare sorted `(flow,item_type,cause)` tuples to the exact Cartesian product:

- Flows: `materialise_then_play`, `play_already_materialised`.
- Item types: `movie`, `episode`.
- Causes: `success`, `availability_abstain`, `no_candidate`, `magnet_dead_stale`,
  `gostream_register_fail`, `gostream_cannot_fetch`, `first_byte_timeout`,
  `plugin_host_error`.

Require length 32 AND exact tuple equality, not distinct-count alone or global
`count >= 32`. Reject duplicates, missing/unknown tuples, stale samples, empty
or failed API responses, and samples from any other instance. At least one
untouched zero must be observable in the controlled dev verification; record
which tuples were untouched before any controlled attempts. If all tuples have
traffic, establish a controlled dev verification window rather than resetting
counters. Dev/rig validation is not evidence of organic production traffic.

Not in scope: changing seeding code speculatively, active-color deployment,
promotion, or implementing this fix inside the daily analytical pass.
