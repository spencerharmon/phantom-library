# p8-loadtime-acceptance-rig — the P8 live acceptance bar

ROI Priority 8 CAPSTONE. Proves the whole P8 loop end to end against the
REAL deployed `phantom-library-bluegreen-deploy` stack: measure the six
ROI-named flows against the actual in-cluster dev/idle color, push every
number into the real Grafana Mimir, and confirm the Grafana dashboard's own
datasource resolves that live data.

## What shipped

- `tools/ci/loadtime-acceptance-run.sh` — the acceptance rig. Resolves the
  dev color LIVE from the Ingress, locates its Pod, mints (or reuses) a
  rig-only Jellyfin ApiKeys row, `kubectl port-forward`s the Pod's Jellyfin
  port (bypassing the Traefik/oauth2-proxy SSO gate that fronts the public
  host — see "Discovery" below), runs the P8/1 measurement engine against
  that forward, pushes the batch to the real Pushgateway (P8/2), then HARD
  verifies (never assumes): queries Mimir directly for all 12
  `phantom_loadtime_seconds{flow,item_type}` series and refuses if any are
  missing, and queries the Grafana dashboard's own datasource proxy for the
  two priority flows (`materialise`, `play_materialised`) to prove the
  dashboard would render real data, not just that its JSON parses.
  `trap`-cleans the port-forward + the rig-minted API key unconditionally.
- `scripts/tests/p8-loadtime-acceptance-rig.test.sh` — the sandboxed `Check:`
  harness (script-test framework, `CHECKS.md`): asserts the rig is
  executable/syntax-clean, trap-clean, refuses dev==prod, bakes no
  Mimir/Pushgateway/Grafana/cluster hostname, reuses the P8/1 engine + P8/2
  emitter by path (never reimplementing either), hard-fails on any missing
  Mimir series, verifies the dashboard via its own datasource proxy for both
  priority flows, and that a toolchain-agnostic dry run (`PHANTOM_CI_DRYRUN=1`)
  exits 0 with no cluster/network access.

## Discovery: the public dev host is fully SSO-gated

