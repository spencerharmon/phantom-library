# Phantom Library

A Jellyfin plugin that makes the entire TMDB catalogue appear to exist
inside a Jellyfin library. Titles materialise on demand: a user
favourites or plays an item, the plugin asks
[gostream](https://github.com/MrRobotoGit/gostream) to register the
matching torrent, and the resulting FUSE-backed `.mkv` becomes a real
streamable Jellyfin library item.

Mascot: *Stygiomedusa gigantea*, the giant phantom jelly.

> **Status: pre-alpha.** v0.1 is under active development. See
> [`PLAN.md`](PLAN.md) for the full design, milestone breakdown, and
> resolved decisions.

---

## Two layers of lazy loading

This is the architectural identity of the project.

1. **Layer 1 — Phantom → Virtual → Materialised** (this plugin).
   Driven by user interactions inside Jellyfin: searches, favourites,
   play presses, suggestions surfacing.
2. **Layer 2 — gostream FUSE byte-on-demand** (gostream).
   Once an item is Materialised, gostream serves bytes from the swarm
   on read, transparently to Jellyfin.

Result: a Jellyfin library that *looks* the size of TMDB but only ever
holds bytes for content somebody actually interacts with.

## Item lifecycle

| State | What Jellyfin sees | What the plugin holds | Playable |
|-------|--------------------|------------------------|----------|
| **Phantom** | nothing | TMDB metadata only, in plugin DB | no (fake button shows splash) |
| **Virtual** | library row, no `Path`, "phantom" badge | TMDB metadata, persisted | no (fake button shows splash + status) |
| **Materialised** | library row with `Path` to FUSE-backed `.mkv` | unchanged | yes (Jellyfin's real player + gostream FUSE) |
| **Watched** | per-user data | unchanged | yes |
| **Evicted** | demoted to Virtual (favourited items protected) | metadata preserved | no, until re-materialised |

## Requirements

- Jellyfin **10.10.x**
- gostream with the `POST /api/library/add` patch applied (see
  [primary patch](https://github.com/spencerharmon/gostream/tree/phantom-library/api-add))
- TMDB v3 API key
- Optional: Prowlarr (primary indexer); falls back to Torrentio

## Install

1. Add the plugin repository in Jellyfin: *Dashboard → Plugins →
   Repositories → +*

   ```
   Name:  Phantom Library
   URL:   https://raw.githubusercontent.com/spencerharmon/phantom-library/main/manifest.json
   ```

2. *Dashboard → Plugins → Catalog → Phantom Library → Install*

3. Restart Jellyfin.

4. *Dashboard → Plugins → Phantom Library* → enter your TMDB key,
   gostream URLs, indexer settings. Save.

## Build from source

```sh
git clone https://github.com/spencerharmon/phantom-library
cd phantom-library
dotnet build src/Jellyfin.Plugin.PhantomLibrary -c Release
```

Output: `src/Jellyfin.Plugin.PhantomLibrary/bin/Release/net8.0/Jellyfin.Plugin.PhantomLibrary.dll`.

Drop the DLL into `<jellyfin-data>/plugins/Jellyfin.Plugin.PhantomLibrary_<ver>/`
and restart Jellyfin to side-load.

## Configuration

All settings live in the admin dashboard. See PLAN.md §Configuration
for the full list. Highlights:

- **TMDB / gostream / indexer URLs and keys**
- **Quality preset** (gostream-default mirror, biggest+most-seeded,
  custom)
- **Eviction policy** (default: 7 idle days; favourites protected
  per-user via a toggle in user preferences)
- **Materialisation concurrency** (global + per-indexer caps)
- **Series autopilot** (enabled, prefetch window)
- **Eager pre-resolve** (background indexer queries on Phantom items
  before user clicks)

## Auth & network model

The plugin assumes it can reach gostream over a trusted network —
loopback for single-host setups, private LAN otherwise. No auth is
performed between the plugin and gostream. If you need to expose
either component to an untrusted network, terminate auth at a reverse
proxy.

## Companion gostream patches

Phantom Library is designed against three patches to gostream, all
optional but increasingly capable:

| Patch | Purpose | Status |
|-------|---------|--------|
| `POST /api/library/add` + `/remove` (M1) | One-shot torrent registration + FUSE path return | branch `phantom-library/api-add` on the fork; PR pending |
| Jellyfin watchlist adapter | Replace Plex watchlist source with Jellyfin favourites | future |
| Vault Mode (`persist=true` stubs) | Per-stub full-file SSD cache, protects favourites from swarm rot | future |

The plugin's runtime detects which patches are present and adjusts
behaviour (e.g. a `persist` flag is only written if Vault Mode is
available).

## Project layout

```
phantom-library/
├── PLAN.md                              # full design + milestones
├── README.md                            # this file
├── build.yaml                           # JPRM plugin build manifest
├── manifest.json                        # published plugin repo manifest
├── src/Jellyfin.Plugin.PhantomLibrary/  # plugin assembly
└── tests/                               # xUnit tests
```

## Roadmap

See [`PLAN.md`](PLAN.md). v0.1 = movies + series + materialisation +
splash + suggestions + eviction. Deferred: dynamic splash overlay,
manual torrent picker, official catalogue submission, multi-user
quotas.

## License

GPL-3.0 (matches Jellyfin's own license to ease catalogue submission).
See [`LICENSE`](LICENSE).
