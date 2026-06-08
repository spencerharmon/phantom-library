# Phantom Library

A Jellyfin plugin that makes the entire TMDB catalogue appear to exist
inside a Jellyfin library. Titles materialise on demand: a user
favourites or plays an item, the plugin asks
[gostream](https://github.com/MrRobotoGit/gostream) to register the
matching torrent, and the resulting FUSE-backed `.mkv` becomes a real
streamable Jellyfin library item.

Mascot: *Stygiomedusa gigantea*, the giant phantom jelly.

> **Status: pre-alpha**, v0.1.0 is the immediate target (all M1–M9
> milestones complete; see [`PLAN.md`](PLAN.md#status-as-of-2026-06-04)).
> Bumps to *Beta* once `v0.1.0` is tagged. Full design, milestone
> breakdown, and resolved decisions in [`PLAN.md`](PLAN.md);
> per-release notes in [`CHANGELOG.md`](CHANGELOG.md).

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

- Jellyfin **10.11.x** (current target — built against 10.11.9)
- gostream with the `POST /api/library/add` patch applied (see
  [primary patch](https://github.com/spencerharmon/gostream/tree/phantom-library/api-add))
- TMDB v3 API key
- Optional: Prowlarr (primary indexer); falls back to Torrentio

## Install

Two paths. Until `v0.1.0` is tagged and the workflow has published a
`versions[]` entry into [`manifest.json`](manifest.json), only the
**build-from-source** path works — the in-Jellyfin repository flow has
nothing to install. Once the tag exists, prefer the repository path.

### A. In-Jellyfin repository (post-`v0.1.0`)

1. *Dashboard → Plugins → Repositories → +*

   ```
   Name:  Phantom Library
   URL:   https://raw.githubusercontent.com/spencerharmon/phantom-library/main/manifest.json
   ```

2. *Dashboard → Plugins → Catalog → Phantom Library → Install*

3. Restart Jellyfin.

4. *Dashboard → Plugins → Phantom Library* → enter your TMDB key,
   gostream URLs, indexer settings. Save.

### B. Build from source (current default)

```sh
git clone https://github.com/spencerharmon/phantom-library
cd phantom-library
dotnet build -c Release
dotnet test  -c Release   # 118+ tests, expected to pass
```

Output: `src/Jellyfin.Plugin.PhantomLibrary/bin/Release/net9.0/Jellyfin.Plugin.PhantomLibrary.dll`.

#### Worked example — Linux (Arch, distro package)

`/var/lib/jellyfin/` is the standard data directory for the Arch
`jellyfin` package. Other distros and Docker images use
`~/.config/jellyfin/data/plugins/` (or the container-mapped
equivalent). Ownership matters: the plugin directory and its
contents must be readable + writable by the `jellyfin` user, or the
plugin will fail to write its `meta.json` on first load.

```sh
sudo mkdir -p /var/lib/jellyfin/plugins/Jellyfin.Plugin.PhantomLibrary_0.2.0.0
sudo cp src/Jellyfin.Plugin.PhantomLibrary/bin/Release/net9.0/Jellyfin.Plugin.PhantomLibrary.dll \
        /var/lib/jellyfin/plugins/Jellyfin.Plugin.PhantomLibrary_0.2.0.0/
sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/Jellyfin.Plugin.PhantomLibrary_0.2.0.0
sudo systemctl restart jellyfin
```

Then configure as in step 4 of the repository flow above.

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

## Troubleshooting

Real issues hit during M2 and M5 install / smoke testing. Check
`/var/log/jellyfin/` (or the systemd journal: `journalctl -u jellyfin
-f`) first; most of these surface as a single distinctive line.

- **Plugin loads but the admin page spins forever on save.**
  Enum string-vs-int mismatch: the config page used to POST enum
  values as integers, and Jellyfin's XML serializer expects enum
  *names*. Fixed in `f6e70b7`. If you see this on a build from
  before that commit, update and rebuild.

- **Plugin fails to load with `ArgumentNullException` at
  registration.** `IServerApplicationHost.Resolve` was being called
  at plugin registration time, before the host is fully constructed.
  Fixed in `5595103` by deferring `IApplicationPaths` resolution to
  a DI factory closure. Same advice: update + rebuild.

- **Plugin doesn't appear under its real GUID** (it shows under a
  random synthetic id, or a “broken plugin” entry persists across
  reinstalls). Jellyfin caches broken-plugin directories under a
  synthetic id and keeps surfacing them. Remove the broken directory
  from `<jellyfin-data>/plugins/` and reinstall; the real GUID
  (`9e7a1f4c-2b5d-4e8f-9a3b-7c1d2e5f6a8b`) will reappear on next
  start.

- **`meta.json: Permission denied`** in the Jellyfin log on startup.
  Plugin directory ownership is wrong. The directory and everything
  inside it must be `jellyfin:jellyfin`. See the Linux worked
  example above; the `chown -R jellyfin:jellyfin …` step is the fix.

- **Plugin builds cleanly but Jellyfin refuses to load it.** ABI /
  TFM mismatch. Verify your Jellyfin server version matches the
  `targetAbi` in [`build.yaml`](build.yaml) (currently
  `10.11.0.0`, framework `net9.0`). A 10.10.x server will not load
  a 10.11-targeted plugin.

- **Materialisation enqueues but never completes.** gostream is
  missing the `POST /api/library/add` endpoint. v0.1 hard-depends
  on the patched gostream from branch
  [`phantom-library/api-add`](https://github.com/spencerharmon/gostream/tree/phantom-library/api-add)
  on the fork. Build that branch, point the plugin's gostream URL
  at it, retry.

- **The FUSE path appears on disk but Jellyfin can't see it.**
  Library scan / permissions problem. The FUSE mount must live
  under a directory that is (a) registered as a Jellyfin library
  and (b) readable by the `jellyfin` user. If gostream runs as a
  different user, fix the mount's permissions (e.g. `allow_other`
  in `fuse.conf` plus appropriate group membership) before
  blaming the plugin.

- **No Virtual items appear in the Suggestions / browse rows.**
  The `SuggestionsRefreshTask` hasn't run yet. Trigger it manually:
  *Dashboard → Scheduled Tasks → “Phantom Library — refresh
  suggestions” → Run*. By default it runs on Jellyfin's normal
  scheduled-task cadence; the first run after install often hasn't
  happened.

- **TMDB rate-limit (429) errors in the log.** The TMDB cache TTL
  is too aggressive for your suggestions traffic. Defaults are 6h
  for *trending* and 24h for *similar* + *recommended*; widen them
  in the admin page. If you run many users, also consider lowering
  the suggestions refresh frequency.

- **Vault Mode prestage appears to no-op.** The gostream
  [`phantom-library/vault-mode`](https://github.com/spencerharmon/gostream/tree/phantom-library/vault-mode)
  branch isn't deployed. This is harmless: the plugin detects the
  patch at runtime and silently skips `persist=true` when absent.
  Deploy the branch (or accept the degradation) to enable
  full-file SSD caching for favourites.

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

## Mascot

*[Stygiomedusa gigantea](https://en.wikipedia.org/wiki/Stygiomedusa)*,
the giant phantom jelly: a deep-sea jellyfish that drifts in the
abyss and is almost never directly observed — a fitting namesake for
a library whose contents only exist when someone reaches for them.
Artwork: [`assets/phantom-library.svg`](assets/phantom-library.svg)
(`assets/phantom-library.png` rendered at 256×256 for the manifest
image).

## License

GPL-3.0 (matches Jellyfin's own license to ease catalogue submission).
See [`LICENSE`](LICENSE).