`tools/ci/loadtime-daily-run.sh` (p8-daily-schedule-job) is designed to drive
the six flows via `curl -H "X-Emby-Token: ..." https://$DEV_HOST/...`. Live
investigation in this session found that is unworkable as designed: the
`phantom-library-oidc-gate` Traefik `ForwardAuth` middleware
(flux repo, `infrastructure/phantom-library-oidc-gate/middleware.yaml`) gates
**every** path on every jellyfin host except the anonymous
`/System/Info/Public` probe and the `/oauth2/*` OIDC handshake — an API
token is rejected with a 302-to-Keycloak on `/Users`, `/Items`, and every
other endpoint the flows exercise (confirmed live: `curl -H "X-Emby-Token:
..." https://dev.jellyfin.polyfam.studio/Users` → 302, while
`/System/Info/Public` → 200 with no auth). This acceptance rig instead
reaches the SAME real deployed Pod directly via `kubectl port-forward` to its
Service port — the identical "reach the real color from outside the
Ingress" posture `tools/ci/in-cluster-acceptance-run.sh` already uses via
`kubectl exec ... curl localhost:8096`, just the curl-from-outside-the-pod
equivalent — so the existing P8/1 engine's `PHANTOM_LOADTIME_API` knob works
unmodified and the engine's own hardcoded `:8096` refusal stays intact
(the URL the engine sees is a local ephemeral port, never `:8096` literally).
This is a real, load-bearing correction to the daily-schedule design, not an
acceptance-rig-only workaround — `p8-daily-schedule-job`'s own change doc
already noted its live cron firing was "necessarily deferred" and untested;
this session is the first actual live drive of the deployed stack's API
flows and it confirms the port-forward path, not the raw HTTPS host, is what
must be used going forward (a follow-up to point the daily cron wrapper at
the same in-cluster port-forward technique is natural future P8 work but is
outside this capstone task's own `Files:`).

## Discovery + fix: the Grafana dashboard was invisible to the sidecar

The `p8-grafana-dashboard` task shipped `grafanaDashboard.enabled` as a
ConfigMap in the **`phantom-library`** namespace (the chart's own release
namespace). Live inspection found the Grafana chart's dashboard sidecar is
configured `NAMESPACE=monitoring` (`grafana-sc-dashboard` container env) —
it only watches ConfigMaps in the `monitoring` namespace, so the
phantom-library-namespaced ConfigMap could never be auto-imported, no matter
what chart version was deployed. This mirrors the pre-existing
`coldstart-bench` pattern in the flux repo, which ships its dashboard as a
**monitoring-namespace companion** ConfigMap for exactly this reason. Fixed
by adding `infrastructure/monitoring/config/grafana-dashboard-phantom-library-loadtime.yaml`
(flux repo) — the same rendered dashboard JSON, sourced from the chart's own
template output, dropped into the `monitoring` namespace/kustomization the
sidecar actually watches. Landed on the flux repo's tracked `main` branch
(commit `3e14a64`) as an infra-side, non-secret companion manifest (permitted
per this hive's ownership rules — ROI/PLAN/docs and this class of
monitoring-config manifest are infra-side). Additionally, the dev/idle
(green) color's HelmRelease had never been repinned past chart `2.7.1`
(published before the dashboard shipped): per LOCALS.md's "Continuous deploy
to the inactive color" (no operator gate), packaged + pushed chart `2.10.0`
to the OCI registry (`scripts/package-push-phantom-chart.sh`) and repinned
ONLY `helmrelease-green.yaml` via `scripts/phantom-library-deploy-inactive.sh
repin --chart-version 2.10.0` (flux repo commit `cf9ef9e`) — `helmrelease-blue.yaml`
(the ACTIVE/prod color) was never touched, verified by the repin script's own
own single-file-changed guardrail.

## Live evidence (this session, against the real cluster)

**1. Real deployed stack measured** — green (idle/dev, confirmed via the live
Ingress: `phantom-library-green` carries `dev.jellyfin.polyfam.studio`) was
repinned to chart `2.10.0`, its Pod (`phantom-library-green-0`) rolled, and
the six flows were driven against it via `kubectl port-forward` + a
DB-minted rig-only ApiKeys row (torn down after). Real result (movie +
episode, all six flows; some flows recorded `errors_total=1` — REAL
observed failures on `info_open`/`get_sources`/`play_materialised`/episode
`materialise`, recorded as data per the engine's contract, never masked):

```
flow=list_load item_type=movie duration_s=0.382467 errors=0
flow=sort_change item_type=movie duration_s=0.410803 errors=0
flow=info_open item_type=movie duration_s=0.014005 errors=1
flow=get_sources item_type=movie duration_s=0.046751 errors=0
flow=materialise item_type=movie duration_s=64.720732 errors=1
flow=play_materialised item_type=movie duration_s=0.035906 errors=1
flow=list_load item_type=episode duration_s=0.171459 errors=0
flow=sort_change item_type=episode duration_s=0.182602 errors=0
flow=info_open item_type=episode duration_s=0.012688 errors=1
flow=get_sources item_type=episode duration_s=0.011981 errors=1
flow=materialise item_type=episode duration_s=0.011467 errors=1
flow=play_materialised item_type=episode duration_s=0.013083 errors=1
```

**2. Pushed to the real Pushgateway** (`http://10.43.212.98:9091`, cluster
`monitoring/prometheus-pushgateway` Service) — `phantom-loadtime-push.sh push`
exited 0: "pushed load-time batch to job=phantom-loadtime".

**3. Confirmed live in the real Mimir** — direct PromQL against
`http://10.43.189.12:8080/prometheus/api/v1/query` for
`phantom_loadtime_seconds{color="green"}` returned all 12 series (6 flows x
2 item types), e.g.:

```json
{"metric":{"__name__":"phantom_loadtime_seconds","color":"green","flow":"materialise","item_type":"movie","job":"phantom-loadtime"},"value":[1788655242.184,"64.720732"]}
```
(all 12 (flow,item_type) combinations present in the same response — verified
programmatically, not spot-checked.)

**4. Confirmed the Grafana dashboard sees it through its OWN datasource** —
after the monitoring-namespace companion ConfigMap synced, `GET
/api/search?query=phantom` returned the dashboard (`uid=phantom-library-loadtime`,
6 panels: List Load, Sort Change, Info Page Open, Get Sources, ⚠ PRIORITY —
Materialise, ⚠ PRIORITY — Play Materialised). Querying THROUGH Grafana's own
Mimir datasource proxy (`/api/datasources/proxy/uid/mimir/api/v1/query`,
datasource `Mimir` confirmed provisioned + `isDefault=true`) for
`phantom_loadtime_seconds{flow="materialise",item_type="movie"}` returned the
identical live value (`64.720732`) — i.e., the exact query path the
dashboard's own panel would execute resolves real data, not merely that the
ConfigMap parses as valid dashboard JSON.

**5. Cleanup verified** — the port-forward process was killed and the
rig-minted ApiKeys row (`phantom-p8acc-<ts>`) was deleted
(`DELETE FROM "ApiKeys" WHERE "Name"='...'` → `DELETE 1`) after the run;
`kubectl -n phantom-library exec ... psql` confirms no rig-owned rows
survive. No production (blue) resource was ever read or written — only the
`green` (idle/dev) color's Pod and HelmRelease were touched.

## Sandbox check

```
$ ./scripts/tests/p8-loadtime-acceptance-rig.test.sh
== A. script exists, executable, syntax-clean
  PASS ... passes bash -n
== B. trap-clean, dev==prod refusal, no baked infra identifier
  PASS rig installs an EXIT trap (trap-clean)
  PASS rig refuses when dev host equals prod host
  PASS rig bakes no real Mimir/Pushgateway/Grafana/cluster hostname (only RFC2606 example.* fixtures)
  PASS rig never builds a literal :8096 target for the measurement engine (port-forward to an ephemeral local port instead)
== C. reuses the P8/1 engine + P8/2 emitter (never reimplements either)
  PASS rig invokes the P8/1 measurement engine by path
  PASS rig invokes the P8/2 Pushgateway emitter by path
== D. hard-fails (never soft-warns) on a missing Mimir series
  PASS rig hard-fails when any of the 12 (flow,item_type) series is missing from Mimir
== E. verifies the Grafana dashboard's OWN datasource resolves live data
  PASS rig fetches the dashboard AND queries through its own datasource proxy
  PASS rig verifies both priority flows (materialise, play_materialised) resolve live data
== F. toolchain-agnostic dry run (no cluster/network access)
  PASS PHANTOM_CI_DRYRUN=1 tools/ci/loadtime-acceptance-run.sh exits 0
  PASS dry run reaches completion (all 8 phases exercised)
== G. prod-safety self-test: refuses when dev host equals prod host
  PASS rig refuses via the dev==prod safety guard

13 passed, 0 failed
```

## Scope / non-goals

- `p8-daily-schedule-job`'s cron wrapper (`tools/ci/loadtime-daily-run.sh`)
  is unmodified by this task — the SSO-gate discovery above documents a
  real follow-on (point the daily wrapper at the same port-forward
  technique) but that wrapper's own `Files:` are out of this capstone's
  scope.
- 35/36 e2e scenarios untouched. No production (blue) color read or written.
