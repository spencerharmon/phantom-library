# P14 — player source and stream stats (rework; not yet accepted)

## API and source ownership

`GET /Plugins/PhantomLibrary/PlayerStats/Current` requires the caller's Jellyfin
user/device session. `GET /PlayerStats/{itemId}?mediaSourceId=&playSessionId=`
requires a playing user's matching item (administrators may inspect without
playing). The server alone calls gostream `/api/library/stats?hash=...` with its
library token, with a four-second timeout. No token, internal URL or file path
is returned. Explicit unknown/null fields survive Jellyfin serialization.

`Materialiser` snapshots the candidate that actually succeeded, including cached
candidates, after gostream add and the file-ready check. It passes magnet/hash,
size, seeders, indexer and release title to `InsertMaterialisedStateAsync`.
That method writes state and the snapshot in **one database transaction**.
The mutable availability recommendation (`magnet_cache`) is not a source of
playing stats. Candidate titles are consulted only at materialisation; later
candidate sweeps, availability writes and preset changes do not alter the snapshot.

Storage: existing `plugin_meta` key
`materialised-source/v1/<type>/<tmdb>/<season>/<episode>`, JSON with version,
stub/FUSE path and materialised timestamp witnesses. No schema/table/column change
or migration. Both SQLite and PostgreSQL use the existing provider transaction and
key upsert. Old binaries ignore the new key. Unknown versions, malformed JSON,
unbound legacy state or mismatched witnesses return `no_source`, never a cache
guess. A new unbound materialisation clears the snapshot; restoring an eviction's
explicit original timestamp preserves its witnessed snapshot. Removing state
makes the snapshot unreadable. At most one snapshot exists per item tuple.

Release falls back to the winning magnet's display name when no candidate title
exists. Hashes accept hex/base32 and normalize to gostream's v1 hex contract.
Movie and episode use the same service with their respective tuple sentinels.
No gostream pin change: the isolated rig uses its real-HTTP fake engine; production
must already provide the linked gostream stats endpoint.

## Web and mobile lifecycle

Embedded `playerstats.js` is served by the plugin, anonymously with no-cache.
Existing `badges.js` injects it; `install.sh` also supports its own idempotent tag.
The load guard prevents duplicate instances. Values use textContent, not HTML.
Desktop uses a compact card, mobile a full-width strip with a 44px toggle.

Visible expanded playback polls every 2–5s, at most one request in flight. Closing,
backgrounding, collapsing, non-phantom playback or a stopped server session stops
polling. A collapsed player performs only a bounded resolution probe, never a
timer; loadstart/playing can recover an initial not-playing/error response.
Concurrent start events coalesce one follow-up after an in-flight probe. Episode
transitions discard the previous display and resolve again.

## Mandatory verification

```sh
beehive submodule worktree exec phantom-library bee-p14-player-stats-overlay -- ./scripts/tests/p14-player-stats-overlay.test.sh
```

35 .NET controller/fake-gostream/SQLite tests; 19 simulated-DOM lifecycle tests;
real HTTP mock contract; then **mandatory** scenario 52. There is no live-skip
switch. Missing prerequisites or failed browser playback fail the whole command.
The old simulated-DOM `--live` path and clone/systemd scenario were removed.

Scenario 52 reuses `isolated-playback-rig.py --backend sqlite --player-stats`:
- Builds v10.11.9 Jellyfin with all repository patches and this checkout's plugin.
- Creates fresh synthetic state and owns/cleans every service; no production DB,
  global rig, systemd unit or deployment. Port 18096 collisions fail closed.
- Uses pinned, SHA256-verified official Jellyfin web 10.11.11, injecting **only**
  the badges tag, so the actual bootstrap must fetch this build's overlay.
- Logs into real Chromium/Jellyfin, clicks the real Play button, verifies live
  source/stream payloads, 2–5s cadence, desktop/mobile layout, served asset bytes
  against source, HTTP media requests, and zero polling after browser Back causes
  Jellyfin to report Stopped. Native REST playback also saves 4096 stream bytes.
- Uses 90-second generated media; the general parity rig's two-second fixtures
  were too short to test multiple stats polls.
- Retains per-run `artifacts/playback-parity-*/` build/server/plugin/web hashes,
  raw responses, stream bytes, screenshots and cleanup receipts. Synthetic browser
  console tokens are redacted. Build servers/node reuse are disabled.

Requires .NET 9, Node/npm, Python, patch and public network downloads. Playwright
is lockfile-pinned in `tools/rig-scenarios/browser`; it uses host Chromium if
available or fetches its matching browser. `PHANTOM_P14_CHROMIUM` can select a
compatible browser. Existing `PHANTOM_PARITY_{SERVER,PLUGIN,FFMPEG}` artifact
injection supports iteration; release proof should use the default candidate build.
`PHANTOM_P14_WEB_ARCHIVE` can reuse the **same hash-verified** archive offline.
See `docs/tasks/postgres-playback-parity-rig.md` for the base rig contract.

## Current gate: native episode queue prerequisite

Full default check currently **fails**, honestly. Movie browser playback passes
with live stats and zero polls after stop. Episode REST native-open, stream bytes
and stats pass, but the real web Play path first calls the all-series endpoint:

`GET /Shows/{seriesId}/Episodes?IsVirtualUnaired=false&IsMissing=false&UserId=...&Fields=Chapters,Trickplay&limit=100&startItemId={episodeId}`

It returns `200 {Items:[],TotalRecordCount:0}`. The season-scoped endpoint lists the
same materialised episode (`LocationType=FileSystem`), and the same user can stream
it. Jellyfin web shows "Unable to find a valid media source to play" before any
PlaybackInfo/player/stats request. The likely boundary is native
`Series.GetEpisodes` (SeriesPresentationUniqueKey + Season aggregation) versus
`Season.GetEpisodes`; no fixture SQL or browser queue bypass was added to hide it.
Hive prerequisite `p14-native-episode-web-queue` owns that fix and its independent
native API/browser regression. P14 remains TODO-blocked until the prerequisite
lands and the full mandatory check passes. This is not a production rollout.
