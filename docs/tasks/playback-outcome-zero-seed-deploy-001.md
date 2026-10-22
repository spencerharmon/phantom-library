# playback-outcome-zero-seed-deploy-001

Problem: zero-seed (playback-outcome-zero-seed-001) merged to phantom-library main 2026-10-21 but the
live pods run images from 2026-09-13/22, so Mimir still returns `[]` for the instant outcome query.

Fix:
1. Build/publish `git.spencerharmon.com/images/jellyfin-phantom` from current phantom-library main
   (existing image pipeline); record the digest (`skopeo inspect`).
2. Bump the live color's image digest in the flux repo (file `flux:<id>` via `beehive task add` +
   `beehive task block` if no task owns it; precedent: flux:phantom-library-green-image-digest-bump-for-playback-outcome).
3. After rollout, confirm via Mimir that every flow×item_type×cause combination is present.

Check (endpoint): `kubectl -n monitoring exec deploy/grafana -c grafana -- curl -sf
http://mimir.monitoring.svc:8080/prometheus/api/v1/query --data-urlencode
'query=count(phantom_playback_outcome_total{job="jellyfin-phantom-library"})' | jq -e '(.data.result[0].value[1]|tonumber) >= 32'`

Not in scope: changing seeding code; blue/green cutover policy.
