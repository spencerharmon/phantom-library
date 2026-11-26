# Isolated PostgreSQL playback parity

## Entry points

- `./scripts/tests/postgres-playback-parity.test.sh`: mandatory adapter regressions
  **and** both 35/movie and 36/episode flows on PostgreSQL. No skip/dry-run mode.
- `python3 tools/rig-scenarios/isolated-playback-rig.py --backend sqlite`: same
  assertions on SQLite, still using synthetic Jellyfin state.
- Standalone `tools/rig-scenarios/35-channel-e2e-playback.sh` / `36-channel-episode-e2e-playback.sh`
  select one scenario (SQLite by default, `PHANTOM_RIG_BACKEND=postgres` to change).
  `RIG_NO_RESET=1` retains existing callers such as migration scenario 44: they
  own bringing up/cleaning the server. See `playback-scenario.sh` for explicit
  paths/API/token overrides. The isolated check never enters the legacy defaults.

Run worktree-scoped commands via the harness, for example:

```sh
beehive submodule worktree exec phantom-library bee-p0-postgres-playback-parity-rig -- ./scripts/tests/postgres-playback-parity.test.sh
```

## Runtime and build contract

Linux, Python >=3.12, .NET 9 SDK/runtime, `patch`, network access to public
Jellyfin source/FFmpeg and NuGet, and either rootless `podman`, `docker` with an
owned test daemon/socket, or `psql` plus an
explicit disposable PostgreSQL test service. The default builds upstream
Jellyfin **v10.11.9** with **every** repository `scripts/jellyfin-patches/*.patch`
in a unique throwaway build tree, then builds **this checkout's plugin source**
with private dependencies copied locally. Build servers/node reuse are disabled;
only owned process groups are terminated. Jellyfin FFmpeg **7.1.3-1** portable
is fetched for the real media probe/stream path (stock FFprobe is not a substitute
for Jellyfin's probe extensions). No web bundle is needed for REST playback.

To inject an already-built candidate, set `PHANTOM_PARITY_PLUGIN` to its directory
containing `Jellyfin.Plugin.PhantomLibrary.dll` **and its private dependency DLLs**.
`PHANTOM_PARITY_SERVER` selects the directory containing `jellyfin.dll` and the
complete patched server distribution. Set both to avoid a build; omitted artifacts
are built from this checkout. The runner copies the selected candidate into a fresh
plugin directory, compares SHA256, checks `/Plugins` reports it Active, and records
its actual version. The real native-open assertions reject an unpatched server.
`PHANTOM_PARITY_FFMPEG` can select a compatible built FFmpeg. For local iteration,
`PHANTOM_PARITY_KEEP_BUILD=1` retains the build tree; this never skips scenarios.

PostgreSQL defaults to a new `postgres:16-alpine` container per scenario, bound to
an OS-assigned loopback port. `PHANTOM_PARITY_CONTAINER_RUNTIME=docker` selects
Docker instead of the rootless-Podman default. Image ID and exact `SELECT version()` are retained.
`PHANTOM_PARITY_POSTGRES_IMAGE` may pin a different tag/digest for an experiment.
For CI, `PHANTOM_PARITY_PGHOST`, `PHANTOM_PARITY_PGPORT` (5432),
`PHANTOM_PARITY_PGUSER` (postgres), and required `PHANTOM_PARITY_PGPASSWORD` select
an **explicit test-only service with CREATEDB permission**. Each flow creates and
drops only its random `parity_<uuid>` database. Never point these test-service
variables at production. Ambient `PG*`, `POSTGRES_*`, `PHANTOM_POSTGRES_*`,
`JELLYFIN_*` and telemetry configuration is not used as a connection default.

## What is proved

`playback_db.py` executes standard-SQL upserts on the explicitly selected backend;
Jellyfin's own metadata inspection remains SQLite. PostgreSQL mode never opens a
Phantom SQLite connection: it rejects any `phantom.db`, checks `current_database()`
against the owned DB name and reads `phantom_schema_meta`. The plugin itself must
create the schema, populate discovery metadata, read seeded candidates, run native
materialisation, and persist `materialised_state` in that same PostgreSQL DB.
A candidate that silently selects SQLite fails before fixtures can mask it.

35/36 now share `playback-scenarios.py`, replacing the SQLite-only shell bodies
rather than leaving a second weaker implementation. Preserved assertions:

| Original subject | Shared rig assertion |
| --- | --- |
| discovery, browse | >=3 movies / >=2 series written by real discovery task |
| existing movie | TMDB Bravo name, no orphan/phantom tag, best HDR variant, probed source, real stream, resume |
| phantom movie/episode | browse runtime, phantom tag, exactly one GUID-shaped source, RequiresOpening, correct OpenToken, no splash path |
| native auto-open | AutoOpenLiveStream, exactly one final File source, real path, probed streams |
| materialisation | PostgreSQL state row; stable Jellyfin item ID; refreshed path/tags; nonzero runtime; real stream; Continue Watching |
| persisted metadata | BaseItems external movie/episode ID, exact real path and nonzero runtime |
| source replacement | alternate exposed by API; RejectCurrent success; new stub; operator_rejected failure; new playable source/bytes |
| TV navigation | native season type/year/overview and 8/1/7 availability summary; 8 native children |
| TV badges | series **and season** omitted; episode Phantom |
| second episode play | final File source, GUID ID, probed streams, nonempty real stream again |

Fixture configuration disables scheduled channel warmup, eviction and autopilot:
this test drives browse/materialisation explicitly. After applying and reading
back configuration through Jellyfin's serializer/API, the runner stops and
restarts **only its owned Jellyfin process**. Playback cannot start until the
restarted authenticated plugin endpoint responds, all fixture values match, and
the new server log contains exactly one warmup startup with `enabled=False`.
`/System/Info/Public` is not a readiness gate: Jellyfin's temporary setup host
serves it while plugin endpoints still return 503. Bootstrap and playback-boot
logs are retained separately. Eviction/autopilot on the current plugin have no
startup configuration log and read `Plugin.Configuration` dynamically; their
post-restart **read-only** API values must both be false. No log is fabricated. On the current plugin, the
30-second background warmup can race native episode materialisation and remove
the active BaseItem; that separate background-warmup behavior is **not** claimed
covered here. Availability and materialisation workers remain enabled and real.
No HTTP/stream/persistence assertion is skipped or retried away on a 404.

Metadata is a separate asynchronous boundary from media-source discovery.
Patched `ChannelManager` queues metadata on first browse and post-flight refresh;
`MetadataService` persists `DateLastRefreshed` after those providers finish.
Dynamic media sources can return the real path before that save. Ordinary browse
updates paths but does **not** overwrite existing tags (only first creation and
`ForceUpdate` do), so polling browse/DataVersion is not a metadata-save barrier.
The rig now waits for the initial phantom probe to save before native-open, then
for a newer saved timestamp, the exact opened path and cleared persisted phantom
tags before browsing again. This separates the initial stale-BaseItem writer from
the post-flight writer instead of racing them. It retains the original refreshed
browse tag assertion and records every SQL observation. Missing items and stuck
tags fail closed; it never clears tags, edits Jellyfin rows, or bumps DataVersion
to make playback pass. This is serial playback parity, not a concurrency fix for
a consumer opening while its first metadata refresh is still outstanding.

Fixtures additionally populate `source_candidates` for the seeded available
movie/episode: current browse pruning intentionally hides previously-assessed
empty-candidate rows. This is a valid available fixture, not a relaxation of the
browse assertions. No application code or database schema changes are needed.
Deterministic TMDB/gostream mocks replace external discovery/download services,
not the Jellyfin/plugin playback, probe, claim/worker or persistence path.

## Isolation, evidence, CI

Every run owns `artifacts/playback-parity-<random>/`, fresh synthetic startup-wizard
user `a`, distinct ports, process groups, and PostgreSQL databases. It applies
and reads back the fixture config through the live plugin API after startup,
then restarts its owned server and verifies the boot configuration as above,
so bootstrap's XML rewrites cannot silently replace mock endpoints with defaults. No copied user
seed, global `/tmp` filenames, user-systemd units, broad `pkill`, production DB,
image deployment or blue/green flip. SIGINT/SIGTERM and failure paths unwind owned
processes/DBs; successful cleanup writes a receipt. A port collision fails closed,
never stops whoever owns that port. Logs/results are retained; data/config/caches
are removed. Only explicitly retained build trees survive.

Evidence contains build source/patch hashes, FFmpeg identity, loaded plugin/server
versions, candidate/deployed DLL hashes, all server assembly hashes, PostgreSQL
identity **without password**, discovery/native-open/badge/persistence responses,
4096 real stream bytes per assertion, scenario logs/results, and cleanup receipts.
The workflow `.gitea/workflows/postgres-playback-parity.yaml` runs on the Gitea
`k8s` runner using its Docker socket for the owned PostgreSQL container; no
optional skip/continue-on-error. Actions service aliases do not resolve on this
runner's host network, so CI deliberately uses the same random-port container
lifecycle tested locally rather than a fixed host-global PostgreSQL port. It
uploads evidence even on failure and runs for main, PRs, manual dispatch and the
implementation branch. It does not build or flip a production image.

No operator deployment steps are needed: this changes test infrastructure only.
