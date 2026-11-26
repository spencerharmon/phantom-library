# p14-player-stats-overlay — in-player source + stream-health overlay (ROI P14)

## Contract

While a Phantom movie/episode is playing, the Jellyfin web/mobile player shows a
small, collapsible panel with the current **source** (release, size, indexer) and
live **stream health** (peers / seeders, download rate, buffered-ahead, gostream
stream state), polled every 2–5 s **only while the player is open**.

## Endpoints (`PhantomLibraryPlayerStatsController`)

| Route | Auth | Purpose |
| --- | --- | --- |
| `GET /Plugins/PhantomLibrary/playerstats.js` | anonymous | the overlay shim (embedded `Configuration/phantomPlayerStats.js`) |
| `GET /Plugins/PhantomLibrary/PlayerStats/Current` | any user | what the overlay polls: resolves the **caller's own** session (`Jellyfin-UserId` + `Jellyfin-DeviceId` claims) → now-playing item → stats. Nothing playing → `{status:"not_playing", playing:false}` |
| `GET /Plugins/PhantomLibrary/PlayerStats/{itemId}?playSessionId=&mediaSourceId=` | user playing it / admin | per-item, per-playback-session form. Non-admin not playing that item (or a different `mediaSourceId`) → **403**; unknown item → 404 |

Response (`PlayerStatsResponse`, camelCase, nullable fields always present —
`JsonIgnore(Never)` overrides the host's `WhenWritingNull`, so `null` means
"not known", never a fabricated 0):

```
status  ok | not_playing | not_phantom | materialising | not_materialised |
        no_source | invalid_hash | torrent_not_loaded | gostream_unavailable | item_not_found
playing, isPhantom, itemId, type(movie|episode), tmdbId, season, episode,
playSessionId, mediaSourceId, isPaused, positionTicks, materialisedAt,
source { release, releaseSource(candidate|magnet_dn), sizeBytes, indexer, infoHash, seedersAtSelection }
stream { state(warmup|streaming|stalled|idle), peers, seeders, downloadRateBytesPerSec,
         bytesCompleted, torrentSize, readOffset, bufferedAheadBytes, bufferedAheadSeconds,
         lastReadAt, torrentSampledAt, streamSampledAt }
pollIntervalMs (3000)
```

## Resolution (`Playback/PlayerStatsService`)

1. `ILibraryManager.GetItemById` → must be a Phantom channel item
   (`ChannelIds.IsPhantom`, not tagged `external`) whose `ExternalId` parses as
   `movie_<tmdb>` / `episode_<tmdb>_s<S>e<E>`; else `not_phantom`.
2. `materialised_state` row (sentinels −1 for movies) → else `materialising`
   (in-flight row) / `not_materialised`.
3. `PhantomDb.GetMaterialisedSourceMagnetAsync`: newest `magnet_cache` row for the
   tuple (movie 0/0), current preset first, **TTL and imdb half ignored** (an
   expired TTL only gates the next materialise; the open file is still that
   torrent) → else `no_source`.
4. Release = `source_candidates.title` for the same magnet/hash
   (`GetSourceCandidateTitleAsync`), else the magnet `dn`.
5. Info-hash normalised to lowercase 40-hex (hex or base32; falls back to the
   magnet `btih`) → else `invalid_hash`.
6. `IGostreamClient.GetTorrentStatsAsync` → gostream
   `GET /api/library/stats?hash=` (gostream:phantom-player-stats-endpoint) with
   the **server-side** `X-Gostream-Token`, 4 s budget. 404 → `torrent_not_loaded`;
   401/403/5xx/timeout/unreachable → `gostream_unavailable` (upstream text is
   logged, never echoed).

Per poll: one cached item lookup, ≤4 single-row indexed reads, ≤1 gostream GET.
No writes, no TMDB/indexer calls. The response never contains the gostream
token, URL, stub or FUSE path.

## Overlay (`Configuration/phantomPlayerStats.js`)

- Loaded by `badges.js` (already injected into every jellyfin-web `index.html` by
  the fork image / `install.sh`) and by its own `install.sh` tag
  (`<!--phantom-library-playerstats-->`). Double load is a no-op
  (`window.__phantomPlayerStatsLoaded`). The jellyfin fork Dockerfile needs no
  change for the overlay to ship; adding its own tag there is optional.
- Player open = `#/video` route or `<video class="htmlvideoplayer">` present.
  Polls immediately, then every `pollIntervalMs` clamped to 2000–5000 ms, one
  request in flight, stale responses discarded.
- Stops on player close, `visibilitychange` → hidden, collapse toggle,
  server `not_playing` (after playing was seen, or after 5 start-up answers) or
  `not_phantom`. The `<video>` element's next `loadstart`/`playing` (autoplay of
  the next episode) re-arms it.
- Toggle: click the header; preference persists in `localStorage`
  (`phantomPlayerStats.collapsed`). Collapsed never polls (one bounded probe on a
  fresh player to know whether to show the pill).
- Layout: desktop card top-right; mobile (≤720 px or coarse pointer) full-width
  strip under the OSD with 44 px toggle. All dynamic text via `textContent`.

## Rig / gostream pin

The local rig talks to `tools/rig-scenarios/gostream-mock.py`, which now serves
`/api/library/stats` (hash loaded on `/api/library/add`; 404/400 per contract;
every request appended to `logs/gostream-mock-stats.jsonl`). The CI live rig pulls
the gostream **registry image**. Neither uses the nested `gostream` submodule
pin, so it was not bumped.

## Tests

`scripts/tests/p14-player-stats-overlay.test.sh` (task `Check:`):
1. `dotnet test --filter PlayerStatsOverlayTests` — 31 real controller tests vs an
   HttpListener fake gostream; exact count, zero skips.
2. `node tools/rig-scenarios/phantom-player-stats-overlay-dom.mjs` — the real
   shim on a virtual clock (16 lifecycle/layout/injection cases).
3. gostream-mock stats endpoint over real HTTP.
4. `tools/rig-scenarios/52-player-stats-overlay.sh` static gates; runs LIVE when
   `PHANTOM_P14_LIVE_RIG=1` (or the seeded rig exists and :18096 is free):
   movie + episode, overlay served/bootstrapped, exact proxied stats, owner 200 /
   other-user 403, real overlay `--live` → `Sessions/Playing/Stopped` → zero
   further gostream stats requests.
